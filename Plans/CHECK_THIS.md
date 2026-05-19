# Things to playtest / verify

Living list of things I should manually check on the next playtest pass. Each item
is a feel-check or interaction the test suite can't cover. Tick off as confirmed.

## Phase 1 — Combat core (Hitstun, Stun, Crush) — back-fill

- [ ] **Hitstun:** Take a single hit. For ~8 frames (≈270ms) Jump is locked but L/R
      movement still works. After the window expires you can jump again.
- [ ] **Hitstun anti-stunlock:** With one player holding L/R uniformly and the other
      hitting them in a 3-hit Slash combo, the held movement should NOT escape
      the hitstun extension — the combo lands. (Roadmap §1.2 explicit test.)
- [ ] **Stun:** Take a hard hit (Stab knockback, big fall). `CombatState.StunActive`
      should set; for ~18 frames movement is muted (~0.4× air accel, ~0.5× max
      air speed) and jumps/attacks gated off. Bleed cleanly into Falling on expire.
- [ ] **No double-jump refund through stun:** Get hit out of a mid-air double-jump.
      After stun expires `HasDoubleJumped` must still be `true` — no free second
      double-jump as a recovery.
- [ ] **Crush damage:** `Body.LastImpulseMagnitude` over `CrushThreshold` deals
      Health damage AND routes the impulse into `CombatState.LastHitImpulse` so
      the stun path can also fire. A stab-throw into a wall should sometimes
      stun AND crush in the same impact.

## Phase 2 — Hitbox 1.75× + stab carry-through — back-fill

- [ ] All Slash arcs (Slash1/2/3, AirSlash1/2, CrouchSlash) read ~1.75× their old
      reach. After playtest, expect to dial back to ~1.5× since "every swing hits"
      tends to feel less satisfying.
- [ ] Stab carry-through: a vertical Stab (mouse above player) should now boost
      vertical velocity to LungeSpeed, not just X. Diagonal stabs get diagonal
      carry. "Ensure-at-least" semantic — if velocity already exceeds along
      `_stabDir`, leave it alone.

## Phase 3 — Block-type selection + foam decay — back-fill

- [ ] Number keys 1/2/3/4 swap the active block type (Sand, Dirt, Stone, Foam).
      Both `HandleBuildInput` drag-place and `EruptionPlanner.ActiveType` /
      `MassBallPlanner.ActiveType` reflect the choice on the next placement.
- [ ] Foam tiles: solid-collidable while alive (you can stand / jump on them).
      Auto-break after ~4s. `FoamDecay` integration test in `FoamDecayTests.cs`
      already covers the timer; the playtest is that decay-while-standing-on-it
      drops the player cleanly into Falling.
- [ ] HUD readout for active block type is visible and updates immediately.

## Phase 4 — Air turn-around attacks + sticky air-facing

- [ ] In air, click on opposite side of facing → AirTurnSlash fires and Facing flips.
- [ ] In air, hold-drag (stab gesture) on opposite side → AirSpinStab fires and Facing flips.
- [ ] In air, click/drag on the **same** side as facing → plain AirSlash1 / Stab still fires (no Facing change).
- [ ] In air with no input, facing stays sticky (doesn't reset to last ground direction
      every frame even when you steer horizontally with arrow keys).
- [ ] Land on ground: next horizontal input updates facing normally.

## Phase 5 — Guard / GuardRetaliate

- [ ] Hold Shift (no L/R) on ground → GuardAction activates; speed slowed to ~0.5×;
      small light-steel-blue marker above the player.
