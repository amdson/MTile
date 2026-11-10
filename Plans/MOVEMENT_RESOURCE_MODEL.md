# Movement resource model

Status: **proposal.** Implemented: the `qp`/`ref`/`lm` engine removal (commit `e71afd1`) and the
path-frame change (§8; in the working tree, kept by the user, not yet committed). Started 2026-11-09. Goals in one line each live in
[todo_movement.txt](../todo_movement.txt); this file holds the detail.

## 0. Decisions so far

Settled in discussion on 2026-11-09. Later sections follow these.

- **The category of problem is inconsistent abstraction:** what a movement state assumes it has
  is not what the channels actually make available. Fixing that should remove code, not add it.
- **The structure is environment-gated resources (the channels), then movement states that
  depend on the environment but mostly on access to those resources.**
- **The environment tests are called `ChannelProbes`:** a bundle of tests on the environment,
  each answering "can this channel act here".
- **Probes may be mixed into `EnvironmentContext`.** No separate struct or purity rule is
  required. The requirement is that a channel's mask and a state's gate read the same probe.
- **States are gated on channel access systematically, where that is the correct gate.** States
  defined by combat state, an ability charge, an input shape or a whole-maneuver feasibility
  check keep their own gate (§3.3).
- **Motion is split along the path versus perpendicular to it, never x versus y** (§2.1).
- **There is one fold engine, the lattice.**
- **The body is not a vertex of the path.** The path is the plan's nodes; the body's offset from
  it goes to the tracker as error (§8, implemented).
- **Channels are never conditioned on the path.** A channel says what the body can do in this
  environment. The path is a goal and enters only through rows. Conditioning a channel's axis,
  mask or cap on the path gets causality backwards.
- **No tricking the tracker.** A capability the body has is never hidden from the solver to get
  a behaviour. If the solver then does the wrong thing, the rows or weights are wrong.
- **The feet are four axis-aligned pushes: up, down, forward, back.** Drive becomes two-way in
  x; the zero-input friction term becomes its braking side. It is not opt-in (§9).
- **Hitstun is meant to affect actions and do very little to movement.** The solver stays on
  while hit and nothing is scaled; the feet grip or slip by the body's speed alone (§9).
- **Drive has one cap**, the same in both directions.
- **S1 is supposed to do basically nothing as knockback.** Hits get scaled up when the knockback
  retune happens; that is not part of building §9.
- **Stun keeps modifying movement.** It is the one combat state that still does. `StunnedState`
  is the ragdoll state: the solver stays off, on purpose. In the model it is a state that uses
  no resources (`Uses` = none), not a scaled-down Standing; its muted air control is its own.
- **The impulse jump and the running jump are gated on having legs.** They need something more
  after launch to allow controllable jump heights (§10).
- **A ground jump needs legs, so its "no floor in leg reach" impulse variant goes away.** The
  ground jump is then always the planned rise. What happens to the launches that reach that
  variant today is open (§10 question 3).
- **A jump is one solver channel, gated at creation only** (§11). Pressing jump with the feet
  planted creates it; after that it is available whatever is underneath, while the button is
  held, up to a hold time. It replaces the leg servo during the jump.
- **The running jump requires a higher horizontal speed, in the direction being pressed.** Today
  it is |vx| ≥ 48 with no direction test, so the first frame of a walk (48.6 px/s) and knockback
  both select it.
- **A wall row is kept only if the route itself satisfies it** (§12). The planner decides which
  tiles are avoided; the tracker's wall rows enforce that set and nothing else. A wall or ceiling
  the plan runs into is hit, not braked for.
- **Action modifiers scale channel strength** (§13). The walk-acceleration modifier scales
  Drive's cap, and only Drive's: not the leg servo, so no sag and no weaker jump. Braking and
  grip weaken with it; revisit if it feels bad.
- **Redirect is left as it is for now**, path-conditioned parts included. Requirement for when it
  is reworked: it must not accelerate the player to high horizontal speed, for instance when
  falling onto a block corner. That may need a friction force on the surface it plants against.
- **Tests are ignored until after the refactor.** Reds are not triaged or repaired during it.
- **Parkour no longer triggering on small steps is accepted;** parkour may be removed altogether.
- **Existing movement and corrector tests are suspect.** Many codify behaviour an earlier session
  assumed. A red test is reported, not obeyed, and tests for removed code are deleted.

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
channel probes  →  resources  →  movement states
(tests on the       (gated by probes)   (defined by resource access)
 environment)
