# CLAUDE.md — OpenVector

## Project context
OpenVector is an open-source (MIT), Windows-only alternative to CORSAIR/Aimlabs "PureVector". It
measures a user's direction-dependent aiming bias and builds a static, personalized directional gain
map g(θ) (optionally also over speed/distance), applied system-wide at the input layer.

Hard constraints (from the project brief; never relax without the user's explicit approval):
1. Anticheat-safe: only transform raw mouse input with a static precomputed profile. Never read game
   memory, inject, look at the screen, know target positions, or react to game state.
2. Static outside calibration: the profile changes only when the user finishes a calibration and clicks "Apply".
3. No background app required in game.
4. Windows only (for the software product).
5. Reuse over reinvention (Raw Accel is the input-layer candidate).

Process: Claude acts as PM and delegates to area subagents (Research, Input/Driver, Trainer,
Calibration/Data Science, Desktop UI, QA, Docs/Release). Decisions live in `docs/DECISIONS.md`, the
plan in `docs/PLAN.md`, and research in `docs/research/`. Stop for user review after Phase 0.

User decisions so far: MIT license; if Raw Accel can't express g(θ) and upstreaming fails, approximate
within the current signed Raw Accel driver (no fork + self-signing). Tech stack chosen by research.

## Parked idea: commercial hardware variant (NOT current scope)
The user floated a possible separate, closed-source commercial product: a USB dongle that sits
between any mouse and the PC. It applies the directional profile in hardware before input reaches the
PC, so it works on every mouse and OS and needs no driver. Keep it in mind for future work, but do not
let it affect the open-source project's scope. Shared, hardware-agnostic parts (profile format,
calibration model) should stay cleanly separable so a future device could consume the same profile.

Known issues the hardware variant would have to solve:
- **Anticheat / legitimacy (biggest risk).** Man-in-the-middle mouse devices (KMBox, Cronus Zen/XIM-style
  adapters) are what hardware cheats use, and Vanguard, FACEIT, EAC and BattlEye actively fingerprint
  them (VID/PID, HID descriptors, timing). A legitimate device could still be flagged or banned, and
  tournaments may prohibit it. It would need a "no PC communication path during play" design, a static
  profile, published behavior, and ideally outreach to anticheat vendors.
- **Polling rate and latency.** USB full-speed caps HID at 1 kHz. 8 kHz needs USB high-speed on both the
  host side (to the mouse) and the device side (to the PC). The MCU therefore needs HS host + HS device
  (e.g. NXP i.MX RT/Teensy 4.x, STM32 with HS PHY). RP2040 PIO-USB is full-speed only. Added latency
  must be measured and kept far below 1 polling interval.
- **Universal mouse support.** The device must parse arbitrary HID report descriptors (16-bit deltas,
  extra buttons, vendor reports, composite devices), re-expose an equivalent descriptor, and keep
  vendor software (DPI or RGB config) working via passthrough.
- **Device identity.** Should it mirror the mouse's VID/PID (impersonation, which looks cheat-like) or use
  its own? An own VID is cleaner but costs USB-IF fees.
- **Profile updates.** There needs to be a config channel (separate HID or vendor interface) that is
  only active in a "calibration mode", e.g. a physical button or a re-plug, so it is static in game.
- **DPI awareness.** The device sees counts, not cm. Profiles must be expressed relative to counts, and
  the user must recalibrate if they change DPI on the mouse.
- **Sub-count precision.** Applying non-integer gain to integer deltas needs remainder accumulation
  per axis (as Raw Accel does) to avoid drift or quantization bias.
- **Business and legal.** FCC/CE/UKCA certification, USB-IF compliance, manufacturing and support.
  Raw Accel (MIT) code can be reused in closed source with attribution. Keep a clear boundary between
  the MIT open-source repo and any proprietary firmware or hardware.
