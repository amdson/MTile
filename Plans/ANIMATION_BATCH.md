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

- **One worker per clip**, in its **own git worktree** (isolates each worker's build + edits so
  parallel workers don't race).
- Each worker follows the **[anim-probe skill](../.claude/skills/anim-probe/SKILL.md)** loop:
  probe → author → re-probe → iterate until the acceptance gate is green. The skill is the how-to
  (rig conventions, FK measurement, contact/cadence gotchas); this doc is the *what* (per-clip intent).
- **Build the probe INSIDE your worktree and invoke that DLL.** The probe reads/writes the
  `SkeletonStates/` of the checkout its DLL lives in, NOT your cwd — running the main
  checkout's probe binary silently authors your clip outside the worktree (this bit a
  calibration worker; the clip nearly missed its commit).
- **⚙ wiring clips: pre-wire before fan-out.** Two calibration workers adding `AnimClip`/
  `AnimTag` values + `SelectClip` branches in parallel produced a guaranteed merge conflict
  on the enum line. For the batch: land ALL enum values and `SelectClip` branches in one
  commit up front, then workers only author clip JSON (which never conflicts).
- **Use the FAST probe for the edit→verify loop** — `dotnet build MTile.Probe` once, then
  `dotnet run --project MTile.Probe --no-build -- digest <clip>` (~1–3s, prints to stdout, NO rebuild —
  clips load at runtime). Do **NOT** run `dotnet test …MotionProbeTests` every iteration (it rebuilds the
  whole test project — the main cost sink in the first batch). Reserve a `Zzz` test harness only for
  custom IK measurement (one build), not for verification.
- **Model assignment (calibrated 2026-07-18, n=1 per cell — directional):** spec'd overlay
  clips → **Sonnet** (guard: green in 5/6 cycles, fastest+cheapest of the four). ⚙ wiring /
  FullBody / unspecced clips → **Opus** (both Opus tasks landed in-budget with the deepest
  doc feedback; Sonnet *did* succeed on the tumble wiring — correct load-bearing branch
  ordering — but ran ~50% over the iteration budget on the clip geometry).
- **Iteration budget:** hit the gate within ~6 edit→digest cycles. If a clip isn't green by then,
  return your best version + a one-line note on what's off — don't churn; one hard clip shouldn't
  dominate the batch.
- After the fan-out, a **verify+merge pass** writes every returned clip JSON back to `SkeletonStates/`,
  regenerates digests, and runs the full suite once. **If running unattended, have each worker COMMIT
  its own clip** so a mid-run stall preserves finished work instead of re-running everything (the first
  batch stalled overnight and lost an entire run to this).

## Authoring reference (recap — skill has the full set)

