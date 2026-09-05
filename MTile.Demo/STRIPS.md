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
| `--world` | Shared world-space camera for gameplay panels, revealing displacement. Default camera follows body position. |

All panels share one fitted scale and extent, including the deformed sprite mesh (ears,
clothing, etc.). Per-panel render targets prevent neighboring frames from bleeding together.
The output is opaque with a dark background and frame labels, intended for inspection.
Open the PNG directly or use an image-viewing tool to compare the sequence. The probe-driven
numeric checks remain useful alongside these visuals; raw clips alone cannot verify foot planting.
