# OpenVector

OpenVector is an open-source (MIT) alternative to direction-dependent sensitivity tools such as
CORSAIR/Aimlabs "PureVector", for **Windows**.

Most people don't aim equally well in every direction. A flick up-left might reliably overshoot
while a flick to the right undershoots. OpenVector does three things about this:

1. It **measures** your direction-dependent aiming bias in a short calibration session.
2. It turns the measurement into a **static, personal gain map g(θ)**: a sensitivity multiplier for
   each direction of mouse movement.
3. It applies that map **system-wide at the input layer**, through the signed
   [Raw Accel](https://github.com/RawAccelOfficial/rawaccel) driver.

After you click **Apply**, OpenVector can be closed. Nothing of ours runs while you play.

> **Status: pre-alpha (Phase 1, in development).** The repository holds the libraries, tests and CI.
> The trainer and desktop app are not usable yet. Do not expect a release build. See
> [`docs/PLAN.md`](docs/PLAN.md).

## How it works

```
calibration session ──► canonical profile g(θ) ──► projection ──► Raw Accel settings.json
 (our 3D trainer,        (Fourier series over        (fit to what      ──► Raw Accel's own signed
  your sens + FOV)        angle, CIs, versioned)      Raw Accel can         writer.exe ──► driver
                                                       express)
```

1. **Calibrate.** A built-in 3D trainer matches your game's cm/360 and FOV. You do multidirectional
   flicks: continuous angles spread evenly over 32 sectors, at 3 distances. The trainer reads raw mouse
   input **only in its own window while it is in the foreground** ([D5](docs/DECISIONS.md), [D6](docs/DECISIONS.md)).
2. **Model.** The analysis fits a smooth gain curve g(θ) with confidence intervals. It is bounded to
   ±15% in v1, and directions with too little data are shrunk towards 1.0 ([D9](docs/DECISIONS.md)).
   That is the **canonical profile**, a hardware-agnostic JSON file.
3. **Apply.** OpenVector projects the profile onto Raw Accel's parameters. It backs up your existing
   Raw Accel `settings.json`, changes only the fields it owns, and runs Raw Accel's own `writer.exe`
   once ([D1](docs/DECISIONS.md), [D4](docs/DECISIONS.md)).
   - As an option, which is **off by default**, a Windows logon task re-runs `writer.exe` at sign-in,
     because the driver forgets its settings on reboot.
4. **Play.** The driver applies a fixed formula to every mouse packet. OpenVector is not running.
   The profile changes only when you calibrate again and click Apply.

### Honest limitation: stock Raw Accel can only approximate the map

Today's signed Raw Accel (1.7.x) **cannot express an arbitrary g(θ)** ([D2](docs/DECISIONS.md)). It can
match the four axis directions (→ ↓ ← ↑) exactly. But all four diagonals share one shape parameter,
and the "/" vs "\\" diagonal asymmetry can't be set at all.

- On realistic bias patterns that captures **about 76%** of the measured bias. About 2.5% RMS is left over.
- The app will always show **both** curves, "measured" and "applied", plus the fraction of your bias
  that could be applied. It never claims to apply the full map.
- We are helping upstream Raw Accel add a true angular lookup table ([D3](docs/DECISIONS.md)). The profile
  format already stores the full map, so no recalibration will be needed when that ships.

To measure whether the rest matters, a **research build** of the driver exists in `research/` ([D11](docs/DECISIONS.md)).
That build is a fork that can apply the full map.
**It is test-signed, is not anticheat-safe, is never shipped, and is only for offline testing.** Read
the warning in [`docs/guide/anticheat-safety.md`](docs/guide/anticheat-safety.md#research-driver-warning)
before going anywhere near it.

## Safety summary

The full explainer is [`docs/guide/anticheat-safety.md`](docs/guide/anticheat-safety.md).

- The only thing that affects games is Raw Accel's driver state. That is a static formula, written once
  from a file, the same kind of change as changing your DPI or in-game sensitivity.
- OpenVector **never** does any of these:
  - opens game processes or reads memory;
  - injects input;
  - installs input hooks;
  - captures the screen;
  - detects running games;
  - changes anything while you play.

  CI enforces this with a linter ([`tools/lint/`](tools/lint/README.md)).
- We ship **no kernel code** and do not bundle a driver. You install official Raw Accel yourself.
- Everything is reversible:
  - **Revert** restores your previous Raw Accel settings.
  - **Reset to neutral** writes an identity profile and removes the logon task.
  - The uninstaller always runs Reset to neutral.
- Raw Accel's acceptance by anticheats is **community-reported**, not officially guaranteed.
  See the explainer.

## Building

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download). Optionally, Python 3.10+ for
the linter and the dev-only model reference.

```sh
dotnet build OpenVector.slnx
dotnet test  OpenVector.slnx          # on Linux, Windows-only test projects can't run; CI runs them on windows-latest
python3 tools/lint/ovlint.py          # constraint linter (must pass)
python3 -m unittest discover -s tools/lint/tests
```

- The core libraries target `net10.0` and build and test on Linux.
- Capture, the trainer and the app target `net10.0-windows`. They compile anywhere, because the build
  sets `EnableWindowsTargeting`, but run only on Windows.
- CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs both a Linux job and a `windows-latest` job.

## Repository layout

```
src/OpenVector.Core/       model, canonical profile, analysis (cross-platform)
src/OpenVector.RawAccel/   Raw Accel projection, settings.json merge/backup, writer.exe runner, logon task
src/OpenVector.Capture/    raw-input capture (Windows)
src/OpenVector.Trainer/    3D calibration trainer (planned)
src/OpenVector.App/        Avalonia desktop app (planned)
tests/                     xUnit tests
tools/lint/                D5/D11 constraint linter
tools/CaptureSpike/        8 kHz capture jitter spike
reference/python/          dev-only model reference + golden vectors (not shipped)
schemas/                   profile and movement-trial JSON Schemas
research/                  research driver fork. NOT part of the product; never referenced by it
docs/                      DECISIONS, ARCHITECTURE, PLAN, THREAT_MODEL, research/, guide/
```

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Design decisions live in [`docs/DECISIONS.md`](docs/DECISIONS.md).
The threat model is [`docs/THREAT_MODEL.md`](docs/THREAT_MODEL.md).

## License

MIT, see [`LICENSE`](LICENSE). For third-party code and the projects we consulted, see
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

OpenVector is not affiliated with CORSAIR, Aimlabs, or the Raw Accel project.
