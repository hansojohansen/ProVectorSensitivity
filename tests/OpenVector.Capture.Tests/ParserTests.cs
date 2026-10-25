using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using OpenVector.Capture.Parsing;

namespace OpenVector.Capture.Tests;

public class ParserTests
{
    private struct ListSink : IRawMouseSink
    {
        public List<RawMouseEvent> Events;
        public void OnMouse(in RawMouseEvent e) => Events.Add(e);
    }

    private sealed class BufferBuilder(RawInputLayout layout)
    {
        private readonly List<byte> _bytes = [];

        public BufferBuilder Mouse(ulong device, ushort flags, ushort buttonFlags, short buttonData, uint rawButtons, int dx, int dy, uint extra)
        {
            var payload = new byte[RawInputLayout.RawMouseSize];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, flags);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), buttonFlags);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(6), buttonData);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), rawButtons);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), dx);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), dy);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), extra);
            return Record(RawInputBufferParser.RimTypeMouse, device, payload);
        }

        public BufferBuilder Record(uint type, ulong device, byte[] payload)
        {
            int size = layout.HeaderSize + payload.Length;
            var header = new byte[layout.HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, type);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)size);
            if (layout.HandleSize == 8) BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), device);
            else BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)device);
            _bytes.AddRange(header);
            _bytes.AddRange(payload);
            while (_bytes.Count % layout.Alignment != 0) _bytes.Add(0xCC); // NEXTRAWINPUTBLOCK padding (garbage)
            return this;
        }

        public byte[] Build() => [.. _bytes, .. new byte[64]]; // trailing slack like a real oversized buffer
    }

    public static TheoryData<string> Layouts => new() { "Native64", "Native32", "Wow64" };

    private static RawInputLayout L(string name) => name switch
    {
        "Native64" => RawInputLayout.Native64,
        "Native32" => RawInputLayout.Native32,
        _ => RawInputLayout.Wow64,
    };

    [Fact]
    public void Event_struct_is_one_cache_line() => Assert.Equal(64, Unsafe.SizeOf<RawMouseEvent>());

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Parses_all_fields_skips_non_mouse_and_honours_alignment(string layoutName)
    {
        var layout = L(layoutName);
        // A 3-byte HID payload makes the record size odd, so NEXTRAWINPUTBLOCK alignment matters.
        var buf = new BufferBuilder(layout)
            .Mouse(0xABCD, 0, (ushort)RawMouseButtonFlags.LeftDown, 0, 7, 5, -3, 0x00020001)
            .Record(RawInputBufferParser.RimTypeHid, 0x99, [1, 2, 3])
            .Mouse(0xABCD, 0, (ushort)RawMouseButtonFlags.Wheel, -120, 0, 0, 0, 0)
            .Record(RawInputBufferParser.RimTypeKeyboard, 0x98, new byte[16])
            .Mouse(0xBEEF, 1, 0, 0, 0, 30000, 40000, 0)
            .Build();

        var sink = new ListSink { Events = [] };
        var counters = new ParseCounters();
        ulong seq = 100;
        int emitted = RawInputBufferParser.Parse(buf, 5, layout, qpcRead: 777, qpcPrevRead: 555, batchIndex: 9,
            ref seq, ref counters, ref sink);

        Assert.Equal(3, emitted);
        Assert.Equal(103UL, seq);
        Assert.Equal(3, counters.MousePackets);
        Assert.Equal(2, counters.NonMouseRecords);
        Assert.Equal(1, counters.AbsolutePackets);
        Assert.Equal(0, counters.MalformedRecords);

        var e = sink.Events;
        Assert.All(e, x => { Assert.Equal(777, x.QpcRead); Assert.Equal(555, x.QpcPrevRead); Assert.Equal(9u, x.BatchIndex); Assert.Equal(3, x.BatchSize); });
        Assert.Equal([0, 1, 2], e.Select(x => (int)x.BatchPosition));
        Assert.Equal([100UL, 101UL, 102UL], e.Select(x => x.Sequence));

        Assert.Equal(0xABCDUL, e[0].Device);
        Assert.Equal(5, e[0].Dx);
        Assert.Equal(-3, e[0].Dy);
        Assert.Equal(RawMouseButtonFlags.LeftDown, e[0].ButtonFlags);
        Assert.Equal(7u, e[0].RawButtons);
        Assert.Equal(0x00020001u, e[0].ExtraInformation);
        Assert.True(e[0].IsRelative);

        Assert.Equal(RawMouseButtonFlags.Wheel, e[1].ButtonFlags);
        Assert.Equal(-120, e[1].ButtonData);

        Assert.Equal(0xBEEFUL, e[2].Device);
        Assert.False(e[2].IsRelative);
        Assert.Equal(30000, e[2].Dx);
        Assert.Equal(40000, e[2].Dy);
    }

    [Fact]
    public void Truncated_buffer_is_counted_and_stops_cleanly()
    {
        var layout = RawInputLayout.Native64;
        var full = new BufferBuilder(layout).Mouse(1, 0, 0, 0, 0, 1, 1, 0).Mouse(1, 0, 0, 0, 0, 2, 2, 0).Build();
        var cut = full.AsSpan(0, 48 + 20).ToArray(); // second record truncated
        var sink = new ListSink { Events = [] };
        var counters = new ParseCounters();
        ulong seq = 0;
        int emitted = RawInputBufferParser.Parse(cut, 2, layout, 1, 0, 0, ref seq, ref counters, ref sink);
        Assert.Equal(1, emitted);
        Assert.Equal(1, counters.MalformedRecords);
        Assert.Equal(1, sink.Events[0].BatchSize);
    }

    [Fact]
    public void Layout_for_current_process_is_64_bit_here() =>
        Assert.Equal(Environment.Is64BitProcess ? RawInputLayout.Native64 : RawInputLayout.Wow64, RawInputLayout.ForCurrentProcess());
}
