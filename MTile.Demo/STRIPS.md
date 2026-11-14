# Animation PNG strips

`MTile.Demo --strip <output.png>` renders labeled panels, saves a PNG, and exits.
It uses MonoGame's actual `SkeletonRenderer` and `SpriteSkin`, including MLS artwork
deformation. It briefly opens a graphics window and needs a working desktop/OpenGL
context. Output paths are relative to the current directory; assets resolve from the repo.
An existing output PNG is replaced. No animation, configuration, or level files are edited.

Build once:

```sh
dotnet build MTile.Demo
```

## Live gameplay: rabbit climbing stairs

```sh
dotnet run --project MTile.Demo --no-build -- --scenario stairs --usebind rabbit --strip .probe/stairs-game-rabbit.png --start 30 --end 130 --frames 12 --size 320
```

This creates an isolated staircase with ten one-block rises, holds Right, and runs
`Simulation.Step` at 60 Hz using the current movement config. Every tick runs the same
terrain extraction → character sample → `CharacterAnimator.Update` → `RigRoot` sequence
as `CosmeticUpdateSystem`. Selected **solved poses** are rendered with the rabbit skin
and their terrain. Labels show simulation frame, elapsed seconds, and selected clip.
The background is a neutral inspection view with plain tile rectangles; game HUD, VFX,
and tile textures are omitted. This is not raw sampling of the `stepup` clip.

Use `--facing -1` for a mirrored staircase and held Left. Omit `--usebind rabbit` to see
the same simulation as stick figures; add `--overlay` to put the rig over the artwork.
Default scenario range: frames 0–149. A custom range may end at frame 3599.

## Live gameplay: grab (phase clips + debug grab box)

```sh
dotnet run --project MTile.Demo --no-build -- --scenario grab --rig biped --strip Plans/img/grab_ingame_throw.png --start 8 --end 86 --frames 16 --size 300
dotnet run --project MTile.Demo --no-build -- --scenario grab-whiff --rig biped --strip Plans/img/grab_ingame_whiff.png --start 8 --end 68 --frames 12 --size 300
```

`--scenario charge | charge-run | charge-air` reuse this stage for the stab wind-up: a plain
LMB held from frame 10 to 90, standing, running, or dropped from the air (the three
`RecoveryAction.Charge*` stance clips):

```sh
dotnet run --project MTile.Demo --no-build -- --scenario charge-run --rig biped --strip .probe/charge/charge-run.png --start 6 --end 96 --frames 16 --columns 8 --size 240
```

`--scenario jump | jump-run` hold Space from frame 40 (after the spawned body has settled at
its standing height), standing or at a run — the take-off is frames 40–46:

```sh
dotnet run --project MTile.Demo --no-build -- --scenario jump --rig biped --strip .probe/charge/jump-close.png --start 39 --end 50 --frames 12 --columns 6 --size 300
```

Flat ground; P1 presses Shift+LMB at frame 10 at open air in front, holds, swipes and
releases (frame 60, or 40 for the whiff). `grab` puts an AFK second player in reach (its
body drawn as a cyan box); `grab-whiff` has nobody. Labels show the action, its phase
variant and the recovery countdown, so start → hold → throw (or start → whiff) is visible;
each panel also draws the action's `DebugQueryShapes` — the latch box (dim through the
windup, solid once live) and the carry box while holding — exactly what
`DebugDrawHitboxes` shows in game.

## Raw authored clips (demo playback)

```sh
dotnet run --project MTile.Demo --no-build -- stepup --rig biped --strip .probe/stepup-stick.png
dotnet run --project MTile.Demo --no-build -- stepup --usebind rabbit --strip .probe/stepup-rabbit.png
dotnet run --project MTile.Demo --no-build -- groundslash1 --usebind rabbit --strip .probe/slash.png --start 0.2 --end 0.8 --frames 8
```

Uses the demo's C1 `SampleSmooth` interpolation and clip-local extra bones. Raw clips
have a fixed rig root, with no runtime contacts, cadence solve, movement, or action-overlay
composition. For a full loop the duplicate phase-1 panel is omitted. One-shots and explicit
`--end` ranges include the endpoint. Raw `--start`/`--end` are normalized phases in [0,1].

## Recorded gameplay takes

```sh
dotnet run --project MTile.Demo --no-build -- --load Takes/example.take.json --usebind rabbit --strip .probe/take.png --start 60 --end 120 --frames 12
```

