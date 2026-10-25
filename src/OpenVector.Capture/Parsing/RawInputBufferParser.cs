using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenVector.Capture.Parsing;

/// <summary>
/// Byte layout of the <c>RAWINPUT</c> records that <c>GetRawInputBuffer</c> writes, which depends on the
/// bitness of the process and of the OS.
/// </summary>
/// <remarks>
/// <code>
/// RAWINPUTHEADER { DWORD dwType; DWORD dwSize; HANDLE hDevice; WPARAM wParam; }
///   64-bit process : 4 + 4 + 8 + 8 = 24 bytes
///   32-bit process : 4 + 4 + 4 + 4 = 16 bytes
///   32-bit process on 64-bit Windows (WOW64): GetRawInputBuffer still writes the 64-bit layout
///                    (24-byte header, 8-byte handles, 8-byte aligned records). This is the documented
///                    "align RAWINPUT by 8 bytes on WOW64" gotcha; SDL handles it the same way.
/// RAWMOUSE (identical on all) :
///   +0  USHORT usFlags
///   +2  (padding)
///   +4  USHORT usButtonFlags   (union with ULONG ulButtons)
///   +6  USHORT usButtonData
///   +8  ULONG  ulRawButtons
///   +12 LONG   lLastX
///   +16 LONG   lLastY
///   +20 ULONG  ulExtraInformation      -> 24 bytes
/// NEXTRAWINPUTBLOCK(p) = RAWINPUT_ALIGN(p + p->header.dwSize)
///   RAWINPUT_ALIGN aligns to sizeof(QWORD)=8 on _WIN64 builds and sizeof(DWORD)=4 on 32-bit builds.
/// </code>
/// OpenVector ships x64 only; the 32-bit layouts are implemented for correctness and unit-tested with
/// synthetic buffers but not exercised on real hardware.
/// </remarks>
public readonly record struct RawInputLayout(int HeaderSize, int HandleSize, int Alignment)
{
    /// <summary>64-bit process (x64, ARM64).</summary>
    public static RawInputLayout Native64 => new(24, 8, 8);

    /// <summary>32-bit process on 32-bit Windows.</summary>
    public static RawInputLayout Native32 => new(16, 4, 4);

    /// <summary>32-bit process on 64-bit Windows: records use the 64-bit layout.</summary>
    public static RawInputLayout Wow64 => new(24, 8, 8);

    /// <summary>Layout for the current process.</summary>
    public static RawInputLayout ForCurrentProcess() =>
        Environment.Is64BitProcess ? Native64 : Environment.Is64BitOperatingSystem ? Wow64 : Native32;

    /// <summary>Size of the mouse payload (<c>RAWMOUSE</c>).</summary>
    public const int RawMouseSize = 24;
}

/// <summary>Receives parsed mouse packets. Implement as a struct to keep the hot path allocation-free and inlined.</summary>
public interface IRawMouseSink
{
    void OnMouse(in RawMouseEvent e);
}

/// <summary>Counters updated by <see cref="RawInputBufferParser.Parse{TSink}"/>.</summary>
public struct ParseCounters
{
    /// <summary>Mouse packets parsed (relative + absolute).</summary>
    public long MousePackets;
    /// <summary>Mouse packets with <c>MOUSE_MOVE_ABSOLUTE</c> (passed through flagged, not usable for calibration).</summary>
    public long AbsolutePackets;
    /// <summary>Records of other types (keyboard/HID) – should be 0 since only the mouse is registered.</summary>
    public long NonMouseRecords;
    /// <summary>Records that were truncated or had an impossible size; parsing of that buffer stopped.</summary>
    public long MalformedRecords;
}

/// <summary>Walks a <c>GetRawInputBuffer</c> result using NEXTRAWINPUTBLOCK semantics. Platform-neutral.</summary>
public static class RawInputBufferParser
{
    public const uint RimTypeMouse = 0;
    public const uint RimTypeKeyboard = 1;
    public const uint RimTypeHid = 2;

