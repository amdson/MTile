# Animation solver — future directions

Speculative roadmap from the 2026-07-14 review: where the constraint-solver
architecture can go next, ordered roughly by leverage-per-effort. None of this is
committed; each section is a sketch with the open questions that would shape a real
plan. Prerequisite consolidation lives in `Plans/ANIMATION_POLISH_PLAN.md`; the
already-planned solver items (d.x + horizontal ComOffset, JointLimits, local-SDF
NoPenetration) remain tracked in `ANIMATION_SOLVER_PLAN §11.6 Phase 4` and are
referenced, not duplicated, here.

## 0. The invariant (a direction NOT to take)

Nothing animation-side ever becomes sim-relevant: no animation-driven hitboxes, no
gameplay reading rendered poses, no grab positions from the rig. The render-only
`CharacterAnimSample` boundary is the most valuable property the system has — it's what
makes hot-reload tuning, non-deterministic iteration counts, and rollback safety free.
Every direction below stays on the render side of it.

---

## 1. Terrain-aware feet (the payoff move)

The game's identity is *reshaped terrain*, which is exactly where canned locomotion
looks worst and a constraint solver visibly shines. Two independent steps, (a) first:

**(a) Ground-snapped contact targets — cheap, high visibility.** Today a contact target
is captured from the CLIP's world tip at plant time, so feet plant at the authored
height regardless of the actual ground. Instead, at capture (`RefreshContacts`),
raycast the `ChunkMap` downward under the tip (a render-only read — the plan's §1
already blesses it) and snap `target.Y` to the real surface; the vertical ground-hold
row + δ then make the body ride the true terrain. Uneven ground, slopes, stairs, and
fresh combat rubble get correct foot planting with machinery that already exists.
- Open: clamp the snap distance (a ledge under one foot shouldn't fold the rig —
  beyond ~half a tile, fall back to the authored height); whether the no-slip row's
  target.X should also project along a sloped surface tangent rather than pure
  horizontal.

**(b) Local-SDF NoPenetration** (`ANIMATION_SOLVER_PLAN §4.5`, the planned general
version). Build a small per-frame SDF from tiles near the player; one-sided rows
`max(0, margin − sdf(q))` with `∇sdf` Jacobians, replacing the single wall half-plane.
Same constraint shape as `NoPenetrationConstraint`, so the FD oracle template carries
over. Heaviest item here; (a) does not depend on it.

## 2. Walk↔Run crossfade + cadence/speed coupling

The multi-motion machinery (`ANIMATION_SOLVER_PLAN` Phase 4) is scaffolded but
unexercised; the pop at `RunSpeedThreshold` is its natural first user, and it forces
the open question from the Phase-1 follow-up (no-slip alone under-constrains cadence).
- Two locomotion motions sharing one φ, blend weight a smooth function of speed. Note
  this is a BASE-layer crossfade (full-body, weight on every bone), not an overlay
  slot: sample both clips and lerp before compose; the Δφ Jacobian's ω becomes the
  blended `Σ w_m·ω_m` (both clips' `SampleAngularVelocity`).
- Requires the clips to be phase-ALIGNED (same φ ⇒ same foot down) — an authoring
  contract worth checking in a test (contact labels of walk/run agree in phase).
- A soft **stride/speed residual** `√w·(Δφ·strideLength − |v|·dt)` ties cadence to body
  speed where no-slip leaves it free (flight windows, low-traction moments), and gives
  the blend a principled cadence through the crossfade.

## 3. Procedural hit reactions (instead of authoring Stunned/Tumble clips)

`ANIMATION_CLIP_GAPS.md` wants stagger/tumble/hitstun clips; the solver suggests a
cheaper, more interesting answer: hitstun as a **constraint regime**, not a clip.
- Sketch: on hit, the sample carries the knockback impulse (render-only scalar+dir).
  The animator drops into a "loose mode" — pose-prior weights way down, an
  impulse-shaped prior on Δθ (bones pushed along the lever of the hit direction,
  decaying), smoothness carrying the momentum. The base clip underneath is whatever
  was playing — the character visibly *crumples out of* their current pose, aligned
  with the actual hit direction, which authored clips can never do.
- Risky/experimental: could read as mushy ragdoll rather than punchy hitstun. Prototype
  behind a flag against a placeholder authored clip and pick per-state (Stunned may
  want authored + procedural layered; Tumble is probably the better pure-procedural
  candidate since it's airborne and rotation-dominated).
- Depends on: polish item 1 (smoothing in-solve) — the effect IS a prior/smoothness
  trade, so it wants the unified objective.

## 4. Cross-character pins (grabs)

`GrabAction` has no clip, and `FixedPointConstraint` already accepts arbitrary world
points: pin the grabber's hand to the victim's chest-bone tip, read render-side from
the victim's animator pose. Grabs then read physically for ~zero new solver code, and
the victim's `GrabbedSlash` overlay already exists.
- Open: solve ordering (grabber reads victim's pose from THIS frame if the victim
  updates first, else one frame stale — likely invisible at 30fps; pick a fixed update
  order anyway for stability). Host-level plumbing: the sample would carry an optional
  "partner pin" `(bone, world target)` resolved by the host, keeping animators unaware
  of each other.

## 5. Generalize the aim constraint + clip metadata

`ActionAimConstraint` is stab-specific only in its wiring: "rotate this bone-pair
vector onto a direction" also covers beam aiming (`BeamAction`), grenade throw windup,
and guard facing. The blockers are hardcoded bone names (`arm_l_lower`/`arm_r_lower`
aim pair, `VaultGripBone`, grip window constants).
- Move them into clip JSON: an `AnimationDocument` gains optional
  `AimBones: [L, R]`, `GripBone`, `GripWindow: [t0, t1]` — the clip knows which limb it
  drives. `ResolveActionAim`/`ResolveMovementPins` read the bound clip's metadata
  instead of constants. New aimed/gripping clips then need zero animator changes.

## 6. Tooling: solver debug HUD

The next phase of this system is tuning-heavy ("weight tuning is where this kind of
solver lives or dies" — the plan's own risk list), and every direction above multiplies
that. Cheap, compounding investment:
- Draw the live constraint set in-world: contacts (already have the plant marker),
  pins + reach vectors, surface half-planes, the aim vector vs û*.
- `SolveScaleReport` as an on-screen overlay (it exists; it's just test-only today).
- Wire it into the recorder scrub (Ctrl+R/Ctrl+P): step frame-by-frame through a
  capture with the constraint overlay on — post-hoc inspection of any glitch.

## 7. Performance envelope (when characters multiply)

Fine today (n≈15 vars, m≈40–90 rows, one character). If enemies get rigs:
- **Adaptive globalization**: skip the 10-point Δφ seed search when the warm start's
  cost is already low (steady-state locomotion) — it exists for cold starts/handoffs.
- **Solve LOD**: distant/many characters cap LM iterations or drop to the smooth-only
  fast path (post polish-item-1, that path exists naturally).
- Budget check: each LM iteration is ~2–10 full FK passes; measure before assuming —
  a dozen bipeds is likely still trivial on desktop, less certain under WASM (the
  plan's AOT note applies).

---

## Rough sequencing opinion

Tooling (6) and terrain feet (1a) first — one multiplies iteration speed on everything
else, the other is the most player-visible win per line of code. Then walk↔run (2) as
the first real multi-motion exercise, cross-character pins (4) alongside a Grab clip
push, aim generalization (5) whenever the next aimed action clip is authored, and
procedural hit reactions (3) as the experimental track once polish item 1 has landed.