Record with Ctrl+R and save with Ctrl+S in the game. The exporter replays the saved sample
stream through a fresh animator, including **every preceding frame** before `--start`,
so contact/cadence history is retained. Take ranges are inclusive zero-based frame indices.
The take's own timing and facing are used. If more panels than source frames are requested,
some frames repeat.

As in the existing take viewer, replay uses current clips/configuration and the recorded
animation inputs, not saved framebuffer screenshots. Older takes omit some inputs (notably
terrain constraint details and recovery data), so they cannot guarantee pixel-identical
reproduction. Use the live scenario for a complete current terrain solve. Takes do not name
their rig: pass `--rig` or `--usebind` matching the recorded character. Without either,
takes default to `biped`; raw clips and scenarios default to `biped_rabbit`.

## Layout and appearance

| Option | Meaning |
|---|---|
| `--frames N` | 2–64 evenly spaced panels; default 12. |
| `--columns N` | Panels per row; default 4 (or frame count if smaller). Use the frame count for one horizontal strip. |
| `--size N` | Square panel size, 128–1024 pixels; default 240. Total width/height must each be <=8192. |
| `--usebind NAME` | SpriteBindings name or JSON path; infers the binding's rig. Missing/mismatched art fails explicitly. |
| `--rig NAME` | Skeleton and clip pool. Must match an explicitly selected binding. |
| `--overlay` | Draw stick-figure bones/joints on top of the sprite. |
| `--facing -1` | Mirror raw clips; run live stairs leftward. Takes retain their recorded facing. |
| `--path` | Live walk scenarios: draw the walk planner's path for that tick in gold (nodes as dots, seed ringed). |
| `--world` | Shared world-space camera for gameplay panels, revealing displacement. Default camera follows body position. |

All panels share one fitted scale and extent, including the deformed sprite mesh (ears,
clothing, etc.). Per-panel render targets prevent neighboring frames from bleeding together.
The output is opaque with a dark background and frame labels, intended for inspection.
Open the PNG directly or use an image-viewing tool to compare the sequence. The probe-driven
numeric checks remain useful alongside these visuals; raw clips alone cannot verify foot planting.

Clip exports include their sprite attachments. Add `--trails` to accumulate their
motion ribbons between sampled frames; omit it for isolated poses. This uses the
raw clip's Duration, not the gameplay action/recovery clock. Recorded takes do
not yet contain attachment clocks and do not export these effects.

For a 60 Hz runtime animator rehearsal, use `--scenario slash-combo --frames 64`
(or `--scenario slash1 --frames 28` to inspect the first move's unchained recovery).
These include attachment clocks and support `--trails` and `--facing -1`. They use
immediate action transitions and fixed recovery samples, without combat/hitstop.

## Live gameplay: walking into one obstacle

Flat ground with a single feature at column 12 (x = 132 px), held-right walk, real
`configs/`. Scenarios: `bump` (one block), `bump3` (3-wide platform), `bumps` / `bumps2`
(two blocks, 1- / 2-tile gap), `step`, `stepdown`, `wall2` (2-tile step), `pillar2`
(2-tile column), `ceiling` (2-high corridor), `ceilbumps` (ceiling bumps with 1- and
2-tile gaps). The body reaches the feature around frames 110–130.

```bash
dotnet run --project MTile.Demo --no-build -- --scenario bump --rig biped --strip .probe/walk/bump.png --start 96 --end 176 --frames 21 --columns 7 --size 340 --path
```

`--scenario stab[-run|-air][-up|-down]` holds LMB from frame 10 and releases at frame 50 with a
swipe (forward, or 50° up / down), so the charge → stab hand-off and the aim re-target are on film:

```sh
dotnet run --project MTile.Demo --no-build -- --scenario stab-up --rig biped --strip .probe/stab/stab-up.png --start 47 --end 70 --frames 12 --columns 12 --size 200
```

More scripted scenarios on the same flat stage: `guard[-up|-down|-sweep]` (Shift held, cursor
forward / 50° up / down / sweeping down→up over frames 20–80; the shield arc is drawn),
`grab[-up|-down]` (the victim starts in latch range and is carried toward the cursor),
`drop` / `drop-run` (no buttons, spawned 180 px up: the heavy landing, standing or into a run).
`run-stop` (run from frame 40, let go at 90) and `run-turn` (run from 40, reverse at 90) film the
sprint start, the stop and the turn-around.
