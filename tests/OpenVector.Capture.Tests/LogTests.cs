using System.Buffers.Binary;
using OpenVector.Capture.Logging;

namespace OpenVector.Capture.Tests;

public class LogTests
{
    private static RawMouseEvent Sample(int i) => new()
    {
        QpcRead = 1_000_000 + i * 10, QpcPrevRead = i == 0 ? 0 : 1_000_000 + i * 10 - 7, Sequence = (ulong)i,
        BatchIndex = (uint)(i / 3), BatchSize = 3, BatchPosition = (ushort)(i % 3), Device = 0xFFFF_0000_1234_5678UL,
        Dx = -i, Dy = i * 2, Flags = i % 5 == 0 ? RawMouseFlags.MoveAbsolute : RawMouseFlags.MoveRelative,
        ButtonFlags = RawMouseButtonFlags.HWheel | RawMouseButtonFlags.Button5Up, ButtonData = (short)(-120 * (i % 2)),
        RawButtons = 0xDEADBEEF, ExtraInformation = RawAccelExtraInfo.Encode((short)-i, (short)i),
    };

    [Fact]
    public void Round_trips_mouse_markers_metadata_and_timebase()
    {
        var tb = new Timing.QpcTimebase(10_000_000, 999_999, 1_700_000_000_123_456);
        var ms = new MemoryStream();
        using (var w = new CaptureLogWriter(ms, tb, """{"tool":"test","ü":1}""", leaveOpen: true))
        {
            w.WriteMarker(new MarkerRecord(1_000_000, MarkerKind.SessionStart), "hello ✓");
            for (int i = 0; i < 5000; i++) w.Write(Sample(i)); // > one internal buffer
            w.WriteRaw(0x8001, [1, 2, 3, 4, 5]); // unknown type must be skipped by readers
            w.WriteMarker(new MarkerRecord(2_000_000, MarkerKind.Frame, 7, 42, -1, 16.6));
            w.WriteMarker(new MarkerRecord(2_000_001, MarkerKind.FocusLost), new string('x', 70_000)); // larger than buffer
            Assert.Equal(5000, w.MouseRecordsWritten);
            Assert.Equal(3, w.MarkerRecordsWritten);
        }

        ms.Position = 0;
        using var r = new CaptureLogReader(ms);
        Assert.Equal(1, r.SchemaVersion);
        Assert.Equal(tb, r.Timebase);
        Assert.Equal("""{"tool":"test","ü":1}""", r.MetadataJson);
        var c = r.ReadAll();
        Assert.Equal(5000, c.Mouse.Length);
        for (int i = 0; i < c.Mouse.Length; i++) Assert.Equal(Sample(i), c.Mouse[i]);
        Assert.Equal(3, c.Markers.Length);
        Assert.Equal(new MarkerRecord(1_000_000, MarkerKind.SessionStart), c.Markers[0]);
        Assert.Equal("hello ✓", c.MarkerTexts[0]);
        Assert.Equal(new MarkerRecord(2_000_000, MarkerKind.Frame, 7, 42, -1, 16.6), c.Markers[1]);
        Assert.Null(c.MarkerTexts[1]);
        Assert.Equal(70_000, c.MarkerTexts[2]!.Length);
        Assert.Equal(1, c.UnknownRecords);
        Assert.Equal(0, c.TruncatedBytes);
    }

