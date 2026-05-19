# Combat & Content Roadmap

A planning pass over the open-ended ideas in `todo.txt`. The goal here is not to commit to every item but to:

1. Pin down what each idea actually *is* mechanically — turning "guard state" into "what input, what window, what consequence, what FSM seat."
2. Group items so changes that share machinery land together (e.g. *crush damage* + *body-on-tile destructive momentum* + *stun on hard hits* all read constraint/impulse data from `PhysicsWorld` — one read site, three features).
3. Flag the items I'd reconsider or defer.
4. Sequence the work into something playable at every step.

The existing roadmaps cover orthogonal areas:
- `DYNAMIC_PHYSICS_ROADMAP.md` — moving surfaces, growing blocks, tile destruction infra.
- `toasty-enchanting-volcano.md` (plan mode) — guided-state refactor for vault/ledge moves.

Where the items below depend on either, I call it out.

---

## 1. The combat-condition core

Six items rely on a shared piece of plumbing that doesn't exist yet: **"how hard was the player just hit, and what does that *cost* them mechanically?"** Once that exists, several follow-up features fall out cheaply. So this group lands first.

### 1.1 `CombatState` — a sibling of `ConditionState`

`PlayerAbilityState.Condition` ([Character/ConditionState.cs](Character/ConditionState.cs)) already holds combo-readiness flags (`Slash2Ready`, `RecoveryActive`, …) keyed by expire-frame. Combat *defensive* condition wants the same shape but distinct data, so it's worth a separate struct rather than ballooning `ConditionState`:

```csharp
public class CombatState {
    public bool   StunActive;        public int StunExpireFrame;
    public bool   HitstunActive;     public int HitstunExpireFrame;   // short post-hit no-jump window
    public bool   GuardActive;                                        // shift held + guard intent
    public bool   GuardCharged;      public int GuardChargedExpireFrame;
    public float  LastHitImpulse;    public int LastHitFrame;         // for stun threshold lookups
    public Vector2 LastHitDirection;                                  // for guard-angle filter
    public void Tick(int frame) { /* expire all */ }
}
```

Owned by `PlayerCharacter` next to `_abilities.Condition`. `OnHit` writes `LastHitImpulse / LastHitDirection / LastHitFrame` directly; the action FSM reads it via `EnvironmentContext`.

### 1.2 Hitstun — the "no full stun-lock" guarantee (item 20, item 22)

Goal from `todo.txt`: "Don't fully allow moves to interrupt jump, but prevent player from initiating jump immediately after getting hit so [combos are possible]." Plus the explicit anti-feature: "full stun-lock loops should be impossible."

Mechanism:

- Every `PlayerCharacter.OnHit` that does non-trivial damage sets `HitstunActive` for `N` frames (start with `N = 8`, ~270ms at 30fps).
- `JumpingState.CheckPreConditions` adds `&& !ctx.Combat.HitstunActive`.
- *Movement* itself stays free — running and air-control work during hitstun. Only jump (the recovery option that resets vertical position cheaply) is locked.
- Hitstun does NOT cap by impulse — every hit locks jump. This is the disadvantage state the user wants without taking control away.
- A *second* hit during hitstun extends the window by `M < N` (start `M = 4`), not by a full `N` — so a 3-hit combo extends the lock by `N + 2M = 16` frames total, not `3N = 24`. Diminishing returns make true stun-locks impossible.

This is the smallest possible change to enable combos and it composes with everything below.

[USER NOTE: a good test for hitstun is that if one player is hitting another with the slash combo, and the attacked player is holding movement keys uniformly, they should not escape the hits] 

### 1.3 Stun — the "you went flying" state (items 19, 24)

Per `todo.txt`: "stunned state for when the player is hit hard… mild control over movement, but can't jump or attack immediately." And: "very hard by a stab… probably shouldn't be able to double jump."

Mechanism:

