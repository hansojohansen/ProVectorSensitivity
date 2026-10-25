using System.Diagnostics;

namespace OpenVector.Capture.Timing;

/// <summary>
/// Converts QPC ticks to the D14 clock: int64 microseconds on a monotonic clock derived from QPC, with a
/// recorded offset to UTC.
/// </summary>
/// <remarks>
/// On Windows <see cref="Stopwatch.GetTimestamp"/> is <c>QueryPerformanceCounter</c> and
/// <see cref="Stopwatch.Frequency"/> is <c>QueryPerformanceFrequency</c> (documented .NET behaviour), so the
/// capture thread stamps with <see cref="Stopwatch.GetTimestamp"/>. The UTC offset is sampled once at session
/// start and never used to order events (UTC can jump; QPC cannot).
/// </remarks>
/// <param name="Frequency">QPC ticks per second.</param>
/// <param name="OriginQpc">QPC value that maps to 0 µs (session start).</param>
/// <param name="UtcUnixMicrosAtOrigin">UTC wall clock, µs since the Unix epoch, at <paramref name="OriginQpc"/>.</param>
public readonly record struct QpcTimebase(long Frequency, long OriginQpc, long UtcUnixMicrosAtOrigin)
{
    /// <summary>Samples QPC and UTC back to back (QPC taken on both sides, midpoint used).</summary>
    public static QpcTimebase CaptureNow()
    {
        long q0 = Stopwatch.GetTimestamp();
        DateTime utc = DateTime.UtcNow; // GetSystemTimePreciseAsFileTime on Windows
        long q1 = Stopwatch.GetTimestamp();
        long unixMicros = (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        return new QpcTimebase(Stopwatch.Frequency, q0 + (q1 - q0) / 2, unixMicros);
    }

    /// <summary>µs since <see cref="OriginQpc"/> (negative for earlier ticks). Exact; no overflow for any realistic delta.</summary>
    public long ToMicros(long qpc) => TicksToMicros(qpc - OriginQpc, Frequency);

    /// <summary>UTC (µs since Unix epoch) for a QPC value, for display/correlation only.</summary>
    public long ToUtcUnixMicros(long qpc) => UtcUnixMicrosAtOrigin + ToMicros(qpc);

    /// <summary>Exact tick→µs conversion, rounding toward negative infinity.</summary>
    public static long TicksToMicros(long ticks, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        long whole = Math.DivRem(ticks, frequency, out long rem);
        if (rem < 0) { whole--; rem += frequency; }
        // rem < frequency (≤ ~10^10 on any real system), so rem * 1e6 fits in int64.
        return whole * 1_000_000 + rem * 1_000_000 / frequency;
    }
}
