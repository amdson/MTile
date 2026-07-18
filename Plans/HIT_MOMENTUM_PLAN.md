# Hit Momentum Plan — collision-based knockback

Goal: make hits feel *solid* — a light target pings off like a ping-pong ball
(strike speed + its own reflected speed), an equal-mass target bounces but slower
than the strike, and a much heavier target barely deflects (and the attacker
feels the recoil instead). The current model can't express any of these because
it ignores both the target's incoming velocity and the attacker's motion.

## 1. What we do today

An attack publishes a `Hitbox` carrying a fixed authored vector
(`Hitbox.KnockbackImpulse = dir * KnockbackMagnitude`, e.g. Slash1 200,
Slash3/Stab 380/1140). Resolution is a bare velocity add on the target:

- `Entity.OnHit`: `Body.Velocity += hit.KnockbackImpulse / Mass` (Entity.cs:55)
- `PlayerCharacter.OnHit`: same, scaled by the escalation percent
  (`* Combat.KnockbackScale`, PlayerCharacter.cs:129)
- Hitstun/stun thresholds key off the *authored* impulse magnitude
  (CombatState.OnHitRegistered; stun threshold 350).
- Attacker recoil is a separate opt-in channel: `RecoilScale` accumulates
  `-KnockbackImpulse * RecoilScale` per connecting cell/entity into
  CombatSystem's per-HitId 1-frame inbox (`PeekRecoil`). Only the stab's tile
  pogo uses it; entity hits give the attacker nothing.
- Only PulseAction manually adds `bodyVel` into its segment knockback; slashes
  and stab ignore attacker velocity entirely.

Why it feels mushy, concretely:

1. **Incoming velocity is ignored.** A ball flying at you at 300 px/s hit with
   a 200-impulse slash *keeps coming* at 100 px/s. A solid hit should reverse
   the approach. This is the single biggest "not solid" contributor.
2. **Attacker velocity is ignored.** A full-sprint slash and a standing slash
   impart identical knockback.
3. **Mass only divides.** The mass ratio never changes the *character* of the
   interaction (bounce vs. plough-through vs. attacker-bounces-off); it just
   scales one number.

## 2. The framework: each hit is a 1D partially-elastic collision

Model the strike as a virtual **striker body** colliding with the target along
the hit direction `n` (the authored knockback direction, normalized — keep it
authored per-move; this is the game-feel "launch angle", not geometry).

Striker state, derived at publish time:

- `strikeVel  v_s = attackerVel + n * StrikeSpeed`   — the move authors a
  *speed* (px/s), not an impulse; attacker motion feeds in for free.
- `strikeMass m_s = attackerMass * MoveMassScale`    — a heavy stab "weighs"
  more than a jab. MoveMassScale ~0.5–2, default 1.

Resolution (pure function, all along `n`; tangential velocity untouched):

```
u  = dot(v_s - v_t, n)                  // closing speed; if u <= 0 → floor path
mu = m_s * m_t / (m_s + m_t)            // reduced mass
J  = (1 + e) * mu * max(u, 0)           // scalar impulse, e = restitution
target:   v_t += (J / m_t) * n
attacker: v_a -= (J / m_s) * n * FollowThrough   // via the existing recoil inbox
```

`e` (restitution, ~0.2–0.8) is per-move (later per-material for tiles). The
limits give exactly the requested feel with no special cases:

- **m_t ≪ m_s** (balloon, 0.5 vs player 2.5): `mu → m_t`, so
  `Δv_t ≈ (1+e)·u` — the ball leaves at strike speed *plus* its reflected
  incoming speed. Ping-pong. Attacker barely notices.
- **m_t ≈ m_s** (mirror player): `mu = m/2`, `Δv_t = (1+e)·u/2` — target
  bounces away, but at roughly half the closing speed. Both parties feel it.
- **m_t ≫ m_s** (boulder/brute): `mu → m_s`, `Δv_t ≈ (1+e)·u·(m_s/m_t)` — the
  falling boulder keeps falling; the *attacker* takes `(1+e)·u·(m_t-share)`
  recoil and bounces off. The stab's tile pogo becomes this same formula with
  `m_t = ∞`.

Game-feel layers stay OUTSIDE the collision core (tunable without breaking the
physics story):

- **Launch floor**: `Δv_t = max(Δv_t, MinLaunch)` per move so a landed hit
  always visibly moves the target even when `u` is small (target retreating).
- **Escalation** (Phase 5 percent): scale `StrikeSpeed` by `KnockbackScale`
  *before* the collision, not the resulting impulse — stun thresholds and
  recoil then follow consistently.
- **Hitstun/stun**: key off closing speed `u` (attack strength as experienced),
  not the authored number and not `J` (which would let heavy targets shrug off
  stun *and* light targets get over-stunned). Retune the constants once.

## 3. Where it lives in the code

1. **`Physics/HitResolver.cs`** (new): static pure
   `Resolve(in HitboxStrike, mass, velocity) -> (dvTarget, dvAttacker, u, J)`.
   No state → determinism- and snapshot-neutral. Unit-test the three mass-ratio
   regimes directly.
2. **`Hitbox`** gains `StrikeVelocity` (Vector2), `StrikeMass`, `Restitution`,
   `MinLaunch`; `KnockbackImpulse` shrinks to the *direction* role (`n`) or is
   replaced by `StrikeDir`. Publish sites (SlashLikeAction, StabAction, Pulse,
   projectiles, beam) switch from `dir * KnockbackMagnitude` to
   `dir`, `StrikeSpeed`, `MoveMassScale`. Pulse deletes its hand-rolled
   `bodyVel` add — the framework now does it.
