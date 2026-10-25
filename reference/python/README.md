# OpenVector Python reference (dev-only, not shipped)

This is the reference implementation of the calibration model (DECISIONS D7). It is the source of the
**golden vectors** that `OpenVector.Core` must match bit-for-bit, or to 1e-9 for floating-point results.

| Module | What it does | C# counterpart (`src/OpenVector.Core/Analysis`) |
|---|---|---|
| `rng.py` | SplitMix64 PRNG, shared bit-exactly with C# | `SplitMix64` |
| `geometry.py` | D14 angle conventions, sectors, along/across error | `Angles` (`Conventions.cs`) |
| `plan.py` | Balanced trial plan: 32 sectors × amplitudes × reps, continuous angles, shuffled | `TrialPlanGenerator` |
| `segment.py` | Velocity-based submovement segmentation of raw packets | `Segmenter` |
| `metrics.py` | Per-trial metrics; per-direction Fitts throughput (ISO 9241-411) | `TrialMetrics`, `FittsThroughput` |
| `fit.py` | Fourier g(θ) fit: ridge/roughness penalty, CV order selection, sparse-stratum shrinkage, bootstrap CIs, `sample_for_projection` | `GainMapEstimator`, `ProjectionSampler` |
| `sequential.py` | D15 diagnostics: bias after reversal vs same direction, reaction time per direction (reported, never applied) | `SequentialEffects` |
| `synth.py` | Synthetic users: Fourier bias b(θ) up to order 4, range effect, noise, minimum-jerk primary plus corrections, packets at any polling rate | test helper `Synthetic.cs` (endpoint level only) |
| `profile.py` | Assembles a Profile v1 dict | `GainFitResult.ToProfile` |

The model, its derivation and its assumptions are documented at the top of `fit.py`. The segmentation
thresholds are documented at the top of `segment.py`.

## Usage
```bash
pip install numpy scipy pytest jsonschema
pytest reference/python                       # from the repo root
cd reference/python && python3 gen_golden.py  # regenerate golden vectors and schema examples
```
`gen_golden.py` writes these files:
- `tests/OpenVector.Core.Tests/Golden/{plan,segmentation,fit_rich,fit_sparse}.json`
- `schemas/examples/<schema>/example.json`

If you change the reference, regenerate the files and make the C# tests pass again.
`test_golden_vectors_are_current` fails if the committed golden files are out of date.

## Synthetic user model, in brief
- **Primary endpoint:** `S + A·[(1 + b(θ) + r(A) + e_a)·u + e_c·n]`, where `e_a ~ N(0, 0.10)` (along) and
  `e_c ~ N(0, 0.05)` (across). `r(A)` is an optional range effect.
- **Corrections:** up to 3, while the cursor is outside the target. Each carries the same directional bias,
  and there is a 15% chance that it overlaps the previous submovement.
- **True compensating gain:** `1/(1 + b(θ))`.
