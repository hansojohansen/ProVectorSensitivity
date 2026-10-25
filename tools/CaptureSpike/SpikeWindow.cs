using System.Runtime.InteropServices;
using OpenVector.Capture.Logging;

namespace OpenVector.Tools.CaptureSpike;

/// <summary>
/// The small visible window the user clicks so that this process owns the foreground window. Raw input registered
/// without the background-sink flag is only delivered while our process is in the foreground (D5), and the console
/// window belongs to the console host process (conhost / Windows Terminal), not to us, so the console itself is
/// not enough.
/// </summary>
/// <remarks>
/// Lives on the main thread (message-only capture mode). Paints instructions, posts focus markers to the log on
/// WM_ACTIVATE and confines the cursor to itself while active (ClipCursor, research C §2.3 item 5) so circles
/// cannot wander onto another window. Only ever touches its own window.
/// </remarks>
internal static unsafe partial class SpikeWindow
{
    private const string ClassName = "OpenVectorCaptureSpikeWindow";
    private const uint WM_DESTROY = 0x0002, WM_ACTIVATE = 0x0006, WM_PAINT = 0x000F, WM_CLOSE = 0x0010;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const uint DT_WORDBREAK = 0x0010, DT_NOPREFIX = 0x0800;
    private const uint PM_REMOVE = 1, QS_ALLINPUT = 0x04FF, MWMO_INPUTAVAILABLE = 0x0004;
    private const nint IDC_CROSS = 32515;

    private static nint s_hwnd;
    private static string s_text = "";
    private static volatile bool s_active;
    private static volatile bool s_closeRequested;
    private static MarkerQueue? s_markers;
    private static bool s_clip = true;

    public static bool IsActive => s_active;
    public static bool CloseRequested => s_closeRequested;
    public static nint Handle => s_hwnd;

    public static void Create(string title, MarkerQueue markers, bool clipCursor)
    {
        s_markers = markers;
        s_clip = clipCursor;
        s_closeRequested = false;
        s_active = false;
        nint hInstance = GetModuleHandleW(null);
        fixed (char* cls = ClassName)
        fixed (char* t = title)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                hCursor = LoadCursorW(0, IDC_CROSS),
                hbrBackground = 5 + 1, // COLOR_WINDOW + 1
                lpszClassName = cls,
            };
            RegisterClassExW(&wc); // may already be registered from a previous phase; CreateWindow will tell
            s_hwnd = CreateWindowExW(0, cls, t, WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                CW_USEDEFAULT, CW_USEDEFAULT, 760, 460, 0, 0, hInstance, 0);
        }
        if (s_hwnd == 0) throw new InvalidOperationException($"CreateWindowExW failed ({Marshal.GetLastPInvokeError()}).");
        SetForegroundWindow(s_hwnd); // best effort; Windows may refuse, the user then clicks the window
    }

    public static void SetText(string title, string body)
    {
        if (s_hwnd == 0) return;
        s_text = body;
        fixed (char* t = title) SetWindowTextW(s_hwnd, t);
        InvalidateRect(s_hwnd, null, 1);
    }

    /// <summary>Processes pending messages for this thread, waiting at most <paramref name="timeoutMs"/>.</summary>
    public static void Pump(uint timeoutMs)
    {
        MsgWaitForMultipleObjectsEx(0, null, timeoutMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        MSG msg;
        while (PeekMessageW(&msg, 0, 0, 0, PM_REMOVE) != 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    public static void Destroy()
    {
        ClipCursor(null);
        if (s_hwnd != 0) DestroyWindow(s_hwnd);
        s_hwnd = 0;
        Pump(0);
        s_markers = null;
    }

    /// <summary>Confines the cursor to <paramref name="hwnd"/> (one of our own windows), or releases it.</summary>
    public static void Clip(nint hwnd, bool on)
    {
        if (on && hwnd != 0 && GetWindowRect(hwnd, out RECT r) != 0) ClipCursor(&r);
        else ClipCursor(null);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_ACTIVATE:
                uint state = (uint)(wParam & 0xFFFF);
                s_active = state != 0;
                s_markers?.Post(state != 0 ? MarkerKind.FocusGained : MarkerKind.FocusLost, state);
                if (s_clip) Clip(hwnd, s_active);
                break;
            case WM_PAINT:
            {
                PAINTSTRUCT ps;
                nint hdc = BeginPaint(hwnd, &ps);
                GetClientRect(hwnd, out RECT rc);
                rc.left += 16; rc.top += 16; rc.right -= 16; rc.bottom -= 16;
                fixed (char* p = s_text) DrawTextW(hdc, p, s_text.Length, &rc, DT_WORDBREAK | DT_NOPREFIX);
                EndPaint(hwnd, &ps);
                return 0;
            }
            case WM_CLOSE:
                s_closeRequested = true; // main loop ends the phase early and destroys the window
                return 0;
            case WM_DESTROY:
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---------------- P/Invoke (own window only) ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("kernel32.dll")] private static partial nint GetModuleHandleW(char* name);
    [LibraryImport("user32.dll")] private static partial nint LoadCursorW(nint hInstance, nint name);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* cls, char* title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint hInstance, nint param);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int SetWindowTextW(nint hwnd, char* text);
    [LibraryImport("user32.dll")] private static partial int InvalidateRect(nint hwnd, RECT* rect, int erase);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial int PeekMessageW(MSG* msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(MSG* msg);
    [LibraryImport("user32.dll")]
    private static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint ms, uint mask, uint flags);
    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] private static partial int EndPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] private static partial int GetClientRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] private static partial int GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] private static partial int DrawTextW(nint hdc, char* text, int len, RECT* rect, uint format);
    [LibraryImport("user32.dll")] private static partial int ClipCursor(RECT* rect);
}
