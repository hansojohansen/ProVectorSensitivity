# Is OpenVector anticheat-safe? What it does and what it changes

This page explains in plain language what OpenVector does to your PC, what it never does, and how to
undo all of it. The formal rules are in [`DECISIONS.md`](../DECISIONS.md) (D1, D4, D5, D11).

## The short version

OpenVector works like a very precise sensitivity setting.

- It measures how you aim in its own training window.
- It then writes a fixed set of numbers into the settings of **Raw Accel**, a widely used, signed
  mouse driver that you install yourself.
- From then on, the driver multiplies your mouse movement by a fixed amount that depends on the
  direction you move.
- OpenVector itself doesn't need to be running, and nothing changes until you calibrate again and
  click **Apply**.

## What OpenVector does

- **During calibration only**, the trainer reads your raw mouse input. It does this **only inside its
  own window, and only while that window is in the foreground**. The data stays on your PC.
- After you review the result and click **Apply**, it:
  1. backs up your Raw Accel `settings.json`;
  2. changes only the fields OpenVector owns (output DPI, Y/X ratio, left/right and up/down
     multipliers, range weights, lookup table) and keeps your other profiles and device settings;
  3. runs **Raw Accel's own** `writer.exe` once, which sends the settings to the driver.
- If you turn it on, it registers a logon task that re-applies the settings when you sign in to Windows
  (see below).

## What OpenVector never does

- It never opens, reads or writes the memory of a game or any other process.
- It never injects or simulates mouse or keyboard input, and never installs input hooks.
- It never captures or looks at your screen, and never knows where targets are in a game.
- It never checks which games are running and never switches profiles automatically.
- It never runs in the background while you play. No OpenVector process is needed after Apply.
- It never changes your profile on its own. A profile changes only after this sequence: calibration →
  your review → your **Apply** click.
- It ships **no kernel driver**. It uses the official signed Raw Accel driver, unmodified.

These rules are checked automatically. Every code change runs a linter that fails the build if the
code uses Windows functions for memory access, input injection, hooks, screen capture or process
scanning ([`tools/lint/`](../../tools/lint/README.md)).

## Why this is like a DPI or sensitivity setting

- Games read your mouse through Windows raw input. Raw Accel sits just below that and scales each
  movement packet with a **fixed formula**.
- That is the same kind of change as picking a different DPI on your mouse or a different in-game
  sensitivity. The only difference is that the multiplier can differ slightly by direction, for
  example 1.04 for up-left and 0.98 for right.
- It does not know what is on screen or where an enemy is, it does not change during a match, and
  it gives the same output for the same hand movement every time.
- Raw Accel also limits settings changes to one per second in the driver, so it can't be used for
  anything fast or reactive.

## What is changed on your system

| What | Where | Notes |
|---|---|---|
| Raw Accel settings | `settings.json` in your Raw Accel folder | Only OpenVector-owned fields in the selected profile are changed |
| Backups | `%LOCALAPPDATA%\OpenVector\backups\` | A timestamped copy is made before every Apply |
| Driver state | Raw Accel driver, in memory | Lost at reboot unless re-applied |
| Optional logon task | Task Scheduler → `OpenVector\ReapplyRawAccel` | **Off by default.** You turn it on with the "Re-apply profile at Windows sign-in" switch. It runs only Raw Accel's `writer.exe` with your `settings.json` and exits in about a second. It does not start OpenVector. |
| OpenVector data | `%LOCALAPPDATA%\OpenVector\` | Profiles and calibration sessions |

## How to fully revert

You can undo in three ways, from the smallest step to the largest:

1. **Revert** (in the app). Restores the backup taken before the last Apply and re-runs `writer.exe`.
2. **Reset to neutral** (in the app). Writes a neutral profile (your previous output DPI, all ratios
   1, no lookup table), re-runs `writer.exe`, and **removes the logon task**.
3. **Uninstall OpenVector.** The uninstaller always runs Reset to neutral first.

Manual fallback, if the app is gone:
1. Open Task Scheduler and delete `OpenVector\ReapplyRawAccel` if it exists.
2. Copy a backup from `%LOCALAPPDATA%\OpenVector\backups\` over Raw Accel's `settings.json`, or reset
   the settings in the Raw Accel GUI.
3. Click Apply in the Raw Accel GUI, or run `writer.exe settings.json`.

Removing Raw Accel completely is done with Raw Accel's own uninstaller.

## Honest caveat: Raw Accel's acceptance is community-reported

- Raw Accel's own FAQ says it was designed to be anticheat-safe. It is signed through Microsoft,
  applies a constant formula and delays writes by one second.
- It also says Raw Accel has been used in FACEIT, Valorant and other matches. It is widely used with
  Vanguard, FACEIT, EAC and BattlEye games, and we found no credible ban reports.
- **However, we found no official statement from any anticheat vendor that names Raw Accel as
  allowed.** Policies can change.
- OpenVector keeps every property that makes Raw Accel tolerated: a static formula, a single write
  and no running process. But **we cannot guarantee** what a third-party anticheat will do.
- Check your game's or league's rules if you are unsure, especially for LAN tournaments.

---

## <a id="research-driver-warning"></a>⚠ Research driver warning: NOT for games with anticheat

This section is about something different from the normal product.

The repository contains a **research fork of the Raw Accel driver** (`research/rawaccel-angular/`).
It can apply the full direction map, which stock Raw Accel can only approximate. It exists only to
measure whether the full map is worth pursuing.

- **It is test-signed, not officially signed.** Loading it requires Windows **test-signing mode**,
  which usually means turning **Secure Boot off**.
- **Never use it with games that have anticheat.**
  - Vanguard (Valorant, LoL) and FACEIT refuse to run in test-signing mode.
  - Other anticheats may flag or ban it.
  - Use it only with the OpenVector trainer and offline games.
- **BitLocker:** suspend BitLocker **before** changing Secure Boot settings. Otherwise you may be
  asked for your BitLocker recovery key at the next boot. Make sure you have that key.
- **It is never shipped.** The OpenVector installer, app and release pipeline never include or
  reference it, and CI fails the build if they do.
- **Afterwards, go back to normal:**
  1. Uninstall the research driver.
  2. Reinstall the **official signed** Raw Accel driver.
  3. Turn test-signing off (`bcdedit /set testsigning off`, then reboot).
  4. Re-enable Secure Boot, then resume BitLocker.

  The full steps are in the research driver's own guide in `research/rawaccel-angular/`.

If you are not taking part in the research experiment, ignore the research driver completely.
