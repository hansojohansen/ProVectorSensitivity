using System.Runtime.InteropServices;

namespace OpenVector.Capture;

/// <summary>
/// One raw mouse packet as delivered by Windows Raw Input (one <c>RAWINPUT</c> record of type mouse),
/// plus the capture metadata we attach at read time.
/// </summary>
/// <remarks>
/// <para>
/// Raw Input carries <b>no hardware timestamp</b>. <see cref="QpcRead"/> is the QueryPerformanceCounter value
/// taken immediately before the <c>GetRawInputBuffer</c> call that returned this packet; every packet in the
/// same batch shares it. Per-packet time is reconstructed later from the batch structure
/// (see <see cref="Timing.TimestampReconstructor"/>), never at capture time (research C §2.3 item 4).
/// </para>
/// <para>
/// Coordinates follow D14: <see cref="Dx"/> &gt; 0 is right, <see cref="Dy"/> &gt; 0 is down, in raw counts.
/// The struct is blittable (64 bytes, one cache line) so it can live in the lock-free ring buffer.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct RawMouseEvent
{
    /// <summary>QPC ticks (Stopwatch ticks) taken right before the <c>GetRawInputBuffer</c> call that returned this packet.</summary>
    public long QpcRead;

    /// <summary>
    /// QPC ticks taken before the <b>previous</b> <c>GetRawInputBuffer</c> call on the capture thread, including
    /// calls that returned no data (the final "is it empty?" call of each drain). The OS queue was empty at that
    /// moment, so every packet of this batch arrived in (<see cref="QpcPrevRead"/>, <see cref="QpcRead"/>].
    /// 0 for the very first read of a session.
    /// </summary>
    public long QpcPrevRead;

    /// <summary>
    /// Monotonic packet sequence number assigned by the capture thread to every mouse packet it parsed,
    /// <b>including</b> packets later dropped because the ring was full. A gap in the sequence seen by a
    /// consumer therefore means ring overflow, not an OS-level drop.
    /// </summary>
    public ulong Sequence;

    /// <summary>Index of the read (one <c>GetRawInputBuffer</c> call) that returned this packet. Starts at 0.</summary>
    public uint BatchIndex;

    /// <summary>Number of mouse packets returned by that read.</summary>
    public ushort BatchSize;

    /// <summary>Position of this packet inside its batch, 0-based, in the order the OS returned them (oldest first).</summary>
    public ushort BatchPosition;

    /// <summary>
    /// The <c>RAWINPUTHEADER.hDevice</c> handle, widened to 64 bits. 0 means the packet did not come from a
    /// physical device the OS could attribute (e.g. injected input, some touchpads).
    /// </summary>
    public ulong Device;

    /// <summary><c>RAWMOUSE.lLastX</c>. Relative counts when <see cref="Flags"/> has no <see cref="RawMouseFlags.MoveAbsolute"/>.</summary>
    public int Dx;

    /// <summary><c>RAWMOUSE.lLastY</c>. Relative counts when <see cref="Flags"/> has no <see cref="RawMouseFlags.MoveAbsolute"/>.</summary>
    public int Dy;

    /// <summary><c>RAWMOUSE.usFlags</c>.</summary>
    public RawMouseFlags Flags;

    /// <summary><c>RAWMOUSE.usButtonFlags</c> (button transitions and wheel).</summary>
    public RawMouseButtonFlags ButtonFlags;

    /// <summary><c>RAWMOUSE.usButtonData</c> (wheel delta, signed when a wheel flag is set).</summary>
    public short ButtonData;

    /// <summary>Reserved, always 0 (keeps the layout explicit).</summary>
    public ushort Reserved;

    /// <summary><c>RAWMOUSE.ulRawButtons</c> (device-specific raw button state; usually 0).</summary>
    public uint RawButtons;

    /// <summary>
    /// <c>RAWMOUSE.ulExtraInformation</c>. With Raw Accel's <c>setExtraInfo</c> enabled this holds the
    /// pre-modification counts; see <see cref="RawAccelExtraInfo"/>.
    /// </summary>
    public uint ExtraInformation;

    /// <summary>True when this is relative motion (the only kind OpenVector calibrates on).</summary>
    public readonly bool IsRelative => (Flags & RawMouseFlags.MoveAbsolute) == 0;
}

/// <summary><c>RAWMOUSE.usFlags</c> values (winuser.h).</summary>
[Flags]
public enum RawMouseFlags : ushort
{
    /// <summary><c>MOUSE_MOVE_RELATIVE</c> (0): lLastX/lLastY are relative counts.</summary>
    MoveRelative = 0x00,
    /// <summary><c>MOUSE_MOVE_ABSOLUTE</c>: lLastX/lLastY are absolute 0..65535 coordinates (tablets, RDP, VMs). Not usable for calibration.</summary>
    MoveAbsolute = 0x01,
    /// <summary><c>MOUSE_VIRTUAL_DESKTOP</c>: absolute coordinates map to the whole virtual desktop.</summary>
    VirtualDesktop = 0x02,
    /// <summary><c>MOUSE_ATTRIBUTES_CHANGED</c>: attributes changed; application must re-query.</summary>
    AttributesChanged = 0x04,
    /// <summary><c>MOUSE_MOVE_NOCOALESCE</c>: this move must not be coalesced.</summary>
    MoveNoCoalesce = 0x08,
}

/// <summary><c>RAWMOUSE.usButtonFlags</c> values (winuser.h).</summary>
[Flags]
public enum RawMouseButtonFlags : ushort
{
    None = 0,
    LeftDown = 0x0001,
    LeftUp = 0x0002,
    RightDown = 0x0004,
    RightUp = 0x0008,
    MiddleDown = 0x0010,
    MiddleUp = 0x0020,
    Button4Down = 0x0040,
    Button4Up = 0x0080,
    Button5Down = 0x0100,
    Button5Up = 0x0200,
    Wheel = 0x0400,
    HWheel = 0x0800,
}
