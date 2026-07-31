# Pseudo-3D stretch & editor reference placement

Two related systems added 2026-07-30, both in service of the biped_rabbit sprite skin:
a per-keyframe **bone-length Stretch channel** (pelvis/shoulder yaw foreshortening, so
strutted rigs don't draw twisted limbs), and the animation editor's **reference
placement** stack (per-keyframe body-vs-scenery placement + riding the maneuver's
authored reference trajectory). Everything here is render/authoring-side; the sim
never reads any of it.

---

## 1. The Stretch channel

`PoseBoneEntry` (one bone in one keyframe) gained an optional `Stretch` — a multiplier
on the rig bone's `Length`:

```json
{ "Bone": "hip_r", "Rotation": 0.0, "Stretch": -0.2 }
```

- **Semantics**: `local translation = UnitX · rigLength · Stretch`. It scales only the
  bone's own offset — the subtree **translates but does not shrink** (`Scale` stays 1;
  a strut foreshortens while the leg hanging off it keeps full size). Negative values
  swing the tip past the joint (a strut slightly behind the depth axis).
- **Null/omitted = 1**: legacy clips round-trip byte-identical; `Capture` re-derives it
  from the pose, so editor keyframe sampling (`K`), clones, and probe `retarget` carry
  it through.
- **Interpolation**: `SampleSmooth` blends translation/scale with a smoothstep between
  the bracketing keys — C1 at keys with zero tangent, the right shape because stretch
  extremes are authored ON keys. (`SampleNormalized`'s plain lerp path carries it too.)
- **Solver interaction**: the solver *consumes* stretch, never solves for it. Constraint
  residuals (contact pins, no-penetration, vault grips) FK through the stretched pose,
  so pins absorb socket motion into leg Δθ — that IS the effect. The one gap: the
  cadence phase Jacobian (`SampleAngularVelocity`) is rotation-only, so the socket's
  d(translation)/dφ is invisible to it — a few-percent derivative error the LM solve
  re-converges through. If run ever shows a once-per-stride cadence tick, ease run's
  seam stretch keys (its wrap segment swings ~full range in 10% of phase).

### Authoring surfaces

| Surface | Use |
|---|---|
| `MTile.Probe -- bakeyaw …` | Law-driven bake over whole clip pools (below) |
| `MTile.Probe -- stretch <clip> <t> <bone> <s>` | Set one value precisely (`1` clears) |
| Editor **STRETCH** mode (Tab ×2) | Drag a joint along its bone's axis; signed — drag past the parent joint to go negative. Pose-only, per-keyframe. (RESIZE, by contrast, edits the **rig's** rest Length and hits every clip.) |

## 2. The pelvis-yaw mapping (`bakeyaw`)

As the legs scissor, the pelvis yaws about the implicit depth axis; in side view both
hip struts (antiparallel ⇒ one shared factor) project as

```
s(χ̂) = cos(ψ₀ − ψₐ·χ̂) / cos(ψ₀)
```

