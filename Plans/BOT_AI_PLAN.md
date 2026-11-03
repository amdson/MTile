# Bot AI Plan — a P2 opponent that plays through raw input

**Status (2026-11-02):** stage 1 built. `Net/BrawlerBotInputSource.cs` walks up to P1,
jumps steps, and slashes; it is what Game1 hands an offline second player.
`Net/BotInputSource.cs` stays as the seeded-random chaos monkey for sim bring-up tests.
Scenario tests: `TwoPlayerStepTests.Brawler_*`.

## Architecture — decided: bots emit raw `PlayerInput`, nothing else

A bot is an `IRemoteInputSource`: once per new frame it reads the sim and returns the
same `PlayerInput` record a keyboard + mouse produces — held `Left/Right/Up/Space`, an
`LeftClick` bit, and `MouseWorldPosition` as the cursor. That record is buffered as P2's
input and replayed from the buffer during rollback; bot RNG/timers live outside the sim
and are never snapshotted.

**Attacks are performed as gestures**, exactly as a human performs them, and decoded by
the unchanged `InputParser`:

| Want | Input to synthesize |
|---|---|
| Slash (`Click`) | LMB down for 1..`ClickMaxHoldSeconds` frames, cursor on the target |
| Stab | LMB held > `ClickMaxHoldSeconds`, cursor swept ≥ `StabSwipeThreshold` px (player-relative) toward the stab direction, release |
| Pulse (`Circle`) | LMB held while the cursor sweeps ≥ 270° around the press point at ≥ `MinCircleRadius` |
| Jump / dash | `Space` press edge / `Space` + `Shift` chord |
| Eruption, grenade, … | the raw button (`RightClick`, `F`, …) — no parser involved |

> **Superseded decision.** An earlier version of this plan rejected gesture-miming in
> favour of bot-only virtual attack buttons (`AttackSlash/Stab/Pulse` on `PlayerInput`)
> and a per-player `IIntentParser`/`DirectIntentParser` — an easier-to-program bot API.
> That is reversed: no new input fields, no parser seam. Bots use the human input
> surface, so whatever a bot can do a player can do with the same timing, and the
> gesture layer gets exercised by every bot match. The cost — bots must know the gesture
> thresholds — is owned by the bot (a small helper per gesture), not by the sim.

Because gestures carry natural windup (a stab needs its hold), part of the fairness
budget comes for free; the remainder is explicit dials below.

## Bot internals (two layers, evaluated each `Poll`)

### 1. Perception

Cheap derived facts, read-only, computed at the top of `Poll`:

- `toTarget = sim.Player.Body.Position - self.Body.Position`; `dx`, `dy`, `dist`.
- `selfGrounded = self.IsGrounded`.
- **Stale target** for fairness: ring buffer of target positions; aim at the
  sample from `ReactionFrames` ago (~4–8). The single most effective "feels
  fair" dial — the bot whiffs when you dodge late.
- **Stuck detector**: pressing a horizontal direction but `|Velocity.X|` below
  epsilon for ~8 consecutive frames ⇒ blocked by terrain.

No pathfinding. Terrain is destructible and constantly reshaped, so reactive
movement (stuck → jump; still stuck → slash the wall) is both simpler and
on-theme: the bot digs through obstacles like the player does.

### 2. Brain FSM

Decision tick every ~6 frames so intent reads as behavior, not per-frame noise:

| State | Enter when | Behavior |
|---|---|---|
| `Pursue` | `dist > EngageRange` (~120 px) | Move toward target; jump logic active. |
| `Engage` | `dist ≤ EngageRange`, attack off cooldown | Pick an attack, wait `TelegraphFrames` (aim already committed), perform its gesture (table above). |
| `Backoff` | Just attacked, or `dist < CrowdRange` (~30 px) while on cooldown | Move away ~15–25 frames. Prevents face-hugging; creates learnable rhythm. |
| `Breach` | Stuck detector fired twice while pursuing | Slash aimed at the terrain in the movement direction; resume `Pursue`. |

Global attack cooldown (randomized 24–45 frames ≈ 0.8–1.5 s) gates `Engage`.

Locomotion (within Pursue/Backoff):

- **Walk**: `Right = dx > Deadzone`, `Left = dx < -Deadzone`, `Deadzone ≈ 24 px`.
- **Jump toward** (grounded only): target above by > ~32 px, or stuck detector
  fired, or (optional polish) a 1-tile gap probe ahead via `WorldQuery`. Hold
  `Space` scaled to needed height; keep holding the direction in air for air
  control.
- **`Up`** when hugging a wall with the target above (ledge/climb intent).

Attack selection — weighted random *within the current range band*, so "random"
never picks nonsense:

- `dist < 50`: slash 60 %, pulse 40 % (pulse weight ↑ if recently hit — its
  knockback is the crowd-clearing panic button).
- `50 ≤ dist < 120`: stab 70 %, slash-after-walk-in 30 %.

Aim error: one `Vector2` offset rolled per attack (magnitude `AimError`),
applied to the aim point for the attack's whole duration — wrong consistently,
not wobbly.

Tier 2 (later, no new machinery): `F` edge → `GrenadeAction` at long range;
`Shift+RMB` → `LobbedAreaAction`.

## Difficulty dials (one config struct)

| Dial | Effect | "Moderate" default |
|---|---|---|
| `ReactionFrames` | staleness of aim/decisions | 5 (~167 ms) |
| `TelegraphFrames` | wind-up between decision and attack press | 6 (~200 ms) |
| `AttackCooldownRange` | rhythm between attacks | 24–45 frames |
| `AimError` | px of per-attack aim offset | 14 |
| `Aggression` | P(engage vs. backoff) when in range off cooldown | 0.7 |
| `EngageRange` | how far it starts attacks | 120 px |

## Files

- `Net/BrawlerBotInputSource.cs` — stage 1: chase (stale-target aim), stuck ⇒ jump,
  target-above ⇒ jump, in range ⇒ 1-frame LMB tap at the target. Dials as public fields
  (`EngageRange`, `VerticalReach`, `CooldownMin/Max`, `ReactionFrames`).
- Next: stab/pulse gesture helpers (hold + swipe, hold + circle) in the bot, then the
  brain FSM above (Engage/Backoff/Breach) layered on the same input output.
- `Net/BotInputSource.cs` — keep as the dumb baseline.

## Tests

1. **Headless scenarios** (`TwoPlayerStepTests`): flat walled arena, stationary P1,
   brawler 200 px away — first hit inside 240 frames and ≥ 1.5 HP over 600; P1 on a
   2-tile step — the bot climbs and lands a hit. Plus a determinism re-run.
2. **Rollback**: nothing bot-specific — bot output is plain `PlayerInput`, covered by
   the existing two-player snapshot/restore test.
3. Later, per gesture helper: the synthesized input stream decodes to the intended
   intent through a real `InputParser` (one intent per gesture, right stab direction).

## Non-goals (for now)

- No pathfinding / navmesh — reactive movement + breach only.
- No blocking/guarding, dodge reads, or adaptive difficulty.
- No projectile leading beyond stale-position aim.
