namespace OpenVector.Capture.Timing;

/// <summary>Tuning knobs for <see cref="PollingAnalyzer"/>. Defaults suit 125 Hz – 8 kHz mice.</summary>
public sealed record PollingAnalysisOptions
{
    /// <summary>A read window longer than this means the mouse was idle; it breaks an active segment.</summary>
    public double IdleGapMicros { get; init; } = 25_000;

    /// <summary>Length of the aggregation windows used to estimate the polling rate.</summary>
    public double RateWindowMicros { get; init; } = 10_000;

    /// <summary>Standard polling rates to snap the estimate to.</summary>
    public IReadOnlyList<double> StandardRatesHz { get; init; } = [125, 250, 500, 1000, 2000, 4000, 8000];

    /// <summary>Relative tolerance for snapping the estimate to a standard rate.</summary>
    public double SnapTolerance { get; init; } = 0.15;

    /// <summary>
    /// A window is "fast" when its Chebyshev path length (Σ max(|dx|,|dy|)) is at least this many counts per
    /// nominal poll. In fast windows the mouse has motion in every poll, so the packet rate should equal the
    /// polling rate; a lower rate there indicates coalescing or drops.
    /// </summary>
    public double FastCountsPerPoll { get; init; } = 2.0;

    /// <summary>Fast windows whose rate is below this fraction of nominal are flagged.</summary>
    public double CoalescingRatio { get; init; } = 0.8;

    /// <summary>Use this nominal rate instead of the estimate (e.g. the rate the user set in G HUB).</summary>
    public double? NominalRateHzOverride { get; init; }
}

/// <summary>Percentile summary in µs (or the unit stated by the property using it).</summary>
public sealed record Percentiles(int Count, double Min, double P50, double P90, double P99, double P999, double Max, double Mean)
{
    public static Percentiles Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public static Percentiles Of(ReadOnlySpan<double> values)
    {
        if (values.Length == 0) return Empty;
        var a = values.ToArray();
        Array.Sort(a);
        double sum = 0;
        foreach (var v in a) sum += v;
        return new(a.Length, a[0], Q(a, 0.5), Q(a, 0.9), Q(a, 0.99), Q(a, 0.999), a[^1], sum / a.Length);
    }

    /// <summary>Linear-interpolated quantile of a sorted array.</summary>
    private static double Q(double[] sorted, double q)
    {
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}

/// <summary>One histogram bucket: [<see cref="Low"/>, <see cref="High"/>).</summary>
public sealed record HistogramBin(string Label, double Low, double High, long Count);

/// <summary>Result of <see cref="PollingAnalyzer.Analyze"/>.</summary>
public sealed record PollingReport
{
    public long Packets { get; init; }
    public long RelativePackets { get; init; }
    public long AbsolutePackets { get; init; }
    public long Batches { get; init; }
    /// <summary>Packets missing from the event stream according to <see cref="RawMouseEvent.Sequence"/> gaps (ring overflow).</summary>
    public long SequenceGaps { get; init; }
    public double DurationMicros { get; init; }

    public int ActiveSegments { get; init; }
    public double ActiveDurationMicros { get; init; }
    public long ActivePackets { get; init; }
    /// <summary>Packets per second over active segments only (lower than the polling rate during slow motion).</summary>
    public double ActivePacketsPerSecond { get; init; }

    /// <summary>Polling-rate estimate: 90th percentile of per-window packet rates (Hz).</summary>
    public double EstimatedRateHz { get; init; }
    /// <summary>Median of per-window packet rates (Hz).</summary>
    public double MedianWindowRateHz { get; init; }
    /// <summary>Nominal rate used for coalescing checks: override, else estimate snapped to a standard rate, else null.</summary>
    public double? NominalRateHz { get; init; }

    /// <summary>Interval between consecutive non-empty reads inside active segments (µs). This is the capture "jitter" view.</summary>
    public Percentiles BatchIntervalMicros { get; init; } = Percentiles.Empty;
    /// <summary>Read window per batch, <c>QpcRead − QpcPrevRead</c> (µs).</summary>
    public Percentiles ReadWindowMicros { get; init; } = Percentiles.Empty;
    public Percentiles BatchSize { get; init; } = Percentiles.Empty;
    /// <summary>Intervals between reconstructed packet times inside active segments (µs).</summary>
    public Percentiles ReconstructedPacketIntervalMicros { get; init; } = Percentiles.Empty;

    public IReadOnlyList<HistogramBin> BatchIntervalHistogram { get; init; } = [];
    public IReadOnlyList<HistogramBin> BatchSizeHistogram { get; init; } = [];

