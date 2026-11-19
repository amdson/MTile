# Character/Action map
Verified against: e5b75c6 (2026-11-19)
Covers: Character/Action/*.cs, Character/Action/action_states_notes.md (callers read in Character/PlayerCharacter.cs, Character/Sensing/EnvironmentContext.cs, Character/Input/IntentBuffer.cs)

## What this area is
The player's second, concurrent FSM: attacks, guard, grab family and block build/paint/place actions (`ActionState` subclasses), plus the plain-data state they need: `ActionVars` (per-activation), `ConditionState` (combo/recovery stamps), `CombatState` (hitstun/stun/hitstop/guard), `BuildMeters` (build economy). All of it is sim-side and snapshotted. Render hooks on `ActionState` (`Telegraph`, `DebugQueryShapes`, `AnimationProgress`, `AnimationVariant`, `TryAnimationAim`) are render-only and take `ActionVars` by `in`.
Boundary: `PlayerCharacter` (Character/PlayerCharacter.cs) owns the registry and drives the FSM; `EnvironmentContext` (Character/Sensing) is what actions read; intents come from `Character/Input`; hit resolution is `World/CombatSystem.cs`; movement is `Character/Movement`.

## Files
- `ActionStates.cs` (4264 lines): `FrameData` table, `ActionState` base, hub states, `SlashLikeAction`, every concrete action, `BlockEruptionHelpers`. Index below.
- `ActionVars.cs` (109): flat `struct ActionVars`, fields reused across actions. The snapshot unit for actions.
- `ConditionState.cs` (91): combo flags (`Slash2Ready`, `Slash3Ready`, `AirSlash2Ready`), `ComboGapExpireFrame`, recovery countdown (`RecoveryActive`/`RecoveryExpireFrame`), `Tick`, `StampRecovery`, `ShiftExpiries`, `Clone`/`CopyFrom`.
- `CombatState.cs` (721): `GuardOutcome`, `CombatState` (hitstun/launched/thrown/stun/hitstop/grabbed/guard/invuln flags, tuning consts, `OnHitRegistered`, `ApplyHitstop`, `ResolveGuard`, `Tick`, `Clone`/`CopyFrom`).
- `PlayerAbilityState.cs` (71): bundle of `Facing`, `Condition`, `Combat`, `Meters` + movement ability flags; `Clone`/`CopyFrom`.
- `BuildMeters.cs` (371): reservoir / working pool / eruption charge (+ post-release banked reserve, crystal-wave cooldown) for block placement; pure value state; `Step(dt, buildHeld)`.
- `IHittable.cs` (33): implemented by `PlayerCharacter` and `Entity`; `CombatSystem` dispatches `OnHit` through it.
- `action_states_notes.md`: old design notes (not code). Its "2 second input buffer" is not the code: `IntentBuffer.DefaultLookbackFrames` is 20 frames, `OpenerLookbackFrames` 12 (Character/Input/IntentBuffer.cs:107,113).

### ActionStates.cs index (line = declaration; priorities are Active/Passive from code)
- 24 `FrameData` (nested const classes, seconds = frames/60): slashes 33-45, GuardRetaliate/GrabbedSlash 52-53, Stab/AirWhipKick/AirHammer/Pulse/Burst 56-60 (`Evict` = wind-up cancel tail; Burst has none), LobbedArea/BlockGrab/Grab 64-71.
- 80 `ActionState` abstract base (virtual hooks: `CommitProfile` 99, `ArmorProfile` 109, `NeutralForEntry` 116, `AllowedWhileGrabbed` 127, `EntryOk` helper 139, lifecycle 153-159, `ApplyMovementModifiers` 177, `ApplyActionForces` 184, render hooks 165-171 and 186-213).
- 217 `NullAction` 0/0: neutral hub, always passes; `NeutralForEntry` and `AllowedWhileGrabbed`.
- 259 `RecoveryAction` 10/`_bid` (45 handoff/wind-up, 60 flinch or grabbed, else the lookahead's inherited candidate bid): countdown + LMB charge wind-up airlock; `AllowedWhileGrabbed`. `CheckPreConditions` 293, `LookaheadBestBid` 372, `ChargeFraction` 413, `Exit` stamps `StabCharge` 508, charge modifiers 528.
- 571 `SlashLikeAction` abstract base 30/30: per-variant knobs 626-740, `CheckPreConditions` 792, `Enter` 819, `Update` (hitbox publish) 845, `PublishHoldField` 913 (dormant: `HoldVictims` false everywhere), `ApplyActionForces` recoil 948.
- 963 `GroundSlash1` 30/30: ground opener.
- 999 `GroundSlash2` 30/50: combo step (needs `Slash2Ready`).
- 1035 `GroundSlash3` 30/50: combo finisher (needs `Slash3Ready`), launches, light armor through the active window.
- 1080 `CrouchSlash` 30/32: one-and-done, requires `ctx.PreviousState(0) is CrouchedState`.
- 1124 `AirSlash1` 30/30: air opener.
- 1150 `AirSlash2` 30/50: air combo step (needs `AirSlash2Ready`).
- 1185 `AirTurnSlash` 30/35: air click behind facing, flips `Facing`.
- 1262 `DownAirSlash` 30/52: air click within 30 deg of straight down, pogo chop; `ApplyEntityPogo` 1374 shared with Burst.
- 1395 `StabAction` 30/30: gesture `Stab` intent, no Shift; consts 1396-1534; `Enter` 1570; `ArmorProfile` 1637 (grounded only); `CommitProfile` 1654; `ApplyActionForces` lunge+recoil 1681; `Update` steer + primary box + tiles-only block box 1718.
- 1897 `AirWhipKick` 30/35: air backward swipe; sweet-spot box published first.
- 2098 `AirHammerAction` 30/36: air swipe within 30 deg of down; `ApplyActionForces` 2217 floors downward speed (`SlamSpeed`) in the active window.
- 2307 `GuardAction` 35/40: Shift held, grounded, no L/R, no RMB; `BeginGuard`/`EndGuard`.
- 2453 `DashSlash` 30/53: PressEdge or Click only while `ctx.CurrentMovement is DashState`.
- 2510 `GuardRetaliateAction` 50/55: Click while `GuardCharged`; the one slash that stuns.
- 2568 `PulseAction` 30/30: `Circle` intent, 12-segment ring.
- 2717 `BurstAction` 30/30: RMB edge during a charging `RecoveryAction`; near box, then a 5-segment fan, each a TilesOnly + EntitiesOnly box pair.
- 2977 `BlockPaintAction` 8/10: plain RMB, `NeutralForEntry`; `Exit` may spawn a `MassBall` (fast release with banked charge).
- 3299 `BlockPlaceAction` 8/10: Shift+RMB (or plain RMB on the seed bar, "seed mode"), `NeutralForEntry`. Seed mode is a crouched `PlantTuning.SeedChannelFrames` channel (`UpdateSeedChannel`): armed at an on-the-feet RMB press on a cell legal from a crouch (Enter; `PlayerCharacter`'s seed crouch crouches the player), counts from the first crouched frame, re-checked every frame (crouch, hit, `SeedPlanting.LegalFromCrouch`), plants once through `SeedPlanting.TryPlant` on completion; Exit forfeits; `BlockPaintAction` (3039) declines on the seed bar.
- 3309 `BlockEruptionHelpers` (internal static: cursor/solid tests, `TryChargeBlock` 3383, `RecruitChargedBlocks` 3416).
- 3463 `LobbedAreaAction` 40/45: NOT in the registry (see traps).
- 3644 `BlockGrabAction` 46/46: Shift+LMB press on terrain; spawns `PullPointEntity` in `Enter` 3752; `HoldTarget` 3848.
- 3948 `GrabAction` 48/48: Shift+LMB press not claimed by BlockGrab; latches one victim, publishes a grab `ForceField` 4149; `Exit` 4164 spawns `GrabThrowEntity`.
- 4223 `GrabbedSlash` 30/36: struggle, `AllowedWhileGrabbed`, erodes `GrabStrength` by 0.5 per hit (4235).

## Flow
Per player per step: `Simulation.Step` calls `_player.Update(...)` (Simulation.cs:555, secondary players 563) -> entities (569) -> `_combat.Apply` (571) -> `ForceFieldSystem.Apply` -> physics. Inside `PlayerCharacter.Update` (Character/PlayerCharacter.cs:631):
1. `Condition.Tick(_frame)` (:742) and `Combat.Tick(_frame, guardHeld: Shift, dt)` (:744) expire stale flags; `Launched` is cleared once `Body.Launch` is spent (:751-753); hitstop thaw hands back parked velocity (:758-763). `hitstopFrozen = Combat.HitstopActive` is computed at :779.
2. `_inputParser.Detect(...)` enqueues intents (`PressEdge`, `Click`, `Stab`, `Circle`, ...; enum at IntentBuffer.cs:9) before either FSM runs (:785).
3. Movement state is selected first; `ctx.CurrentMovement` is set (:903).
4. Natural exit: `_currentAction.CheckConditions(...)`; on false -> `Exit`, swap to `NullAction`, `Enter` (:904-910).
5. `ctx.CurrentAction` / `ctx.CurrentActionVars` (a struct COPY) are refreshed (:915-916).
6. Selection: loop over `_actionRegistry` in registry order (:923-932). Skips the incumbent and, when `Combat.BlocksAttack`, any action with `AllowedWhileGrabbed == false`. Takes the highest `PassivePriority` among passing `CheckPreConditions` (strict `>`: first registered wins ties). Replaces the incumbent only if best > `incumbent.ActivePriority` (strict, :933): `Exit` old, `Enter` new. `PassivePriority` is read AFTER `CheckPreConditions` (`&&` short-circuit; RecoveryAction sets `_bid` inside it).
7. `_currentAction.ApplyMovementModifiers(ref ctx.Modifiers, in vars)` (:943), then hitstun/slide air-control scalars are applied on top (:952-958).
8. `_currentState.Update` (:964; movement, writes `Body.AppliedForce`).
9. `ApplyActionForces` (:972), skipped when `hitstopFrozen`; runs after movement force, before action `Update`. Reads last frame's `CombatSystem.PeekRecoil/PeekHits(HitId)`.
10. `_currentAction.Update` (:994), skipped when frozen (then `CombatSystem.HoldHitId(vars.HitId)` :998 keeps the dedupe set alive). Hitboxes are published here via `ctx.Hitboxes.Publish(new Hitbox(...))`; entities/forces via `ctx.Spawner`, `ctx.ForceFields.Publish`.
11. `Meters.Step(dt, RightClick)` (:1007) runs every frame regardless of action.
12. Attacker hitstop: `CombatSystem.PeekHitstop(vars.HitId)` -> `Combat.ApplyHitstop` + `Condition.ShiftExpiries(TakeHitstopShift())` (:1015-1023).
13. After all updates, `CombatSystem.Apply` resolves hits (Simulation.cs:571); a victim's `PlayerCharacter.OnHit` (:211) runs invuln, guard (`ResolveGuard`), grab-struggle, HP loss, held block, armor (`ArmorProfile` :283), knockback/launch, then `OnHitRegistered` (:324).

How an attack starts: gesture -> intent (Click opens `GroundSlash1`/`AirSlash1`/...; Stab swipe -> Stab/AirWhipKick/AirHammer by direction; Circle -> Pulse; LMB press edges -> Grab/BlockGrab/DashSlash; RMB edge in a charging wind-up -> Burst). `CheckPreConditions` peeks the intent (`Peek` with a lookback window), `EntryOk`, and posture. `Enter` consumes the intent, allocates `vars.HitId = ctx.HitIds.Next()`, zeroes `TimeInState`.
Chains: an `Exit` sets next-stage flag + `StampRecovery`; combo steps use `EntryFromAnyRecoveryIndex` (`EntryOk(ctx, int.MaxValue)`) and `ComboGapExpireFrame`, so they ignore the recovery countdown (but not hit disadvantage), and their `Enter` clears `RecoveryActive`.
Cancels: a live action is evicted only by `RecoveryAction` (lookahead bid at the future candidate's `PassivePriority`, flinch bid 60, grabbed bid 60) or by a direct higher-priority precondition. The evicted action's `Exit` still runs (Stab/Kick/Hammer/Pulse stamp `CommitProfile`; slashes stamp their `EndLag`).
Hit state: `OnHitRegistered` (CombatState.cs:440) sets hitstun (impulse-derived or `Hitbox.HitstunSecondsOverride`; hits 4+ of a run, or 2+ for non-chaining hits, are halved), stun only when the caller passes `stunSeconds` (the hitbox's `StunSeconds`, a throw, or a non-player Impulse hit past `CreatureStunDv`, PlayerCharacter.cs:320-323; first-wins, then immunity), hitstop via `HitstopSecondsFor`. `Launched` is set by `PlayerCharacter.Launch` (:416) and `RegisterThrown` (CombatState.cs:185); it is cleared in `PlayerCharacter.Update` (:751-753). `Thrown` is cleared in `Tick` when hitstun lapses.
Rollback: `CaptureState` writes the ECS `PlayerData` component (Sim/ECS/Components/EcsComponents.cs:241, deep-copied by its `DeepCopy` cloner): `_actionVars` by value, `_abilities.Clone()`, and the action as a registry INDEX (PlayerCharacter.cs:1064-1083); `RestoreState` reverses with `_abilities.CopyFrom` (:1088-1111).

## Invariants and traps
- Movement must not read action state (ActionStates.cs:78-79, enforced by convention only). Permitted channels out of actions: `ApplyMovementModifiers` (multiplicative `MovementModifiers`, ActionStates.cs:177) and `ApplyActionForces` / `Body.AppliedForce`/`Velocity` (:184). Actions may read movement: `ctx.CurrentMovement` (Guard via `DashState.Airborne` :2329, DashSlash :2481) or `ctx.PreviousState(0)` (CrouchSlash :1109).
- `ApplyActionForces` and `Update` freeze during hitstop but selection does not (PlayerCharacter.cs:765-778); a frozen action must keep its HitId alive (:998).
- Preemption is Passive(candidate) > Active(incumbent), strict (:933). Registry order only breaks equal passives (:923-932; comment at :543-545). Registry comments in PlayerCharacter.cs:546-573 can be stale (GuardRetaliate says 30/55, code 50/55; GrabbedSlash says 36/36, code 30/36); trust each class's `ActivePriority`/`PassivePriority`.
- `RecoveryAction.PassivePriority` is the mutable `_bid` (:284-287), reset and recomputed in every `CheckPreConditions`; never read it before that call. It is a plain, non-snapshotted field on the per-player registry instance; rollback-safe only because it is rewritten before every read within a Step (:281-283) and the lookahead skips `RecoveryAction`.
- `RecoveryIndex()` (EnvironmentContext.cs:120-131) returns null when `PreviousAction(0)` (last frame's action, from the history ring) is neither `RecoveryAction` nor a `NeutralForEntry` hub (`NullAction`, `BlockPaint`, `BlockPlace`). `EntryOk` (ActionStates.cs:139) folds hit disadvantage into the index; the `int.MaxValue` window (combo privilege) refuses while hit disadvantage > 0 (:144). `GrabbedSlash` passes `ignoreHitDisadvantage: true` (:4257).
- `GuardAction` does not use `EntryOk`; it uses `RecoveryIndex(includeHitDisadvantage:false)` (:2337) plus a 5-frame hit-disadvantage cap (`MaxEntrySeconds` :2319, checked :2338), so it fires out of any recovery. `GuardRetaliate` (no recovery gate at all), `Grab`, `BlockGrab`, `Burst`, `LobbedArea` have bespoke entry rules (:2534, :4017, :3674, :2778, :3487).
- Eviction lookahead mutates `ctx.CurrentFrame`/`ctx.LookaheadRecoveryIndex` and restores in `finally` (:378-407). Preconditions must be pure peeks (no `Consume`, no state writes) or the lookahead corrupts them. `Peek` is pure; `Consume` belongs in `Enter`.
- Grab lock-out is one gate: `AllowedWhileGrabbed` (:127), checked in the selection loop and the lookahead (:384). Only `NullAction`, `RecoveryAction` and `GrabbedSlash` opt in; a new action is locked out without any `BlocksAttack` check of its own.
- Charge handoff: `RecoveryAction.Exit` stamps `StabCharge` + `StabChargeFrame`; every consumer goes through `RecoveryAction.TakeCharge` (honors it only if <= 1 frame old, always zeroes it). `TakeChargeDamageBoost` is the damage multiplier Stab / `AirWhipKick` / `AirHammer` share (2x at the sweet spot, body damage capped at `MaxBodyDamageBoost` 2.5x with the dive boost or sweet spot); Burst takes the raw fraction for knockback/reach. A new hold-and-swipe action must call one of them in `Enter`.
- `CommitProfile` is the price the lookahead quotes; an action whose `Exit` stamps recovery should stamp the same number (:93-98). Stab/Kick/Hammer/Pulse call `StampRecovery(CommitProfile(...))` in `Exit`; slashes keep the default 0 quote while their `Exit` stamps `EndLag`. `StampRecovery` max-merges (ConditionState.cs:50-55); `SetFor`/`SetForSeconds` overwrite.
- Slash recovery: `EndLag` on whiff, `HitEndLag` when `vars.AttackConnected` (latched from `PeekHits` in `Update`, :856-858). Combo flags are set whether or not the slash connected (ex. :981-990).
- Hitstop is frame-neutral: `ApplyHitstop` pushes `Hitstun/Stun/GuardStart/GuardCharged/Slide` expiries (CombatState.cs:513-518) and `ConditionState.ShiftExpiries` pushes combo/recovery stamps (:63-71). Any new absolute expiry-frame field in `ConditionState` must be added to `ShiftExpiries`, `Tick` and `CopyFrom`.
- Snapshot: `ConditionState.CopyFrom` (:83), `CombatState.CopyFrom` (:693), `PlayerAbilityState.CopyFrom` (:54) and `BuildMeters.CopyFrom` (BuildMeters.cs:357) list fields by hand; a new field missing there silently fails rollback (capture uses `MemberwiseClone`, restore uses `CopyFrom`). `CombatState._hitstopShift` and `_tickDt` are deliberately not copied (:525, :588).
- `ActionVars` is a flat value struct; reference state must not go in it. Actions hold entity ids only (`PullPointId`, `GrabVictim`; ActionVars.cs:56-61, 88-95). Effects outliving release go into spawned entities (`MassBall` ActionStates.cs:3141-3154, `GrabThrowEntity` :4179, `PullPointEntity` :3764-3769).
- Instance fields on action objects are not snapshotted: `_trail` :778 and `_tipTrail` :1534/:1947 are render-only; `_hitShape` :756 is a sim hitbox shape but a constant cache (pure function of per-variant constants). Do not put mutable sim state there.
- Sim-affecting statics: Stab, AirWhipKick, BlockGrab and Grab read `MovementConfig.Current` (:1789-1798, :2062, :3741-3804, :4112). Hot-reload is gated off in multiplayer per CLAUDE.md. Hit ids come from `ctx.HitIds.Next()`, never a static.
- Armor (`ArmorProfile`) is compared against victim dv in px/s; a hit below it takes full HP damage and scaled knockback but never registers hitstun, so no flinch eviction (PlayerCharacter.cs:278-288). Stab armor is grounded-only (:1638); S3 armor ends after the active window (:1052).
- `LobbedAreaAction` (:3463) is commented out of the registry (PlayerCharacter.cs:567-570) but still needs an authored clip (`SkeletonStates/biped/lobbedarea.json` exists): `ClipBindingTests` enumerates every concrete `ActionState` in the assembly except its `NoOverlay` set (ClipBindingTests.cs:21-31). Its name is also referenced as a string by bot code (Net/Bot/Maneuvers/CrystalWaveManeuver.cs:51, WallUpManeuver.cs:52, Duelists/Rampart/RampartWallUp.cs:58, Duelists/Riposte/RiposteModel.cs:39), and `FrameData.LobbedAreaAction` (:64) feeds it.
- `DashSlash`'s header comment says "3f startup" (:2436); the value is `FrameData.DashSlash.Startup` = 8 frames (:45). Likewise `GuardRetaliateAction` says "4f/3f" (:2512) but startup is 8 (:52). Trust `FrameData`.
- `GrabAction` never latches a `Helpless` victim (Launched/Stunned/thrown hitstun; CombatState.cs:211, published on the hurtbox at PlayerCharacter.cs:208, skipped at ActionStates.cs:4058); grab ignores guard because the field bypasses `OnHit` (ActionStates.cs header at :3938-3939). Grab-strength comments saying a grab breaks on the 3rd struggle (:3958-3960, :4234) are stale: `GrabStrengthMax` 3 / `GrabStrengthDamage` 0.5 = 6 hits.
- Slash direction is hemisphere-clamped to `Facing` unless `AllowBackward` (:704, :836-843); `AirTurnSlash` and `AirWhipKick` flip `ab.Facing` in `Enter` (:1220, :1983).
- `Hitbox.Damage` is the TILE number; `BodyDamage` is HP (see `bodyDamage:` args, e.g. :880). Two boxes sharing a `HitId` dedupe per target in publish order (sweet spot first in Kick/Hammer; Stab adds a tiles-only block box, Burst splits TilesOnly/EntitiesOnly boxes).
- New `ActionState` subclass needs: a registry line in `PlayerCharacter` ctor (:546-573, else it never runs; registry order is the tie-break), and a clip whose `Type` equals the class name unless listed in `ClipBindingTests.NoOverlay` (an action that declines `AnimationProgress` needs a looping clip).
- Seed mode latches `vars.SeedMode` at the press; swapping bars mid-hold ends the action. A hold resumed after an attack preempted it, or pressed in the air, is not armed (no on-the-feet press edge) and plants nothing; a cancelled channel stays unarmed for the rest of the hold.
- Bots using the seed bar must end on the BLOCK bar: leave it on and every later RMB plants and Num keys pick seeds (`SeedBarReturnConstraint`, Net/Bot/Maneuvers/PlantManeuvers.cs:180).

## Where to change X
- Attack timing/end-lag: `FrameData` (ActionStates.cs:24); hitstun per move: the slash's `HitstunSecondsOverride`, Stab/Pulse consts.
- Slash shape/damage/knockback/launch/priority: the variant class (963-1389) or `SlashLikeAction` knobs (626-740); hitbox assembly in `SlashLikeAction.Update` (:845).
- Which input fires which move: that class's `CheckPreConditions`; gesture classification is `Character/Input/InputParser.cs`; buffer windows in `IntentBuffer.cs:107-113`.
- Chain/cancel rules: `OnExitSetFlags`/`OnEnterClearFlags` per slash, `ConditionState`, `EntryOk` (:139), `CommitProfile`, `RecoveryAction.LookaheadBestBid` (:372).
- Priorities / registration order: each class's `ActivePriority`/`PassivePriority` and `PlayerCharacter` ctor (:546-573).
- Armor / flinch / grab lock-out: `ArmorProfile` (:109), `RecoveryAction` flinch cases (:328-344), `AllowedWhileGrabbed` (:127).
- Hitstun/stun/hitstop/launch tuning, guard windows and constants: `CombatState` consts (:183-429), `ResolveGuard` (:608).
- Movement feel during an action: that action's `ApplyMovementModifiers`; hitstun air control scalars `CombatState.HitstunAccelScale/DragScale` (:428-429) applied in PlayerCharacter.cs:952-958.
- Build economy numbers / charge curve: `BuildMeters` consts and `Step` (:147); paint/place behavior in `BlockPaintAction` (:2977) / `BlockPlaceAction` (:3234).
- Grab hold/throw behavior: `GrabAction.Update` (:4110) / `Exit` (:4164); config knobs `PlayerGrab*` in `MovementConfig`.

## Tests
Under MTile.Tests/ (class names; `scripts/test-group.py` matches each term as a substring of the fully-qualified test name, method name included):
- `combat` group: `GuardTests`, `GrabTests`, `GrabLockoutTests`, `HitEvictionTests`, `RecoveryTransitionTests`, `CommitmentRecoveryStampTests`, `CommitmentSpectrumTests`, `CombatFeelTests`, `CombatHitstunTests`, `CombatClankTests`, `DirectDamageTests`, `AttackRecoilTests`, `AirHammerTests`, `AirWhipKickTests`, `BurstChargeTests`, `BurstPogoTests`, `DownAirSlashTests`, `DashTests`, `InputParserGestureTests`, `ClodGrabTests`, `GroundedForActionsTests`, `ClipBindingTests` (clip Type == class name), `TelegraphListTests`.
- `terrain` group (name contains `Block`): `BlockPaintTests`, `BlockPeelTests`, `BlockThrowTests`, `BlockChargeTests`, `BlockEruptionTests`, `ChargedBlockUseTests`.
- `simcore`: `SnapshotRoundTripTests` (Sim/SnapshotRoundTripTests.cs) round-trips the whole sim, but its scenarios only walk/jump and RMB-paint (BlockPaint + BuildMeters); no attack, guard or grab, and its probe compares position/velocity/state+action name/HP/terrain, not `ActionVars`/`ConditionState`/`CombatState` fields.
- `bot` group: `BotManeuver*Tests` drive these actions through the bot layer; some also match `combat` (`BotManeuverGrabTests`, `BotManeuverPulseGuardTests`, `BotManeuverSpikeGuardTests`) or `terrain` (`BotManeuverRangerBlockThrowTests`, `BotManeuverEruptionTests`).
- Run: `scripts/test-group.py combat` (add `terrain` for build actions, `simcore` for snapshot changes). `ClipBindingTests`' action-clip checks run under `combat`; `animation` catches only its `EveryAnimClip_*` method (via "Anim").
