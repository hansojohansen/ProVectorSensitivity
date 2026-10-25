using System.Runtime.InteropServices;

namespace OpenVector.Capture.Windows;

/// <summary>
/// Hand-written, minimal Win32 surface used by the capture thread. Every import is listed here so it can be
/// audited against D5 in one place: raw input for our own window, our own window/message queue, and our own
/// thread's priority. Nothing here touches other processes, other windows, the screen or global input state.
/// All signatures are blittable (no runtime marshalling).
/// </summary>
internal static unsafe partial class Native
{
    // ---- Raw input (winuser.h) ----
    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RIDI_DEVICEINFO = 0x2000000b;
    public const uint RIM_TYPEMOUSE = 0;
    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

    // ---- Messages / waiting ----
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_INPUT = 0x00FF;
    public const uint PM_REMOVE = 0x0001;
    public const uint QS_POSTMESSAGE = 0x0008;
    public const uint QS_SENDMESSAGE = 0x0040;
    public const uint QS_RAWINPUT = 0x0400;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_FAILED = 0xFFFFFFFF;
    public const uint INFINITE = 0xFFFFFFFF;

    // ---- Windows ----
    public static readonly nint HWND_MESSAGE = -3;
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_VISIBLE = 0x10000000;
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);
    public const int COLOR_WINDOW = 5;
    public const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public nint hDevice;
        public uint dwType;
    }

    /// <summary>RID_DEVICE_INFO: cbSize, dwType, then a 24-byte union; only the mouse member is used.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public uint mouse_dwId;
        [FieldOffset(12)] public uint mouse_dwNumberOfButtons;
        [FieldOffset(16)] public uint mouse_dwSampleRate;
        [FieldOffset(20)] public int mouse_fHasHorizontalWheel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
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

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int RegisterRawInputDevices(RAWINPUTDEVICE* pRawInputDevices, uint uiNumDevices, uint cbSize);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetRawInputBuffer(void* pData, uint* pcbSize, uint cbSizeHeader);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetRawInputData(nint hRawInput, uint uiCommand, void* pData, uint* pcbSize, uint cbSizeHeader);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetRawInputDeviceList(RAWINPUTDEVICELIST* pRawInputDeviceList, uint* puiNumDevices, uint cbSize);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    public static partial uint GetRawInputDeviceInfo(nint hDevice, uint uiCommand, void* pData, uint* pcbSize);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    public static partial nint CreateWindowEx(uint dwExStyle, char* lpClassName, char* lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(WNDCLASSEXW* lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true)]
    public static partial int UnregisterClass(char* lpClassName, nint hInstance);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    public static partial int PeekMessage(MSG* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(MSG* lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(MSG* lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint MsgWaitForMultipleObjectsEx(uint nCount, nint* pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial nint GetModuleHandle(char* lpModuleName);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int SetThreadPriority(nint hThread, int nPriority);
}
