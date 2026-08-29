# Hit feel plan (2026-08-28)

## Implementation status (2026-08-28)

All phases (0-7) implemented. Full test suite: same 8 pre-existing failures
as before this work (each independently confirmed via revert-check to
predate it — see below), 530 passing.

Two things worth knowing before playtesting:

- **Hitstop's scope shrank from the original sketch.** Blanket-skipping
  `PlayerCharacter.Update` while `HitstopActive` broke two real mechanics
  found by the full suite: `HitEvictionTests` (skipping action *selection*
  meant a fresh hit could never flinch-evict a mid-swing attack — nothing
  could preempt what it just hit) and `TumbleTechTests` (skipping *movement*
  Update blocked `TumbleState`'s tech-window check, an infinite lock under
  sustained hits). Shipped version only freezes the current action's
  `Update`/`ApplyActionForces` — movement Update and all FSM
  selection/eviction run every frame regardless of hitstop. This still
  freezes attacks (no hitbox progression, no recoil/lunge) but does not
  freeze the victim's position/animation the way a from-scratch fighting-game
  hitstop would. A true position freeze would mean excluding a body from
  `Simulation.Step`'s shared physics batch for a few frames — doable, but a
  separate, more careful change than fits under "small bundle."
- **Two pre-existing bugs surfaced, not touched**: `GroundSlash1`/`GroundSlash2`
  both have `HoldVictims => false` and default `StrikeSpeed` to `1f` (Collision
  mode), while their class comments and `CombatFeelTests.HoldField_Slash1_*`
  both assume `HoldVictims` is `true` and/or Impulse mode is the default for
  these two moves. `HoldField_Slash1_KeepsVictimInRange_DespiteWalkingAway`
  fails on `main` too (confirmed by reverting this session's changes and
  re-running it) — unrelated to this work, left alone.
- **Audio pipeline compromise**: `ffmpeg`/`pwsh` aren't available in the
  sandbox this was implemented in, so the 5 new clips (`hit_connect_05..07`,
  `swing_01/02`) were copied in as-is rather than run through
  `build-sfx.ps1`'s loudness-match/mono/22.05kHz normalization, and
  `Content.mgcb`/`Content.Mac.mgcb`/`SoundManifest.g.cs` were hand-edited to
  match what `sync-sounds.ps1` would have generated. Re-run the real pipeline
  once on a machine that has both tools.

---

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
- **`HitConnect` sound is already wired AND already has clips** —
  `GameAudio.HitConnect` (`Audio/GameAudio.cs:127-140`) fires `SoundKind.HitConnect`
  scaled by `LastHitImpulse` every time a hit lands, gated on
  `age <= StampWindowFrames`; `hit_connect_01..04.ogg` already exist in
  `Assets/Sounds` and are already built into `Content.mgcb`. (Earlier draft
  of this doc assumed this was clipless — it isn't. What's actually new is
  the raw material dropped in the repo root just now; see phase 3 below.)
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

## Phase 2b — attacker recoil ("feel yourself get bumped back")

This is the Newton's-third-law counterpart to phase 2, and the pipeline for
it **already exists end-to-end** — it's just only wired to one move.

