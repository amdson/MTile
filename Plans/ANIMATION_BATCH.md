# Animation batch — clip authoring spec

Goal: bring **every gameplay clip** to a clean, on-rig state in one parallelized batch.
Scope of this batch = **Tiers 1+2**: re-author the existing clips clean on the
post-migration joint-chain rig, and author the missing **combat overlays**.
Movement-specific clips (hang / wall-slide / tumble) are **Tier 3, deferred**.

> Each row below is a first DRAFT of intent. **Refine the intent prose before launch** —
> the worker authors to whatever this doc says.
>
> ⚠️ **The specific combat numbers in Groups B/C (sweep angles, "active windows",
> knockback, reach scales) are NOT all in the code yet — treat them as placeholder feel
> hints, not facts.** For the real timing/handedness/geometry, the worker (and you, when
> refining) should read each action's state class in `Character/ActionStates.cs`
> (`CombatState.cs` / `ActionVars.cs`) directly. Where the code is silent, it's a design
> choice to make here, not a value to look up.

---

## How the batch runs

- **One worker per clip**, in its **own git worktree** (each runs `MotionProbeTests`,
  which rebuilds the test dll and writes `.probe/` — they would race in a shared tree).
- Each worker follows the **[anim-probe skill](../.claude/skills/anim-probe/SKILL.md)**
  loop verbatim: probe → author → re-probe digest → iterate until the acceptance gate
  is green. The skill is the how-to (rig conventions, FK measurement, contact/cadence
  gotchas); this doc is the *what* (per-clip intent).
- After the fan-out, a **verify+merge pass** collects every `.json` back onto one
  branch and runs the full suite once (`dotnet test MTile.Tests`).

## Authoring reference (recap — skill has the full set)

- **Pure joint chain, T·R·S.** A bone attaches at its parent's tip; authored
  `Rotation` is the **full local angle** (`bindRot` + swing), NOT a deviation.
- **Bind orientations** (rest `Rotation`, rad): chest `-π/2`, head `0`, arms `+π`
  (hang), legs `+π/2` (down), `*_lower` `0`, foot `≈-1.33`. Forward swing = toward +X.
  **Measure every sign with the digest** — don't trust remembered signs.
- **Knife is fixed to `arm_r_lower`** (right forearm). Every knife slash/stab is a
  **right-arm** move; combo variety comes from sweep *direction* (CW vs CCW) and
  wind-up, not from switching arms. (Confirm/"correct me" on the per-clip notes that
  assume this.)
- **Region masks** decide which bones a clip writes:
  - `FullBody` — base locomotion / movement clips (own the legs).
  - `UpperBody` — combat overlays composited over a locomotion base; **legs/hip are
    masked, so their stored values don't render.** Author a *neutral* leg pose anyway
    so `.probe/<clip>.digest.md` reads clean (no false recurvatum flag).
  - **`OffRegionWeight` (0..1, default 0)** — graded weight for bones OUTSIDE the
    clip's Region. 0 = hard mask (legacy). >0 lets a "whole-body" overlay lightly
    drive its off-region bones: a Pulse cast authored `Region=UpperBody` +
    `OffRegionWeight=0.3` braces the legs at 30% without taking them over (the base
    locomotion still owns 70% + keeps cadence). Off-region legs at any weight DO show,
    so pose them deliberately (and flag-free).
- **Overlay time is remapped** to span the action's `OverlayDuration` ([0,1] over the
  move's lifetime). The clip's own `Duration` matters only for editor playback; align
  pose *phase fractions* (windup/active/recovery) to the listed hitbox window.

## Global acceptance gate (every clip)

1. `.probe/<clip>.digest.md` has **no FLAGS** (no recurvatum; knees forward).
2. Knife/hand reaches the intended apex at the **active-window phase** (diff vs the
   ref clip shows a clear departure — not "reads like idle").
3. **FullBody clips:** planted foot bob ≈ 0; swing foot clears; for `walk/run/walkback`
   the behavioral `RealWalkJson/RealRunJson_AdvancesPhase` tests still pass.
