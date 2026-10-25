using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenVector.RawAccel.Apply;

public sealed record DriverProbeResult(bool Installed, Version? Version, string? Error = null);

/// <summary>Reads the Raw Accel driver version (GET_VERSION IOCTL). Never writes to the driver.</summary>
public interface IRawAccelDriverProbe
{
    DriverProbeResult GetVersion();
}

/// <summary>Reads a file's version (used for writer.exe).</summary>
public interface IFileVersionReader
{
    Version? GetFileVersion(string path);
}

public sealed class FileVersionReader : IFileVersionReader
{
    public Version? GetFileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return v.FileMajorPart == 0 && v.FileMinorPart == 0 && v.FileBuildPart == 0 ? null
                : new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Opens <c>\\.\rawaccel</c> with access 0 (as common/rawaccel-io.hpp does) and sends
/// GET_VERSION = CTL_CODE(0x8888, 0x88a, METHOD_BUFFERED, FILE_ANY_ACCESS) = 0x88882228,
/// which returns three int32 (major, minor, patch). A failing IOCTL means a driver older than 1.3.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRawAccelDriverProbe : IRawAccelDriverProbe
{
    public const uint IoctlGetVersion = (0x8888u << 16) | (0u << 14) | (0x88au << 2) | 0u;
    public const string DevicePath = @"\\.\rawaccel";

    public DriverProbeResult GetVersion()
    {
        using var h = CreateFileW(DevicePath, 0, 0, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) return new DriverProbeResult(false, null, $"Raw Accel driver not found (CreateFile error {Marshal.GetLastWin32Error()}).");
        var buf = new int[3];
        bool ok = DeviceIoControl(h, IoctlGetVersion, IntPtr.Zero, 0, buf, 12, out _, IntPtr.Zero);
        if (!ok) return new DriverProbeResult(true, new Version(0, 0, 0), "GET_VERSION not implemented (driver older than 1.3).");
        return new DriverProbeResult(true, new Version(buf[0], buf[1], buf[2]));
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuf, int inSize,
        [Out] int[] outBuf, int outSize, out int returned, IntPtr overlapped);
}

/// <summary>Probe used on non-Windows hosts: reports "not installed".</summary>
public sealed class UnavailableRawAccelDriverProbe : IRawAccelDriverProbe
{
    public DriverProbeResult GetVersion() => new(false, null, "Raw Accel is only available on Windows.");
}
