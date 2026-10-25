using System.Runtime.InteropServices;

namespace OpenVector.Capture.Windows;

/// <summary>Lists raw-input mouse devices (<c>GetRawInputDeviceList</c> + <c>GetRawInputDeviceInfoW</c>).</summary>
/// <remarks>
/// This only queries the OS device list (the same list any app with raw input sees); it does not open devices.
/// One physical mouse often appears as several entries (e.g. a Lightspeed receiver exposes several HID
/// collections); <see cref="RawMouseEvent.Device"/> tells which one actually produced motion.
/// </remarks>
public static unsafe class RawInputDeviceEnumerator
{
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    public static IReadOnlyList<MouseDeviceInfo> GetMice()
    {
        uint itemSize = (uint)sizeof(Native.RAWINPUTDEVICELIST);
        Native.RAWINPUTDEVICELIST[] list;
        uint count = 0;
        while (true)
        {
            if (Native.GetRawInputDeviceList(null, &count, itemSize) == uint.MaxValue)
                throw new InvalidOperationException($"GetRawInputDeviceList failed (Win32 error {Marshal.GetLastPInvokeError()}).");
            list = new Native.RAWINPUTDEVICELIST[Math.Max(count, 1)];
            uint got;
            fixed (Native.RAWINPUTDEVICELIST* p = list) got = Native.GetRawInputDeviceList(p, &count, itemSize);
            if (got != uint.MaxValue) { count = got; break; }
            if (Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER) // a device was added in between: retry
                throw new InvalidOperationException($"GetRawInputDeviceList failed (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        var mice = new List<MouseDeviceInfo>();
        for (int i = 0; i < count; i++)
        {
            if (list[i].dwType != Native.RIM_TYPEMOUSE) continue;
            nint h = list[i].hDevice;
            string name = GetName(h);
            var info = new Native.RID_DEVICE_INFO { cbSize = (uint)sizeof(Native.RID_DEVICE_INFO) };
            uint size = info.cbSize;
            bool ok = Native.GetRawInputDeviceInfo(h, Native.RIDI_DEVICEINFO, &info, &size) != uint.MaxValue;
            var (vid, pid) = DeviceNameParser.ParseVidPid(name);
            mice.Add(new MouseDeviceInfo(
                (ulong)h, name, vid, pid,
                ok ? info.mouse_dwId : 0, ok ? info.mouse_dwNumberOfButtons : 0,
                ok ? info.mouse_dwSampleRate : 0, ok && info.mouse_fHasHorizontalWheel != 0));
        }
        return mice;
    }

    private static string GetName(nint h)
    {
        uint chars = 0;
        Native.GetRawInputDeviceInfo(h, Native.RIDI_DEVICENAME, null, &chars); // size in characters
        if (chars == 0) return "";
        var buf = new char[chars + 1];
        uint res;
        fixed (char* p = buf) res = Native.GetRawInputDeviceInfo(h, Native.RIDI_DEVICENAME, p, &chars);
        if (res == uint.MaxValue || res == 0) return "";
        return new string(buf, 0, (int)Math.Min(res, (uint)buf.Length)).TrimEnd('\0');
    }
}
