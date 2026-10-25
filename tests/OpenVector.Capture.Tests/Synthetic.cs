using OpenVector.Capture.Timing;

namespace OpenVector.Capture.Tests;

/// <summary>Builds synthetic capture streams: packets with true arrival times, read by a simulated drain loop.</summary>
internal static class Synthetic
{
    public const long Frequency = 10_000_000; // 10 MHz, the common QPC frequency on Windows 10/11
    public const long OriginQpc = 5_000_000_000;
    public static readonly QpcTimebase Timebase = new(Frequency, OriginQpc, 1_700_000_000_000_000);

    public static long Qpc(double micros) => OriginQpc + (long)Math.Round(micros * (Frequency / 1e6));

    public readonly record struct Packet(double ArrivalMicros, int Dx, int Dy, uint Extra = 0);

    /// <summary>
    /// Simulates the capture loop: at each wake time w the thread reads at w (returns packets in (prev, w]) and then
    /// does the "is it empty?" read at w + drainMicros. Every read's QPC becomes the next read's QpcPrevRead.
    /// </summary>
    public static RawMouseEvent[] Capture(IReadOnlyList<Packet> packets, IEnumerable<double> wakeTimes, double drainMicros = 3)
    {
        var reads = new List<double>();
        foreach (var w in wakeTimes) { reads.Add(w); reads.Add(w + drainMicros); }
        reads.Sort();

        var events = new List<RawMouseEvent>();
        int p = 0;
        ulong seq = 0;
        uint batch = 0;
        long prevQpc = 0;
        foreach (var r in reads)
        {
            int start = p;
            while (p < packets.Count && packets[p].ArrivalMicros <= r) p++;
            int n = p - start;
            long q = Qpc(r);
            for (int i = 0; i < n; i++)
            {
                var pk = packets[start + i];
                events.Add(new RawMouseEvent
                {
                    QpcRead = q, QpcPrevRead = prevQpc, Sequence = seq++, BatchIndex = batch,
                    BatchSize = (ushort)n, BatchPosition = (ushort)i, Device = 0x1234,
                    Dx = pk.Dx, Dy = pk.Dy, ExtraInformation = pk.Extra,
                });
            }
            if (n > 0) batch++;
            prevQpc = q;
        }
        return [.. events];
    }

    /// <summary>Mouse at <paramref name="rateHz"/> reporting every poll for <paramref name="durationMicros"/> with motion (dx, dy) per packet.</summary>
    public static List<Packet> Polled(double rateHz, double startMicros, double durationMicros, int dx = 3, int dy = -2)
    {
        var list = new List<Packet>();
        double T = 1e6 / rateHz;
        for (double t = startMicros; t < startMicros + durationMicros; t += T) list.Add(new Packet(t, dx, dy));
        return list;
    }

    /// <summary>Wakeups every <paramref name="periodMicros"/> ± jitter (deterministic RNG).</summary>
    public static IEnumerable<double> PeriodicWakes(double start, double end, double periodMicros, double jitterMicros, int seed = 1)
    {
        var rng = new Random(seed);
        for (double t = start; t < end; t += periodMicros)
            yield return t + (rng.NextDouble() * 2 - 1) * jitterMicros;
    }

    /// <summary>Wakes shortly after each packet arrival (what happens at low polling rates).</summary>
    public static IEnumerable<double> WakeOnArrival(IEnumerable<Packet> packets, double latencyMicros = 20) =>
        packets.Select(p => p.ArrivalMicros + latencyMicros);
}
