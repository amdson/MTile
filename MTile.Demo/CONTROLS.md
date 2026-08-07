# MTile.Demo — Skeleton Animation Editor

A standalone tool for authoring the skeletal animations the game plays (walk, idle,
jump, …). It edits `AnimationDocument` JSON files in the repo's
`SkeletonStates/<rigName>/` folder (one dir per base rig) — the same files
[CharacterAnimator](../Animation/CharacterAnimator.cs) loads at runtime. The editor
never touches the simulation.

Run it:

```bash
dotnet run --project MTile.Demo
```

Content is **authored-only**: on launch the editor loads the rig from
`Skeletons/<name>.json` (and **fails fast with a clear error if it's missing** — no
procedural fallback, no autogeneration) plus every json in that rig's
`SkeletonStates/<name>/` dir exactly as it exists on disk. New clips are created
explicitly with `N` (new) or `C` (clone); restoring lost content means restoring the
files from git.

---

## Command line

One executable, five modes. The **first matching mode flag wins**, in this order:
`--import` → `--ref` → `--load` → `--bind` → animation editor (the default when no
mode flag is given). Any bare argument is taken as a clip name for the editor.

```bash
# Animation editor (default mode)
dotnet run --project MTile.Demo                          # open on the first clip (rig: biped_rabbit)
dotnet run --project MTile.Demo -- walk                  # open a clip by name
dotnet run --project MTile.Demo -- --rig biped           # edit the legacy rig's clip pool
dotnet run --project MTile.Demo -- walk --usebind rabbit

# Sprite bind editor
dotnet run --project MTile.Demo -- --bind rabbit                     # SpriteBindings/rabbit.json
dotnet run --project MTile.Demo -- --bind hero.png                   # legacy: create/edit from a PNG
dotnet run --project MTile.Demo -- --bind rabbit --rig biped_rabbit  # pick / re-target the rig

# Art import (decomposed-limb intake, SPRITE_SKIN_PLAN.md §10.2)
dotnet run --project MTile.Demo -- --import SkeletonAssets/rabbit_and_badger
dotnet run --project MTile.Demo -- --import <dir> --out SpriteBindings --scale 0.25

# Take viewer (scrub an in-game recording with solver overlays)
dotnet run --project MTile.Demo -- --load Takes/<name>.take.json

# Reference-clip editor (maneuver Hermite arcs, authored in game pixels)
dotnet run --project MTile.Demo -- --ref parkour
```

| Flag | Modes it applies to | Meaning |
|---|---|---|
| `<clip>` (bare arg) | editor | Clip name to open (sidebar jumps there). Ignored by other modes |
| `--rig <name>` | editor, `--bind` | Rig from `Skeletons/<name>.json`, default `biped_rabbit`. **Editor**: also selects the clip pool `SkeletonStates/<name>/`; Ctrl-S rig edits write back to that rig's own file. **Bind editor**: default is the binding's `Skeleton` field (then `biped`); passing a *different* rig re-targets the binding — bones match by name, new bones start at rest, Ctrl-S persists the new rig name. Other modes ignore it (viewer/ref are biped-tied) |
| `--usebind <binding>` | editor | Superimpose a sprite skin on the rig through scrub/playback. The skin bakes against the **binding's** own `Skeleton` rig; keys: `G` sprite, `W` wireframe, `X` skeleton |
| `--bind <name\|png\|json>` | mode flag | Open the sprite bind editor. A bare name resolves `SpriteBindings/<name>.json` first (multi-image bindings have no single PNG); a `.png` argument is the legacy path and also creates brand-new bindings |
| `--import <dir>` | mode flag | One-time intake of decomposed part art: alpha-crop + downscale each PNG, write `SpriteBindings/<char>/<part>.png` + first-pass binding jsons |
| `--out <dir>` | `--import` | Output root for imported art (default `SpriteBindings`) |
| `--scale <f>` | `--import` | Downscale factor for imported art (default `0.25`) |
| `--load <path>` | mode flag | Take viewer for a `.take.json` recorded in-game (Ctrl+R / Ctrl+S) |
| `--ref <clip>` | mode flag | Hermite reference-arc editor; loads/saves `ReferenceClips/<name>.json`. Arcs are authored in **game pixels** against the clip's draggable **entry/gate anchors** (green rings) — the runtime rescales from that span onto the obstacle it measures, so keys are free to sit before the entry or past the gate. `U` converts a pre-anchor normalized clip to a pixel box; `[` / `]` set the arc's **Duration** (seconds end to end — what animation clips pace against) |

