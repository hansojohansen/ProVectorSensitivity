using OpenVector.Capture.Timing;

namespace OpenVector.Capture.Tests;

public class PollingAnalyzerTests
{
    [Fact]
    public void Detects_8kHz_with_batching_and_no_coalescing()
    {
        var packets = Synthetic.Polled(8000, 1000, 3_000_000);
        var events = Synthetic.Capture(packets, Synthetic.PeriodicWakes(0, 3_010_000, 1000, 300));
        var r = PollingAnalyzer.Analyze(events, Synthetic.Timebase);

        Assert.Equal(packets.Count, r.Packets);
        Assert.Equal(8000, r.NominalRateHz);
        Assert.InRange(r.EstimatedRateHz, 7600, 8400);
        Assert.InRange(r.BatchSize.P50, 6, 10);
        Assert.InRange(r.BatchIntervalMicros.P50, 800, 1200);
        Assert.InRange(r.ReconstructedPacketIntervalMicros.P50, 100, 150);
        Assert.False(r.CoalescingSuspected);
        Assert.True(r.FastWindows > 100);
        Assert.InRange(r.FastWindowRateRatioMedian!.Value, 0.95, 1.05);
        Assert.Equal(0, r.SequenceGaps);
        Assert.Equal(1, r.ActiveSegments);
        Assert.Equal(r.BatchIntervalMicros.Count, r.BatchIntervalHistogram.Sum(b => b.Count));
    }

    [Fact]
    public void Detects_1kHz_one_packet_per_wake()
    {
        var packets = Synthetic.Polled(1000, 1000, 2_000_000);
        var events = Synthetic.Capture(packets, Synthetic.WakeOnArrival(packets));
        var r = PollingAnalyzer.Analyze(events, Synthetic.Timebase);
        Assert.Equal(1000, r.NominalRateHz);
        Assert.Equal(1, r.BatchSize.P50);
        Assert.False(r.CoalescingSuspected);
    }

    [Fact]
    public void Flags_coalescing_when_8kHz_motion_arrives_at_125Hz()
    {
        // Windows 11 background throttling: a 8 kHz mouse delivered as 125 messages/s carrying combined deltas.
        var coalesced = new List<Synthetic.Packet>();
        for (double t = 1000; t < 3_000_000; t += 8000) coalesced.Add(new Synthetic.Packet(t, 3 * 64, -2 * 64));
        var events = Synthetic.Capture(coalesced, Synthetic.WakeOnArrival(coalesced));

        var blind = PollingAnalyzer.Analyze(events, Synthetic.Timebase);
        Assert.Equal(125, blind.NominalRateHz); // on its own, it looks like a 125 Hz mouse

        var r = PollingAnalyzer.Analyze(events, Synthetic.Timebase, new PollingAnalysisOptions { NominalRateHzOverride = 8000 });
        Assert.True(r.CoalescingSuspected);
        Assert.InRange(r.FastWindowRateRatioMedian!.Value, 0.0, 0.05);
        Assert.Equal(192, r.MaxPacketDelta);
    }

    [Fact]
    public void Slow_motion_does_not_count_as_coalescing()
    {
        // 8 kHz mouse moving slowly: a packet only every 4th poll, 1 count each -> not "fast", no flag.
        var packets = Synthetic.Polled(2000, 1000, 2_000_000, dx: 1, dy: 0);
        var events = Synthetic.Capture(packets, Synthetic.PeriodicWakes(0, 2_010_000, 1000, 200));
        var r = PollingAnalyzer.Analyze(events, Synthetic.Timebase, new PollingAnalysisOptions { NominalRateHzOverride = 8000 });
        Assert.Equal(0, r.FastWindows);
        Assert.False(r.CoalescingSuspected);
    }

    [Fact]
    public void Idle_gaps_split_segments_and_sequence_gaps_are_counted()
    {
        var a = Synthetic.Polled(1000, 1000, 500_000);
        var b = Synthetic.Polled(1000, 1_000_000, 500_000);
        var packets = a.Concat(b).ToList();
        var events = Synthetic.Capture(packets, Synthetic.WakeOnArrival(packets)).ToList();
        events.RemoveRange(100, 10); // simulate ring overflow
        var r = PollingAnalyzer.Analyze(events.ToArray(), Synthetic.Timebase);
        Assert.Equal(2, r.ActiveSegments);
        Assert.Equal(10, r.SequenceGaps);
        Assert.InRange(r.ActiveDurationMicros, 975_000, 1_000_000); // 2 × 499 ms minus the 10 removed 1 ms reads
    }

    [Fact]
    public void Empty_input_gives_empty_report() =>
        Assert.Equal(0, PollingAnalyzer.Analyze(ReadOnlySpan<RawMouseEvent>.Empty, Synthetic.Timebase).Packets);
}
