# Research B — Aim data source for directional calibration

**Status:** research note (no product code) · **Date:** 2026-10-25 · **Author:** Research Agent B
**Question:** Where should OpenVector get its per-movement, per-direction endpoint data? It needs start, target, endpoint, time, along/across-axis error, path and corrective sub-movements. The choices are an existing aim trainer (KovaaK's, Aim Lab, others), a hybrid (raw mouse capture next to an existing trainer), or a trainer we build ourselves.

**Short answer:** No commercial trainer exports per-target position plus endpoint error through a documented, supported channel. KovaaK's exports per-kill rows and timestamped counters, but no geometry at all. Aim Lab writes per-play replay files that do contain crosshair and target poses, but the format is undocumented and reverse-engineered, sampled at about 60 Hz, and changes between versions. A hybrid (Windows Raw Input plus KovaaK's CSV) can recover a usable but confounded directional signal, and only on static-target flicking scenarios. **We recommend a small built-in 3D calibration trainer as the primary source.** It should record ground truth at the full polling rate, and the data model should sit behind a pluggable `MovementTrial` interface so that the hybrid, Aim Lab and osu! adapters can be added later as optional, lower-confidence sources.

Confidence labels used below: **[verified]** means checked in source code or primary docs during this research. **[reported]** means secondary sources or community reverse-engineering. **[unverified]** means my best understanding, not confirmed here.

---

## 0. Context: what PureVector actually is