Screenshot env vars (dev captures; the window renders a few frames, saves a PNG, and
exits): `MTILE_SHOT=<path>` works in **every** mode. Modifiers — editor:
`MTILE_SHOT_HELP`, `MTILE_SHOT_WIRE`, `MTILE_SHOT_NOSKEL`; bind editor:
`MTILE_SHOT_PREVIEW`, `MTILE_SHOT_CLIP=<clip>`, `MTILE_SHOT_LAYER=<layer>`;
take viewer: `MTILE_SHOT_HELP`, `MTILE_SHOT_FRAME=<n>`.

---

## Layout

- **Left sidebar** — the animation list, grouped under a `Type` header, with each
  entry's keyframe count. The selected clip is highlighted.
- **Center** — the rig at the current timeline position. Bright = an editable
  keyframe is active; dimmed = an interpolated (non-editable) frame.
- **Bottom** — the timeline: a track with keyframe **bars** and the orange
  **playhead**.
- **Top** — clip name/type, unsaved marker, current frame state, duration/loop, and
  the active edit mode.

---

## Selecting & navigating

| Input | Action |
|---|---|
| Click a sidebar row | Load that animation (renders its first keyframe) |
| Click/drag on the timeline track | Move the playhead (scrub / interpolate between keyframes) |
| Click a keyframe **bar** | Select it as the active, editable keyframe |
| Drag a keyframe **bar** | Move that keyframe in time |

When the playhead sits exactly on a keyframe, that frame becomes the editable one;
otherwise you're on an interpolated frame (use `K` to turn it into a keyframe).

---

## Editing the pose

Drag a **joint** to edit the active keyframe's pose. The drag behavior depends on the
current **edit mode**, cycled with one key:

| Input | Action |
|---|---|
| **Tab** | Cycle edit mode: **ROTATE → RESIZE → STRETCH** |
| **F** | Flip the animation across a vertical axis — mirrors the **data** (persists on save). Press again to flip back. Use it to make a clip face the game's canonical direction (the runtime mirrors by player facing) |
| Drag joint (ROTATE) | Rotate the bone about its parent, preserving limb length; the subtree carries along |
| Drag joint (RESIZE) | Move the joint to the cursor — changing the limb's rest **Length on the rig** (persists to `Skeletons/<name>.json`, affects every clip) — rolling the bone's rotation so the subtree follows |
| Drag joint (STRETCH) | Slide the joint along the bone's axis — writes the ratio as this **keyframe's `Stretch`** (pseudo-3D foreshortening; rotation and rig untouched). Signed: dragging past the parent joint flips the bone slightly negative, e.g. a hip strut at full leg swap |
| Drag **com marker** | Place the player against the fixed scenery **per keyframe**: com and skeleton travel with the cursor while the floor line and obstacle block stay put; the drag writes the active keyframe’s `edref` placement (refused while a reference arc is attached — the arc owns placement), and scrubbing interpolates it — so the body visibly arcs over the refs (e.g. parkour clearing its block). Editor-only visualization, saved with the clip (`edref` additions; the runtime ignores them). Arrow keys pan everything together |

