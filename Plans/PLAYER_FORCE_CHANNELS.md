# Player force channels — how stacking them makes jumps/movement too fast

*Survey, 2026-10-18. Read-only research against `fighter-plan`; line numbers drift — re-grep
before relying on one. Motivation: a possible general limit on player force output,
especially vertical.*

## 0. Reference numbers

- `MaxAssistRiseSpeed` (derived, `MovementConfig.cs` ~30-42) =
  max(100 + (1500−600)·0.12, 120 + (1200−600)·0.12, 100) = **208 px/s**. Every corrector
  rise ceiling is this.
- `SpringMaxRiseSpeed` = **80** — the "ballistic / launch gate".
- Fold engine = `"lattice"` (`configs/movement_config.json`). The `ref` (`GuidedMaxForce`
  10000) and `lm` (`FoldLmMaxForce` 8000) engines are dormant but hot-swappable.
- **Write order within one step** (this is what decides who sees whom):
  1. `PlayerCharacter.Update` releases the hitstop velocity stash.
  2. The movement state's `Update` sets `AppliedForce` (`=`); its closing `ApplyAmbient`
     adds the corrector's tick-0 (`+=`).
  3. `ApplyActionForces`, then the GravityScale counter-force
     (`AppliedForce += Gravity·(GravityScale−1)`).
  4. `Simulation.Step`: `CombatSystem.Apply` (knockback) → `ForceFieldSystem.Apply` →
     `PreStep` (hitstop hold, `AppliedForce = −gravity`) → `PhysicsWorld.StepSwept`
     integrates.

## 1. Inventory

### Movement-state baselines (per-frame force, `=`)

| Channel | Where | Axis / size | Existing cap |
|---|---|---|---|
| Fold gravity hold + station friction | `LocomotionStates.cs` `FoldBaseline`; also Stair, Carried | up to 600 up (cancels gravity) | only while supported and rising ≤ 80 rel. support; fades to 0 at SupportReach |
| Jump hold forces | `JumpStates.cs`, `WallStates.cs` | 1500 / 1200 / 1500 / 1500 / 1000 up | time: `MaxJumpHoldTime` 0.12 s |
| LedgeJump servo | `LedgeStates.cs`, `JumpServo.cs` | ±3000 + 600 gravity cancel | two-sided servo to `LedgeJumpTargetVy` **−210** (> 208) |
| LedgeGrab spring + gravity cancel | `LedgeStates.cs` | 600 + spring | rising momentum coasts uncaught while the press is held |
| AirControl | `AirControl.cs` | horizontal 1500 | lands on `MaxAirSpeed` 150; braking skipped under `PreserveExternalVelocity` |
| Fast fall | `LocomotionStates.cs`, `WallStates.cs` | 1000 down | — |
| TerrainCarried servo | `TerrainCarriedState.cs` | both axes, ±3000 | ride target + air speed |
| CoveredJump slide spring | `JumpStates.cs` | spring | `SpringMaxRiseSpeed` excess brake |

### Corrector channels (per-frame force, `+=`)

`CorrectorChannels.BuildFold` — the lattice tracker's set:

- **LegServo** ≤ 6000, fades to 0 at `FoldLegPushFadeSpeed` 400 rise; `RiseCap` = 208 + frameRise.
- **Drive** horizontal 3000.
- **CornerAssist** 1500 up, `RiseCap` (masked off on lattice).
- **Redirect disc** 1500 (force lever on lattice), `RiseCap` + `ForwardCap`.
- **Tuck** 3600 down. **ContactPush L/R** horizontal 1000.
- **AirLateral / AirVertical** 1500 / 300 (masked off on lattice).
- **CornerPlant** 0 → channel omitted.

`BuildManeuver` (climb-family feasibility probe only today): LegServo, CornerAssist,
Redirect — **no `RiseCap`**; Redirect has only `ForwardCap`.

`BuildGrip` (LedgePull, climb `Update`): `LipLiftForce` 2000, `LipPushForce` 500, each fading
with pace — **no `RiseCap`**.

### Instantaneous velocity writes — bypass every force cap

- Jump launches (source-relative vy −100 / −120 / DoubleJump −100 replace / Covered).
- WallJump `(±250, −150)`; tech bounce −260; climb entry hop
  `min(vy, −min(apex, ceiling, 208))`; Dropdown exit ×0.4; LedgeGrab zeroes on tap.