```

- **Channel probes** are a bundle of tests on the environment: is there something here for a
  channel to push on. Each is run once per frame and answers yes or no, with the geometry it found.
- **Resources** are what the body can act with. Each is available iff its probes pass. Each solver
  channel belongs to exactly one resource.
- **Movement states** are chosen by resource access and by input. A state says which resources it
  uses and what it wants from them (direction, speed, hover or rise). It never says what exists.

### 2.1 Who owns which direction

The split is along the path versus perpendicular to it. On flat ground that is x and y; on a
bevel or a stair flight it is not, which is why an x/y split would be a special case.

- **Along the path, input is the only authority.** One commanded speed (input × the state's
  speed) and one law that accelerates toward it. Zero input commands zero, so braking is the same
  law, not a separate friction term. Being hit does not switch the law off (§9).
- **Perpendicular to the path, the solver is the only authority.** Hover, clearance, the landing
  catch and the band live here, and none of them may do work along the tangent.

Most measured symptoms are violations of this: the running-jump brake and the landing speed loss
(a perpendicular correction applied along the tangent), the ledge boost (a different along-path
speed per state), the knockback slide (along-path braking that exists only at zero input), and
early wall braking (clearance met by shedding tangential speed).

## 3. Data structures

Names are proposals. All three are derived per frame from the body, terrain and combat state, so
none of them is snapshot state.

### 3.1 `ChannelProbes` — the environment layer

Probes live on `EnvironmentContext`, alongside the checkers it already caches (`TryGetGround`,
`TryGetWall`, `TryGetCeiling`, …). They are not a separate layer and need not be pure: a probe is
just a named, cached test that answers "can this channel act here", and it may sit next to or be
built from an existing checker. What matters is that a channel's mask and a state's gate read the
SAME probe, so they cannot disagree.

| Probe | Passes when | Payload | Replaces |
|---|---|---|---|
| `Floor` | A standable surface lies under the body (C-space) | y, distance, surface velocity | `FloorEnvelope` calls in `LatticeTracker`, `JumpingState.LegsInReach`, `DropdownState` |
| `Legs` | `Floor` within leg reach, and the body is not rising faster than that surface could push it | — | `near` and `ballistic` in `LatticeTracker` and `BuildFold`; the standing ground probe as a state gate; three copies of the `SpringMaxRiseSpeed` test |
| `Support` | `Floor` within support reach, same rise test | hold strength 0–1 | `FoldBaseline`'s `supported`, `anchored`, `IsStandingGround` |
| `HandCorner` | A plantable convex corner within hand reach | corner point | `MarkCornerPlants` at tick 0 |
| `WallLeft`, `WallRight` | An exposed wall face within hand reach | face x | `MarkContactPush` at tick 0, `WallChecker` |
| `Grip` | A ledge corner within arm's reach | corner point | `abilities.GrabbedCorner` + `InGripReach` |
| `Ceiling` | Solid overhead within standing headroom | gap | `CeilingChecker` |

External control (hitstun, stun, launched, grabbed) is not a probe. It enters at the resource
layer as an authority scale (§3.2). `Ceiling` gates no channel; it is listed because states and
the planner read it and it already lives on the context.

The reach numbers that exist today, to be reduced to one per probe:

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
| `Legs` | the `Legs` probe | LegServo, Drive, Tuck, floor Redirect |
| `Hands` | `HandCorner`, `WallLeft`/`WallRight`, or `Grip` | corner Redirect, ContactPush L/R, Grip Lift/Pull/Hold/Brace |
| `Air` | not `Legs` | the state's air control |
| `Support` | the `Support` probe | the gravity hold |

```
struct ResourceSet {
    MovementResource Available;
    float LegAuthority, HandAuthority, AirAuthority;   // 1 = full
}
```

`Available` comes from `ChannelProbes` alone. The authority scalars are where external control
enters: action modifiers scale them (§13), and never change which probes pass. Stun does not
use them: a stunned body is a ragdoll that uses no resources (§0). Hitstun does not (§9). Launch and grab are undecided (§10).

One table maps `CorrectionChannel` → `MovementResource`. A channel's mask is
`Available & state.Uses` for its resource and nothing else. `BuildFold`'s per-tick `near[]` and
`LatticePolicy.BuildChannels`' per-channel rewrites go away as each is covered.

### 3.3 State declarations

Gating states on channel access is the point of this refactor, applied where it is the correct
gate and not everywhere. Each `MovementState` gains two properties:

- `Requires` — resources that must be available to enter and to continue. Empty where channel
  access is not what defines the state.
- `Uses` — resources it asks the solver for. `Uses ⊆ Requires ∪ optional`.

Where channel access is the right gate (the state IS "acting with this resource"):

| State | Requires | Uses |
|---|---|---|
| Standing, Crouched | Legs | Legs, Support |
| Falling | none (the fallback when Legs is absent) | Air, Hands |
| Jump (planned rise) | Legs | Legs, Hands |
| WallSlide | Hands (wall) | Hands |
| LedgeGrab / Pull | Hands (grip) | Hands |

Where it is not, and the existing gate stays:

| State | Gated by |
|---|---|
| Stunned, Tumble, Grabbed | combat state |
| Dash, DoubleJump | an ability charge and a button |
| Dropdown, CoveredJump | input plus a specific edge shape |
| Climb family, StairClimb | a feasibility probe over the whole maneuver |

These still declare `Uses`, so their channels come from the same probes as everyone else's.

The impulse jump and the running jump are gated on `Legs` too (decided 2026-11-09). Both launch
by a velocity write, so what they need after launch to give controllable jump heights is open
(§10).

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
1. **`ChannelProbes`, read-only.** Add the probes to `EnvironmentContext` and route existing derivations through them
   without changing any threshold. Expected: no behaviour change. Output: the list of places
   where two definitions of one probe disagree.
2. **Collapse definitions.** One reach per probe. This changes behaviour wherever stage 1 found a
   disagreement; each change is listed and traced.
3. **Resource table.** Channel → resource mapping; masks come from `ResourceSet`. Delete the
   per-channel overrides one at a time, each checked against the bug its comment cites.
4. **State declarations.** `Requires` / `Uses`. Falling becomes "no Legs" by definition; the
   Standing boundary moves to the `Legs` probe.
5. **External control.** Stun is a state that uses no resources (ragdoll, solver off). Hitstun needs nothing on the ground: grip
   and slip (§9) replaces it. Remove the `PreserveExternalVelocity` early return and the dead
   `WalkAccel` modifier path.
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

Moved to §10, with what has been decided since.

## 7. Not covered here

These were measured in the same pass but are not state/resource disagreements:

- ~~**Path's first segment.**~~ Confirmed for the running-jump brake and fixed (§8). Not the
  cause of the landing speed loss.
- **Jump mechanisms.** Planned rise, impulse + hold, and the running-jump impulse have different
  hold curves (23.5 → 33 px over 4 frames; 13 → 44 px over 8; 39 px).
- **Hover dead band.** Rest height is anywhere in a 3.6 px band.
- **Early wall braking.** Deceleration starts 5 frames and 2.8 px before contact. Cause and
  agreed fix in §12.
- **Redirect's contribution.** todo_movement asks for a limit near walk speed; on the lattice
  engine its forward output is already capped at the state's speed, but the cap is per state.
  Changed 2026-11-09 (uncommitted): the cap is min(walk speed, the state's speed) in every
  state, so Falling's 90 no longer applies. No trace changed: none of the scenarios, including
  a new fall onto a block corner, has Redirect adding forward speed above walk. The case the
  user sees in play has not been reproduced headless.

## 8. Path frame (implemented 2026-11-09, uncommitted)

The tracker's polyline was "body, then the first plan node at least 0.9 of a cell away, then the
rest". The body-to-node connector was read as route: Drive and Redirect took their direction
from it, and its length counted against the speed cap. During a running jump the plan is a
vertical column at the grid-snapped seed x; one frame later the body is past it, the connector
points backward, and Drive braked at its cap.

Change (`LatticeTracker.cs`, `PathTracker.cs`): the polyline is the plan's nodes only, with the
first segment extended backward as a ray. The body is projected onto it. Drive/Redirect direction
is the route's tangent at that projection; the speed cap and the progress chord are measured from
the projection.

| Measure | Before | After |
|---|---|---|
| Running jump: min vx in first 8 frames | 24 | 53.9 |
| Running jump: distance downrange (px) | 43.4 | 58.5 |
| Flat walk speed | 59.4–62.9 | 59.6 steady |
| Flat walk height while walking | 44.2–45.55 | 45.50 |
| 10-frame tap from five sub-tile starts (px) | 9.95–10.02 | 9.93, all five |
| Landing, direction held: min vx / frames to recover | 42 / 14 | 43 / 8 |
| Ledge walk-off peak vx | 86 | 86 |

What it exposed or cost:

- **The body rides low.** After a landing or a hit it settles up to 4.2 px below the hover row
  and stays (before: up to 3.6 px high). Cause, traced: the planner seeds at the body's cell every
  frame. One cell below the hover row the plan is a 3-across-1-up edge; over the tracker's 5 ticks
  that rises 1.67 px, inside the 1.83 px band, so nothing asks the body to rise, and the next
  frame re-seeds. Inside the hover row's own cell (y 44.0–47.67) the error is always under the band.
- **Drive has no look-ahead.** A neutral jump from under a slab edge no longer escapes sideways.
  Expected to go away with §9 (Drive stops taking a direction from the path at all).
- **Parkour stopped triggering** on a one-high step, bump and one-wide pit when enabled (code
  defaults; the live config has it off). Not investigated.
- **Tests:** newly red `BallisticPredictorTests.Landing_FallOntoFlat_HoldRight_SettlesToHover`,
  `LatticeScenarioTests.Row03_CoveredJump_NearEdge_RisesOutDiagonally`, three
  `BotManeuverRouteJumpTableTests.Entry_StillLands` rows. Newly green: four (mantle, avalanche
  charge, corridor hole drop, unhangable step).

Parked idea (hover track, not agreed): for hover profiles, seed the plan at the hover row in the
body's column instead of at the body, so the route is fixed in the world and the body's height
error reaches the band rows. It takes the climb-back trade out of `LatticeHoverWeight` in open
ground and may pop at corridor exits.

## 9. Two-way Drive, grip and slip (planned; agreed 2026-11-09)

Today, measured: during hitstun the tracker returns early (`PreserveExternalVelocity`), and the
only horizontal force on a grounded victim is `FoldBaseline`'s station friction, which applies
only at zero input (675 px/s² at hitstun's 30% scale). S1 moves an idle victim 3.2 px and a
victim holding either direction about 22 px. The intended DI (15% of `WalkAccel`) is read by
nothing. When hitstun ends the solver returns at full strength in one frame.

### 9.1 The rule

Hitstun does nothing to ground movement. What a hit changes is the body's velocity, and the feet
respond to that by one law that never reads combat state:

- **Gripping:** body speed relative to the floor is at or below walk speed. Drive has its full
  cap. Walking, stopping and taps are as now.
- **Slipping:** body speed relative to the floor is above walk speed. Two things decelerate it:
  - **Sliding friction:** passive, weak, always on while slipping, independent of input. It is
    what the floor does to the feet, so it is part of the physics the solver predicts from, not
    a channel it chooses.
  - **Drive:** active, with a weak cap while slipping. The solver adds it when the commanded
    velocity calls for slowing down.

| Victim input | Decelerated by | Slide |
|---|---|---|
| Toward the attacker | sliding friction + Drive | shortest |
| Idle (zero input commands zero) | sliding friction + Drive | shortest |
| Away from the attacker | sliding friction only | longest |

The victim's influence is the ratio of the two weak numbers. Idle and toward are equal on
purpose; separating them would need a force proportional to the velocity error, which is not
planned.

**The walk-speed limit changes meaning.** It becomes "the legs cannot ADD speed beyond walk
speed", a statement about the legs, not a demand that the solver brake anything faster. Without
this a victim holding away would be braked by Drive too and all three rows would be equal. Air
control has worked this way since 2026-11-05.

Consequences accepted by the user:

- A hit must exceed walk speed to displace. S1's 60 px/s is exactly walk speed and would stop in
  about a frame; knockback speeds or the weak numbers need retuning. Slide length is roughly
  (speed² − walk speed²) / (2 × deceleration).
- There is a catch at the threshold: gentle decay to walk speed, then a stop within a frame or two.
- Unintended over-speed (the 86 px/s ledge boost) glides instead of being braked hard, until it
  is fixed at its source.
- Ground only. In the air, hitstun still scales air drag and air acceleration.

Removed by this: the hitstun early return, `HitstunFrictionScale` and the dead `WalkAccel` DI
on the ground, `PreserveExternalVelocity` for grounded states, and the guard-pushback slide
window (`GuardPushSlideSeconds`), which exists only because friction killed the shove next frame.
The hitstun sink and pop go away because the legs stay on.

### 9.2 Steps

1. **Drive pushes either way in x, with one cap.** Not conditioned on the path: no axis from the
   route, no mask on `planning` or on the route's tangent. Available whenever the legs are.
   (Today accelerating is 3000 px/s² and friction 2250; with one cap, stopping gets slightly
   quicker or starting slightly slower, depending on which number is kept.)
2. **A speed row at zero input** (commanded speed zero, relative to the floor's own velocity),
   so braking is asked for by a row like everything else.
3. **Delete the friction term** in `FoldBaseline`.
4. **Grip and slip** (§9.1): the weak Drive cap and the passive sliding friction above walk
   speed; the walk-speed limit as a limit on what the legs add; delete the hitstun early return
   and the ground-side hitstun scalars.

Steps 1–3 are checked against the flat-ground traces (walk, stop, taps unchanged). Step 4 is
checked against the hit traces.

Expected side effects, to be read as information about the rows and weights, not patched with
masks: the solver may brake to meet band or clearance rows (early wall braking, stalling at a
lip). Redirect's `ForwardAxis`/`ForwardCap` are also taken from the path today and need the same
treatment.

### 9.3 Not part of building this

- **Knockback retune.** S1 is meant to do basically nothing. Other hits get scaled up, and the
  two weak numbers chosen, when knockback is tuned.
- **Air knockback.** Hitstun still scales air drag and air acceleration. Handled when we get
  to it.
- **The solver braking for the wrong rows** (stalling at a lip, earlier wall braking). To be
  worked out when it shows up.
- **Redirect** stays as it is (§0).

## 10. Open questions

Reviewed with the user 2026-11-09. Settled items are in §0.

**Blocking §9**

1. **The walk-speed limit on slopes.** Today it limits speed along the path, which is what stops
   a 45° bevel being run at 141 px/s. As "the most the legs can add" it has to be stated without
   the path. The user's suggestion: base it on velocity measured against voxel corners. Read as:
   measure the body's speed relative to the terrain the feet are on (the corners and surfaces
   under it), so the limit comes from the environment. Not yet worked out, and the reading itself
   needs confirming: which corners, and whether speed is taken along the line between them.

**Blocking the ledge fix**

2. ~~**Where the Standing boundary is.**~~ Decided 2026-11-09 (left to Claude): the leg-reach
   test, the one the channels use, so the state and its channels cannot disagree. Standing ends
   about 2 px later at a ledge. Expected to remove the ledge boost, which the trace shows is
   Drive (1122 px/s² on the first Falling frame), not Redirect.

   The other reach numbers, decided the same day (left to Claude):
   - **Support keeps its own reach.** The gap beyond leg reach is deliberate: the gravity hold
     fades from full to zero across it, so a body floating above hover gets mostly-real gravity.
     Its three users (`FoldBaseline`'s `supported`, the tracker's `anchored`,
     `IsStandingGround`) read the one `Support` probe.
   - **The 60 px jump-source scan and `JumpSupportChecker` are deleted.** Under §11 a jump is
     created by the `Legs` probe, whose `Floor` payload already carries the surface velocity
     the scan was finding.
   - **The three launches that used the no-legs variant:** a stair tread the ground probe lost
     jumps if `Legs` passes (the unified probe is the fix); "floor just out of reach or a lip
     beside the body" falls through to the double jump; the corner push-off out of a hang or
     climb becomes a hands-created jump (§11.3's open item, taken as yes for this one case).

**To discuss**

3. **What a jump is.** Two mechanisms, three tunings:

   | Jump | Chosen when | Launch | Rise, tap → full hold | Hold matters for |
   |---|---|---|---|---|
   | Ground, planned rise | floor in leg reach, speed under 48 | the solver's legs along a rising plan | 23.5 → 33.1 px | about 4 frames |
   | Running | speed ≥ 48 | velocity write + hold force | 39.1 px at full hold (short holds not measured) | up to 7 frames |
   | Impulse + hold (double jump; ground jump with no floor in leg reach) | no legs | velocity write + hold force | 12.8 → 44.2 px (double jump) | 8 frames |

   Decided: the third row's ground-jump case goes away, and the running jump's entry tightens
   (§0). Still open:
   - ~~Height control on the planned rise.~~ Decided: the jump channel (§11).
   - **Launches that use the no-legs variant on purpose today:** a corner push-off out of a hang
     or a climb at the lip (a hands launch), a stair tread the ground probe has lost for a few
     frames, and `JumpSupportChecker`'s "floor just out of reach or a lip beside the body". With
     the variant gone, each either becomes legs through the unified probe, becomes a
     hands-gated launch, or falls through to the double jump.
   - **The running jump's threshold.** Walk speed by frame is 48.6, 54.0, 56.7, 58.2, 58.9, and
     settles at 59.6; it dips to about 54 on a step. The number has to sit under 59.6.
   - ~~**Different maximum heights** (33, 39, 44 px) for what reads as one action.~~ Any
     reasonable initial values; not a concern (2026-11-09).
   - **Jumping out of knockback while holding away** still passes the running jump's
     pressed-direction check. Parked: revisit only if it comes up after the other refactors.
4. **Drive and Redirect cancel on flat ground** (about +48 against −47 every frame). To be
   investigated, then discussed.
5. **The body rides up to 4 px low** after a landing or a hit (§8). Not discussed in the review.
   The seed-at-hover idea is parked.
6. **Launch and grab.** Stun keeps modifying movement; the user did not say whether launch and
   grab do.
7. **Does combat block verbs or scale resources?** Today it blocks verbs through
   `MovementCapability`; stun will scale resources. Both can coexist; one may be redundant.

**Investigated**

8. **Landing speed dip: cause established 2026-11-09.** The whole loss is in the first three
   frames after Standing claims the body (vx 57.8 → 48.3 → 44.4 → 42.9), and the channel doing
   it is Redirect (−570, −230, −100 px/s² in x). The plan's first leg runs diagonally from the
   body down to the hover row, and the walk-speed limit is measured along the route, so the
   descent (120 px/s) counts as travel: about 117 px/s along a leg capped at 60. The solver
   sheds it with the leg servo (−2648, the catch) and with Redirect, which takes x as well.
   Experiment: with the limit applied to x only, vx stays 58.3–59.6 through the landing and the
   catch is gentler (leg servo −548, no rebound). The experiment was reverted. Two planned
   changes each remove the cause: the walk-speed limit becoming "the most the legs can add,
   never a demand to brake" (§9.1), and question 1 above, which decides what the limit is
   measured against.

## 11. The jump channel (planned; agreed 2026-11-09)

Why: the planned rise is the jump that fits the model (the legs launch it), but it has almost no
height control. The leg servo is strong (6000 px/s²), reaches launch speed in about two frames,
and the body leaves leg reach after about 7 px of rise, so holding the button does nothing after
3–4 frames and the shortest tap already gives 71% of full height. The impulse jumps control well
but write velocity outside the solver and need a "legs off" profile so it does not launch them
again.

### 11.1 The rule

- **Created** when jump is pressed with the feet planted (the `Legs` probe). That is the only
  environment check.
- **Then not gated on the environment.** It is available over an edge, beside a step or in open
  air, so the same press gives the same jump everywhere.
- **Ends** when the button is released or the hold time runs out, whichever is first. The hold
  time is the one that already exists for the impulse jumps (`MaxJumpHoldTime`, 7 frames).
- **It is a solver channel:** an upward push with a cap, visible to the solver. Nothing writes
  velocity, and the "legs off" profile is not needed.
- **It replaces the leg servo for the duration of the jump.**

Shape of the push, so that hold length maps to height:

- The push fades as upward speed approaches a top speed. The first frame is a solid kick, so a
  tap is a real short hop.
- While held, it sustains that top speed against gravity. Each extra held frame adds about the
  same height.
- On release or timeout the body coasts.

This is the one exception to "resources are gated by probes and nothing else": the channel is
gated at creation and carries its remaining hold time afterwards. That state lives in
`MovementVars` with the existing hold timer, so it survives a rollback.

### 11.2 What it replaces

- The planned rise's use of the leg servo.
- The ground jump's impulse variant (`JumpVelocity`, `JumpHoldForce`, `JumpInitForce`).
- The running jump's velocity write and hold force (`RunJumpVelocity`, `RunJumpHoldForce`). The
  running jump becomes this jump entered with horizontal speed.

### 11.3 Open

- **Whether the other jumps become the same channel with a different creation condition:**

  | Jump | Created by |
  |---|---|
  | Ground and running | feet planted |
  | Double | an air-jump charge |
  | Wall | hands on a wall |
  | Ledge / corner push-off | hands on a corner |

  Not committed. It is the reason to build creation separately from the push. It would also
  settle the corner push-off (a hands-created jump).
- **Whether the running jump keeps a different height** (39 px against 33 today), and if so by
  what, now that it is the same mechanism.
- **The numbers:** the cap, the top speed, and whether the hold time stays at 7 frames.
- **The solver using the push for other rows** during the hold (it is an upward push available
  in the air). To be watched, not masked.

## 12. Wall rows follow the plan (planned; agreed 2026-11-09)

### 12.1 The fault

Walking into a wall, the body starts slowing 5 frames and 2.8 px before contact. The same is
expected under a low ceiling during a jump's launch frames (not measured).

The planner does what the bonk concept says: a wall not worth climbing gives a refused plan whose
last segment is extended as a ray into the wall ("never a planned stop"). The tracker then undoes
it. `LatticeTracker` builds a clearance row for every tile face the 5-tick free rollout enters,
with `verticalFacesOnly: false`, on the reasoning that "the DP already decided bonk-vs-route".
For a refused plan that is backwards: the planner decided to hit the wall and the rows say not
to. The rows are at full weight; the only row that pulls forward (progress) is at 0.02 of it. The
5-frame lead is the tracker's horizon. Read from the code; not confirmed by switching the rows
off.

The wall rows are the only rows that do not come from the plan. They exist because the band lets
the body stray 1.83 px from a route the planner keeps only 0.25 px clear of tiles (corner hits in
the bumpy corridor).

### 12.2 The rule

A wall row is kept only if the route itself satisfies it.

- A corner the route goes around: the route is clear of that tile, the row stands.
- A wall the plan runs into: the route's extension enters that tile, the row contradicts the plan
  and is dropped. Physics stops the body.
- A ceiling above a jump: the same case.

Build: each wall row already carries its source tile and its tick, and the tracker already has
the route point (bead) the body is following at that tick. Drop the row when that point violates
it.

### 12.3 Considered and not chosen

- Dropping rows by direction (faces opposing the motion, or terrain acting sideways only): a
  special case.
- Deleting the wall rows and replacing the free band with a cost that grows from zero, on a
  smoothed route. It would also remove the hover dead band, but a band of any free width beyond
  the planner's 0.25 px clearance reopens the gap, and smoothing cuts bends toward the inside
  tile. Larger change; kept in reserve.

### 12.4 To check when built

- Wall approach and low-ceiling jump: no early braking.
- Bumpy corridor: corners still clean.
- A refused plan that ends short with its last segment along the wall rather than into it: the
  row would stay and the body would brake as today.
- The hover dead band and the body riding low are not touched by this (§10 question 5).

## 13. Action modifiers scale channel strength (planned; agreed 2026-11-09)

`MovementModifiers.WalkAccel` is written at nine sites in `Character/Action/ActionStates.cs`
(lines 526, 1665, 2374, 2626, 2826, 3395, 3705, 4033, 4271; values 0.5–0.8) and by hitstun, and
read by nothing on the lattice engine. Five of the nine also scale `MaxWalkSpeed`, which works.
The other four (526, 1665, 2626, 2826) currently have no effect on walking at all.

Decided: modifiers feed the resource layer's authority scalars (§3.2), the same mechanism stun
uses, so the solver sees the true cap.

| Modifier | Becomes |
|---|---|
| `MaxWalkSpeed` | unchanged: it scales what the state asks for, not what the body can do |
| `WalkAccel` | scales Drive's cap (both directions; §9's one cap). Not the leg servo |
| `AirAccel` | scales air control's cap |
| `GroundFriction` | goes away: §9 folds friction into Drive |

Consequences, accepted:

- Braking and grip weaken with Drive. A body at ×0.5 stops half as hard and slides further when
  hit, so knockback distance depends on what the victim was doing. To be revisited if the feel
  is bad.
- The four acceleration-only actions start having an effect for the first time.

Hitstun's own write of this modifier is removed with the rest of the hitstun scalars (§9).

## 14. Build log

Implementation of the refactor on branch `movement-resource-refactor` (from `combat-frame-data`
210fe11), one commit per item. Traces: `ZzzMovementResponse` (r1, code-default config, parkour on)
and `ZzzMovementResponse2` (r2, live config). "Trace" below means measured in those; "code" means
read from the code only.

### Item 1 — wall rows follow the plan (§12)

Change: `PathTracker.Solve` keeps a copy of the terrain rows and, on every bead pass, keeps a row
only if the route point at the row's tick satisfies it against the tile itself (the row's depth
less the `LatticePathPlanner.Clearance` margin it was inflated by — the same margin the planner
keeps its nodes clear by). With no polyline (a refused plan with nothing past its seed, or no
route) the route is the body's ray along the fallback tangent (intent). The test uses the row's own
plane, which is the source tile's facet at that tick; the tile index itself is not needed. Applies
to every `PathTracker` user (lattice and the authored clip followers).

Decision not in the plan: the no-polyline case. The first build used beads only, and the wall
approach still braked for 2 frames, because at the wall the plan collapses to its seed (count 1,
Refused) and the tracker has no polyline. Taking the route there as the intent ray is what the
tracker's own comment says the regime means ("drive along intent … walk into it").

| Measure (trace) | Before | After |
|---|---|---|
| Wall approach (r2 #7): first braking frame / vx at contact | f101, 5 frames before contact / 31.8 | none / 59.6 (stops on contact) |
| Low-ceiling neutral jump (r2 #8): rise speed at the bonk | tuck brakes from f42, 73.8 px/s at contact | no tuck, 172.8 px/s at contact |
| Flat walk, taps, jumps, running jump, landings, hits | — | identical |
| Bumpy corridor (`BumpyTunnelSpeedTests`, trace only) | 115.0 px/s, final x 600.4 | same (sub-0.1 px differences) |

Got worse / changed:
- With parkour on (r1, code defaults), parkour no longer triggers on the one-high step, bump and
  pit (accepted in §0). The step-up is now walked by the solver: vx 54.2–67.0 (was 59.0–62.5 under
  parkour). The 1-wide bump is walked over level (the collision hull clears it by 2.3 px; legs are
  virtual). The 1-wide pit now shows the ledge boost (vx 87.6), which parkour had been hiding.
- r2 #6 (fall onto a block corner from x=213): Mantle no longer fires; the body is caught on the
  block by the legs (Drive peaks 1559 px/s², vx 63.8) and then rises slowly onto the top.
- §12.4's third case (a refused plan whose last segment runs along a wall) was not constructed.

### Item 2 — `ChannelProbes`, read-only (stage 1)

Change: `EnvironmentContext` gains `Floor` (C-space floor envelope, band −2 … +`LegReach`, payload
y / distance / the ground probe's surface velocity; cached per body position), `LegsInReach`,
`Launching` (the support-relative rise test), `Legs` (= in reach and not launching), `Support(out
hold)` (the ground probe within `SupportReach` with the rise test; hold strength as payload),
`HandCorner`, `Walls(out left, out right)` and `Grip(corner)`. Velocity-dependent probes are
evaluated on demand, not cached, because a state's `Enter` can write velocity between a
precondition and the tracker. Routed through them, thresholds unchanged: the tracker's floor,
`near`, `ballistic`, support velocity, corner-plant and contact-push masks; `FoldBaseline`;
`IsStandingGround`; `JumpingState.LegsInReach`; `DropdownState`'s floor.

Trace: r1 and r2 byte-identical to item 1.

Where two definitions of one probe disagree (read from the code; distances are center → tile top
unless stated; the polygon bottom is center + 7.2):

| Probe | Definitions | Disagreement |
|---|---|---|
| Legs (reach) | `LegsInReach`: C-space envelope ≤ 21.8 (top ≤ center + 29.0), over the hull's full width with bevels. Standing ground probe (`TryGetGround`): strip under the polygon, inset 2 px each side, top ≤ bottom + 21.8 (= center + 29.0) | Same vertically; sideways the strip loses the floor 2 px + the bevel earlier at an edge than the envelope. This is the ledge boost: Standing ends (Falling owns the body) while the channels still have legs. |
| Legs (rise test) | Redirect mask: `Legs` (reach and not launching). LegServo, Drive, Tuck masks: reach only (the leg servo's cap fades with rise instead). Contact push: `Grounded && −vy ≤ 80` absolute. Climb triggers (`ClimbStates` 75, 192) and `BallisticPredictor` 199: absolute vy. `Launching`, `Support`: relative to the floor's velocity | Three channels skip the test; three sites use absolute instead of support-relative vy. |
| Support | `FoldBaseline` / `IsStandingGround` (now `Support`): ground probe, top ≤ center + 29.8, hold full at ≤ 28.8. Tracker's `anchored`: C-space distance ≤ 29.8 (= top ≤ center + 37) — but its envelope only searches to `LegReach`, so `anchored` is identical to `near`. Coast predictor / planner: C-space ≤ 29.8 | `SupportReach` is applied to two different distances (tile top vs C-space surface, 7.2 px apart); the tracker's support collapses to leg reach. |
| Standing (state) | Entry: `Support`. Continuation: `TryGetGround` (no rise test, reach 29.0). Channels: `LegsInReach` (C-space) | Entry, continuation and channels are three tests. |
| Jump source | Planned-rise vs impulse choice: `LegsInReach`. Precondition: `TryGetGround`, stair probe, gripped corner, or `JumpSupportChecker` (exposed tops to bottom + 21.8, 4 px past the sides, lips 6 px above the foot line). Continuation: ground probe with 60 px slack (`JumpSourceProbeSlack`). Running jump: `TryGetGround` | Four reaches for "can I push off". |
| Floor (other users) | `GroundChecker` strip (states: Dropdown, WallSlide, Stunned, Tumble, Dash, CoveredJump, Crouched, the PlayerCharacter fall check). `TryGetCrouchGround` (slack + hover offset). Floor envelope (tracker, jump, dropdown) | Two geometries (strip vs C-space). |
