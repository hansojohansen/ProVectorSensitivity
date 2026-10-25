# OpenVector — Architecture (v0, Phase 0)

See `DECISIONS.md` for the reasoning. This doc describes the components, data flow and boundaries.

```
 ┌──────────────── Calibration time (user-initiated only) ────────────────┐
 │                                                                         │
 │  OpenVector.Trainer (exe)              OpenVector.App (Avalonia)        │
 │  ├─ RawInputCapture thread  ──ring──►  ├─ Setup wizard (DPI, game sens, │
 │  │  (GetRawInputBuffer, QPC)           │   polling, Raw Accel folder)   │
 │  ├─ D3D11 renderer (camera = counts    ├─ Calibration flow / A/B flow   │
 │  │  × deg/count, matched FOV)          ├─ Polar plots (measured vs      │
 │  ├─ Task engine (flick, tracking)      │   applied, CIs, residual)      │
 │  └─ Trial logger ──► MovementTrial     ├─ Profile mgmt + history        │
 │                      JSONL + Parquet   └─ Apply / Revert / Reset + status│
 │                           │                        ▲                    │
 │                           ▼                        │                    │
 │                 OpenVector.Core (library)          │                    │
 │                 ├─ Data: MovementTrial schema, DataSource interface     │
 │                 ├─ Analysis: segmentation, per-direction metrics,       │
 │                 │   Fitts throughput, Fourier g(θ) fit, bootstrap CIs   │
 │                 ├─ Profile: versioned canonical g(θ) (JSON + Schema)    │
 │                 ├─ Projection backends: RawAccel17 (5-DOF), future LUT  │
 │                 └─ RawAccelAdapter: settings.json merge, backup,        │
 │                     version key map, writer.exe runner, sched. task     │
 └────────────────────────────────────────────┬────────────────────────────┘
                                              │ writes settings.json once,
                                              │ runs Raw Accel writer.exe
 ┌──────────────── Game time (OpenVector closed) ──────────────────────────┐
 │  Mouse ─► Raw Accel signed filter driver (static settings) ─► mouclass  │
 │        ─► raw input / cursor ─► any game                                │
 │  At logon: scheduled task runs Raw Accel's writer.exe settings.json,    │
 │  then exits.                                                             │
 └─────────────────────────────────────────────────────────────────────────┘
```

## Repository layout (planned)
```
src/OpenVector.Core/          model, profile, projection, Raw Accel adapter (no Windows-only deps except adapter)
src/OpenVector.Capture/       raw-input capture (Windows); native fallback in native/capture/ if needed
src/OpenVector.Trainer/       renderer + tasks
src/OpenVector.App/           Avalonia UI
tests/OpenVector.Core.Tests/  xUnit: synthetic users, projection, settings merge, schema
tests/OpenVector.Integration/ Windows-only: apply/revert/reset, latency, stress
reference/python/             dev-only model reference + golden vectors
schemas/                      profile.schema.json, movement-trial.schema.json
docs/                         DECISIONS, ARCHITECTURE, PLAN, research/, user guide, anticheat explainer
```

## Key data contracts
- **Profile v1 (canonical, hardware-agnostic).** Contains:
  - `schema_version`, `id`, `parent_id`, `created_at`
  - `mapping` (DPI, deg/count at calibration)
  - `model: {type: "fourier", order, coeffs, cov}`
  - `bins: [{theta, gain, ci_low, ci_high, n}]`
  - `bounds`, `sessions[]`
  - `projections: {rawaccel-1.7: {params, residual_rms, explained_var, max_tilt_deg}}`
  - `validation` (the A/B result)
- **MovementTrial v1.** Follows research B §7.1. It stores raw counts plus the mapping and always carries provenance and confidence.
- **Ownership rule.** Only `RawAccelAdapter` knows Raw Accel's JSON keys. Everything else speaks profile v1.

## Module ownership (subagents)
| Module | Owner |
|---|---|
| RawAccelAdapter, projection backends, apply/revert/reset, scheduled task | Input/Driver |
| Capture, Trainer | Trainer |
| Analysis, model, Profile schema, Python reference | Calibration/Data Science |
| App | Desktop UI |
| tests/Integration, CI constraint linter, latency/stress | QA |
| docs, licensing, release pipeline | Docs/Release |

Cross-module interface changes (schemas, the `DataSource` and `IProjectionBackend` interfaces) go through the PM and are recorded in DECISIONS.md.
