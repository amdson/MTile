# MTile.Demo — Skeleton Animation Editor

For batch PNG panels of raw clips, sprite skins, recorded takes, or a live staircase climb,
see [Animation PNG strips](STRIPS.md) (`--strip`).

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
dotnet run --project MTile.Demo                          # open on the first clip (rig: biped)
dotnet run --project MTile.Demo -- walk                  # open a clip by name
dotnet run --project MTile.Demo -- --rig biped           # edit the biped clip pool (the default)
dotnet run --project MTile.Demo -- walk --usebind rabbit

# Sprite bind editor
dotnet run --project MTile.Demo -- --bind rabbit                     # SpriteBindings/rabbit.json
dotnet run --project MTile.Demo -- --bind hero.png                   # create a NEW binding from a PNG
dotnet run --project MTile.Demo -- --bind rabbit --rig biped_rabbit  # pick / re-target the rig

# Art import (decomposed-limb intake, SPRITE_SKIN_PLAN.md §10.2)
dotnet run --project MTile.Demo -- --import SkeletonAssets/rabbit_and_badger
dotnet run --project MTile.Demo -- --import <dir> --out SpriteBindings --scale 0.25

# Take viewer (scrub an in-game recording with solver overlays)
dotnet run --project MTile.Demo -- --load Takes/<name>.take.json

# Reference-clip editor (maneuver Hermite arcs, authored in game pixels)
dotnet run --project MTile.Demo -- --ref parkour

# Remap all clips e.g.
 dotnet run --project MTile.Probe -- --rig biped retarget biped_rabbit 
 # Remap one clip e.g. 
 dotnet run --project MTile.Probe -- --rig biped retarget biped_rabbit 

