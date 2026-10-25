# OpenVector.RawAccel

The input-layer adapter for stock, signed **Raw Accel 1.7.x** (D1, D2, D4). It has no dependency on
OpenVector.Core (D14). Everything Raw Accel-specific lives here. That covers the JSON keys, the driver
math, writer.exe, and the logon task.

Upstream reference: `RawAccelOfficial/rawaccel` at commit `53a7213` (release v1.7.1). Its internal
version string is `"1.7.0"`, and that string appears both in `settings.json` and in the driver's
GET_VERSION reply.

## What it contains

| Area | Types |
|---|---|
| Driver math | `RawAccelModel` is a port of `modifier::modify` (rotation, snap, speed cap, whole/by-component, noaccel + LUT, output DPI, Y/X, L/R, U/D). `RawAccelProfile` and `AccelCurve` describe the settings it evaluates. |
| Projection | `RawAccel17Projection` (backend id `rawaccel-1.7`), plus `ProjectionTarget`, `ProjectionOptions`, `ProjectionBaseline`, `DirectionalParams`, `ProjectionResult`, `IProjectionBackend` |
| settings.json | `RawAccelSettingsDocument` (parse / merge / write), `RawAccelKeyMap` (keys for each tested version), `RawAccelSettingsValidator`, `OwnedProfileValues`, `RawAccelParameterMapping`, `NeutralProfile`, `BackupStore` |
| Applying | `WriterRunner`, `RawAccelLogonTask`, `IRawAccelDriverProbe` / `WindowsRawAccelDriverProbe`, `IProcessRunner` / `ProcessRunner`, `ApplyService`, `ApplyStateStore` |

## What OpenVector changes on the system

Changes happen only when the user clicks Apply, Revert, Reset, or the logon toggle, or when the app is
uninstalled.

1. **The user's Raw Accel `settings.json`** (`<Raw Accel folder>\settings.json`). This is the same file
   the Raw Accel GUI uses.
   - OpenVector edits only these fields, and only in **one selected profile** (the first profile by default):
     - `Output DPI`
     - `Y/X output DPI ratio (vertical sens multiplier)`
     - `L/R output DPI ratio (left sens multiplier)`
     - `U/D output DPI ratio (up sens multiplier)`
     - `Stretches accel range for horizontal vs vertical inputs` (`x`, `y`)
   - Two more parts change **only when the diagonal shape κ is used**, which requires the profile to have no accel curve:
     - in `Whole or horizontal accel parameters`: the `mode` (set to `"lut"`), `Gain / Velocity` (set to `false`) and `data` (set to `[1, c, 2, c]`) keys;
     - `Whole/combined accel …` (set to `true`).
   - Everything else is preserved: other profiles, device mappings, unknown keys and key order. A no-op
     merge reproduces the file byte for byte (this is tested).
   - Writes are atomic: a temp file in the same folder, then a rename.
   - If writer.exe fails during Apply, the file is restored to its previous bytes.
2. **Backups.** Before every write, the file is copied to `%LOCALAPPDATA%\OpenVector\backups\settings-<UTC timestamp>.json`.
   - The folder is configurable.
   - The newest 200 backups are kept.
   - Backups are never deleted on uninstall.
3. **The state file** `%LOCALAPPDATA%\OpenVector\rawaccel-state.json` records:
   - the user's pre-OpenVector values of the owned fields (the "baseline");
   - the values last applied;
   - the SHA-256 of the file last written;
   - the backup to revert to.
4. **The Raw Accel driver state.** OpenVector never talks to the driver to write settings. It runs
   **Raw Accel's own `writer.exe <settings.json>`**, which validates the file and sends it with the
   WRITE IOCTL. The driver sleeps about 1 s on each write.
   - OpenVector itself only sends the read-only **GET_VERSION IOCTL (0x88882228) to `\\.\rawaccel`**.
   - The driver keeps settings until reboot.
5. **The scheduled task `\OpenVector\ReapplyRawAccel`.** It is **off by default** and only created when
   the user turns on "Re-apply profile at Windows sign-in".
   - It is created by `schtasks /Create /XML`.
   - It uses a logon trigger for the current user with a 5 s delay, `InteractiveToken`, `LeastPrivilege` and a 1-minute time limit.
   - Its action is `"<Raw Accel>\writer.exe" "<Raw Accel>\settings.json"`. **No OpenVector process runs at logon or in game.**
   - Reset to neutral and uninstall always delete it.

## What it never does
- Ships, bundles or modifies the Raw Accel driver, writer.exe or wrapper.dll.
- Writes driver settings directly. Only writer.exe writes them.
- Opens, reads or inspects any other process, window or game (D5). The only processes it starts are
  `writer.exe` and `schtasks.exe`.
