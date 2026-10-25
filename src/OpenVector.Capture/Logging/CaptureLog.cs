using System.Buffers.Binary;
using System.Text;
using OpenVector.Capture.Timing;

namespace OpenVector.Capture.Logging;

/// <summary>Record types of the capture log. Readers skip types they do not know (framing carries the length).</summary>
public enum CaptureRecordType : ushort
{
    /// <summary>One raw mouse packet (<see cref="RawMouseEvent"/>).</summary>
    Mouse = 1,
    /// <summary>A timestamped marker (<see cref="MarkerRecord"/>): frames, trainer events, focus, overflow, coalescing …</summary>
    Marker = 2,
    // 3..0x7FFF reserved for future OpenVector record types (e.g. dedicated frame or trial records).
    // 0x8000..0xFFFF free for experiments; never used by released writers.
}

/// <summary>
/// OpenVector capture log (<c>*.ovcap</c>), schema version 1. Platform-neutral, little-endian, self-framing records.
/// </summary>
/// <remarks>
/// <code>
/// File = Header (48 bytes) + Metadata (UTF-8 JSON, MetadataLength bytes) + Record*
///
/// Header
///   off size type   field
///   0   8    ascii  magic "OVCAPLOG"
///   8   2    u16    schema_version (1)
///   10  2    u16    header size (48)           readers skip header bytes beyond the ones they know
///   12  4    u32    reserved (0)
///   16  8    i64    QPC frequency (ticks/s)
///   24  8    i64    origin QPC                 (maps to t = 0 µs on the D14 clock)
///   32  8    i64    UTC at origin, µs since Unix epoch
///   40  4    u32    metadata length in bytes   (free-form JSON: tool, devices, options …)
///   44  4    u32    reserved (0)
///
/// Record = Frame (8 bytes) + Payload
///   0   2    u16    record type (CaptureRecordType)
///   2   2    u16    flags (0, reserved)
///   4   4    u32    payload length in bytes
///   Unknown types are skipped by length. Known types may grow: readers read the fields they know and skip
///   the rest of the payload, so new fields are only ever appended.
///
/// Mouse payload (type 1, 64 bytes)
///   0   8    i64    qpc_read                   QPC before the GetRawInputBuffer call that returned it
///   8   8    i64    qpc_prev_read              QPC before the previous read (empty or not); 0 = none
///   16  8    u64    sequence                   per-session packet counter; gaps = in-process ring overflow
///   24  4    u32    batch_index
///   28  2    u16    batch_size
///   30  2    u16    batch_position
///   32  8    u64    device (hDevice)
///   40  4    i32    dx (lLastX)                D14: +x right
///   44  4    i32    dy (lLastY)                D14: +y down
///   48  2    u16    usFlags                    bit 0 = MOUSE_MOVE_ABSOLUTE
///   50  2    u16    usButtonFlags              all button transitions + wheel/h-wheel flags
///   52  2    i16    usButtonData               wheel / h-wheel delta
///   54  2    u16    reserved
///   56  4    u32    ulRawButtons
///   60  4    u32    ulExtraInformation         Raw Accel setExtraInfo payload when enabled
///
/// Marker payload (type 2, 44 + text bytes)
///   0   8    i64    qpc                        same clock as mouse records
///   8   4    u32    kind (MarkerKind)
///   12  4    u32    code                       kind-specific sub-code
///   16  8    i64    a                          kind-specific
///   24  8    i64    b                          kind-specific
///   32  8    f64    c                          kind-specific
///   40  4    u32    text length (bytes)
///   44  n    utf8   text
/// </code>
/// <para>Records appear in write order, which is <b>not</b> guaranteed to be QPC order across record types
/// (mouse packets and markers come from different threads); sort by QPC when merging. A trailing partial record
/// (crash) is ignored and reported via <see cref="CaptureLogReader.TruncatedBytes"/>. Sibling files (e.g. a separate
/// frame log) use the same header, so all streams of a session share one clock. Per-packet µs timestamps are not
/// stored; they are derived with <see cref="TimestampReconstructor"/>. Parquet export is planned separately (D7).</para>
/// </remarks>
public static class CaptureLogFormat
{
    public static ReadOnlySpan<byte> Magic => "OVCAPLOG"u8;
    public const ushort SchemaVersion = 1;
    public const int HeaderSize = 48;
    public const int FrameSize = 8;
    public const int MousePayloadSize = 64;
    public const int MarkerFixedSize = 44;
    public const string FileExtension = ".ovcap";

