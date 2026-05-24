# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A 2D platformer in C#/MonoGame built around "the terrain IS the weapon": the player slashes, stabs, pulses, and erupts blocks to reshape a chunked tile world while moving through it. Fixed timestep of 30 fps.

**Read [CODEBASE_OVERVIEW.md](CODEBASE_OVERVIEW.md) first** — it is the authoritative architecture doc (physics, character FSMs, combat, world/tiles, drawing). This file covers only build/run/test mechanics, project layout, and the in-flight refactor that the overview predates. Design notes and roadmaps live in [Plans/](Plans/).

## Project layout

Game source lives **at the repo root** (`Character/`, `Physics/`, `World/`, `Entities/`, `Drawing/`, `Game1.cs`, etc.). It is compiled into the `MTile.Core` library and reused by three hosts:

| Project | Role |
|---|---|
| `MTile.Core.csproj` (root) | The library. Globs the root `.cs` files; excludes `MTile.Tests/`, `MTile.Desktop/`, `MTile.Web/`. Compiles against `MonoGame.Framework.DesktopGL`. |
| `MTile.Desktop/` | `WinExe` desktop host (`AssemblyName` = `MTile`). DesktopGL. The normal way to run the game. |
| `MTile.Web/` | Blazor WebAssembly host via the **KNI** MonoGame variant (`nkast.Xna.Framework`). Does **not** ProjectReference Core — KNI is a different assembly identity, so it re-globs the same root `.cs` and compiles them a second time. Browser-port status tracked in `Plans/BROWSER_PORT_PLAN.md`. |
| `MTile.Tests/` | xUnit. ProjectReferences `MTile.Core`. |

`MTile.sln` contains Core + Desktop + Tests. `MTile.Web.sln` is separate.

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
dotnet run --project MTile.Web              # dev server
```

Quickest correctness check while iterating on game logic: `dotnet build MTile.Core.csproj`.

**Gotcha:** while the game is running, `MTile.exe` is file-locked, so a Desktop/Tests build's final copy step fails even though the C# compile + test dll succeed. Use `dotnet test --no-build` against the already-built `MTile.Tests.dll` in that case, or close the game first.

Content (`.xnb`) is built from `Content/Content.mgcb` by `MonoGame.Content.Builder.Task`; the `dotnet-mgcb` tool is pinned in `.config/dotnet-tools.json` (`dotnet tool restore`).

## Config & assets at runtime

- `movement_config.json` — movement tuning, **hot-reloaded** via `FileSystemWatcher` (gated by `GameConfig.HotReloadMovementConfig`; off in multiplayer). Edit while the game runs to retune.
- `game_config.json` — match/stage config (`GameConfig`). Top-level tuning that overrides `movement_config.json` lives here too (e.g. `SproutLifetime`), applied once in the `Simulation` ctor.
- `Levels/*.json` — terrain: chunk-position → ASCII-file map + Perlin params, loaded by `TerrainLoader`.

Each host copies these from the repo root into its own output (Desktop: alongside the binary; Web: into `wwwroot/`). Edit the **root** copies — the per-host copies under `bin/` and `MTile.Web/wwwroot/` are generated and gitignored.

## In-flight refactor: deterministic sim for rollback netcode

A large uncommitted refactor is extracting the deterministic game world out of `Game1` so it can be snapshotted and replayed for GGPO-style rollback multiplayer. **This is the most important thing the overview doesn't yet describe.**

- **`Simulation.cs`** is now the deterministic core: it owns players, entities, chunks, combat registries, and platforms, and advances them with a single `Step(PlayerInput)` on a fixed `Simulation.FixedDt` (1/30). `Game1` is becoming a thin shell (gather hardware input → `Step` → render); particles, trail, camera, and sprites are **render-only and must never feed back into the sim**.
- `Stage`/`Stages` (`Stage.cs`) describe a match (`TerrainConfig`, `PlayerSpawn`, `Populate`). `Simulation` consumes a `Stage`.
- `Snapshot()`/`Restore()` capture/restore players, entities, combat dedupe, platforms, and terrain. Terrain uses an inverse-delta **journal** (`World/TerrainJournal.cs`) rather than full copies; sparse per-tile structures are value-snapshotted (`*Snapshot.cs`, `*State` types, `BodyState.cs`). Verified by `MTile.Tests/Sim/SnapshotRoundTripTests.cs`.
- **Determinism rules** when touching sim code: no sim-affecting `static` mutable state (HitIds flow through `World/HitIdAllocator.cs` and `EnvironmentContext.HitIds`); no polling hardware mid-step (all input must arrive via `PlayerInput`); same iteration order on restore. See `Plans/ROLLBACK_ROADMAP.md` (status checklist), `Plans/STATE_SNAPSHOT_PLAN.md`, and `Plans/GGPO_PLAN.md`.

`MTile.Tests/Sim/` (`SimRunner`, `SimTerrain`, `InputScript`, `SimReport`) is the headless analogue of the sim — scenario tests with ascii terrain + scripted input, mirroring the same phase ordering as `Simulation.Step`. Use it for deterministic gameplay tests.

## Key conventions (see CODEBASE_OVERVIEW.md for the full set)

- **Y-down coords** (MonoGame default); world gravity `(0, 600)` px/s². Tile coords `gtx/gty` are integer cell indices; cell center world pos is `gtx*16 + 8`.
- **Forces are accelerations** — `PhysicsBody` has no mass (`Velocity += AppliedForce * dt`); mass appears only in `ImpactDamage`/`Entity` knockback.
- **Movement must not read action state.** Actions may read movement; the only channels the other way are `MovementModifiers` (multiplicative scalars on baseline config) and `Body.AppliedForce`.
- **State priorities form bands** (free 0–20, walls 20–40, guided 25–45, launches 50–60); `Active`/`Passive` priority pairs control stickiness vs. preemption.
- **World reactions go through events** (`ChunkMap.OnTileBroken`), not polling.
