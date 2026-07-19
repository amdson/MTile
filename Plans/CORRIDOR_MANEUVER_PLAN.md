# Corridor Probe & Maneuver Layer

Status: design agreed, nothing built. Prereq (shipped, uncommitted): SteeringRamp reflex fixes —
steep-angle Weight taper (~63°..77° fade; flush starts get no ramp assist) and the ballistic
crest cap in ParkourState (`MaxRedirectVy = min(config, √(2g·BindClearance))`).

## Purpose split (the design contract)

Two layers with different authority; keep them sharply separate.

- **Reflex layer** = SteeringRamps, as they now are. Always-on, energy-neutral, single-corner,
  zero lookahead, small-angle only (the taper enforces the domain). DONE — no further
  intelligence is ever added here.
- **Maneuver layer** = discrete, intent-gated movement states (mantle, arc-jump, preemptive
  duck). May inject energy ONCE at entry, may anticipate within a 1–2 block horizon. Planning
  happens at the entry decision (feasibility), not as continuous replanning. Cancel-on-release
  always.

Horizon is deliberately capped at ~2 blocks: within a body length reads as character reflexes;
beyond reads as autopilot. Route choice and speed management belong to the player.

## Piece 1: CorridorProbe (`Character/CorridorProbe.cs`)

Pure function, no allocation, no statics: `Scan(PhysicsBody, ChunkMap, int dir) → Corridor`.

- Column-scan 3–4 tiles ahead of the body in `dir`. Per column: `floorY` (top of supporting
  solid), `ceilY` (bottom of nearest solid above), converted to a body-center gate
  `[ceilY + halfHeight + margin, floorY − standHeight]`.
- Truncate at the first infeasible column; record `TruncationReason { Horizon, Pinch,
  TallRise, NoFloor }` + column index. Horizon-edge truncation is OPEN (no deceleration
  toward the unknown); in-window truncation is a WALL. This is where impossible geometry is
  rejected — before any maneuver looks at it.
- Binding corners: up to 2 per side via the degenerate 2-corner hull (cross-product checks on
  corners offset by the existing `Clearance` / `OverVertLift` constants — same Minkowski
  inflation the ramps use).
- Exposed as **measurements, not classifications**: first-rise height + forward distance,
  floor-hull slope, lowest overhead bottom vs head height, pinch gap width, truncation.
  No CorridorClass enum — each maneuver's precondition owns its predicate over the numbers,
  keeping decision logic in the FSM where it already lives.

`EnvironmentContext.TryGetCorridor(dir)` caches per frame, same idiom as the existing nine
queries. The probe is pure ⇒ nothing to snapshot. Only allowed cross-frame state: a hysteresis
bit in MovementVars, and only if FSM Active/Passive stickiness proves insufficient.

Determinism rules: fixed iteration order (column-major, then y), no sim-affecting statics, no
caching across steps outside MovementVars.

Thresholds (flush distance, shallow/steep slope boundary, horizon columns, gate margin) go in
MovementConfig for hot-reload tuning. Debug overlay (gates + corners + truncation marker) in
DebugOverlayRenderer from day one — tuning depends on seeing it.

## Piece 2: maneuver states

Plain MovementStates in the guided band (25–45). NO shared base class until the second
maneuver shows what is actually common. Shape of each: predicate over corridor measurements
(+ intent gate) → optional one-shot entry impulse with full-maneuver feasibility rollout →
guided phase driving TargetVelocity through existing contact machinery → cancel on release.

**Shared arc math lives in a helper, not a base class.** `GateArc` (with BallisticProbe folded
in or beside it): (entry pos/vel, landing gate, ceiling gates en route) → feasible arc +
per-frame target velocity + progress, or abstain. Mantle and ArcJump both consume it; the
ceiling-flattening logic is written and unit-tested ONCE. This is the codebase's existing
idiom (AirControl, checkers, SteeringRamp are shared helpers; states never share via
inheritance). **Handoff logic is written nowhere:** a maneuver just ends when its arc
completes; whichever state's preconditions match the delivered gate (UnderpassState for a low
one) claims the next frame via normal passive-priority arbitration — same as every existing
state transition.

