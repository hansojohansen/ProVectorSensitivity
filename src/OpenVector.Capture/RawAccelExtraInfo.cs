namespace OpenVector.Capture;

/// <summary>
/// Decodes Raw Accel's <c>setExtraInfo</c> payload from <see cref="RawMouseEvent.ExtraInformation"/>.
/// </summary>
/// <remarks>
/// <para>
/// With the Raw Accel device option <c>setExtraInfo</c> enabled, the driver overwrites
/// <c>MOUSE_INPUT_DATA.ExtraInformation</c> with the <b>pre-modification</b> counts packed as
/// <c>union { struct { short x; short y; }; ULONG raw; }</c>, i.e. low 16 bits = x, high 16 bits = y
/// (research C §1.6; Raw Accel <c>driver/driver.cpp</c>). Raw Input exposes it as <c>RAWMOUSE.ulExtraInformation</c>,
/// while <c>lLastX/lLastY</c> carry the post-modification counts. Logging both lets the trainer verify that the
/// applied profile is really in effect (D7).
/// </para>
/// <para>Caveats (why this is a <i>Try</i> API and analysis must cross-check):</para>
/// <list type="bullet">
/// <item>Values are truncated to 16 bits; a single packet beyond ±32767 counts wraps.</item>
/// <item>The field is also used by other producers: pen/touch-generated mouse messages carry the signature
/// <c>0xFF515700</c> in the upper 24 bits (<c>(extra &amp; 0xFFFFFF00) == 0xFF515700</c>), and
/// software that synthesises input can put anything in this field. Such values are rejected or must be filtered.</item>
/// <item>When <c>setExtraInfo</c> is off (the default) the field is normally 0, which is indistinguishable from
/// "pre-modification motion was (0,0)". Use <see cref="LooksEnabled"/> over a whole session, not per packet.</item>
/// <item>Raw Accel must actually be processing the device (not excluded by device id) for the field to be written.</item>
/// </list>
/// </remarks>
public static class RawAccelExtraInfo
{
    /// <summary>Upper-24-bit signature Windows puts on mouse messages synthesized from pen/touch input.</summary>
    public const uint PenTouchSignatureMask = 0xFFFFFF00;
    public const uint PenTouchSignature = 0xFF515700;

    /// <summary>Unpacks the two 16-bit pre-modification counts. Returns false for the pen/touch signature.</summary>
    public static bool TryDecode(uint extraInformation, out short preDx, out short preDy)
    {
        preDx = unchecked((short)(extraInformation & 0xFFFF));
        preDy = unchecked((short)(extraInformation >> 16));
        return (extraInformation & PenTouchSignatureMask) != PenTouchSignature;
    }

    /// <summary>Packs pre-modification counts the way Raw Accel does (used by tests and simulators).</summary>
    public static uint Encode(short preDx, short preDy) =>
        unchecked((uint)(ushort)preDx | ((uint)(ushort)preDy << 16));

    /// <summary>
    /// Session-level heuristic: <c>setExtraInfo</c> looks enabled if a clear majority of moving relative packets
    /// carry a non-zero payload whose signs agree with the post-modification deltas (true for any positive
    /// sensitivity without rotation). Returns false for fewer than <paramref name="minPackets"/> moving packets.
    /// </summary>
    public static bool LooksEnabled(ReadOnlySpan<RawMouseEvent> events, int minPackets = 200, double minAgreement = 0.9)
    {
        int moving = 0, agree = 0;
        foreach (ref readonly var e in events)
        {
            if (!e.IsRelative || (e.Dx == 0 && e.Dy == 0)) continue;
            moving++;
            if (e.ExtraInformation == 0 || !TryDecode(e.ExtraInformation, out short px, out short py)) continue;
            if (Math.Sign(px) == Math.Sign(e.Dx) && Math.Sign(py) == Math.Sign(e.Dy)) agree++;
        }
        return moving >= minPackets && agree >= minAgreement * moving;
    }
}