    /// <summary>
    /// Parses <paramref name="recordCount"/> records from <paramref name="buffer"/> (the count returned by
    /// <c>GetRawInputBuffer</c>) and emits one <see cref="RawMouseEvent"/> per mouse record.
    /// </summary>
    /// <param name="qpcRead">QPC value taken just before the read; stamped on every packet.</param>
    /// <param name="qpcPrevRead">QPC value taken before the previous read (empty or not); 0 if none.</param>
    /// <param name="batchIndex">Index of this read.</param>
    /// <param name="sequence">Running packet sequence; incremented for every mouse packet emitted.</param>
    /// <returns>Number of mouse packets emitted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Parse<TSink>(
        ReadOnlySpan<byte> buffer, int recordCount, RawInputLayout layout,
        long qpcRead, long qpcPrevRead, uint batchIndex, ref ulong sequence, ref ParseCounters counters, ref TSink sink)
        where TSink : struct, IRawMouseSink
    {
        // First pass: count mouse records so every event can carry its batch size. Walking headers is cheap
        // (a few hundred records at most) and keeps BatchSize exact even if non-mouse records are interleaved.
        int mouseCount = 0;
        int offset = 0;
        int valid = 0;
        for (int i = 0; i < recordCount; i++)
        {
            if (!TryReadHeader(buffer, offset, layout, out uint type, out int size, out _))
            {
                counters.MalformedRecords++;
                break;
            }
            if (type == RimTypeMouse) mouseCount++;
            valid++;
            offset = Align(offset + size, layout.Alignment);
        }

        ushort batchSize = (ushort)Math.Min(mouseCount, ushort.MaxValue);
        int pos = 0;
        offset = 0;
        for (int i = 0; i < valid; i++)
        {
            TryReadHeader(buffer, offset, layout, out uint type, out int size, out ulong device);
            if (type == RimTypeMouse)
            {
                ReadOnlySpan<byte> m = buffer.Slice(offset + layout.HeaderSize, RawInputLayout.RawMouseSize);
                var e = new RawMouseEvent
                {
                    QpcRead = qpcRead,
                    QpcPrevRead = qpcPrevRead,
                    Sequence = sequence++,
                    BatchIndex = batchIndex,
                    BatchSize = batchSize,
                    BatchPosition = (ushort)Math.Min(pos++, ushort.MaxValue),
                    Device = device,
                    Flags = (RawMouseFlags)BinaryPrimitives.ReadUInt16LittleEndian(m),
                    ButtonFlags = (RawMouseButtonFlags)BinaryPrimitives.ReadUInt16LittleEndian(m[4..]),
                    ButtonData = BinaryPrimitives.ReadInt16LittleEndian(m[6..]),
                    RawButtons = BinaryPrimitives.ReadUInt32LittleEndian(m[8..]),
                    Dx = BinaryPrimitives.ReadInt32LittleEndian(m[12..]),
                    Dy = BinaryPrimitives.ReadInt32LittleEndian(m[16..]),
                    ExtraInformation = BinaryPrimitives.ReadUInt32LittleEndian(m[20..]),
                };
                counters.MousePackets++;
                if (!e.IsRelative) counters.AbsolutePackets++;
                sink.OnMouse(in e);
            }
            else
            {
                counters.NonMouseRecords++;
            }
            offset = Align(offset + size, layout.Alignment);
        }
        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadHeader(ReadOnlySpan<byte> buffer, int offset, RawInputLayout layout,
        out uint type, out int size, out ulong device)
    {
        type = 0; size = 0; device = 0;
        if (offset < 0 || offset + layout.HeaderSize > buffer.Length) return false;
        type = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
        uint dwSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 4)..]);
        device = layout.HandleSize == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(buffer[(offset + 8)..])
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 8)..]);
        if (dwSize < layout.HeaderSize || offset + (long)dwSize > buffer.Length) return false;
        if (type == RimTypeMouse && dwSize < layout.HeaderSize + RawInputLayout.RawMouseSize) return false;
        size = (int)dwSize;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
