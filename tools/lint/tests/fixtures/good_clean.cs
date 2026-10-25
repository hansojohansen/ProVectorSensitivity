// Fixture: legitimate code that must NOT trip the linter.
using System.Diagnostics;
static class Good
{
    const uint RIDEV_DEVNOTIFY = 0x2000;          // allowed raw-input flag
    static void OnMouseInput() { }                 // ordinary identifier
    static void on_mouse_event_logged() { }        // banned name only inside a longer identifier
    static void MySendInputCounter() { }           // banned name only inside a longer identifier
    static nint Self() => Process.GetCurrentProcess().Handle;  // own process is fine
    static bool OpenProcessTokenForSelf() => true; // token API for own process is a different name
    static void BitBltLike() { }                   // longer identifier
}
