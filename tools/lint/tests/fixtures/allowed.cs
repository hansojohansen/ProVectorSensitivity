// Fixture: allow comments, same-line and preceding-line (stacked) forms.
static class Allowed
{
    static extern uint SendInput(uint n, nint p, int s); // openvector-lint-allow: input-injection test harness only, drives our own trainer window
    // openvector-lint-allow: screen-capture integration test screenshots our own window only
    static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    // openvector-lint-allow: process-open needed to test own child process handle
    // openvector-lint-allow: process-memory reads our own child process memory in a test
    static void Both() { OpenProcess(); ReadProcessMemory(); }
}
