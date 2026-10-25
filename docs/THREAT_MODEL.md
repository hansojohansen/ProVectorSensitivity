# OpenVector — Threat model (v0, Phase 1)

Scope: the product (Core, RawAccel adapter, Capture, Trainer, App, installer) on a single-user Windows
PC, plus the research driver, which is separate from the product. Owners: QA and Docs/Release.
Update this file whenever what we write to the system changes.

## Assets
- **The user's input path:** Raw Accel driver state, which affects every game.
- **Integrity of the user's Raw Accel `settings.json`** and of our backups in `%LOCALAPPDATA%\OpenVector\backups\`.
- **The user's standing with anticheats and leagues:** they must not be flagged or banned.
- **Calibration data and profiles:** low sensitivity, but they are personal performance data.

## Trust boundaries
- **Standard user ↔ Raw Accel driver.** The driver's control device is the boundary.
- **OpenVector ↔ Raw Accel install.** We run its `writer.exe` and edit its `settings.json`.
- **Task Scheduler.** The optional logon task `OpenVector\ReapplyRawAccel`.
- **Shared profiles from other people** (Phase 3).
- **Product ↔ research driver** (D11).

## Threats and mitigations

### T1. Raw Accel control device is writable by any user without admin
**Fact (A §1.4).** `\\.\rawaccel` uses SDDL `SDDL_DEVOBJ_SYS_ALL_ADM_RWX_WORLD_RW_RES_R`. Any user-mode
process can send the WRITE IOCTL. The kernel enforces a 1 s delay per write.

**Threats.**
- Any local program (malware, or another app) can silently change the user's mouse behaviour.
- A cheat could abuse the device for slow (≤ 1 Hz) dynamic changes. If that happened, anticheats
  could start blocking `rawaccel.sys`, and OpenVector would lose its input layer.

**Mitigations.**
- This is out of our control, because it is upstream's design. We do not widen it.
- OpenVector writes only through `writer.exe`, once per Apply, never on a timer.
- The status indicator compares the hash of the file we last wrote, the driver version and the task
  state. That detects some tampering with the file, but not with in-memory driver state, until Phase 2
  read-back.
- We document the risk in the user guide.
- We never build "dynamic" features such as per-game switching, which would make the device look abused.

**Residual risk.** Accepted. It is the same for every Raw Accel user.

### T2. Tampering with `settings.json` or backups
**Threat.** These files sit in user-writable locations. A local process could edit them so that the
next Apply, or the next logon re-apply, loads hostile values. Hostile values include extreme
sensitivity or a malformed LUT. writer.exe silently replaces unknown keys with defaults.

**Mitigations.**
- Before Apply, we validate the merged file against the per-version key map and refuse untested Raw
  Accel versions (D4).
- We clamp our fields to the profile bounds (D9: ±15%, configurable to ±25%).
- We keep a timestamped backup before every write. The status indicator shows a hash mismatch when the
  file differs from what we last wrote.
- Revert and reset-to-neutral need no trust in the current file.

**Residual risk.** A process running as the user can already do worse. We guard against accidents and
inconsistent states, not against a compromised account.

### T3. Tampering with the logon task `OpenVector\ReapplyRawAccel`
**Threat.** Someone changes the task's action to run a different executable or a different settings
file at every logon. That gives persistence for malware disguised as our task.

**Mitigations.**
- The task is **off by default**, and created only when the user flips the switch (D4).
- It is created per user, with least privilege (no "run with highest privileges"). Its action is the
  absolute path of Raw Accel's `writer.exe` and the absolute path of `settings.json`.
- The app's status check verifies that the task's action exactly matches what we expect, and flags any
  difference. Reset to neutral and uninstall delete the task.
- We never store an OpenVector executable as the task's action.
- **TODO (Input/Driver):** confirm that the task is created without elevation and that its ACL
  doesn't let other non-admin users modify it.

### T4. Malicious or broken shared profiles (Phase 3)
**Threat.** A profile file from the internet contains out-of-range gains, NaN or ∞, huge arrays or
schema-violating content. The aim might be to make the user's aim worse, to crash the app, or to
smuggle in Raw Accel keys.

**Mitigations (required before profile sharing ships).**
- **Validation.** Strict JSON Schema validation with `schema_version`. Reject unknown fields that
  affect behaviour. Reject non-finite numbers.
- **Clamps and limits.** Clamp gains to D9 bounds after loading. Cap array sizes and Fourier order.
- **Fixed output.** Shared profiles are canonical g(θ) only, never Raw Accel keys. Only our projection
  produces Raw Accel parameters (the ownership rule in ARCHITECTURE.md).
- **Review before Apply.** An imported profile is shown in the measured-vs-applied view, and still
  requires an explicit Apply.

### T5. Research driver (D11)
**Threats.**
- The user runs the test-signed driver with an anticheat game and gets flagged or banned.
- Test-signing and Secure Boot off weaken platform security.
- Changing Secure Boot with BitLocker active triggers a recovery-key prompt, and could lock the user
  out if they don't have the key.
- The research driver accidentally ends up in a product build or release.

**Mitigations.**
- The driver lives only in `research/`, is never shipped, and is built only by `research-driver.yml`.
- The constraint linter fails CI if any product project, solution, installer file or other workflow
  references `research/` or `rawaccel-angular`.
- The user guide has a separate warning section: suspend BitLocker first; use only with our trainer
  and offline games; reinstall signed Raw Accel and turn test-signing off afterwards.

**Residual risk.** User error. Mitigated by clear documentation and by keeping the research build
out of every user-facing download.

### T6. OpenVector itself drifting into banned behaviour
**Threat.** A future change adds a capability anticheats treat as cheating: process enumeration, input
injection, background raw input (`INPUTSINK`), screen capture, or hooks.

**Mitigations.**
- The D5 constraint linter runs in CI on Linux and Windows. Allow comments need a written reason and
  PM review.
- CONTRIBUTING lists the hard constraints.
- The linter is a tripwire, not a proof (see `tools/lint/README.md`). Code review remains required.

### T7. Supply chain and update channel
**Threat.** A compromised dependency, or a compromised release/update artifact.

**Mitigations (planned).**
- Signed releases via SignPath Foundation (D7).
- Pinned action major versions with read-only CI permissions.
- Dependency review before release, and THIRD_PARTY_NOTICES regenerated from the actual dependency graph.

## Out of scope
- An attacker with admin or kernel access.
- Vulnerabilities inside Raw Accel's driver itself. We report them upstream.
- The parked hardware dongle (D10).
