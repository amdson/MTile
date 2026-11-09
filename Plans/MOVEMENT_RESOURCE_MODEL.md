# Movement resource model

Status: **proposal, nothing implemented.** Started 2026-11-09. Goals in one line each live in
[todo_movement.txt](../todo_movement.txt); this file holds the detail.

## 1. The problem

A movement state is written assuming a set of resources (legs to push with, a hover, air control).
The resources are solver channels, and each channel decides its own availability from geometry,
without knowing which state is running. Nothing checks that the two agree. Where they disagree,
the player's input stops mapping to motion.

Measured cases (headless traces, live `configs/movement_config.json`, lattice engine):

| Symptom | State's assumption | What the resource did |
|---|---|---|
| Walking off a ledge: vx 63 → 86 | Falling has no legs | Drive still passed its leg-reach test for 2 frames, bounded by Falling's 90 px/s limit |
| Running jump: vx 60 → 24 while holding forward | `ImpulseJump` means legs off | Only the leg servo is masked; Drive stayed live and pushed backward |
| S1 knockback: 3.2 px idle, 22 px with any direction held | Standing has hover, drive, braking | Tracker returns early in hitstun; the only brake left applies at zero input |
| Hitstun: body sinks 1 px, pops at 44 px/s after | Standing is supported | No leg servo during hitstun |
| Landing: vx 54 → 42, 15 frames to recover | Standing means travelling along the surface | Standing is claimed while the body is still descending at 130–180 px/s |
| Ground jump: hold beyond 4 frames does nothing | Jump lasts while the button is held | The legs are spent in about 3 frames |
| Jump out of hitstun is the running jump | vx ≥ 48 means a run | Knockback supplies the speed |

Read from the code, not measured:

- `MovementModifiers.WalkAccel` is written by nine actions and by hitstun, and read by nothing on
  the lattice engine.
- "On the ground" has at least five definitions (table in §3.1).
- ~~`ApplyAmbient` sets the policy Off in hitstun, but fold states still pass a profile.~~ Gone
  with `AmbientPolicy` (2026-11-09). The one switch left is `LatticeTracker`'s early return on
  `PreserveExternalVelocity`.
- `StunnedState` is grounded-only but uses air control and no hover.

## 2. The model

Three layers. Each reads only the one above it.

```
environment facts  →  resources  →  movement states
(sensed once)         (gated by facts)   (defined by resource access)
```

- **Environment facts** are what the body can physically touch this frame, plus whether something
  else controls it. Sensed once per frame.
- **Resources** are what the body can act with. Each is available iff its facts hold. Each solver
  channel belongs to exactly one resource.
- **Movement states** are chosen by resource access and by input. A state says which resources it
  uses and what it wants from them (direction, speed, hover or rise). It never says what exists.

## 3. Data structures

Names are proposals. All three are derived per frame from the body, terrain and combat state, so
none of them is snapshot state.

### 3.1 `ContactFacts` — the environment layer

One struct, built once per frame, cached on `EnvironmentContext` beside the existing checkers.

| Field | Meaning | Replaces |
|---|---|---|
| `FloorFound`, `FloorY`, `FloorDist`, `FloorVelocity` | Nearest standable surface under the body, C-space | `FloorEnvelope` calls in `LatticeTracker`, `JumpingState.LegsInReach`, `DropdownState`, `AmbientCorrector` |
| `LegContact` | Floor within leg reach | `near` in `LatticeTracker` and `BuildFold`; the standing ground probe as a state gate |
| `Supported` | Floor within support reach and not rising faster than support could push | `FoldBaseline`'s `supported`, `anchored`, `IsStandingGround` |
| `Ballistic` | Rising faster than support could push, surface-relative | Three copies of the `SpringMaxRiseSpeed` test |
| `HandCorner` (+ point) | Plantable convex corner within hand reach | `MarkCornerPlants` at tick 0 |
| `WallLeft`, `WallRight` | Exposed wall face within hand reach | `MarkContactPush` at tick 0, `WallChecker` |
| `GripCorner` (+ point) | Held or reachable ledge corner | `abilities.GrabbedCorner` + `InGripReach` |
| `Ceiling` (+ gap) | Headroom | `CeilingChecker` |
| `External` | None / Hitstun / Stun / Launched / Grabbed | `Combat` flags read ad hoc by states |

The reach numbers that exist today, to be reduced to one per fact:

| Constant | Value | Used for |
|---|---|---|
| `GroundChecker.StandingProbeSlack` | 5 + 4.8 | Standing entry and exit |
| `CorrectorChannels.LegReach` | 21.8 | Leg channels |
| `BallisticPredictor.SupportReach` | 29.8 | Gravity hold, anchor |
| `BallisticPredictor.HoldFullDist` | 28.8 | Gravity hold at full strength |
| `JumpSourceProbeSlack` | 60 | Jump source surface |
| `JumpSupportChecker` | own scan | Jump push-off |

The standing probe and `LegReach` are meant to be the same condition and are, vertically. They
differ sideways at an edge (strip probe versus C-space extent), which is the ledge boost.

### 3.2 `MovementResource` — the resource layer

A flags enum plus an authority scalar per resource.

| Resource | Available when | Channels it owns |
|---|---|---|
| `Legs` | `LegContact` and not `Ballistic` | LegServo, Drive, Tuck, floor Redirect |
| `Hands` | `HandCorner`, a wall face, or `GripCorner` | corner Redirect, ContactPush L/R, Grip Lift/Pull/Hold/Brace |
| `Air` | not `Legs` | the state's air control |
| `Support` | `Supported` | the gravity hold |

