# C. Tech stack research

**Status:** research note, not a decision record. Written 2026-10-25.
**Scope:** language, frameworks, and toolchain for OpenVector's four parts: the desktop app, the calibration trainer, the analysis code, and the Raw Accel integration. Also covers build, release and signing.

Each claim has a confidence tag:
- **[verified]** I read the source code or the primary documentation myself.
- **[reported]** It comes from a secondary source or a search summary.
- **[uncertain]** It is my inference, or I could not confirm it.

Raw Accel claims were checked against its source at `RawAccelOfficial/rawaccel` master `53a7213` (tag `v1.7.1`, 2025-07-27) and against the `userinterface` branch (`909ca77`, 2026-04-22).

---

## TL;DR recommendation

**Use C# on .NET 10 LTS for every component, in one Visual Studio solution:**

| Component | Choice |
|---|---|
| Shared core library (`OpenVector.Core`) | Plain C#: gain model, profile I/O, Raw Accel adapter, statistics. Tested with xUnit. |
| Desktop app | **Avalonia** (MIT) + **ScottPlot 5** (`PolarAxis`) |
| Trainer | **Separate C# executable** with a small purpose-built D3D11 renderer (Vortice.Windows or Silk.NET bindings). It owns its Win32 window and runs a **dedicated Raw Input thread** that reads with `GetRawInputBuffer` and stamps each read with QPC. |
| Raw Accel integration | **Short term:** generate Raw Accel `settings.json` and run the user's own `writer.exe`. Read the driver version ourselves with a 12-byte `GET_VERSION` IOCTL. **Medium term:** vendor Raw Accel's C++/CLI `wrapper` (MIT), rebuilt with `CLRSupport=NetCore`, so we can read the active config back and validate settings without spawning a process. |
| Research/reference analysis | Python (numpy/scipy/statsmodels) in a `research/` folder that is not shipped. It produces golden test vectors that the C# implementation must match. |
| Data formats | Profile: JSON + JSON Schema. Raw event streams: Parquet. Session/trial metadata: JSON. |
| Release | GitHub Actions `windows-2025` runners, **Velopack** installer/updater, **SignPath Foundation** signing (fallback: Azure Artifact Signing) |

**Alternatives considered:** (1) the same C# core with a **Godot 4.7.2+ (C#) trainer**; (2) a **Rust** stack (Tauri + wgpu/Bevy + `windows-rs`). Both are covered in §7, along with why they are not first choice.

---

## 1. Raw Accel's configuration interface

### 1.1 How settings reach the driver [verified]

- The driver exposes the device `\\.\rawaccel`. It handles three buffered IOCTLs (`common/rawaccel-io-def.h`):
  - `READ = CTL_CODE(0x8888, 0x888, METHOD_BUFFERED, FILE_ANY_ACCESS)`
  - `WRITE = …0x889`
  - `GET_VERSION = …0x88a`
- `common/rawaccel-io.hpp` implements `read()`, `write()`, `reset()` and `get_version()` with `CreateFileW` + `DeviceIoControl`. The handle is opened with access `0`. **[uncertain]** Whether this works without elevation in practice. The device ACL is not in the code I read, but the GUI is not normally run elevated.
- **The WRITE payload is a raw, native C++ memory image.** It is an `io_base` followed by N × `modifier_settings` and M × `device_settings`.
  - `modifier_settings` holds the `profile` struct and a precomputed `data_t`. `data_t` contains flags, a rotation vector and an `accel_union` of per-curve precomputed state, including a 514-float lookup table.
  - The user-mode side computes this state (`init_data`) before writing.
  - So "direct IOCTL" means reproducing the exact struct layout, alignment and curve precomputation of one specific Raw Accel version.
- **The driver sleeps 1 s on every WRITE** (`WriteDelay()`, `WRITE_DELAY = 1000` ms, a deliberate anti-cheat measure). Every apply or revert therefore takes at least about 1 s and must not block a UI thread.
- **The driver does not persist settings.** From the FAQ: "The driver itself does not store your settings. To enable them on PC start, run the GUI, or run `writer.exe settings.json`."
- **Version gate.** `valid_version_or_throw()` throws `"reinstallation required"` if driver < `min_driver_version` (1.7.0). It throws `"newer driver is installed"` if the driver is newer than the user-mode code. User-mode code compiled for version X therefore refuses a newer driver.

### 1.2 The three layers we could call

