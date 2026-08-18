# Hit airlock: getting hit as involuntary eviction

Design sketch, 2026-08-14. Companion to the recovery-airlock redesign (Recovery as the single
transition state, CommitProfile pricing, inherited-bid eviction lookahead — see
`RecoveryTransitionTests`). Status: **IMPLEMENTED 2026-08-14** (`HitEvictionTests` pins it),
with the open questions resolved: universal flinch (light hits evict; armor is the exception
tool), guard uses tail-window entry, armor scales knockback (×0.3, no binary no-sell yet), and
armored hits add full percent. Implementation deviates from the sketch in one way: hitstun/stun
are NOT stamped onto `Condition.Recovery*` — `RecoveryIndex()` DERIVES the countdown as
max(self stamp, `HitDisadvantageFrames()`), which gets the max-merge for free, needs no taint
bit (the any-index combo bypass just checks hit disadvantage == 0), and leaves `RegisterThrown`
untouched. The struggle channel is exempt via `EntryOk(..., ignoreHitDisadvantage: true)`.

## The problem

The action FSM and the disadvantage system are two disjoint implementations of the same idea —
"you don't own your action slot right now" — and they don't talk to each other:

1. **Getting hit never interrupts a live action.** An attack's `CheckConditions` is just
   `TimeInState < Duration`, so a player who eats a stun mid-stab keeps stabbing, hitboxes still
   publishing. Hitstun/stun only bite at *entry*, after the action naturally exits.
2. **Hitstun is a hand-rolled second recovery index.** `CombatState.HitstunExpireFrame` is
   structurally identical to `Condition.RecoveryExpireFrame`, but it's consumed by ~15 scattered
   `ctx.Combat?.BlocksAttack == true` checks (some also checking `HitstunActive`, inconsistently)
   instead of the one `EntryOk(maxEntryIndex)` law.
3. **There is no action escape from stun.** A stale comment in the slash preconditions says
   "Guard is the intended escape (it can fire during stun)," but `GuardAction` itself gates on
   `BlocksAttack` (which includes `StunActive`), so nothing can fire.

The movement side (StunnedState / TumbleState / tech / DI mute / `BlockedCapabilities`) is already
priority-law-shaped and is **explicitly out of scope** — this plan changes only how the *action*
FSM experiences getting hit.

## Core idea: one countdown, one law

A hit is an **involuntary eviction**. The airlock already gives voluntary eviction a law
(inherited-priority bid + CommitProfile price + Exit stamp). Getting hit reuses the identical
machinery with the price dictated by the hit instead of by the incumbent:

```
voluntary:    entrant request  → Recovery bids entrant's Passive → evict at CommitProfile price
involuntary:  incoming hit     → Recovery bids InterruptBid      → evict at hitstun price
```

Concretely:

### 1. Hitstun stamps the recovery countdown

`CombatState.OnHitRegistered` (or the caller) additionally writes the hitstun window into
`Condition.RecoveryActive` / `RecoveryExpireFrame`, **max-merged** with whatever stamp is already
there. The diminishing-extension logic (0.5× on follow-ups) stays in CombatState and feeds the
stamped value, so stun-lock convergence is unchanged.

Max-merge is load-bearing against a real exploit: if a hit *replaced* the stamp, getting jabbed
(0.1 s hitstun) mid-stab-strike would be a cheaper cancel than the stab's own 0.3 s tail — you
could ask a friend to hit you to skip your end lag. With max-merge, the evicted action's Exit
still stamps its CommitProfile quote and the hit stamp only ever lengthens it. Both writes go
through max, so ordering (Exit vs. OnHit within a frame) doesn't matter.

`HitstunActive`/`HitstunExpireFrame` **stay** on CombatState for the movement-side consumers
(control mute, `BlockedCapabilities`, Stunned/Tumble). Only the action side stops reading them.

### 2. Hit-eviction through Recovery

`PlayerCharacter.OnHit` fires from `CombatSystem.Apply`, after all updates in the same frame, so
the next frame's action scan sees the fresh stamp. RecoveryAction gains an involuntary entry
case:

```
incumbent is non-neutral
AND a hit registered since the incumbent's last scan (Combat.LastHitFrame >= entry frame works)
AND the hit was not armored (see §4)
⇒ enter with bid = InterruptBid
```

`InterruptBid` should sit **above every action's Active** (e.g. 60, over Grab's 48) — flinch is
universal; exceptions are carved by armor (§4), not by the priority race. This is deliberately
different from voluntary eviction (which must win the priority race): a jab interrupting a
half-swung stab is the point of hitstun.

Consequences worth naming:
- **Trades now interrupt.** Two simultaneous connects evict each other (CombatSystem applies
  both after both updates — symmetric, order-free). Today both swings complete. This is a real
  gameplay change and the main thing playtesting must judge.
- A hit while already in Recovery or neutral needs no eviction — the stamp max-merges and the
  existing countdown-handoff case (neutral incumbent + RecoveryActive) picks it up. Side
  benefit: a hitstunned victim now visibly *sits in RecoveryAction*, which gives the animation
  layer one obvious hook for the existing `hitstun.json` clip.
- An out-of-cone (unparried) hit evicts guard. Getting hit from behind breaks your stance —
  reasonable, but worth a test pinning it as intended.

### 3. Entry windows replace the BlocksAttack sprinkle

With hitstun living on the countdown, `EntryOk(ctx, maxEntryIndex)` already expresses per-action
disadvantage rules, and the ~15 hand checks delete:

- **Attacks** (openers, stab, pulse, ranged): `maxEntryIndex = 0` — cannot fire until the
  countdown fully drains. Exactly today's behavior, minus the sprinkle.
