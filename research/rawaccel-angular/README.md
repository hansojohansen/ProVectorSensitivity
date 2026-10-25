# rawaccel-angular: RESEARCH ONLY

> **⚠ RESEARCH ONLY. This is not part of OpenVector and not an official Raw Accel build.**
> It is an unofficial fork of Raw Accel v1.7.1 that is only ever **test-signed**. It loads only when
> Windows test-signing mode is on, which usually means turning Secure Boot off.
> **Never** use it, or test-signing mode, with anticheat-protected games (Valorant/Vanguard, FACEIT,
> ESEA, EAC, BattlEye, Ricochet, EA Javelin, ...). The OpenVector app, installer and release pipeline
> never ship or reference this folder (D11; `tools/lint/ovlint.py` enforces it).
> To use it, follow **[GUIDE.md](GUIDE.md)**, including the rollback in section F.

## Why this exists

The signed Raw Accel 1.7 driver can only express a 5-degree-of-freedom approximation of a directional
gain map g(θ) (see `docs/research/A-input-layer.md` §2 and D2). This fork adds a full-resolution
**angle × speed gain table** to the driver, so that we can measure on a real PC how much better the
full map is than the stock 5-DOF projection. The experiment is a blind A/B/C test (D11):
A = identity, B = stock projection, C = full map. The answer feeds the upstream PR (D3) and the
hardware decision (D10).

## What is in this folder

