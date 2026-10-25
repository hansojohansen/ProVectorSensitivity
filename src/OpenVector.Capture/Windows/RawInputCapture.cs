using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenVector.Capture.Logging;
using OpenVector.Capture.Parsing;
using OpenVector.Capture.Timing;

namespace OpenVector.Capture.Windows;

/// <summary>Which window the capture thread registers raw input for.</summary>
public enum CaptureWindowKind
{
    /// <summary>
    /// A message-only window (<c>HWND_MESSAGE</c>) owned by the capture thread (research C §2.3). The process
    /// must still own the foreground window (e.g. the trainer's render window) to receive input.
    /// </summary>
    MessageOnly,

    /// <summary>
    /// A small visible top-level window owned by the capture thread itself. Fallback in case foreground delivery
    /// to a message-only window on a separate thread does not work on a given Windows build (spike S1 checks this).
    /// The user must click this window so that it is the foreground window.
    /// </summary>
    Visible,
}

/// <summary>Options for <see cref="RawInputCapture"/>.</summary>
public sealed record RawInputCaptureOptions
{
    /// <summary>Ring capacity in events (power of two). 65536 ≈ 8 s of 8 kHz input with no consumer.</summary>
    public int RingCapacity { get; init; } = 1 << 16;

    /// <summary>Bytes passed to each <c>GetRawInputBuffer</c> call (≈1365 mouse records at 64 KiB on x64).</summary>
    public int ReadBufferBytes { get; init; } = 64 * 1024;

    public CaptureWindowKind Window { get; init; } = CaptureWindowKind.MessageOnly;

    /// <summary>Title of the window when <see cref="Window"/> is <see cref="CaptureWindowKind.Visible"/>.</summary>
    public string VisibleWindowTitle { get; init; } = "OpenVector capture";

    /// <summary>Raise the capture thread to <c>THREAD_PRIORITY_TIME_CRITICAL</c> (as SDL3 does). Otherwise Highest.</summary>
    public bool TimeCriticalPriority { get; init; } = true;

    /// <summary>
    /// Optional queue for markers the capture thread emits: <see cref="MarkerKind.FocusGained"/>/<see cref="MarkerKind.FocusLost"/>
    /// (visible mode; in message-only mode the owner of the foreground window posts these from its own WM_ACTIVATE)
    /// and <see cref="MarkerKind.ReadError"/>. Usually the same queue the <see cref="CaptureRecorder"/> drains.
    /// </summary>
    public MarkerQueue? Markers { get; init; }
}

/// <summary>Live counters of a capture session (read with <see cref="RawInputCapture.GetCounters"/>).</summary>
public sealed record CaptureCounters
{
    /// <summary>Times the capture thread woke up for input.</summary>
    public long Wakeups { get; init; }
    /// <summary>Non-empty <c>GetRawInputBuffer</c> reads.</summary>
    public long Batches { get; init; }
    public long MousePackets { get; init; }
    /// <summary>Packets flagged <c>MOUSE_MOVE_ABSOLUTE</c> (pushed, but not usable for calibration).</summary>
    public long AbsolutePackets { get; init; }
    public long NonMouseRecords { get; init; }
    public long MalformedRecords { get; init; }
    /// <summary>Events dropped because the consumer did not drain the ring fast enough.</summary>
    public long RingOverflows { get; init; }
    /// <summary><c>GetRawInputBuffer</c> calls that returned an error.</summary>
    public long ReadErrors { get; init; }
    public int LastWin32Error { get; init; }
    /// <summary>
    /// <c>WM_INPUT</c> messages read one by one with <c>GetRawInputData</c> after a drain: either a packet that
    /// arrived in the instant between the empty buffered read and the check, or one the buffered read did not
    /// consume. Expected to be a small fraction of <see cref="Batches"/>; no data is lost either way.
    /// </summary>
    public long UnbufferedMessages { get; init; }
    public int MaxBatchSize { get; init; }
    /// <summary>Longest wake-to-drained time of one wakeup, µs (time the capture thread was busy reading).</summary>
    public double MaxDrainMicros { get; init; }
    /// <summary>Managed bytes allocated by the capture thread since its loop started (should stay 0).</summary>
    public long CaptureThreadAllocatedBytes { get; init; }
}