- New `MovementState`: `StunnedState`. Active priority 30, passive priority 30. Precondition: `ctx.Combat.StunActive` (set by `OnHit` when `LastHitImpulse / Mass > StunThreshold`).
- During stun: gravity normal, air-drag elevated (~1.5×), horizontal accel cut (~0.4× of `MaxAirAccel`), `MaxAirSpeed` muted. Player can nudge but not redirect.
- Action FSM gates: `JumpingState`, `DoubleJumpingState`, `WallJumpingState`, all `SlashLikeAction`/`StabAction` preconditions add `&& !ctx.Combat.StunActive`. (Guard *can* fire during stun — see 1.5 — that's the recovery beat.)
- Stun expires after `StunDurationFrames` (start 18, ~600ms). On expire → falls into `FallingState` cleanly. `HasDoubleJumped` is *not* reset by stun-exit so a player who got hit out of a double-jump doesn't suddenly gain another.

**Important** anti-feature: I'd resist anything that visually ragdolls (rotating sprite, spinning body). It's expressive but reads as "I've lost control" too strongly. Current sprite + a tinted hit-flash is enough.

### 1.4 Crush damage (item 7)

Goal: bodies wedged between surfaces take damage. `ImpactDamage` ([Physics/ImpactDamage.cs](Physics/ImpactDamage.cs)) already converts impact impulse → damage for *tiles*; the symmetric "damage applied to the body by the world" path doesn't exist.

Mechanism:

- `PhysicsBody` gains a `LastImpulseMagnitude` field, written by `PhysicsWorld` each time it resolves a constraint that brakes the body.
- `PlayerCharacter.Update` checks `Body.LastImpulseMagnitude` post-step; over a `CrushThreshold` it deals `Health -= …` and writes the impulse into `CombatState.LastHitImpulse` (so the stun-threshold path fires too).
- True *converging-surface* crush (two surfaces closing on the player) is deferred until moving surfaces ship — flagged in `DYNAMIC_PHYSICS_ROADMAP.md` §6.10. For now, "crush" is just "your impulse magnitude this frame was very high," which catches the cases that matter: smashed into a wall by a Stab knockback, slammed into the ground from a fall.
- *Don't* couple to `ImpactDamage` — that's tile-side. Crush is body-side, distinct knob.

### 1.5 Guard / GuardRetaliate (item 1)

The most under-specified item. The user wrote: "Slows character… both a Guard action and Guard movement state. Activated by shift. If hit by an attack from the correct angle, with damage below a threshold, becomes charged, allows a fast GuardRetaliate attack. Kills momentum of projectiles on contact."

Proposed mechanics — **call these out for confirmation before implementation**:

| Aspect | Proposal |
|---|---|
| **Activation** | Hold Shift. Active only while held. Press-edge enters Guard immediately; release-edge exits. |
| **Type** | Both: a `GuardAction` (publishes a small reflective hitbox in front of player, gates damage) and a `GuardMovementState` that slows the player. Two FSM seats, one input gate — mirrors how `BlockReadyAction`/`BlockEruptionAction` split. |
| **Posture** | Slow speed (~0.5× walk, ~0.8× air), gravity normal, no jump-cancel. |
| **Coverage angle** | Front 120° cone (60° each side of facing). Hits in cone → blocked; outside → normal damage. |
| **Charge condition** | Hit lands in cone AND `hit.Damage <= GuardCharge MaxDamage` (small threshold so only weak hits charge). Sets `CombatState.GuardCharged` for `N` frames (start 24). |
| **Charge expiry** | Release Shift OR window expires OR `GuardRetaliate` fires. |
| **Retaliate** | LMB-press during `GuardCharged` → `GuardRetaliateAction` (a fast forward slash, ~0.10s, high knockback, brief recovery). Higher passive priority than `GroundSlash1`. |
| **Projectile interaction** | A bullet hitting `GuardAction`'s hitbox sets bullet velocity to zero (matches the user's "kills momentum"). Deflection (Player faction-flip) is reserved for *slashes*; Guard is the no-effort version that just stops bullets dead. |

