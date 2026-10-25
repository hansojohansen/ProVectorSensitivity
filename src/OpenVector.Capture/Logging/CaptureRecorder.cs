namespace OpenVector.Capture.Logging;

/// <summary>Options for <see cref="CaptureRecorder"/>.</summary>
public sealed record CaptureRecorderOptions
{
    /// <summary>
    /// Expected polling rate (e.g. the rate set in G HUB). Enables online <see cref="MarkerKind.CoalescingSuspected"/>
    /// markers; null disables them (post-hoc analysis with <see cref="Timing.PollingAnalyzer"/> still works).
    /// </summary>
    public double? NominalRateHz { get; init; }

    /// <summary>Window length for the online coalescing check, µs.</summary>
    public double WindowMicros { get; init; } = 10_000;

    /// <summary>Read windows longer than this are idle and reset the check, µs.</summary>
    public double IdleGapMicros { get; init; } = 25_000;

    /// <summary>Minimum Chebyshev counts per nominal poll for a window to count as "fast".</summary>
    public double FastCountsPerPoll { get; init; } = 2.0;

    /// <summary>Flag fast windows delivering less than this fraction of nominal polls.</summary>
    public double CoalescingRatio { get; init; } = 0.8;

    /// <summary>Also keep every mouse event in memory (preallocated to this capacity; 0 = don't keep).</summary>
    public int RetainCapacity { get; init; }
}

/// <summary>
/// The logger side of a capture session: drains the capture ring and a <see cref="MarkerQueue"/> into one
/// <see cref="CaptureLogWriter"/>, and turns gaps into explicit markers (D15). Single-threaded: call
/// <see cref="Pump"/> from one logger thread. Platform-neutral.
/// </summary>
/// <remarks>
/// Markers emitted automatically:
/// <list type="bullet">
/// <item><see cref="MarkerKind.RingOverflow"/> when the packet sequence has a gap (packets the capture thread parsed
/// but could not queue).</item>
/// <item><see cref="MarkerKind.CoalescingSuspected"/> when <see cref="CaptureRecorderOptions.NominalRateHz"/> is set and a
/// fast-motion window delivers too few packets.</item>
/// </list>
/// Focus, frame and trainer markers come from the <see cref="MarkerQueue"/>. Within one <see cref="Pump"/>,
/// queued markers are written before mouse packets; readers must sort by QPC to interleave.
/// </remarks>
public sealed class CaptureRecorder
{
    private readonly SpscRingBuffer<RawMouseEvent> _source;
    private readonly CaptureLogWriter _writer;
    private readonly MarkerQueue? _markers;
    private readonly CaptureRecorderOptions _options;
    private readonly RawMouseEvent[] _scratch = new RawMouseEvent[1024];
    private readonly MarkerRecord[] _markerScratch = new MarkerRecord[256];
    private readonly RawMouseEvent[]? _retained;
    private readonly long _idleTicks, _windowTicks;

    private bool _haveSeq;
    private ulong _nextSeq;
    private uint _lastBatch = uint.MaxValue;
    private long _wTicks, _wPackets, _wPath;

    public CaptureRecorder(SpscRingBuffer<RawMouseEvent> source, CaptureLogWriter writer, MarkerQueue? markers = null,
        CaptureRecorderOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _markers = markers;
        _options = options ?? new CaptureRecorderOptions();
        if (_options.RetainCapacity > 0) _retained = new RawMouseEvent[_options.RetainCapacity];
        long f = writer.Timebase.Frequency;
        _idleTicks = (long)(_options.IdleGapMicros * f / 1e6);
        _windowTicks = (long)(_options.WindowMicros * f / 1e6);
    }

    public long MouseEvents { get; private set; }
    public long MissingPackets { get; private set; }
    public long CoalescingMarkers { get; private set; }
    /// <summary>Events that did not fit into the retain buffer (still written to the log).</summary>
    public long RetainOverflow { get; private set; }

    /// <summary>Retained events (empty unless <see cref="CaptureRecorderOptions.RetainCapacity"/> &gt; 0).</summary>
    public ReadOnlySpan<RawMouseEvent> Retained =>
        _retained is null ? default : _retained.AsSpan(0, (int)Math.Min(MouseEvents, _retained.Length));

    /// <summary>Drains everything currently queued. Returns the number of mouse events written.</summary>
    public int Pump()
    {
        if (_markers != null)
        {
            int m;
            while ((m = _markers.Drain(_markerScratch)) > 0)
                for (int i = 0; i < m; i++) _writer.WriteMarker(in _markerScratch[i]);
        }

        int total = 0, n;
        while ((n = _source.Drain(_scratch)) > 0)
        {
            for (int i = 0; i < n; i++) Handle(in _scratch[i]);
            total += n;
        }
        return total;
    }

    /// <summary>Writes a marker directly (logger thread only), e.g. session start with a JSON text.</summary>
    public void WriteMarker(in MarkerRecord marker, string? text = null) => _writer.WriteMarker(in marker, text);

    private void Handle(in RawMouseEvent e)
    {
        if (_haveSeq && e.Sequence != _nextSeq && e.Sequence > _nextSeq)
        {
            long missing = (long)(e.Sequence - _nextSeq);
            MissingPackets += missing;
            _writer.WriteMarker(new MarkerRecord(e.QpcRead, MarkerKind.RingOverflow, 0, (long)_nextSeq, missing));
        }
        _haveSeq = true;
        _nextSeq = e.Sequence + 1;

        _writer.Write(in e);
        if (_retained != null)
        {
            if (MouseEvents < _retained.Length) _retained[MouseEvents] = e;
            else RetainOverflow++;
        }
        MouseEvents++;

        if (_options.NominalRateHz is { } nominal && nominal > 0) TrackCoalescing(in e, nominal);
    }

    private void TrackCoalescing(in RawMouseEvent e, double nominal)
    {
        if (e.BatchIndex != _lastBatch)
        {
            _lastBatch = e.BatchIndex;
            long window = e.QpcPrevRead != 0 ? e.QpcRead - e.QpcPrevRead : long.MaxValue;
            if (window > _idleTicks) { _wTicks = 0; _wPackets = 0; _wPath = 0; }
            else { _wTicks += window; _wPackets += e.BatchSize; }
        }
        if (e.IsRelative) _wPath += Math.Max(Math.Abs((long)e.Dx), Math.Abs((long)e.Dy));

        if (_wTicks >= _windowTicks && e.BatchPosition + 1 >= e.BatchSize)
        {
            double micros = _wTicks * 1e6 / _writer.Timebase.Frequency;
            double polls = micros * nominal / 1e6;
            if (polls > 0 && _wPath >= _options.FastCountsPerPoll * polls)
            {
                double ratio = _wPackets / polls;
                if (ratio < _options.CoalescingRatio)
                {
                    _writer.WriteMarker(new MarkerRecord(e.QpcRead, MarkerKind.CoalescingSuspected, 0, _wPackets, (long)micros, ratio));
                    CoalescingMarkers++;
                }
            }
            _wTicks = 0; _wPackets = 0; _wPath = 0;
        }
    }
}
