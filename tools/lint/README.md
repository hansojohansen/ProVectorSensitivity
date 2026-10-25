# tools/lint — OpenVector constraint linter

`ovlint.py` turns OpenVector's hard safety constraints into a CI failure. It enforces:

- **D5**: no API that could read games, inject input, hook input, see the screen or react to game state.
- **D11**: product builds never reference the research driver.

It needs only Python 3.10+ and the standard library.

```sh
python3 tools/lint/ovlint.py                           # lint this repo; exit 1 on violations
python3 tools/lint/ovlint.py --list-allows             # every allow comment + reason (PM review)
python3 tools/lint/ovlint.py --list-rules
python3 -m unittest discover -s tools/lint/tests -v    # linter tests (pytest also works)
python3 tools/lint/check_schemas.py                    # CI helper: validate schemas/*.json
```

Output format: `path:line:col: rule: message`.

## What is scanned

| Check | Files |
|---|---|
| Banned APIs | Source-like files (`.cs .c .cpp .h .hpp .py .ps1 .xaml .axaml .csproj .props .targets` …) under `src/` and `tools/` |
| Research references | Root-level build files, plus build/solution/installer files under `src/`, `tools/`, `tests/` and `installer/`, plus `.github/workflows/*.yml` |

Build/solution/installer files are `.csproj .vcxproj .props .targets .sln .slnx .wxs .iss .nsi`, plus
`global.json`, `nuget.config` and Velopack config. For workflows, files whose names start with
`research` (for example `research-driver.yml`) are allowed to build the fork and are not checked.

**Always excluded:** `tools/lint/` (it contains the rule text and fixtures), `research/`, and the
`bin/`, `obj/` and `.git/` directories. `tests/` and `reference/python/` are not checked for banned APIs.
Test harnesses may need some of these APIs on purpose. They never ship.

Matches are case-sensitive and whole-word, like the Win32 export names. Comments and string literals
**are** scanned on purpose. `EntryPoint = "SendInput"` is just as bad as calling it directly. So
don't name banned APIs in code comments. Write "input injection" instead.

## Rules

| Rule id | Catches | Why (decision) |
|---|---|---|
| `process-open` | `OpenProcess`, `NtOpenProcess`, `ZwOpenProcess` | D5: never open game processes |
| `process-memory` | `ReadProcessMemory`, `WriteProcessMemory`, `Nt/ZwReadVirtualMemory`, `Nt/ZwWriteVirtualMemory`, `VirtualAllocEx`, `VirtualProtectEx` | D5: memory reading is permanently banned |
| `code-injection` | `CreateRemoteThread(Ex)`, `NtCreateThreadEx`, `RtlCreateUserThread` | D5: never inject |
| `input-hook` | `SetWindowsHookEx(A/W)` | D1/D5: user-mode hooks are rejected |
| `input-injection` | `SendInput`, `NtUserSendInput`, `mouse_event`, `keybd_event` | D1/D5: injected input is what anticheats flag |
| `background-raw-input` | `RIDEV_INPUTSINK`, `RIDEV_EXINPUTSINK` | D5: the trainer reads raw input only in its own foreground window |
| `screen-capture` | `BitBlt`, `PrintWindow`, `CopyFromScreen`, `IDXGIOutputDuplication`, `DuplicateOutput(1)`, `GraphicsCaptureSession/Item` | D5: never look at the screen |
| `process-enumeration` | `GetProcesses`, `GetProcessesByName`, `EnumProcesses`, `CreateToolhelp32Snapshot`, `Process32First/Next` | D5: no game detection or reacting to game state |
| `research-reference` | a `research/` (or `research\`) path segment, or `rawaccel-angular`, in a checked file. `docs/research/` is fine | D11: installer, app and release pipeline never ship or reference the research driver |
| `allow-syntax` | malformed allow comment: unknown rule, or a reason under 3 words | allowlist hygiene |
| `stale-allow` | an allow comment that suppresses nothing, or whose rule doesn't apply to that file | allowlist hygiene |

## Allowlist (exceptions)

Every exception is an inline comment next to the code, with a mandatory reason:

```csharp
var n = SendInput(1, ref input, size); // openvector-lint-allow: input-injection <reason, at least 3 words>

// openvector-lint-allow: screen-capture <reason>
bool ok = PrintWindow(ourOwnHwnd, hdc, 0);
```

```xml
<!-- openvector-lint-allow: research-reference <reason> -->
```

- A comment at the **end of a line** applies to that line only.
- A comment on **its own line** applies to the next non-blank line. Several allow lines stacked
  directly above one line all apply to it.
- One rule per comment. There is no file-wide or directory-wide allow.
- **Every new allow comment must be reviewed by the PM** in the PR. CI prints them all with
  `--list-allows`. Exceptions to D5 that would touch other processes, the screen or global input are
  not approvable. They need a DECISIONS.md change approved by the user.

## Limits

This is a tripwire, not a proof. It cannot catch an API built from strings at runtime, raw numeric
flags such as `0x100` for `RIDEV_INPUTSINK`, or a forbidden behaviour reached through some other
API. Code review and D5 remain the real guarantee. Tighten the rules whenever such a case is seen.
