# MTile.Demo — Skeleton Animation Editor

A standalone tool for authoring the skeletal animations the game plays (walk, idle,
jump, …). It edits `AnimationDocument` JSON files in the repo's `SkeletonStates/`
folder — the same files [CharacterAnimator](../Animation/CharacterAnimator.cs) loads
at runtime. The editor never touches the simulation.

Run it:

```bash
dotnet run --project MTile.Demo
```

Content is **authored-only**: on launch the editor loads the rig from
`Skeletons/biped.json` (and **fails fast with a clear error if it's missing** — no
procedural fallback, no autogeneration) plus every `SkeletonStates/*.json` exactly as
it exists on disk. New clips are created explicitly with `N` (new) or `C` (clone);
restoring lost content means restoring the files from git.

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
| **Tab** | Cycle edit mode: **ROTATE → TRANSLATE → RESIZE** |
| **F** | Flip the animation across a vertical axis — mirrors the **data** (persists on save). Press again to flip back. Use it to make a clip face the game's canonical direction (the runtime mirrors by player facing) |
| Drag joint (ROTATE) | Rotate the bone about its parent, preserving limb length; the subtree carries along |
| Drag joint (TRANSLATE) | Move the joint freely in the parent frame (children keep their orientation) |
| Drag joint (RESIZE) | Move the joint to the cursor — changing the limb's **length** and angle — rolling the bone's rotation so the subtree follows |

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
| **T** / **Shift-T** | Cycle the clip's Type forward/back through the known categories: movement clips (`Idle`, `Walk`, …) plus every action state name (`GroundSlash1`, `StabAction`, …). The runtime binds action overlay clips by exact action name |

---

## File operations

| Input | Action |
|---|---|
| **Ctrl-S** | Save **all** animations to their JSON files (`*unsaved*` clears) |
| **N** | Create a new (empty) animation |
| **C** | Clone the selected animation — deep-copies all keyframes/contacts into a new clip named `<name>_copy`, selected and ready to edit (saved as a separate file on Ctrl-S). Use it to fork a variant, e.g. derive a run from the walk |
| **Escape** | Quit |

Edits are kept in memory until `Ctrl-S`; the header shows `*unsaved*` while dirty.