**Open questions**, can't decide alone:
- Should Guard prevent *all* damage in cone (parry), or *reduce* it (block)? I lean parry — block-with-residual-damage feels muddy in a game with a small health pool.
- Does Guard work in air? Cleanest answer: yes, with the same slow-fall effect a charged BlockReady has. Air-guard skips the movement-state seat and only runs the action.
- Should `GuardRetaliate` consume the charge or just *use* it (refire-able while charged)? Consume.

### 1.6 Turn-around air attack (item 12)

Goal: a click on the opposite side of facing in air → a special attack. Two flavors:
- Click → fast narrow long-range turn-around slash.
- Click-and-drag (i.e. Stab gesture) → spin-stab, longer range, more power.

Face direction in air *only* changes via turn-around attacks.

Mechanism:

- `PlayerCharacter.Update` currently writes `_abilities.Facing` from `ctx.Intent.CurrentHorizontal`. Gate that to ground-only (`if (grounded || groundedLastFrame) …`). In air, `Facing` is sticky.
- Add `AirTurnSlash : SlashLikeAction` with precondition: in air, click-edge, mouse on opposite side of `Facing` (the existing hemisphere-clamp logic in `ComputeSlashDir` becomes the trigger condition instead of a clamp). Long reach (`ArcRadiusScale ~ 1.4`), narrow sweep (~60°), short duration, no combo flag.
- Add `AirSpinStab : StabAction` analog with same trigger — opposite-side direction at release of a held click. Detection lives in `InputParser` since that's where Stab swipe vs Click is already disambiguated.
- On successful turn-around attack Enter: flip `_abilities.Facing` to the new direction. Other in-air states never write Facing.

**Subtle issue**: the existing `SlashLikeAction.ComputeSlashDir` hemisphere-clamps backward clicks to perpendicular. That has to change for AirTurnSlash specifically — it *needs* the backward direction. Easiest is a virtual `protected virtual bool AllowBackward => false;` on the base.

### 1.7 Hitbox size + stab carry-through (items 9, 10)

Cheap, mostly tuning:

- Hitbox expansion 1.75× — `SlashLikeAction.BaseArcRadius` and the rectangle dimensions in `StabAction.PrimaryPoly`/`BlockPoly`. Build once, playtest, expect to dial back to ~1.5× since rewards feel less satisfying when *every* swing hits.
- Stab carry-through: today `StabAction.ApplyActionForces` only writes `Velocity.X`. Generalize to `Velocity = max-magnitude-in-stab-direction`. The "ensure-at-least" semantic stays — project velocity onto `_stabDir`, raise to `LungeSpeed` if below. Vertical stabs now get vertical carry, diagonal stabs get diagonal.

### 1.8 Sequencing

Order within this section, each step playable:

1. **`CombatState` struct + hitstun (1.2)** — single new field, single conditional on `JumpingState`. Tiny diff, huge feel impact for multi-hit fights.
2. **Stun (1.3)** — adds `StunnedState`, gates jumps/attacks. Validate that single hits feel right (probably not stun) and that stab+knockback into walls *does* stun.
3. **Crush damage (1.4)** — needs `PhysicsBody.LastImpulseMagnitude` plumbed from `PhysicsWorld`. Read it in `PlayerCharacter.Update`, dispatch to `OnHit`. Single read site.
4. **Hitbox expansion + stab carry-through (1.7)** — pure tuning, do alongside above to recalibrate stun thresholds against the new feel.
5. **Turn-around air attacks (1.6)** — depends on Facing-becomes-sticky-in-air refactor; orthogonal to stun work.
6. **Guard / GuardRetaliate (1.5)** — biggest single addition, lands once stun/hitstun are stable so the "Guard is the escape from stun" beat actually has something to escape from.

---

## 2. Destructive physics tuning (item 8)

`todo.txt`: "the player thrown hard enough into rocks should smash through dozens of blocks, not just break one or two. … the player shouldn't break most blocks just by running into them."

Today `ImpactDamage` fires once per collision impulse site and `PhysicsWorld` zeroes the body's normal velocity on contact ([Physics/PhysicsWorld.cs](Physics/PhysicsWorld.cs) `ResolveChunkCollisionsSwept`). So even when a body has enough impulse to shatter a tile, it stops dead — leaving exactly one broken block.

