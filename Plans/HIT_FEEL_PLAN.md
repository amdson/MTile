# Hit feel plan (2026-08-28)

Follow-up to `todo.txt` item 1. Scope: make a landed hit read as an impact —
hitstop, a baseline shove, sound, and screen-space juice — scaled consistently
by how hard the hit was. Excludes windup/follow-through animation authoring
(todo.txt's "better animation" bullet) — that's a separate, much bigger,
clip-authoring effort or a new `AmbientCorrector`/action work, unrelated to
this reactive-feedback pass.

## Design principle: one strength number, one trigger stamp

Everything below should read off the same two things instead of inventing
parallel ones:

- **Strength** — `HitResult.Strength` (`Physics/HitResolver.cs:36`), already
  the thing `CombatState` uses for the stun threshold and hitstun duration,
  and already what `GameAudio.HitConnect` scales gain/pitch by
  (`Audio/GameAudio.cs:134`, as `LastHitImpulse`). Hitstop length, shake
  magnitude, particle count, and decal size should all be `f(Strength)`, not
  four separately-tuned constants.
- **Trigger stamp** — `CombatState.LastHitFrame` (`Character/Action/CombatState.cs:24`).
  It's already snapshotted, already how `GameAudio` derives a rollback-safe
  one-shot ("the stamp IS the identity" — `GameAudio.cs:125-126`), and
  `AudioMixer.Fire` already dedupes on `(simFrame, id)` for exactly this
  reason (`Audio/AudioMixer.cs:93-96`). Any new cosmetic system should
  compare its own last-seen frame to `LastHitFrame` per player rather than
  building a second event/dedup mechanism.

One consequence: `CombatState` currently stamps hit *magnitude* but not
*direction*. Several items below (shake direction, the knockback cue, the
weapon flash) want direction too, so phase 0 adds it once, centrally, instead
of each feature re-deriving it a different way.

## Already in place (no work needed)

- **Hit dedup under rollback** — `CombatSystem._hitDedupe` + `SimSnapshot`
  (`World/CombatSystem.cs:28`, `.cs:217-235`) guarantee `OnHit` fires exactly
  once per `(HitId, Target)` even across resimulation. Every render-side
  trigger below rides on top of this transitively, through `LastHitFrame`.
- **A baseline "hit always moves you" floor already exists** —
  `HitResolver.Resolve`'s `MinLaunch` (`Physics/HitResolver.cs:92-95`): "a
  landed hit always visibly moves a movable target, even when the closing
  speed was tiny." Phase 2 below is mostly a tuning pass on this, not new
  code.
- **`HitConnect` sound is already wired**, just clipless —
  `GameAudio.HitConnect` (`Audio/GameAudio.cs:127-140`) fires `SoundKind.HitConnect`
  scaled by `LastHitImpulse` every time a hit lands, gated on
  `age <= StampWindowFrames`. `SoundKind.HitConnect` already exists in the
  enum (`Audio/SoundKind.cs:29`). Dropping in `hit_connect_01.ogg` (+ `_02`,
  `_03`, …) and running `sync-sounds.ps1` is most of phase 3 — see
  `/audio-pipeline`.
- **Render-only edge-detect pattern** — `CosmeticUpdateSystem` already does
  exactly this for the landing puff (`_wasGroundedLastFrame`,
  `Drawing/CosmeticUpdateSystem.cs:28-30,136-141`): reads settled sim state
  once per real frame, after rollback resolves, fires a cosmetic once on the
  transition. Every phase-4+ system below should be structured the same way
  and live in the same "reads sim, writes nothing back" layer — not inside
  `Simulation.Step`.
- **Particle primitives exist** — `Effects.HitSpark` (`Drawing/Effects.cs`,
  "short bright streaks at the contact point, biased along `dir`") is
  *already* the hit-spark particle; it's just not called from anywhere yet.
  `Effects.TileBreak` is the template for a debris burst.
- **`AttackGlowSystem`** already renders the ongoing knife-trail glow during
  a swing. The "weapon flash" idea (phase 6) is a distinct, separate,
  one-frame accent *at the moment of connection* — don't fold it into that
  system, which is about the swing itself, not the hit.

## Phase 0 — shared plumbing

- Add `Vector2 LastHitDir` next to `LastHitImpulse`/`LastHitFrame` in
  `CombatState.cs:24`, set alongside them (from `res.Impulse` normalized, or
  `hit.StrikeDir`) at the `OnHitRegistered` call site in
  `PlayerCharacter.cs:191`. Add it to `CombatState.CopyFrom` (`.cs:260-272`)
  — it's a value type, same flat-copy treatment as everything else there.
- New render-only class, e.g. `Drawing/HitFeelSystem.cs`, parallel to
  `GameAudio`: a `Collect(Simulation sim)` called once per render frame from
  `Game1.cs` alongside `CosmeticUpdateSystem.Update`/`GameAudio.CollectLevel`.
  Internally: one `int[] _lastHandledFrame` per tracked player (mirrors
  `_wasGroundedLastFrame`'s style but keyed on `LastHitFrame` equality rather
  than a bool edge, since consecutive frames can share the same stamp within
  `StampWindowFrames`). This is the one place phases 4-7 hook in — don't give
  each its own dedup state.

## Phase 1 — hitstop (sim-side, the one item here that changes gameplay feel, not just cosmetics)

This is the load-bearing one; everything else is decoration around it.

- Add `bool HitstopActive` / `int HitstopExpireFrame` to `CombatState`,
  following the exact `HitstunActive`/`HitstunExpireFrame` shape
  (`CombatState.cs:17`) — an absolute expire-frame, ticked in `Tick(currentFrame)`
  (`.cs:217-227`), not a countdown. That's what makes it replay-identical
  across a rollback resimulation regardless of how many times a frame reruns.
- Set it from `OnHitRegistered` (`CombatState.cs:179-203`), duration scaled
  by `Strength` (e.g. `Clamp(impulse * k, MinFrames, MaxFrames)`, same shape
  as the existing hitstun-seconds formula immediately above it).
- **Victim** gets it for free — `OnHitRegistered` already runs in
  `PlayerCharacter.OnHit` (`PlayerCharacter.cs:191`). **Attacker** doesn't,
  yet: `CombatSystem.Apply`'s entity path only resolves `hb.Target`
  (`World/CombatSystem.cs:185`) to dispatch `OnHit`; it never resolves
  `hit.Owner` (available at `.cs:177`) to anything. Open design question:
  give `IHittable` a second, symmetric method (`OnLandedHit`? or reuse `OnHit`
  with a flag) so `CombatSystem` can also notify the attacker — or scope V1
  to victim-only hitstop and revisit. Victim-only is still a big feel win and
  ships without touching `CombatSystem`'s dispatch shape.
- **Where the freeze actually applies**: not by skipping `Simulation.Step`
  calls (would desync the rollback session from the real-time network tick
  it's paced against) and not by threading a new channel through
  `MovementModifiers` (that's a multiplicative scalar; hitstop wants a hard
  skip, not "×0 speed" — a `0×` state can still evaluate transitions).
  Instead, gate the per-entity movement/action FSM tick and velocity
  integration in `Simulation.Step` itself: while `HitstopActive`, don't
  advance that entity's movement/action state or run physics integration for
  it this frame, but still tick `CombatState.Tick` (so the timer itself
  expires) and still consume/buffer that player's input normally. Only the
  two combatants freeze — other entities/players keep simulating.
- Scope to players only for V1 (enemies/`EnemyState<TVars>` freezing is a
  stretch goal, not blocking).

## Phase 2 — baseline backward knockback ("most hits" push)

Reframing this against what's already there: `HitResolver.MinLaunch`
(`Physics/HitResolver.cs:92-95`) is precisely "always push the victim a
minimum amount, even on a weak/glancing hit." The likely gap isn't a missing
mechanism, it's **inconsistent authoring** — some hitboxes probably have
`MinLaunch: 0` or never set it. This phase is:

1. Audit hitbox authoring (wherever `Hitbox` values get built per-move) for
   `MinLaunch` coverage; set a small default floor across normal attacks.
2. Explicitly **preserve the deliberate zero-knockback exceptions** — the
   grab-struggle hit is zero-knockback *by design* ("wearing a grab down
   never stuns the grabber," `CombatState.cs:71-77`), and a parried hit
   returns early before any knockback applies at all (`PlayerCharacter.cs:146-147`).
   Don't blanket-apply the floor; apply it where hits currently fall through
   to `HitResolver.Resolve`.
3. If "backward" specifically (i.e., independent of the authored strike
   direction, always opposite the victim's facing) is what's wanted rather
   than "along the hit's existing launch direction, just with a guaranteed
   minimum" — that's a different, small change: bias `n` in `HitResolver.Resolve`
   toward `-facing` before the floor applies. Worth confirming which reading
   is intended before implementing; the `MinLaunch` floor is strictly simpler
   and reuses an existing knob.

## Phase 3 — hit sounds

Mostly asset work, per the note above:

- Drop `hit_connect_01.ogg` (`_02`, `_03`, …) into `Assets/Sounds`, run
  `sync-sounds.ps1` (`/audio-pipeline` has the full recipe + `Content.mgcb`
  regen step).
- Optional refinement once clips exist: `GameAudio.HitConnect` currently maps
  one continuous `t = LastHitImpulse/900` onto gain+pitch. Consider a small
  tier split (e.g. a distinct clip or bigger pitch drop past the
  `StunImpulseThreshold` in `CombatState.cs:161`) so a stun-crossing hit
  sounds categorically heavier, not just louder — mirrors how `Land`
  (`GameAudio.cs:158-170`) already scales gain off impact but is worth a
  second clip tier for "hard" landings too, if that's cheap while touching
  this code.

## Phase 4 — screen shake

New render-only piece, driven by `HitFeelSystem` (phase 0), *not* by
`Simulation`. `Camera` (`Camera.cs`) currently has no shake offset — add a
transient `Vector2 ShakeOffset` decayed per render-`dt` (trauma/spring decay,
not a fixed-length wiggle, so overlapping hits compound naturally rather than
resetting), applied inside `GetTransform` (`Camera.cs:50-53`) alongside
`Position`. Magnitude scales with `Strength`. Never touches sim state, so no
rollback concerns — same reasoning as why `ParticleSystem` isn't in
`SimSnapshot`.

## Phase 5 — directional knockback cue

Needs `LastHitDir` from phase 0. A short streak/arrow rendered at the victim
along `LastHitDir`, fired by `HitFeelSystem`'s edge-detect, sized by
`Strength`. Cheapest version: another `Effects.*` helper alongside
`HitSpark`/`TileBreak`, spawned into the existing `ParticleSystem` — no new
render subsystem needed, just a new preset burst function.

## Phase 6 — weapon flash (attacker-side)

Distinct from `AttackGlowSystem`'s continuous swing trail — a single bright
flash at the weapon/contact point on the frame the hit lands. Needs the
attacker-side hookup from phase 1's open question (resolving `hit.Owner`) if
it's meant to render on the attacker's weapon specifically; if it's meant to
render at the contact point instead (simpler, and arguably reads just as
well), it only needs `hb.Region`/the hit position already available at
`CombatSystem.Apply`'s dispatch site (`World/CombatSystem.cs:192`) — thread
the contact point through to something `HitFeelSystem` can pick up (e.g.
stash it on `CombatState` alongside `LastHitDir`, or emit through the
existing `PresentationEventLog`/`ITelegraphSource` seam if it needs to be
occlusion/timing-accurate). Start with contact-point-only; it's strictly less
plumbing and may be enough.

## Phase 7 — debris decals

Persistent (multi-second, not one-particle-lifetime) marks at hit/crush
locations. Render-only, same as particles — don't add these to `SimSnapshot`;
they're purely cosmetic and, like `ParticleSystem`, re-derive fine from a
settled post-rollback frame via the phase-0 edge-detect. Simplest version:
a longer-lived sibling of `Effects.TileBreak`'s debris squares that stick
instead of arcing away, or a tiny fixed-capacity ring of decal quads drawn
under the sprite layer. Natural pairing point: the crush-into-terrain impact
path (`Physics/ImpactDamage.cs`) for cracked-tile decals, and
`CombatSystem`'s tile-break dispatch (`World/CombatSystem.cs:137`, already
firing `PresentationKind.TileBreak`) for hit-on-terrain decals — both are
already edge-triggered through the existing `PresentationEventLog` seam, so
decals can likely hang off that instead of inventing a third trigger path.

## Suggested order

Phase 0 → Phase 1 (hitstop) first, since it's the one sim-affecting change
and worth validating/playtesting in isolation before stacking cosmetics on
top. Phases 3-7 are independent of each other and of hitstop, and can be done
in any order once phase 0 lands — phase 3 (sound) is unblocked today and
doesn't even need phase 0.

## Open questions

- Attacker-side hitstop/flash: extend `IHittable`/`CombatSystem` to notify
  the attacker symmetrically, or scope V1 to victim-only? (Phase 1, Phase 6)
- Phase 2: is "backward knockback" the existing `MinLaunch`-floor reading, or
  a genuinely direction-overriding push regardless of the attack's authored
  launch vector? Changes the implementation, not just the tuning.
- Hitstop scope: players only for V1, or should enemy `EnemyState<TVars>`
  hits freeze too?