| Layer | What it is | How to call it |
|---|---|---|
| `wrapper.dll` (C++/CLI) | `DriverConfig` with `Convert(json)` (validates and builds native settings), `Activate()`, `GetActive()`, `ToJSON()`, `Deactivate()`, plus `VersionHelper.ValidOrThrow()`. Uses Newtonsoft.Json. | Only from .NET. **It is built as a .NET Framework 4.7.2 mixed-mode assembly** (`<CLRSupport>true</CLRSupport>`, `TargetFrameworkVersion v4.7.2`) on both master and the `userinterface` branch. |
| `writer.exe` (C# .NET Framework 4.7.2) | `writer.exe <settings file>`: loads JSON → `DriverConfig.Convert` → `Activate()`. With no argument it creates a default `settings.json`. | Any language can spawn it. |
| Raw IOCTL | The device and codes above. | Any language with `DeviceIoControl`. The *payload* is the hard part. |

### 1.3 Per-language options

**C#/.NET (recommended)**

- *Reference the shipped `wrapper.dll`:* this only works if our app targets .NET Framework 4.8. Microsoft's porting guide says C++/CLI must be rebuilt with `<CLRSupport>NetCore</CLRSupport>` (`/clr:netcore`) to run on .NET Core / .NET 5+ [verified: [MS porting guide](https://learn.microsoft.com/dotnet/core/porting/cpp-cli)]. Microsoft does not state outright that a Framework-built mixed-mode DLL fails to load on .NET 10, but it is unsupported, so do not rely on it [uncertain].
  - Oddity: Raw Accel's own new Avalonia UI (`userspace-backend`, `net8.0`) has a `ProjectReference` to the v4.7.2 `wrapper.vcxproj`. I could not confirm that this works at runtime; it may be unfinished.
  - Raw Accel's stale `dotnet5` branch (2021) has the wrapper building with `CLRSupport=NetCore` / `net5.0`. That shows the netcore rebuild is feasible.
- *Vendor and rebuild the wrapper (medium-term recommendation):* add Raw Accel as a git submodule pinned to a tag, and build `wrapper.vcxproj` with `CLRSupport=NetCore` for `net10.0`. This gives us Raw Accel's own validation (`Errors()`), JSON round-tripping, and `GetActive()` for the status indicator.
  - Cost: contributors and CI need the MSVC "C++/CLI support" workload. The WDK is **not** needed, because only `common/` headers are used.
  - The Raw Accel license (MIT) allows vendoring.
- *Spawn `writer.exe` (MVP recommendation):* see the caveats below.

**Rust / Python / anything else**

- *Spawn `writer.exe`* with a generated JSON file. This is the simplest path and is version-matched to whatever the user installed.
- *Direct IOCTL:* `GET_VERSION` is trivial and stable (three `int`s), so use it from any language for detection.
  - **Do not hand-mirror the WRITE payload** (`#[repr(C)]` structs or `ctypes.Structure`). It includes `accel_union` precomputation and changes between versions.
  - If we need direct writes without .NET, the only sane route is a tiny native shim compiled from Raw Accel's **header-only** `common/*.hpp` (C ABI, e.g. built with `cc`/`cxx` in Rust or as a DLL for ctypes), rebuilt per supported Raw Accel version.

### 1.4 Caveats of the `writer.exe` route [verified from source]

1. **Errors are reported with a modal `MessageBox`, then exit code 1.** Success is a silent exit code 0. Our app must run it with a timeout and treat a hang as "error dialog shown". We cannot capture the error text. Validating our JSON ourselves first (see §5) reduces this risk.
2. **It writes the whole config** (all profiles and all device mappings). To avoid clobbering the user's other Raw Accel settings, we must start from the user's existing `settings.json` and change only the fields we own.
3. **JSON keys are human-readable strings that change between versions.** From v1.6.1 to v1.7.0:
   - `"Sensitivity multiplier"` → `"Output DPI"`
   - `"Y/X sensitivity ratio (vertical sens multiplier)"` → `"Y/X output DPI ratio (vertical sens multiplier)"`
   - The L/R and U/D keys changed the same way.
   - Smoothing keys were added.

   Deserialization uses `DefaultValueHandling.Populate`, so **a wrong or old key is silently replaced by its default instead of raising an error.** We need a per-version key map and must refuse Raw Accel versions we have not tested.
4. **No read-back.** `writer.exe` cannot report what is currently active, so the status indicator needs either the vendored wrapper or our own READ parsing, which is version-fragile.
5. **The Raw Accel GUI may overwrite us.** The GUI has an "auto write to driver on startup" option (`GuiSettings.AutoWriteToDriverOnStartup`); when it is set, launching the GUI re-applies its own `settings.json`. To persist across reboots, and to stay consistent with the GUI, we should write into the user's Raw Accel `settings.json` after making a timestamped backup. "Revert" restores that backup and re-runs `writer.exe`. Persistence at logon means a scheduled task that runs `writer.exe settings.json`, exactly as the FAQ suggests.
6. **Where writer.exe lives.** Raw Accel ships as an extracted folder with `installer.exe`, which installs only the driver. The setup wizard must ask the user to locate their Raw Accel folder. We then verify `writer.exe`, `wrapper.dll` and the driver version.

### 1.5 Version-coupling summary

| Approach | Breaks when… |
|---|---|
| `writer.exe` | JSON keys change (they did in 1.7). Mitigation: a tested key map per version. |
| Vendored wrapper | The driver is newer than our pinned wrapper (`"newer driver is installed"`). Mitigation: rebuild per Raw Accel release. Patch releases such as v1.7.1 did not change `common/` or `driver/`, and their internal version stayed 1.7.0. |
| Hand-mirrored IOCTL | Any change to struct layout or curve state. **Avoid.** |

Raw Accel is also preparing "User Interface 2.0" (Avalonia, .NET 8) on the `userinterface` branch. This may change file formats or packaging when it ships, so watch that branch.

### 1.6 Important side finding for the product design (outside my topic; flagging for whoever owns Raw Accel mapping)

**Raw Accel 1.7 cannot express an arbitrary smooth angle→gain map.** [verified, `common/rawaccel.hpp` `modifier::modify`]

The angle-dependent controls are:
- `degrees_rotation`, applied to the input first.
- `degrees_snap`.
- `yx_output_dpi_ratio`, which scales output y.
- `lr_output_dpi_ratio` and `ud_output_dpi_ratio`, which scale output x when x < 0 and output y when y < 0.
- `domain_weights`.
- `range_weights`. With "whole" mode these are interpolated **linearly in the reference angle over 0–90°**, but they only scale the *acceleration* part, `1 + (f(v) − 1)·w`. With `noaccel` they have no effect.

So with constant sensitivity, Raw Accel applies a per-half-plane diagonal scaling. That changes direction as well as magnitude, and gives about 4 degrees of freedom plus a rotation. A fitted Fourier gain map has to be **projected onto this family** (e.g. constrain the model to the reachable family, or fit and then report the residual), unless OpenVector ships its own driver, which is out of scope.

**Useful for capture:** the device config `setExtraInfo` makes the driver write the *pre-modification* `(short)LastX, (short)LastY` into `ExtraInformation`. Raw Input exposes this as `RAWMOUSE.ulExtraInformation`. The trainer can therefore log both raw and Raw-Accel-modified counts in one stream. Caveat: values are truncated to 16 bits, and the field may conflict with pen/touch signatures, which Godot checks for `0xFF515700`.

---

## 2. Raw input at up to 8 kHz on Windows

### 2.1 API facts [verified, Microsoft docs sources on GitHub]

- **Two read styles.** The standard method is `WM_INPUT` + `GetRawInputData`, one packet per message. The buffered method is `GetRawInputBuffer`, which drains an array of `RAWINPUT` and removes the corresponding `WM_INPUT`s. Microsoft's "About Raw Input" page now recommends the buffered method for "high-frequency devices such as mice at 1000Hz". If you mix the two, read the current message with `GetRawInputData`, then drain with `GetRawInputBuffer`. [about-raw-input](https://learn.microsoft.com/windows/win32/inputdev/about-raw-input), [GetRawInputBuffer](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getrawinputbuffer)
- **WOW64 alignment gotcha.** `RAWINPUT` must be 8-byte aligned, and the buffer size is ×8. This only matters for 32-bit processes; ship x64 only.
- **Only one window per process per device class.** "Only one window per raw input device class may be registered to receive raw input within a process (the window passed in the last call to RegisterRawInputDevices)… RegisterRawInputDevices should not be used from a library" ([RegisterRawInputDevices remarks](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerrawinputdevices)). **This is the key constraint for engines:** if a game engine registers the mouse and we register our own capture window in the same process, the last registration wins and the other side silently stops receiving input.
- **No hardware timestamps.** `RAWINPUT`/`RAWMOUSE` carry no timestamp. `GetMessageTime` is a millisecond tick (`GetTickCount` domain, which wraps) and is too coarse for 8 kHz (125 µs between packets). The only option is a QPC (`QueryPerformanceCounter`) stamp *at read time*.
  - With batching, every packet in a batch shares one read time. Within-batch spacing must be inferred, e.g. by spreading packets evenly between successive reads (SDL3 does this) or from the known polling interval.
  - Raw Accel itself does the latter: the driver takes `KeQueryPerformanceCounter` per callback and divides elapsed time by packet count.
- **Background throttling.** A Microsoft engineer's post (reposted on Blur Busters; Insider build 25272, later shipped) says Windows 11 **throttles and coalesces raw mouse input for *background* listeners**. For example, a 1000 Hz mouse may arrive as about 125 messages/s, each carrying combined deltas [reported: [Blur Busters thread](https://forums.blurbusters.com/viewtopic.php?p=95000)]. **Consequence:** capture must run in the **foreground** process, i.e. the trainer window itself. A background helper process with `RIDEV_INPUTSINK` is not reliable for per-packet timing.

### 2.2 Known high-polling-rate problems and what engines do

- **Godot (verified in source).** Before 4.7.2, Godot handled `WM_INPUT` one message at a time through `PeekMessage`/`GetRawInputData`. With an 8 kHz mouse in captured mode this could drop below 1 FPS. Godot **4.7.2** (and 4.8.dev3) switched to `GetRawInputBuffer` in `process_raw_input()`. It never dispatches `WM_INPUT` through the pump, dispatches synthesized `WM_MOUSEMOVE` at most once per frame, and coalesces after 512 raw events per frame. The reported benchmark: 8 kHz on a 480 Hz display went from under 1 FPS to about 477 FPS [reported: [Godot blog](https://godotengine.org/article/fixing-high-polling-rate-mice-on-windows/), [GameDev.net mirror](https://gamedev.net/news/fixing-high-polling-rate-mice-on-windows-in-godot-r5258/)]. The source comment cites <https://ph3at.github.io/posts/Windows-Input/>, which I could not fetch.
- **SDL3 (verified in source).** Raw input runs on a **dedicated thread** at `THREAD_PRIORITY_TIME_CRITICAL`. The thread blocks in `MsgWaitForMultipleObjects(QS_RAWINPUT)` and drains with `GetRawInputBuffer`. Each mouse packet gets a timestamp **linearly interpolated between the previous and current poll times** (`SDL_windowsevents.c`, `WIN_PollRawInput`). This is the best existing reference design for us.
- **winit, and therefore Bevy (verified in source).** Uses the standard method (`GetRawInputData` per `WM_INPUT`) on the event-loop thread and registers mice and keyboards. Events have no timestamps.
- **GLFW, and therefore raylib's default backend.** `GLFW_RAW_MOUSE_MOTION` uses `WM_INPUT` only while the cursor is disabled. raylib exposes per-frame `GetMouseDelta`, i.e. aggregated, not per packet.

### 2.3 Recommended capture design (language-agnostic)

1. The trainer process owns a message-only window (`HWND_MESSAGE`) on a dedicated high-priority thread. That thread is the **only** raw-input registrant in the process: no engine or SDL relative mode registers in parallel.
2. Loop: `MsgWaitForMultipleObjects(QS_RAWINPUT)` → QPC → `GetRawInputBuffer` until empty → push `(qpc_read, batch_index, hDevice, dx, dy, buttons, ulExtraInformation)` into a preallocated lock-free ring buffer.
3. The render thread drains the ring each frame to drive the camera (cm/360 conversion). The logger thread writes the same events to disk, so the camera and the log can never diverge.
4. Record the per-batch read QPC and the packet count. Derive per-packet time in analysis, not at capture time. Expose an estimated polling rate so we can detect when the OS or driver coalesced packets.
5. Cursor: `ClipCursor` to the window rect + hidden cursor. Do not use `RIDEV_NOLEGACY` unless we handle every consequence (it also stops window dragging).

### 2.4 Language suitability for this loop

| Language | Assessment |
|---|---|
| **C/C++** | Ideal and trivial. Zero runtime jitter beyond OS scheduling. |
| **Rust** | Equally good via the `windows` crate. No GC. |
| **C#** | Fine with care. P/Invoke via CsWin32 or `LibraryImport`; keep the loop allocation-free (pinned/preallocated buffers, `stackalloc`, no LINQ or boxing). .NET GC pauses suspend *all* managed threads. Gen0 pauses are usually sub-millisecond but comparable to 125 µs packet spacing [uncertain: depends on allocation rate]. The OS queues packets during a pause, so no data is lost, only read-time stamps are delayed. Combined with per-batch timing (above), the impact on analysis should be small. **If measurement shows otherwise**, move just the capture loop into a roughly 200-line native C DLL that fills the ring buffer. Use `GCSettings.LatencyMode = SustainedLowLatency` in the trainer. |
| **Python (ctypes)** | Works at 8 kHz in a thread, but the GIL means the capture thread competes with rendering and logging for the interpreter. Read-time stamps get millisecond-scale jitter whenever another thread holds the GIL. The free-threaded CPython build (3.14+) helps, but not all of the extension ecosystem supports it. Python is not suitable for the trainer; it is fine for offline analysis. |

**Possible future option:** Microsoft **GameInput** (now on PC) wraps raw packets into "readings" with a timestamp and keeps about 0.5 s of history [reported: [GDK input readings](https://learn.microsoft.com/gaming/gdk/docs/features/common/input/overviews/input-readings)]. If its mouse readings carry per-packet timestamps from early in the input stack, it could beat read-time QPC. I could not verify the timestamp semantics or the redistribution story. **Worth a one-day spike, not a dependency.**

---

## 3. Trainer rendering options

What the trainer needs: a simple first-person 3D scene (room or grid, spheres/targets, crosshair, HUD), stable frame pacing at high refresh rates, the ability to own the Raw Input registration (§2.1), and contributor friendliness.

| Option | License | Raw input situation | Frame pacing | Contributor friendliness | Verdict |
|---|---|---|---|---|---|
| **Custom C# renderer**: Win32 window + D3D11 flip-model swapchain (DXGI waitable object) via **Vortice.Windows** or **Silk.NET** | MIT | We own the window and the single registration, so the §2.3 design applies directly | Best control on Windows: flip model, waitable swapchain, tearing/VRR flag | Medium: no editor, but the scene is procedural and small (a few thousand lines). Same language as the rest. | **Recommended** |
| **Godot 4.7.2+ (C#/.NET)** | MIT | Godot registers raw input itself. In 4.7.2+ it uses `GetRawInputBuffer` and handles 8 kHz well. `Input.use_accumulated_input` (default `true`) coalesces all motion to one event per frame. With `false`, you get per-packet events up to 512 per frame. **`InputEvent` has no timestamp** [uncertain: I found none in the API]. A second registration of our own would steal input from Godot (last registration wins), and Godot can re-register (e.g. window changes), which makes that fragile. | Good (Vulkan/D3D12, V-Sync modes, max FPS) | High: an editor, a large community, and C# projects can reference our `OpenVector.Core` class library | **Alternative 1.** Choose it if visual polish or editor-driven content matters more than per-packet timing. |
| **SDL3 + own renderer** (SDL3-CS bindings from ppy/osu!) | zlib | SDL3's raw-input thread with interpolated ns timestamps is good, but it hides `ulExtraInformation`, so we lose Raw Accel's pre-modification counts. Alternatively, do not enable SDL's relative mode and register raw input ourselves. | Good (SDL_GPU or our own D3D11) | Medium; osu-framework (C#, net10, SDL3) is a proven precedent | A reasonable variant of the recommendation (SDL for window/swapchain plumbing). |
| **Unity** | Proprietary. Personal tier free under US$200k revenue. Runtime Fee announced 2023 and **cancelled 2024-09-12** [[Unity](https://unity.com/blog/unity-is-canceling-the-runtime-fee)] | Input System events do carry times [uncertain on Windows raw path details] | Good | **Poor for MIT open source.** Contributors need a Unity account and the exact editor version. The engine source is closed and the license terms can change. | Not recommended. |
| **Bevy (Rust)** | MIT/Apache-2.0 | winit per-message `GetRawInputData`, `MouseMotion` with no timestamps | Good (wgpu) | Medium-low: Rust ECS, and breaking releases every few months | Only if we pick the Rust stack. Even then, a custom wgpu renderer with our own capture thread is simpler. |
| **raylib** (C, bindings in many languages) | zlib | Per-frame aggregated delta via GLFW. We would add our own capture thread with a native window handle. | OK; less control over the DXGI swapchain | High (simple API) | Viable for a C/C++ stack, which we are not choosing. |
| **MonoGame** | Ms-PL | Mouse is polled per frame (`Mouse.GetState`) [uncertain about raw details] | OK | Medium | No advantage over a custom D3D11 renderer for our needs. |

---

## 4. Desktop app UI

| Option | Notes | Verdict |
|---|---|---|
| **Avalonia** (MIT; XPF/Accelerate are separate paid add-ons we do not need) | Modern XAML/MVVM, active, cross-platform (irrelevant here but harmless). **Raw Accel's own next GUI uses Avalonia**, so there is contributor and code-pattern overlap with our upstream. ScottPlot has `ScottPlot.Avalonia`. | **Recommended** |
| WPF (.NET 10) | Mature, Windows-only, huge knowledge base, minimal new investment from Microsoft. Fine too. | Close second |
| WinUI 3 / Windows App SDK | Packaging/deployment complexity (MSIX or unpackaged bootstrapper), slower inner loop, smaller contributor pool. | Not recommended |
| Tauri 2 (Rust + web UI) | Small binaries; uses WebView2 (download bootstrapper adds 0 MB, offline installer about 127 MB [[Tauri docs](https://v2.tauri.app/distribute/windows-installer/)]). Two languages (Rust + TS). Plotly/D3 make polar plots easy. | Only with the Rust stack |
| Qt / PySide6 | LGPL obligations, Python packaging issues (see §5) | Not recommended |
| Electron | Heavy runtime, a JS-only ecosystem for native Win32 work | Not recommended |

**Polar plot:**
- **ScottPlot 5** ships a `PolarAxis` plottable with a cookbook recipe [verified in the repo tree: `src/ScottPlot5/ScottPlot5/Plottables/PolarAxis.cs`]. It is MIT, active (5.1.5x in 2026), and has WPF/Avalonia/WinForms controls.
- LiveCharts2 (MIT) has polar charts but has had a long preview period.
- OxyPlot (MIT) has a polar axis but is slow-moving.
- A gain-vs-angle curve with CI bands is also easy to draw by hand on an Avalonia `Canvas` if the library fights us.

---

## 5. Analysis code and data formats

### 5.1 Options

- **Python (numpy/scipy/statsmodels).** Best for exploration, bootstrap, mixed models and periodic splines. Shipping it inside a Windows app is painful:
  - PyInstaller bundles are tens to hundreds of MB.
  - Unsigned PyInstaller executables commonly trigger antivirus/Defender false positives [reported: [pydevtools PyInstaller](https://pydevtools.com/handbook/reference/pyinstaller/)].
  - We would have a two-runtime app (C# UI ↔ Python subprocess).
- **C# (MathNet.Numerics or hand-rolled).** Our model is a small linear least-squares problem. A Fourier series of order K has 2K+1 columns (K ≤ about 8), with ridge/roughness penalties. Bootstrap and permutation tests are loops. All of this is about 300 lines of C# using a QR/Cholesky solve.
  - MathNet.Numerics (MIT) is adequate but low-activity: last stable 5.0.0 (2022), 6.0.0-beta2 (2025-03), last commit 2025-03-02 [verified via git].
  - Keep the dependency thin or optional.
- **Rust (nalgebra/ndarray).** Fine for linear algebra; the statistics ecosystem is thin.

### 5.2 Recommendation

**Keep one shipped implementation, in C#, in `OpenVector.Core`.** The desktop app, the trainer and the tests all use it.

Keep a **Python reference implementation** in `research/`, not shipped. It is used for:
- (a) prototyping model changes;
- (b) heavier offline statistics (e.g. statsmodels mixed models for A/B studies);
- (c) generating **golden test vectors**: synthetic users with known gain maps, noise seeds, and expected fitted coefficients and CIs. These are stored as JSON/Parquet in `tests/golden/`.

C# unit tests (xUnit) assert agreement within tolerances. This gives us scientific tooling without two shipped runtimes. CI can run the Python side with `uv`/pip on the same Windows or Linux runner.

### 5.3 Formats (language-neutral)

- **Profile:** JSON validated by a published **JSON Schema** (draft 2020-12), with a `schemaVersion` field. It holds:
  - user setup: DPI, game, sensitivity, cm/360, polling rate;
  - the model: basis type, coefficients, regularization, CI;
  - provenance;
  - the *projected* Raw Accel parameters (§1.6) actually applied, plus the Raw Accel version.

  Validation libraries: C# `JsonSchema.Net` or `NJsonSchema`; Python `jsonschema`.
- **Raw event stream:** **Parquet** (typed, compressed, columnar). 8 kHz × 10 min ≈ 4.8 M rows, which is far too big for comfortable CSV. Read it with pyarrow/pandas in Python and Parquet.Net in C# [uncertain: confirm Parquet.Net's current license and maintenance before adopting].
  - Columns: `qpc_read`, `batch_idx`, `dx`, `dy`, `raw_dx`, `raw_dy` (from `ulExtraInformation`), `buttons`, `device_id`, `frame_id`.
  - Fallback: gzip'd CSV with a header comment for interchange.
- **Trial/session metadata:** JSON (or JSONL, one trial per line). It holds task definitions, target positions, frame timings (present QPC), and app/OS/driver versions.

---

## 6. Build, test, release

- **CI:** GitHub Actions hosted Windows runners.
  - `windows-latest` / `windows-2025` moved to a **Visual Studio 2026** image in June 2026. `windows-2022` keeps VS 2022 [reported: [GitHub changelog](https://github.blog/changelog/2026-05-14-github-actions-upcoming-image-migrations)].
  - The C++/CLI wrapper build needs the MSVC C++/CLI component. Confirm it is present in the runner image's software manifest [uncertain].
  - `dotnet test` runs normally.
  - **Hosted runners have no Raw Accel driver and no real mouse.** So:
    - abstract input behind an `IRawInputSource` and replay recorded Parquet traces in tests;
    - test the Raw Accel adapter against golden `settings.json` files per supported Raw Accel version;
    - keep a manual hardware test checklist, optionally with a self-hosted runner that has Raw Accel installed.
- **Target framework:** **.NET 10 LTS** (released 2025-11-11, supported to 2028-11-14). .NET 8 and 9 reach end of support on **2026-11-10** [reported: [.NET support policy](https://dotnet.microsoft.com/platform/support/policy/dotnet-core)].
- **Installer/updater:**
  - **Velopack** (MIT; Rust core, .NET SDK) gives a one-command installer, delta updates, GitHub Releases feeds, and a signing hook [reported: [docs.velopack.io](https://docs.velopack.io/packaging/overview)].
  - Inno Setup is the simple fallback.
  - **Avoid MSIX**: it is sandboxed, so writing into the user's Raw Accel folder and launching `writer.exe`, scheduled tasks and so on add friction.
  - **WiX v6+** now carries an Open Source Maintenance Fee for organizations making more than US$10k/year [reported: [FireGiant OSMF](https://docs.firegiant.com/wix/osmf/)]. It is probably not a problem for us but adds legal noise.
- **Code signing (user-mode binaries only; we never ship or sign a driver):**
  - **SignPath Foundation:** free for OSI-licensed projects. Requirements: a public repo, automated builds via GitHub Actions (`signpath/github-action-submit-signing-request`), a published code-signing policy, and named maintainers/approvers. Releases are manually approved [reported: [signpath.org](https://signpath.org/), [SignPath OSS](https://about.signpath.io/product/open-source)].
  - **Azure Artifact Signing** (formerly Trusted Signing): about $9.99/month Basic (5,000 signatures). Individual developers must be in the US or Canada; organizations can be in a wider list (US, CA, EU, UK, AU, …). Billing starts when the account is created [reported: [pricing](https://azure.microsoft.com/pricing/details/artifact-signing/), [quickstart](https://learn.microsoft.com/azure/artifact-signing/quickstart)].
  - **SmartScreen:** since 2024, EV certificates no longer bypass SmartScreen. Reputation builds per file and per certificate, so expect warnings on early releases whichever way we sign [reported: [MS code signing options](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)].

---

## 7. Recommendation and alternatives

### Recommended: all-C# / .NET 10

- **Raw input correctness.** The trainer is its own process and window, with a single dedicated `GetRawInputBuffer` capture thread (the SDL3/Godot 4.7.2 pattern), QPC stamps per batch, and logged `ulExtraInformation`, which gives pre-Raw-Accel counts. There is no engine competing for the one-per-process registration. The GC risk is understood, and if measurements demand it, an escape hatch exists: a native capture DLL.
- **Raw Accel integration.** Raw Accel's whole user-mode stack is .NET (wrapper, writer, current GUI, next Avalonia GUI). C# is the only language that can eventually reuse `DriverConfig` directly, with validation and read-back. That requires rebuilding the wrapper for .NET 10, which upstream already did once on the `dotnet5` branch. The MVP needs no C++ at all (`writer.exe` + a `GET_VERSION` P/Invoke).
- **Contribution.** One language, one solution, one test runner. Avalonia and WPF are familiar to Windows developers, and the Raw Accel community already works in this stack.
- **Single toolchain.** `dotnet build/test/publish` plus Velopack. Python appears only in non-shipped research tooling. C++/CLI appears only when the wrapper is vendored.

### Alternative 1: C# core + Godot 4.7.2+ (C#) trainer

- **Pros:** better visuals and tooling, a scene editor, a big community.
- **Why not first:**
  - Godot owns raw input registration. Running our own capture in the same process would steal input from Godot.
  - Godot exposes no per-event timestamps and coalesces input per frame by default.
  - The Godot editor and .NET export add a second toolchain.

  Reconsider it if we find that frame-time stamping of per-packet events (`use_accumulated_input = false`) is accurate enough for the gain-map fit. That should be tested empirically with the replay harness.

### Alternative 2: Rust (Tauri 2 UI + wgpu renderer + `windows-rs` capture thread + Rust analysis)

- **Pros:** the best low-level control with no GC, and small binaries.
- **Why not first:**
  - Raw Accel integration is limited to `writer.exe` or a hand-built shim over `common/` headers (no read-back via the official wrapper).
  - The UI is split across Rust and TypeScript, which raises the contributor bar.
  - The statistics ecosystem is thin.
  - Bevy, if used, has frequent breaking releases.

### Not recommended

- **Python end-to-end:** GIL jitter in capture/render, painful packaging, and antivirus false positives.
- **Unity:** closed source with license-policy risk for an MIT project.
- **Electron:** heavyweight with no benefit.

### Open questions / spikes to schedule

1. Measure capture jitter in C# (allocation-free loop) against a native C loop at 8 kHz, using the per-batch QPC histogram.
2. Confirm whether `writer.exe` and the IOCTLs need elevation.
3. Check whether `setExtraInfo` coexists with other software that uses `ulExtraInformation`.
4. Run a one-day GameInput timestamp spike.
5. Decide the gain-model → Raw Accel projection (§1.6). This changes what "apply" means, so involve the product/analysis owners.

---

## Sources

**Raw Accel source (read directly, commit `53a7213` / tag v1.7.1)**
- Repo: <https://github.com/RawAccelOfficial/rawaccel> (moved from <https://github.com/a1xd/rawaccel>)
- IOCTL codes: <https://github.com/RawAccelOfficial/rawaccel/blob/master/common/rawaccel-io-def.h>
- IO functions and version check: <https://github.com/RawAccelOfficial/rawaccel/blob/master/common/rawaccel-io.hpp>, <https://github.com/RawAccelOfficial/rawaccel/blob/master/common/rawaccel-version.h>
- Settings structs and directional math: <https://github.com/RawAccelOfficial/rawaccel/blob/master/common/rawaccel-base.hpp>, <https://github.com/RawAccelOfficial/rawaccel/blob/master/common/rawaccel.hpp>
- Driver (WriteDelay, extra-info, timing): <https://github.com/RawAccelOfficial/rawaccel/blob/master/driver/driver.cpp>
- C++/CLI wrapper (`DriverConfig`, JSON keys) and project file: <https://github.com/RawAccelOfficial/rawaccel/blob/master/wrapper/wrapper.cpp>, <https://github.com/RawAccelOfficial/rawaccel/blob/master/wrapper/wrapper.vcxproj>
- writer: <https://github.com/RawAccelOfficial/rawaccel/blob/master/writer/Program.cs>
- GUI settings manager (auto-write on startup): <https://github.com/RawAccelOfficial/rawaccel/blob/master/grapher/Models/Serialized/SettingsManager.cs>
- FAQ (no persistence; writer usage): <https://github.com/RawAccelOfficial/rawaccel/blob/master/doc/FAQ.md>
- UI 2.0 branch (Avalonia, net8): <https://github.com/RawAccelOfficial/rawaccel/tree/userinterface>
- `dotnet5` branch (wrapper with `CLRSupport=NetCore`): <https://github.com/RawAccelOfficial/rawaccel/tree/dotnet5>

**Windows input**
- About Raw Input (standard vs buffered): <https://learn.microsoft.com/windows/win32/inputdev/about-raw-input>
- GetRawInputBuffer: <https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getrawinputbuffer>
- RegisterRawInputDevices (one window per class per process): <https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerrawinputdevices>
- GetMessageTime: <https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getmessagetime>
- Windows 11 background raw-input throttling (MS engineer post): <https://forums.blurbusters.com/viewtopic.php?p=95000>
- GameInput readings: <https://learn.microsoft.com/gaming/gdk/docs/features/common/input/overviews/input-readings>

**Engines and frameworks**
- Godot high-polling fix: <https://godotengine.org/article/fixing-high-polling-rate-mice-on-windows/>; source: <https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp>
- Godot `Input` / `InputEventMouseMotion` docs: <https://docs.godotengine.org/en/stable/classes/class_input.html>
- SDL3 raw input thread and timestamp interpolation: <https://github.com/libsdl-org/SDL/blob/main/src/video/windows/SDL_windowsrawinput.c>, <https://github.com/libsdl-org/SDL/blob/main/src/video/windows/SDL_windowsevents.c>
- winit Windows raw input: <https://github.com/rust-windowing/winit/blob/master/winit-win32/src/raw_input.rs>
- GLFW raw mouse motion: <https://www.glfw.org/docs/3.3/input.html>
- osu-framework (C#, net10, SDL3-CS): <https://github.com/ppy/osu-framework>
- Unity Runtime Fee cancellation: <https://unity.com/blog/unity-is-canceling-the-runtime-fee>
- C++/CLI on .NET Core: <https://learn.microsoft.com/dotnet/core/porting/cpp-cli>
- Avalonia licensing: <https://avaloniaui.net/FAQ>, <https://avaloniaui.net/legal>
- ScottPlot PolarAxis: <https://github.com/ScottPlot/ScottPlot/blob/main/src/ScottPlot5/ScottPlot5/Plottables/PolarAxis.cs>
- LiveCharts2: <https://github.com/beto-rodriguez/LiveCharts2>
- MathNet.Numerics: <https://github.com/mathnet/mathnet-numerics>, <https://www.nuget.org/packages/MathNet.Numerics>
- Tauri Windows installer: <https://v2.tauri.app/distribute/windows-installer/>

**Build and release**
- .NET support policy: <https://dotnet.microsoft.com/platform/support/policy/dotnet-core>
- GitHub runner image migration: <https://github.blog/changelog/2026-05-14-github-actions-upcoming-image-migrations>
- Velopack: <https://docs.velopack.io/packaging/overview>
- WiX OSMF: <https://docs.firegiant.com/wix/osmf/>
- SignPath Foundation: <https://signpath.org/>, <https://about.signpath.io/product/open-source>
- Azure Artifact Signing: <https://azure.microsoft.com/pricing/details/artifact-signing/>, <https://learn.microsoft.com/azure/artifact-signing/quickstart>
- SmartScreen / EV change: <https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options>
- PyInstaller packaging notes: <https://pydevtools.com/handbook/reference/pyinstaller/>

**Access note:** in this environment, direct fetches of learn.microsoft.com, godotengine.org and ph3at.github.io were blocked. Microsoft doc text was read from the public `MicrosoftDocs/sdk-api` and `MicrosoftDocs/win32` GitHub sources, and Godot/SDL/winit claims from their source code at the cited paths. Items marked [reported] rely on search summaries of the linked pages.
