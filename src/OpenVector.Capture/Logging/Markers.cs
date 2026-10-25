using System.Diagnostics;

namespace OpenVector.Capture.Logging;

/// <summary>Marker kinds. Values are part of the log schema: never renumber, only append.</summary>
public enum MarkerKind : uint
{
    /// <summary>Session start. a = capture options hash or 0; text = optional JSON.</summary>
    SessionStart = 1,
    /// <summary>Session end. a = mouse packets written, b = ring overflows.</summary>
    SessionEnd = 2,

    /// <summary>Our window became the foreground/active window (WM_ACTIVATE != WA_INACTIVE). code = WA_* value.</summary>
    FocusGained = 10,
    /// <summary>Our window lost activation (WM_ACTIVATE == WA_INACTIVE). Raw input stops being delivered (D5).</summary>
    FocusLost = 11,

    /// <summary>Packets dropped by the in-process ring. a = first missing sequence number, b = count. qpc = read time of the next delivered packet.</summary>
    RingOverflow = 20,
    /// <summary>Capture read error. code = Win32 error.</summary>
    ReadError = 21,
    /// <summary>
    /// Fast motion delivered well below the nominal polling rate (OS coalescing or device drops).
    /// a = packets in window, b = window length µs, c = rate ratio vs nominal.
    /// </summary>
    CoalescingSuspected = 22,

    /// <summary>Trainer frame. a = frame index, b = trainer-defined (e.g. present id), c = trainer-defined (e.g. GPU time ms).</summary>
    Frame = 100,
    /// <summary>Trainer event (trial start/stop, target spawn/hit, click …). code = trainer-defined event id; a/b/c trainer-defined.</summary>
    TrainerEvent = 101,

    /// <summary>First value free for experimental kinds (not used by released writers).</summary>
    UserDefined = 0x8000_0000,
}

/// <summary>A timestamped marker on the capture clock (QPC ticks, convert with the log's timebase).</summary>
public readonly record struct MarkerRecord(long Qpc, MarkerKind Kind, uint Code = 0, long A = 0, long B = 0, double C = 0);

/// <summary>
/// Thread-safe, allocation-free hand-off of markers from any thread (render thread, task engine, window procedure)
/// to the logger thread. Markers are stamped with <see cref="Stopwatch.GetTimestamp"/> (= QPC on Windows), the same
/// clock as <see cref="RawMouseEvent.QpcRead"/>, so mouse packets, frames and trainer events share one timeline.
/// </summary>
/// <remarks>
/// This is a plain in-process queue: the trainer posts its own frame/event markers. Nothing is observed from
/// other processes. Producers take a short lock; never post from the raw-input capture thread's hot loop.
/// If the logger falls behind, new markers are dropped and counted in <see cref="Dropped"/>.
/// </remarks>
public sealed class MarkerQueue
{
    private readonly SpscRingBuffer<MarkerRecord> _ring;
    private readonly Lock _producerLock = new();

    public MarkerQueue(int capacity = 4096) => _ring = new SpscRingBuffer<MarkerRecord>(capacity);

    public long Dropped => _ring.OverflowCount;

    /// <summary>Posts a marker stamped now.</summary>
    public bool Post(MarkerKind kind, uint code = 0, long a = 0, long b = 0, double c = 0) =>
        Post(new MarkerRecord(Stopwatch.GetTimestamp(), kind, code, a, b, c));

    /// <summary>Posts a marker with a caller-supplied QPC timestamp (e.g. taken right after Present).</summary>
    public bool Post(in MarkerRecord marker)
    {
        lock (_producerLock) return _ring.TryWrite(in marker);
    }

    /// <summary>Logger thread only.</summary>
    public int Drain(Span<MarkerRecord> destination) => _ring.Drain(destination);
}
