# Controls & dev settings — the game (`MTile.Desktop`)

Everything you can press or flip while running `dotnet run --project MTile.Desktop`.
The animation/bind/take tools in `MTile.Demo` have their own sheet:
[MTile.Demo/CONTROLS.md](MTile.Demo/CONTROLS.md).

"Offline" below means a solo run (no `host` / `join` argument). Online, only gameplay
input and the passive keys work — anything that would pause, edit, or reload one
peer's world would desync the match.

## Gameplay

| Input | Does |
|---|---|
| A / D, ← / → | Move |
| W / ↑, S / ↓ | Up / down (ledge and climb intents, aim) |
| Space | Jump (hold for height) |
| LMB tap | Slash (repeat taps chain the combo) |
| LMB hold + swipe | Stab along the swipe |
| Shift (no L/R held) | Guard — the bronze arc covers the 120° cone you face |
| LMB while guard-charged | Guard retaliate (fast counter) |
| RMB | Block paint |
| Shift + RMB | Block place (build) |
| RMB + LMB combos | Bursts (`BurstAction`, `BlockBurstAction`) |
| 1–4 | Build material: Stone / Dirt / Sand / Foam |

The gesture details (hold thresholds, circle tolerance) live in
`Character/Input/InputParser.cs`; which action claims which input is each
`CheckPreConditions` in `Character/Action/ActionStates.cs`.

## Creative mode — level editing (Tab, offline)

**Tab** freezes the sim and frees the camera; **Tab** again resumes play exactly where
you were, so build → test → build is one key.

| Input | Does |
|---|---|
| LMB (drag) | Paint the selected material (instant, no support needed) |
| RMB (drag) | Erase |
| 1–5 | Material: Stone / Dirt / Sand / Foam / **Hardened** |
| `[` / `]` | Brush size 1×1 … 9×9 |
| Ctrl+Z | Undo the last stroke (repeatable) |
| Mouse wheel | Zoom about the cursor |
| WASD / arrows (Shift = faster), middle-drag | Pan |
| T | Teleport the player to the cursor, at rest |
| **Ctrl+S** | **Save — overwrites the loaded level** |
| Tab | Back to play |

The HUD shows the material swatches, brush, hovered cell, and an unsaved-edit count;
the overlay draws a tile grid with stronger chunk borders.

**Saving** (`LevelWriter.cs`) writes into the level the active stage loaded —
`Levels/<name>.json` plus its chunk `.txt` files. Git is the undo.
- Only chunks that differ from what the file already produces are written; untouched
  Rules/Perlin chunks stay procedural, authored chunk files keep their names, and the
  rest of the `.json` is preserved.
- Painting past the level's box grows `Extents` (the toast says so).
- Launch **from the repo root**: the save targets `./Levels/` (the source, not the
  build copy) and fails with a toast if it can't find the file there. The build's copy
  is updated too, so **F5** re-enters the saved level immediately.
- Stages built in code (no level file) and endless `WorldGen` levels can't be
  overwritten — use Ctrl+M to snapshot them instead.

Which level you're editing is `"Stage"` in `configs/game_config.json`; stage → file
mapping is in `Stage.cs`.

## Time: pause, step, slow-mo (offline)

| Input | Does |
|---|---|
| F6 or Pause | Pause / resume |
| F7 or `.` | Step one frame (pauses first if running); Shift = 10 frames; hold to repeat |
| `"TimeScale"` in `game_config.json` | Slow / fast motion (0.25 = quarter speed, 0 = frozen) |

## Stages (offline, desktop)

| Input | Does |
|---|---|
| F5 | Reload the active stage from disk (also re-reads `TimeScale` + the `FreezeFrame*` fields) |
| Ctrl+M | Snapshot nearby terrain + player position as a new `Levels/saved/saved_NNN` stage (never overwrites); F5 re-enters it, `"Stage": "saved_NNN"` persists it |

## Recording & replay (offline)

| Input | Does |
|---|---|
| Ctrl+R | Start / stop recording a take |
| Ctrl+P | Enter / leave playback of the take |
| Ctrl+S (not in creative) | Save the take to `Takes/` for `MTile.Demo --load` |
| ← / → (Shift = 10), Home / End | Scrub (in playback) |
| J / K or Space / L | Play reverse / stop / play forward (in playback) |
| Ctrl+L | Start / stop the animation trace log |

## Diagnostics & misc

| Input | Does |
|---|---|
| F12 | Screenshot (see the `/screenshot` skill for `MTILE_SCREENSHOT` automation) |
| F11 | Append the profiler window to `perf_log.txt` (and the console) |
| F8 | Run the corrector-QP microbenchmark (blocks ~1.5 s) |
| F9 | Play the dev tone (audio chain check) |
| F10 | Mute / unmute |
| Esc | Quit |

## `configs/game_config.json` — the settings worth knowing

Read at launch (F5 re-reads only the fields noted above). Full list with defaults:
`GameConfig.cs`.

| Key | What |
|---|---|
| `Stage` | Which stage loads (the comment in the file lists them) |
| `Fullscreen`, `WindowWidth`, `WindowHeight` | Window |
| `CameraZoom` | Play zoom (creative mode zooms independently and restores this on exit) |
| `TimeScale` | Sim speed multiplier |
| `SpawnSecondPlayer` | Offline P2 driven by a bot |
| `AnimationRig`, `PlayerSpriteBindings` | Character rig + skin (`rabbit_floppy` on `biped_rabbit`) |
| `DrawPlayerSpriteSkin`, `DrawPlayerSprites`, `DrawBackground`, `BackgroundStyle` | What draws |
| `DebugDraw*` | Overlays: hitboxes, hurtboxes, bodies, skeleton, constraints, corrector paths, step planner, health bars, force fields… |
| `DebugFrameTimings` | Per-pass frame-time HUD line |
| `FreezeFrame*` | Start frozen at a set position/velocity (corrector debugging) |
| `StageSaveChunkRadius` | Ctrl+M capture radius, in chunks |

`configs/movement_config.json` hot-reloads while the game runs when
`HotReloadMovementConfig` is on (never in multiplayer) — edit and save to retune live.