    internal static void WriteMouse(Span<byte> d, in RawMouseEvent e)
    {
        BinaryPrimitives.WriteInt64LittleEndian(d, e.QpcRead);
        BinaryPrimitives.WriteInt64LittleEndian(d[8..], e.QpcPrevRead);
        BinaryPrimitives.WriteUInt64LittleEndian(d[16..], e.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(d[24..], e.BatchIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(d[28..], e.BatchSize);
        BinaryPrimitives.WriteUInt16LittleEndian(d[30..], e.BatchPosition);
        BinaryPrimitives.WriteUInt64LittleEndian(d[32..], e.Device);
        BinaryPrimitives.WriteInt32LittleEndian(d[40..], e.Dx);
        BinaryPrimitives.WriteInt32LittleEndian(d[44..], e.Dy);
        BinaryPrimitives.WriteUInt16LittleEndian(d[48..], (ushort)e.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(d[50..], (ushort)e.ButtonFlags);
        BinaryPrimitives.WriteInt16LittleEndian(d[52..], e.ButtonData);
        BinaryPrimitives.WriteUInt16LittleEndian(d[54..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d[56..], e.RawButtons);
        BinaryPrimitives.WriteUInt32LittleEndian(d[60..], e.ExtraInformation);
    }

    internal static RawMouseEvent ReadMouse(ReadOnlySpan<byte> s) => new()
    {
        QpcRead = BinaryPrimitives.ReadInt64LittleEndian(s),
        QpcPrevRead = BinaryPrimitives.ReadInt64LittleEndian(s[8..]),
        Sequence = BinaryPrimitives.ReadUInt64LittleEndian(s[16..]),
        BatchIndex = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
        BatchSize = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]),
        BatchPosition = BinaryPrimitives.ReadUInt16LittleEndian(s[30..]),
        Device = BinaryPrimitives.ReadUInt64LittleEndian(s[32..]),
        Dx = BinaryPrimitives.ReadInt32LittleEndian(s[40..]),
        Dy = BinaryPrimitives.ReadInt32LittleEndian(s[44..]),
        Flags = (RawMouseFlags)BinaryPrimitives.ReadUInt16LittleEndian(s[48..]),
        ButtonFlags = (RawMouseButtonFlags)BinaryPrimitives.ReadUInt16LittleEndian(s[50..]),
        ButtonData = BinaryPrimitives.ReadInt16LittleEndian(s[52..]),
        RawButtons = BinaryPrimitives.ReadUInt32LittleEndian(s[56..]),
        ExtraInformation = BinaryPrimitives.ReadUInt32LittleEndian(s[60..]),
    };

    internal static void WriteMarkerFixed(Span<byte> d, in MarkerRecord m, int textLength)
    {
        BinaryPrimitives.WriteInt64LittleEndian(d, m.Qpc);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], (uint)m.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], m.Code);
        BinaryPrimitives.WriteInt64LittleEndian(d[16..], m.A);
        BinaryPrimitives.WriteInt64LittleEndian(d[24..], m.B);
        BinaryPrimitives.WriteDoubleLittleEndian(d[32..], m.C);
        BinaryPrimitives.WriteUInt32LittleEndian(d[40..], (uint)textLength);
    }

    internal static MarkerRecord ReadMarkerFixed(ReadOnlySpan<byte> s, out int textLength)
    {
        textLength = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(s[40..]), int.MaxValue);
        return new MarkerRecord(
            BinaryPrimitives.ReadInt64LittleEndian(s),
            (MarkerKind)BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(s[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(s[24..]),
            BinaryPrimitives.ReadDoubleLittleEndian(s[32..]));
    }
}