**Gates are the targets, not poses.** A maneuver delivers the body into a corridor GATE
(the fused floor/ceiling interval), never to an obstacle-specific pose like "standing on the
lip". This is what dissolves the hybrid cases that forced ParkourState to be an over+under
bundle: a mantle into a low-roof tunnel is just a mantle whose landing gate is low — the
entry rollout shapes a flatter arc under the ceiling gates and the body arrives already at
crouch height. Composition lives in the gate data, not in state arbitration; no frame ever
needs two maneuvers active at once. Handoffs between maneuvers are sequential, at clean
boundaries (e.g. grounded-in-tunnel → underpass locomotion).

Commitment rule (applies to all energy-injecting maneuvers): the entry impulse is committed,
but air control and release-to-cancel apply from frame one — an assisted hop can always be
turned into an ordinary jump-fall.

1. **MantleState** — flush-adjacent 1-block step (the case the steep taper vacated).
   Precondition: rise in vault band, forward distance < flush threshold, and a landing GATE
   exists past the lip (non-pinched at crouch height — NOT "standing headroom"; a low-roof
   landing is a valid mantle that arrives crouched). Motion: guided servo up-and-over
   (LedgePullState idiom), arc shaped to fit under the landing's ceiling gates; no ramps.
2. **ArcJumpState** — steep stairs / 1.5–2.5 block rise. Entry impulse `vy₀ = √(2g·(rise+lift))`
   from the measured rise; `BallisticProbe.TryClearArc` (parabola swept-sampled via TileQuery)
   validates lip clearance + headroom + landing before committing. Guided descent onto the flat.
   (Possible math reuse: ballistic solve at ActionStates.cs ~line 2119.)
3. **UnderpassState** (née PreemptiveDuck) — stable crouch-height traversal under low ceilings
   via a held contact (kills the duck bob), stand on exit. Two entry paths, like LedgeGrab's:
   (a) preemptive, from open ground when an overcrop bottom is below head height within
   braking-lead distance (pose anticipation, not route anticipation); (b) handoff, when a
   mantle/arc delivers the body grounded into a low gate. Test fixture from day one:
   mantle-into-low-roof-tunnel — the hybrid case that forced ParkourState to bundle over+under.

## Piece 3: ambient reflex system (NOT a third FSM)

Ramp generation is generally useful but currently gated behind state lifecycle — three states
hand-roll acquire/refresh/release (ParkourState.Reconcile, CoveredJumpState.EnsureContacts,
PlatformDropState.EnsureRamp), and airborne motion gets no reflexes at all (jumping into a
ledge corner clips it; todo #45 "ceiling sweep" is an air reflex with no home). Fix by making
the reflex layer ambient — but as a per-frame reconciliation SYSTEM, not an FSM: it has no
modes, transitions, or hysteresis, and a third state machine would triple the cross-FSM
interaction surface the Modifiers/AppliedForce channel discipline exists to avoid.

- `RampPolicy` — published per frame by the active movement state (actions can modify), on the
  MovementModifiers pattern: over/under enable flags, MaxForce/MaxRedirectVy caps, optional
  target velocity. Default = on with weak caps (plain run/jump/fall get reflexes); servo states
  (LedgePull) and hitstun publish off. Opt-out channel replaces lifecycle code.
- `ReflexSystem.Reconcile(body, corridor, policy)` at a fixed update phase: diff the body's
  SteeringRamps against what probe + policy require; stamp/refresh/remove. The three
  hand-rolled lifecycles collapse here.
- Snapshot bonus: fully reconciled-per-frame ramps may drop out of snapshot state entirely
  (rebuild-on-restore).

ParkourState afterwards = the drive (entry-speed target through ramps) + animation contract
(AnimTag.Parkour, vault progress, hand grip). It stops being "the state that makes ramps
exist". Possible far-future: dissolve into run/stand with anim keyed on ramp engagement — NOT
now. Sequencing: needs the corridor probe as sensing input → build as step 3½, alongside the
ParkourState precondition migration.

## Where ParkourState remains

ParkourState keeps the game's most common case: **carrying existing momentum through transient
corner geometry** — running vaults, running duck-unders, shallow stairs at speed. Design rule:
*ParkourState redirects motion the player already has; maneuvers generate motion the player
doesn't have.* It stays the state-machine face of the reflex layer (ramp lifecycle,
entry-speed drive, vault anim/grip contract). Arbitration vs MantleState is a fallback chain,
not a contested boundary: at speed the ramps engage before the body is flush → Parkour wins;
too steep/slow → taper makes ramps abstain → body ends flush and slowed → Mantle's
precondition. The taper IS the switch; no explicit coordination.