- [ ] Hold Shift while pressing L/R → Guard does **not** activate (movement wins).
- [ ] Hold Shift in air → Guard activates (no movement-state penalty in air, just
      the action's air-modifier slowdown).
- [ ] Take a hit from the front while guarding with a *weak* attack (Slash1, dmg ≤ 1.0):
      damage **0**, no knockback, no hitstun, GuardCharged armed for ~24 frames.
- [ ] Take a hit from the front while guarding with a *strong* attack (Slash3, Stab):
      damage **0**, no knockback, but GuardCharged does **not** arm.
- [ ] Take a hit from behind (or above ±60° cone) while guarding → normal damage applies.
- [ ] During GuardCharged window, LMB → GuardRetaliate (cyan slash) fires; consumes
      the charge so a second click doesn't refire.
- [ ] In stun, Guard is gated off (StunActive → CheckPreConditions fails).
- [ ] **Deferred — not yet wired:** projectiles do not lose momentum on guard hitbox.
      Waits for the `Projectile` base class in Phase 8. Today bullets pass through
      Guard normally; that's the bug to ignore until then.
- [ ] **Deferred — not yet wired:** GuardCharged visual tint on the shield marker.
      Currently it's a single color regardless of state. Add when Draw can read
      `ab.Combat.GuardCharged` (small plumbing fix).

## Phase 6 — Charge-anywhere block eruption

- [ ] RMB press over empty space → charge ring starts at body center.
- [ ] RMB press over solid → charge ring starts at the cursor cell.
- [ ] Hold RMB and stay still → charge accumulates to gold (~2s) then dips orange-red.
- [ ] Hold RMB and *drag through a build-reachable empty area* → tiles get placed AND
      charge resets to zero each time a tile lands. Releasing here drops the charge.
- [ ] Hold RMB on empty / out-of-reach area → no build placements, charge accumulates.
- [ ] Release RMB above MinChargeToArm → eruption fires with the sample path
      collected during the charge.
- [ ] Release RMB below MinChargeToArm → no eruption; build-input path picks up
      naturally on the next RMB press.
- [ ] Block-type picker (1/2/3/4) → eruption uses the active material.

## Phase 7 — Destructive physics (no-substep variant)

Per the roadmap user-note, no substepping. Body still pauses at the broken
block's boundary for the current frame; the next frame's sweep continues into
the now-empty space with reduced normal velocity. This caps penetration at one
block / frame — accepted tradeoff.

Tuning knobs on `Body.Impact` (PlayerCharacter.cs:158):
- `ImpulseThreshold = 700f` (existing) — below this, no damage.
- `BreakThreshold   = 1100f` (new)    — above this AND a tile actually broke ⇒ bleed instead of zero normal velocity.
- `NormalRetainOnBreak = 0.6f`        — fraction of vn kept on break-through.

- [ ] Walking into a wall (max ~100 px/s × Mass 2.5 = impulse 250) → no damage,
      no break-through (impulse below ImpulseThreshold). Sanity check the
      no-regression case.
- [ ] Falling onto Sand from medium height → chips Sand normally (impulse over
      ImpulseThreshold but below BreakThreshold → stops dead, damages cells,
      maybe breaks them one at a time across frames).
- [ ] Getting stab-thrown into a Stone wall → impulse crosses BreakThreshold
      (~1100), at least one cell breaks, body continues with ~60% velocity into
      the next layer next frame. Expected feel: 3–8 blocks per clean throw,
      "up to a dozen" for chained stab→pulse combo throws (user spec).
- [ ] Falling from high up onto Stone → similar chained break.
- [ ] Body never visibly tunnels — should always stop at the block boundary the
      frame the break happened, then continue next frame. If you see "teleported
      past the wall" that's a bug.
- [ ] Crush damage from §1.4 still works — `LastImpulseMagnitude` is set BEFORE
      the break-through path runs, so the magnitude reflects the pre-bleed value.
- [ ] If a stab-throw with break-through still feels like it "stops too dead,"
      try lowering BreakThreshold to ~900 or raising NormalRetainOnBreak to ~0.7.
- [ ] Body's `Constraints` list: when break-through happens we now SKIP
      `UpdateSurfaceConstraint`. If a player slides along a wall, breaks one
      tile via stab-throw, and ends up still in contact with the wall above,
      the surface constraint stays from the previous frame's call — that should
      be correct, but verify that wall-slide doesn't lose its grip oddly.

## Phase 8 — Ranged attacks (complete: EnergyBall + Beam + Grenade + LobbedArea)

Bindings:
| Gesture                       | Action            | Notes |
|---|---|---|
| Shift + LMB **tap**           | EnergyBall        | Light-cyan piercing projectile toward cursor. |
| Shift + LMB **hold** (≥ 0.35s)| Beam              | Sustained magenta beam, ≤ 0.55s firing. Release early = no fire. |
| Shift + RMB **hold + release**| LobbedArea        | Ballistic lob, lands → mass-ball eruption + radial AOE. |
| F **press**                   | StickyGrenade     | Olive grenade arcs to cursor, sticks on contact, fuse 1.2s. |
| RMB (no Shift) hold           | BlockReady        | Existing — charge-anywhere block eruption. |
| LMB (no Shift) click / hold   | Slash / Stab      | Existing — Shift gates these off when held. |

**Implemented**
- `Projectile` base, `EnvironmentContext.Spawner`, `IChunkProvider` on Game1 (so LobbedArea can call `EruptionPlanner.Plan` at landing).
- `Game1.WorldGravityY` static — read by LobbedAreaAction's ballistic solver to match the world's gravity vector exactly.
- `StabAction.CheckPreConditions` now gates off Shift. Plain Stab + AirSpinStab both inherit this — Shift+swipe is reserved for Beam. (AirSpinStab requires Shift-released-by-then so it's only reachable by non-Shift backward-swipes in air, which is the intended trigger.)

**Playtest — energy ball**
- [ ] Hold Shift, **short tap** LMB → one light-cyan ball flies toward cursor, pierces 1-2 thin tiles. One tap = one ball.
- [ ] Ball stops dead in a 3-block-thick stack (its BreakThreshold = 80 is intentionally tight). If it pierces too much, raise BreakThreshold; if not enough, lower `Mass` or `ImpulseThreshold` on the ball's ImpactDamage.

**Playtest — beam (NEW)**
- [ ] Hold Shift+LMB. For ~0.35s a magenta charge dot grows at the player — no damage yet.
- [ ] Past the charge time, beam emits magenta segments along player→cursor up to ~220px. Tile damage is heavy (~½ TileMaxHP per frame); a Stone wall in the beam's path crumbles in 2-3 frames of overlap.
- [ ] Release LMB during charge phase (< 0.35s) → no beam fires. Click intent (≤ 6 frames) routes to EnergyBall instead → light-cyan ball appears at release. Holding 6 < frames < charge-time → release fires neither (cancel, no shot).
- [ ] After firing ends (release or MaxFiringTime ~0.55s), Recovery is set for 6 frames — can't immediately spam another Shift+LMB.
- [ ] Sweep the beam by moving cursor mid-fire — segments track the cursor each frame. Beam should "carve" through terrain.
- [ ] Stun during charge → Beam cancels (CheckConditions sees StunActive).
- [ ] **Known limitation**: Beam visual during firing uses `_lastBeamDir` cached from Update. If Draw runs on the SAME frame as the firing-phase transition with no Update having stored a direction yet, the beam draws toward `Vector2.UnitX` for one frame. Likely invisible at 30fps but flag if you see a single-frame flicker.

**Playtest — sticky grenade (REBOUND from Shift+RMB → F)**
- [ ] Press F (no modifiers) → olive grenade arcs ballistically toward cursor. Sticks on first contact (turns lime-green), 1.2s fuse, then radial explosion.
- [ ] Hard cap lifetime 6s — a grenade that rolls off-map without sticking still dies.
- [ ] Explosion damages BOTH tiles in ~3.5-tile radius AND entities (radial knockback).

**Playtest — lobbed area (NEW)**
- [ ] Hold Shift+RMB → goldenrod charge ring at player grows with charge time. Past saturation (1.8s) it dims (timing dip).
- [ ] Release Shift+RMB past MinChargeToFire (0.4s) → sienna ballistic ball launches toward cursor and arcs over.
- [ ] Lands at cursor → eruption mound spawns (uses MassBallPlanner with single zero-velocity sample = wide-base pile) AND radial AOE damages nearby tiles + entities.
- [ ] Ball uses `EruptionPlanner.ActiveType` at launch time (1/2/3/4 picker applies).
- [ ] Release before MinChargeToFire → no projectile, brief recovery only.
- [ ] Non-Shift RMB → still routes to BlockReadyAction. Shift+RMB → LobbedAreaAction only (higher priority).
- [ ] Trajectory accuracy: the ball should land *at* the cursor position chosen on release. The solver uses `Game1.WorldGravityY` and a fixed upward LaunchApexBoost (180 px/s). If the ball overshoots/undershoots in practice, tune LaunchApexBoost.
- [ ] Cursor BELOW the player → ball lobs downward (still ballistic). Cursor too high → solver discriminant goes negative; falls back to fixed t=0.8s. The ball may miss in that corner case — flag if it feels broken when aiming up at distant cliffs.

**Deferred — Phase 5 carry-over**
- Projectile-momentum-killed-by-Guard: still pending. Easy hook now that `Projectile` is a base type — in `CombatState.TryParry` (or via a new `OnIncoming` path on the Player) zero `projectile.Body.Velocity` when the source is a `Projectile`. Worth doing before any of these ranged attacks meet enemy-faction variants.

## Open caveat I noticed during Phase 6

- Build-input now runs *during* the charge state (so cancel-on-build works).
  This means a fast RMB drag near solids can flicker between "placing tiles" and
  "charging." Visually fine in theory — the ring blinks while tiles drop, then
  steadies once you stop building. If it feels noisy in practice, consider:
  (a) hiding the charge ring while `_recentBuildFrames < N`, or
  (b) requiring a small "no-build dwell" before charge even starts to count.