```
struct ResourceSet {
    MovementResource Available;
    float LegAuthority, HandAuthority, AirAuthority;   // 1 = full; External scales these
}
```

One table maps `CorrectionChannel` → `MovementResource`. A channel's mask is
`Available & state.Uses` for its resource and nothing else. `BuildFold`'s per-tick `near[]` and
`LatticePolicy.BuildChannels`' per-channel rewrites go away as each is covered.

### 3.3 State declarations

Each `MovementState` gains two properties:

- `Requires` — resources that must be available to enter and to continue.
- `Uses` — resources it asks the solver for. `Uses ⊆ Requires ∪ optional`.

| State | Requires | Uses |
|---|---|---|
| Standing, Crouched | Legs | Legs, Support |
| Falling | none (the fallback when Legs is absent) | Air, Hands |
| Jump (planned rise) | Legs | Legs, Hands |
| Jump (impulse), DoubleJump | none | Air, Hands |
| WallSlide | Hands (wall) | Hands |
| LedgeGrab / Pull | Hands (grip) | Hands |

`FoldProfile` keeps the intent half (hover offset, speed, rise cost, rising) and loses the
availability half (`Legs`, and `Hover` where it stands in for "has legs").

Relationship to `MovementCapability` (Jump / WallCling / LedgeGrab, blocked by combat): that enum
gates verbs, this one gates physical means. They stay separate for now; whether combat blocks a
verb or scales a resource is §6 question 3.

## 4. Stages

Each stage builds and is checked against the trace baseline before the next starts. Existing
movement and corrector tests are treated as suspect throughout: many pin behaviour an earlier
session assumed rather than something decided. A red test is reported, not obeyed.

0. **Baseline.** Keep the two scratch traces (`MTile.Tests/Sim/ZzzMovementResponse*.cs`) as the
   regression check. Record the current numbers (§5).
1. **`ContactFacts`, read-only.** Build the struct and route existing derivations through it
   without changing any threshold. Expected: no behaviour change. Output: the list of places
   where two definitions of one fact disagree.
2. **Collapse definitions.** One reach per fact. This changes behaviour wherever stage 1 found a
   disagreement; each change is listed and traced.
3. **Resource table.** Channel → resource mapping; masks come from `ResourceSet`. Delete the
   per-channel overrides one at a time, each checked against the bug its comment cites.
4. **State declarations.** `Requires` / `Uses`. Falling becomes "no Legs" by definition; the
   Standing boundary moves to the leg-contact fact.
5. **External control.** Hitstun, stun and grab set authority scalars. Remove the
   `PreserveExternalVelocity` early return and the dead `WalkAccel` modifier path.
6. **Along-path law.** One speed per intent across states; braking and driving become one law.

Stages 5 and 6 depend on answers in §6.

## 5. Baseline numbers

To be re-measured after every stage. Flat-ground values are the ones that must not regress.

| Measure | Now | Target |
|---|---|---|
| Walk: first-frame vx / frames to full / stop frames | 49 / 3 / 2 | unchanged |
| Tap N frames → px | N ± 0.05 | unchanged |
| Step-up: vx range | 60–63 | unchanged |
| Ledge walk-off: peak vx | 86 | ≤ walk speed |
| Running jump: min vx in first 8 frames, direction held | 24 | ≥ takeoff vx |
| Landing with direction held: min vx / frames to recover | 42 / 15 | separate track |
| S1 knockback: idle / hold away / hold toward (px) | 3.2 / 22.9 / 22.0 | set by §6 q1 |
| Hitstun end: vertical pop (px/s) | 44 | 0 |
| Standing rest height spread (px) | 3.6 | separate track |

## 6. Open questions

1. **What should a held direction do during hitstun?** todo_movement says hitstun should mostly
   not affect movement. Read literally, legs stay available and knockback is only a velocity the
   normal law then acts on: idle brakes it, holding toward brakes it harder, holding away keeps
   it up to walk speed. Is that the intent, or should control be muted by an authority scalar?
2. **Where is the Standing boundary?** If Falling means "no Legs", then at a ledge the body
   stays Standing until its trailing edge clears the lip. That is later than today by about 2 px.
3. **Does combat block verbs or scale resources?** Today it blocks verbs through
   `MovementCapability`. Stage 5 adds resource scaling. Both can coexist; one may be redundant.
4. ~~Which fold engines stay?~~ **Done 2026-11-09:** `qp`, `ref` and `lm` are removed, with
   `AmbientPolicy`, the `FoldEngine` knob and every `OnLattice` branch. Both traces were
   byte-identical before and after.

## 7. Not covered here

These were measured in the same pass but are not state/resource disagreements:

- **Path's first segment.** The polyline starts at the body, so the leg from the body to the
  first plan node is counted as path direction and arc length. Suspected cause of the
  running-jump brake direction and the landing speed loss. Not yet confirmed by experiment.
- **Jump mechanisms.** Planned rise, impulse + hold, and the running-jump impulse have different
  hold curves (23.5 → 33 px over 4 frames; 13 → 44 px over 8; 39 px).
- **Hover dead band.** Rest height is anywhere in a 3.6 px band.
- **Early wall braking.** Deceleration starts 5 frames and 2.8 px before contact.
- **Redirect's contribution.** todo_movement asks for a limit near walk speed; on the lattice
  engine its forward output is already capped at the state's speed, but the cap is per state.
