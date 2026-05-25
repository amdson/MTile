# Top-of-todo.txt Plan

Plan covering the first 10 bullets of `todo.txt` (the active ones, above the blank
line break). Step numbers below are execution order, not todo-bullet numbers.
Each step lists the concrete files/lines it touches so the work can be picked up
without re-spelunking the codebase.

## Phase A — Quick win / config plumbing

### Step 1 — Bullet 8: `SproutLifetime` from `game_config.json`
- Add `public float SproutLifetime { get; set; } = 0.1f;` to [GameConfig.cs](../GameConfig.cs).
- In [Simulation.cs](../Simulation.cs) ctor where `MovementConfig` is loaded, write the
  override (`MovementConfig.Current.SproutLifetime = gameConfig.SproutLifetime` — or,
  cleaner, add a constructor param on `Simulation` and remove the
  [MovementConfig.cs:136](../Character/MovementConfig.cs#L136) hardcoded default's
  role as the source-of-truth).
- Mention in [CLAUDE.md](../CLAUDE.md) "Config & assets" section.
- Snapshot impact: zero — it's a static tuning constant.

## Phase B — Physics foundation (unlocks #2, #6, #7 diagnostics)

### Step 2 — Bullet 1: per-contact accumulated impulse
- Extend `PhysicsContact` (or just `SurfaceContact`) in
  [Physics/PhysicsContact.cs](../Physics/PhysicsContact.cs) with `LastImpulse`
  (or a 2D `LastImpulse` vector) + a `BeginStep()` that zeros it. Don't snapshot it —
  it's per-step.
- Reset all contacts' accumulators at the top of
  [PhysicsWorld.StepSwept](../Physics/PhysicsWorld.cs) (alongside the existing
  `body.LastImpulseMagnitude = 0` reset).
- At every impulse site, after computing `vnRel`, add `vnRel * body.Mass` (or just
  `vnRel` since the integrator is mass-less) to the contact's accumulator:
  - [PhysicsWorld.cs:120-132](../Physics/PhysicsWorld.cs#L120-L132) discrete resolve
  - [PhysicsWorld.cs:276-305](../Physics/PhysicsWorld.cs#L276-L305) swept resolve
  - friction sites at [PhysicsWorld.cs:367-389](../Physics/PhysicsWorld.cs#L367-L389)
    (tangential component)
  - [SteeringRamp.ResolveVelocity](../Character/SteeringRamp.cs#L99-L137) redirect —
    measure `Δv` before/after
- For dynamic contacts created mid-frame (chunk collisions): create the
  `SurfaceDistance` first, accumulate into it, then add to `body.Constraints` —
  gives a uniform place for #6's tests to read.
- Snapshot impact: zero if you keep these fields non-`Maintained`.

### Step 3 — Bullet 2: cap ramp-applied speed
- `SteeringRamp.MaxSpeed` already exists ([SteeringRamp.cs:31](../Character/SteeringRamp.cs#L31))
  but `ParkourState` only sets it at
  [MovementStates.cs:718-719](../Character/MovementStates.cs#L718-L719). Two changes:
  - Hard-cap **vertical** component added by `ResolveVelocity` (currently it caps
    total magnitude — a steep redirect on a high ledge converts horizontal speed
    into a fast vertical kick that the magnitude cap still allows). Add
    `MaxRedirectVy` and clamp the vertical delta after the rotation in
    [SteeringRamp.cs:103-136](../Character/SteeringRamp.cs#L103-L136).
  - In `ParkourState.Enter`, scale `MaxSpeed`/`MaxRedirectVy` by entry horizontal
    speed so high-ledge parkour at slow walk doesn't yeet upward.
- Verify with a sim test (`SimRunner` + ascii: tall L-shaped wall): walk into a
  1-tile vault and a 4-tile-high stack — peak vY in the second case must not
  exceed jump speed.

## Phase C — Sprout/standing jitter

### Step 4 — Bullet 7: standing jitter on sprout→solid promotion
- Symptom analysis: in [Simulation.Step](../Simulation.cs) phase order, `TickSprouts`
  runs (step 4) *before* combat + `_player.Update` + `StepSwept` (steps 5/7). A
  sprout finalizing under the player has its animated geometry from
  [TileSproutNode.cs:61-68](../World/TileSproutNode.cs#L61-L68) replaced by the
  static tile shape mid-frame; the standing-spring contact in
  [StandingState](../Character/MovementStates.cs) was last resolved against the
  previous geometry.
- Diagnose first with a sim test: spawn standing on a `Sprouting` cell, tick until
  it finalizes, log body Y per frame. Look for the discontinuity.
- Likely fix: when a sprout is promoted to `Solid` *inside the body's bounds*,
  snap the body's Y up by the polygon-top delta in the same atomic write rather
  than letting the next `StepSwept` push it up via collision resolution. Add a
  "promoted-under-body" callback from `ChunkMap.TickSprouts` consumed by
  `PhysicsWorld` (or the Standing state's `EnsureGround` rebuild).
- Snapshot care: the fix path must be idempotent on restore (same tile finalization
  journal entry → same Y snap).

## Phase D — Test scaffolding using Phase B data

### Step 5 — Bullet 6: movement-never-breaks-blocks tests
- New file `MTile.Tests/Sim/MovementImpactTests.cs`. Pattern from
  `MTile.Tests/Sim/EruptionPillarTests.cs`.
- Scenarios:
  - Walk into a wall at max walk speed (~100 px/s) for N frames → assert
    `chunks.GetCellState(wall) == Solid` AND that wall tile's `TileDamage.GetHP`
    is unchanged.
  - Run-jump and land on `Sand` (lowest HP) from 1, 2, 3, 4-tile drops → unchanged.
  - Crouch-slide into wall → unchanged.
  - Dropdown 1 tile → unchanged.
- Use the per-contact `LastImpulse` from Step 2 to assert *why* it didn't break
  (impulse < `ImpactDamage.ImpulseThreshold = 700f` at
  [PlayerCharacter.cs:188-192](../Character/PlayerCharacter.cs#L188-L192)) —
  surfaces regressions clearly.
- If any case currently *does* break a tile, the test fails and tells us to
  either lower player Mass, raise `ImpulseThreshold`, or gate
  `TryApplyImpactDamage` on a state-controlled flag (e.g., disable impact damage
  while in free movement states; require launch/jump/stab to enable it).

## Phase E — Action FSM tightening

### Step 6 — Bullet 9: cooldown re-arming block charge after a placement
- Add `_lastPlacementFrame` (sim frame index, `int`) to `PlayerAbilityState` (or
  directly on `ActionVars` for `BlockReadyAction`) so it round-trips through
  snapshot.
- In [`BlockReadyAction.TryDragPlace`](../Character/ActionStates.cs), stamp
  `_lastPlacementFrame = ctx.Frame` on a successful placement.
- In `BlockReadyAction.Update`, gate the charge accumulator on
  `(ctx.Frame - _lastPlacementFrame) >= PlacementCooldownFrames` (suggest 6 frames
  = 0.2s). Below that, force `vars.ChargeTime = 0` so the wind-up visual never
  flickers during continuous building.
- Add a sim test: drag-place across 5 tiles, assert `ChargeTime` never exceeds zero.

## Phase F — Ledge cluster (one PR-sized chunk in `MovementStates.cs`)

These three share the same Ledge/Dropdown state file and interact, so do them
together. Refactor the input semantics once instead of three times.

### Step 7 — Bullets 3 + 4 + 5 together

- **#3 (hold-Up auto-climb, tap-jump stays):**
  - Today [LedgePullState.CheckPreConditions:882-885](../Character/MovementStates.cs#L882-L885)
    needs `UpJustPressed` and
    [`CheckConditions:889`](../Character/MovementStates.cs#L889) needs sustained
    `Input.Up`.
  - Change `LedgePullState` to trigger when Up has been **held for
    `HoldClimbFrames`** (e.g. 6) while in `LedgeGrabState`, regardless of when it
    was pressed. Track `UpHeldFrames` on `LedgeGrabState`'s `MovementVars`.
  - Tap-jump (Space): handled by existing jump-state preempt — verify that pressing
    Space exits LedgeGrab into a normal `JumpingState`/`WallJumpingState` without
    crossing the hold threshold.
- **#4 (right → pull over):**
  - In `LedgePullState.CheckPreConditions`, add an OR branch: held horizontal
    direction == `_wallDir` (toward the wall = over the lip).
  - In [LedgeGrabState.CheckConditions:787-794](../Character/MovementStates.cs#L787-L794),
    the "pressing away" check needs to skip when direction is *toward* the wall —
    it currently treats any horizontal input as cancel. (Re-check the sign —
    `pressingAway` looks correct, but the symmetry of "pull over" is the new
    behavior.)
- **#5 (dropdown → grab unless Down held):**
  - In [DropdownState.Exit:1054-1063](../Character/MovementStates.cs#L1054-L1063),
    after the airborne handoff, check if `ExposedUpperCornerChecker.TryFind*`
    finds a corner at grab range AND `!ctx.Input.Down`. If yes, set a transient
    `WantsLedgeGrab` flag on `PlayerAbilityState` that
    `LedgeGrabState.CheckPreConditions` honors for one frame (or just call into
    LedgeGrab entry directly via the FSM's normal scan — most maintainable).
- Sim tests for each of the three behaviors (one ascii ledge fixture, three input
  scripts).

## Phase G — Parkour leap

### Step 8 — Bullet 10: mini-jump into ParkourState on 2-tile ledge + Up
- Extend `ExposedUpperCornerChecker` to report corner *height* above body feet
  (it already reports `InnerEdge` position; just need the caller to use it).
- In a free state (`StandingState` or `RunningState`), add a precondition path that:
  - Up is pressed, no jump active, grounded.
  - There's a corner ahead in facing direction at 24–40 px height (2-tile vault).
  - Body has space above for the leap apex (use existing ceiling clearance check).
- Apply: small upward impulse (scaled by horizontal speed and inversely by leap
  height) + entry into `ParkourState` with elevated `EntrySpeed`. Compose this on
  top of #2's velocity cap so the leap doesn't blow the cap.
- Edge case: don't fire if a normal jump is being requested simultaneously (Space) —
  let jump take priority.

---

## Risk + sequencing notes

- **Steps 2 and 3 should ship together** — Step 3 reads new fields Step 2 adds;
  isolating them halves the value.
- **Step 4** is the riskiest (touches `Simulation.Step` phase order or `WriteTile`
  atomicity). If it gets ugly, ship Phase D/E/F first and circle back.
- **Step 7** is the biggest single-file refactor; carry a checklist of the three
  sub-bullets to make sure none gets dropped mid-merge.
- **Snapshot determinism gate** for each step: anything new on
  `ActionVars`/`MovementVars` is captured automatically (struct copy); anything on
  `PlayerAbilityState` needs to thread through `PlayerAbilityState.Clone` and
  `PlayerSnapshot`.
- **Cross-build (KNI vs DesktopGL)**: nothing in the plan touches a host-specific
  API, but if Step 4 reaches for `FileSystemWatcher`-style timing or threading,
  don't — sim only.

## Bullet → Step index

| todo.txt bullet | Step |
|---|---|
| 1. Track forces applied by physics contacts | Step 2 |
| 2. Cap ramp-applied velocity | Step 3 |
| 3. Hold-Up auto-climb out of ledge grab | Step 7 |
| 4. Right pulls player over ledge | Step 7 |
| 5. Dropdown → ledge grab unless Down held | Step 7 |
| 6. Movement never breaks blocks + tests | Step 5 |
| 7. Standing jitter on sprout→solid | Step 4 |
| 8. Tile-generation-speed in game config | Step 1 |
| 9. Block-charge cooldown after placement | Step 6 |
| 10. Up + 2-high ledge → mini-jump parkour | Step 8 |