- Actions (`ActionStates.cs`): slash recoil `+=`; DownAir pogo
  `vy = min(vy, −clamp(|recoil|, 140, 270))`; **stab lunge** "ensure ≥ 135 along StabDir"
  every frame 0.05–0.40 s; stab recoil 280, rise capped 300.
- Combat: `AddKnockback`; the hitstop stash release.

### Action-side forces

GravityScale counter-force, added after the corrector: ×0.3 charge wind-up and Pulse, ×0.4
Burst, ×0.5 LobbedArea — up to **+420 up**. MovementModifiers only scale speeds and
accelerations (never add energy), but `PreserveExternalVelocity` disables the over-cap brakes.

## 2. Mechanisms

### A. Rise caps are per channel, not on the total — OPEN

`RiseHeadroom` / `ClampRise` (`CorrectionSolver.cs` ~628-646) measure each channel against
`CoastVel[k]`, the no-correction rollout, never against what the other channels add on the
same tick. So LegServo and the Redirect disc can each fill the whole gap up to 208, and
together exceed it. On lattice, Redirect is masked once rise rel. support > 80, which leaves
corner plants as the case where both lift together. Raising `FoldCornerPlantForce` above 0
adds a third channel to the same gap.

### B. Legs stay on for double jumps and wall jumps — OPEN (skipped row 18)

`FoldProfile.Jump` has `Legs = true` (`AmbientCorrector.cs` ~75-80), used by
`DoubleJumpingState` and `WallJumpingState` — contradicting DoubleJump's own comment ("the
legs play no part"). Over a pillar the floor is within `LegReach` (21.8 px), so the legs
re-arm under a body that is already leaving: `LatticeScenarioTests` row 18 is skipped on
"~4300 px/s² of leg lift while rising at 210", peak ≈300. `MaxAssistRiseSpeed` also counts
only −`DoubleJumpVelocity` for the double jump, ignoring its 1500 hold force. `ImpulseJump`
(Legs = false), used by RunningJump and hybrid Jumping, is the existing fix pattern.

### C. Action forces arrive after the corrector planned — OPEN (one symptom fixed)

`ApplyActionForces` and the GravityScale counter-force run after `ApplyAmbient`, so the
corrector's rollout never sees them. A charge held during a jump nets 1500 − 600·0.3 = 1320
up instead of 900 → ≈**258 px/s vs 208** (+24%); the lattice legs' `RiseCap` is computed
without that +420.

Grounded, the fold already cancels gravity, so GravityScale 0.3 is +420 of free lift. This
was the stab-charge "hover + spasm" (body rose ~3.4 px and flickered Standing↔Falling);
**fixed 2026-10-18** by making the charge's dip air-only (`RecoveryAction`). Pulse, Burst and
LobbedArea still apply their GravityScale on the ground — check them for the same hover.

### D. Velocity writes skip every force cap — OPEN, partly deliberate

- **Stab lunge.** `StabDir` is clamped only in X, so an upward swipe lunges upward. An upward
  ground stab holds vy ≈ −135 for ~21 frames (~47 px of flight with no gravity loss), and a
  jump in the window raises the launch from 100 to 135 before the hold force → peak ≈243
  (worked out from the code, not measured). The 2026-10-18 changes (`LungeSpeed` 90→135,
  vault/stair states counted as grounded) widen this: the lunge can now start mid-vault.
- **Pogo** max 270 and stab recoil 280 (rise capped 300) exceed 208. The pogo comment's "a
  jump nets ~280" ignores gravity; the real jump ceiling is 208, so the pogo is ~30% above a
  jump, not "barely matching" one.
- **WallJump** |v| = 291 at launch (vx 250 > air cap 150 on purpose). **LedgeJump** 210 > 208.

### E. Redirecting speed from one axis into another — mostly guarded

The Redirect disc adds no energy but rotates velocity. Each guard followed a measured
incident: `ForwardCap` (it minted 175 px/s vx from hop energy), `RiseCap` (ground jump 256.6,
wall jump 335.1), the ballistic mask (446 px/s² of lift on flat launches). Still open:
`BuildManeuver`'s disc and CornerAssist have no `RiseCap` — inert today (probe only), live if
`ManeuverCorrector` ever drives the body again. Stairs/bevels: x was capped but total speed
was not, so the legs added rise (167 px/s hops) — fixed by capping speed along the path
(`LATTICE_SCENARIOS.md`, "A walk speed is a speed"). The leg fade at 400 is the same for bumps
and launches, so bumps become small hops (corridor leg rise 150 vs 79 on `qp`) — flagged,
unchanged.

### F. States re-entering mid-rise and re-adding hop energy — guarded

