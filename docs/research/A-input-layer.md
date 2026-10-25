# Research A: Input layer (how OpenVector applies g(θ) system-wide)

Status: Phase 0 research. Author: Research Agent (topic A). Date: 2026-10-25.
Source of truth for Raw Accel: `RawAccelOfficial/rawaccel`, default branch at commit
[`53a7213`](https://github.com/RawAccelOfficial/rawaccel/tree/53a721345617a1e29f3a16750cbdf807040cf44e)
(2025-07-27, release v1.7.1). All code links below are permalinks to that commit.

Confidence tags: **[verified]** = I read the source or primary doc myself. **[reported]** = second-hand
or community source. **[uncertain]** = my inference or conflicting sources.

---

## TL;DR

* Today's signed Raw Accel driver **cannot** take an arbitrary g(θ). In "whole" mode it can express:
  per-axis output scaling (Y/X ratio), separate left and up multipliers, a gain that changes linearly
  with the *folded* angle φ = atan(|y/x|) ∈ [0°, 90°] (via range weights plus a constant-sensitivity LUT),
  and a global rotation that also rotates the output. **[verified]**
* With rotation fixed at 0 (rotation changes output *direction*), the reachable family has **5
  effective degrees of freedom**: the 4 axis gains (right, down, left, up) can be matched exactly, plus
  one diagonal-shape parameter. The oblique 2nd harmonic sin 2θ (45°/225° vs 135°/315°) and all
  independent harmonics ≥ 3 are unreachable. Every left/up/Y-ratio asymmetry also tilts diagonal
  output by a few degrees. **[verified, numerically checked]**
* Fit quality: on random smooth targets (harmonics 1 to 4, about 5 % RMS deviation) the best fit leaves
  about 2.5 % RMS error and explains about 76 % of the variance. Targets with only the first harmonic
  plus a cos 2θ term fit to under 1 %. A pure sin 2θ target gets 0 % improvement.
* A true angular LUT is a small driver change (about 60 to 150 lines in `common/` plus the wrapper).
  **Two such PRs already exist upstream and are unreviewed**: #352 (angle→multiplier LUT, 128 points,
  direction-preserving) and #349 (8 fixed sectors), both opened in Oct 2026. Upstreaming is
  *technically* easy but *organisationally* slow. Signed driver releases are rare (v1.6.0 in Sep 2021,
  then v1.7.0 in May 2025), and the signing is tied to one maintainer's EV certificate.
* Raw Accel is the only open-source, signed, anticheat-tolerated mouse filter I found. Interception is
  blocked or unsupported by Vanguard and EAC (and historically FACEIT). Self-signing needs an EV cert,
  Partner Center attestation and anticheat goodwill, which conflicts with our constraints. User-mode hooks and
  SendInput are unsuitable.
* **Recommendation:** ship v1 on stock Raw Accel 1.7.x, using a parameter fit
  (`D, ρ, L, U, rx/ry + constant LUT`) that our tool writes once through Raw Accel's own `writer.exe`,
  or through its IOCTL with the same struct layout. In parallel, support and help land PR #352 (or an
  equivalent) upstream. Switch to the true angular LUT when a signed release ships it.

---

## 1. What Raw Accel's driver can express today

### 1.1 Architecture **[verified]**

