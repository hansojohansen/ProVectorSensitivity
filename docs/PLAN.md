# OpenVector — Plan

Status values: ✅ done, 🔄 in progress, ⏳ todo, ⛔ blocked.

## Phase 0: Research and decisions — ✅ done, awaiting user review
| Task | Owner | Status |
|---|---|---|
| A. Input layer research | Research | ✅ `docs/research/A-input-layer.md` |
| B. Aim data source research | Research | ✅ `docs/research/B-aim-data-source.md` |
| C. Tech stack research | Research | ✅ `docs/research/C-tech-stack.md` |
| DECISIONS.md, ARCHITECTURE.md, PLAN.md | PM | ✅ |
| User approval of D2 (projection) and D4 (logon task) | User | ⏳ **blocker for Phase 1** |

## Phase 1: MVP (static 16-bin profile, built-in trainer, apply/revert)
Definition of done for each task: code, unit tests and a short README section. CI must be green on both Linux (core) and Windows.

| # | Task | Owner | Status | Notes |
|---|---|---|---|---|
| 1.1 | Solution skeleton, CI (Linux + windows-latest), MIT LICENSE, constraint linter (D5 banned APIs) | QA + Docs/Release | ⏳ | |
| 1.2 | `profile.schema.json` and `movement-trial.schema.json` v1 + C# types + round-trip tests | Calibration (PM review) | ⏳ | Cross-cutting contract |
| 1.3 | Python reference: synthetic users with injected biases; Fourier fit; golden vectors | Calibration | ⏳ | |
| 1.4 | C# analysis: segmentation, per-direction metrics, Fitts throughput, 16-bin + Fourier fit, regularisation (D9), bootstrap CIs; recovers the injected biases | Calibration | ⏳ | Depends on 1.2, 1.3 |
| 1.5 | RawAccel17 projection backend (A §2.4), residual/tilt report; tests against a port of `modify()` | Input/Driver | ⏳ | Depends on 1.2 |
| 1.6 | RawAccelAdapter: locate the install, version check, key map for 1.7.x, settings.json merge with backup, writer.exe runner, revert, reset-to-neutral, scheduled task | Input/Driver | ⏳ | Needs D4 approval |
| 1.7 | Spike S1: C# vs native capture jitter at 8 kHz; spike S2: writer/IOCTL elevation | Trainer + QA | ⏳ | **Needs a Windows machine with a high-polling mouse (user)** |
| 1.8 | Capture thread + trial logger (JSONL/Parquet) | Trainer | ⏳ | After S1 |
| 1.9 | Trainer: D3D11 view, cm/360 matching, flick task with balanced 16 directions × 3 amplitudes | Trainer | ⏳ | |
| 1.10 | App: setup wizard, calibration flow, simple measured-vs-applied plot, Apply/Revert/Reset, status | Desktop UI | ⏳ | |
| 1.11 | Integration tests: apply → reboot-equivalent re-apply → revert → reset; app-closed behaviour identical | QA | ⏳ | Windows runner + manual |
| 1.12 | README, anticheat explainer (draft), licensing notes (Raw Accel MIT; GPL references not copied) | Docs/Release | ⏳ | |

## Phase 2
32–64 bins; distance and speed dimensions (note that Raw Accel can only express speed through its LUT and range weights); blind A/B validation (D8); profile history and rollback; full polar visualisation; tracking tasks; read-back via a rebuilt Raw Accel wrapper; latency and stress tests; upstream work on Raw Accel PR #352.

## Phase 3
External adapters (kovaaks-hybrid, osu research adapter); profile sharing; installer polish; true angular LUT backend once a signed Raw Accel release supports it.

## Open risks
1. **Raw Accel expressiveness (D2).** About 76% of bias variance is applicable. Diagonal "/" vs "\" bias can't be applied.
2. **Upstream angular LUT** may never ship in a signed release.
3. **Anticheat evidence for Raw Accel** is community-sourced only. There are no official statements.
4. **writer.exe fragility:** silent defaults for unknown keys, modal error dialogs, no read-back.
5. **Competition:** PureVector (firmware, Oct 2026) and FlickTune (closed, uses Raw Accel) already exist.
6. **Windows-only verification:** this cloud environment is Linux. Core and tests run here, but capture, trainer and driver work need Windows CI and the user's machine.