    [Fact]
    public void Header_layout_matches_documentation()
    {
        var ms = new MemoryStream();
        using (var w = new CaptureLogWriter(ms, new Timing.QpcTimebase(3, 4, 5), "{}", leaveOpen: true)) w.Write(Sample(1));
        var b = ms.ToArray();
        Assert.Equal("OVCAPLOG"u8.ToArray(), b[..8]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)));
        Assert.Equal(48, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(10)));
        Assert.Equal(3, BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(16)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40)));
        int rec = 48 + 2;
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(rec)));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(rec + 4)));
        Assert.Equal(rec + 8 + 64, b.Length);
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(rec + 8 + 40))); // dx
    }

    [Fact]
    public void Truncated_tail_is_ignored_and_reported()
    {
        var ms = new MemoryStream();
        using (var w = new CaptureLogWriter(ms, new Timing.QpcTimebase(10, 0, 0), "{}", leaveOpen: true))
            for (int i = 0; i < 3; i++) w.Write(Sample(i));
        var cut = ms.ToArray()[..^10];
        using var r = new CaptureLogReader(new MemoryStream(cut));
        var c = r.ReadAll();
        Assert.Equal(2, c.Mouse.Length);
        Assert.Equal(72 - 10, c.TruncatedBytes);
    }

    [Fact]
    public void Rejects_foreign_files()
    {
        Assert.Throws<InvalidDataException>(() => new CaptureLogReader(new MemoryStream("not a log at all, definitely not"u8.ToArray())));
        var bad = new byte[48];
        "OVCAPLOG"u8.CopyTo(bad);
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(8), 99);
        Assert.Throws<InvalidDataException>(() => new CaptureLogReader(new MemoryStream(bad)));
    }

    [Fact]
    public void Recorder_writes_overflow_and_coalescing_markers_and_queued_markers()
    {
        var ring = new SpscRingBuffer<RawMouseEvent>(1 << 12);
        // 125 Hz arrivals of combined 8 kHz motion (coalesced), with a 5-packet sequence gap.
        var packets = new List<Synthetic.Packet>();
        for (double t = 1000; t < 1_000_000; t += 8000) packets.Add(new Synthetic.Packet(t, 128, 0));
        var events = Synthetic.Capture(packets, Synthetic.WakeOnArrival(packets)).ToList();
        events.RemoveRange(50, 5);

        var markers = new MarkerQueue();
        var ms = new MemoryStream();
        var writer = new CaptureLogWriter(ms, Synthetic.Timebase, "{}", leaveOpen: true);
        var rec = new CaptureRecorder(ring, writer, markers, new CaptureRecorderOptions { NominalRateHz = 8000, RetainCapacity = 1000 });
        markers.Post(MarkerKind.FocusGained, 1);
        foreach (var e in events) Assert.True(ring.TryWrite(e));
        Assert.Equal(events.Count, rec.Pump());
        writer.Dispose();

        Assert.Equal(5, rec.MissingPackets);
        Assert.True(rec.CoalescingMarkers > 10);
        Assert.Equal(events.Count, rec.Retained.Length);

        ms.Position = 0;
        var c = new CaptureLogReader(ms).ReadAll();
        Assert.Equal(events.Count, c.Mouse.Length);
        Assert.Equal(MarkerKind.FocusGained, c.Markers[0].Kind);
        var overflow = Assert.Single(c.Markers, m => m.Kind == MarkerKind.RingOverflow);
        Assert.Equal(50, overflow.A);
        Assert.Equal(5, overflow.B);
        Assert.Contains(c.Markers, m => m.Kind == MarkerKind.CoalescingSuspected && m.C < 0.1);
    }

    [Fact]
    public void Recorder_does_not_flag_full_rate_8kHz()
    {
        var ring = new SpscRingBuffer<RawMouseEvent>(1 << 16);
        var packets = Synthetic.Polled(8000, 1000, 1_000_000);
        var events = Synthetic.Capture(packets, Synthetic.PeriodicWakes(0, 1_010_000, 1000, 300));
        var writer = new CaptureLogWriter(new MemoryStream(), Synthetic.Timebase);
        var rec = new CaptureRecorder(ring, writer, null, new CaptureRecorderOptions { NominalRateHz = 8000 });
        foreach (var e in events) ring.TryWrite(e);
        rec.Pump();
        Assert.Equal(0, rec.CoalescingMarkers);
        Assert.Equal(0, rec.MissingPackets);
    }

    [Fact]
    public void Marker_queue_is_safe_for_multiple_producers()
    {
        var q = new MarkerQueue(1 << 16);
        Parallel.For(0, 4, p => { for (int i = 0; i < 5000; i++) q.Post(MarkerKind.TrainerEvent, (uint)p, i); });
        var buf = new MarkerRecord[1 << 16];
        int n = q.Drain(buf);
        Assert.Equal(20000, n);
        for (int p = 0; p < 4; p++)
        {
            var mine = buf[..n].Where(m => m.Code == p).Select(m => m.A).ToArray();
            Assert.Equal(Enumerable.Range(0, 5000).Select(i => (long)i), mine);
        }
    }
}