Mantle vs ArcJump stay separate states (despite both consuming GateArc) because they are
different player contracts: energy injection, jump-input interaction, commitment semantics.
Merging by measurement would be a milder rerun of the over+under bundling trap.

## Build order (each step ships independently)

1. CorridorProbe + EnvironmentContext.TryGetCorridor + CorridorProbeTests. Zero behavior
   change. ASCII fixtures: staircase, tunnel, zigzag, pinch, overcrop, flush wall, open floor —
   assert gates/corners/slopes/truncation directly. Cross-check probe agrees with the current
   checkers on their cases.
2. MantleState (first consumer; fills the flush-step gap).
3. Migrate ParkourState preconditions onto the probe; ExposedUpperCornerChecker /
   ExposedLowerCornerChecker shrink to wrappers or die. Behavior-neutral, pinned by existing
   sim tests.
4. ArcJumpState (+ BallisticProbe), then PreemptiveDuckState.

## Anchor scenarios (acceptance contract — SimTerrain fixtures for each, written before the
implementation they exercise)

1. **Run + 1 block up onto flat** — no hitch/hop; bends up over the corner, lands at run speed,
   zero overshoot. Owner: reflex layer (SHIPPED — crest cap; vault test asserts apex ≤ rest+2px).
   Flush/slow variant → MantleState.
2. **45° staircase (1-up-1-along) at a run** — reads as running up a ramp; continuous diagonal,
   no per-step kicks. Owner: reflex + two-corner hull (kills the Reconcile sawtooth). Crest cap
   fires only at the top step. Fixture: assert no vy spikes between steps.
3. **Staircase with >1-block risers** — bouncy/arc-y; one hop per riser, cadence emerges from
   tread spacing. Owner: ArcJumpState repeatedly (grounded frame between hops). Fixture: state
   sequence ArcJump→grounded→ArcJump per riser; small vy at each landing (guided descent).
4. **Any of the above arriving into a 2-high corridor** — climb completes with flattened crest;
   body slips under the roof at tight standing height. Owner: gates-as-targets + GateArc ceiling
   shaping. MOST AT-RISK unbuilt piece; build the fixture before GateArc. ArcJump's rollout must
   flatten-or-degrade-to-climb when the arc can't fit under the landing roof — never fire+bonk.
5. **Level entry into a 2-high tunnel** — barely an event: slight head-tuck at the lip via
   ambient under-ramp (ReflexSystem), normal running inside, NO bobbing (UnderpassState's
   stable hold claims it if the spring/lip margin fights).
6. **1-block drop into a 2-high tunnel mouth** — one motion: walk off, sink, tuck under the
   lip, keep running. No maneuver state involved — pure composition: gravity/PlatformDrop +
   ambient under-ramp + fused descending-floor/low-ceiling gates. Best integration fixture.

Load-bearing pieces across the six: two-corner hull (2), GateArc ceiling shaping (3, 4),
ambient under-ramps (5, 6). Everything else is shipped or plain arbitration.

## Explicit non-goals

- No route planning / navigation; corridors are x-monotone in the held direction only.
- No horizon growth beyond ~2 blocks; no anticipatory braking for geometry outside the window.
- No continuous replanned trajectories — reflex ramps + entry-planned maneuvers only.
- Gap-crossing / long ballistic planning is out of scope for the corridor system.