- Changes settings outside an explicit user action. Nothing runs in the background.
- Accepts Raw Accel versions other than the tested ones. These are settings `"version": "1.7.0"`,
  driver 1.7.0 and writer.exe 1.7.x. writer.exe fills unknown or renamed keys with defaults without
  any warning, so an untested version could silently change a user's settings.

## Operations (`ApplyService`)
- **Preflight** checks the following:
  - the folder exists and contains `writer.exe` (`wrapper.dll` is checked as a warning);
  - writer.exe's file version is 1.7.x;
  - the driver is installed with version 1.7.0;
  - `settings.json` exists, has a supported version, and the user's current file validates.
- **GetBaseline** chooses between two baselines:
  - the recorded pre-OpenVector values, if the file still holds what OpenVector last applied;
  - otherwise, the current values.

  Pass `BaselineInfo.Projection` to `RawAccel17Projection.Project`. All parameters are multipliers on
  top of the baseline, so the user's own Y/X, L/R, U/D, rotation and Output DPI are kept.
- **Apply** runs: preflight → baseline check (it refuses a projection computed against a stale baseline)
  → compose → merge → validate → backup → atomic write → writer.exe → record the state and hash.
- **Revert** restores the backup taken before the last Apply (or a backup you choose) and runs writer.exe.
- **ResetToNeutral** (also used on uninstall) removes the logon task and writes neutral values.
  - **Identity** mode (D4): Output DPI goes back to the user's prior value, Y/X, L/R and U/D become 1,
    and OpenVector's LUT is removed. A user's own accel curve is never touched.
  - **RestoreBaseline** mode puts back the exact pre-OpenVector owned values.
  - If writer.exe cannot run, the neutral file is still kept.
- **Status** reports:
  - whether the file's hash still equals OpenVector's last write;
  - the driver version;
  - the logon task state.

  writer.exe cannot read settings back, so this is not true read-back.

## writer.exe behaviour
| Result | Meaning |
|---|---|
| exit 0 | Success (writer.exe is silent). |
| exit 1 | Error. A MessageBox was shown and dismissed. We cannot capture its text. |
| no exit within 20 s | Assumed to be a modal error dialog waiting for the user. The process tree is killed. |

## Projection (`rawaccel-1.7`)
The fit implements A §2.4:
- it is a weighted log-space least-squares fit;
- it adds a direction-tilt penalty λ (default 1 rad⁻¹);
- rotation is fixed at the user's own value;
- the solver is a bounded Levenberg–Marquardt (ρ, L, U in [0.5, 2], κ in [0.5, 2]).

**Mapping to Raw Accel settings (κ ≠ 1):**
- a constant LUT with c = max(2, 1/κ + 0.01), `gain=false`;
- range weights rx = 1 and ry = (κc − 1)/(c − 1);
- `Output DPI = base × G0 / c`.

**If the profile has an accel curve**, only G0, ρ, L and U are fitted (`AxisOnly`), and a warning is returned.

**Reported metrics:**
- the residual curve and the tilt per sample;
- RMS and maximum relative residual;
- the bias RMS (deviation from the best constant gain);
- explained variance (log gain);
- **FractionOfBiasApplied** = 1 − RMS_residual / RMS_bias;
- the maximum tilt in degrees.

Angle snapping and smoothing are not modelled. A warning is returned when they are active.

## Verified vs. assumed
- **Verified against source** (wrapper.cpp, rawaccel-base.hpp, rawaccel.hpp, accel-lookup.hpp,
  rawaccel-validate.hpp, rawaccel-io(-def).hpp, writer/Program.cs, grapher SettingsManager.cs):
  - every JSON key and enum string;
  - the defaults;
  - the validation limits;
  - the IOCTL code;
  - the writer exit and dialog behaviour;
  - the GUI using `settings.json` in its own folder.
- **Verified numerically.** The model matches `docs/research/scripts/fit_rawaccel.py` to 1e-12. The fit
  matches scipy `least_squares` to 2e-3 in each parameter. The golden vectors are in
  `tests/OpenVector.RawAccel.Tests/Golden/` and are regenerated with `generate_golden.py`.
- **Assumed, needs a Windows test (QA):**
  - the test fixture is a hand-built file in writer.exe's layout, not one captured from a real install;
  - a standard (non-admin) user can create a task in the `\OpenVector\` folder;
  - `schtasks /Query /XML` output parses as expected;
  - writer.exe's FileVersion reads as 1.7.0;
  - GET_VERSION works without elevation.