| Path | What |
|---|---|
| `rawaccel/` | Vendored Raw Accel source at upstream commit [`53a7213`](https://github.com/RawAccelOfficial/rawaccel/tree/53a721345617a1e29f3a16750cbdf807040cf44e) (v1.7.1, MIT, `rawaccel/LICENSE`), with our changes. Only the parts needed to build the driver, installer, uninstaller, wrapper and writer are included. The GUI (`grapher/`), docs and the official signed binaries (`signed/`) are left out. |
| `patches/0001-openvector-angular-gain.patch` | The complete diff from upstream to `rawaccel/`, for review and upstreaming. `scripts/make-patch.sh --check` verifies it against a fresh upstream checkout. |
| `build.ps1` | Windows build: restores the WDK from NuGet, builds Release x64, and optionally test-signs (`-TestSign`). |
| `../../.github/workflows/research-driver.yml` | Manual-only GitHub Actions job: runs the host tests, then calls `build.ps1 -TestSign` and uploads the artifact `RESEARCH-ONLY-test-signed-rawaccel-angular`. |
| `test/run.sh` | Host tests on Linux (g++/clang++) of the real `common/` headers. |
| `tools/make_angular_table.py` | Builds the table JSON from a Fourier model, CSV or D14 samples and writes it into a copy of a `settings.json`. Standard library only. |
| `examples/` | A table fragment for reference. |
| `GUIDE.md` | Step-by-step install, experiment and **full rollback** for the user's PC. |

## Design of the change

All math lives in `rawaccel/common/` (shared by the kernel driver and the user-mode wrapper):

* **Data** (`rawaccel-base.hpp`, `angular_gain_args`, appended to `profile`). It holds `angle_points`
  N (1–128, 0 = off) and `speed_points` M (1–16), `speed_min` and `speed_max`, and up to 2048 float
  gains, row-major: `data[j*N + i] = g(θ_i, s_j)`.
  * The **angle grid** is uniform: θ_i = 2π·i/N, periodic.
  * The **speed grid** is log-spaced from `speed_min` to `speed_max`. M = 1 makes the table
    speed-independent.
  * **Speed** is Raw Accel's own input unit: counts/ms, multiplied by 1000/DPI if the device DPI is
    set in `settings.json`. We recommend leaving the DPI at 0 so speed is raw counts/ms, which fits D10
    (profiles in counts).
* **Angle convention (D14).** θ = atan2(dy, dx) of the **raw input packet**, before Raw Accel's
  rotation, snapping and speed cap: 0 = right, π/2 = down, π = left, 3π/2 = up.
  Note that this differs from upstream PR #352, which uses the post-rotation angle.
* **Lookup** (`rawaccel.hpp`, `angular_gain_lookup`). It interpolates bilinearly: linear in angle with
  a periodic wrap, linear in log(speed), and clamped at the speed ends. It is O(1) with no binary
  search. It is **stateless**, keeps no history, and does no allocation.
* **Application.** `modify()` computes g from the raw packet first, runs the unchanged upstream
  pipeline, and finally multiplies the **whole output vector** by g. That changes the output's length
  only, never its direction. This is relative to the stock output, so it can be combined with every
  stock setting. With the table off, the code path is bit-identical to upstream (tested).
* **Validation** (`rawaccel-validate.hpp`). The checks are: N in 0–128, M in 1–16, and gain count =
  N·M. With M > 1, they also require 0 < speed_min < speed_max. Every gain must be finite and in
  [0.5, 2]. The writer shows the errors and refuses to write.
* **Kernel safety.** The driver never runs `valid()` on the blob it receives, as upstream also doesn't.
  The lookup therefore re-checks N and M itself, so it can never index outside the array. It treats
  non-finite intermediate values with a bit-pattern check (the driver is built with `/fp:fast`), and
  clamps the result to [0.5, 2].
* **JSON** (`wrapper.cpp`). The table is a new optional key inside a profile. A file without it
  (every upstream 1.7.x `settings.json`) loads unchanged, and when the table is off the writer leaves
  the key out:
  ```json
  "Angular gain table (RESEARCH ONLY)": {
    "angles": 64, "speeds": 1, "speedMin": 1.0, "speedMax": 1.0,
    "gains": [1.03, 1.06, ...]
  }
  ```
* **Version and ABI.** The driver reports version **1.7.900**. The settings blob layout differs from
  upstream, so the versions keep the two apart:
  * A stock 1.7.x writer or GUI used with this driver refuses with "newer driver is installed".
  * This writer used with a stock driver refuses with "reinstallation required".
  * The driver also rejects any WRITE whose size doesn't match its structs.
  * `sizeof(profile)` grows by about 8.2 KB. That's per profile in the IOCTL blob, plus one copy
    per mouse in the device extension.
* **Marking.** The `.sys` version resource says "OpenVector RESEARCH build … TEST-SIGNED, NOT FOR
  GAMES". The installer prints a warning and requires you to press **Y** before it does anything.

### Trade-off: stateless per-packet angle (A §2.4 note)

The angle comes from each USB packet on its own. At slow speeds and high polling rates, packets are a
few counts long, so their angles are quantised. For example, (1,0), (1,1) and (0,1) are the only
directions available for 1-count packets. Each packet gets the gain for its own quantised direction.
`test/run.sh` simulates straight strokes across 360 directions with a smooth target g(θ) (about 7% RMS
deviation from 1, including sin 2θ). The tables below show the resulting error. The direction error
comes from neighbouring packets getting different gains.

RMS error of the effective gain, relative to the target:

| polling | 0.5 counts/ms | 2 | 8 | 32 | 128 |
|---|---|---|---|---|---|
| 1000 Hz | 3.4% | 0.59% | 0.06% | 0.03% | 0.02% |
| 8000 Hz | 4.9% | 4.2% | 1.9% | 0.16% | 0.03% |

Worst-case direction error (degrees):

| polling | 0.5 counts/ms | 2 | 8 | 32 | 128 |
|---|---|---|---|---|---|
| 1000 Hz | 4.5° | 0.7° | 0.05° | 0.003° | 0° |
| 8000 Hz | 6.0° | 5.2° | 2.2° | 0.2° | 0.01° |

At 1600 DPI, 1 count/ms ≈ 1.6 cm/s, so a flick at 50–200 cm/s is about 30–125 counts/ms.
**For the experiment, use 1000 Hz polling and at least 1600 DPI**, and keep them the same in all
conditions. The stock projection's range-weight term κ has the same issue, so A/B/C stays fair.

We kept the lookup stateless anyway, for three reasons:
1. Upstream's maintainers stress "a constant set of formulas".
2. A stateless transform is easier to reason about for anticheat and for upstreaming (D3).
3. It preserves the property that the output depends only on the current packet.

The alternative is a stateful direction smoother like PR #352's (an 8 ms half-life accumulated
vector). It would remove most of the low-speed error, but it introduces history-dependent behaviour.
If the experiment shows that slow micro-adjustments matter, a smoother can be added later as a
separate, opt-in variant D. It is not implemented here.