- `CombatSystem.Apply`'s entity path unconditionally accumulates
  `-delivered * hit.RecoilScale` into a per-`HitId` recoil inbox whenever a
  hitbox connects with an entity (`World/CombatSystem.cs:171-172`) —
  `delivered` is the same `HitResult.Impulse` `HitResolver.Resolve` computes
  for the victim's own knockback (`Physics/HitResolver.cs:26-30`). This is
  not tile-restricted despite most of the surrounding comments talking about
  wall pogo; `RecoilBreakProtected`/`RecoilMinMaterialHP` are the only
  genuinely tile-only gates (`World/Hitbox.cs:63,` "has no effect on entity
  recoil").
- **`RecoilScale` defaults to `0`** on every `Hitbox` (`Hitbox.cs:59,120`)
  and is only ever set nonzero in one place: `StabAction`'s
  `RecoilScale = 0.2f` (`ActionStates.cs:936`) on its primary (entity-eligible)
  hitbox. Every slash-family move — `GroundSlash1/2/3`, `CrouchSlash`,
  `AirSlash1/2`, `AirTurnSlash`, `GuardRetaliateAction`, `GrabbedSlash`, all
  built on `SlashLikeAction`'s shared hitbox publish
  (`ActionStates.cs:562-573`) — never sets it, so they're recoil-inert today
  against players and terrain alike.
- The read side is just as narrow: `PeekRecoil` has exactly one caller in the
  whole codebase, `StabAction.ApplyActionForces`
  (`ActionStates.cs:1116-1120`), which adds the recoil vector straight into
  `ctx.Body.Velocity`. `ActionState.ApplyActionForces`'s base is a no-op, and
  no other action overrides it.
- To make this general rather than stab-only:
  1. Author a small nonzero `RecoilScale` at `SlashLikeAction`'s shared
     hitbox-publish site (`ActionStates.cs:562-573`) — one edit covers every
     slash variant at once, matching "most hits."
  2. Hoist the 3-line `PeekRecoil → Body.Velocity` consumer out of
     `StabAction.ApplyActionForces` into `SlashLikeAction` (or a small shared
     helper both call) instead of duplicating it per subclass.
- **Check before tuning**: whether recoil "scales with larger hits" for free
  depends on hitbox mode. `HitResult.Impulse` in **Impulse mode** is the raw
  authored `KnockbackImpulse`, *not* scaled by the victim's `DamagePercent`
  escalation (`HitResolver.cs:56-59` — only `TargetDeltaV`/`Strength` get the
  `scale` factor); in **Collision mode** the impulse already bakes in the
  percent-scaled closing speed (`HitResolver.cs:70`, `u *= scale`). So a
  Collision-mode slash would already recoil harder as the fight escalates,
  the way Stab's presumably does; an Impulse-mode one would give the same
  recoil every time regardless of the target's accumulated percent — worth
  confirming which mode the slash family is in before assuming the "larger
  hits push back harder" framing falls out automatically.
- **No test coverage today**: `MTile.Tests/Sim/AttackRecoilTests.cs` only
  exercises the tile-recoil path — every `combat.Apply(...)` call in it
  passes `_ => null` as the entity resolver, which short-circuits the entity
  branch entirely. PvP recoil is unverified; add a scenario test (two
  `SimPlayer`s, one lands a hit, assert the attacker's velocity picks up the
  recoil next frame) alongside whatever lands here.

## Phase 3 — hit sounds

Checked what's actually sitting in the repo root right now:

- `hits/` — a 37-clip raw pack (`hit01.mp3.flac` … `hit37.mp3.flac`), untouched
  source material.
- `hit02.mp3.ogg`, `hit33.mp3.ogg`, `hit_big.mp3.ogg` — already Ogg Vorbis
  (stereo, 44.1 kHz), evidently hand-picked from the `hits/` pack (`hit02`/
  `hit33` line up with pack indices) plus one distinct "big hit" pick. Not
  yet loudness-matched/mono/22.05 kHz to the pipeline spec
  (`Plans/AUDIO_ASSET_LIST.md`) — they need to go through `build-sfx.ps1`
  same as anything else, even though they're already `.ogg`; the script
  accepts `.ogg` as valid input and will re-normalize it.
- `swosh1.ogg`, `swosh2.ogg` — attack-swing whooshes. There's no `SoundKind`
  for this yet; it's new wiring, not just new clips.

None of this is committed and none of it is in `Audio/raw/` (gitignored
intake dir, `.gitignore:42`) yet — it's loose in the repo root.

### 3a. Expand `HitConnect` variety

1. Move the desired picks into `Audio/raw/` and run `build-sfx.ps1`. **Watch
   the numbering**: the script restarts variant numbering at `_01` for
   whatever's in `Audio/raw` at run time (`scripts/build-sfx.ps1:74-76`) and
   skips a destination that already exists unless `-Force` is passed
   (`.ps1:83-86`) — so pointing `-Name hit_connect` at just the 3 new picks
   would try to write `hit_connect_01..03.ogg` and silently no-op against the
   4 that already exist. Either process the new files without `-Name`
   (keeps each one's sanitized source name as the stem, no collision) and
   hand-rename the outputs to continue the sequence at `hit_connect_05.ogg`,
   `_06`, `_07`; or copy the existing 4 back into `Audio/raw` alongside the
   new picks so one pass renumbers everything 01..07 consistently.
2. The full 37-clip `hits/` pack is raw material, not something to bulk-add
   — `Plans/AUDIO_PLAN.md` §5's round-robin variety is about avoiding
   identical repeats, not maximizing clip count, and the 3 already-exported
   picks look like the curated subset. Worth confirming with whoever picked
   them whether more than those 3 are wanted before processing the whole
   pack.
3. Run `sync-sounds.ps1` to regenerate `Content.mgcb` + `SoundManifest.g.cs`.
   No `GameAudio.cs` changes needed — `HitConnect` already round-robins
   across however many `hit_connect_*` clips exist.

### 3b. New: swing/whoosh sound

This needs actual wiring, not just clips:

1. Add a `SoundKind` entry (e.g. `Swing`) in `Audio/SoundKind.cs`. Insert it
   **before** `Respawn`, not after — `SoundKinds.Count` is hardcoded as
   `(int)SoundKind.Respawn + 1` (`SoundKind.cs:42`), so anything appended
   after `Respawn` needs that line updated too; inserting earlier needs no
   other change since nothing hardcodes a kind's numeric value.
2. `build-sfx.ps1 -Name swing` on `swosh1.ogg`/`swosh2.ogg` → `swing_01.ogg`,
   `swing_02.ogg` in `Assets/Sounds`, then `sync-sounds.ps1`.
3. Trigger it in `GameAudio.cs` with a state-entry edge, the same pattern
   `Jump`/`DoubleJump` already use (`GameAudio.cs:145-152,174-181`) — except
   keyed off the **action** FSM, not movement: `PlayerCharacter` exposes
   `CurrentAction`/`GetPreviousAction(int)` (`PlayerCharacter.cs:290,664`)
   exactly parallel to `CurrentState`/`GetPreviousState`. Add a `Swing`
   method to `GameAudio`, called from `Player()` (`GameAudio.cs:96-106`)
   alongside the others: fire when `CurrentAction` is a `SlashLikeAction` or
   `StabAction` and `GetPreviousAction(1)` isn't — same "derivable from the
   snapshotted ring, so it's rollback-safe for free" reasoning the jump
   sounds already rely on. Two clips round-robin automatically through
   `_bank.Pick`, same as everywhere else.

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
top. Phase 2b (attacker recoil) is small, self-contained, and also
sim-affecting — good to land and playtest right after phase 1, before the
cosmetic phases. Phases 3-7 are independent of each other and of
hitstop/recoil, and can be done in any order once phase 0 lands — phase 3
(sound) is unblocked today and doesn't even need phase 0. Within phase 3,
3a (hit variety) is pure asset work and can happen any time; 3b (swing
sound) is a few real code lines and can land alongside it or separately.

## Open questions

- Attacker-side hitstop/flash: extend `IHittable`/`CombatSystem` to notify
  the attacker symmetrically, or scope V1 to victim-only? (Phase 1, Phase 6)
- Phase 2: is "backward knockback" the existing `MinLaunch`-floor reading, or
  a genuinely direction-overriding push regardless of the attack's authored
  launch vector? Changes the implementation, not just the tuning.
- Phase 2b: confirm slash-family hitboxes' `KnockbackMode` (Impulse vs
  Collision) before tuning `RecoilScale` — determines whether recoil grows
  with the victim's escalating `DamagePercent` for free or needs that added
  explicitly.
- Hitstop scope: players only for V1, or should enemy `EnemyState<TVars>`
  hits freeze too?
- Phase 3a: is the whole 37-clip `hits/` pack meant to feed `HitConnect`, or
  just the 3 already-exported picks (`hit02`, `hit33`, `hit_big`)? Assumed
  the latter above.
