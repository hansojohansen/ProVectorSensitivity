# Contributing to OpenVector

Thanks for helping! Please read this file before opening a pull request. You should also know
[`docs/DECISIONS.md`](docs/DECISIONS.md), especially D1, D2, D4, D5, D11 and D14.

## Hard constraints (non-negotiable)

A PR that weakens any of these will not be merged. Changing one needs a DECISIONS.md entry that the
project owner has approved.

1. **Anticheat-safe** ([D5](docs/DECISIONS.md)). The product only transforms raw mouse input, using a
   static, precomputed profile applied through stock signed Raw Accel. Never:
   - read or write other processes' memory, or open or inject into them;
   - hook or synthesise input;
   - capture the screen;
   - detect games or react to game state.
2. **Static outside calibration.** A profile changes only when the user finishes a calibration and
   clicks Apply. No automatic or per-game switching.
3. **No background app in game.** The only startup item is the optional, off-by-default logon task
   `OpenVector\ReapplyRawAccel`, which runs Raw Accel's `writer.exe` and exits ([D4](docs/DECISIONS.md)).
4. **Windows only** for the product. The core libraries also build and test on Linux for CI.
5. **Reuse over reinvention.** Raw Accel is the input layer. We ship no kernel code ([D1](docs/DECISIONS.md)).
6. **Research isolation** ([D11](docs/DECISIONS.md)). Nothing outside `research/` and its own workflow may
   reference `research/`. The test-signed research driver is never shipped.
7. **Licensing** ([D0](docs/DECISIONS.md)). MIT. Never copy GPL code (RefleK's, LibreAim, ValTrainer).
   Do not use NVIDIA FPSci.
8. **Hardware-agnostic profile** ([D10](docs/DECISIONS.md)). The canonical profile is expressed in counts
   and angles. Only `OpenVector.RawAccel` knows Raw Accel's JSON keys.

## Module ownership

| Area | Directory | Owner |
|---|---|---|
| Model, analysis, profile schema, Python reference | `src/OpenVector.Core/`, `schemas/`, `reference/python/`, `tests/OpenVector.Core.Tests/` | Calibration/Data Science |
| Raw Accel projection, settings merge/backup, writer.exe, logon task | `src/OpenVector.RawAccel/`, `tests/OpenVector.RawAccel.Tests/` | Input/Driver |
| Research driver | `research/rawaccel-angular/`, `.github/workflows/research-driver.yml` | Input/Driver (research) |
| Raw-input capture, trainer, capture spike | `src/OpenVector.Capture/`, `src/OpenVector.Trainer/`, `tools/CaptureSpike/` | Trainer |
| Desktop app | `src/OpenVector.App/` | Desktop UI |
| CI, constraint linter, integration/latency tests | `.github/workflows/ci.yml`, `tools/lint/`, `tests/OpenVector.Integration/` | QA |
| Docs, licensing, release | `README.md`, `docs/guide/`, `THIRD_PARTY_NOTICES.md`, release pipeline | Docs/Release |
| Decisions, plan, architecture, solution file | `docs/DECISIONS.md`, `docs/PLAN.md`, `docs/ARCHITECTURE.md`, `OpenVector.slnx`, `Directory.Build.props` | PM |

Cross-module interface changes go through the PM and are recorded in DECISIONS.md. These include
schemas, `DataSource`, `IProjectionBackend` and the Core↔RawAccel sample interface.

## Before you open a PR

```sh
dotnet build OpenVector.slnx
dotnet test  OpenVector.slnx
python3 tools/lint/ovlint.py                          # must print "OK"
python3 -m unittest discover -s tools/lint/tests      # if you touched tools/lint
python3 -m pytest reference/python                    # if you touched reference/python
```

- **Tests are required.** Each change ships with unit tests. Model and analysis changes must keep
  matching the Python golden vectors. RawAccel changes need settings-merge and round-trip tests.
- **CI must be green** on both the Linux and the `windows-latest` job.
- Warnings are errors (`TreatWarningsAsErrors`). Nullable reference types are on.

### The constraint linter

`tools/lint/ovlint.py` fails the build on banned APIs (D5) and on references to `research/` (D11).
The rules are in [`tools/lint/README.md`](tools/lint/README.md).

- If you truly need an exception, for example in a test harness, add
  `// openvector-lint-allow: <rule> <reason>` next to the line.
- Every allow comment is **reviewed by the PM**. Exceptions that touch other processes, the screen or
  global input will not be approved.
- Don't name banned APIs in comments. The linter scans comments too.

## Conventions ([D14](docs/DECISIONS.md))

- **Angle θ** = `atan2(dy, dx)` in raw mouse counts.
  - dx > 0 is right and dy > 0 is down.
  - 0 = right, π/2 = down, π = left, 3π/2 = up.
  - Use **radians** in [0, 2π) internally. Degrees are for the UI only.
- **Gain g(θ)** = |output counts| / |input counts| for a movement in direction θ, relative to base
  sensitivity. Identity is **1.0**.
- **Time**: int64 **microseconds** on a monotonic QPC-derived clock, with a recorded offset to UTC.
- **Core ↔ RawAccel**: projection backends take sampled `(theta[], gain[], weight[])` and must not
  depend on Core types.
- **Schemas** are JSON + JSON Schema, versioned with `schema_version` from day one.
- C#: .NET 10, file-scoped namespaces, `OpenVector.<Module>` namespaces. Keep Windows-only code in
  `net10.0-windows` projects. `OpenVector.Core` stays cross-platform.

## Commits and PRs

- Keep PRs focused on one module where possible, and describe user-visible effects.
- Anything that changes what is written to the user's system (the files, the logon task, the Raw Accel
  fields we own) must update [`docs/guide/anticheat-safety.md`](docs/guide/anticheat-safety.md) and
  [`docs/THREAT_MODEL.md`](docs/THREAT_MODEL.md) in the same PR.
- By contributing you agree your contribution is licensed under the MIT License.
