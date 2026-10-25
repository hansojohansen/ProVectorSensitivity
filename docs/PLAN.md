# OpenVector — Plan

Status values: ✅ done, 🔄 in progress, ⏳ todo, ⛔ blocked.

## Phase 0: Research and decisions — ✅ done (approved by the user on 2026-10-25)
| Task | Owner | Status |
|---|---|---|
| A. Input layer research | Research | ✅ `docs/research/A-input-layer.md` |
| B. Aim data source research | Research | ✅ `docs/research/B-aim-data-source.md` |
| C. Tech stack research | Research | ✅ `docs/research/C-tech-stack.md` |
| DECISIONS.md, ARCHITECTURE.md, PLAN.md | PM | ✅ |
| User approval of D2 (projection plus full-resolution measurement) and D4 (logon task, toggleable) | User | ✅ |

## Phase 1: MVP (full-resolution measurement, stock-Raw Accel apply/revert, research driver)
Definition of done for each task: code, unit tests and a short README section. CI must be green on both Linux (core) and Windows.

**Wave 1** (these tasks run in parallel and touch separate directories):
| # | Task | Owner | Status | Directory |
|---|---|---|---|---|
| 1.1 | Solution skeleton, LICENSE | PM | ✅ | root |
| 1.2 | CI (Linux + windows-latest), D5 constraint linter, no product reference to `research/`, README and anticheat explainer drafts | QA + Docs/Release | ✅ | `.github/`, `tools/lint/`, `README.md`, `docs/guide/` |
| 1.3 | Schemas v1 (profile, movement trial) + C# types; Python reference with synthetic users; C# analysis (segmentation, per-direction metrics, Fitts throughput, Fourier order ≤ 8 fit, regularisation, bootstrap CIs) that recovers injected biases | Calibration | ✅ | `schemas/`, `src/OpenVector.Core/`, `reference/python/`, `tests/OpenVector.Core.Tests/` |
| 1.4 | RawAccel library: 5-DOF projection (A §2.4) + residual and tilt report; settings.json merge with backup; per-version key map; writer.exe runner; revert; reset-to-neutral; logon-task enable/disable/status | Input/Driver | ✅ | `src/OpenVector.RawAccel/`, `tests/OpenVector.RawAccel.Tests/` |
| 1.5 | Research driver: Raw Accel v1.7.1 fork with an angle × speed table, a writer extension, a Windows CI build (test-signed), a safety guide (D11) | Input/Driver (research) | ✅ | `research/rawaccel-angular/` |
| 1.6 | Capture library (`GetRawInputBuffer` thread, QPC, ring buffer, setExtraInfo) + `tools/CaptureSpike` for the user to run (8 kHz jitter, spike S1) | Trainer | ✅ | `src/OpenVector.Capture/`, `tools/CaptureSpike/` |

**Wave 2** (after wave 1 is reviewed):
| # | Task | Owner | Status |
|---|---|---|---|
| 1.7 | Trainer: D3D11 view, cm/360 + FOV matching, flick task (continuous angles stratified over 32 sectors × 3 amplitudes), trial logger | Trainer | ⏳ |
| 1.8 | App: setup wizard, calibration flow, measured-vs-applied polar plot, Apply/Revert/Reset, logon-task toggle, status | Desktop UI | ⏳ |
| 1.9 | Integration tests on Windows: apply → re-apply at logon → revert → reset; behaviour with the app closed | QA | ⏳ |
| 1.10 | Run spike S1 + the research A/B/C experiment on the user's PC | User + PM | ⏳ needs the user's machine |

## Phase 2
Speed/distance dimension in the model and projection (D12); blind A/B validation in the product (D8); profile history and rollback; full polar visualisation; tracking tasks; read-back via a rebuilt Raw Accel wrapper; latency and stress tests; upstream work on Raw Accel PR #352 using research evidence.

## Phase 3
External adapters (kovaaks-hybrid, osu research adapter); profile sharing; installer polish; a true angular LUT backend once a signed Raw Accel release supports it.

## Open risks
1. **Raw Accel expressiveness (D2).** Stock Raw Accel captures about 76% of bias variance, and diagonal "/" vs "\" bias can't be applied. The research driver (D11) measures whether the remainder matters.
2. **Upstream angular LUT** may never ship in a signed release.
3. **Anticheat evidence for Raw Accel** is community-sourced only. There are no official statements.
4. **writer.exe fragility:** silent defaults for unknown keys, modal error dialogs, no read-back.
5. **Competition:** PureVector (firmware, Oct 2026) and FlickTune (closed source, uses Raw Accel) already exist.
6. **Windows-only verification:** this cloud environment is Linux. Capture, trainer and driver work need Windows CI and the user's PC.
7. **Research driver risks for the user:** test-signing and Secure Boot changes, and BitLocker recovery prompts. The guide must be very clear about these.