    /// <summary>Windows with fast motion (see <see cref="PollingAnalysisOptions.FastCountsPerPoll"/>).</summary>
    public int FastWindows { get; init; }
    /// <summary>Median of (window rate / nominal) over fast windows; ≈1.0 when nothing is coalesced.</summary>
    public double? FastWindowRateRatioMedian { get; init; }
    /// <summary>Fraction of fast windows below <see cref="PollingAnalysisOptions.CoalescingRatio"/> × nominal.</summary>
    public double? FastWindowsBelowRatioFraction { get; init; }
    /// <summary>True when fast windows show sustained under-delivery: likely OS coalescing (e.g. background throttling) or drops.</summary>
    public bool CoalescingSuspected { get; init; }
    /// <summary>Largest single-packet Chebyshev delta (counts); very large values together with low rates also hint at coalescing.</summary>
    public int MaxPacketDelta { get; init; }
}

/// <summary>Polling-rate estimation and coalescing/drop detection from captured events. Platform-neutral.</summary>
/// <remarks>
/// <para><b>Batches.</b> Consecutive events with the same <see cref="RawMouseEvent.BatchIndex"/> form one read.
/// The batch's packet count is <see cref="RawMouseEvent.BatchSize"/> (OS truth, robust to ring drops).</para>
/// <para><b>Active segments.</b> A batch whose read window (<c>QpcRead − QpcPrevRead</c>) exceeds
/// <see cref="PollingAnalysisOptions.IdleGapMicros"/> starts a new segment; the idle window itself is excluded
/// from active time.</para>
/// <para><b>Rate estimate.</b> Inside segments, consecutive batches are grouped into windows of at least
/// <see cref="PollingAnalysisOptions.RateWindowMicros"/>; window rate = Σn / Σ(read window). The 90th percentile of
/// window rates estimates the polling rate (mice only report when there is motion, so slow movement gives lower
/// rates; the upper part of the distribution reflects polls that all carried motion).</para>
/// <para><b>Coalescing.</b> Only "fast" windows are judged, where motion per nominal poll is large enough that
/// every poll must have produced a packet. If their median rate is clearly below nominal, packets were merged
/// (Windows 11 background throttling delivers ~125 Hz of combined deltas) or dropped.</para>
/// </remarks>
public static class PollingAnalyzer
{
    private static readonly double[] IntervalEdges = [0, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000, 32000, double.PositiveInfinity];
    private static readonly double[] SizeEdges = [1, 2, 3, 5, 9, 17, 33, 65, 129, double.PositiveInfinity];

