using OpenVector.Capture.Timing;

namespace OpenVector.Capture.Tests;

public class TimingTests
{
    [Theory]
    [InlineData(10_000_000L)]
    [InlineData(3_579_545L)]   // legacy ACPI PM timer frequency
    [InlineData(2_441_406L)]
    public void TicksToMicros_is_exact_and_floor(long freq)
    {
        Assert.Equal(0, QpcTimebase.TicksToMicros(0, freq));
        Assert.Equal(1_000_000, QpcTimebase.TicksToMicros(freq, freq));
        Assert.Equal(-1_000_000, QpcTimebase.TicksToMicros(-freq, freq));
        Assert.Equal(-1, QpcTimebase.TicksToMicros(-1, freq));
        long ticks = freq * 86_400 * 365 + 12345; // a year of uptime
        long expected = (long)((Int128)ticks * 1_000_000 / freq);
        Assert.Equal(expected, QpcTimebase.TicksToMicros(ticks, freq));
    }

    [Fact]
    public void Timebase_converts_relative_to_origin_and_to_utc()
    {
        var tb = new QpcTimebase(10_000_000, 1_000_000, 1_700_000_000_000_000);
        Assert.Equal(0, tb.ToMicros(1_000_000));
        Assert.Equal(125, tb.ToMicros(1_001_250));
        Assert.Equal(1_700_000_000_000_125, tb.ToUtcUnixMicros(1_001_250));
        var now = QpcTimebase.CaptureNow();
        Assert.True(now.Frequency > 0);
        Assert.InRange(now.ToMicros(now.OriginQpc), 0, 0);
    }

    [Fact]
    public void Even_spread_between_reads_like_SDL3()
    {
        // 4 packets, previous read at 1000 µs, this read at 2000 µs -> 1250, 1500, 1750, 2000.
        var events = Enumerable.Range(0, 4).Select(i => new RawMouseEvent
        {
            QpcRead = Synthetic.Qpc(2000), QpcPrevRead = Synthetic.Qpc(1000), BatchSize = 4, BatchPosition = (ushort)i,
        }).ToArray();
        var t = TimestampReconstructor.Reconstruct(events, Synthetic.Timebase);
        Assert.Equal([1250L, 1500L, 1750L, 2000L], t);
    }

    [Fact]
    public void Idle_window_packs_at_nominal_interval_and_first_batch_has_no_previous_read()
    {
        // Mouse idle for 500 ms, then 3 packets; with T = 125 µs they land at read - 250, -125, 0.
        var idle = Enumerable.Range(0, 3).Select(i => new RawMouseEvent
        {
            QpcRead = Synthetic.Qpc(600_000), QpcPrevRead = Synthetic.Qpc(100_000), BatchSize = 3, BatchPosition = (ushort)i,
        }).ToArray();
        Assert.Equal([599_750L, 599_875L, 600_000L], TimestampReconstructor.Reconstruct(idle, Synthetic.Timebase, 125));
        // Without a nominal rate the spread would put the first packet 333 ms in the past.
        Assert.Equal(266_667L, TimestampReconstructor.Reconstruct(idle, Synthetic.Timebase)[0]);

        var first = idle.Select(e => e with { QpcPrevRead = 0 }).ToArray();
        Assert.Equal([599_750L, 599_875L, 600_000L], TimestampReconstructor.Reconstruct(first, Synthetic.Timebase, 125));
        Assert.All(TimestampReconstructor.Reconstruct(first, Synthetic.Timebase), t => Assert.Equal(600_000L, t));
    }

    [Theory]
    [InlineData(8000.0, 1000.0, 300.0)]  // 8 kHz, ~1 ms wakeups with jitter -> batches of ~8
    [InlineData(8000.0, 125.0, 40.0)]    // 8 kHz, a wakeup per packet-ish
    [InlineData(1000.0, 1000.0, 400.0)]  // 1 kHz
    public void Reconstruction_error_is_bounded_by_read_window(double rateHz, double wakePeriod, double jitter)
    {
        var packets = Synthetic.Polled(rateHz, 1000, 2_000_000);
        var events = Synthetic.Capture(packets, Synthetic.PeriodicWakes(0, 2_010_000, wakePeriod, jitter));
        Assert.Equal(packets.Count, events.Length);

        var t = TimestampReconstructor.Reconstruct(events, Synthetic.Timebase, 1e6 / rateHz);
        double sumAbs = 0;
        for (int k = 0; k < events.Length; k++)
        {
            double truth = packets[k].ArrivalMicros;
            double window = events[k].QpcPrevRead == 0 ? 1e6 / rateHz * events[k].BatchSize
                : Synthetic.Timebase.ToMicros(events[k].QpcRead) - Synthetic.Timebase.ToMicros(events[k].QpcPrevRead);
            double err = t[k] - truth;
            Assert.InRange(err, -window - 1, window + 1);
            sumAbs += Math.Abs(err);
            if (k > 0) Assert.True(t[k] >= t[k - 1], "timestamps must be non-decreasing");
        }
        // Even spreading of an evenly polled stream is nearly exact on average.
        Assert.True(sumAbs / events.Length < Math.Max(wakePeriod, 1e6 / rateHz) / 2, $"mean abs error {sumAbs / events.Length}");
    }
}