/// <summary>
/// Foreground-only raw mouse capture on a dedicated high-priority thread (D5, D7, research C §2.3).
/// </summary>
/// <remarks>
/// <para><b>Design.</b> The capture thread owns its window and is the only raw-input registrant in the process
/// (Windows allows one target window per device class per process; the last registration wins). It blocks in
/// <c>MsgWaitForMultipleObjectsEx(QS_RAWINPUT)</c>, then repeatedly takes a QPC stamp and calls
/// <c>GetRawInputBuffer</c> until the queue is empty, and pushes every mouse packet into a preallocated
/// <see cref="SpscRingBuffer{T}"/>. The loop is allocation-free (native read buffer, struct sink, no boxing);
/// <see cref="CaptureCounters.CaptureThreadAllocatedBytes"/> verifies this at runtime.</para>
/// <para><b>Foreground only (D5).</b> The registration uses flags = 0: Windows delivers raw input only while this
/// process owns the foreground window. The background-sink registration flags are deliberately never used (the
/// constraint linter bans them), so OpenVector cannot observe input while a game or any other app is in front.
/// No hooks, no input injection, no access to other processes or windows.</para>
/// <para><b>Legacy messages.</b> <c>RIDEV_NOLEGACY</c> is not used, so normal mouse messages and window dragging
/// keep working (research C §2.3 item 5).</para>
/// <para><b>Lifetime.</b> One instance at a time per process. <see cref="Start"/> returns once the window exists and
/// registration succeeded (or throws). <see cref="Stop"/>/<see cref="Dispose"/> unregisters, destroys the window and
/// joins the thread.</para>
/// </remarks>
public sealed unsafe class RawInputCapture : IDisposable
{
    private static int s_instanceActive;
    private static volatile bool s_closeRequested;
    private static volatile bool s_visibleActive;
    private static MarkerQueue? s_markers;

    private readonly RawInputCaptureOptions _options;
    private readonly ManualResetEvent _stop = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private Exception? _startError;
    private bool _ownsInstanceFlag;

    // Live counters, written by the capture thread with Volatile.Write.
    private long _wakeups, _batches, _mouse, _absolute, _nonMouse, _malformed, _readErrors, _unbuffered, _allocated, _maxDrainTicks;
    private int _lastError, _maxBatch;

    public RawInputCapture(RawInputCaptureOptions? options = null)
    {
        _options = options ?? new RawInputCaptureOptions();
        if (_options.ReadBufferBytes < 1024) throw new ArgumentOutOfRangeException(nameof(options), "ReadBufferBytes must be >= 1024.");
        Ring = new SpscRingBuffer<RawMouseEvent>(_options.RingCapacity);
    }

    /// <summary>The event stream. The capture thread is the producer; exactly one consumer thread may read it.</summary>
    public SpscRingBuffer<RawMouseEvent> Ring { get; }

    /// <summary>QPC/UTC timebase sampled in <see cref="Start"/>. Event QPC values convert to D14 µs with it.</summary>
    public QpcTimebase Timebase { get; private set; }

    /// <summary>The capture window (message-only or visible). 0 before start.</summary>
    public nint WindowHandle { get; private set; }

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>An unexpected exception that ended the capture loop after a successful start, if any.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Visible mode: the user clicked the window's close button (the window ignores it; the owner should stop).</summary>
    public bool CloseRequested => s_closeRequested;