A clip can also ride the maneuver's **authored reference trajectory**: press **A** in the editor to
cycle one on (or set `"ReferenceArc": "<name>"` in the clip json / run `MTile.Probe -- refarc <clip>
<name>`). That drives the body's scene placement from `ReferenceClips/<name>.json` (falling back to
the baked registry defaults) while scrubbing, and **reloads live** when that file is saved — so you
can keep `--ref <name>` open in a second window and shape the arc while watching the body ride it —
the header shows `arc <name> <arcDur>s x<ratio> at <progress>`. The arc is authored in game pixels
against its own entry/gate anchors, so it maps to the scene at true scale — for every clip, including
Parkour. The reference block is scenery to position the arc *against*, never a retarget target; the
runtime does its own retargeting onto the obstacle it measures. **Clip and arc have independent durations** — the body
advances along the arc at `τ · clipDuration/arcDuration`, so a 0.4s clip on a 0.3s arc hits the gate
at τ≈0.75 and overshoots after. The arc draws into the scene: bright where the clip's timeline
reaches, dim past it, a green ring at the gate, a dot per keyframe, and a body-radius ring at the
playhead — author each pose against the dot it lands on: the ring rides the curve exactly, because
**the arc OWNS placement while it is attached** — `edref` is the fallback for clips with no arc, not
an additive nudge, so a com-marker drag is refused (with a console hint) rather than pushing the body
off its own arc. Press **A** to detach the arc if you want to hand-place again. Editor visualization
only; the runtime ignores it.
| Drag **root joint** | Move the body **within** the com frame — the inverse edit of the active keyframe's `com` (the game's vertical anchor): the skeleton follows the cursor while the com marker and floor line hold still, exactly as the game will place it |

The active mode is shown in the top header.

---

## Contacts (foot-plant labels)

Contact labels mark which node is planted on a keyframe; the runtime cadence solver
pins them for no-slip locomotion. See
[Plans/ANIMATION_LOCOMOTION_PLAN.md](../Plans/ANIMATION_LOCOMOTION_PLAN.md).

| Input | Action |
|---|---|
| **M + click** a node | Toggle a `SelfPlant` contact on that node (active keyframe) |

Contact-labeled nodes are drawn with a **green halo** on the active keyframe. Sampling
a new keyframe with **K** inherits the contact marks in effect at the playhead by
default (deep-copied, so editing one keyframe's marks doesn't change the other's).

---

## Keyframes, playback & clip settings

| Input | Action |
|---|---|
| **K** | Sample the current (possibly interpolated) pose into a new keyframe at the playhead, and make it active |
| **Delete** | Delete the active keyframe |
| **Space** | Play / pause timeline playback (honors Duration & Loop) |
| **[** / **]** | Decrease / increase the clip's Duration (seconds) by 0.1 |
| **L** | Toggle Loop on/off |
| **R** | Cycle the clip's Region: **FullBody → UpperBody → LowerBody**. Region is the bone mask an *action overlay* clip owns when layered over movement at runtime — a slash is `UpperBody` (chest/head/arms) so the legs keep walking. Movement clips stay `FullBody` (the default; not written to JSON) |
| **A** / **Shift-A** | Attach the next / previous **reference arc** to this clip, wrapping through `none`. Offers the baked arc names plus every `ReferenceClips/*.json`. Same edit as `MTile.Probe -- refarc`, saved with Ctrl-S |
| **T** / **Shift-T** | Cycle the clip's Type forward/back through the known categories: movement clips (`Idle`, `Walk`, …) plus every action state name (`GroundSlash1`, `StabAction`, …). The runtime binds action overlay clips by exact action name |

---

## File operations

| Input | Action |
|---|---|
| **`** | Toggle the block grid — one cell = one game tile (`Chunk.TileSize`), anchored to the floor line and the scene origin so cell edges sit where terrain would. On by default |
| **Ctrl-S** | Save **all** animations to their JSON files (`*unsaved*` clears) |
| **N** | Create a new (empty) animation |
| **C** | Clone the selected animation — deep-copies all keyframes/contacts into a new clip named `<name>_copy`, selected and ready to edit (saved as a separate file on Ctrl-S). Use it to fork a variant, e.g. derive a run from the walk |
| **Escape** | Quit |

Edits are kept in memory until `Ctrl-S`; the header shows `*unsaved*` while dirty.