- **Pure joint chain (FK = `R·T·S` per local).** A bone attaches at its parent's tip; authored
  `Rotation` is the **full local angle** (`bindRot` + swing), NOT a deviation. **`world[i].Translation`
  IS the bone's far tip** (children attach there) — never add `Length` along +X to "get a tip".
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
    so `.probe/<clip>.digest.md` reads clean (no false recurvatum flag). **Paste this
    idle lower-body block verbatim into every keyframe** — it's already flag-free, so the
    legs cost ZERO iteration (don't re-derive them):
    ```json
    { "Bone": "hip",         "Rotation": 0 },
    { "Bone": "leg_l_upper", "Rotation": 1.18 },
    { "Bone": "leg_l_lower", "Rotation": 0.62 },
    { "Bone": "foot_l",      "Rotation": -1.3258 },
    { "Bone": "leg_r_upper", "Rotation": 1.62 },
    { "Bone": "leg_r_lower", "Rotation": 0.2 },
    { "Bone": "foot_r",      "Rotation": -1.3258 }
    ```
  - **`OffRegionWeight` (0..1, default 0)** — graded weight for bones OUTSIDE the
    clip's Region. 0 = hard mask (legacy). >0 lets a "whole-body" overlay lightly
    drive its off-region bones: a Pulse cast authored `Region=UpperBody` +
    `OffRegionWeight=0.3` braces the legs at 30% without taking them over (the base
    locomotion still owns 70% + keeps cadence). Off-region legs at any weight DO show,
    so pose them deliberately (and flag-free).
- **Overlay time is remapped** to span the action's `OverlayDuration` ([0,1] over the
  move's lifetime). The clip's own `Duration` matters only for editor playback — EXCEPT
  for actions with no `OverlayDuration` override (e.g. `BlockEruptionAction`), where the
  animator falls back to the clip's `Duration`, which then paces real gameplay: check
  the action class before assuming Duration is cosmetic. Align pose *phase fractions*
  (windup/active/recovery) to the listed hitbox window.

## Global acceptance gate (every clip)

1. `.probe/<clip>.digest.md` has **no FLAGS** — neither recurvatum NOR **STEEP-interval**
   (bone travel ≥ 8 rad/phase between adjacent keys; the digest's "walk max ≈ 4" is a
   reference stat, not the threshold). STEEP is the usual blocker on fast overlays —
   budget angular travel across keys; `energyball` (≈ 6.3 max) is the ceiling to match.
2. Knife/hand reaches the intended apex at the **active-window phase** (diff vs the
   ref clip shows a clear departure — not "reads like idle").
3. **FullBody clips:** planted foot bob ≈ 0; swing foot clears; for `walk/run/walkback`
   the behavioral `RealWalkJson/RealRunJson_AdvancesPhase` tests still pass.
4. `UpperBody` clips: masked leg pose is neutral & flag-free; upper-body silhouette
   matches the intent; doesn't fight the base locomotion at the mask seam (chest/hip).

---

## Group A — base locomotion (FullBody, re-author / polish)

> ⚠️ The status column below is a stale snapshot — **run `MTile.Probe -- list` for live
> flags before assigning workers.** As of 2026-07-18: `idle`/`jump`/`fall`/`vault` digest
> CLEAN (the fix rows below are done), while `run`, `stab`, `pulse`, and most slashes
> (`airslash1`, `airturnslash`, `crouchslash`, `grabbedslash`, `groundslash1`,
> `groundslash3`, `slash1`) currently FLAG.
>
> 👁 **Human review note (amdson, 2026-07-18): the digest is not the last word — in
> either direction.** `jump`, `fall`, and `idle` should be checked in the viewer/game
> and possibly recreated even though they digest clean (the gate catches geometry
> errors, not "reads wrong"). Conversely, **`run` and `wallslide` are confirmed GOOD
> from play — treat them as exemplar clips** (reference/diff targets, style guides for
> new FullBody work) even though `run` currently FLAGS (the known, accepted rig-limit
> swing-foot graze — do NOT re-author `run` to chase that flag). `run`'s only open item
> is the polish edit in CLIP_BACKLOG (more pronounced jump mid-stride), which should
> preserve its current feel.

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
  `bindRot`. ~~Tiny breathing bob~~ (NOT AUTHORABLE: bones only rotate — a vertical bob
  cannot be expressed in clip keyframes on this rig, and IdleBobHz on a 1-kf clip is a
  no-op; the single static keyframe is correct). Both feet planted, no sweep. Fix: legs currently recurvatum — set `leg_*_lower` so the knee
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
| `guard` | GuardAction | held | ~~static shield stance~~ **DONE** (calibration run) | both forearms raised, brace 0.4, 2s micro-sway loop | UpperBody +brace≈0.4 |
| `grab` | GrabAction | 1.2 hold + 0.12 throw | both arms reach forward to **seize**, then a throw fling | rooted heavy stance during hold; throw = sharp arm extension along grab dir | UpperBody |
| `energyball` | EnergyBallAction | 0.15 | quick right-arm **throw** toward aim, instant release | no lunge; brief wind + toss | UpperBody |
| `grenade` | GrenadeAction | 0.15 | ~~right-arm lob/toss~~ **DONE** (calibration run) | raised overhand lob — a true "over-shoulder" cock-back is geometrically STEEP-flagged on this rig | UpperBody |
| `beam` | BeamAction | 0.35 charge + ≤0.55 fire | right arm **extends and locks** toward aim; charge gather → steady beam-emit hold | planted; direction locks at fire | UpperBody |
| `lobbedarea` | LobbedAreaAction | ≤1.8 charge + release | right-arm **overhand charge** then ballistic launch (upward arc) | heavy charge stance → release | UpperBody |
| `blockready` | BlockReadyAction | charge 0–2.0+ | both hands **building gesture**, palms shaping at place-range; grows with charge | light paint stance → committed brace past ~1.0s | UpperBody +brace≈0.3 |
| `blockeruption` | BlockEruptionAction | ~0.6 sample | both hands sweep a **gesture path** (cursor sampling) then erupt | gesture-driven sweep; heavy during charge | UpperBody +brace≈0.3 |

### Open questions for you (resolve before launch)
1. **Handedness on combo S2 / AirSlash2** — I assumed knife stays on the right arm and
   only the *sweep direction* reverses (CW). Correct, or do these visually cross to a
   left-hand grip?
2. ~~**`slash1` legacy clip**~~ — **RESOLVED (found done in repo):** `slash1.json` already
   carries `Type: GroundSlash2`, so the Group C `groundslash2` row is covered by
   re-authoring `slash1` (it currently FLAGS).
3. ~~**`pulse` / `guard` / `block*` as FullBody**~~ — **RESOLVED:** graded
   `OffRegionWeight` is now wired, so these become `Region=UpperBody` overlays with a
   partial lower-body brace (e.g. `pulse` OffRegionWeight≈0.3) instead of FullBody clips
   that kill the locomotion legs. Pick the brace weight per clip when authoring; I've
   left the Group C "region" column as UpperBody+brace for these.
4. **Charge-action clips (`beam`, `lobbedarea`, `block*`)** play over a *variable* charge
   time. Overlay τ remaps to `OverlayDuration`; confirm these expose a sensible duration,
   or whether the hold pose should just freeze at the charged frame (like the spatial-τ
   vault-hands pattern).
