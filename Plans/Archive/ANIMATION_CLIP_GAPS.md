# Animation clip gaps — worklist

Inventory of registered, reachable movement states and action states cross-referenced against
the clips that exist in `SkeletonStates/`. "Gap" = the state/action has no dedicated clip, so it
either animates **wrong** (falls through to a generic clip) or plays **no pose at all** (an action
whose overlay lookup finds nothing). Generated 2026-06-24.

## How a clip is chosen (so the gaps make sense)

- **Movement → base clip**: `CharacterAnimator.SelectClip` maps `MovementState` (the state class
  name, via substring) + grounded/velocity to one `AnimClip`. An unmapped state falls through to
  a **generic** clip (Idle / Jump / Fall by vertical velocity). Adding a clip for a soft-gap state
  therefore *also* needs a `SelectClip` branch before the new clip is ever picked.
- **Action → overlay clip**: `CurrentActionName` (the action class name) is looked up in
  `_actionClips`, which is keyed by each loaded clip's `Type`. So an action animates iff a
  `SkeletonStates/*.json` exists with `Type` == the action's class name (and `Region`/skeleton
  match). No clip ⇒ the overlay binds nothing ⇒ the attack fires with the character still in its
  locomotion pose. `Null`/`Ready`/`Recovery` actions are excluded by design (no overlay).

## Existing clips (for reference)

Idle, Walk, WalkBack, Run, Crouch, Jump, Fall, Vault (+ VaultHands overlay), WallSlide,
GroundSlash1/2/3, CrouchSlash, AirSlash1/2, AirTurnSlash, GuardRetaliateAction, GrabbedSlash,
StabAction, PulseAction, EnergyBallAction. (`wave.json` = Misc, unused.)

---

## Action gaps (registered actions with no clip → attack plays with NO pose)

| Action | Priority | Notes |
|---|---|---|
| `AirSpinStab` | **High** | Air backward-swipe stab — a core melee move with no pose at all. |
| `GuardAction` | **High** | Parry/guard posture (Shift held). A held defensive stance reads as "broken" with no pose. |
| `GrabAction` | Med | Grab → throw (the grabber). The *grabbed victim* has `GrabbedSlash`; the grabber has nothing. |
| `BlockReadyAction` | Med | RMB drag-build / hold-in-solid charge stance. |
| `BlockEruptionAction` | Med | Fires the block eruption on release. |
| `BeamAction` | Med | Shift+LMB hold — sustained beam after charge. |
| `GrenadeAction` | Low | F press — throw a sticky grenade (wants a throw windup). |

(`LobbedAreaAction` is commented out of the registry — not live; skip until re-enabled.)

Covered already: all slashes, `StabAction`, `PulseAction`, `EnergyBallAction`, `GuardRetaliateAction`.

---

## Movement gaps

### Hard gaps — currently animates *wrong*

| State | Plays today | Wants | Notes |
|---|---|---|---|
| `StunnedState` | Idle / Jump / Fall (by velocity) | **Hitstun / stagger** | Heavy-hit lockout; reads as normal idle/air. |
| `TumbleState` | Jump / Fall | **Tumble / knockdown-roll** | Airborne tumble after a launch. |
| `LedgeGrabState` | **Fall** (explicit placeholder) | **Hang** | `SelectClip` maps LedgeGrab→Fall "until a Hang clip exists" — a written TODO. |

### Soft gaps — reuses a generic clip, acceptable but unpolished

Each also needs a `SelectClip` branch to pick the new clip.

| State | Reuses | Dedicated clip would be |
|---|---|---|
| `WallJumpingState` | Jump | wall **kick-off** |
| `DoubleJumpingState` | Jump / Fall | **flip / second-jump** |
| `RunningJumpState` | Jump | **running** jump (longer/flatter arc) |
| `CoveredJumpState` | Jump / Fall | **ducked** jump (under a low ceiling) |
| `LedgePullState` | Vault | **mantle / pull-up** (borrows the vault shape today) |
| `LedgeJumpState` | Jump / Fall | ledge **launch** |
| `DropdownState` | Fall / Idle | **drop-through** a platform |

Fine as-is (no clip needed): `FallingState`→Fall, `StandingState`→Idle/Walk/WalkBack/Run,
`CrouchedState`→Crouch, `WallSlidingState`→WallSlide, `ParkourState`→Vault.

---

## Suggested order

1. **Hard gaps first** — `AirSpinStab`, `GuardAction`, and `StunnedState`/`TumbleState`/`LedgeGrabState (Hang)`.
   These are the ones that visibly animate wrong or not at all.
2. **Remaining action gaps** — Block/Beam/Grenade/Grab: attacks firing with no pose.
3. **Soft movement gaps** — pure polish; the generic clip reads acceptably. Bundle each new clip
   with its `SelectClip` branch.

Authoring uses the probe workflow (`.claude/skills/anim-probe`), the way `wallslide`/`vaulthands`
were made: pose the rig, run `MotionProbeTests`, read the digest, iterate. Action clips are
`Region: "UpperBody"` overlays keyed by `Type` == the action class name; a new movement base clip
is a full-body clip keyed by `Type` == its `AnimClip` enum name (+ a `SelectClip` branch + possibly
a new `AnimClip` enum value).