    /// <summary>Visible mode: whether the capture window is currently the active (foreground) window.</summary>
    public bool VisibleWindowActive => s_visibleActive;

    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("Already started.");
        if (Interlocked.CompareExchange(ref s_instanceActive, 1, 0) != 0)
            throw new InvalidOperationException("Only one RawInputCapture may run per process (raw input allows one target window per device class).");
        _ownsInstanceFlag = true;
        s_closeRequested = false;
        s_visibleActive = false;
        s_markers = _options.Markers;
        Timebase = QpcTimebase.CaptureNow();
        _thread = new Thread(ThreadMain, 256 * 1024)
        {
            IsBackground = true,
            Name = "OpenVector.RawInput",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        _started.Wait();
        if (_startError != null)
        {
            _thread.Join();
            ReleaseInstanceFlag();
            throw new InvalidOperationException("Raw input capture failed to start: " + _startError.Message, _startError);
        }
    }

    public void Stop()
    {
        if (_thread == null) return;
        _stop.Set();
        _thread.Join();
        ReleaseInstanceFlag();
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
        _started.Dispose();
    }

    public CaptureCounters GetCounters() => new()
    {
        Wakeups = Volatile.Read(ref _wakeups),
        Batches = Volatile.Read(ref _batches),
        MousePackets = Volatile.Read(ref _mouse),
        AbsolutePackets = Volatile.Read(ref _absolute),
        NonMouseRecords = Volatile.Read(ref _nonMouse),
        MalformedRecords = Volatile.Read(ref _malformed),
        RingOverflows = Ring.OverflowCount,
        ReadErrors = Volatile.Read(ref _readErrors),
        LastWin32Error = Volatile.Read(ref _lastError),
        UnbufferedMessages = Volatile.Read(ref _unbuffered),
        MaxBatchSize = Volatile.Read(ref _maxBatch),
        MaxDrainMicros = Volatile.Read(ref _maxDrainTicks) * 1e6 / Stopwatch.Frequency,
        CaptureThreadAllocatedBytes = Volatile.Read(ref _allocated),
    };

    private void ReleaseInstanceFlag()
    {
        if (_ownsInstanceFlag)
        {
            s_markers = null;
            _ownsInstanceFlag = false;
            Volatile.Write(ref s_instanceActive, 0);
        }
    }

    private struct RingSink : IRawMouseSink
    {
        public SpscRingBuffer<RawMouseEvent> Ring;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnMouse(in RawMouseEvent e) => Ring.TryWrite(in e); // overflow is counted by the ring
    }

    private const string VisibleClassName = "OpenVectorRawInputCaptureWindow";

