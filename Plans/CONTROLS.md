# Player controls reference

Generated alongside the Phase 8 ranged-attack rollout. Reflects what's wired in
the action + movement FSMs today; update when bindings change.

## Movement (keyboard)

| Input | Result | Notes |
|---|---|---|
| **A / D** or **← / →** | Walk left/right | Driven by `StandingState`/`FallingState` air-control |
| **W** or **↑** | Pull up onto ledge | While `LedgeGrabState` — enters `LedgePullState` |
| **S** or **↓** | Crouch (ground) / drop through (one-way platforms) | `CrouchedState`; `DropdownState` on edges with ↓+Space |
| **Space** | Jump | Context-sensitive: `JumpingState`, `RunningJumpState` if moving fast, `WallJumpingState` from wall-slide, `DoubleJumpingState` second tap in air, `CoveredJumpState` under low ceilings |
| (passive) | Wall slide | `WallSlidingState` — auto-engages when airborne, pressing into wall |
| (passive) | Vault / parkour | `ParkourState` — auto-triggers walking into low corners with direction held |
| (passive) | Ledge grab | `LedgeGrabState` — auto-catches falling player near a ledge |
| (passive) | Stunned | `StunnedState` — set by `CombatState.StunActive` after a hard hit (movement muted, jumps/attacks gated off) |

## Combat — melee (LMB, no Shift)

| Input | Action | Notes |
|---|---|---|
| **LMB press** | `ReadyAction` (wind-up) | Brief pre-attack pose, sets up the gesture parse |
| **LMB short tap** (≤ 6 frames) toward facing | `GroundSlash1` → `GroundSlash2` → `GroundSlash3` | Chain via combo flags; ground only |
| **LMB short tap** in air | `AirSlash1` → `AirSlash2` | Air combo |
| **LMB short tap** while crouched | `CrouchSlash` | Lower arc, no combo |
| **LMB short tap** on opposite side of facing, in air | `AirTurnSlash` | Flips facing on Enter; sticky-air-facing override |
| **LMB hold + swipe** (≥ 7 frames + ≥ 12 px) | `StabAction` | Direction = swipe vector; carries momentum along stab dir |
| **LMB hold + backward swipe**, in air | `AirSpinStab` | Air backward variant of stab; flips facing |
| **LMB hold + circular drag** (≥ 270° sweep) | `PulseAction` | Radial ring AOE |
| **LMB click** during `GuardCharged` window | `GuardRetaliateAction` | Cyan fast counter; consumes the charge |

## Combat — defense (Shift)

| Input | Action | Notes |
|---|---|---|
| **Shift held**, no L/R | `GuardAction` | Parry posture; slows movement; in-cone weak hits charge `GuardCharged` |
| Shift held **+** L/R | (Guard suppressed) | Movement wins — guard only activates when standing still |

## Combat — ranged (Shift + LMB / RMB / F)

| Input | Action | Notes |
|---|---|---|
| **Shift + LMB tap** (release ≤ 6 frames) | `EnergyBallAction` | Light-cyan piercing projectile toward cursor |
| **Shift + LMB hold** (≥ 0.35s charge) | `BeamAction` | Magenta sustained beam ≤ 0.55s; release early = no fire |
| **Shift + RMB hold + release** (≥ 0.4s) | `LobbedAreaAction` | Sienna ballistic ball → mass-ball eruption + radial AOE at landing |
| **F press** | `GrenadeAction` | Olive ballistic grenade, sticks on contact, 1.2s fuse → radial AOE |

## Building / world editing (RMB, no Shift; number keys)

| Input | Action | Notes |
|---|---|---|
| **RMB hold + drag** (no Shift) | Drag-place tiles | `Game1.HandleBuildInput` — places `_activeBlockType` along cursor sweep |
| **RMB hold ≥ 1.0s + release** (anywhere, no Shift) | `BlockReadyAction` → eruption | Charge-anywhere block eruption; cancels if HandleBuildInput places a tile within 10 frames |
| **1 / 2 / 3 / 4** | Pick Sand / Dirt / Stone / Foam | `_activeBlockType` (also drives eruption/lobbed-area material) |

## Action priority cheatsheet

The FSM picks the highest-passive-priority action whose precondition matches.
Active priority is what the *current* action holds; another action must exceed
it to preempt.

```
GuardRetaliate    30 / 55   highest preempt — counter beats everything
GroundSlash2/3    30 / 50
AirSlash2         30 / 50
Recovery          40 / 45
EnergyBall/Beam   40 / 45
Grenade/Lobbed    40 / 45
Guard             35 / 40
AirTurnSlash      30 / 35
AirSpinStab       30 / 35
CrouchSlash       30 / 32
GroundSlash1      30 / 30
AirSlash1         30 / 30
Stab              30 / 30
Pulse             30 / 30
Ready             10 / 15
BlockReady         8 / 10
Null               0 /  0
```
