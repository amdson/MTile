# Animation binding map

Which state plays which clip, and where reference arcs fit. Verified against the code on
2026-08-04; every claim has a file:line.

Four namespaces are in play and **they do not line up by name**:

```
MovementState.AnimationTag → AnimTag → (IMoveDriver) → AnimClip → clip file (by Type)
ActionState class name ─────────────────────────────→ clip file (by Type, exact ordinal)
ReferenceClips/<arc>.json ─── sim: flown by the state · editor: bound via clip ReferenceArc
```

**Selection is by the clip JSON's `Type` field.** Filename and the `Name` field are ignored
at runtime. An `AnimClip` enum member with no matching file **throws** at the first frame that
selects it, so enum values and clip files must land together — in **both** rigs, since the
binder filters on the clip's `Skeleton` field. `ClipBindingTests` guards this. Binding table:
[CharacterAnimator.cs:365-376](../Animation/CharacterAnimator.cs#L365-L376). A `Type` that
parses as an `AnimClip` enum member becomes a movement clip; anything else lands in
`_actionClips` keyed by the exact string.

## Movement states

Driver registry (first match wins):
[MoveDriver.cs:89-106](../Animation/MoveDriver.cs#L89-L106). Tag→clip is a hardcoded list,
not name matching.

| State | AnimTag | AnimClip | Clip file | Reference arc |
|---|---|---|---|---|
| `FallingState` | *None* | Fall / Jump (by v.y) | `fall.json` / `jump.json` | — |
| `StandingState` | *None* | Idle/Walk/WalkBack/Run/RunTurn/Land | speed fan-out, 6 files | — |
| `CrouchedState` | Crouch | Crouch / CrouchWalk / DuckUnder | 3 files | — |
| `DropdownState` | Dropdown | Dropdown | `dropdown.json` | **`dropdown`** (sim + editor) |
| `JumpingState` | *None* | Jump / Fall | `jump.json` | — |
| `RunningJumpState` | *None* | Jump / Fall | `jump.json` | — |
| `CoveredJumpState` | *None* | Jump / Fall | `jump.json` | — |
| `DoubleJumpingState` | DoubleJump | DoubleJumpFlip | `doublejumpflip.json` | — |
| `WallSlidingState` | WallSlide | WallSlide | `wallslide.json` | — |
| `WallJumpingState` | WallJump | WallJumpKick | `walljumpkick.json` | — |
| `LedgeGrabState` | LedgeGrab | Hang | `hang.json` | — |
| `LedgePullState` | LedgePull | LedgePull | `ledgepull.json` | **`ledge_pull`** (sim + editor) |
| `LedgeJumpState` | LedgeJump | LedgeJump | `ledgejump.json` | — |
| `StunnedState` | Stunned | Hitstun | `hitstun.json` | — |
| `TumbleState` | Tumble | Tumble | `tumble.json` | — |
| `ParkourState` | Parkour | Parkour + ClimbHands overlay | `parkour.json` + `climbhands.json` | `parkour` (editor only) |
| `MantleState` | Mantle | Mantle + ClimbHands overlay | `mantle.json` + `climbhands.json` | `mantle` (editor only) |
| `ArcJumpState` | ArcJump | ArcJump + ClimbHands overlay | `arcjump.json` + `climbhands.json` | `arcjump` (editor only) |

The four guided lip maneuvers (Parkour / Mantle / ArcJump / LedgePull) were **one shared clip**
until 2026-08-04. They are now four files that start as identical copies of the old shared clip,
so nothing changed visually — the point is that they can now be authored apart. All three climb
tags still route through `ParkourDriver`, so they keep the shared `ClimbHands` overlay and the
hand-grip pin; only the base clip differs. `ClipBindingTests` pins that they stay distinct.

Speed fan-out ([MoveDriver.cs:125-165](../Animation/MoveDriver.cs#L125-L165)): `|vx| ≤ 12`
→ Idle; `12 < |vx| ≤ 40` → Walk (WalkBack if against facing); `|vx| > 40` → Run (RunTurn if
against facing). `GroundGap > 2px` holds the cycle frozen. The Land override on a near-idle
touchdown is core-side ([CharacterAnimator.cs:409](../Animation/CharacterAnimator.cs#L409)).

Five states carry no tag — `Falling`, `Standing`, and all three single-jump states animate
purely off velocity through the terminal drivers.

## Action states

23 classes ([PlayerCharacter.cs:297-320](../Character/PlayerCharacter.cs#L297-L320)), all
`UpperBody`, all bound by **exact ordinal match of the CLR class name to the clip's `Type`**
([CharacterAnimator.cs:373](../Animation/CharacterAnimator.cs#L373),
[:731](../Animation/CharacterAnimator.cs#L731)). No aliases, no fallback.

`GroundSlash1` · `GroundSlash2` · `GroundSlash3` · `CrouchSlash` ·
`AirSlash1` · `AirSlash2` · `AirTurnSlash` · `StabAction` · `AirSpinStab` · `GuardAction` ·
`GuardRetaliateAction` · `PulseAction` · `BlockReadyAction` · `BlockEruptionAction` ·
`EnergyBallAction` · `BeamAction` · `GrenadeAction` · `GrabAction` · `GrabbedSlash` — each
has exactly one matching clip. `NullAction`/`ReadyAction`/`RecoveryAction` are excluded by
design.

`Region` is the bone mask ([OverlayStack.cs:170](../Animation/OverlayStack.cs#L170)):
UpperBody = the `chest` subtree. `OffRegionWeight` grades the legs instead of hard-masking
(guard 0.4, blockready/blockeruption/pulse 0.3).

**Overlay time**: each action reports `AnimationProgress(in ActionVars)` — normalized [0,1] —
and the overlay clip is remapped onto it, so the authored pose sweeps once over the activation
however long it really lasts. This is the action-side mirror of `MovementState.AnimationProgress`.
A **negative** return declines, and the animator falls back to the clip's own authored seconds;
that is correct only for a held, open-ended action whose clip loops (`GuardAction`).
`ClipBindingTests` enforces "reports progress OR loops", so an unpaced overlay can't
reappear silently.

Fixed-length actions are just `vars.TimeInState / Duration`. Two are phase-aware because their
real length is variable: `GrabAction` (hold → throw, so an early throw cuts to the throw pose
instead of never reaching it) and `BeamAction` (charge → fire, which together outlast the 0.6s
clip). `BlockReadyAction` and `LobbedAreaAction` track their *charge* rather than the clock, so
the pose holds at full once saturated.

Enemies have a **separate** action FSM (`Entities/EnemyActions.cs`) that never reaches
`CharacterAnimator` and has no clips at all.

## Reference arcs

An arc serves two unrelated purposes, and **only two arcs serve both**:

| Arc | Sim flies the body along it | Bound to a clip (authoring) |
|---|---|---|
| `ledge_pull` | ✅ `LedgePullState` ([LedgeStates.cs:363](../Character/LedgeStates.cs#L363)) | `ledgepull.json` |
| `dropdown` | ✅ `DropdownState` ([LocomotionStates.cs:316](../Character/LocomotionStates.cs#L316)) | `dropdown.json` |
| `parkour` | ❌ | `parkour.json` |
| `mantle` | ❌ | `mantle.json` |
| `arcjump` | ❌ | `arcjump.json` |

**The climb family's three arcs are authoring aids only.** `LedgePull` and `Dropdown` are
`ReferencePath`-driven and genuinely follow their arc; Parkour/Mantle/ArcJump are driven by the
ballistic corrector and would have to be migrated to `ReferencePath` to actually follow one. So
those three give you a trajectory to pose against — they do not change how the moves move, and
editing them changes nothing in game.

That split is also why only `ledge_pull` and `dropdown` are in `ReferenceClipRegistry.Names`:
that registry is the SIM's, loaded into the deterministic step. Editor-only arcs live purely as
files, which the ref-clip editor and the animation editor's `A` picker both discover by scanning
`ReferenceClips/`. Nothing at runtime reads a clip's `ReferenceArc` field.
See [ANIMATION_STRETCH_AND_REFERENCE.md](ANIMATION_STRETCH_AND_REFERENCE.md) §4.

Placeholder sizes, chosen to match each state's real rise band — retune freely, the shape is
what matters: `mantle` 18×16px (flush one-block step), `arcjump` 34×32px (two-block launch with
the apex above the gate). `parkour`'s box is 26×40px, which is roughly 2× `ParkourState`'s actual
8–20px band — worth shrinking when that clip gets authored for real, since the animation
editor previews an arc at its authored pixel size.

## "Vault" is retired (2026-08-04)

`Vault` used to mean three unrelated things at once — the shared clip, the move now called
`ParkourState`, and a family of config prefixes. All three are gone from the source; the word
survives only in sim test *fixture* names (`VaultCourse`, `HoldRight_VaultOneBlock`,
`Vault_DtInvariantDelivery`, the `MTile.Bench` course), which are deliberately left alone as
the tuning vocabulary. The rename map:

| Was | Now | Note |
|---|---|---|
| `AnimClip.Vault`, `vault.json` | `AnimClip.Parkour`, `parkour.json` | `ParkourState`'s own clip |
| `VaultHands`, `vaulthands.json` | `ClimbHands`, `climbhands.json` | shared by all three climb tags |
| `ReferenceClips/vault.json` | `ReferenceClips/parkour.json` | arc name `parkour` |
| `VaultLiftForce` / `VaultPushForce` | `LipLiftForce` / `LipPushForce` | climb family **and** `LedgePullState` |
| `MaxVaultTime` | `MaxLipManeuverTime` | bail timeout for both |
| `CorrectorVaultEnabled` / `CorrectorVaultTriggerDistance` | `CorrectorClimbEnabled` / `CorrectorClimbTriggerDistance` | gates only the three climb states ([ClimbStates.cs:59](../Character/ClimbStates.cs#L59)) |
| `VaultKickForward` / `VaultKickUp` | `ParkourKickForward` / `ParkourKickUp` | `ParkourState` entry impulse |
| `VaultAutoFireSpeed` | *(deleted)* | declared, never read |
| `MinVaultHeightTiles` / `MaxVaultHeightTiles` | `MinClimbHeightTiles` / `MaxClimbHeightTiles` | private to `ExposedUpperCornerChecker` |
| `VaultGripSolverTests` | `ParkourGripSolverTests` | |

No `movement_config.json` key had to change — none of the renamed properties were overridden
there. **There is no `VaultState` class and never was**: the at-speed one-block climb is
`ParkourState` ([ClimbStates.cs:299](../Character/ClimbStates.cs#L299)) — rise band
`MantleMinRise`..`MantleMaxRise` (8–20px), entry speed **above** `MantleMaxEntrySpeed`
(60 px/s). `MantleState` is its exact complement at or below that speed, so precisely one of
the pair bids per frame; `ArcJumpState` takes the taller band and additionally wants Up held
plus `ArcJumpRunSpeed`.

Arc names still don't track clip names: `dropdown` the arc ↔ `dropdown.json` the clip (same
name, different kinds of thing); `ledge_pull` the arc ↔ `ledgepull.json` the clip (same move,
different spelling).

## Known dead / broken bindings

- **`lobbedarea.json`** — `LobbedAreaAction` exists but its registration is commented out
  ([PlayerCharacter.cs:317](../Character/PlayerCharacter.cs#L317)), so the clip can never play.
  Kept deliberately (dormant, not rot); it reports progress so re-enabling it just works.

**Fixed 2026-08-04** — `wave.json` (`Type: "Misc"`, matched nothing) and
`SkeletonStates/biped/old/` (never loaded — `LoadAll` is non-recursive,
[AnimationDocument.cs:124](../Animation/AnimationDocument.cs#L124)) were deleted;
`slash1.json` was renamed to `groundslash2.json` to match its `Type`; and the overlay-pacing
gap (Guard, Pulse, BlockReady, BlockEruption, EnergyBall, Beam, Grenade, Grab all running on
the clip's own clock) was closed by `ActionState.AnimationProgress`.

`biped_rabbit/` mirrors `biped/` on every selection-relevant field; only poses differ.