The fix is **let the body continue when it broke through**. Three changes:

1. **Conditional contact persistence.** After `TryApplyImpactDamage` deletes the tile(s), check if the contact face is now empty. If yes, *don't* zero normal velocity. Instead bleed off a percentage (start with 40%) representing energy spent breaking the block, and re-sweep against the new (smaller) tile set for the remainder of the frame's displacement.
2. **Impulse threshold separation.** Currently one threshold (`ImpulseThreshold = 700` for player). Split into:
   - `ChipThreshold` — below this, no damage; above, single-tile damage but body stops (current behavior).
   - `BreakThreshold` — above this, full tile damage + body continues with bleed-off.
   - The "running into rocks" use case sits between Chip and Break: running max speed (~100 px/s · mass 2.5 = impulse 250) is below Chip; a stab-knockback throw (~500 px/s · 2.5 = 1250) crosses both.
3. **Per-tile-type breakage.** Stone takes more impulse than Dirt. Already exists implicitly via `TileMaxHP`. The split above just ensures the impulse-per-frame is enough to *cross* the tile's HP, not just chip it.

Sub-stepping the resolver to handle multi-block penetration in one frame is the gnarly bit. The current `ResolveChunkCollisionsSwept` already iterates broad-phase tile cells; the new path is "if broke through, advance body by `remainingDt` and recurse." Bounded depth (~3) to prevent pathological runaway. [USER NOTE: DONT SUBSTEP. substepping is the most correct way to handle, but very complicated. simply don't fully stop player when they break through stuff and pause them at block boundary for a frame. (this caps velocity to one block per frame, but is acceptable.)]

**Big "this is risky" caveat**: the existing physics test suite (`MTile.Tests/PhysicsTests.cs`, etc.) hard-codes "body crashes into solid → velocity becomes zero." Several tests will need to learn the new behavior. Tests like `BodyAtRest_OnTile_DoesNotChip` should still pass; `HighSpeedDive_BreaksOneStone` becomes `HighSpeedDive_BreaksThreeStones` or similar.

Order: ships after section 1's stun/crush work because both touch the same `PhysicsWorld` impulse pipeline — better to land one full impulse-data refactor than two.

---

## 3. Block-eruption charge-anywhere (item 6)

`todo.txt`: "BlockEruption can charge when player simply holds down right click anywhere. Instead of tracking whether mouse is over a filled tile, track whether player is actively producing blocks."

Today `BlockReadyAction.CheckPreConditions` requires `IsCursorInSolid`. The redesign:

- **Trigger:** RMB press-edge, regardless of cursor location.
- **Charging vs Building disambiguation:** The "actively producing blocks" check translates to: *is the player's drag currently materializing tiles?* That's a property of `HandleBuildInput` in [Game1.cs](Game1.cs), not of the cursor. Concrete signal: track a `_lastTilePlacedFrame` counter; if it hasn't ticked in the last ~10 frames, the player isn't building right now.
- **State diagram:**
  - RMB press-edge → enter `BlockReadyAction`. Start charging immediately.
  - If `HandleBuildInput` places a tile during charge → cancel charge (reset `_chargeTime = 0`), drop back to building.
  - If charge passes `MinChargeToArm` and RMB is still held → "armed" state where the visual is unmistakable (full-radius ring at the cursor or at body-center).
  - On RMB release → if armed, hand off to `BlockEruptionAction` (same as today). If not armed, charge is discarded.
- **Origin point:** Currently the last solid cell visited. New behavior: at release, if cursor is over a solid cell, use it; otherwise use the player's body center. Both are concrete defaults that the player can target deliberately.

**Reconsidered intent**: the user's framing "instead of tracking whether mouse is over a filled tile" reads as "don't gate charge on cursor location." Agreed. But charge should still *cancel* if the player starts placing tiles — otherwise every build session also charges an eruption, which floods the move into normal play. The `_lastTilePlacedFrame` heuristic captures this cleanly.

Order: independent of section 1. Cheap. Could ship at any point.

---

## 4. Ranged attacks (items 13, 14, 15, 16)

Four ranged options on `todo.txt`. They have enough mechanism in common (publishing hitboxes from a non-player source, traveling in world space) to share infrastructure but enough variation that they shouldn't be a single class.

### Shared infrastructure

A new `Entities/Projectile.cs` base — abstract over `BulletProjectile` ([Entities/BulletProjectile.cs](Entities/BulletProjectile.cs)) which is *already* exactly the shape we want for player-spawned projectiles:

```csharp
public abstract class Projectile : Entity {
    protected float Lifetime;
    protected float DamagePerFrame;
    protected Faction OwnerFaction = Faction.Player;
    // virtual OnHit (for tiles? entities? walls?)
    // virtual OnExpire (decay vs explode vs splash)
}
```

`BulletProjectile` becomes one subclass; the four ranged moves below add more.

### 4.1 Energy ball (item 13)

Shift+LMB-tap → fires `EnergyBall` toward cursor.

- `Projectile` subclass. Constant velocity ~500 px/s. Lifetime ~1.2s. Small hitbox. Pierces 1–2 tiles before dying (uses §2 destructive-physics machinery, which fits naturally).
- Faction `Player`, deflectable by enemies if they grow Guard equivalents later.
- Spawned by a new `EnergyBallAction : ActionState`, precondition `Input.Shift && Click intent (short tap)`.

### 4.2 Particle beam (item 14)

Shift+LMB hold-swipe → sustained particle beam, breaks most blocks.

- *Not* a projectile entity — a per-frame raycast/shape extending from player toward cursor, publishing a long thin hitbox.
- High DamagePerFrame (chunky), high `BlockReach`. Tile-shockwave path from `StabAction` is the right template.
- Charge model: same hold-and-release timing as Stab. Beam fires during the active window, not on release. Holds for ~0.5s.
- `BeamAction : ActionState`, precondition `Input.Shift && stab-shaped intent`.

**Open question**: does Beam consume a meter, or is it cooldown-gated? `todo.txt` doesn't say. [USER NOTE: Beam should require some sustained charge time, and essentially just fail when interrupted early, so it won't be spammable.]

