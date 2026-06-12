# Ledge Pull Input Matrix

Systematic enumeration of input combinations during `LedgePullState`, with current
behavior (as of the uncommitted animation-states work) vs. proposed behavior under the
**release → re-grab** + **jump intent queuing** design. Companion to
`Plans/ledge_vault_design.md`.

## Setup & vocabulary

The player hangs on a ledge (`LedgeGrabState`, priority 42/42), wall on side
`wallDir`. Pressing Up transitions to `LedgePullState` (43/43). Directions are
relative to the ledge:

- **inward** = horizontal input toward the wall/ledge (`Input.Left/Right == wallDir`)
- **outward** = horizontal input away from the ledge

The pull has two physical phases (`LedgePullState.Update`):

| Phase | Body position | Forces |
|---|---|---|
| **P1 rising** | below standing height (`Y >= cornerTop − 2R`) | `−VaultLiftForce` up; ramp constraint steers around the lip |
| **P2 cresting** | above standing height | vertical brake (`min(−vy/dt, 2×VaultLiftForce)`) + inward push (`VaultPushForce`) |
| **Done** | at standing height AND past corner horizontally | `CheckConditions` false → exit on top (Falling → Standing same frame) |

Priority context for what can preempt the pull (passive must exceed pull's active 43):

| State | Passive | Preempts pull? |
|---|---|---|
| WallJumping | 45 | **Yes** — Space + any horizontal, while wall detected (P1 only) |
| DoubleJumping | 40 | No |
| LedgeGrab | 42 | No (re-entry only via exit fallback + selection) |
| Stunned | 25 | No — a hit mid-pull does not break the state (knockback impulses still perturb the body physically) |

## Hang state (`LedgeGrabState`) — for context

The hang becomes the hub once "release → re-grab" lands, so its own input table:

| Input at hang | Current | Proposed | Notes |
|---|---|---|---|
| neutral / inward held | hang (spring holds at hang point) | unchanged | |
| Up press | → LedgePull | unchanged | |
| Down hold (after 0.1 s grace) | drop → Falling, ~0 velocity | unchanged | grace exists for drop-in entries (`MovementStates.cs` ~804) |
| outward hold | drop → Falling | unchanged *except* a short grace after re-grab (see row F below) | |
| Space alone | nothing (DoubleJump 40 < grab 42) | nothing (intent expires unconsumed) | open question: should jump-at-hang do something? |
| Space + horizontal | WallJump away (45 > 42) | unchanged | |

## Pull input matrix

"Release" rows assume Up goes up→released mid-pull; "hold" rows assume Up stays held.
Unless a phase is named, the row applies to both P1 and P2.

| # | Input during pull | Phase | Current behavior | Proposed behavior | Status |
|---|---|---|---|---|---|
| A | hold Up, neutral | → Done | completes; crest brake kills vy; stands on top | unchanged | ✅ good |
| B | hold Up + inward | → Done | completes, runs onto platform | unchanged | ✅ good |
| C | hold Up + outward | both | horizontal ignored; pull continues | unchanged — Up is the commitment; outward alone does not cancel | decision: confirmed |
| D | **release Up, neutral** | P1 | exit → Falling with full upward vy → **looks like a free jump** | re-grab (path C) → hang spring (`GrabSpringK/GrabDamping`, overdamped) absorbs vy → settles at hang | 🐞 the bug |
| E | release Up, neutral | P2 | exit → Falling, vy partially braked | re-grab → sags back down to hang | 🐞 same bug, milder |
| F | release Up + outward | both | Falling with upward vy **plus** outward air control — worst exploit | re-grab; extend the existing 0.1 s entry grace to also cover `pressingAway` on re-grab entries, so the damper eats most vy before the away-drop fires. Exit then reads as a small wall-kick | 🐞 + decision: grace length |
| G | release Up + Down | both | Falling with upward vy, then fast-fall force (up-then-slam, reads wrong) | re-grab → 0.1 s grace → Down still held → clean drop from hang at ~0 velocity = climb-down | 🐞 fixed for free |
| H | release Up + inward | both | Falling, drifts back into wall (may wall-slide) | re-grab → settles at hang (inward is the natural "keep holding" input) | 🐞 fixed for free |
| I | Space, no horizontal (Up held) | both | **input lost** — DoubleJump blocked (40 < 43), edge expires before pull ends | `IntentType.Jump` queued; pull keep-alives it; on Done, buffered intent fires a normal ground jump off the ledge top (source-relative vy per `JumpingState.Enter`, so no super-jump) | ✨ new |
| J | Space + outward (Up held) | P1 | WallJump fires immediately (passive 45 > 43, wall detected) → kicks away, cancels climb | unchanged — deliberate bail-out; WallJump consumes the Jump intent | decision: keep |
| J′ | Space + outward (Up held) | P2 | nothing (body above lip, wall no longer detected) | intent queues → jump on top at Done | ✨ new |
| K | Space + inward (Up held) | P1 | WallJump **away** fires (precondition accepts either horizontal) — counterintuitive: pressing toward the ledge ejects you from it | gate WallJump so that during pull only *outward* + Space bails; inward + Space queues → jump on top | decision: change |
| L | release Up + Space together | both | Falling w/ vy; Space does nothing (no wall after exit frame, DoubleJump blocked until Falling active… actually DoubleJump can fire next frame from Falling) → effectively free-jump + double-jump available | re-grab; queued Jump intent at hang has no consumer → expires (see hang-table open question) | ⚠ decide with hang-jump question |
| M | hit by attack mid-pull | both | Stunned (25) cannot preempt; pull continues but knockback impulse fights the lift/constraints; stun applies after exit | unchanged for now | known, acceptable |
| N | hold Up but blocked (timeout `MaxVaultTime` 0.5 s) | P1 | exit → Falling with whatever vy the stall left | re-grab → failed vault sags back to hang | ✨ falls out of path C |

## Decisions to confirm

1. **Row C** — outward alone never cancels the pull (only releasing Up does). *Recommended: yes.*
2. **Row F** — extend the re-grab entry grace (~0.1 s, same constant as the Down grace) to `pressingAway`, so release+outward can't skip the damper. *Recommended: yes.*
3. **Row J** — keep Space+outward as an instant wall-jump bail-out mid-pull. *Recommended: yes.*
4. **Row K** — make Space+inward queue instead of wall-jumping away. *Recommended: yes.*
5. **Rows L / hang-Space** — should a queued/pressed jump at the hang do anything (e.g. auto-pull-then-jump, or neutral wall-hop)? *Currently: nothing. No recommendation yet.*

## Known edge cases (out of scope for this change)

- **Corner tile destroyed mid-pull/hang**: `GrabbedCorner` is a stored position and the
  pins are floating constraints (`PointForceContact`/`FloatingSurfaceDistance`), not
  tied to live tiles — the player keeps climbing a phantom corner. Needs a
  `ChunkMap.OnTileBroken`-driven invalidation eventually.
- **Rollback**: re-grab path C reads only `PreviousState(1)` + `abilities.GrabbedCorner`
  (both snapshotted); Jump intents live in `IntentBuffer` (snapshotted via
  `Capture/Restore`). No new snapshot surface expected.