- **Guard**: its existing 0.2 s MaxEntry window becomes the stun-escape semantics for free —
  guard may fire once ≤ 0.2 s of disadvantage remains. So a heavy hit buys a *guaranteed*
  (window − 0.2 s) of disadvantage, and a mashed guard caps the tail rather than negating it.
  This finally implements the roadmap's "guard is the escape" intent, with a real cost attached.
- **Combo slashes** (`EntryFromAnyRecoveryIndex`) are the one place the two countdown sources
  must NOT unify blindly: any-index entry exists so *your own* combo chains through *your own*
  recovery — it must not let a victim slash out of hitstun instantly. Fix: one taint bit on the
  stamp, `Condition.RecoveryStampFromHit` (set by the hit path, cleared when the countdown
  expires; snapshotted). `EntryFromAnyRecoveryIndex` bypasses only untainted (self) stamps. A
  hit mid-combo taints the whole remaining countdown — being interrupted drops your combo, which
  is the desired reading.

**Grabbed stays a flag, not a countdown.** `GrabbedActive` is a live external hold re-marked
every frame by the grab field — there is no window to count down. `BlocksAttack` shrinks to
`GrabbedActive` only (StunActive drops out), keeping the struggle-slash exemption exactly as is.

### 4. Superarmor: the involuntary-eviction price knob

Symmetric with CommitProfile, per action, phase-dependent:

```csharp
// Strength threshold below which an incoming hit does not interrupt this action.
// 0 (default) = no armor: any hit evicts. Compare against HitResult.Strength —
// the same pre-mass scalar the stun threshold uses, so armor tuning reads in the
// same units as CombatState's constants.
public virtual float ArmorProfile(EnvironmentContext ctx, in ActionVars vars) => 0f;
```

- `CommitProfile` prices **voluntary** eviction (frames of recovery).
- `ArmorProfile` prices **involuntary** eviction (strength threshold to break it).

An armored hit (Strength < threshold), Smash-style: **still adds DamagePercent** (armor tanks
the damage), **no knockback, no hitstun/stun flags, no stamp, no eviction**. The attacker still
gets normal recoil (`OnHit` returns the resolved impulse as usual). Implementation site:
`PlayerCharacter.OnHit` consults `_currentAction.ArmorProfile(ctx…, in _actionVars)` before the
knockback/registration block — the fields are on hand and the call is deterministic.

First candidates (numbers TBD in playtest, against the Strength reference points in
CombatState's stun-threshold comment — Slash1 ~200, Slash3 ~500, Stab ~950):
- **Pulse** during charge/windup: armor ≈ 300 — light jabs don't stuff the big commitment,
  Slash3/Stab still do.
- **Stab** during the lunge: maybe a small window, maybe none — the stab is fast; armor may
  make it oppressive.
- Everything else: 0. Armor should be rare enough to be legible.

Open sub-question: should armor also *scale* knockback rather than binary-gate (heavy armor vs.
super armor in Smash terms)? Start binary; the threshold form leaves room.

### 5. What deletes / what stays

Deletes:
- All per-action `ctx.Combat?.BlocksAttack == true` and `HitstunActive` checks in
  preconditions (~15 sites), including guard's (its entry window takes over) — EXCEPT the
  grabbed gate, which `BlocksAttack == GrabbedActive` keeps covering.
- The stale "guard can fire during stun" comment.

Stays:
- `CombatState` flags + Tick + diminishing extensions (movement side reads them; the stamp
  writer reuses the computed window).
- Parry, struggle, tech, i-frames — all upstream of the eviction decision in `OnHit`'s filter
  chain, untouched.
- Stun's *movement* meaning (Stunned/Tumble/capability mask).

New state (all snapshot-safe value fields):
- `Condition.RecoveryStampFromHit` (bool) — the taint bit.
- Nothing else: InterruptBid is a const; ArmorProfile is stateless.

## Edge cases to pin with tests

1. **Mid-stab hit evicts**: victim's stab hitboxes stop publishing the frame after the hit
   lands; victim ends in Recovery with the max(hitstun, stab-tail) stamp.
2. **Jab-cancel exploit closed**: hit with 0.1 s hitstun mid-strike still pays the full 0.3 s
   stab tail (max-merge).
3. **Guard escape window**: heavy hit (0.6 s stun-tier stamp) → guard held from frame 1 enters
   only once ≤ 0.2 s remains; attacks wait for index 0.
4. **Taint bit**: combo slash can chain through its own recovery but NOT through a hit stamp of
   equal length.
5. **Trade**: both players connect same frame → both evicted, both stamped, no order asymmetry
   (run both player orders, assert same result — determinism guard).
6. **Armor**: armored pulse charge eats a Slash1 (percent added, no interrupt), loses to a Stab.
7. **Guard broken from behind**: out-of-cone hit evicts GuardAction into Recovery.
8. Existing `CombatHitstunTests` / `TumbleTechTests` / `GrabTests` stay green (movement side
   untouched).

## Open questions (decide before implementing)

1. **Do light hits evict?** This sketch says yes — flinch is universal, armor is the exception
   tool. The alternative (only stun-tier hits evict) keeps today's trade behavior for light
   pokes but needs a second threshold and makes light hitstun purely an entry gate again.
   Recommendation: universal flinch + armor; it's one rule.
2. **Guard escape shape**: tail-window entry (this sketch) vs. guard-anytime-during-stun (the
   stale comment's intent). Tail-window preserves hit reward; anytime makes stun nearly
   meaningless against a defensive player.
3. **Armor semantics**: binary no-sell (sketch) vs. knockback scaling. Also whether armored
   hits should still add full percent or reduced percent.
4. **Should the victim's DI/movement mute key off the stamp too?** Sketch says no — movement
   keeps reading Hitstun flags; don't couple the FSMs further than needed.