4. `UpperBody` clips: masked leg pose is neutral & flag-free; upper-body silhouette
   matches the intent; doesn't fight the base locomotion at the mask seam (chest/hip).

---

## Group A — base locomotion (FullBody, re-author / polish)

| clip | status | ref-diff |
|---|---|---|
| `idle` | legs flag recurvatum → fix | self / rest |
| `walk` | clean — polish only | — |
| `run` | clean — polish only | walk |
| `walkback` | clean — polish only | walk |
| `crouch` | clean — verify | idle |
| `jump` | legs flag recurvatum → fix | fall |
| `fall` | legs flag recurvatum → fix | jump |
| `vault` | corrupted/both knees back → **rebuild** | run |

- **idle** — weight centered over both feet, soft forward knees, arms hang near
  `bindRot`. Tiny breathing bob (hip/chest ≤ ~1px, ~0.5 Hz via IdleBobHz). Both feet
  planted, no sweep. Fix: legs currently recurvatum — set `leg_*_lower` so the knee
  cross is positive (see skill knee test).
- **walk** — KEEP current (clean): half-cycle-offset legs, single planted contact per
  keyframe, gentle contralateral arm swing (±~0.4). Polish only if digest bob > ~0.5.
- **run** — KEEP shape: longer stride, flight windows, bent-elbow arm pump, com lift.
  Rig limit noted: swing foot grazes (no heel tuck) — acceptable.
- **walkback** — same cadence as walk, body leans **back** (`WalkBack` lean sign is
  negative), reversed sweep; legs stay knee-forward.
- **crouch** — lowered hip, deep knee bend (knees still forward), torso slightly
  forward, arms tucked. Static (no contacts cycle).
- **jump** — launch pose: hip rising, legs extending downward then tucking, arms up/out
  for lift. One-shot (ClipTime). Fix recurvatum on the tuck.
- **fall** — descending: legs trailing, slight tuck, arms out for balance. Should diff
  clearly from `jump` (different leg gather). Fix recurvatum.
- **vault** — REBUILD from scratch (current file corrupt). Parkour **legs base**: a
  planted-then-push leg drive that carries the body up-and-over a corner; pairs with
  the `vaulthands` overlay. Reference `run` for clean leg geometry. τ is spatial
  (body-vs-corner), so author the *pose sequence*, not a clock.

## Group B — existing combat overlays (UpperBody, re-author clean on rig)

Knife on `arm_r_lower`. Legs masked → give a neutral flag-free leg pose. For real
timing/active windows, read the action's state class (don't trust the placeholder
columns below). Diff each vs `idle` to confirm the arm actually departs.

| clip | Type | dur | active window | sweep | reach | notes |
|---|---|---|---|---|---|---|
| `groundslash1` | GroundSlash1 | 0.14 | 0.20–0.70 | CCW ~100° | med | combo opener; HOLD (low knockback). Wind-up draws knife back-up, strike sweeps down-forward. |
| `groundslash3` | GroundSlash3 | 0.18 | wide | CCW ~160° | long | finisher; big LAUNCH. Full body lunge, widest arc, follow-through past the body. |
| `stab` | Stab | 0.60 | 0.30–0.60 | linear thrust | long | wind-up 0–0.18 (draw back), strike 0.18–0.36 (extend along facing), hold 0.36, retract. Arm + knife colinear, forward. |
| `airslash1` | AirSlash1 | 0.12 | 0.20–0.70 | CCW ~110° | tight | airborne opener; compact, fast. |
| `airslash2` | AirSlash2 | 0.14 | wide | **CW** ~140° | med | air finisher; reverse sweep vs airslash1, wider. |
| `airturnslash` | AirTurnSlash | 0.11 | mid | CCW ~60° | long | **flips facing mid-swing** — author the swing so it reads through a facing flip; narrow & fast. |
| `crouchslash` | CrouchSlash | 0.16 | 0.20–0.70 | CCW ~90° | long | from crouch: low stance, extended forward reach. Masked legs may stay neutral, but torso reads crouched. |
| `grabbedslash` | GrabbedSlash | 0.16 | 0.20–0.70 | CCW ~80° | short | struggle while grabbed; tight, no lunge; cramped arm. |
| `guardretaliate` | GuardRetaliate | 0.10 | early | CCW ~70° | med | snappy counter from parry; quick forward jab-slash. |
| `slash1` | (legacy) | — | — | — | — | legacy `Slash1` clip with no live action. **Decide: retire or remap to GroundSlash2.** Default: skip (retire). |
| `wave` | Wave | — | — | — | — | emote, not combat. Leave as-is unless you want a polish pass. |