    private void ThreadMain()
    {
        nint hwnd = 0;
        bool registered = false, classRegistered = false;
        byte* buffer = null;
        nint hInstance = Native.GetModuleHandle(null);
        try
        {
            if (_options.TimeCriticalPriority)
                Native.SetThreadPriority(Native.GetCurrentThread(), Native.THREAD_PRIORITY_TIME_CRITICAL); // best effort

            if (_options.Window == CaptureWindowKind.MessageOnly)
            {
                // "Message" is the predefined system class for message-only windows (as SDL3 uses).
                fixed (char* cls = "Message")
                    hwnd = Native.CreateWindowEx(0, cls, null, 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, hInstance, 0);
            }
            else
            {
                fixed (char* cls = VisibleClassName)
                fixed (char* title = _options.VisibleWindowTitle)
                {
                    var wc = new Native.WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                        lpfnWndProc = &VisibleWndProc,
                        hInstance = hInstance,
                        hbrBackground = Native.COLOR_WINDOW + 1,
                        lpszClassName = cls,
                    };
                    classRegistered = Native.RegisterClassEx(&wc) != 0;
                    if (!classRegistered) throw Win32("RegisterClassExW");
                    hwnd = Native.CreateWindowEx(0, cls, title, Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE,
                        Native.CW_USEDEFAULT, Native.CW_USEDEFAULT, 720, 420, 0, 0, hInstance, 0);
                }
            }
            if (hwnd == 0) throw Win32("CreateWindowExW");
            WindowHandle = hwnd;

            // Foreground-only registration (D5): dwFlags = 0, target = our own window.
            var rid = new Native.RAWINPUTDEVICE
            {
                usUsagePage = Native.HID_USAGE_PAGE_GENERIC,
                usUsage = Native.HID_USAGE_GENERIC_MOUSE,
                dwFlags = 0,
                hwndTarget = hwnd,
            };
            if (Native.RegisterRawInputDevices(&rid, 1, (uint)sizeof(Native.RAWINPUTDEVICE)) == 0)
                throw Win32("RegisterRawInputDevices");
            registered = true;

            buffer = (byte*)NativeMemory.AlignedAlloc((nuint)_options.ReadBufferBytes, 16);
            _started.Set();
            Loop(buffer, hwnd);
        }
        catch (Exception ex)
        {
            if (!_started.IsSet) { _startError = ex; _started.Set(); }
            else Fault = ex;
        }
        finally
        {
            if (registered)
            {
                var remove = new Native.RAWINPUTDEVICE
                {
                    usUsagePage = Native.HID_USAGE_PAGE_GENERIC,
                    usUsage = Native.HID_USAGE_GENERIC_MOUSE,
                    dwFlags = Native.RIDEV_REMOVE,
                    hwndTarget = 0, // must be NULL with RIDEV_REMOVE
                };
                Native.RegisterRawInputDevices(&remove, 1, (uint)sizeof(Native.RAWINPUTDEVICE));
            }
            if (hwnd != 0) Native.DestroyWindow(hwnd);
            if (classRegistered)
                fixed (char* cls = VisibleClassName) Native.UnregisterClass(cls, hInstance);
            if (buffer != null) NativeMemory.AlignedFree(buffer);
            WindowHandle = 0;
            s_visibleActive = false;
        }
    }

    private void Loop(byte* buffer, nint hwnd)
    {
        RawInputLayout layout = RawInputLayout.ForCurrentProcess(); // GetRawInputBuffer (64-bit layout under WOW64)
        RawInputLayout dataLayout = Environment.Is64BitProcess ? RawInputLayout.Native64 : RawInputLayout.Native32; // GetRawInputData
        uint headerSize = (uint)(Environment.Is64BitProcess ? 24 : 16); // sizeof(RAWINPUTHEADER) for this process
        var sink = new RingSink { Ring = Ring };
        var counters = new ParseCounters();
        ulong sequence = 0;
        uint batchIndex = 0;
        long prevRead = 0;
        nint stopHandle = _stop.SafeWaitHandle.DangerousGetHandle();
        uint wakeMask = _options.Window == CaptureWindowKind.Visible
            ? Native.QS_ALLINPUT
            : Native.QS_RAWINPUT | Native.QS_POSTMESSAGE | Native.QS_SENDMESSAGE;
        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        Native.MSG msg;

        while (true)
        {
            uint r = Native.MsgWaitForMultipleObjectsEx(1, &stopHandle, Native.INFINITE, wakeMask, Native.MWMO_INPUTAVAILABLE);
            if (r == Native.WAIT_OBJECT_0) break; // stop requested
            if (r == Native.WAIT_FAILED)
            {
                Volatile.Write(ref _lastError, Marshal.GetLastPInvokeError());
                Volatile.Write(ref _readErrors, _readErrors + 1);
                break;
            }
            Volatile.Write(ref _wakeups, _wakeups + 1);

            // ---- Drain: QPC, then GetRawInputBuffer, until it reports an empty queue ----
            long wakeQpc = Stopwatch.GetTimestamp();
            long drained = 0;
            while (true)
            {
                long qpc = Stopwatch.GetTimestamp();
                uint size = (uint)_options.ReadBufferBytes;
                uint n = Native.GetRawInputBuffer(buffer, &size, headerSize);
                if (n == uint.MaxValue)
                {
                    int err = Marshal.GetLastPInvokeError();
                    Volatile.Write(ref _lastError, err);
                    Volatile.Write(ref _readErrors, _readErrors + 1);
                    _options.Markers?.Post(MarkerKind.ReadError, (uint)err); // rare path; short lock is fine here
                    break;
                }
                if (n == 0) { prevRead = qpc; break; }
                int emitted = RawInputBufferParser.Parse(new ReadOnlySpan<byte>(buffer, _options.ReadBufferBytes), (int)n,
                    layout, qpc, prevRead, batchIndex++, ref sequence, ref counters, ref sink);
                prevRead = qpc;
                drained += n;
                Volatile.Write(ref _batches, _batches + 1);
                if (emitted > _maxBatch) Volatile.Write(ref _maxBatch, emitted);
            }

            // ---- Safety net: a WM_INPUT the buffered read did not consume would make the wait spin. ----
            // Read it the unbuffered way so no packet is lost (documented mixing pattern).
            while (Native.PeekMessage(&msg, 0, Native.WM_INPUT, Native.WM_INPUT, Native.PM_REMOVE) != 0)
            {
                long qpc = Stopwatch.GetTimestamp();
                uint size = (uint)_options.ReadBufferBytes;
                uint bytes = Native.GetRawInputData(msg.lParam, Native.RID_INPUT, buffer, &size, headerSize);
                if (bytes != uint.MaxValue && bytes > 0)
                {
                    RawInputBufferParser.Parse(new ReadOnlySpan<byte>(buffer, (int)bytes), 1,
                        dataLayout, qpc, prevRead, batchIndex++, ref sequence, ref counters, ref sink);
                    Volatile.Write(ref _batches, _batches + 1);
                }
                prevRead = qpc;
                Volatile.Write(ref _unbuffered, _unbuffered + 1);
                Native.DefWindowProc(msg.hwnd, msg.message, msg.wParam, msg.lParam); // lets the OS release the input handle
            }

            // ---- Pump everything else (never WM_INPUT, which the drain owns) ----
            while (Native.PeekMessage(&msg, 0, 0, Native.WM_INPUT - 1, Native.PM_REMOVE) != 0
                || Native.PeekMessage(&msg, 0, Native.WM_INPUT + 1, 0xFFFF, Native.PM_REMOVE) != 0)
            {
                Native.TranslateMessage(&msg);
                Native.DispatchMessage(&msg);
            }

            long drainTicks = Stopwatch.GetTimestamp() - wakeQpc;
            if (drained > 0 && drainTicks > _maxDrainTicks) Volatile.Write(ref _maxDrainTicks, drainTicks);
            Volatile.Write(ref _mouse, counters.MousePackets);
            Volatile.Write(ref _absolute, counters.AbsolutePackets);
            Volatile.Write(ref _nonMouse, counters.NonMouseRecords);
            Volatile.Write(ref _malformed, counters.MalformedRecords);
            Volatile.Write(ref _allocated, GC.GetAllocatedBytesForCurrentThread() - allocStart);
        }
        Volatile.Write(ref _allocated, GC.GetAllocatedBytesForCurrentThread() - allocStart);
    }

    [UnmanagedCallersOnly]
    private static nint VisibleWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Native.WM_CLOSE:
                s_closeRequested = true; // the owner decides when to stop; don't destroy the capture window here
                return 0;
            case Native.WM_ACTIVATE:
                uint state = (uint)(wParam & 0xFFFF); // WA_INACTIVE = 0, WA_ACTIVE = 1, WA_CLICKACTIVE = 2
                s_visibleActive = state != 0;
                s_markers?.Post(state != 0 ? MarkerKind.FocusGained : MarkerKind.FocusLost, state);
                break;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static InvalidOperationException Win32(string api)
    {
        int err = Marshal.GetLastPInvokeError();
        return new InvalidOperationException($"{api} failed (Win32 error {err}: {Marshal.GetPInvokeErrorMessage(err)}).");
    }
}