```

| Flag | Modes it applies to | Meaning |
|---|---|---|
| `<clip>` (bare arg) | editor | Clip name to open (sidebar jumps there). Ignored by other modes |
| `--rig <name>` | editor, `--bind` | Rig from `Skeletons/<name>.json`, default `biped_rabbit`. **Editor**: also selects the clip pool `SkeletonStates/<name>/`; Ctrl-S rig edits write back to that rig's own file. **Bind editor**: default is the binding's `Skeleton` field (then `biped`); passing a *different* rig re-targets the binding — bones match by name, new bones start at rest, Ctrl-S persists the new rig name. Other modes ignore it (viewer/ref are biped-tied) |
| `--usebind <binding>` | editor | Superimpose a sprite skin on the rig through scrub/playback. The skin bakes against the **binding's** own `Skeleton` rig; keys: `G` sprite, `W` wireframe, `X` skeleton |
| `--bind <name\|png\|json>` | mode flag | Open the sprite bind editor. A bare name resolves `SpriteBindings/<name>.json` first (multi-image bindings have no single PNG); a `.png` argument is how a brand-new binding is created |
| `--import <dir>` | mode flag | One-time intake of decomposed part art: alpha-crop + downscale each PNG, write `SpriteBindings/<char>/<part>.png` + first-pass binding jsons |
| `--out <dir>` | `--import` | Output root for imported art (default `SpriteBindings`) |
| `--scale <f>` | `--import` | Downscale factor for imported art (default `0.25`) |
| `--load <path>` | mode flag | Take viewer for a `.take.json` recorded in-game (Ctrl+R / Ctrl+S) |
| `--ref <clip>` | mode flag | Hermite reference-arc editor; loads/saves `ReferenceClips/<name>.json`. Arcs are authored in **game pixels** against the clip's draggable **entry/gate anchors** (green rings) — the runtime rescales from that span onto the obstacle it measures, so keys are free to sit before the entry or past the gate. `[` / `]` set the arc's **Duration** (seconds end to end — what animation clips pace against) |

Screenshot env vars (dev captures; the window renders a few frames, saves a PNG, and
exits): `MTILE_SHOT=<path>` works in **every** mode. Modifiers — editor:
`MTILE_SHOT_HELP`, `MTILE_SHOT_WIRE`, `MTILE_SHOT_NOSKEL`, `MTILE_SHOT_BODY`; bind editor:
`MTILE_SHOT_PREVIEW`, `MTILE_SHOT_CLIP=<clip>`, `MTILE_SHOT_LAYER=<layer>`;
take viewer: `MTILE_SHOT_HELP`, `MTILE_SHOT_FRAME=<n>`.

---

## Layout

The panels are Dear ImGui windows (`MTile.Demo/ImGuiRenderer.cs` is the MonoGame
backend), so they clip and scroll their own contents; the canvas in the middle is
unchanged MonoGame drawing, and every gesture on it works exactly as before.

- **Menu bar** — File / Clip / Edit / Scene / View, plus the live status on the right
  (clip, playhead state, unsaved markers). Every item has a keyboard equivalent.
- **Clips** (left) — the animation list grouped by `Type`, with a filter box and each
  entry's keyframe count. The selected clip is highlighted and scrolled into view.
- **Canvas** (center) — the rig at the current timeline position. Bright = an editable
  keyframe is active; dimmed = an interpolated (non-editable) frame.
- **Clip properties** (right) — type, duration, loop, region, reference arc, motion
  source, the current edit mode and endpoint/effect selection, and the wrapped warnings
  (guide hint, loop-seam mismatch).
- **Timeline** (bottom) — play/sample/delete buttons, the track with keyframe **bars**,
  the orange **playhead**, and the draggable annotation rows — one per contact span (with its
  weight curve drawn inside) and one per attachment, each with its own label and numbers.

Single-letter shortcuts are suppressed while a text field has focus (the name prompt,
the clip filter), and a click that lands on a panel never reaches the canvas.

---

## Selecting & navigating

| Input | Action |
|---|---|
| Click a sidebar row | Load that animation (renders its first keyframe) |
| Click/drag on the timeline track | Move the playhead (scrub / interpolate between keyframes) |
| Click a keyframe **bar** | Select it as the active, editable keyframe |
| Drag a keyframe **bar** | Move that keyframe in time |
| Drag a **contact span** (the coloured rows under the track) | Either end retimes that end; the body slides the whole window. Dragging the end past the right edge is how a stance is made to wrap the loop seam; a wrapped span's tail at the clip's head is grabbable too, and its end handle lives there (drag it back to the head's left edge to un-wrap). Where lanes overlap, the lane nearest the pointer is the one grabbed |
| Drag an attachment **span** (the rows below the contacts) | Same gesture. The rows all sit below the keyframe ticks, so a click down there never retimes a pose |
| `,` / `.` (or the timeline's `< key` / `key >`) | Step to the previous/next keyframe and make it editable |

### View navigation (canvas)

| Input | Action |
|---|---|
| **Wheel** | Zoom about the cursor — the scene point under the pointer stays under it (25%–800%) |
| **Middle-drag** | Pan the view |
| Arrow keys / `Home` | Nudge the pan (Shift faster) / recenter |
| `Ctrl+0` | Reset zoom to 100% |

The whole view zooms as one — rig, guides, tile grid, path, ghosts — and every drag converts
through the live scale, so editing at any zoom writes the same rig-unit values. The wheel over
a panel scrolls that panel instead.

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
| **IK drag** box (header) | Toggle the kinematics drag mode. While on, dragging a limb joint solves the limb's chain (up to the hip/chest) so the joint follows the cursor, biased toward the drag-start pose and last frame's solution, with the lower bone's bend kept on its starting side. A cross marks the target and the header row shows the miss in rig units when the target is out of reach. Esc mid-drag restores the drag-start pose. The root/torso, RESIZE, STRETCH and com-marker drags are unchanged |
| Drag joint (STRETCH) | Slide the joint along the bone's axis — writes the ratio as this **keyframe's `Stretch`** (pseudo-3D foreshortening; rotation and rig untouched). Signed: dragging past the parent joint flips the bone slightly negative, e.g. a hip strut at full leg swap |
| Drag **com marker** | Place the player against the fixed scenery **per keyframe**: com and skeleton travel with the cursor while the clip's guides stay put; the drag writes the active keyframe’s `body_path` placement, and scrubbing interpolates it — so the body visibly arcs over the refs (e.g. parkour clearing its block). Editor-only visualization, saved with the clip (`edref` additions; the runtime ignores them). Arrow keys pan everything together |

A clip can be shaped against a maneuver's **authored reference trajectory**, but it never
*rides* one: attach the arc as a Scene overlay to see it, then **Scene ▸ Map com to arc** to
write the clip's own `body_path` from it (see "Reference arcs" below). The clip owns its path
afterwards, so the com marker keeps editing it and nothing re-reads the arc behind your back.

Arcs are authored in game pixels against their own entry/gate anchors, so they map to the scene
at true scale. The reference block is scenery to position the arc *against*, never a retarget
target; the runtime does its own retargeting onto the obstacle it measures. **Clip and arc have
independent durations** — mapping advances along the arc at `τ · clipDuration/arcDuration`, so a
0.4 s clip on a 0.3 s arc reaches the gate at τ≈0.75 and overshoots after, unless you map
stretched.

---

## Scene (the menu bar's **Scene** menu)

The scene is the fixed reference geometry a clip is authored against (`Scene` in the clip
json: a ground line and axis-aligned blocks, in rig units, X right / Y down at canonical
right-facing) plus the clip's declared **motion source** (`Motion`). Every guide a clip shows
is its own authored data — nothing is synthesized — so the editor, `ClipSceneBake` and
`probe scenecheck` all read the same geometry. `probe new` starts a clip with the floor line
it is posed against; add blocks from there. Guides are reference data only — the runtime
never spawns them.

The menu is organised around the two things an endpoint can carry, because they are the
two shapes in the data — not around the order the features were written:

| Menu item | Action |
|---|---|
| *(header)* `<bone> end [id]` | The endpoint's identity. `(unnamed)` means no `NamedPoint` names it yet |
| Name / Rename this endpoint… | Give this location a stable id. Both kinds of addition hang off it, and contacts reference it rather than the bone — so a bone rename can't silently move a plant. You rarely need this by hand: adding a contact names the endpoint for you |
| Target: cycle (…) | Only shown when another bone's endpoint is coincident here (a child's Start is its parent's End) — switches which one the menu is about |
| **Add element** ▸ Knife / Custom… | An **add-on with its own frame**, hung off this endpoint's **named point** and drawn over a window of the clip. `Knife` is the one-step preset: a clip-local orientation bone, a point on its tip, and the `knife` attachment on that point. `Custom…` names any effect from `Assets/AnimationEffects/`. Because an element anchors on a point rather than a bone, it may sit at a point's **offset** — which contacts refuse, since the solver pins tips only |
| **Add contact** ▸ No slip / Planned support / External pin | **Labeled spline data** on this endpoint: an interval plus a weight curve, read by the solver and never drawn. No slip = `SelfPlant` (capture and hold a world point); Planned support requests a terrain target from the step planner; External pin needs a target supplied by gameplay. All three act **at the playhead** — if a span already covers it the source changes in place, so retyping a contact never retimes it |
| **Add contact** ▸ Clear the one at the playhead | Removes the span under the playhead, if any |
| **Add contact** ▸ New span: playhead → next key / whole clip | Where a newly authored span starts and ends. Keyframes are a decent first guess at a plant's extent, but only a guess — the span is draggable afterwards and owes them nothing |
| *(on this endpoint)* element / contact / point ▸ Select, Remove | Everything already here, each with its verbs. **Select** aims **U / I** and **Delete** at it, and selecting a contact opens its weight-curve editor. **Remove** on a point takes its contacts with it; on an element it takes the clip-local bone it was the last user of |

The two categories are the menu's whole structure: presets like *Knife* and *No slip* are the
**submenu** of their category rather than siblings of it. A `NamedPoint` is neither — it is the
anchor both hang off, which is why naming lives in the header.

Contacts reference the rig's or clip's **named points** (`Point`) — the only spelling. A bare
bone name in that field still resolves, as that bone's tip. A contact naming a point that does
not exist, or one carrying an offset or sitting at a bone's Start, is an error at solve time
rather than a quietly dropped contact.

A contact is an **interval**, not a keyframe annotation: `[Start, End)` on the clip's phase,
independent of where the keys fall. So a plant can start between keys, retiming a keyframe no
longer drags every contact keyed on it, and a stance crossing the loop seam is one span with
`End > 1`. Its bar under the timeline is coloured by source (green no slip, blue planned
support, orange external pin) and carries its **weight curve** drawn as a profile inside the
bar — an unauthored weight is an ease-in / hold / ease-out ramp over 15% of the span at each
end, which is what makes a foot swap a crossover. Two spans overlapping IS the crossfade;
there is no global feather width any more, so a short plant eases quickly and a long one
slowly.

**Selecting a contact bar opens its weight-curve editor.** The curve is authored on the span's
own normalized domain, so the editor's x axis is a *fraction of the span*, not clip time —
retiming the span stretches the shape rather than redrawing it. Drag a key to move it, drag its
amber tangent handle to set the ease, or **Auto tangent** to hand it back to the sampler (amber
keys are authored, blue are derived). **Add key** drops a handle at the playhead without moving
the line; the two end keys are pinned in time because they *are* the span's ends, and move
vertically only. **Reset to ramp** clears the curve back to unauthored; **Flat 1.0** pins it
at full weight with no ease. The orange playhead line shows the weight at the current frame
when the playhead is inside the span.

### Clip sprite attachments

Hover a joint and press **E** to assign an effect name (`knife` is supplied).
**Shift+E** removes one — it now deletes whatever is *selected*, falling back to the
nearest attached joint only when nothing is; the endpoint menu's `remove` item is the
discoverable form of the same thing.

An attachment's lifetime is a **span on the timeline**, one row per attachment under the
track: drag either end to retime it, drag the body to slide it, and read its numbers off the
label. **U / I** still set start/end to the playhead. A knife's trail window
(`TrailStart`/`TrailEnd`, a second lifetime nested inside the blade's) draws as a thin amber
line inside the span; sliding the span carries it along, and dragging an end clamps it back
inside, so it can never describe a trail outside the blade's own window. **Ctrl+S** saves. Space previews the moving trail;
scrubbing previews a single blade frame. See [KNIFE_ATTACHMENTS.md](../Plans/KNIFE_ATTACHMENTS.md)
for orientation, JSON fields, and shared PNG strip assets.