### 4.3 Lobbed area attack (item 15)

Shift+RMB → lobbed projectile that lands and spawns an overspilling radius of blocks plus area damage. Charging scales block count.

This is essentially **`BlockEruptionAction` spawned at a remote location**. The mass-ball planner ([Character/MassBallPlanner.cs](Character/MassBallPlanner.cs)) already takes `(origin, samples, budget)` — for the lobbed case, the "samples" are just `[ (landingPos, Vector2.Zero) ]` (single sample, near-zero velocity, gives the wide-base behavior the mass-ball planner already produces for a stationary release).

Plan:

- `LobbedAreaAction : ActionState` charges like `BlockReadyAction`. Charge time → budget.
- On release: spawn a `LobbedBallProjectile` (new `Projectile` subclass) with ballistic velocity computed from `(playerPos, cursorPos)` and a hardcoded arc apex.
- On landing: call `MassBallPlanner.Plan(chunks, landingPos, [single sample], budget)` PLUS publish a one-shot wide area-damage hitbox.

This is the most expressive of the four ranged moves and the cheapest to implement (most machinery exists).

### 4.4 Explosive (item 16)

Lobbed grenade or land mine. Both fit `Projectile` + timer fuse.

- `GrenadeProjectile` — gravity-affected, bounces, explodes after `FuseSeconds = 1.5`.
- `LandmineProjectile` — sticks on landing, explodes on entity touch or after a long fuse.
- Explosion = one-frame large-radius hitbox + radial knockback + several tile-damage points sprinkled in the radius (reuse `MassBallPlanner` deposit semantics in *destructive* mode — set a few cells to non-solid).

