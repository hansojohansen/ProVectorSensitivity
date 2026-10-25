# CaptureSpike (spike S1)

This spike answers one question: can a C# capture loop with no allocations read an 8 kHz mouse on
your PC with acceptable timing jitter? (See research C §2.4 and §7 and decision D7. If the answer is no,
we move the capture loop into a small native DLL.)

The spike reads raw mouse input **only while its own window is the active window**. It does not use
hooks, does not capture in the background, and does not touch other programs or the screen (D5). It
sends nothing over the network. Everything is written to a local folder and zipped so you can send it
back.

## What you need

- Windows 10 or 11, x64.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Run `dotnet --version` to check:
  it should print 10.x.
- In G HUB, set the polling rate you want to test (8000 Hz if your receiver supports it). Write down
  the value and the DPI.
- Close games and other overlays. Normal background apps are fine.

## Run it

From the repository root, in a normal terminal (PowerShell, Windows Terminal or cmd):

```powershell
dotnet run -c Release --project tools/CaptureSpike -- --expected-hz 8000
```

Replace `8000` with the polling rate set in G HUB. If you don't know it, leave out
`-- --expected-hz 8000`. The spike then estimates the rate itself.

By default the spike runs **two phases** of 15 s each, one per capture design:

1. **message-only.** This is the design planned for the trainer: a hidden message-only window on a
   dedicated capture thread, plus a visible window on the main thread.
2. **visible.** This is the fallback: the capture thread owns the visible window itself.

For each phase:

1. Press **Enter** in the terminal. A window titled **"OpenVector CaptureSpike"** opens.
2. **Click inside that window.** It must be the active window, or Windows delivers no input at all.
   The terminal does not count: it belongs to a different process, the console host.
3. Move the mouse in **big, fast, continuous circles** in both directions, plus a few slow ones, until the
   timer runs out. The window title and text show how much time is left. The cursor is confined to the
   window while it is active. This only applies to the window you clicked, and the cursor is released
   when the window is no longer active.
4. The timer only starts once the window is active **and** the first motion arrives.

After each phase the terminal prints:

- the batch-interval histogram;
- packets per second and the estimated polling rate;
- jitter percentiles;
- ring overflows;
- garbage-collection counts and pause time;
- the capture thread's allocated bytes.

At the end it prints the path of a **zip file**. Please send that file back.

```
capture-spike-results/<timestamp>.zip
  <timestamp>/summary.json             all numbers, both phases
  <timestamp>/capture-message-only.ovcap
  <timestamp>/capture-visible.ovcap    raw logs, about 10 MB each at 8 kHz before zipping
```

A phase with **0 packets** is a valid, useful result. It tells us that this capture design does not
receive foreground input on your Windows build. Please send the zip anyway.

## Options

| Option | Meaning |
|---|---|
| `--seconds N` | Seconds to record per phase, counted from the first motion (default 15). |
| `--window message\|visible\|both` | Which phases to run (default `both`). |
| `--expected-hz HZ` | The polling rate you set. The coalescing check compares against it, and the log gets live `CoalescingSuspected` markers. |
| `--out DIR` | Output folder. |
| `--list` | Only list raw-input mouse devices: handle, VID:PID and device path. |
| `--no-clip` | Don't confine the cursor to the window. |
| `--no-prompt` | Don't wait for Enter between phases. |

Pass options after `--` when you use `dotnet run`.

## What the numbers mean

| Field | Meaning |
|---|---|
| read interval | Time between consecutive non-empty `GetRawInputBuffer` reads while the mouse moves. This is the capture loop's real-world wake-up jitter. |
| batch size | Packets returned by one read. At 8 kHz, about 8 per read means roughly 1 ms wakeups. |
| estimated polling rate | The 90th percentile of packet rates over 10 ms windows. The mouse only reports when it moves, so slow motion gives lower rates. |
| coalescing suspected | During fast motion, far fewer packets arrived than the polling rate implies. Possible causes: Windows coalesced input (as it does for background windows), the receiver dropped packets, or the G HUB rate isn't what you think it is. |
| ring overflows | Packets the capture thread read but our in-process queue had no room for. This should be 0. |
| capture thread allocated | Managed bytes allocated by the capture loop. It should be 0, or a tiny constant. |
| GC gen0/1/2, pause | Garbage collections during the phase. Each one briefly pauses all managed threads, including the capture thread. |
| ulExtraInformation / setExtraInfo | Shows whether Raw Accel's `setExtraInfo` option is active. If it is, each packet also carries the counts from before Raw Accel. |

## Log format

The `.ovcap` format is documented in `src/OpenVector.Capture/Logging/CaptureLog.cs`. It has:

- a versioned header with the QPC frequency, origin and UTC offset (D14 clock);
- JSON metadata;
- self-framing typed records: mouse packets and markers for focus, overflow, coalescing, session start
  and end, and, later, trainer frames and events.

It can be read on any OS with `CaptureLogReader`.
