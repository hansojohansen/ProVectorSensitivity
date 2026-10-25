using System.Globalization;

namespace OpenVector.Capture;

/// <summary>A raw-input mouse device as reported by <c>GetRawInputDeviceList</c>/<c>GetRawInputDeviceInfo</c>.</summary>
/// <param name="Handle">The <c>hDevice</c> handle; matches <see cref="RawMouseEvent.Device"/>.</param>
/// <param name="Name">The device interface path (<c>RIDI_DEVICENAME</c>), e.g. <c>\\?\HID#VID_046D&amp;PID_C547&amp;MI_01&amp;Col01#...</c>.</param>
/// <param name="VendorId">USB/Bluetooth vendor id parsed from <paramref name="Name"/>, or null.</param>
/// <param name="ProductId">Product id parsed from <paramref name="Name"/>, or null.</param>
/// <param name="MouseId">RID_DEVICE_INFO_MOUSE.dwId.</param>
/// <param name="ButtonCount">RID_DEVICE_INFO_MOUSE.dwNumberOfButtons.</param>
/// <param name="SampleRate">RID_DEVICE_INFO_MOUSE.dwSampleRate. Usually 0 or a legacy PS/2 value for USB mice; NOT the polling rate.</param>
/// <param name="HasHorizontalWheel">RID_DEVICE_INFO_MOUSE.fHasHorizontalWheel.</param>
public sealed record MouseDeviceInfo(
    ulong Handle, string Name, ushort? VendorId, ushort? ProductId,
    uint MouseId, uint ButtonCount, uint SampleRate, bool HasHorizontalWheel)
{
    public string VidPid => VendorId is { } v && ProductId is { } p ? $"{v:X4}:{p:X4}" : "unknown";
}

/// <summary>Parses VID/PID out of a Windows device interface path. Platform-neutral.</summary>
public static class DeviceNameParser
{
    /// <summary>
    /// Finds <c>VID_xxxx</c> and <c>PID_xxxx</c> (HID/USB enumerators) or the Bluetooth forms
    /// <c>VID&amp;02xxxx_PID&amp;xxxx</c> / <c>{...}_VID&amp;0002046d_PID&amp;b023</c> in a device path. Case-insensitive.
    /// </summary>
    public static (ushort? Vid, ushort? Pid) ParseVidPid(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return (null, null);
        return (FindHex(deviceName, "VID"), FindHex(deviceName, "PID"));
    }

    private static ushort? FindHex(string s, string key)
    {
        int from = 0;
        while (true)
        {
            int i = s.IndexOf(key, from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int j = i + key.Length;
            if (j < s.Length && (s[j] == '_' || s[j] == '&'))
            {
                j++;
                int start = j;
                while (j < s.Length && Uri.IsHexDigit(s[j]) && j - start < 8) j++;
                int len = j - start;
                // VID_046D (4 digits) or Bluetooth VID&0002046D (8 digits: 4-digit source prefix + id).
                if (len == 4 || len == 8)
                {
                    string hex = s.Substring(start + len - 4, 4);
                    return ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
                // Bluetooth PID&xxxx is followed by '_' or end; anything else -> keep searching.
            }
            from = i + key.Length;
        }
    }
}