**Reconsidered**: I'd suggest shipping only **one** of grenade vs mine in v1. They're mechanically too similar to justify both. The user can pick after playing it; the other slot is free for something more differentiated later.

### Order

Section 4 is *long* but each item is mostly additive. Suggested order:
1. `Projectile` base + refactor `BulletProjectile` onto it (no behavior change).
2. Energy ball (4.1) — proves the player-projectile-spawn path.
3. Lobbed area (4.3) — proves the eruption-remote-spawn path; visually flashy enough to motivate the work.
4. Beam (4.2) — needs §2's destructive physics tuning to feel right.
5. Grenade (4.4).

---

## 5. Terrain features (items 17, 18)

### 5.1 Lava (item 17)

Fast-spreading terrain hazard. `todo.txt` asks how to allow lava placement and proposes "treat as another block option" — combine with item 18.

Mechanism:

- New `TileType.Lava`. Solid=false, but writes a hazard hitbox into `HitboxWorld` each frame for any cell containing it.
- Spreading model — cellular automaton, simplest variant: each lava cell with probability `p_spread` per frame samples a 4-neighbor; if the neighbor is empty *and* there's a solid cell below the neighbor (or it's at the same height as the source), the neighbor becomes lava. Cellular-automaton ticks are coarse-grained — once every 4 game frames — so spread is visible, not flickering.
- Cooling — lava cells optionally cool to `TileType.Stone` after `N` seconds. Off by default; turn on as a knob later.
- Limits — keep total lava cell count bounded (~512?) so spread doesn't tank performance. Once cap is reached, new lava spread stalls.

This is a big new system. **I'd defer it to after sections 1–4** since hazard-spreading mechanics interact deeply with the destructive-physics tuning from §2 — better to ship lava when the body-vs-terrain dynamics are stable.

### 5.2 Block-type selection (item 18)

User picks active block via number keys: sand, dirt, stone, foam.

- `GameConfig.ActiveBlockType` (set by 1/2/3/4 keys in `Game1.HandleInput`). Read by `HandleBuildInput` and `BlockEruptionAction.Enter` to set the type each new sprout fires with.
- **Foam** is the new mechanic. Foam tiles decay back to empty after `FoamLifetimeSeconds` (start 4s). Needs a sparse "timed tile" list since iterating the whole chunk map for decay is wasteful — when a foam tile is created, push `(chunkCoord, expireFrame)` onto a heap; each frame pop expired entries and call `chunks.DestroyTile`.
- Foam doesn't damage tiles below it on impact and is cheap to break (`TileMaxHP ~ 0.5`).
- **Sand vs Dirt vs Stone**: already exist as concepts in `TileType` — just expose them through the picker.

Lava (5.1) plugs into the same picker — `5` = lava — once shipped.

Order: 5.2 is short and unblocks both lava (5.1) and ranged-area-attack (4.3 wants a tile-type knob too). Ship 5.2 early.

---

## 6. Movement: items 35–40

The "vault one-block only, two-block adds a hop" / "ledge-grab rests at stable height" / "down-arrow drops into ledge-grab" items overlap heavily with the in-flight plan in `toasty-enchanting-volcano.md` (guided-state refactor).

Brief reading:

- **35 — Vault for one-block, hop-vault for two-block.** Once `ParkourState` becomes a `GuidedState` per that plan, `TwoBlockVaultState` is just another subclass with a different goal-pos/goal-vel pair (extra vertical velocity at start). Defer until the guided-state base lands.
- **36 — Require direction held into vault.** Already in the plan's `IntentHeld` design.
- **37, 38 — Auto-crouch + stable height under ledge.** This is `LedgeDropState` from the plan + a new `LowClearanceCrouchedState` whose precondition is "tile directly above body at clearance < N." Stable height is a PD-tracked rest position.
- **39 — Ledge grab to stable height below lip.** Already partially the case via `LedgeGrabState`; just retune the spring rest position.
- **40 — Down arrow on edge → ledge grab.** Listed as `LedgeDropState` in the plan but the trigger condition is exactly this.