The launch gate `−vy > SpringMaxRiseSpeed` appears in the climb family (entry and
completion), `IsStandingGround`, `FoldBaseline`, lattice `ballistic` and qp `launched`.
Without it the jump-buffered ground probe (20 px slack) let Standing or a vault grab a rising
body. Jumps *replace* vy relative to the source surface instead of adding to it, so buffered
jumps, landing bounces and climb hops don't stack; LedgeJump's two-sided servo brakes a pull's
extra vy down to 210. Remaining gap: climb and stair `Enter` reset `HasDoubleJumped`, so
jump → climb → corner-launch jump → double jump re-arms resources. Each step is individually
capped and the launch gate confines it to near-floor moments.

### G. Ceilings measured relative to a moving surface — intentional

`RiseMax = 208 + max(0, −supportVy)`. A jump off a sprout rising at 110 px/s legitimately
reaches ~318 in world terms (BACKLOG §5.8, `SproutLiftJumpTests`).

### H. Lattice planned jump

The legs ARE the launch (6000 force, `RiseCap` 208). Measured before the cap: neutral 247,
running hop 270; the fourteenth pass brought the jump-near-a-corner from 256.6 to 215.4.

## 3. Where a central limit could go

**Option 1 — `PlayerCharacter.Update`, right after the GravityScale counter-force.** The only
point where movement, corrector and action forces are all summed. Covers A, B, C. Misses
every velocity write (launches, climb hop, lunge, pogo, recoil, tech) and everything
`Simulation.Step` adds later (knockback, fields, `PreStep`).

**Option 2 — just before `StepSwept`.** Adds fields and `PreStep`, but `PreStep` sets
`AppliedForce = −gravity` for hitstop, so any vertical cap under 600 lets a frozen body
fall; fields come from opponents, so clamping them is a combat change.

**Recommended shape: cap the player's own velocity change per frame, not force magnitude.**
Record v₀ after the hitstop release; after the GravityScale line compute
v′ = v_now + AppliedForce·dt; apply `RiseCap`'s rule to the *total* — the body may not leave
the frame rising faster than max(rise at v₀ + gravity·dt, ceiling + frameRise). Trim the
upward force first, then the velocity write. This catches D and C, and excludes knockback and
fields automatically (they arrive after `Update`). The launch states' authored impulses need
an exemption or a per-state ceiling (jump 208, wall jump ~198, LedgeJump 210, tech 260, pogo
270, recoil 300); `SpeedInvariantTests.DeliberateRise` already lists them.

**Why not a plain force-magnitude cap:**

- *Landing catch:* legs at 6000 catch a 300 px/s fall in a few ticks. A smaller cap means
  harder landings → more crush damage (`CrushImpulseThreshold` 700) and different
  bounce/break behaviour that the impact-materials tuning is calibrated against. If capping
  force at all, cap only while rising.
- *Needs big forces:* LedgeJump 3000 + 600, ledge-pull lift 2000, the lattice planned jump
  (all leg force), TerrainCarried ±3000, jump holds 1500 (and `MaxAssistRiseSpeed` is derived
  from those holds).

**Other risks:**

- *Planner mismatch.* `ClimbManeuverBase.Delivers`, `LatticePlanner` and `BallisticPredictor`
  assume uncapped dynamics; a post-hoc clamp makes climbs pass the trigger and then not
  deliver, and the tracker plan moves it can't execute. Making the solver's `RiseCap` cover
  the total (fix A) is the better long-term home.
- *Determinism* is fine: a pure clamp needs no rollback state.
- *Tests.* `SpeedInvariantTests` says "a clamp would mask new rockets, a growth check names
  the frame" — keep those asserting pre-clamp values, or log every frame the clamp fires.
  Trajectory-pinned tests at risk: `LatticeScenarioTests` (row 16: double-jump rise within
  2 px of control), `JumpExitSweepTests`, `RunningJumpLatticeTests`, `LedgePullExitTests`,
  `SproutLiftJumpTests`, `PlayerImpactByVelocityTests` (double-jump impact ≈363),
  `BounceTests`, tumble/tech tests. Several are already red by design — check BACKLOG §5.

## 4. Targeted fixes that don't need a global limit

1. Make `RiseCap` measure the total across channels (fixes A).
2. Give DoubleJump and WallJump a legs-off profile (fixes B, un-skips row 18).
3. Let the corrector's rollout see action forces, or move the GravityScale counter-force
   before `ApplyAmbient` (fixes C).
4. Limit the stab lunge to the horizontal component (fixes the upward half of D).