## Group C — new combat overlays (author from scratch)

Default `Region=UpperBody` unless marked FullBody (whole-body cast/stance). Legs masked
→ neutral pose. Set clip `Type` to the exact action name (matches `CurrentActionName`).

| clip (new file) | Type | dur | shape | body | region |
|---|---|---|---|---|---|
| `groundslash2` | GroundSlash2 | 0.13 | **CW** ~110°, tighter than S1 | ground, slight lunge; combo mid-step — reverse sweep so S1→S2→S3 alternates direction | UpperBody |
| `airspinstab` | AirSpinStab | 0.60 | linear thrust + **facing flip**, backward swipe variant of stab | airborne dive along stab vector | UpperBody |
| `pulse` | PulseAction | 0.70 | both arms sweep out into an **expanding ring** cast; active 0.15–0.55 | hovering planted stance (low-gravity "cast"); no lunge | UpperBody +brace≈0.3 |
| `guard` | GuardAction | held | static **shield stance**, both forearms raised across body | grounded, braced, slight crouch; held indefinitely (loop a near-static pose) | UpperBody +brace≈0.4 |
| `grab` | GrabAction | 1.2 hold + 0.12 throw | both arms reach forward to **seize**, then a throw fling | rooted heavy stance during hold; throw = sharp arm extension along grab dir | UpperBody |
| `energyball` | EnergyBallAction | 0.15 | quick right-arm **throw** toward aim, instant release | no lunge; brief wind + toss | UpperBody |
| `grenade` | GrenadeAction | 0.15 | right-arm **lob/toss** (overhand), instant | no lunge; quick over-shoulder throw | UpperBody |
| `beam` | BeamAction | 0.35 charge + ≤0.55 fire | right arm **extends and locks** toward aim; charge gather → steady beam-emit hold | planted; direction locks at fire | UpperBody |
| `lobbedarea` | LobbedAreaAction | ≤1.8 charge + release | right-arm **overhand charge** then ballistic launch (upward arc) | heavy charge stance → release | UpperBody |
| `blockready` | BlockReadyAction | charge 0–2.0+ | both hands **building gesture**, palms shaping at place-range; grows with charge | light paint stance → committed brace past ~1.0s | UpperBody +brace≈0.3 |
| `blockeruption` | BlockEruptionAction | ~0.6 sample | both hands sweep a **gesture path** (cursor sampling) then erupt | gesture-driven sweep; heavy during charge | UpperBody +brace≈0.3 |

### Open questions for you (resolve before launch)
1. **Handedness on combo S2 / AirSlash2** — I assumed knife stays on the right arm and
   only the *sweep direction* reverses (CW). Correct, or do these visually cross to a
   left-hand grip?
2. **`slash1` legacy clip** — retire it, or repurpose the file as `groundslash2`?
3. ~~**`pulse` / `guard` / `block*` as FullBody**~~ — **RESOLVED:** graded
   `OffRegionWeight` is now wired, so these become `Region=UpperBody` overlays with a
   partial lower-body brace (e.g. `pulse` OffRegionWeight≈0.3) instead of FullBody clips
   that kill the locomotion legs. Pick the brace weight per clip when authoring; I've
   left the Group C "region" column as UpperBody+brace for these.
4. **Charge-action clips (`beam`, `lobbedarea`, `block*`)** play over a *variable* charge
   time. Overlay τ remaps to `OverlayDuration`; confirm these expose a sensible duration,
   or whether the hold pose should just freeze at the charged frame (like the spatial-τ
   vault-hands pattern).
