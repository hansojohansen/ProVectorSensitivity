# OpenVector — Decisions Log

Each decision lists its status, reasoning and evidence. Decisions that affect anticheat safety, the driver
or the data model are mandatory entries (per the PM rules). Research reports are in `docs/research/`.

Status values: **Accepted**, **Proposed (needs user approval)**, **Superseded**.

---

## D0. License: MIT — Accepted (user, 2026-10-25)
- MIT, compatible with Raw Accel (MIT) and OpenAimTrainer (Apache-2.0).
- **GPL code is reference only and is never copied.** This covers RefleK's (GPL-3.0), LibreAim and ValTrainer.
- **NVIDIA FPSci (CC BY-NC-SA) is not used.**

## D1. Input layer: stock, officially signed Raw Accel 1.7.x — Accepted
**Decision.** OpenVector applies profiles by writing a Raw Accel `settings.json`. That file is applied by
Raw Accel's own signed `writer.exe` against the official signed driver.
- We ship **no kernel code**.
- We do **not bundle or fork the driver**. The user installs official Raw Accel, and we detect and verify its version.

**Why.**
- Raw Accel is the only open-source, signed, widely anticheat-tolerated mouse filter we found.
- It sits below mouclass, so it affects raw input (`WM_INPUT`), which is what games read. (A §1.1, §5)

**Rejected alternatives.**
- **Interception** (A §4): blocked or unsupported by Vanguard and EAC, and historically FACEIT, so it would get users flagged.
- **Our own KMDF driver:** EV certificate plus attestation, no anticheat goodwill, and it violates "no unsigned kernel code from us".
- **User-mode hooks or SendInput:** produce injected-flag input, are bypassed by raw input, and can trigger anticheat.
- **Windows pointer curves:** do not affect raw input.

**Fallback (user decision).** If true angular support never lands, we keep approximating within stock Raw Accel. No fork and no self-signing.

## D2. ⚠ Raw Accel 1.7 cannot express an arbitrary g(θ). We project onto what it can express. — Accepted, flagged to user
**Finding (A §2, verified against driver source and numerically).**
- With rotation fixed at 0, the reachable family has **5 degrees of freedom**: base gain G0, a vertical shape factor κ (range weights plus a constant LUT), Y/X ratio ρ, left multiplier L and up multiplier U.
- **All four axis gains (→ ↓ ← ↑) can be matched exactly.**
- **The four diagonal gains are tied to one shared shape parameter.**
- **sin 2θ (the "/" vs "\" diagonal asymmetry) is unreachable.**
- **Harmonics ≥ 3 are not independently settable.**
- ρ, L and U also **tilt diagonal output by about 2–5°**.
- On random smooth targets the fit captures about 76% of the bias variance, leaving about 2.5% RMS residual.

**This means part of the brief is not achievable on today's driver:** a full 16–64-bin directional map
cannot be applied exactly. Only its 5-DOF projection can.

**Decision.**
1. The **canonical profile** stays a full-fidelity, smooth g(θ): a Fourier series over angle, with optional
   distance and speed dimensions later. It is what calibration estimates and what the polar plot shows.
2. A separate **driver projection** step compiles g(θ) into Raw Accel parameters using the weighted
   log-space least-squares recipe in A §2.4.
   - It includes a direction-tilt penalty λ.
   - Rotation is fixed at 0, because rotation is a constant aim-direction error.
   - It reports a per-direction residual and a "fraction of your bias we can apply" score.
3. The UI shows **both** curves (measured and applied) and never implies the applied profile is the full map.
4. **A/B validation (D8) always tests the applied projection**, not the ideal map.
5. Calibration analysis reports the empirical **b₂ (sin 2θ) and h ≥ 3 share** across users. That tells us
   whether the gap matters in practice and gives evidence for upstream.

**Known side effect.** At slow speed and high polling rate, per-packet angles are coarsely quantised
(1-count vectors). The κ term is per-packet, so it is only approximately right there. ρ, L and U are exact.
The QA agent will measure this.

**Revision (user, 2026-10-25).** The user judged that a map limited to essentially four axis gains is
too weak to give a real advantage. Measurement therefore targets **full resolution from day one**:
- **Flick directions are drawn from continuous angles,** balanced by stratifying them over 32 sectors.
- **The fit is a Fourier series over angle,** with order up to 8 (about 20° features). It is displayed at 64 bins.
- **The per-bin minimum (D9) becomes a minimum per 32-sector stratum.**

The stock 5-DOF projection stays the safe, shippable default. Whether the full map is worth it is
answered empirically by the research driver (D11).

## D3. Upstream: support a true angular LUT in Raw Accel — Accepted
- Two unreviewed upstream PRs already exist: **#352** (an angle→multiplier LUT with 128 points that keeps
  movement direction, so the closest fit) and #349 (8 sectors).