* KMDF **upper filter on the Mouse device class** (`GUID_DEVCLASS_MOUSE`, `UpperFilters`, installed by
  `installer.exe` via `SetupDiSetClassRegistryPropertyW`; see
  [`common/utility-install.hpp` L125-174](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/common/utility-install.hpp#L125-L174)). It hooks the
  mouclass service callback (`IOCTL_INTERNAL_MOUSE_CONNECT`) and edits `MOUSE_INPUT_DATA.LastX/LastY` in
  place for every relative packet (`RawaccelCallback`,
  [`driver/driver.cpp` L48-139](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/driver/driver.cpp#L48-L139)). It skips absolute packets
  (`MOUSE_MOVE_ABSOLUTE`).
* Because it sits **below mouclass**, the modified counts are what both the cursor and **Raw Input
  (`WM_INPUT`)** see. Games that read raw input are affected, and that is why Raw Accel works in FPS games.
* Fractional output is carried per device (`carry.x/y`), so sub-count gains don't drift.
* Per-packet `time` comes from QPC deltas, clamped to `[clamp.min, clamp.max]` (default 1/16 ms to 100 ms),
  or from a fixed polling interval if `pollTimeLock` is set. Time only matters for speed-dependent curves.

### 1.2 The exact per-packet pipeline (`modifier::modify`) **[verified]**

[`common/rawaccel.hpp` L313-423](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/common/rawaccel.hpp#L313-L423). Windows convention: +x is right,
**+y is down** (toward the user). Input is `v` in counts, and `ips = dpi_factor / time`.

1. **Rotation** (`degrees_rotation`, α): `v ← R(α)·v`. This rotates the output too; it is a grip-angle
   correction, not a magnitude-only effect.
2. **Reference angle** (computed only if snapping or a directional range weight is active):
   `φ = atan(|v.y / v.x|) ∈ [0, π/2]`. This is a **folded** angle: right, left, up and down all map into
   one quadrant.
3. **Angle snapping** (`degrees_snap`, 0 to 45°): if φ < snap the vector becomes horizontal; if
   φ > 90°−snap it becomes vertical (magnitude kept). This changes direction.
4. **Speed clamp** (`speed_min/max`, JSON "Input Speed Cap"): clamps the input *magnitude*.
5. **Speed**: `s = ips · ‖(domain_x·|v.x|, domain_y·|v.y|)‖_p`. Here `lp_norm` p, with `p ≥ 16` (`MAX_NORM`)
   or p ≤ 0 giving the max norm. Note: the Guide says ">64", but the code uses 16.
   ([`rawaccel-base.hpp` L22](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/common/rawaccel-base.hpp#L22),
   [`rawaccel.hpp` L235-254](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/common/rawaccel.hpp#L235-L254)).
6. **Scale**. In *whole* mode (`combineMagnitudes=true`):
   `k = 1 + (f(s) − 1) · w(φ)`, with `w(φ) = rx + (2/π)·φ·(ry − rx)`. Here (rx, ry) = `range_weights`
   (JSON "Stretches accel range for horizontal vs vertical inputs"). `v ← k·v`, a scalar, so it
   **preserves direction**.
   In *by-component* mode: `v.x ← v.x·(1+(f_x(|v.x|·…)−1)·rx)`, and the same for y with a separate curve.
   There is no angle term in this mode.
   Optional EMA smoothing of input speed, scale and output (v1.7 "coalescion").
7. **Output scaling**: `v.x ← v.x · D`, and `v.y ← v.y · D · ρ`. Here `D = output_dpi/1000 ·
   dpi_normalisation`, where `output_dpi` is the GUI "Sensitivity multiplier" ×1000 since 1.7.
   `ρ = yx_output_dpi_ratio` (JSON "Y/X output DPI ratio (vertical sens multiplier)").
8. **Directional multipliers**: `if v.x < 0: v.x ← v.x·L` and `if v.y < 0: v.y ← v.y·U`, with
   `L = lr_output_dpi_ratio` ("left sens multiplier") and `U = ud_output_dpi_ratio` ("up sens
   multiplier"; y<0 is up/away from the user). These scale **components**, not magnitude, so they
   change diagonal directions slightly.

### 1.3 Knob inventory (JSON names from [`wrapper/wrapper.cpp` L57-330](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/wrapper/wrapper.cpp#L57-L330), limits from [`rawaccel-validate.hpp`](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/common/rawaccel-validate.hpp)) **[verified]**

| Knob (settings.json key) | Kernel field | Effect on g(θ) | Limits |
|---|---|---|---|
| `Output DPI` (GUI: sens multiplier) | `output_dpi` | global gain D | ≠ 0 |
| `Y/X output DPI ratio (vertical sens multiplier)` | `yx_output_dpi_ratio` | scales y component (ρ) | ≠ 0 (a negative value would invert y) |
| `L/R output DPI ratio (left sens multiplier)` | `lr_output_dpi_ratio` | x·L when x<0 | > 0 |
| `U/D output DPI ratio (up sens multiplier)` | `ud_output_dpi_ratio` | y·U when y<0 | > 0 |
| `Degrees of rotation` | `degrees_rotation` | rotates input and output | any |
| `Degrees of angle snapping` | `degrees_snap` | snaps near-axis motion to axis | 0 to 45 |
| `Stretches accel range for horizontal vs vertical inputs` {x,y} | `range_weights` | scales (f−1) linearly in folded angle (whole mode only) | ≥ 0 |
| `Stretches domain for horizontal vs vertical inputs` {x,y} | `domain_weights` | direction-dependent *speed* input to f | > 0 |
| `lpNorm` | `speed_args.lp_norm` | shape of speed iso-lines | > 0 |
| `Whole/combined accel …` | `speed_args.whole` | whole vs by-component | bool |
| `Input Speed Cap` | `speed_max` | clamps input speed | ≥ 0 |
| `Whole or horizontal accel parameters` / `Vertical accel parameters` | `accel_x/accel_y` | curve f (classic, jump, natural, synchronous, power, **lut**, noaccel) | per mode |
| `data` (in accel args) + `mode: "lut"` | `accel_args.data[514]` | LUT points (x₁,y₁,x₂,y₂,…), up to **257 points** | x increasing |
| `Gain / Velocity` | `accel_args.gain` | for LUT: y is velocity (true) or sensitivity (false) | bool |
| device `DPI (normalizes input speed unit…)` | `device_config.dpi` | input normalisation to 1000 DPI | ≥ 0 |
| device `Polling rate Hz …`, `pollTimeLock`, min/max time | `device_config` | timing only | |
| `devices[]` {name, profile, id} | `device_settings` | **per-device profile** by hardware ID | |

**LUT input.** [`accel-lookup.hpp`](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/common/accel-lookup.hpp): binary search plus linear
interpolation on **speed only** (the scalar `s` from step 5). Values clamp at the ends. There is **no
angle key**. The LUT can still be useful: a 2-point LUT with constant sensitivity c (gain=false) makes
`f(s) ≡ c`. That turns the range-weight term into a pure, speed-independent directional gain (see §2).

**Per-device profiles.** `DriverConfig.profiles[]` with named profiles. `devices[]` maps a device
hardware ID to a profile name. The default (first) profile applies otherwise
([`driver.cpp` DeviceSetup](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/driver/driver.cpp#L369-L432)).

### 1.4 How settings reach the driver, and persistence **[verified]**

* The control device is `\\.\rawaccel` ([`driver/driver.h` L15-16](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/driver/driver.h#L15-L16)), with
  IOCTLs `READ 0x888`, `WRITE 0x889` and `GET_VERSION 0x88a`, all `METHOD_BUFFERED`
  ([`rawaccel-io-def.h`](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/common/rawaccel-io-def.h)).
* The WRITE payload is a **raw binary blob**: `io_base` + N×`modifier_settings` + M×`device_settings`
  ([`driver.cpp` L223-290](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/driver/driver.cpp#L223-L290)). `modifier_settings` includes the
  precomputed `accel_union`, which user mode builds with `init_data` (wrapper `ManagedAccel`). The struct
  layout is therefore a de-facto versioned ABI. The wrapper refuses to talk to drivers older than
  `min_driver_version = 1.7.0` ([`rawaccel-version.h`](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/common/rawaccel-version.h)).
* **Every write sleeps 1 s in the kernel** (`WriteDelay`, `WRITE_DELAY = 1000` ms). This is the
  anti-abuse measure the FAQ cites.
* The control device's SDDL is `SDDL_DEVOBJ_SYS_ALL_ADM_RWX_WORLD_RW_RES_R`
  ([`driver.cpp` L541](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/driver/driver.cpp#L541)), so **any user-mode process can write settings
  without admin**. This is convenient for us, and worth knowing for threat modelling.
* Writers: the GUI, or **`writer.exe settings.json`** ([`writer/Program.cs`](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/writer/Program.cs)),
  which parses JSON → `DriverConfig.Activate()` → IOCTL WRITE, then exits.
* **No persistence in the driver.** The driver never reads the registry or a file. The FAQ says:
  "The driver itself does not store your settings. To enable them on PC start, run the GUI, or run
  `writer.exe settings.json`" ([FAQ](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/doc/FAQ.md)). Settings stay active until shutdown with no
  process running. Persistence therefore means a **one-shot writer at logon** (Task Scheduler or Startup
  shortcut). It exits right away, so it is *not* a background app in game. Open upstream PRs #230
  ("Add Autostart") and #291 (startup shortcut) would add this to the GUI.
* Implication for OpenVector: we can generate a standard `settings.json` and call the signed
  `writer.exe`. That is the simplest and most transparent option. We could also reimplement the IOCTL in
  our own code from the MIT headers, but then we must track the struct layout per Raw Accel version.

---

## 2. Mathematical expressiveness

### 2.1 Closed form (rotation α = 0, whole mode, constant LUT f ≡ c, no snap or clamp) **[verified]**

Let u = (cos θ, sin θ) with θ = atan2(y, x) in screen coordinates (0° right, 90° down, 180° left,
270° up). Let φ(θ) = atan(|tan θ|) be the folded angle. Then:

```
k(φ)   = 1 + (c − 1)·(rx + (2/π)·φ·(ry − rx))          ← scalar, direction-preserving
Lx(θ)  = L if cos θ < 0 else 1;   Uy(θ) = U if sin θ < 0 else 1
out(θ) = D · k(φ) · ( Lx·cos θ ,  ρ·Uy·sin θ )
g(θ)   = |out| = D · k(φ) · sqrt( Lx² cos²θ + ρ² Uy² sin²θ )
ψ(θ)   = atan2( ρ·Uy·sin θ , Lx·cos θ )                 ← output direction; error δ = ψ − θ
```

I checked this closed form numerically against a line-by-line port of `modify()`
(`fit_rawaccel.py` in my scratchpad; not product code).

Reduced parameters: `G0 = D·k(0)`, `κ = k(90°)/k(0)`, ρ, L, U, which gives **5 effective DOF**. Every
ratio κ > 0 can be realised (see §2.4 step 4), because D absorbs the overall scale. The only constraint
is that range weights must be ≥ 0.

Axis gains are matched **exactly**:
`g(0°)=G0`, `g(180°)=G0·L`, `g(90°)=G0·κ·ρ`, `g(270°)=G0·κ·ρ·U`.
The single remaining DOF is how the vertical/horizontal ratio R = κ·ρ splits between κ and ρ:
* **κ (range weights)** interpolates *linearly in angle*. It preserves direction, but it has a cusp
  (|θ| kink) at each axis, because φ is folded.
* **ρ (Y/X ratio)** interpolates *elliptically*. It is smooth, but it rotates diagonal output by up to
  `atan((ρ−1)/(2√ρ))`, which is about 2.7° for ρ = 1.1 and about 5.2° for ρ = 1.2.
* L and U are half-plane ellipses. They always tilt diagonals by the same formula, using L or U in
  place of ρ.

### 2.2 Which Fourier harmonics are reachable **[verified numerically]**

Linearising log g around the identity (Fourier content of ∂ log g / ∂q):

| param | h0 | cos θ | sin θ | cos 2θ | sin 2θ | h3 | h ≥ 4 |
|---|---|---|---|---|---|---|---|
| G0 | 1 | | | | | | |
| κ (range weights) | 0.5 | | | −0.405 | | | small cos 6θ, 10θ… (triangle wave) |
| ρ (Y/X) | 0.5 | | | −0.5 | | | |
| L (left) | 0.25 | −0.424 | | +0.25 | | −0.085 cos 3θ | small |
| U (up) | 0.25 | | −0.424 | −0.25 | | +0.085 sin 3θ | small |

So, to first order: **a₀, a₁ (cos θ), b₁ (sin θ) and a₂ (cos 2θ) are reachable. b₂ (sin 2θ, the
diagonal "/" vs "\\" asymmetry) is not reachable at all.** Harmonics ≥ 3 appear only as fixed
by-products. They cannot be set independently. Once the four axis gains are fixed, all four diagonal
gains are fixed by a single shared shape parameter, so they cannot be set independently.

**Rotation α** shifts the fold axes and the ellipse axes, so it *could* create a sin 2θ term. But it
rotates every output by α, which is a constant aim-direction error. This is unacceptable for a
"magnitude only" profile. Keep α for the user's real grip-angle correction, if they want one.

**Domain weights and Lp norm** affect only the *speed* fed to f. With f ≡ c they do nothing. With a
power-law f(s) = a·s^e, the result separates into `‖W u‖_p^e · |v|^e`. That is a direction factor (a
super-ellipse, mirror-symmetric about both axes) multiplied by acceleration. A "pure directional,
no accel" profile cannot use them.

**Coupling caveat:** the range-weight term scales *(f−1)*. If the user also wants a speed curve, then
κ means "how much acceleration per direction", not a separate directional gain. ρ, L and U stay
independent of speed in all cases.

### 2.3 Fit quality (from `fit_rawaccel.py`; log-space LSQ, 720 angles)

| target | baseline (best constant) | best fit RMS / max | max direction error |
|---|---|---|---|
| Issue #351 example (R +1 %, D −8 %, L +10 %, U −3 %), smooth 1st-harmonic interp | n/a | 0.70 % / 1.1 % | 2.7° (L/U tilt) |
| 8-direction target with independent diagonals | n/a | 2.7 % / 4.9 % | 4.1° (2.1° with ρ=1) |
| pure sin 2θ, ±8 % | 5.66 % | **5.66 %** (no improvement) | 0° |
| 200 random smooth targets, harmonics 1 to 4 | 5.4 % median | 2.5 % median (p90 3.9 %), about 76 % variance explained | median 5.2°, 1.9° with direction penalty |
| random targets with only a₁, b₁, a₂ | 4.7 % | 0.83 % | |

Interpretation: if real users' bias is mostly left/right and up/down asymmetry plus a horizontal vs
vertical difference, stock Raw Accel is a good approximation. If the bias is diagonal or "per-octant",
it cannot capture most of it. **[uncertain: we don't yet know the empirical spectrum of user bias.
Calibration research (topic C) should measure it and report the b₂ and h≥3 share.]**

### 2.4 Fitting recipe (target g(θ) → Raw Accel JSON)

1. Sample the target on θᵢ (e.g. 360 points). Optional weights wᵢ give the angle-occurrence density
   from calibration, so frequent directions count more.
2. Parameters `q = log[G0, κ, ρ, L, U]`. Model g(θ; q) and ψ(θ; q) from §2.1.
3. Minimise `Σ wᵢ (log g(θᵢ;q) − log g*(θᵢ))² + λ² Σ wᵢ δ(θᵢ;q)²` with `scipy.optimize.least_squares`,
   optionally with bounds such as 0.5 ≤ L, U, ρ ≤ 2. λ trades magnitude fidelity against direction
   tilt. λ ≈ 1 rad⁻¹ (log-units) was a reasonable default. Start from `log g*` at the 4 axes:
   G0 = g*(0), L = g*(180)/g*(0), U = g*(270)/g*(90), κ·ρ = g*(90)/g*(0), κ = ρ.
4. Map back:
   * `accel_x.mode = "lut"`, `gain = false` (sensitivity), `data = [1, c, 2, c]` with c = 2. For
     κ < 1 with rx = 1, use the formula below. `whole = true`, `lpNorm = 2`, domain = {1,1}.
   * `k(0) = 1+(c−1)·rx`, `k(90) = 1+(c−1)·ry`. Choose rx = 1 to get k(0) = c, then ry = (κ·c − 1)/(c − 1).
     This needs ry ≥ 0, so pick c with κ·c ≥ 1, for example c = max(2, 1/κ + ε).
   * `Output DPI = 1000 · G0 / k(0) / dpi_norm`, `Y/X = ρ`, `L/R = L`, `U/D = U`, rotation 0, snap 0.
5. Verify by feeding the JSON through the wrapper's `ManagedAccel.Accelerate` (the GUI's chart path),
   or our own port, on a dense direction grid. Report the residual curve to the user.

Packetisation note **[verified reasoning]**: ρ, D, L and U are linear on each sign of each component.
That makes them exact regardless of how motion is split into packets. The range-weight term κ works
on the *per-packet* angle. At high polling rates and slow speeds, packets are 1-count vectors like
(1,0), (0,1) and (1,1), so the per-packet angle is coarsely quantised. Because k is linear in φ, the
error partly averages out, but it isn't exact. Any future angular LUT has the same issue, which is
why PR #352 adds a direction smoother.

---

## 3. Minimal driver change for a true angular LUT

### 3.1 Design (1D)

* `profile` gains `int dir_len; float dir_data[2*N]` (angle, multiplier pairs) or a uniform grid
  `float dir_mul[N]`, with N = 64 to 256.
* In `modify()`, after rotation and before or after scaling:
  `θ = atan2(v.y, v.x)` → periodic interpolation → `v *= m(θ)`. A scalar multiply preserves direction.
  It is about 15 to 30 lines. The kernel already uses `atan`, `pow` and doubles in this path, so it adds
  no new kernel facilities.
* Validation (bounds 0.5 to 2, monotonic angles): about 15 lines. Wrapper marshal attributes and JSON
  property: about 10 lines. GUI editor and chart: optional, larger (for our purposes we don't need the
  GUI because we write JSON).
* Optional 2D (angle × speed) bilinear table, e.g. 32×16 floats: about +40 lines. `sizeof(profile)`
  grows by 2 KB. That's fine; the blob is copied per device.
* An ABI break is unavoidable, because `profile` is in the IOCTL blob. It needs a version bump, a
  `min_driver_version` bump and a new signed driver.

**Estimate:** 60 to 150 LOC in `common/` + wrapper + tests, excluding GUI. **This already exists:**
[PR #352](https://github.com/RawAccelOfficial/rawaccel/pull/352) "Add directional multiplier" (opened
2026-10-22 by akashrtd) touches `rawaccel-base.hpp`, `rawaccel.hpp`, `math-vec2.hpp`,
`rawaccel-validate.hpp`, `wrapper.cpp`, the grapher `SettingsManager.cs`, tests and the Guide. It adds up
to 128 (angle, multiplier) points, periodic linear interpolation by binary search, multipliers clamped
to [0.5, 2], and it is applied last, so it preserves direction. It also adds a stateful direction
smoother (8 ms half-life, 120° reset). `sizeof(profile)` becomes 6048 bytes (+1032). There are no reviews
yet, the author tested in user mode only, and the driver hasn't been run on hardware.
[PR #349](https://github.com/RawAccelOfficial/rawaccel/pull/349) (8 fixed sectors with cone width,
feathering and minimum speed; 2026-10-12) competes with it and is also unreviewed. Related feature
requests: [#351](https://github.com/RawAccelOfficial/rawaccel/issues/351) (angle→multiplier),
[#348](https://github.com/RawAccelOfficial/rawaccel/issues/348) (separate left/right rotation; it
notes that Secure Boot and EA Javelin make self-built drivers impractical), and #236 (separate X and Y
rotation). **[verified via GitHub pages; review state as of 2026-10-25]**

Suggested contribution (for the Input/Driver agent, not done here): review and test #352 on hardware.
Argue for keeping it stateless (drop or make optional the direction smoother, since maintainers have
historically stressed "constant set of formulas") or for a uniform grid (O(1) lookup). Add an
optional 2D speed axis only if calibration shows we need it.

### 3.2 Is upstreaming realistic? **[verified facts; judgement uncertain]**

* **Maintainers:** a1xd (Alexander Dwivedi, original author and certificate holder) and Jacob Palecki
  (1.7.x releases). Of the 49 commits on the default branch since 2024-01-01: Jacob Palecki 26, a1xd 17, others 6. lexm2 merges a new UI
  (PRs #281 to #342, Jul 2025 to Apr 2026).
* **Release cadence of the signed driver:** v1.6.0 Sep 2021 → v1.6.1 Apr 2022 (GUI) → **v1.7.0 May
  2025** → v1.7.1 Jul 2025 (GUI fixes). The driver directory was last changed Jul 2024 (ARM64,
  "primary sensitivity → output DPI"). So there has been about one driver ABI change every 3 to 4 years.
* **PR intake:** 114 PRs total, 13 open. Outside feature PRs (#230 autostart, #312 crash fix,
  #327/#337 tests/docs, #349, #352) sit open for months without review. The merged PRs are mostly
  maintainers' and lexm2's.
* **Signing:** [`signed/readme.txt`](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/signed/readme.txt) and [`signed/x64/sign.ps1`](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/signed/x64/sign.ps1)
  show the flow. Build → MakeCab → sign the CAB with the EV cert `"DWIVEDI, ALEXANDER ASHOK"` →
  **submit to Microsoft Partner Center (attestation signing)** → Microsoft returns `rawaccel.sys`. Only
  the holder of that EV cert/Partner Center account can produce a signed release. That is a single
  point of failure.
* **Judgement:** the change is small and in the spirit of the existing "anisotropy" features, and users
  are asking for it right now (two independent PRs in one month). A first-class "static directional
  multiplier, applied after everything, no smoothing" is the kind of feature the maintainers have
  accepted before (L/R and U/D multipliers, LUT). But **getting a new signed driver depends on a1xd's
  availability**, and history suggests a timescale of quarters to years. Estimate: possible within
  6 to 18 months, not something to block v1 on. **[uncertain]**

---

## 4. Alternatives

| Option | Raw-input effect | Anticheat status | Fits our constraints? |
|---|---|---|---|
| **Raw Accel (stock)** | Yes (below mouclass) | Tolerated (see §5) | **Yes**, with limited fidelity |
| **Raw Accel + upstream angular LUT** | Yes | Same driver, so same standing **[assumed]** | Yes, once released |
| **Interception** ([oblitum/Interception](https://github.com/oblitum/Interception)) | Yes. A signed kernel filter passes packets to a *user-mode* process that rewrites them | **Vanguard:** Riot anticheat lead Paul Chamberlain (May 2020 hotfix): Interception "still not supported", and the game won't launch while it is loaded ([GINX](https://www.ginx.tv/en/valorant-vanguard-hotfix-mouse-keyboard-input-device-driver)). **EAC:** iRacing support tells users to uninstall Interception because EAC blocks it ([iRacing KB](https://support.iracing.com/support/solutions/articles/31000176764--please-close-interception-before-starting-the-game-)). **FACEIT:** blocked it historically, per the Custom Curve developer ([mouse-sensitivity.com](https://www.mouse-sensitivity.com/forums/topic/5423-new-sensitivity-and-mouse-acceleration-options/)) **[reported]**. **EA Javelin:** detects it ([EA forum](https://forums.ea.com/discussions/fc-26-technical-issues-en/ea-javelin-anticheat-falsely-detects-interception-on-windows-11-steam-version/13540404)). | **No.** It's flagged because it gives *arbitrary, live, user-mode* control over input, which is exactly what cheats (macros, recoil scripts, aim assists) use. It also needs a running background app. License: dual LGPL/commercial; I believe the driver source isn't public **[uncertain]**. |
| **Own KMDF filter (fork of Raw Accel)** | Yes | Unknown signer, so likely "incompatible driver" prompts or blocks until vendors trust it | **No.** It violates "no own unsigned kernel code", and the user chose not to fork and self-sign. Cost and process: an EV code-signing cert (typically several hundred USD per year, hardware token, org or individual identity vetting), a Partner Center hardware-program account, and attestation submission for each build ([MS: code-signing reqs](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-reqs)). Since 2021 Microsoft is the only kernel signer. After that you still need to negotiate with each anticheat, and Custom Curve did that with FACEIT. HVCI compatibility and the vulnerable-driver blocklist would be our liability. |
| **User-mode hooks (`WH_MOUSE_LL`) + `SendInput`** | **No.** Raw input reads device data. Swallowing an event in an LL hook doesn't reliably stop the original `WM_INPUT`, and re-injected events carry `LLMHF_INJECTED` and arrive with `hDevice = NULL` in raw input ([MSLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct), [About Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input)) | Injected input is exactly what anticheats flag (macros, triggerbots). Many games (Valorant, CS2 with raw input) ignore or penalise it. | **No.** It also needs a background process and adds hook latency (it runs on the hooking thread's message loop, with a system timeout) **[uncertain: exact raw-input/LL-hook interaction is undocumented; community reports vary]**. |
| **Windows pointer settings / `SmoothMouseXCurve`/`SmoothMouseYCurve` registry, EPP** | **No.** These pointer ballistics apply to the cursor only. Raw input is pre-ballistics ([MS: Pointer ballistics](https://learn.microsoft.com/en-us/previous-versions/windows/hardware/design/dn613967(v=vs.85))) | Safe | **No.** It does nothing in raw-input games, and it is speed-only, not directional. |
| **Mouse firmware / onboard memory** | Yes (at the source) | Safe | **Partially.** It's vendor-specific. Some vendors offer separate X/Y DPI (e.g. Razer Synapse "X-Y DPI"). None I know of offer per-direction gain, and there's no common API. It's out of scope for a universal tool. (The parked hardware dongle idea lives here.) |
| **Other signed open-source filters** | | | I found **none that are maintained**. InterAccel/povohat-era tools used Interception plus user-mode math ([KovaaK/InterAccel](https://github.com/KovaaK/InterAccel)). Custom Curve 3.0 uses its own *proprietary* signed driver, built with FACEIT. The Microsoft `moufiltr` WDK sample isn't signed. maccel and YeetMouse are Linux-only. Raw Accel is the only open-source, signed, widely tolerated one. |

---

## 5. Raw Accel's anticheat track record

* **Project statement** **[verified]**: "Yes. That is one of the fundamental goals of this driver. It
  is fully signed and has a one-second delay on write, so it cannot be used to cheat … So far it is
  working in FaceIT, Valorant, and Diabotical matches" ([FAQ](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/doc/FAQ.md)). The [README](https://github.com/RawAccelOfficial/rawaccel/blob/53a721345617a1e29f3a16750cbdf807040cf44e/ReadMe.md) says it
  "only modifies mouse input by a constant set of formulas".
* **Vanguard (Valorant/LoL):** widely used. KovaaK (2022, [X post](https://x.com/Kovaak_of_qw/status/1563908538572967936)):
  "Raw accel is not a bannable app for Valorant (or anything that we've encountered)." Community
  guides agree ([The Spike](https://www.thespike.gg/valorant/tips/is-raw-accel-bannable-in-valorant)).
  **I found no primary Riot statement naming Raw Accel.** One undated third-party page claims a
  2026 Vanguard patch freezes the mouse with custom-curve drivers. I couldn't verify it and treat it as
  low-confidence **[uncertain]**. Riot's general policy: VAN "incompatible software" errors name the
  `.sys` file ([Riot support](https://support.riotgames.com/en-us/valorant/performance/van-incompatible-software)).
* **FACEIT:** "working in FaceIT matches" (project FAQ). An unofficial community FAQ claims FACEIT
  confirmed it **[reported]**. FACEIT's own policy pages don't name it
  ([FACEIT: what is deemed a cheat](https://support.faceit.com/hc/en-us/articles/360015788779-What-is-deemed-to-be-a-cheat)).
* **ESEA, EAC, BattlEye, Javelin, Ricochet:** **I found no official statement either way.** Community
  use is widespread (Apex/EAC, Fortnite/EAC, R6/BattlEye) and I found no credible ban reports. The
  ban anecdotes I did find (Steam CS2 and Blizzard D4 forums) are unverified, and KovaaK attributes such
  claims to deflection by cheaters **[reported]**.
* **Why it's tolerated** (our reasoning): it is signed through Microsoft attestation, the transform is a
  static formula, writes take ≥ 1 s, it needs no user-mode process, and the source is public. **Our use
  keeps all of these properties**: we write once after calibration, and nothing runs in game. One
  risk to watch: the control device is world-writable. If a cheat ever abused it (≤ 1 Hz rewrites),
  anticheats could start blocking `rawaccel.sys`, and we would lose our input layer with it. We should
  also avoid adding features to OpenVector that look like "dynamic" use, such as per-game automatic
  switching while a game is running.

---

## 6. Recommendation (ranked)

1. **v1: stock Raw Accel 1.7.x + fitted parameters (do this now).**
   OpenVector computes g*(θ), fits `{D, ρ, L, U, rx, ry, c}` per §2.4, writes `settings.json` (keeping
   the user's other profile fields and devices), and runs the signed `writer.exe` once on Apply. A
   one-shot logon task re-applies it after reboot. Show the user the residual (target vs achieved)
   and the maximum direction tilt.
   *Risks:* limited fidelity (no sin 2θ, no independent diagonals, axis cusp if κ is used), a few
   degrees of diagonal tilt from L/U/ρ, coupling with any speed curve the user already has, a
   settings-ABI change on Raw Accel upgrades (avoid this by using writer.exe and JSON, not our own
   IOCTL), and depending on a third-party install (we bundle or link the official release; MIT allows
   bundling, but we should not redistribute modified binaries).
2. **v1.x: true angular LUT through upstream Raw Accel (pursue in parallel).** Review, test on hardware
   and help shape PR #352 (stateless, direction-preserving, applied last, ideally a uniform grid,
   optional 2D speed axis). Keep OpenVector's profile format a superset (g(θ) samples + fitted Raw Accel
   parameters) so that when the driver supports it, we just emit the LUT. *Risks:* maintainer
   bandwidth, and a signed release depends on a single cert holder. The timeline is uncertain (6 to 18
   months). The PR might be merged in a different form (sectors, #349).
3. **Fallback if upstream stalls for a long time:** stay on option 1. Per the user's decision we do
   **not** fork and self-sign. We could revisit with the user if a third party (e.g. a maintained fork
   with its own attestation signing *and* anticheat acceptance) appears.
4. **Rejected:** Interception (blocked by Vanguard, EAC and historically FACEIT; needs a background
   process), user-mode hooks/SendInput (injected, doesn't affect raw input, flagged), Windows pointer
   curves (no raw-input effect), our own driver (violates constraints), firmware (vendor-specific).

Open questions for other agents:
* Topic C (calibration): measure the real harmonic spectrum of user bias. If b₂ and h≥3 are small, option 1
  is nearly lossless, and that strengthens the case for shipping it as the main path.
* Input/Driver agent: confirm on real hardware that `writer.exe` + a logon task works without UAC
  prompts (the SDDL suggests it does), and test the interaction with HVCI/Memory Integrity and Secure Boot.