### Relation to upstream PRs #352 and #349

* **#352** "Add directional multiplier", by GitHub user `akashrtd` (commits `abb72f9` and `91c767e`,
  2026-10-22). It adds an (angle, multiplier) list with up to 128 points and binary-search periodic
  linear interpolation, clamped to [0.5, 2] and applied last. It also adds a stateful direction
  smoother.
* **#349**, by GitHub user `yaazzzzzzzz` (four commits, 2026-10-11). It adds 8 fixed
  sectors with cone width and feathering.
* **Licensing.** Neither PR carries its own license notice. Both were submitted to an MIT-licensed
  repo, so under GitHub's terms (ToS §D.6, "inbound = outbound") they are offered under MIT.
* **What we took.** **We didn't copy code from either PR.** This is an independent implementation.
  We reused these design ideas from #352, credited here: a scalar multiplier applied last, the
  [0.5, 2] bounds, a kernel-side size re-check, and the optional-JSON-key pattern. The pattern itself
  comes from upstream's own LUT code.
* **How we differ.** We use a uniform grid (O(1), with no angle list to validate), an optional speed
  axis, no smoother, and the raw packet angle (D14).
* **Contributing upstream.** To propose this upstream, we'd offer the 1-D part (M = 1) as a
  review or alternative on #352. That needs the user's approval first.

## Build

* **CI (recommended).** In GitHub, go to Actions → "Research driver (RESEARCH ONLY, test-signed)" →
  *Run workflow*. Download the artifact `RESEARCH-ONLY-test-signed-rawaccel-angular` (kept for 14 days).
* **Local (Windows).** You need Visual Studio 2022 with C++ desktop, C++/CLI v143, the .NET Framework
  4.7.2 targeting pack and the Spectre libraries (or pass `-NoSpectre`). Then run:
  `powershell -ExecutionPolicy Bypass -File research\rawaccel-angular\build.ps1 -TestSign`.
  The output goes to `research\rawaccel-angular\out\RESEARCH-ONLY-test-signed\`, which is git-ignored.
* **Host tests (Linux/WSL):** `research/rawaccel-angular/test/run.sh`.

## Verified vs unverified

**Verified here (Linux, g++ 13 with ASan/UBSan and clang 18, plus `-ffast-math` builds):**
* Grid values hold exactly at the nodes, and the D14 axis convention is correct.
* Interpolation in angle, the periodic seam and the log-speed axis behave as designed, including
  clamping.
* Output = g × stock output for random stock settings, so direction is preserved.
* The lookup is stateless.
* Every validation error triggers.
* No out-of-bounds access or non-finite result occurs on 2000 random garbage tables.
* With the table off, research output is **bit-identical** to pristine upstream 1.7.1 on 60,000
  random packets × settings, including smoothers and every accel mode.
* `make_angular_table.py --selftest` mirrors the lookup.

**Not verified (no Windows here):**
* The MSVC/WDK build, the C++/CLI wrapper's JSON round trip and marshalling of the new struct, and
  the writer end to end.
* Whether the WDK NuGet props path and `/p:SignMode=Off` work as written on `windows-2025`.
* signtool discovery.
* Test-signed loading with HVCI (Memory integrity) on.
* Behaviour on real hardware, including latency. The extra cost is one atan2, at most two logs and a
  few multiplies per packet; it hasn't been measured.
* The GitHub workflow has never been run.

Treat the first CI run and the first install as the real test, and follow GUIDE.md's checks.