Order: these all fall out of the toasty-enchanting-volcano plan. **No additional roadmap needed** — they ship in that plan's phases 4–5 (LedgePull → LedgeDrop conversion).

Item 43's "TODO later" list (roll, dodge, overcrop, ceiling-sweep) is genuinely later — all of them are guided-state subclasses, free once the base exists.

---

## 7. Hygiene (item 11)

`.gitignore` audit. Cheap. Should land early so subsequent commits stop touching build artifacts. 

Check `.gitignore` against the dirty files in `git status` — currently `MTile.Tests/bin/*`, `MTile.Tests/obj/*`, `Content/obj/*`, `obj/Debug/*` are tracked-modified. Add appropriate ignore globs and run a one-shot `git rm --cached` on the offenders.

---

## 8. Summary: phasing

A reading order that ships playable game at every step and doesn't reshuffle existing tests too aggressively:

| Phase | Items | Why first |
|---|---|---|
| **0. Hygiene** | gitignore (item 11) | Stops every commit from carrying noise. |
| **1. Combat core** | Hitstun (1.2), Stun (1.3), Crush (1.4) | Smallest plumbing, enables combos and gives every later attack/move a place to land its impulse. |
| **2. Tuning pass** | Hitbox expansion + stab carry-through (1.7) | Recalibrates feel against the new stun thresholds. |
| **3. Block-type selection** | Item 18 | Tiny, unblocks several follow-up features (ranged area, lava). |
| **4. Air turn-around attacks** | Item 12 | Orthogonal, satisfying to play, low risk. |
| **5. Guard / GuardRetaliate** | Item 1 | Once Stun/Hitstun exist, Guard has stakes. |
| **6. Charge-anywhere block eruption** | Item 6 | Independent, cheap, big QOL. |
| **7. Destructive physics** | Item 8 | Heavy tests-rewrite; ship after combat feel is stable so the test rewrite isn't fighting two moving targets. |
| **8. Ranged attacks** | Items 13, 15 (then 14, 16) | Several share machinery; lobbed area is the flashy one. Depends on §7 for beam feel. |
| **9. Movement guided-state refactor** | toasty-enchanting-volcano plan (items 35–40, 43–44) | Tracked in the existing plan; this roadmap doesn't duplicate it. |
| **10. Lava** | Item 17 | Biggest new system; lands after destructive physics + block selection make it expressive. |

Items I'd **drop or defer indefinitely**:
- Both grenade *and* mine — ship one, see if the other is missed (item 16).
- Full ragdoll visuals on stun — pushes the feel toward "I lost control" too hard (item 24 sub-mechanic).
- A separate "GuardWindow" combat-condition (item 23 in ConditionState) — current code has it but nothing reads it; remove once Guard ships under the new `CombatState` struct.

---

## 9. Open questions for the user

Before any of the medium-or-larger items above ship, I'd like a direct read on:

1. **Guard — parry or block?** Parry (zero damage in cone) is my default. Block (reduced damage) is muddier in a small-health game. (Go with parry)
2. **Guard in air?** I'd ship yes-with-action-only; user might want grounded-only. (yes, allow guard in air. note that guard should slow the player, and that guard should not activate when player is holding down left or right)
3. **Stun ragdoll visuals?** Default no; spinning sprite is dramatic but reads worse than a hit-flash + brief slow. (no)
4. **Destructive physics: how brutal?** A "stab-thrown body smashes through 5 stones" feel is *very* destructive. Tune target: 3–8 blocks for a clean hit? Up to a dozen for a chained stab→pulse combo throw? (your intuition seems right. player thrown by slashes, or simply walking into wall, shouldn't break anything)
5. **Grenade or mine — pick one?** Or keep both? (let's say sticky grenades, no mines)
6. **Lava cooling — on or off?** I lean off (permanent hazard), but a 30-second cool is reasonable. (off, but allow lava blocks to be overwritten by block placements)
7. **Foam — does it solid-collide with the player while alive?** Default yes (so jumping on foam is real); easy to flip to "passes through player" if it feels gimmicky. (yes, allow jumping on foam)