- We will help get **#352** (or an equivalent) merged: reviews, tests, and a stateless, direction-preserving design.
- The profile format already stores the full g(θ), so moving to a true LUT later needs only a new projection backend. No re-calibration is needed.
- Expectation: **6–18 months or never**. Signed releases are rare, and only the original author's EV certificate can sign them.
- **Phase 1 does not depend on this.**

## D4. Applying, persistence, coexistence and reset — Accepted (user approved the logon task on 2026-10-25, on condition it is easy to toggle)
**Toggle requirement.** The logon task is **off until the user turns it on**. The app's main screen has
an on/off switch for it ("Re-apply profile at Windows sign-in") showing its current state, and
reset-to-neutral and uninstall always remove it. The task is named `OpenVector\ReapplyRawAccel`, so it is
also visible and removable in Task Scheduler.

- **Apply.**
  1. Read the user's existing Raw Accel `settings.json`.
  2. Make a timestamped backup in `%LOCALAPPDATA%\OpenVector\backups\`.
  3. Modify **only the fields we own** (Output DPI, Y/X ratio, L/R, U/D, range weights, LUT) in the
     selected profile, and keep the user's other profiles and device mappings.
  4. Validate the file against our per-version key map. writer.exe silently replaces unknown keys with defaults, so we must refuse untested Raw Accel versions.
  5. Run `writer.exe` with a timeout. On error it shows a modal dialog and exits with code 1.
- **Persistence.** The driver forgets its settings on reboot. That is acceptable because Raw Accel's own
  `writer.exe` re-runs at logon, through a scheduled task that applies the same `settings.json`.
  - **No OpenVector process runs at logon or in game.** The task applies the file and exits in about 1 s.
  - The Raw Accel GUI's "auto write on startup" applies the same file, so the two stay consistent.
  - **⚠ Needs approval:** this is a startup entry. It does not break "no background app in game", but we are spelling it out.
- **Revert.** Restore the backup and re-run writer.exe.
- **Reset to neutral.** Write a known neutral profile (Output DPI = the user's prior value, all ratios 1,
  no LUT) and remove the scheduled task. The **uninstaller always runs reset-to-neutral**.
- **Accel curves.** If the user already uses a Raw Accel accel curve, κ (range weights) would interact with
  it. In that case the projection uses only G0, ρ, L and U, and says so in the UI.
- **Status indicator.** writer.exe cannot read settings back. In v1 the status compares the hash of the
  file we last wrote, the driver version (GET_VERSION IOCTL) and the scheduled-task state. In Phase 2 a
  rebuilt Raw Accel wrapper gives true read-back.
- **Security note.** The Raw Accel control device is writable by any user without admin. That is
  convenient, but QA must document it in the threat model.

## D5. Anticheat-safety guarantees — Accepted
- The **only** thing that affects games is Raw Accel's driver state, written once from a static file.
- OpenVector never opens game processes, reads memory, captures the screen, or reads the state of any other app.
- The **trainer** reads raw input **only in its own window, only while it is in the foreground**, and only during a calibration session.
- **No adaptation outside calibration.** A profile changes only after: a calibration session → user review → an explicit "Apply" click.
- **External trainer adapters (Phase 3)** may only read files the trainer exports, or capture our own raw input while the user plays.
  - **Memory reading is permanently banned**, so tools like Aiming-cookie's approach are excluded.
- QA runs a static check in CI that bans these APIs: `OpenProcess`, `ReadProcessMemory`,
  `WriteProcessMemory`, `CreateRemoteThread`, `SetWindowsHookEx`, `SendInput`, `BitBlt` on foreign windows,
  and DXGI duplication.

## D6. Aim data source: a built-in 3D calibration trainer behind a pluggable `DataSource` — Accepted
**Why (B §1–5).**
- **KovaaK's** exports per-kill and per-shot stats, but **no crosshair or target positions**.
- **Aimlabs** has no SDK. Its replays are undocumented, run at about 60 Hz, and its terms of service are unclear.
- **A hybrid capture-while-playing approach** needs about 2–4× more flicks to detect a 5% bias, works on static flick scenarios only, and its targets are inferred.
- **PureVector itself** runs calibration inside Aimlabs, where target positions are known.

**Decision.**
- **View:** a native 3D view whose yaw/pitch per count and FOV match the user's game.
  - Matching is set by cm/360, or by game sens + DPI.
  - Perspective makes screen position non-linear in angle, so a 2D view is not equivalent.
- **Protocol:** an ISO 9241-411-style multidirectional flick protocol.
  - Directions are balanced across bins, in randomised order.
  - 3 amplitudes, and controlled target sizes.
  - Tracking tasks come second.
- **Every trial is a `MovementTrial` record** (schema in B §7.1, versioned from day one).
  - It stores raw counts plus the mapping, and `target_truth` and `confidence` for its provenance.
- **Planned adapters, Phase 3:**
  - `kovaaks-hybrid` (inferred targets, down-weighted)
  - `osu-replay` (research only)
  - Aimlabs: **out of tree only, after the user reads its EULA.**
- OpenAimTrainer (Apache-2.0, Godot) was evaluated for forking. We **are not forking it** (see D7: own renderer for timing control), but it can serve as a design reference.

## D7. Tech stack: C# / .NET 10 LTS, one solution — Accepted
| Component | Choice | Reason |
|---|---|---|
| Core library (model, profiles, Raw Accel adapter, stats) | C# (.NET 10), MathNet.Numerics, xUnit | Same ecosystem as Raw Accel's GUI and wrapper; testable on Linux CI too |
| Desktop app | Avalonia + ScottPlot 5 polar plots | Raw Accel's next GUI also uses Avalonia; MIT |
| Trainer | Separate C# exe, small D3D11 renderer (Silk.NET or Vortice), **dedicated raw-input thread** | Godot and SDL take over the single per-process raw-input registration and lose per-event timing |
| Raw input | `GetRawInputBuffer` batching on a high-priority thread with a message-only window; QPC per batch; lock-free ring buffer; the camera and the logger read the same stream | Required for 8 kHz (C §2) |
| Model reference | Python (numpy/scipy), **dev-only, not shipped**; generates golden test vectors the C# code must match | Best tools for the science work, no packaging pain |
| Formats | Profile: JSON + JSON Schema (versioned). Trials: JSONL summary + Parquet raw streams | Language-neutral, so external sources can be added |
| Release | GitHub Actions (windows-latest), Velopack installer, SignPath Foundation signing (Azure Artifact Signing as fallback) | Free signing for OSS |

**Fallback.** If the C# capture loop shows unacceptable GC/jitter at 8 kHz (spike S1), move the capture loop into a small native C DLL.

**Raw Accel in-trainer.** Enable `setExtraInfo` so each packet carries the counts both before and after Raw Accel. This lets us check that the applied profile really is in effect during A/B sessions.

## D8. Validation, versioning and rollback — Accepted (implemented in Phase 2)
- **Blind A/B:** interleaved blocks with the old and new *applied projections*, applied through the same writer path. The user doesn't know which one is active.
  - Throughput (Fitts, effective width) is the main metric.
  - Recommend applying only if the improvement is statistically meaningful (pre-registered test, e.g. a paired bootstrap CI excluding 0 with α = 0.05) **and** no direction gets meaningfully worse.
- **Profiles are immutable and versioned.** Each version stores: parent version, calibration session IDs, model parameters + CIs, the projection, the residual and the A/B result.
  - A history list supports one-click rollback.

## D9. Model regularisation defaults (owned by the Calibration agent; PM sets the bounds) — Accepted
- Gain is limited to **±15% from 1.0** in v1 (configurable to ±25%).
- Smoothness: the Fourier order is capped by data, with a penalty on high orders.
- (Amended by D17: 10 per 32-sector stratum.) At least 20 valid trials per bin before a bin influences the fit. Otherwise the fit shrinks towards the
  neighbouring bins, then towards 1.0.
- Bootstrap CIs are reported for every bin and every projected parameter.

## D11. Research driver build (NOT anticheat-safe, never shipped) — Accepted (user, 2026-10-25)
**Purpose.** Measure how much better a full-resolution map is than the stock 5-DOF projection, before we
spend effort on upstream or hardware.

**What it is.** A research fork of Raw Accel at v1.7.1, kept in `research/rawaccel-angular/` (MIT, with
attribution kept). It adds a direction-preserving **angle × speed gain table** to the driver: a periodic
angle axis plus an optional speed axis, interpolated bilinearly. It is modelled on upstream PR #352 and
kept stateless, so it can be proposed upstream.

**Safety rules.**
- It is **test-signed only.** It requires Windows test-signing mode, which usually means turning off Secure
  Boot. Vanguard and FACEIT refuse to run in that mode, and other anticheats may flag it.
- **Use it only with our trainer and offline games.**
- The guide covers:
  - suspending BitLocker before changing Secure Boot;
  - reinstalling the official signed Raw Accel driver afterwards;
  - turning test-signing off again.
- **The installer, app and release pipeline never ship or reference it.**
- The QA linter fails the build if any product project references `research/`.

**Experiment.** Blind, interleaved A/B/C on the same calibration data:
- A = identity;
- B = the stock 5-DOF projection;
- C = the full-resolution map.

The main metric is Fitts throughput; we also look at per-direction error. The results feed the upstream PR evidence (D3) and the hardware decision (D10).

## D12. Speed/distance-dependent gain — Accepted
- **The trainer logs amplitude and peak speed** for every trial. The model gets an optional speed dimension: g(θ, v) is a tensor product of a Fourier series in θ and a spline in log v.
- **Stock Raw Accel projection:** horizontal and vertical speed curves via its "by component" mode.
  Left/right and up/down asymmetry stays constant, not speed-dependent.
- **Research driver:** the full angle × speed table.
- **Default scope:** this is Phase 2 by default. Logging the data is in Phase 1, so no recalibration is needed later.

## D13. User hardware — Recorded
- The test machine is the user's Windows PC with a Logitech G Pro wireless (2nd gen; exact model, DPI and polling rate to be confirmed in G HUB).
- Recent Logitech Lightspeed mice support up to 8 kHz polling, so it is a suitable stress-test device.

## D10. Hardware dongle variant — Parked
This is a closed-source commercial USB pass-through device. It is outside this project's scope; see `CLAUDE.md`.
**Only constraint now:** the profile format must stay hardware-agnostic, in counts and angles, not Raw Accel keys.

## D14. Conventions shared by all modules — Accepted
These conventions apply to every schema and API. Changing any of them goes through the PM.
- **Angle θ** = `atan2(dy, dx)` in raw mouse-count coordinates, matching Raw Input and Raw Accel:
  - dx > 0 is right and dy > 0 is down;
  - 0 = right, π/2 = down, π = left, 3π/2 = up;
  - the range is [0, 2π) in **radians** internally; degrees are for UI only.
- **Gain g(θ)** = |output counts| / |input counts| for a movement in direction θ, relative to the user's
  base sensitivity. Identity is 1.0.
- **Time:** int64 microseconds on a monotonic clock derived from QPC, with a recorded offset to UTC.
- **Interface between Core and RawAccel.** Projection backends take a sampled target
  `(theta[], gain[], weight[])` and do not depend on Core types. Core converts its Fourier model to those samples.

## D15. Log everything raw in calibration sessions, derive later — Accepted (user, 2026-10-25)
The user wants far more data than the current model needs, so that patterns can be found later. Examples
are sequential effects (a flick right followed by a flick left), stimulus-to-movement delay, fatigue and
warm-up. The trial summary (`MovementTrial`) is therefore a **derived view**. The source of truth is a set of
lossless **session streams** recorded **only inside our trainer during a calibration session** (D5).

**Raw input stream: every packet.**
- QPC read time and batch index.
- Device handle, dx/dy, button transitions and wheel/h-wheel.
- Raw flags and `ulExtraInformation`, which holds the pre-Raw Accel counts.
- Packet drop and coalescing markers.

**Frame stream: every frame.**
- Frame id, simulation time, present-call QPC, and DXGI frame statistics (present/vblank QPC).
- Camera yaw/pitch.
- All target states (position, velocity, size, visible).
- The input packets consumed by that frame.

**Event stream.**
- Target spawn requested, first frame on screen (estimated from vblank), hit/miss and click down/up.
- Task phase changes, pauses, focus loss and window changes.

**Session context.**
- Device VID/PID/name, DPI, nominal and measured polling rate.
- Monitor refresh rate and resolution, FOV and deg/count.
- Active Raw Accel settings hash and the active profile id.
- Trainer version and seed, time of day, session duration and break times.
- Optional self-reports: warm-up yes/no, fatigue on a 1–5 scale.

**Per-trial context fields.**
- Previous trial's direction, amplitude and outcome.
- Inter-trial interval and the trial's index in the session and the block.
- Time since the last break.

**Notes and limits.**
- Stimulus-to-photon latency can't be measured without external hardware, such as a photodiode. We log present/vblank times and
  the monitor refresh rate so that the display-side delay is bounded and consistent.
- Streams are compressed and stored locally. Nothing is uploaded.
- The model must stay static outside calibration (constraint 2). Richer data only changes what a *calibration* can fit.
  - Sequential effects, such as previous-direction dependence, **cannot** be applied by a static profile in any case: the driver doesn't know the previous flick's intent.
  - They are research or diagnostic outputs unless a later decision says otherwise.

## D16. Baseline composition and reset semantics — Accepted (PM, from Input/Driver report)
**Baseline composition.**
- The fitted Raw Accel parameters are **multipliers on top of the user's pre-OpenVector settings**: Output DPI, Y/X, L/R, U/D and rotation. We call these the *baseline*.
- The baseline is captured at first apply, and Apply refuses to write if it no longer matches the file.
- Calibration always runs against the **baseline profile**, never against an already-applied OpenVector profile. This means the App asks `ApplyService.GetBaseline()` before a session, and the trainer runs with the baseline active.
- Output DPI is composed as `baseline Output DPI × G0 / c`, which preserves the user's base sensitivity.

**Reset semantics.**
- **"Reset" (default)** restores the baseline, so ratios the user chose themselves are kept.
- **"Full neutral"** (Y/X = L/R = U/D = 1, no LUT) is a separate, explicit option.
- Both remove the logon task.

**Other points.**
- `IProjectionBackend` lives in `OpenVector.RawAccel` for now. Core stays independent (D14), and the App wires the two together.
- If writer.exe fails at sign-in, its error dialog appears at logon. This is acceptable and is documented in the user guide.

## D17. Calibration amendments — Accepted (PM, from Calibration report)
**1. Sample minimum (amends D9).**
- A sector needs **at least 10 valid trials per 32-sector stratum**, which means about 320 flicks or roughly 10 minutes.
- **Sessions accumulate**, as long as they ran under the same baseline and mapping (D16). The full map gets sharper with more sessions.
- Below the minimum, a sector shrinks towards its neighbours as before.
- Reasoning: 20 per stratum needed 640+ flicks before the map moved away from 1.0 at all.

**2. The map is centred by default.**
- The applied g(θ) is normalised so that its geometric mean over directions is 1, which makes it **purely directional**.
- The uniform under- or overshoot (c0) is **reported as a suggestion** ("you undershoot everywhere by X%"), not applied.
  - Slight undershoot in every direction is normal motor strategy (optimised submovements), not a sensitivity error.
  - The user's base sensitivity is a deliberate choice.
- An advanced option can include c0.

**3. Conventions.**
- D14 overrides research B §7.1's "CCW+, screen-up positive" note.
- JSON names that contain digits are pinned with `[JsonPropertyName]`, because the snake_case policy would otherwise mangle them.

**4. Trainer contract** (for wave 2).
- Path packets start at the stimulus.
- The plan's θ is a count-space direction; the trainer converts it with deg/count.
- The trainer calls `Segmenter.Annotate`.
- It uses per-packet reconstructed timestamps.
- It fills `context.previous` and `t_stimulus_on_screen_us`.