/// <summary>
/// Streams records to a <c>.ovcap</c> log. Allocation-free for mouse records and text-less markers
/// (internal 64 KiB buffer). Not thread-safe: use from one logger thread (never from the capture thread);
/// other threads hand markers over through a <see cref="MarkerQueue"/>.
/// </summary>
public sealed class CaptureLogWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _used;
    private bool _disposed;

    public long MouseRecordsWritten { get; private set; }
    public long MarkerRecordsWritten { get; private set; }
    public QpcTimebase Timebase { get; }

    public CaptureLogWriter(Stream stream, QpcTimebase timebase, string metadataJson = "{}", bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
        Timebase = timebase;
        byte[] meta = Encoding.UTF8.GetBytes(metadataJson ?? "{}");
        Span<byte> h = stackalloc byte[CaptureLogFormat.HeaderSize];
        h.Clear();
        CaptureLogFormat.Magic.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], CaptureLogFormat.SchemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], CaptureLogFormat.HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(h[16..], timebase.Frequency);
        BinaryPrimitives.WriteInt64LittleEndian(h[24..], timebase.OriginQpc);
        BinaryPrimitives.WriteInt64LittleEndian(h[32..], timebase.UtcUnixMicrosAtOrigin);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)meta.Length);
        _stream.Write(h);
        _stream.Write(meta);
    }

    public static CaptureLogWriter Create(string path, QpcTimebase timebase, string metadataJson = "{}") =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), timebase, metadataJson);

    public void Write(in RawMouseEvent e)
    {
        Span<byte> d = Reserve(CaptureRecordType.Mouse, CaptureLogFormat.MousePayloadSize);
        CaptureLogFormat.WriteMouse(d, in e);
        MouseRecordsWritten++;
    }

    public void Write(ReadOnlySpan<RawMouseEvent> events)
    {
        foreach (ref readonly var e in events) Write(in e);
    }

    /// <summary>Writes a marker. <paramref name="text"/> is optional free text (UTF-8, allocates; keep it for rare events).</summary>
    public void WriteMarker(in MarkerRecord marker, string? text = null)
    {
        byte[]? textBytes = string.IsNullOrEmpty(text) ? null : Encoding.UTF8.GetBytes(text);
        int textLen = textBytes?.Length ?? 0;
        int payload = CaptureLogFormat.MarkerFixedSize + textLen;
        if (CaptureLogFormat.FrameSize + payload > _buffer.Length)
        {
            // Very long text: write frame + payload straight to the stream.
            FlushBuffer();
            Span<byte> head = stackalloc byte[CaptureLogFormat.FrameSize + CaptureLogFormat.MarkerFixedSize];
            WriteFrame(head, CaptureRecordType.Marker, payload);
            CaptureLogFormat.WriteMarkerFixed(head[CaptureLogFormat.FrameSize..], in marker, textLen);
            _stream.Write(head);
            _stream.Write(textBytes);
        }
        else
        {
            Span<byte> d = Reserve(CaptureRecordType.Marker, payload);
            CaptureLogFormat.WriteMarkerFixed(d, in marker, textLen);
            textBytes?.CopyTo(d[CaptureLogFormat.MarkerFixedSize..]);
        }
        MarkerRecordsWritten++;
    }

    /// <summary>
    /// Writes a record of an arbitrary type (for future record kinds such as dedicated frame records).
    /// Types 1 and 2 must use the typed overloads.
    /// </summary>
    public void WriteRaw(ushort recordType, ReadOnlySpan<byte> payload)
    {
        if (recordType is (ushort)CaptureRecordType.Mouse or (ushort)CaptureRecordType.Marker)
            throw new ArgumentException("Use the typed Write/WriteMarker overloads for built-in record types.", nameof(recordType));
        if (CaptureLogFormat.FrameSize + payload.Length > _buffer.Length)
        {
            FlushBuffer();
            Span<byte> head = stackalloc byte[CaptureLogFormat.FrameSize];
            WriteFrame(head, (CaptureRecordType)recordType, payload.Length);
            _stream.Write(head);
            _stream.Write(payload);
            return;
        }
        payload.CopyTo(Reserve((CaptureRecordType)recordType, payload.Length));
    }

    public void Flush()
    {
        FlushBuffer();
        _stream.Flush();
    }

    private Span<byte> Reserve(CaptureRecordType type, int payloadLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int total = CaptureLogFormat.FrameSize + payloadLength;
        if (_used + total > _buffer.Length) FlushBuffer();
        Span<byte> r = _buffer.AsSpan(_used, total);
        WriteFrame(r, type, payloadLength);
        _used += total;
        return r[CaptureLogFormat.FrameSize..];
    }

    private static void WriteFrame(Span<byte> d, CaptureRecordType type, int payloadLength)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(d[2..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], (uint)payloadLength);
    }

    private void FlushBuffer()
    {
        if (_used == 0) return;
        _stream.Write(_buffer, 0, _used);
        _used = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}

/// <summary>Everything in a log, split by record type (see <see cref="CaptureLogReader.ReadAll"/>).</summary>
public sealed record CaptureLogContents(
    QpcTimebase Timebase, string MetadataJson, RawMouseEvent[] Mouse, MarkerRecord[] Markers,
    IReadOnlyList<string?> MarkerTexts, long UnknownRecords, int TruncatedBytes);

/// <summary>Reads a <c>.ovcap</c> log record by record.</summary>
public sealed class CaptureLogReader : IDisposable
{
    private const int MaxPayload = 64 << 20;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private byte[] _payload = new byte[256];
    private int _payloadLength;

    public ushort SchemaVersion { get; }
    public QpcTimebase Timebase { get; }
    public string MetadataJson { get; }
    /// <summary>Bytes of a trailing partial record that were ignored (known after reading to the end).</summary>
    public int TruncatedBytes { get; private set; }

    /// <summary>Type of the record returned by the last successful <see cref="TryReadNext"/>.</summary>
    public ushort CurrentType { get; private set; }

    /// <summary>Payload of the current record (valid until the next read).</summary>
    public ReadOnlySpan<byte> CurrentPayload => _payload.AsSpan(0, _payloadLength);