- **χ̂** — leg scissor: thigh world-angle difference (right − left, measured from each
  strut's socket), divided by the fixed `--ref` amplitude (default 1.0 rad) and clamped
  to [−1, 1]. Fixed normalization is deliberate: per-clip max would blow idle's
  micro-scissor up to full pelvis flips.
- **ψ₀** (`--view`, default 18°) — the art's base "slightly facing the viewer" yaw.
  Dividing by cos ψ₀ makes the *authored* strut length the neutral-scissor length.
- **ψₐ** — yaw swing, solved from the pin `s(−1) = --swap` (default **−0.2**: at full
  leg swap the struts flip a touch past the depth axis, preserving the 3/4 read).
  Defaults give ψₐ ≈ 83°.
- **Shoulders** — same law driven by the *arm* scissor (naturally counter-phased, no
  sign flip), scaled by `--shoulderamp` (default 0.5) since torso counter-rotation is
  much smaller than pelvis yaw. `--noshoulders` skips them.

Examples:

```bash
P=MTile.Probe/bin/Debug/net8.0/MTile.Probe.dll
dotnet $P --rig biped_rabbit bakeyaw walk --dry     # preview one clip's numbers
dotnet $P --rig biped_rabbit bakeyaw                # bake the whole pool
dotnet $P --rig biped_rabbit bakeyaw --ref 1.4      # softer everywhere
dotnet $P --rig biped_rabbit bakeyaw --shoulderamp 0.3
```

Walk bakes to `s: −0.2 → 0.96 → 0.44 → 0.53 → −0.2` across the stride (seam keys match,
loops stay cyclic). Re-running overwrites from the law, so knob changes re-bake cleanly;
hip values and shoulder values are printed per key (`χ−1.00→−0.20/sh+1.01`).

Known behavior to sanity-check in game, not "bugs": static-stagger clips (idle, crouch,
guard) hold a constant foreshortening because their stance IS a scissor (idle s ≈ 0.18);
UpperBody overlay clips carry inert hip entries (masked at runtime) and live shoulder
values matching their held arm pose.

## 3. Editor frame model (com / hip / pan)

The editor renders com-anchored clips the way the game places them
(`root = anchor − com·scale`). Three controls, three frames:

| Control | Frame it moves | Data |
|---|---|---|
| **Arrow keys** (Home resets) | The world — everything pans together | none |
| **Drag com marker** | The player's base frame vs. the fixed scenery, **per keyframe** | `edref` addition (editor-only) |
| **Drag root/hip joint** | The body *within* the com frame — skeleton moves, com marker + floor hold still | the keyframe's `com` channel (the game's vertical anchor) |

**`edref`** is a hidden root-space Point addition written by the com drag: each drag
places that keyframe's body against the floor/vault block, and scrubbing interpolates
the track, so the body visibly arcs over the scenery. It saves with the clip, follows
retimes, is inherited by `K`/`C`, and the runtime ignores it (additions are read by
name). First drag on a fresh keyframe seeds from the currently displayed placement.

The **vault block**'s scene position persists in `SkeletonStates/.editor_view.json`
(machine-local view state, gitignored — the per-keyframe player placement lives in the
clip itself, not here).

## 4. Reference trajectories (`ReferenceArc`)

A clip can ride the maneuver's *authored* arc instead of hand-placed `edref` keys:

```json
{ "Name": "dropdown", "Type": "Dropdown", "ReferenceArc": "dropdown", ... }
```

```bash
dotnet $P --rig biped_rabbit refarc dropdown dropdown   # bind
dotnet $P refarc dropdown none                          # clear
```

- Resolution: `ReferenceClips/<name>.json` (the file `--ref <name>` edits) wins, else
  the baked `ReferenceClipRegistry` default (`ledge_pull`, `dropdown`). Header shows
  `arc <name>` (or `arc <name> (missing)`).
- While scrubbing, the body's scene placement = the normalized Hermite arc at the
  playhead, mapped by a per-Type editor gate (arcs are entry (0,0) → gate (1,−1); the
  gate's *world* direction is only known at runtime from the measured maneuver):
  **Vault** → onto the reference block's top (dragging the block rescales the ride
  live); **Dropdown** → a ledge-height down; anything else → a maneuver-height up.
  Constants in `DemoGame.ArcOffset`.
- Any hand-dragged `edref` placement **adds on top** as a nudge.
- Editor visualization only. The sim follows the arc through `ReferencePath`; nothing
  runtime reads `ReferenceArc`.

There is no vault arc file yet — author one with
`dotnet run --project MTile.Demo -- --ref vault`, then `refarc vault vault`.

## 5. Misc editor additions (same batch)

- **Clip-list scrollbar**: wheel over the sidebar (2 rows/notch), proportional
  click/drag thumb on its right edge; selection (CLI `-- <clip>` open, `N`/`C`) auto-
  scrolls into view.
- **CONTROLS.md** carries the user-facing versions of all of the above (CLI flags,
  edit-mode table, com/root/arrow frame table).
- **`GameConfig.AnimationRig`** selects the in-game rig + clip pool + skin bake target
  (render-only). `game_config.json` currently runs `biped_rabbit` with the rabbit/badger
  bindings; the badger binding is still authored on biped and needs a
  `--bind badger --rig biped_rabbit` re-drag before enabling the second player.