- Corsair announced it on **2026-10-20** as a Corsair-exclusive feature of the **SABRE v3 PRO PureVector** mouse series, co-developed with **Aimlabs** (State Space Labs, Inc., formerly "Statespace") ([Corsair press release](https://corsair.com/newsroom/press-release/corsair-changes-the-game-with-purevector-a-science-based-personalized-aiming-technology-launching-exclusively-in-the-sabre-v3-pro-purevector-mouse-series), [TweakTown](https://www.tweaktown.com/news/114222/corsairs-sabre-v3-pro-mouse-lineup-features-brand-new-purevector-tech-that-improves-aiming-and-accuracy/index.html), [Lowyat](https://www.lowyat.net/2026/408044/corsair-sabre-v3-pro-ultralight/)). The sensor is rated at 42,000 DPI with polling up to 8 kHz.
- **Data collection:** the user runs a calibration sequence of about 5 minutes **inside the Aimlabs app**. Targets appear in different directions, and the software builds a per-direction profile of distance moved and relative accuracy, i.e. where the user overshoots and where they land accurately ([Corsair "PureVector" page](https://www.corsair.com/eu/hu/explorer/gamer/mice/pure/), [how-to](https://www.corsair.com/explorer/gamer/mice/how-to-use-purevector/)). ProSettings reports **8 directions** and five rounds of Aimlabs "Spidershot". Another review describes four stages with varying target speed and distance. Corsair itself gives no bin count. **[reported, sources disagree]**
- **Application:** the per-direction gain lives **in mouse firmware**, so no background software runs during play. That makes it anticheat-irrelevant by construction. It works for one DPI stage only, and the other stages are disabled while it is active ([Corsair page](https://www.corsair.com/eu/hu/explorer/gamer/mice/pure/)).
- **Claims:** "up to 81 ms / 33 % faster". KitGuru's reviewer could not reproduce this ([KitGuru review](https://www.kitguru.net/tech-news/featured-tech-reviews/carlosribeiro/corsair-sabre-v3-pro-purevector-cf-mouse-review/), [Brit Brief](https://britbrief.co.uk/tech/gadgets/corsair-sabre-v3-pro-purevector-review-software-hides-weaknesses.html)).
- **Takeaway for us:** PureVector does **not** infer directionality from arbitrary gameplay. It uses a **dedicated, controlled calibration task in a trainer that knows target positions**. That is the design we should copy.
- **Prior art to be aware of:** [FlickTune](https://github.com/kicedz/flicktune-releases), by Kice Djingov, appeared on 2026-10-21. It is closed source ("All rights reserved"), with a Windows installer only. It ships a built-in 3D "flick room" that turns like the user's game (game and DPI matched) and nudges [RawAccel](https://github.com/RawAccelOfficial/rawaccel) (MIT, signed driver) settings between flicks. It confirms the same design choice: a built-in 3D trainer plus a driver-level output stage.

---

## 1. KovaaK's (FPS Aim Trainer)

### 1.1 Stats CSV (`FPSAimTrainer/stats/*.csv`), [verified from parser source]

The game writes one file per finished challenge, named for example `1w6ts reload v2 - Challenge - 2026.05.22-19.00.33 Stats.csv` ([Steam discussion](https://steamcommunity.com/app/824270/discussions/0/2572068292096509989)). The layout below comes from the GPL-3.0 parser in [RefleK's `internal/runs/kovaaks/stats.go`](https://github.com/ARm8-2/refleks) and its tests. The file has three sections.

1. **Per-kill rows** (13 columns), header `Kill #,Timestamp,Bot,Weapon,TTK,Shots,Hits,Accuracy,Damage Done,Damage Possible,Efficiency,Cheated,OverShots`. Example rows:
   `1,18:59:34.330,target,BB Gun,0.000000s,1,1,1.000000,1.000000,1.000000,1.000000,false,0`
   - `Timestamp` is local wall-clock time, `HH:MM:SS.mmm`.
   - `TTK` is time since the previous kill, in seconds.
   - `Shots` / `Hits` are counted per kill.
   - `OverShots` is a count of overshoot events. KovaaK's own definition: roughly, the crosshair crossed the target and went past it **[unverified]**.
2. **Per-weapon summary** rows: shots, hits, damage, and sens settings.
3. **Key/value summary** (`Key:,value`), including: Score, Kills, Deaths, Fight Time, Avg TTK, Damage Done, Total Overshots, Hit Count, Miss Count, Distance Traveled, Scenario, Hash, Game Version, Challenge Start, Pause Count/Duration, Avg Target Scale, Avg Time Dilation, Input Lag, Max FPS, **Sens Scale, Sens Increment, Horiz Sens, Vert Sens, DPI, cm/360, FOV, FOVScale**, Resolution, Avg FPS, Date Played. Which keys appear depends on the game version.

**Verdict:** the CSV has **no target positions, no crosshair positions and no per-shot endpoints**. It gives only per-kill timestamps (ms resolution) and counts. It **does** give everything needed to convert mouse counts to degrees (sens scale, horizontal and vertical sens, FOV, cm/360). That is what makes the hybrid in §4 possible.

### 1.2 "Performances" file (`FPSAimTrainer/performances/`), [verified from parser source]

Newer builds write a **protobuf** sidecar per run. RefleK's [`performances.go`](https://github.com/ARm8-2/refleks) decodes it without a `.proto` file.

- **Header:** scenario name and hash, `ChallengeStartUTC`, schema version, and a challenge profile snapshot (time limit, bots, map, map scale, timescale, …).
- **Repeated timestamped events** of the following types: `shotsFired`, `shotsHit`, `shotsMissed`, `kills`, `deaths`, `overshots`, `reloads`, `pauseCount` (counts); `damageDone`, `damagePossible`, `score`, `playerDamageTaken`, `distanceTraveled`, `mbsPoints` (deltas); `targetSize`, `targetSpeed`, `randomSensScale` (values).

**Verdict:** this gives **per-shot timing** (fired / hit / missed with timestamps), which is better than the CSV. It still has **no positions** for crosshair or targets. The format is undocumented and could change without notice.

### 1.3 Replays, scenario editor, mods

- **Replays:** I found no public player-replay file format or parser. Version 3.3.0 added **character recording** in the scenario editor (F1 to start and stop), which records playback paths to drive bots or player spawns ([SteamDB 3.3.0 notes](https://steamdb.info/patchnotes/12218293)). That is an authoring tool, not a telemetry export. **[reported]**
- **Scenario editor / KovaaK's Map Creator:** scenarios are text `.sce` files with embedded `.json` maps ([KovaaK's wiki — KMC FAQ](https://wiki.kovaaks.com/en/home/KovaaK'sMapCreator/faq)). The editor exposes spawn areas, bot profiles, sizes and timings, so a scenario's **spawn geometry is knowable** (useful in §4). It does not expose runtime target positions.
- **SDK / Lua / plugin API:** none found. No public modding or scripting API exports runtime state. **[searched, not found]**
- **Community tools:**
  - Stats viewers that parse the CSV: [nahuef/statsViewer](https://github.com/nahuef/statsViewer) (Go), [Bredgren/KovaakStats](https://github.com/Bredgren/KovaakStats), [VoltaicHQ/Progress-Sheet-Updater](https://github.com/VoltaicHQ/Progress-Sheet-Updater), [KovOBS](https://github.com/Bassel-Bakr/KovOBS), and others.
  - Two tools go further:
    - **[RefleK's](https://github.com/ARm8-2/refleks) (GPL-3.0, Go + Wails + React, Windows).** A Raw Input tracker (`RegisterRawInputDevices`, `RIDEV_INPUTSINK`, hidden message window) records accumulated deltas during KovaaK's and aligns them with the CSV and performances file via `Challenge Start`. A **heuristic** `targetInference.ts` then *guesses* target paths from kills, hit fractions and cursor kinematics. Its own comments say it relies on hand-tuned constants and is not validated against ground truth. Timestamps come from `time.Now()` (ms resolution), not QPC.
    - **[Clickist/Aiming-cookie](https://github.com/Clickist/Aiming-cookie) (KovaaK's AI coach).** Its progress log describes a KovaaK's "telemetry" path that resolves **memory offsets**, i.e. it reads target and camera state from the game process. **That violates our hard constraint (no memory reading)**. Even though KovaaK's has no kernel anticheat that I know of, it is fragile and almost certainly contrary to the EULA. We must not copy it.
- **Does anything give per-target position and endpoint error?** **No**, not through any supported channel. Only memory reading (excluded) or heuristic inference (unreliable) gets near it.

---

## 2. Aim Lab / Aimlabs

- **Owner:** State Space Labs, Inc. ("Statespace"), now branded **Aimlabs**. It is a Unity / IL2CPP game. Riot Games took a minority stake in 2022, and the company raised about $15M in January 2025 per SEC-based listings ([Wikipedia](https://en.wikipedia.org/wiki/Aimlabs), [Owler](https://www.owler.com/company/statespace), [CB Insights](https://www.cbinsights.com/investor/statespace)). I found no acquisition. It is PureVector's calibration host.
- **Creator Studio / Workshop:** an in-app editor for maps, tasks, bot behaviour and spawning, shared on the Steam Workshop ([Worthplaying 2021](https://worthplaying.com/article/2021/1/25/news/123802-aim-lab-launches-creator-studio-public-beta-so-you-can-train-to-be-a-better-gamer-trailer/), [Aimlabs Workshop article](https://aimlabs.com/articles/aimlabs/it-came-from-the-workshop-trending-playlists-to-play-in-aimlabs/)). I found **no public SDK, Unity mod kit or scripting API** that exports runtime data. The only community "mods" are IL2CPP patches such as skin unlockers, which plainly break the ToS.
- **Local data [reported]:**
  - **Score database:** a SQLite file named `klutch.bytes` under `%USERPROFILE%\AppData\LocalLow\Statespace\aimlab_tb\` (newer builds: `...\Users\<id>\LocalDB\Klutch.bytes`). It holds per-play score, accuracy, `timePerKill` and a performance JSON. It is used by [Voltaic's sheet updater](https://github.com/VoltaicHQ/Progress-Sheet-Updater), [MuhAimLabScoresViewer](https://github.com/M41AKore/MuhAimLabScoresViewer) and SquadOV.
  - **Per-play replay:** `Replay/<playId>.avro`, schemaless Avro. [Aiming-cookie's reverse-engineering log](https://github.com/Clickist/Aiming-cookie/blob/main/docs/PROGRESS.md) (2026-10-12) and the [aimlab-benchmarks-viewer data inventory](https://github.com/yohannesakd/aimlab-benchmarks-viewer/blob/main/docs/aimlabs-data-inventory.md) report that it holds the **crosshair/aim-rotation trajectory, target pose time series and an event stream** (16 event types; `TARGET_HIT.receiverID` joins `TargetData.id`).
  - **Caveats:**
    - Crosshair sampling is about **60 Hz** (adaptive in 2.0: written only while moving).
    - The **schema broke in 2.0.6.1** (`GameplayData` → `MultiplayerGameplayData`).
    - Replays are pruned by an LRU cache.
    - Event codes are only partly mapped.
  - In principle this is the **only commercial source with real per-target geometry**.
- **Cloud API:** `api.aimlab.gg/graphql` and `api.aimlabs.com` are **undocumented**. They expose mostly per-run aggregates, and some fields need auth. `PlayManifest.replayUrl` points to the signed Avro replay ([inventory doc](https://github.com/yohannesakd/aimlab-benchmarks-viewer/blob/main/docs/aimlabs-data-inventory.md)). Building on this would mean scraping an unofficial API.
- **ToS:** I could not retrieve the Aimlabs Terms/EULA text in this environment (the site was unreachable). Typical game EULAs forbid reverse engineering and unofficial API access. **Treat an Aimlabs replay adapter as legally uncertain and do not ship it in core.** At most, offer it as an opt-in community plugin that only reads files on the user's own disk, and only after someone reads the current EULA. **[unverified, must check before any work]**
- **Practical blockers even if the ToS allows it:**
  - 60 Hz is too coarse for sub-movement segmentation. At 8 kHz polling, a 150 ms flick is about 9 samples at 60 Hz versus about 1,200 at 8 kHz.
  - Version churn breaks the schema.
  - We do not control the task design: direction balance, amplitude, or "no corrections" instructions.

---

## 3. Other trainers

| Trainer | Per-target export? | Notes |
|---|---|---|
| **3D Aim Trainer** (Steam/SteelSeries) | None found | Shows 25+ in-app stats; deeper stats are on its roadmap ([Steam](https://store.steampowered.com/app/1100990/)) |
| **Aiming.Pro** (browser) | None found | In-app analysis covers "under/over shoot tendencies" ([about](https://aiming.pro/about)), which is interesting but not exported |
| **Aimbeast** | None found | Leaderboards and progress graphs only ([vgdb](https://www.vgdb.co/games/aimbeast)) |
| **osu!** (stable/lazer) | **Yes, de facto** | `.osr` replays hold LZMA-compressed frames `(Δt, x, y, keys)`, and the `.osu` beatmap gives every hit-object position and time ([osr spec](https://osu.ppy.sh/wiki/osu!_File_Formats/Osr_(file_format)), [osrparse](https://pypi.org/project/osrparse/)). This is the only open-format source with **true target geometry**. Caveats: 2D absolute cursor (many players use tablets), rhythm-imposed timing (not self-paced Fitts), beatmap-driven direction distribution, frames often about 60 Hz or event-driven, and desktop-cursor mapping instead of yaw/pitch. Useful only as an optional 2D research adapter. |
| **NVIDIA FirstPersonScience (FPSci)** | Yes (SQLite `.db` results per session) | A research FPS trainer ([GitHub](https://github.com/NVlabs/FPSci), [paper](https://arxiv.org/pdf/2202.06429)), Windows, G3D engine. **License is CC BY-NC-SA 4.0 (non-commercial, share-alike), so it is not MIT-compatible.** We cannot fork it, but it is the best reference design for experiment config and logging. |

---

## 4. Hybrid: Windows Raw Input capture while playing KovaaK's / Aim Lab

### 4.1 Legality, ToS and anticheat

- **Mechanism.** A separate process calls `RegisterRawInputDevices` (usage page 0x01, usage 0x02) with `RIDEV_INPUTSINK` and receives `WM_INPUT` in the background. Do **not** set `RIDEV_NOLEGACY` or `RIDEV_CAPTUREMOUSE`, so the game is unaffected. This is a passive, documented OS facility, the same one overlays and input-visualizers use ([MS docs: Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input)). It does not touch the game process, memory or screen, so it **fits our anticheat constraint**.
- **Anticheat.** KovaaK's and Aimlabs ship no kernel anticheat that I know of **[unverified]**. Either way, a raw-input listener is not something anticheat flags.
- **Files.** Reading the user's own KovaaK's CSV and performances files is plainly fine. Aimlabs replay parsing is the legally uncertain part (§2).
- **Bonus.** The same passive listener is exactly what we would use in calibration sessions anyway.

### 4.2 How much directional signal can be recovered without target positions

**What we know:**
- **Counts.** Exact raw counts (dx, dy) at the full polling rate, if we timestamp with QPC. We must not use `GetMessageTime` or ms clocks like RefleK's does.
- **Counts → degrees.** The CSV keys (Horiz/Vert Sens, Sens Scale, FOV) let us convert counts to yaw/pitch. KovaaK's maps counts linearly to angle, so the trace is already in the game's angular space.
- **Shot times.** Kill timestamps (CSV, ms, wall clock) and per-shot fired/hit/miss timestamps (performances file).

**What we do not know:** where the target was.

**Reconstruction recipe (static-target flicking scenarios only, e.g. 1w6ts, Spidershot-like, "1wall" grids):**

1. **Segment** the trace into movements using velocity: onset and offset at about 5–10 % of peak speed, with a minimum dwell between movements. Split each movement into a **primary sub-movement** (up to the first velocity minimum or zero crossing after peak) plus **corrective sub-movements** (Meyer et al. 1988; Elliott et al. 2001).
2. **Proxy the target.** For movements ending in a **hit** shot, the click position lies within one target radius `r` of the target centre. Use **start = previous hit position** and **target ≈ click position**.
3. **Directional bias** = primary-sub-movement endpoint projected on the start→click axis, divided by |click − start|. Values above 1 are overshoot; below 1 is undershoot. The across-axis component is the lateral error.
4. **Discard** misses, multi-shot kills, tracking segments and pauses.

**Why this partly works:**
- The *final* endpoint always ends on target because corrections absorb any gain error. The *primary* sub-movement, however, carries the open-loop gain error, and the click gives a good estimate of the intended target.
- Target-proxy error is about r/2 SD per axis (uniform disk). For typical r/A ≈ 0.05–0.10 that is 2.5–5 % of amplitude. Added in quadrature to motor noise, it inflates σ only modestly.

**Why it is confounded (honest list):**
1. **No direction or amplitude design.** Scenario spawn patterns are uneven; 1-wall scenarios are mostly horizontal with little vertical extent. Some angle bins get few trials, and **amplitude is correlated with direction**. The well-known *range effect* (short movements overshoot, long ones undershoot) then shows up as a fake direction effect unless amplitude is modelled as a covariate.
2. **Unknown intent.** On large targets players aim for the near edge, which biases toward undershoot. The bias is roughly isotropic, so it mostly lands in the mean term, but not entirely.
3. **The start point is not a fixation point.** Users sometimes pre-move before the previous shot or chain flicks, which breaks segmentation.
4. **Segmentation is ambiguous** for fast blended flicks. This affects any source, but without ground truth we cannot validate the segmenter.
5. **Clock alignment.** CSV wall-clock ms versus our QPC needs an offset estimate. Kill↔click matching makes it workable (±few ms), but the performances file's timestamp units are unconfirmed.
6. **Tracking, reactive and moving-target scenarios give essentially no recoverable endpoint signal.**

### 4.3 Is about 5 % per-direction bias detectable?

Back-of-envelope, with stated assumptions:

- **Assumption:** the along-axis SD of the primary sub-movement endpoint for fast flicks is σ ≈ 8–12 % of amplitude. This ballpark is consistent with Meyer et al. (1988) and typical Fitts effective-width data, and should be confirmed in our pilot.
- **Built-in trainer (ground truth):**
  - **Independent bins:** with 16 independent bins, telling one bin from the mean at a 5 % difference (α = 0.05, power 0.8) needs n ≈ 2·(1.96+0.84)²·σ²/δ² ≈ **60 trials per bin** at σ = 10 %, about 1,000 flicks.
  - **Smooth model:** with a low-order Fourier model of g(θ) (mean + 1st + 2nd harmonics, 5 parameters), each coefficient has SE ≈ σ·√(2/N). A 5 % harmonic amplitude is then detectable with **N ≈ 150–300 flicks**, about 5–8 minutes. That is consistent with PureVector's ~5-minute calibration.
- **Hybrid on a static flicking scenario:**
  - σ_eff ≈ √(σ² + (r/2)²) ≈ 11–12 %, and roughly 20–40 % of movements are lost to misses, chains or segmentation rejects.
  - Uneven direction coverage widens some coefficients' SE by maybe 1.5–3×.
  - Statistically, 5 % is therefore **detectable with roughly 2–4× more play** (about 20–30 minutes of a suitable scenario).
  - **However, the systematic confounds above are of the same order (a few %) and cannot be checked without ground truth.**
  - **Conclusion:** the hybrid is plausible as a *"passive drift monitor / sanity check"* but **not trustworthy as the primary calibration source**.

---

## 5. Open-source trainers to fork or reuse

| Project | License | Stack | Activity | Raw input / high-poll | Fit for calibration trainer |
|---|---|---|---|---|---|
| [erkkon/OpenAimTrainer](https://github.com/erkkon/OpenAimTrainer) | **Apache-2.0** (MIT-compatible with NOTICE retained) | Godot (GDScript), 2D + 3D | ~88 commits; repo updated 2026-10 | Engine-level; no sens matching found | Usable as a *starting point / reference*; small |
| [Nokorpo/LibreAim](https://github.com/Nokorpo/LibreAim) | **GPL-3.0** | Godot 4 | GitHub archived 2025-08-27; moved to Codeberg | Engine-level | Best-known OSS FPS trainer, but **GPL is incompatible with our MIT license**; ideas only |
| [idoraz1/ValTrainer](https://github.com/idoraz1/ValTrainer) | **GPL-3.0+** | Godot 4.7 .NET (C#) | Active (2026-10) | Uses raw input; matches Valorant 0.07°/count × sens and 103° FOV | Good example of cm/360 matching; GPL, so ideas only |
| [ahmadhayyan/project-aim](https://github.com/ahmadhayyan/project-aim) | (check) | Godot | Low | — | Minimal |
| [NVlabs/FPSci](https://github.com/NVlabs/FPSci) | **CC BY-NC-SA 4.0** | C++ / G3D, Windows | Mature | Yes; latency-validated | Best research design; **license excludes it** |
| [ARm8-2/refleks](https://github.com/ARm8-2/refleks) | **GPL-3.0** | Go / Wails | Active | Raw Input `INPUTSINK` (ms timestamps) | Reference for the hybrid adapter only; do not copy code |
| Web three.js clones (e.g. [WebClicker](https://github.com/SanGraphic/WebClicker), [aim-trainer-web](https://github.com/jiao329/aim-trainer-web)) | varies | Browser, Pointer Lock | — | `requestPointerLock({unadjustedMovement:true})` gives raw-ish deltas, but events are coalesced and timing is browser-limited | Not adequate for 4–8 kHz kinematics |

**Engine notes:**
- **Godot (MIT).** Uses Raw Input on Windows in captured mode. Godot **4.7.2** fixed a severe high-polling-rate congestion bug: before the fix, 8 kHz could drop below 1 FPS. Raw input reads are now buffered in `DisplayServerWindows::process_raw_input()`, with legacy motion limited to once per frame ([Godot blog: Fixing high polling rate mice on Windows](https://godotengine.org/article/fixing-high-polling-rate-mice-on-windows/)). Scripts still see **per-frame-accumulated** motion events without per-packet hardware timestamps.
- **Logging thread.** For any engine, put the **logging path on our own Raw Input thread**: `GetRawInputBuffer` in batches, a QPC timestamp at receipt, and a lock-free ring buffer. Rendering consumes the same counts per frame.
- **Engine choice.** Alternatives are Bevy (MIT/Apache, Rust), raylib (zlib) or plain D3D11 + C++/Rust. The trainer needs only spheres, a crosshair, a skybox/grid and precise camera math, so engine choice is secondary. The engine's input-to-camera mapping must be **exactly linear** (no smoothing or acceleration), and the logger must be independent of frame rate.
- **Fork or write?** Writing a **fresh, minimal MIT trainer** is low-effort and avoids licence entanglement. OpenAimTrainer (Apache-2.0) is the only listed fork candidate whose license is clean.

### 5.1 2D cursor trainer vs 3D FPS view

- Game sensitivity maps **counts → degrees of yaw/pitch** linearly (e.g. yaw = counts × sens × k_game). On screen, a target at angle φ off-centre appears at `tan(φ)/tan(FOV/2)` of half-width. That is **not linear**: at 103° horizontal FOV, a 40° target sits at 0.67 of half-width, whereas linear scaling would put it at 0.78. A 2D trainer that moves a cursor linearly in pixels therefore presents **different visual-to-motor geometry** from the game. Users' muscle memory, especially for large flicks, is tuned to the angular mapping.
- In 3D, the **world stays fixed and the view rotates**, which matches the game's visual flow (optic flow, the target moving toward the crosshair). In a 2D trainer the cursor moves over a static background, which is the desktop-pointing paradigm the HCI literature studies (§6) and not FPS aiming. Looser et al. (2005) and FPSci show FPS-view Fitts studies are valid, but they are a different task from desktop pointing.
- The **calibrated quantity is in counts**: the gain map g(θ) is applied to (dx, dy) counts by firmware or a driver. With matched cm/360 and FOV, a 3D trainer measures bias in count space while reproducing the in-game visual context. A "2D window with simulated camera via cm/360" is **just a 3D renderer with a degenerate projection**, so there is no real simplicity gain. Use the true perspective projection.
- **Recommendation:** use a 3D view with game-matched yaw-per-count (per-game presets: Valorant 0.07°, CS2 0.022°, …), the user's FOV, and their resolution and aspect. Include an optional "desktop 2D" mode for users who want to calibrate desktop pointing. Keep pitch and yaw factors separate in case a game has a vertical multiplier (e.g. KovaaK's Horiz/Vert sens, R6 vertical multiplier).

---

## 6. Academic grounding: directional anisotropy

- **Gordon, Ghilardi, Cooper & Ghez (1994).** *Accuracy of planar reaching movements. II. Systematic extent errors resulting from inertial anisotropy.* Exp Brain Res 99:112–130. [doi:10.1007/BF00241416](https://dx.doi.org/10.1007/BF00241416), [UMN abstract](https://experts.umn.edu/en/publications/accuracy-of-planar-reaching-movements-ii-systematic-extent-errors/).
  - Movements in low-inertia directions (perpendicular to the forearm) have higher acceleration and **overshoot**. Movements in high-inertia directions undershoot.
  - This is the closest biomechanical rationale for a direction-dependent gain map. Part I (same issue, pp. 97–111, [doi:10.1007/BF00241415](https://link.springer.com/article/10.1007/BF00241415)) shows that direction and extent variability are independent, which supports modelling along-axis and across-axis error separately.
- **Boritz, Booth & Cowan (1991).** *Fitts's law studies of directional mouse movement.* Proc. Graphics Interface '91, 216–223. [doi:10.20380/GI1991.28](https://graphicsinterface.org/?p=2324). An early demonstration that mouse movement time depends on direction.
- **Whisenand & Emurian (1995/1999).** *Analysis of cursor movements with a mouse* / *Effects of angle of approach on cursor movement with a mouse.* CHI '95 short paper ([CHI '95](https://chi1995.chistatic.hosting.acm.org/proceedings/shortppr/tgw_bdy.htm)); Computers in Human Behavior 15(1):85–103, doi:10.1016/S0747-5632(98)00036-3. Eight approach angles; angle had a **significant effect on movement time**, which classical Fitts models ignore.
- **MacKenzie & Buxton (1992).** *Extending Fitts' law to two-dimensional tasks.* CHI '92. Defines target width along the approach angle; foundation for direction-aware ID.
- **Soukoreff & MacKenzie (2004).** *Towards a standard for pointing device evaluation…* IJHCS 61(6):751–789 ([PDF](https://yorku.ca/mack/p113-soukoreff.pdf)). Effective width We = 4.133·SDx and throughput TP = IDe/MT. We should compute these **per direction bin** on the along-axis projection.
- **ISO 9241-9:2000 / ISO 9241-411:2012.** The multidirectional (circular) tapping task. Its target ring gives balanced directions by construction, and it is the natural template for our calibration layout.
- **Wobbrock, Shinohara & Jansen (2011).** *The effects of task dimensionality, endpoint deviation, throughput calculation, and experiment design on pointing measures and models.* CHI '11, doi:10.1145/1978942.1979270. Covers how to project endpoints onto the task axis and why 2D tasks need bivariate endpoint deviation.
- **Meyer, Abrams, Kornblum, Wright & Smith (1988).** *Optimality in human motor performance: ideal control of rapid aimed movements.* Psych Review 95(3):340–370. The stochastic optimized-submovement model, the basis for primary versus corrective sub-movement segmentation. See also **Elliott, Helsen & Chua (2001)**, *A century later: Woodworth's two-component model of goal-directed aiming*, Psych Bull 127(3):342–357.
- **Looser, Cockburn & Savage (2005).** *On the validity of using first-person shooters for Fitts' law studies.* People and Computers XIX. Fitts' law holds in an FPS view.
- **FPS-specific work:** Boudaoud, Spjut, Kim et al., *FirstPersonScience* (SIGGRAPH '22 Talks; [arXiv 2202.06429](https://arxiv.org/pdf/2202.06429)). Recent KAIST/NVIDIA work on modelling aim-and-shoot behaviour (Yoon, Moon, Boudaoud, Spjut, Frosio, Lee, Kim 2025, [DBLP](https://dblp.uni-trier.de/pid/24/2928.html)) is relevant for the speed/distance extensions **[titles not verified here]**.
- **Error-rate modelling** without known angles: Yamanaka et al., [arXiv 2302.03103](https://arxiv.org/pdf/2302.03103). Notes that angle affects mouse pointing error.

**Implication for the model:**
- The literature supports a **smooth, low-order directional effect**, with axial and biomechanical (inertia) components. That favours a Fourier or von Mises basis for g(θ) over 64 free bins.
- It also supports **amplitude dependence** through the range effect and speed.
- Fit bias on **primary sub-movement endpoints**, not final click points.
- Report per-direction **effective throughput** alongside bias.

---

## 7. Recommendation

**Primary source: a built-in calibration trainer.** It is MIT, Windows, a native 3D view with game-matched yaw/pitch-per-count and FOV, and it logs via our own Raw Input thread with QPC timestamps at the native polling rate (up to 8 kHz).

- **Design:** an ISO 9241-411-style **multidirectional flick protocol**. Fixation on a centre or previous target, then a target at a controlled direction θ (balanced across 16–64 bins, randomised order) and controlled angular amplitude (e.g. 3 levels) and size.
- **Mode switch:** "first-shot counts" (primary endpoint) versus "click-to-kill" (full correction sequence). Optional speed and time-pressure conditions.
- **Ground truth for every trial:** start, target, endpoint, path, sub-movements.
- **Anticheat:** irrelevant. The trainer is our own process, and no game is involved.

**Pluggable `DataSource` interface** so other sources can feed the same fitter, each tagged with a **confidence/provenance level**:

1. `builtin` (ground truth): core.
2. `kovaaks-hybrid` (raw input + CSV + performances, static flicking scenarios only, inferred targets): optional, read-only, flagged "inferred". Good as a passive drift monitor.
3. `osu-replay` (2D, true geometry): optional research adapter.
4. `aimlabs-replay` (true geometry, 60 Hz): **only as an out-of-tree community plugin, and only after a EULA review**.

Never memory reading.

### 7.1 `MovementTrial` record (proposed, source-agnostic)

```text
MovementTrial
  # identity & provenance
  trial_id, session_id, source_kind {builtin|kovaaks_hybrid|osu|aimlabs|...}
  source_version, scenario_id/hash, protocol_id
  target_truth {measured|inferred|unknown}, inference_method, confidence (0–1)
  # input/device context
  device {vid, pid, name}, polling_hz_nominal, polling_hz_measured, dpi (if known)
  mapping {yaw_deg_per_count, pitch_deg_per_count, fov_h_deg, fov_scaling, resolution, aspect}
  active_gain_map_id (the g(θ) in effect during the trial, or "identity")
  # geometry (all angular, deg, in the view frame at movement onset; plus raw counts)
  start {yaw, pitch}, target {yaw, pitch, radius_deg, shape}, (optional target velocity)
  amplitude_deg, direction_theta_deg (atan2 of target−start, 0 = right, CCW+, screen-up positive)
  # outcome
  endpoint_final {yaw, pitch}, endpoint_primary {yaw, pitch}
  error_along_final, error_across_final, error_along_primary, error_across_primary (deg and fraction of A)
  hit (bool), shots_fired, clicks [{t, yaw, pitch, hit}]
  # timing (µs, monotonic QPC-derived)
  t_stimulus, t_onset, t_peak_velocity, t_primary_end, t_final_click
  reaction_time, movement_time, primary_duration, peak_speed_deg_s
  submovements [{t_start, t_end, displacement_counts, peak_speed}], n_corrections
  # raw path (optional, compressible)
  path_ref → [{t_us, dx_counts, dy_counts, buttons}] (raw, unaccumulated, per packet)
  # flags
  excluded_reason (miss | pause | chained | segmentation_ambiguous | outlier), notes
```

**Design rules:**
- Store **raw counts** and derive angles through `mapping`, so trials stay re-analysable after mapping fixes.
- Keep `target_truth` and `confidence` on every record, so the fitter can down-weight inferred trials or fit them separately.
- Use a **monotonic µs clock** with a recorded offset to wall clock, so external sources can be aligned.
- Version the schema from day one.

---

### Sources (main)

- PureVector: [Corsair press release](https://corsair.com/newsroom/press-release/corsair-changes-the-game-with-purevector-a-science-based-personalized-aiming-technology-launching-exclusively-in-the-sabre-v3-pro-purevector-mouse-series) · [Corsair PureVector page](https://www.corsair.com/eu/hu/explorer/gamer/mice/pure/) · [How to use PureVector](https://www.corsair.com/explorer/gamer/mice/how-to-use-purevector/) · [ProSettings review](https://prosettings.net/reviews/corsair-sabre-v3-pro/) · [KitGuru review](https://www.kitguru.net/tech-news/featured-tech-reviews/carlosribeiro/corsair-sabre-v3-pro-purevector-cf-mouse-review/) · [TweakTown](https://www.tweaktown.com/news/114222/corsairs-sabre-v3-pro-mouse-lineup-features-brand-new-purevector-tech-that-improves-aiming-and-accuracy/index.html)
- KovaaK's: [RefleK's (parser + raw input tracker)](https://github.com/ARm8-2/refleks) · [Steam stats CSV thread](https://steamcommunity.com/app/824270/discussions/0/2572068292096509989) · [KMC FAQ](https://wiki.kovaaks.com/en/home/KovaaK'sMapCreator/faq) · [SteamDB 3.3.0](https://steamdb.info/patchnotes/12218293) · [statsViewer](https://github.com/nahuef/statsViewer) · [Aiming-cookie](https://github.com/Clickist/Aiming-cookie)
- Aimlabs: [Wikipedia](https://en.wikipedia.org/wiki/Aimlabs) · [Creator Studio beta](https://worthplaying.com/article/2021/1/25/news/123802-aim-lab-launches-creator-studio-public-beta-so-you-can-train-to-be-a-better-gamer-trailer/) · [aimlabs data inventory](https://github.com/yohannesakd/aimlab-benchmarks-viewer/blob/main/docs/aimlabs-data-inventory.md) · [Voltaic Progress-Sheet-Updater](https://github.com/VoltaicHQ/Progress-Sheet-Updater)
- Others: [osu! .osr format](https://osu.ppy.sh/wiki/osu!_File_Formats/Osr_(file_format)) · [osrparse](https://pypi.org/project/osrparse/) · [FPSci](https://github.com/NVlabs/FPSci) · [FlickTune](https://github.com/kicedz/flicktune-releases) · [RawAccel](https://github.com/RawAccelOfficial/rawaccel)
- Engines/input: [Godot high-polling fix](https://godotengine.org/article/fixing-high-polling-rate-mice-on-windows/) · [MS Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input) · [OpenAimTrainer](https://github.com/erkkon/OpenAimTrainer) · [LibreAim](https://github.com/Nokorpo/LibreAim) · [ValTrainer](https://github.com/idoraz1/ValTrainer)
- Literature: see §6 (Gordon et al. 1994; Boritz et al. 1991; Whisenand & Emurian 1999; MacKenzie & Buxton 1992; Soukoreff & MacKenzie 2004; ISO 9241-9/411; Wobbrock et al. 2011; Meyer et al. 1988; Elliott et al. 2001; Looser et al. 2005).

### Open items / uncertainty

- The Aimlabs and KovaaK's EULA texts were not retrievable here, so both need a manual read.
- PureVector's exact bin count and task are not confirmed (sources say 8 directions / Spidershot).
- The KovaaK's `OverShots` definition and the performances timestamp unit are unconfirmed.
- The σ ≈ 10 % primary-endpoint SD is an assumption to validate in a pilot.
- I did not verify OpenAimTrainer's Godot version or recent commit history, or project-aim's license.
