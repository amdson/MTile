# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A 2D platformer in C#/MonoGame built around "the terrain IS the weapon": the player slashes, stabs, pulses, and erupts blocks to reshape a chunked tile world while moving through it. Fixed timestep of 60 fps (`Simulation.FixedDt`).

**Read [CODEBASE_OVERVIEW.md](CODEBASE_OVERVIEW.md) first** — it is the authoritative architecture doc (sim/ECS/rollback, physics, character FSMs, combat, world/tiles, animation, drawing). This file covers build/run/test mechanics, project layout, and the conventions that bite hardest. Design notes and roadmaps live in [Plans/](Plans/).

## Project layout

Game source lives **at the repo root** (`Character/`, `Physics/`, `World/`, `Entities/`, `Drawing/`, `Game1.cs`, etc.). It is compiled into the `MTile.Core` library and reused by three hosts:

`Character/` is split into `Input/`, `Movement/`, `Action/`, `Corrector/`, and `Sensing/` (with
`PlayerCharacter.cs` + `SimFrames.cs` at its root) — see the table in
[CODEBASE_OVERVIEW.md](CODEBASE_OVERVIEW.md#character-character). **Every file there is still
`namespace MTile;`**: the subdirs are for navigation only, so a file can move between them without
touching a single `using`. Keep it that way — folder-scoped namespaces would turn a rename into a
codebase-wide edit.

| Project | Role |
|---|---|
| `MTile.Core.csproj` (root) | The library. Globs the root `.cs` files; excludes `MTile.Tests/`, `MTile.Desktop/`, `MTile.Web/`. Compiles against `MonoGame.Framework.DesktopGL`. |
| `MTile.Desktop/` | `WinExe` desktop host (`AssemblyName` = `MTile`). DesktopGL. The normal way to run the game. |
| `MTile.Web/` | Blazor WebAssembly host via the **KNI** MonoGame variant (`nkast.Xna.Framework`). Does **not** ProjectReference Core — KNI is a different assembly identity, so it re-globs the same root `.cs` and compiles them a second time. Also excludes `MTile.Rtc/`; the browser transport is `wwwroot/mtileRtc.js`. **Now runtime-verified and deployed** — browser PvP works over WebRTC with Firestore room codes. See `Plans/WEB_PVP.md` (operator guide) and `Plans/INTERNET_READY_PLAN.md` (what's left). |
| `MTile.Tests/` | xUnit. ProjectReferences `MTile.Core`. |
| `MTile.Probe/` | Headless animation CLI — the clip-authoring workhorse (`list/digest/diff/new/addkey/ik/contact/rot/retime/stretch/…`, `--rig`). Writes clips in place. |
| `MTile.Demo/` | Windowed tooling: animation editor, sprite-bind editor + art import (`--bind`), take viewer (`--load Takes/*.take.json`), reference-arc editor. |
| `MTile.Rtc/` | WebRTC transport for the rollback netcode. |
| `MTile.Bench/`, `MTile.FxLab/` | Perf harness; shader/VFX lab. |

`MTile.sln` contains Core + Desktop + Tests. `MTile.Web.sln` is separate **by design** — including Web would pull KNI's emcc native build into every desktop build.

Because the same source compiles under both DesktopGL and KNI, **don't use APIs that exist in only one variant.** A change that builds via `MTile.Core` can still break the web build.

## Commands

```bash
# Build / run the desktop game
dotnet build MTile.sln
dotnet run --project MTile.Desktop          # launches the game window

# Tests
dotnet test MTile.Tests/MTile.Tests.csproj
dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~SnapshotRoundTrip"   # single class/test
dotnet watch test --project MTile.Tests/MTile.Tests.csproj   # re-run on change

# Web build (KNI/Blazor WASM)
dotnet build MTile.Web/MTile.Web.csproj
dotnet run --project MTile.Web              # dev server (interpreted — slow, fine for logic)

# Web publish → GitHub Pages (https://amdson.github.io/mtile/)
pwsh scripts/publish-web.ps1                # -NoPush / -SkipBuild / -SiteRepo

# Performance (see Plans/PERF_AUDIT.md)
dotnet run -c Release --project MTile.Bench              # sim + animation-solver µs/frame
dotnet run -c Release --project MTile.Bench -- --check   # diff vs baseline.txt; exit 1 on a ~1.5x regression
dotnet run -c Release --project MTile.Bench -- --save MTile.Bench/baseline.txt   # re-baseline
dotnet run -c Release --project MTile.Bench -- --corrector             # corrector QP share of a sim tick
node MTile.Web/smoke/qp_bench.js http://127.0.0.1:8080/                # ...the same solve in the browser (AOT publish required)

# Audio assets: hand-cut slices in Audio/raw → game-ready Ogg in Assets/Sounds
pwsh scripts/build-sfx.ps1 -Name tile_break -DryRun   # print the plan + filter chain
pwsh scripts/build-sfx.ps1 -Name tile_break           # → tile_break_01.ogg, _02, …
pwsh scripts/build-sfx.ps1 -Loop -Name wall_scrape    # loops: no silence trim, no end fade
pwsh scripts/sync-sounds.ps1                          # wire clips into the pipeline + manifest
```

**AOT is mandatory for a playable web build: 2.7 fps interpreted vs ~40 fps AOT.** The csproj still
has `<RunAOTCompilation>false</RunAOTCompilation>`; `scripts/publish-web.ps1` overrides it with
`-p:RunAOTCompilation=true`. **A plain `dotnet publish -c Release` therefore ships the 2.7 fps
build** — always publish via the script. AOT needs `dotnet workload install wasm-tools`; the first
compile takes ~15 min and the output wwwroot is ~49 MB.

Browser smoke tests (Playwright, headless Chromium + SwiftShader) live in `MTile.Web/smoke/`:
`web_smoke.py` (boot + console errors), `pvp_move.py` (two *separate* browser processes — not two
tabs, since a backgrounded tab's rAF throttles and the peer stall-caps — driving the manual
copy/paste lobby and pixel-diffing that input mirrored). Setup in `MTile.Web/smoke/README.md`.

`build-sfx.ps1` needs ffmpeg (`winget install Gyan.FFmpeg`, then restart the shell). It outputs the
format `Plans/AUDIO_ASSET_LIST.md` specifies — mono, 22.05 kHz, Ogg Vorbis, **loudness-matched by
LUFS** (peak normalization does not match perceived loudness, and round-robin variants that differ
in loudness read as a bug rather than as variety). It deliberately does *not* bake in pitch, gain,
pan, or variant selection: those are runtime concerns (`Plans/AUDIO_PLAN.md` §5), and doing both is
the classic mistake.

**Slotting a clip in: `build-sfx.ps1` → `sync-sounds.ps1` → build.** `sync-sounds.ps1` scans
`Assets/Sounds/*.ogg` and regenerates the `GENERATED SOUNDS` region of `Content/Content.mgcb`
plus `Audio/SoundManifest.g.cs`; both are generated, don't hand-edit them. The clips stay in
`Assets/Sounds` and are built out-of-tree (`/build:../Assets/Sounds/x.ogg;Sounds/x.xnb`), so
nothing is moved or duplicated, and the same entries serve both content toolchains — no
wwwroot copy rule needed. `SoundEffect.FromStream` is WAV-only on both backends, which is why
audio goes through the pipeline while the PNGs under `Assets/` do not.

A clip is only *audible* once a `SoundKind` maps to its stem (`Audio/SoundKind.cs`) and
`Audio/GameAudio.cs` says what triggers it — that policy file is the only hand-written step.
Unmapped stems build fine and are reported by `sync-sounds.ps1` with a `?`. A kind with no
clips on disk is silent, never an error, so the game runs identically with an empty bank.
Dev hotkeys: **F9** fires the committed `dev_tone.ogg`, **F10** mutes. Design:
`Plans/AUDIO_PLAN.md`; web audio still needs a click-to-start unlock before it will sound.

All operational tooling is indexed in [scripts/README.md](scripts/README.md) (publish, SFX
conversion, headless build VM).

Quickest correctness check while iterating on game logic: `dotnet build MTile.Core.csproj`.

**Frame profiling in-game**: `GameConfig.DebugFrameTimings` (on by default) draws a per-pass
breakdown — `sim / anim / cosmetic / chunks / skins / fx / glow / present / …`, mean+worst over
60 frames. **F11 dumps the last window to the console**, which is how the *web* build is
profiled (the dump lands in devtools). Draw-side scopes measure batch *building*; the GPU
submit lands in `present`. `Diagnostics/FrameProfiler.cs`.

### Running a relevant slice of the suite

The full suite is ~75 s / 489 tests, but **two scratch classes are 65% of that** — `Sim.ZzzLatticeTiming`
(~35 s) and `Animation.ZzzRestSpasm` (~13 s). The `Zzz` prefix marks long-running harnesses, so the
default full-coverage run should skip them (~35 s, 481 tests):

```bash
dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName!~Zzz"
```

Parallelization is disabled assembly-wide on purpose (`MTile.Tests/TestAssemblySetup.cs`) — sim tests
mutate `MovementConfig.Current`, so classes race. 22 of 103 test files touch process-wide statics.
Don't re-enable it without moving those into a shared `[Collection]` first.

Targeted slices, by what a change touches (`--filter` alternations; namespaces are NOT a reliable
selector — `GrabTests` is in `MTile.Tests` while `CaveMouthTests` is in `MTile.Tests.Sim`):

| Area | Filter | ≈ |
|---|---|---|
| Combat / action FSM | `~Combat\|~Grab\|~Escalation\|~AttackRecoil\|~Commitment\|~Eruption\|~ClipBinding\|~ActionOverlay` | 4 s / 77 |
| Snapshot / rollback | `~Snapshot\|~Rollback\|~InputCodec\|~ECS` | 2 s / 15 |
| Corrector / fold | `~Corrector\|~Fold\|~Ballistic\|~CaveMouth\|~BumpyTunnel\|~SpeedInvariant` | ~5 s |
| Animation solver | `~Solver\|~Anim\|~Pose\|~Skeleton\|~NoPen` | ~5 s |

(Prefix each term with `FullyQualifiedName`, e.g. `--filter "FullyQualifiedName~Combat|FullyQualifiedName~Grab"`.)

**Adding an `ActionState` subclass requires an authored clip** whose `Type` equals the class name, or
`ClipBindingTests` fails — and an action that declines `AnimationProgress` needs a *looping* clip.
Create it with `dotnet run --project MTile.Probe -- new <name> <ClassName> --from <clip>@<t>`, then set
`Region` by hand (`probe new` defaults to `FullBody`, wrong for an overlay).

**Gotcha:** while the game is running, `MTile.exe` is file-locked, so a Desktop/Tests build's final copy step fails even though the C# compile + test dll succeed. Use `dotnet test --no-build` against the already-built `MTile.Tests.dll` in that case, or close the game first.

Content (`.xnb`) is built from `Content/Content.mgcb` by `MonoGame.Content.Builder.Task`; the `dotnet-mgcb` tool is pinned in `.config/dotnet-tools.json` (`dotnet tool restore`).

## Config & assets at runtime

All five runtime configs live in **`configs/`**:

- `configs/movement_config.json` — movement tuning, **hot-reloaded** via `FileSystemWatcher` (gated by `GameConfig.HotReloadMovementConfig`; off in multiplayer). Edit while the game runs to retune.
- `configs/game_config.json` — match/stage config (`GameConfig`).
- `configs/anim_solver_config.json` — animation-solver weights/limits. Render-only, so hot-reload is always safe (no multiplayer gate).
- `configs/impact_profiles.json`, `configs/material_strengths.json` — per-body impact tuning and per-tile-type strength/build cost. Loaded once at boot: both are sim-affecting, so a mid-match reload would desync rollback peers.
- `Levels/*.json` — terrain: chunk-position → ASCII-file map + Perlin params, loaded by `TerrainLoader`.

Each host copies `configs/` into its own output, **at the same sub-path** (Desktop: `configs/` beside the binary; Web: `wwwroot/configs/`). That match is load-bearing rather than cosmetic: every config is loaded by one string that resolves CWD-relative first — so launching from the repo root reads the file you actually edit, which is what makes hot-reload work — and falls back to title-relative, which reads the host copy. Move the source without updating a host's copy rule and it still compiles; the game just boots with silently-defaulted tuning, because every loader no-ops on a missing file. `MTile.Tests/ConfigLayoutTests.cs` guards the pairing.

Edit the **`configs/` originals** — the per-host copies under `bin/` and `MTile.Web/wwwroot/` are generated and gitignored.

## Deterministic sim + rollback netcode (shipped)

The sim extraction is **done and committed**, as is rollback multiplayer on top of it. Treat this as the settled architecture, not work in progress.

- **`Simulation.cs`** is the deterministic core: players, entities, chunks, combat registries, force fields, and platforms, advanced by `Step(PlayerInput)` (or `Step(p0, p1)`) on a fixed `Simulation.FixedDt` (1/60). Particles, trail, camera, sprites, and everything in `Animation/` are **render-only and must never feed back into the sim**.
- Bodies and entities live in the sparse-set ECS `World` under `Sim/ECS/`. `Snapshot()` is essentially `_world.Capture()` plus terrain and a few side tables — there are no per-player/per-entity snapshot struct arrays anymore. Terrain uses an inverse-delta **journal** (`World/TerrainJournal.cs`); sparse per-tile structures are value-snapshotted. Verified by `MTile.Tests/Sim/SnapshotRoundTripTests.cs`.
- `Net/` holds a working rollback session (predict → reconcile → replay, checksum desync detection). `NetSetup` (`LocalPlayerIndex` + `Send` + a delivery queue) is the transport-agnostic seam, and it held up: **adding browser multiplayer required zero changes under `Net/`.** Two parallel transports — desktop `MTile.Rtc` (`MTile.Desktop -- host` / `-- join`) and browser `MTile.Web/wwwroot/mtileRtc.js`. Wire-compatible but **never cross-play**: float determinism doesn't hold across runtimes, so both peers must run the same build.
- **Determinism rules** when touching sim code: no sim-affecting `static` mutable state (HitIds flow through `World/HitIdAllocator.cs` and `EnvironmentContext.HitIds`); no polling hardware mid-step (all input must arrive via `PlayerInput`); same iteration order on restore. Anything added to a live object that must survive a rollback belongs in an ECS value component.

Note that `Plans/ROLLBACK_ROADMAP.md`'s checklist is **stale** — several unchecked goals are plainly done.

## Where the live work is

**[BACKLOG.md](BACKLOG.md) is the single list of outstanding work** — movement, animation, combat, engineering debt, and the deliberately-skipped tests, each with a verified status and file evidence. It replaced the old scattered `todo.txt` / `anim_todo.txt` / `movement_todo.md` files; add new items there.

- **The ballistic corrector** (`Character/Corrector/AmbientCorrector.cs`, `CorrectionSolver.cs`, `BallisticPredictor.cs`, `FoldReference.cs`, …) — free-state locomotion is solver-driven, and this is the actively-tuned area. Hot-reload ablation knobs (`CorrectorVaultEnabled`, `FoldRedirectEnabled`, `FoldEngine`) exist for live A/B during playtests; `TrajectoryLm` is the nonlinear oracle the QP path is checked against.
- **The animation solver** (`Animation/`) — vertical cadence/constraints shipped; horizontal `d.x`/ComOffset, joint limits, and local-SDF non-penetration are still open (`Plans/ANIMATION_SOLVER_PLAN.md` §11.6 Phase 4).
- **Browser PvP** — works end to end (WebRTC + Firestore room codes, deployed to GitHub Pages). What's left before strangers can play: TURN (STUN-only today, so symmetric NAT/CGNAT fails), desync/disconnect handling, and a Firestore-path smoke test. `Plans/INTERNET_READY_PLAN.md`.
- **Terrain building** — the paint/place/burst/peel + mass-economy rework replaced the old eruption planners. Actively evolving.
- Known gaps worth not rediscovering: `Character/Movement/JumpStates.cs:61` has the sim reading animation-layer data (a layering violation); `PlayerCharacter.cs:485-554` prints `[move]`/`[action]` transitions to the console on the sim hot path (fires during rollback re-sim too — almost certainly leftover tracing); 7 tests are deliberately `Skip`ped with reasons; `Game1.cs` render/HUD extraction is half-done.

`MTile.Tests/Sim/` (`SimRunner`, `SimTerrain`, `InputScript`, `SimReport`) is the headless analogue of the sim — scenario tests with ascii terrain + scripted input, mirroring the same phase ordering as `Simulation.Step`. Use it for deterministic gameplay tests.

## Key conventions (see CODEBASE_OVERVIEW.md for the full set)

- **Y-down coords** (MonoGame default); world gravity `(0, 600)` px/s². Tile coords `gtx/gty` are integer cell indices; cell center world pos is `gtx*Chunk.TileSize + Chunk.TileSize/2` (the codebase is parameterized on `Chunk.TileSize`, but px overrides in `configs/movement_config.json` do not scale with it).
- **Forces are accelerations** — `PhysicsBody` has no mass (`Velocity += AppliedForce * dt`); mass appears only in `ImpactDamage`/`Entity` knockback.
- **Movement must not read action state.** Actions may read movement; the only channels the other way are `MovementModifiers` (multiplicative scalars on baseline config) and `Body.AppliedForce`.
- **State priorities**: `Character/Movement/MovementPriorities.cs` is the single source of truth — read it rather than trusting a band summary. Preemption compares the **candidate's Passive** to the **current state's Active**; getting that backwards is how the climb family sat at an unbeatable 46/46 for a while.
- **Combat is escalation-based**: hits add to a monotonic `DamagePercent` and scale knockback; HP is lost only via crush impact into terrain. A hitbox's `Damage` is a percent contribution (except on the tile path).
- **World reactions go through events** (`ChunkMap.OnTileBroken`), not polling.
- **Core gameplay attributes are read-only** unless explicitly asked: `PlayerCharacter.Radius`/`BodyWidthScale`, `Simulation.Gravity`, `Chunk.TileSize`, `FixedDt`. Everything tuned in the corrector and impact stacks is calibrated against them.
