namespace OpenVector.Capture.Timing;

/// <summary>
/// Reconstructs per-packet timestamps (D14 µs) from per-read QPC stamps. Platform-neutral.
/// </summary>
/// <remarks>
/// <para>
/// Raw Input has no packet timestamps; we only know that every packet of a batch arrived in the OS queue
/// after the previous read found the queue empty (<see cref="RawMouseEvent.QpcPrevRead"/>) and before this
/// read (<see cref="RawMouseEvent.QpcRead"/>).
/// </para>
/// <para><b>Rule (same idea as SDL3's <c>WIN_PollRawInput</c>):</b> for a batch of <c>n</c> packets read at
/// <c>t_read</c> with previous read at <c>t_prev</c>, packet <c>i</c> (0-based, oldest first) gets
/// <c>t_prev + (i + 1)·(t_read − t_prev)/n</c>, i.e. packets are spread evenly and the newest lands on the read time.</para>
/// <para><b>Idle correction:</b> if a nominal polling interval <c>T</c> is known (from
/// <see cref="PollingAnalyzer"/> or the user's setting) and the window is much longer than the batch can explain
/// (<c>t_read − t_prev &gt; n·T·idleSlack</c>), the mouse was idle for part of the window; spreading would smear
/// the first packet of a movement far into the past. Instead the packets are packed at spacing <c>T</c> ending at
/// <c>t_read</c>: <c>t_read − (n − 1 − i)·T</c>.</para>
/// <para><b>No previous read</b> (first batch of a session): packed at <c>T</c> ending at <c>t_read</c> if <c>T</c> is
/// known, otherwise all packets get <c>t_read</c>.</para>
/// <para>Each packet is computed from its own fields only, so the result is unaffected by ring-overflow drops of
/// other packets. Output is non-decreasing in sequence order.</para>
/// <para>Accuracy bound: a reconstructed time is within <c>(t_read − t_prev)</c> of the true arrival, plus
/// scheduling latency of the capture thread; at 8 kHz with ~1 ms wakeups that is ≲1 ms, which is small compared to
/// flick durations (100–400 ms) but must be considered for velocity-peak timing.</para>
/// </remarks>
public static class TimestampReconstructor
{
    public const double DefaultIdleSlack = 1.5;

    /// <summary>Fills <paramref name="packetMicros"/> (same length as <paramref name="events"/>) with D14 µs times.</summary>
    /// <param name="nominalIntervalMicros">Nominal polling interval (e.g. 125 for 8 kHz), or null if unknown.</param>
    public static void Reconstruct(ReadOnlySpan<RawMouseEvent> events, QpcTimebase timebase, Span<long> packetMicros,
        double? nominalIntervalMicros = null, double idleSlack = DefaultIdleSlack)
    {
        if (packetMicros.Length < events.Length)
            throw new ArgumentException("Destination is shorter than the event span.", nameof(packetMicros));
        if (nominalIntervalMicros is <= 0) throw new ArgumentOutOfRangeException(nameof(nominalIntervalMicros));

        for (int k = 0; k < events.Length; k++)
            packetMicros[k] = ReconstructOne(in events[k], timebase, nominalIntervalMicros, idleSlack);
    }

    /// <summary>Convenience overload that allocates the result.</summary>
    public static long[] Reconstruct(ReadOnlySpan<RawMouseEvent> events, QpcTimebase timebase,
        double? nominalIntervalMicros = null, double idleSlack = DefaultIdleSlack)
    {
        var result = new long[events.Length];
        Reconstruct(events, timebase, result, nominalIntervalMicros, idleSlack);
        return result;
    }

    public static long ReconstructOne(in RawMouseEvent e, QpcTimebase timebase, double? nominalIntervalMicros,
        double idleSlack = DefaultIdleSlack)
    {
        double tRead = timebase.ToMicros(e.QpcRead);
        int n = Math.Max(1, (int)e.BatchSize);
        int i = Math.Min(e.BatchPosition, n - 1);
        bool hasPrev = e.QpcPrevRead != 0 && e.QpcPrevRead < e.QpcRead;

        double t;
        if (hasPrev)
        {
            double tPrev = timebase.ToMicros(e.QpcPrevRead);
            double span = tRead - tPrev;
            if (nominalIntervalMicros is { } T && span > n * T * idleSlack)
                t = tRead - (n - 1 - i) * T;
            else
                t = tPrev + (i + 1) * span / n;
        }
        else if (nominalIntervalMicros is { } T)
        {
            t = tRead - (n - 1 - i) * T;
        }
        else
        {
            t = tRead;
        }
        return (long)Math.Round(t, MidpointRounding.AwayFromZero);
    }
}