    public CaptureLogReader(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
        Span<byte> h = stackalloc byte[CaptureLogFormat.HeaderSize];
        if (ReadFully(h) != h.Length || !h[..8].SequenceEqual(CaptureLogFormat.Magic))
            throw new InvalidDataException("Not an OpenVector capture log (bad magic or short header).");
        SchemaVersion = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]);
        if (SchemaVersion != CaptureLogFormat.SchemaVersion)
            throw new InvalidDataException($"Unsupported capture log schema version {SchemaVersion}.");
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
        if (headerSize < CaptureLogFormat.HeaderSize) throw new InvalidDataException("Corrupt capture log header.");
        Timebase = new QpcTimebase(
            BinaryPrimitives.ReadInt64LittleEndian(h[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(h[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(h[32..]));
        if (Timebase.Frequency <= 0) throw new InvalidDataException("Corrupt capture log header (frequency).");
        uint metaLen = BinaryPrimitives.ReadUInt32LittleEndian(h[40..]);
        if (metaLen > MaxPayload) throw new InvalidDataException("Corrupt capture log header (metadata length).");
        if (headerSize > CaptureLogFormat.HeaderSize) Skip(headerSize - CaptureLogFormat.HeaderSize);
        var meta = new byte[metaLen];
        if (ReadFully(meta) != meta.Length) throw new InvalidDataException("Capture log metadata is truncated.");
        MetadataJson = Encoding.UTF8.GetString(meta);
    }

    public static CaptureLogReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));

    /// <summary>Reads the next record of any type. Returns false at end of file (or at a truncated tail).</summary>
    public bool TryReadNext()
    {
        Span<byte> frame = stackalloc byte[CaptureLogFormat.FrameSize];
        int n = ReadFully(frame);
        if (n < frame.Length) { TruncatedBytes = n; return false; }
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]);
        if (len > MaxPayload) throw new InvalidDataException($"Corrupt record frame (payload length {len}).");
        if (_payload.Length < len) _payload = new byte[Math.Max((int)len, _payload.Length * 2)];
        int got = ReadFully(_payload.AsSpan(0, (int)len));
        if (got < len) { TruncatedBytes = n + got; return false; }
        CurrentType = type;
        _payloadLength = (int)len;
        return true;
    }

    /// <summary>Decodes the current record as a mouse packet (requires <see cref="CurrentType"/> == Mouse).</summary>
    public RawMouseEvent CurrentMouse()
    {
        if (CurrentType != (ushort)CaptureRecordType.Mouse || _payloadLength < CaptureLogFormat.MousePayloadSize)
            throw new InvalidOperationException("Current record is not a (complete) mouse record.");
        return CaptureLogFormat.ReadMouse(CurrentPayload);
    }

    /// <summary>Decodes the current record as a marker (requires <see cref="CurrentType"/> == Marker).</summary>
    public MarkerRecord CurrentMarker(out string? text)
    {
        if (CurrentType != (ushort)CaptureRecordType.Marker || _payloadLength < CaptureLogFormat.MarkerFixedSize)
            throw new InvalidOperationException("Current record is not a (complete) marker record.");
        var m = CaptureLogFormat.ReadMarkerFixed(CurrentPayload, out int textLen);
        textLen = Math.Min(textLen, _payloadLength - CaptureLogFormat.MarkerFixedSize);
        text = textLen > 0 ? Encoding.UTF8.GetString(CurrentPayload.Slice(CaptureLogFormat.MarkerFixedSize, textLen)) : null;
        return m;
    }

    /// <summary>Reads the remaining records into memory, split by type.</summary>
    public CaptureLogContents ReadAll()
    {
        var mouse = new List<RawMouseEvent>();
        var markers = new List<MarkerRecord>();
        var texts = new List<string?>();
        long unknown = 0;
        while (TryReadNext())
        {
            switch ((CaptureRecordType)CurrentType)
            {
                case CaptureRecordType.Mouse: mouse.Add(CurrentMouse()); break;
                case CaptureRecordType.Marker: markers.Add(CurrentMarker(out var t)); texts.Add(t); break;
                default: unknown++; break;
            }
        }
        return new CaptureLogContents(Timebase, MetadataJson, [.. mouse], [.. markers], texts, unknown, TruncatedBytes);
    }

    public static CaptureLogContents ReadFile(string path)
    {
        using var r = Open(path);
        return r.ReadAll();
    }

    private int ReadFully(Span<byte> dest)
    {
        int total = 0;
        while (total < dest.Length)
        {
            int n = _stream.Read(dest[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private void Skip(int count)
    {
        Span<byte> tmp = stackalloc byte[256];
        while (count > 0)
        {
            int n = ReadFully(tmp[..Math.Min(count, tmp.Length)]);
            if (n == 0) throw new InvalidDataException("Capture log header is truncated.");
            count -= n;
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen) _stream.Dispose();
    }
}