3. **Resolution moves into `CombatSystem.Apply`** (or a helper it calls):
   CombatSystem already sits between hitbox and target and owns the recoil
   inbox. It computes `J` once, passes the resolved impulse into
   `OnHit(hit, hurtbox, in HitResult)`, and accumulates the *actual*
   `-J·n·FollowThrough` into `_recoilByHitId` — replacing the fixed
   `-KnockbackImpulse * RecoilScale` guess. Needs target mass+velocity
   visible at that layer: extend `IHittable` with `Body`/mass accessors (both
   implementors already have them).
4. **`Entity.OnHit` / `PlayerCharacter.OnHit`** shrink to: apply `dvTarget`,
   then their own bookkeeping (health / percent / hitstun via `u`). The
   duplicated `impulse / Mass` line dies.
5. **Tiles**: unchanged initially. Later, tile recoil (`RecoilBreakProtected`
   etc.) re-derives from the same resolver with `m_t = ∞` and per-material `e`
   so stab-pogo strength follows attacker speed.

## 4. Migration & tuning

- Rough mapping to preserve current feel as a starting point:
  `StrikeSpeed ≈ KnockbackMagnitude / ((1+e)·mu/m_t)` for the typical target;
  e.g. vs. a 1.0-mass entity with player m_s=2.5, e=0.5: Slash1 200 → ~190
  px/s strike speed. Expect a retune pass anyway — that's the point.
- CombatState thresholds (`StunImpulseThreshold` 350, hitstun-per-impulse,
  parry charge gate) now read `u`; renumber against the same reference moves
  listed in CombatState's comments.
- Tests to update/extend: `CombatFeelTests`, `AttackRecoilTests`,
  `TumbleTechTests`, `CombatHitstunTests`; add `HitResolverTests` for the
  three regimes + `u ≤ 0` floor path + determinism (pure function, but assert
  attacker-velocity feed-in uses frame-N velocity only).
- Snapshot impact: none new — resolver is pure; `_recoilByHitId` note in
  CombatSystem (currently "always empty") becomes real and needs the promised
  snapshot treatment once entity recoil ships.

## 5. Order of work

Update: the hitbox is dual-mode (`KnockbackMode.Impulse` default / `Collision`),
so AoE/field hits (Pulse, beam) stay impulse-based permanently and moves convert
individually. Resolution stayed inside `OnHit` (which now RETURNS the delivered
impulse to CombatSystem for the recoil inbox) instead of hoisting target
mass/velocity access up into CombatSystem — same centralization via
`HitResolver.Resolve`, less interface surface.

1. ~~`HitResolver` + unit tests (pure math, no game changes).~~ **Done** —
   `Physics/HitResolver.cs`, `MTile.Tests/Physics/HitResolverTests.cs`.
2. ~~`Hitbox` fields + thread through CombatSystem → OnHit; keep old numbers via
   the mapping so behavior shifts minimally.~~ **Done** — all moves still
   publish Impulse-mode hitboxes; behavior verified unchanged (full suite green
   minus 2 pre-existing unrelated failures: SproutPush, CourseCorridor).
3. ~~Tiles-as-infinite-mass unification; per-material restitution; convert the
   stab.~~ **Done** (folded §5's steps 4–6 for the stab):
   - `MaterialStrength.Restitution` (stone 0.70 rings / dirt 0.35 / sand 0.05 /
     foam 0.15), tunable via material_strengths.json.
   - `HitResolver.TileRecoil`: attacker Δv = (1+e)·approach·RecoilScale opposite
     the strike dir, floored at `Hitbox.MinRecoilSpeed`, zero when retreating.
     NOT self-limiting across frames (the swing speed re-adds every re-publish) —
     CombatSystem latches the bounce once per attack via an `EntityId.None`
     sentinel in the dedupe set, which snapshots for free.
   - CombatSystem resolves collision-mode tile recoil ONCE per hitbox per frame
     against the bounciest eligible surface (a wall face ≠ N collisions); the
     break-protected / hardness gates still decide eligibility. Impulse-mode
     recoil keeps the legacy per-cell accumulation.
   - The recoil inbox is now snapshotted (`CaptureRecoil`/`RestoreRecoil` +
     `SimSnapshot.Recoil`) — it's a live frame-N→N+1 message.
   - StabAction = first Collision-mode move: StrikeSpeed 950 + attacker
     velocity, StrikeMass = ctx.Mass (new EnvironmentContext.Mass), e 0.5,
     MinLaunch 120, RecoilScale 0.25, MinRecoilSpeed 380 (standing stone pogo ≈
     the old 380–400 baseline; dives stack their speed on top).
     KnockbackImpulse stays authored as the direction hint for parry cone /
     bullet deflect / early-out recoil echo.
   - The two skipped wall-stab pogo integration tests are re-enabled and green.

Remaining: convert the slashes (and decide which moves stay impulse — Pulse and
the beam do), then the per-move retune pass with thresholds already on `u`.
3. Flip hitstun/stun/parry thresholds to `u`; retune constants.
4. Enable attacker recoil (FollowThrough) on entity hits; retune stab pogo.
5. Retire `KnockbackImpulse`; per-move StrikeSpeed/e/MassScale tuning pass.
6. (Later) tiles-as-infinite-mass unification; per-material restitution.
