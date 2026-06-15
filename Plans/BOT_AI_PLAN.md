# Bot AI Plan — a moderately challenging P2 opponent

Goal: upgrade the stub `BotInputSource` (seeded-random walk + loosely aimed pokes)
into an opponent that pursues the player, jumps toward them, and picks among
several attacks — challenging but readable/fair, with explicit difficulty dials.

## Architecture

### The bot stays outside the sim…

Keep the existing seam: the bot is an `IRemoteInputSource` polled once per *new*
frame; its output is buffered as P2's input and replayed from the buffer during
rollback. Bot RNG/timers are never snapshotted. All sim influence flows through
the per-frame input record.

### …but it does NOT mime human gestures

The stub's model — synthesize mouse swipes/circles so `InputParser` can decode
them back into intents the bot already knew it wanted — is a pointless
encode/decode round-trip, and brittle against gesture-threshold tuning. The bot
should produce intents directly.

The constraint that shapes *how*: intents must be **derived in-sim from the
buffered input**, not injected from outside. `InputParser.Detect` runs inside
`PlayerCharacter`'s update and its state is snapshotted — that's what makes
intents recompute correctly when a rollback re-steps from the input buffer. An
intent pushed into `IntentBuffer` at poll time would vanish (or double-fire) on
replay, because the loop replays buffered inputs and never re-polls the source.

So: the per-frame input record stays the rollback/replay unit, and we widen it
with **virtual attack buttons** that only bots set:

```csharp
// PlayerInput additions
public bool AttackSlash;   // → IntentType.Click
public bool AttackStab;    // → IntentType.Stab (direction from aim point)
public bool AttackPulse;   // → IntentType.Circle
```

Inside the sim, the gesture-classification layer becomes pluggable per player:

- **Human players** keep `InputParser` (gesture decoding, unchanged).
- **Bot players** get a `DirectIntentParser` (~30 lines): press-edge on a virtual
  button (cur vs `controller.GetPrevious(1)`, same pattern RightClick actions
  already use) ⇒ issue the corresponding intent. Stab `Direction` is computed
  at parse time as `normalize(MouseWorldPosition - body position)` — pass the
  body position into `Detect` (InputParser ignores it). Deriving everything from
  the controller ring keeps the direct parser stateless, so it needs no snapshot.

Seam: an `IIntentParser` interface over `Detect/Capture/Restore`; `PlayerCharacter`
takes which one at construction.

What this does *not* change: movement is still held keys (`Left/Right/Space/Up`)
read continuously by movement states — the bot moves under the same physics and
input constraints as a human. `MouseWorldPosition` remains load-bearing as the
**aim point** (slash/stab/eruption all read it directly for aiming); the bot sets
it to its target every frame. Raw-input actions (RightClick eruption, `F`
grenade) already need no parser and work as-is.

GGPO note: `PlayerInput` is what will cross the wire; three extra bools are
noise size-wise, are simply never set by human sources, and mean rollback needs
no bot-specific side channel.

Tradeoff to own: gesture timing imposed a natural windup (a stab required an
8-frame hold). With direct intents that telegraph disappears, so fairness moves
into the brain as an explicit `TelegraphFrames` delay before each attack —
better anyway: a dial instead of an emergent artifact.

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
| `Engage` | `dist ≤ EngageRange`, attack off cooldown | Pick an attack, wait `TelegraphFrames` (aim already committed), press its virtual button for 1 frame. |
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

- `Character/Controller.cs` — add `AttackSlash/AttackStab/AttackPulse` to
  `PlayerInput` (humans never set them).
- `Character/DirectIntentParser.cs` — new; the trivial in-sim button→intent
  frontend. `IIntentParser` interface extracted over it + `InputParser`;
  `PlayerCharacter` picks per player at construction.
- `Net/CombatBotInputSource.cs` — new `IRemoteInputSource`: perception + FSM +
  locomotion (~150 lines).
- `Net/BotInputSource.cs` — keep as the dumb baseline (useful chaos-monkey for
  sim bring-up tests).

## Tests

1. **Parser unit test** (pure): inject virtual-button frames into a `Controller`,
   run `DirectIntentParser.Detect`, assert exactly one intent per press edge,
   correct stab direction, no re-fire while held.
2. **Rollback regression**: extend `SnapshotRoundTripTests` / `RollbackHarnessTests`
   coverage to a bot-driven P2 — attack intents must replay identically from the
   input buffer after a restore (this is the property the whole design hangs on).
3. **Headless scenario** (`MTile.Tests/Sim`, alongside `TwoPlayerStepTests`):
   flat ascii terrain, stationary P1, bot P2 spawned 200 px away — assert the
   bot closes to `EngageRange` and P1's health drops within N frames. A second
   scenario with P1 on a 2-tile ledge exercises jump-toward/breach.

## Non-goals (for "moderately challenging")

- No pathfinding / navmesh — reactive movement + breach only.
- No blocking/guarding, dodge reads, or adaptive difficulty.
- No projectile leading beyond stale-position aim.
