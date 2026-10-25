// Fixture: every banned API once. Never compiled.
using System.Diagnostics;
using System.Runtime.InteropServices;
static class Bad
{
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint a, bool b, uint pid);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(nint h, nint a, byte[] b, int n, out int r);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(nint h, nint a, byte[] b, int n, out int r);
    [DllImport("kernel32.dll")] static extern nint CreateRemoteThread(nint h, nint a, uint s, nint f, nint p, uint c, out uint id);
    [DllImport("kernel32.dll")] static extern nint VirtualAllocEx(nint h, nint a, nuint s, uint t, uint p);
    [DllImport("ntdll.dll")] static extern int NtReadVirtualMemory(nint h, nint a, byte[] b, int n, out int r);
    [DllImport("user32.dll")] static extern nint SetWindowsHookExW(int id, nint fn, nint mod, uint tid);
    [DllImport("user32.dll", EntryPoint = "SendInput")] static extern uint Inject(uint n, nint p, int s);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint d, nint e);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte sc, uint f, nint e);
    const uint RIDEV_INPUTSINK = 0x100;
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint d, int x, int y, int w, int h, nint s, int sx, int sy, uint op);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    static void Capture(System.Drawing.Graphics g) => g.CopyFromScreen(0, 0, 0, 0, default);
    static void Dup(IDXGIOutput1 o) { IDXGIOutputDuplication d = o.DuplicateOutput(null); }
    static bool GameRunning() => Process.GetProcessesByName("VALORANT").Length > 0;
    static int Count() => Process.GetProcesses().Length;
}
