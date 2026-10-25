// Fixture: malformed and stale allow comments.
static class BadAllow
{
    static extern uint SendInput(uint n); // openvector-lint-allow: input-injection
    static extern bool BitBlt(); // openvector-lint-allow: no-such-rule because reasons are here
    // openvector-lint-allow: process-open this line suppresses nothing at all
    static void Fine() { }
    // openvector-lint-allow: screen-capture wrong rule for the next line
    static void Wrong() => keybd_event();
}
