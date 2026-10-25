namespace OpenVector.Capture.Tests;

public class DecoderTests
{
    [Theory]
    [InlineData((short)5, (short)-3)]
    [InlineData((short)-32768, (short)32767)]
    [InlineData((short)0, (short)0)]
    public void ExtraInfo_round_trip(short x, short y)
    {
        Assert.True(RawAccelExtraInfo.TryDecode(RawAccelExtraInfo.Encode(x, y), out var dx, out var dy));
        Assert.Equal(x, dx);
        Assert.Equal(y, dy);
    }

    [Fact]
    public void ExtraInfo_layout_is_low_x_high_y() =>
        Assert.Equal(0xFFFD0005u, RawAccelExtraInfo.Encode(5, -3));

    [Fact]
    public void ExtraInfo_rejects_pen_touch_signature() =>
        Assert.False(RawAccelExtraInfo.TryDecode(0xFF515701, out _, out _));

    [Fact]
    public void LooksEnabled_requires_consistent_payloads()
    {
        var on = Enumerable.Range(1, 300).Select(i => new RawMouseEvent
        { Dx = i % 7 + 1, Dy = -(i % 5 + 1), ExtraInformation = RawAccelExtraInfo.Encode((short)(i % 7 + 1), (short)-(i % 5 + 1)) }).ToArray();
        Assert.True(RawAccelExtraInfo.LooksEnabled(on));
        var off = on.Select(e => e with { ExtraInformation = 0 }).ToArray();
        Assert.False(RawAccelExtraInfo.LooksEnabled(off));
        Assert.False(RawAccelExtraInfo.LooksEnabled(on.AsSpan(0, 50)));
    }

    [Theory]
    [InlineData(@"\\?\HID#VID_046D&PID_C547&MI_01&Col01#8&2b2e4d2a&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", 0x046D, 0xC547)]
    [InlineData(@"\\?\hid#vid_1532&pid_00b4&mi_00#7&1e5a&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", 0x1532, 0x00B4)]
    [InlineData(@"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002046d_PID&b023&Col01#9&1&0&0000#{378de44c}", 0x046D, 0xB023)]
    public void Parses_vid_pid(string name, int vid, int pid)
    {
        var (v, p) = DeviceNameParser.ParseVidPid(name);
        Assert.Equal((ushort)vid, v);
        Assert.Equal((ushort)pid, p);
    }

    [Theory]
    [InlineData(@"\\?\ACPI#PNP0F13#4&1&0#{378de44c-56ef-11d1-bc8c-00a0c91405dd}")]
    [InlineData("")]
    [InlineData(null)]
    public void Missing_vid_pid_is_null(string? name)
    {
        var (v, p) = DeviceNameParser.ParseVidPid(name);
        Assert.Null(v);
        Assert.Null(p);
    }
}