    public static PollingReport Analyze(ReadOnlySpan<RawMouseEvent> events, QpcTimebase timebase, PollingAnalysisOptions? options = null)
    {
        options ??= new PollingAnalysisOptions();
        if (events.IsEmpty) return new PollingReport();

        // ---- Collect batches ----
        var batches = new List<Batch>();
        long relative = 0, absolute = 0, gaps = 0;
        int maxDelta = 0;
        ulong prevSeq = events[0].Sequence;
        for (int k = 0; k < events.Length; k++)
        {
            ref readonly var e = ref events[k];
            if (k > 0 && e.Sequence > prevSeq + 1) gaps += (long)(e.Sequence - prevSeq - 1);
            prevSeq = e.Sequence;
            if (e.IsRelative) relative++; else absolute++;
            int d = e.IsRelative ? Math.Max(Math.Abs(e.Dx), Math.Abs(e.Dy)) : 0;
            maxDelta = Math.Max(maxDelta, d);

            if (batches.Count == 0 || batches[^1].Index != e.BatchIndex)
            {
                double tRead = timebase.ToMicros(e.QpcRead);
                double tPrev = e.QpcPrevRead != 0 && e.QpcPrevRead < e.QpcRead ? timebase.ToMicros(e.QpcPrevRead) : double.NaN;
                batches.Add(new Batch(e.BatchIndex, tRead, tPrev, Math.Max(1, (int)e.BatchSize), 0));
            }
            var b = batches[^1];
            batches[^1] = b with { PathCounts = b.PathCounts + d };
        }

        // ---- Segments, intervals, rate windows ----
        var batchIntervals = new List<double>();
        var readWindows = new List<double>();
        var sizes = new List<double>(batches.Count);
        var windowRates = new List<double>();
        var windows = new List<(double Span, long N, long Path)>();
        int segments = 0;
        double activeTime = 0;
        long activePackets = 0;
        double wSpan = 0; long wN = 0, wPath = 0;

        for (int i = 0; i < batches.Count; i++)
        {
            var b = batches[i];
            sizes.Add(b.N);
            double window = double.IsNaN(b.TPrev) ? double.NaN : b.TRead - b.TPrev;
            if (!double.IsNaN(window)) readWindows.Add(window);

            bool newSegment = i == 0 || double.IsNaN(window) || window > options.IdleGapMicros;
            if (newSegment)
            {
                segments++;
                wSpan = 0; wN = 0; wPath = 0; // discard partial window across an idle gap
                continue; // the first batch of a segment has no trustworthy active time before it
            }
            batchIntervals.Add(b.TRead - batches[i - 1].TRead);
            activeTime += window;
            activePackets += b.N;
            wSpan += window; wN += b.N; wPath += b.PathCounts;
            if (wSpan >= options.RateWindowMicros)
            {
                windowRates.Add(wN * 1e6 / wSpan);
                windows.Add((wSpan, wN, wPath));
                wSpan = 0; wN = 0; wPath = 0;
            }
        }

        var rateP = Percentiles.Of(windowRates.ToArray());
        double estimate = rateP.Count > 0 ? rateP.P90 : 0;
        double? nominal = options.NominalRateHzOverride ?? Snap(estimate, options);

        // ---- Coalescing check on fast windows ----
        int fast = 0;
        double? ratioMedian = null, belowFrac = null;
        bool suspected = false;
        if (nominal is { } nom && nom > 0)
        {
            var ratios = new List<double>();
            foreach (var w in windows)
            {
                double polls = w.Span * nom / 1e6;
                if (polls <= 0 || w.Path < options.FastCountsPerPoll * polls) continue;
                ratios.Add(w.N / polls);
            }
            fast = ratios.Count;
            if (fast > 0)
            {
                var rp = Percentiles.Of(ratios.ToArray());
                ratioMedian = rp.P50;
                belowFrac = ratios.Count(r => r < options.CoalescingRatio) / (double)fast;
                suspected = fast >= 5 && rp.P50 < options.CoalescingRatio;
            }
        }

        // ---- Reconstructed packet intervals (active segments only) ----
        double? T = nominal is { } nn && nn > 0 ? 1e6 / nn : null;
        var pktIntervals = new List<double>();
        long prevT = long.MinValue;
        uint prevBatch = events[0].BatchIndex;
        int bi = 0;
        for (int k = 0; k < events.Length; k++)
        {
            ref readonly var e = ref events[k];
            if (e.BatchIndex != prevBatch) { bi++; prevBatch = e.BatchIndex; }
            long t = TimestampReconstructor.ReconstructOne(in e, timebase, T);
            var b = batches[bi];
            bool idleBefore = bi == 0 || double.IsNaN(b.TPrev) || b.TRead - b.TPrev > options.IdleGapMicros;
            if (prevT != long.MinValue && !(idleBefore && e.BatchPosition == 0))
                pktIntervals.Add(t - prevT);
            prevT = t;
        }

        double first = batches[0].TRead, last = batches[^1].TRead;
        return new PollingReport
        {
            Packets = events.Length,
            RelativePackets = relative,
            AbsolutePackets = absolute,
            Batches = batches.Count,
            SequenceGaps = gaps,
            DurationMicros = last - first,
            ActiveSegments = segments,
            ActiveDurationMicros = activeTime,
            ActivePackets = activePackets,
            ActivePacketsPerSecond = activeTime > 0 ? activePackets * 1e6 / activeTime : 0,
            EstimatedRateHz = estimate,
            MedianWindowRateHz = rateP.P50,
            NominalRateHz = nominal,
            BatchIntervalMicros = Percentiles.Of(batchIntervals.ToArray()),
            ReadWindowMicros = Percentiles.Of(readWindows.ToArray()),
            BatchSize = Percentiles.Of(sizes.ToArray()),
            ReconstructedPacketIntervalMicros = Percentiles.Of(pktIntervals.ToArray()),
            BatchIntervalHistogram = Histogram(batchIntervals, IntervalEdges, "us"),
            BatchSizeHistogram = Histogram(sizes, SizeEdges, ""),
            FastWindows = fast,
            FastWindowRateRatioMedian = ratioMedian,
            FastWindowsBelowRatioFraction = belowFrac,
            CoalescingSuspected = suspected,
            MaxPacketDelta = maxDelta,
        };
    }

    /// <summary>Snaps a rate to the nearest standard rate within tolerance, else null.</summary>
    public static double? Snap(double rateHz, PollingAnalysisOptions options)
    {
        if (rateHz <= 0) return null;
        double best = 0, bestErr = double.MaxValue;
        foreach (var r in options.StandardRatesHz)
        {
            double err = Math.Abs(rateHz - r) / r;
            if (err < bestErr) { bestErr = err; best = r; }
        }
        return bestErr <= options.SnapTolerance ? best : null;
    }

    private static List<HistogramBin> Histogram(List<double> values, double[] edges, string unit)
    {
        var counts = new long[edges.Length - 1];
        foreach (var v in values)
        {
            for (int i = 0; i < counts.Length; i++)
                if (v >= edges[i] && v < edges[i + 1]) { counts[i]++; break; }
        }
        var bins = new List<HistogramBin>(counts.Length);
        for (int i = 0; i < counts.Length; i++)
        {
            string hi = double.IsPositiveInfinity(edges[i + 1]) ? "inf" : edges[i + 1].ToString(System.Globalization.CultureInfo.InvariantCulture);
            string lo = edges[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            bins.Add(new HistogramBin($"[{lo},{hi}){unit}", edges[i], double.IsPositiveInfinity(edges[i + 1]) ? double.MaxValue : edges[i + 1], counts[i]));
        }
        return bins;
    }

    private readonly record struct Batch(uint Index, double TRead, double TPrev, int N, long PathCounts);
}
