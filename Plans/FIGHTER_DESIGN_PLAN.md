# Fighter Design Plan — parametrized NPC fighters with costed features

**Status:** phases 1–4 implemented on branch `fighter-plan` (2026-09-30), plus an interim
in-library arena (`FighterArena`, the §8.1 shape) and the AI designer as `MTile.Bench
--forge`. §§10–13 — the fightbox, the ledger and ratings, `MTile.Forge` intake, the
benchmark protocol — are not started; see §16 for what exists and what the reconciliation
with those sections needs. Originally written against commit `1ef80d7`. §§1–9 are the
fighter model and its phases; §§10–13 (added the same day) are the arena, the rating
system, the submission pipeline, and the benchmark protocol built on top of it.

**One-liner:** make a game out of *designing* fighters. A fighter is a data spec —
attributes, an action list, and its own brain — priced by a cost model and compiled to the
existing `EnemyBlueprint`. Humans and search algorithms both act as designers; the
deterministic headless sim is the test bench that scores them.

## Decisions already taken

| Question | Answer | Consequence |
|---|---|---|
| Whose chassis? | **NPCs only.** The player stays the bespoke protagonist. | Build on `EnemyEntity` + `EnemyBlueprint`. `PlayerCharacter`, the corrector, and the impact stack are untouched — their body constants are read-only (CLAUDE.md). |
| Who pilots? | **The brain ships with the fighter**, as code. | An `EnemyController` subclass is part of the fighter package. No human piloting path; no `PlayerInput` parsing for fighters. |
| How rich are the controls? | **Simple.** Left/right, jump, fly if bought, aim, and an explicit action choice. | No lattice planner or corrector for bots — that is the player's locomotion and it is both expensive and calibrated to one body. |
| How are costs derived? | **Physics-derived first**, with room for other cost types. | Mass is the spine: everything trades against it through the real sim. The cost model sits behind one interface so table-based or map-coupled models can be added later without touching specs. |
| Fighter vs fighter? | **In scope.** | Needs a target abstraction and teams (§5). The combat pass already permits it (it gates on owner, not faction). |

Assumed until said otherwise: runtime energy is a **fixed reserve plus regen** per fighter.
Coupling it to the map (the energy-field idea in `MAP_STATE_BRAINSTORM.md`) is a later cost
model, not part of this plan.

---

## 1. Where the code is today

Facts the rest of this plan leans on. File references are to the current tree.

**The enemy chassis is already data-driven.** `Entities/Enemies/EnemyBlueprint.cs` declares
an enemy as body knobs (`Radius`, `Sides`, `Health`, `Mass`, `GravityScale`, `FrictionScale`,
`TargetMemory`, `Rooted`), a movement-state list, an action list, and a `Controller`;
`EnemyFactory.Register` keys it by `EntityKind`, and `EntityFactory.Rehydrate` dispatches
snapshot restore through the same factory. Eight enemies are plain registrations today
(Skirmisher, Bastion, Pouncer, Latcher, Bird, Shrike, Zeus, Template); `BruteEnemy` is the
hand-written reference; `StalkerEnemy` / `TurretEnemy` predate the framework.

**The reusable pool** (`EnemyMovementStates.cs`, `EnemyActions.cs`, `GauntletActions.cs`,
`EnemyController.cs`):

| Movement states | Actions | Controllers |
|---|---|---|
| Idle, Chase, AttackHold, Stagger, Jump, Cling, Fly, Hop | Melee, Contact, Lunge, Slam, Ranged, RailShot, PounceSlam, Lash (+ Zeus bolt/strike/sweep/column, Shrike detonate, Template) | ChasePlayer, MoveTowardPlayer, Patrol, StationaryAim (+ Zeus, Shrike, Template) |

**The brain contract** is `EnemyController.Decide(in EnemyContext) → EnemyInput`, where
`EnemyInput` is `MoveDir` (2-D), `Jump`, `JumpVelocity`, `AimWorld`, `WantAttack`.
Controllers must be stateless or config-only — there is no snapshot path for brain memory.
`EnemyEntity.Update` (`EnemyEntity.cs:131`) builds the context, asks the brain, derives facing
from `AimWorld`, then runs `SelectAction` → `SelectMovement` → movement `Update` → action
`Update`. `SelectAction` (`EnemyEntity.cs:239`) picks the highest-passive-priority action
whose precondition passes; the brain can only veto via `WantAttack`, not choose.

**Everything targets "the player".** `Simulation.cs:361` calls
`e.Update(dt, _player, _hitboxes, this)`; `EnemyContext.Player` is a `PlayerCharacter`;
`ToPlayer` / `Dist` / `PlayerVisible` / `LastSeenPos` and every controller derive from it.
Target memory (`UpdateTargetMemory`) raycasts to the player specifically.

**The combat pass does not care about faction.** `World/CombatSystem.cs:191` skips a
hitbox/hurtbox pair only when `hit.Owner == hb.Owner`. `Faction` exists
(`Player1, Player2, Enemy, Neutral`) but is consulted only by NPC code asking "is this a
player" (`Factions.IsPlayer`). Two enemies can already hit each other; nothing today makes
one *want* to.

**Action tuning is locked in class overrides.** Every knob — windup, active, recovery,
range, damage, knockback, projectile speed, hitbox extents — is a `protected virtual`
property on the action class (`EnemyActions.cs:15-32`, `:212-226`, `:361-374`, `:475-481`).
`PopulateDurations` copies the three durations into `EnemyActionVars` at `Enter` and on
restore. A cost function cannot read these, and a designer cannot vary them without a
subclass. **This is the first refactor and the gate for everything else.**

**Mass already has physics consequences.** `Entity.Mass` divides knockback
(`Entity.cs:19`, `HitResolver.Resolve`); a separate `ImpactDamage.Mass` drives crush damage.
The physics has no mass otherwise — forces are accelerations (`PhysicsBody` has no mass
field). So "heavier is slower" is not free; it must be imposed where locomotion produces
accelerations. **One precedent exists:** `EnemyFlyState` computes its per-frame budget as
`MaxAcceleration / mass * dt` (`EnemyMovementStates.cs:346+`). `EnemyChaseState` sets
`Velocity.X` directly from a `Speed` constant and `EnemyJumpState` adds a fixed
`JumpImpulse`; neither consults mass.

**A per-player resource economy exists but no per-entity one.** `Character/Action/BuildMeters.cs`
is a reservoir / working-pool / charge triple denominated in "meter units";
`configs/material_strengths.json` prices a placed tile per material (`BuildCost`). Enemies
have no meter. The only enemy feature currently described as having a cost is
`TargetMemory` ("costs a terrain raycast per frame, so it is opt-in").

**Snapshot slots** for an enemy are the `EntityData` value struct
(`Sim/ECS/Components/EcsComponents.cs:45`): base fields, `AIState`/`StateTime`/`Facing`/`Aim`
for the movement FSM, `ActionIdx`/`ActionTime`/`LockedFacing` for the action FSM, and the
target-memory pair. BACKLOG 5.15: movement vars snapshot only their clock, so new per-frame
state must live on the entity and be written in `WriteState`.

**Headless test bench:** `MTile.Tests/Sim/` (`SimRunner`, `SimTerrain.FromAscii`,
`InputScript`, `HeadlessEntityWorld`), and `GauntletEnemyTests` shows the pattern for running
a real `Simulation` with spawned enemies and scripted player input. `Net/BotInputSource.cs`
is a seeded-random stub and `BOT_AI_PLAN.md` is unstarted — the pilot brain is greenfield.

---

## 2. The fighter package

```
FighterSpec  (data: attributes + actions + brain type)     ← what a designer writes
    │  FighterCompiler.Compile(spec, costModel)
    │      → cost report (per-currency totals, violations)
    │      → EnemyBlueprint (registered under the fighter's EntityKind)
    ▼
EnemyBlueprint → BlueprintEnemy : EnemyEntity               ← unchanged sim-facing form
```

The blueprint stays the sim's view of an enemy, so spawn, snapshot, rehydrate, stages, and
tests need nothing new. The spec is the *design document*; the compiler is where the cost
model runs; the blueprint is the *compiled* result.

```csharp
public sealed class FighterSpec
{
    public required string     Name;
    public required EntityKind Kind;                 // one per fighter, as today

    // Attributes — each is a "buy" with a physical consequence (§3.2).
    public float Health        = 3f;
    public float Strength      = 1f;   // damage scale applied to every action
    public float Armor         = 0f;   // extra knockback resistance beyond mass
    public float EnergyReserve = 0f;
    public float EnergyRegen   = 0f;
    public float GroundPower   = 0f;   // → walk acceleration = GroundPower / Mass
    public float JumpImpulse   = 0f;   // → launch velocity   = JumpImpulse / Mass
    public float Thrust        = 0f;   // → flight accel      = Thrust / Mass (0 = cannot fly)
    public bool  Cling         = false;
    public bool  TargetMemory  = false;
    public bool  Rooted        = false;
    public float Radius        = 12f;
    public int   Sides         = 6;

    // Actions — each entry is an action kind plus its knobs (§4).
    public List<ActionSpec> Actions = new();

    // Brain — a factory so the compiler can instantiate it with the spec's
    // config. Must be deterministic; may use the scratch block (§5.2).
    public required Func<FighterSpec, EnemyController> Brain;
}
```

`Mass` is **not** a field. It is derived (§3.2). A designer never types a mass; they buy
things that weigh something.

---

## 3. The cost model

### 3.1 Currencies

Four currencies, each doing a different job. Every feature reports a **cost vector** across
all four; the validator sums vectors and checks each against the budget.

| Currency | Kind | What it constrains | Where it lives at runtime |
|---|---|---|---|
| **Mass** | continuous, derived | Everything physical: knockback, crush, and — via power ÷ mass — speed, jump, flight | `Entity.Mass`, plus the acceleration divisions in movement states |
| **Energy** | continuous, runtime meter | Per-use and per-second costs: shots, terrain-eating, flight, block placement | New `EnemyEntity.Energy` meter (§6) |
| **Slots** | discrete | How many actions and movement modes a fighter carries | Compile-time only |
| **Points** | discrete budget | The catch-all for things with no physical price (an extra slot, target memory, a special) | Compile-time only |

```csharp
public readonly record struct Cost(float Mass, float Energy, int Slots, int Points)
{
    public static Cost operator +(Cost a, Cost b) => new(a.Mass + b.Mass, a.Energy + b.Energy,
                                                         a.Slots + b.Slots, a.Points + b.Points);
}

public interface ICostModel
{
    Cost AttributeCost(FighterSpec s);          // the mass the attributes add, plus points/slots
    Cost ActionCost(in ActionSpec a, FighterSpec s);
    Cost BrainCost(FighterSpec s);              // e.g. target memory's raycast → points
    FighterBudget Budget { get; }               // caps per currency
    IEnumerable<string> Validate(FighterSpec s, Cost total);   // constraint violations
}
```

`PhysicsCostModel` is the first implementation. A table-driven or map-coupled model later is
a second implementation, not a spec change. The compiler takes the model as a parameter.

### 3.2 Physics-derived pricing (the first model)

Mass is the spine. Attributes add mass; locomotion is bought as *power* and the sim divides
by mass, so every strength you buy makes you slower unless you also buy power, which weighs
something too.

| Buy | Adds mass | Sim consequence | Hard constraint |
|---|---|---|---|
| Base body (radius, sides) | `k_body · Radius²` | Hurtbox / hitbox size follows radius | — |
| Health | `k_hp · Health` | `Entity.Health/MaxHealth` | — |
| Strength | `k_str · (Strength − 1)` for `Strength > 1` | Every action's `Damage` × Strength | `Strength ≥ 0.25` |
| Armor | `k_arm · Armor` | Knockback divisor = `Mass + Armor` (armor is "mass that only counts for shoves") | — |
| Energy reserve / regen | `k_e · Reserve + k_r · Regen` | The meter (§6) | — |
| Ground power | `k_gp · GroundPower` | Walk accel `= GroundPower / Mass`; top speed `= √(GroundPower / drag)` | `GroundPower / Mass ≥ a_min` or the fighter cannot walk (allowed: a turret) |
| Jump impulse | `k_j · JumpImpulse` | Launch `v = JumpImpulse / Mass` | `v ≥ v_min` or `Jump` is refused (no slot spent) |
| Thrust | `k_t · Thrust` | Fly accel `= Thrust / Mass`; hover costs energy `= Thrust · c_hover` per second | **`Thrust / Mass > g`** or flight is refused |
| Cling | `k_cling` (fixed) | `EnemyClingMoveState` | — |
| Rooted | negative points (a discount) | `Entity.Rooted` | Excludes ground power, jump, thrust, cling |
| Target memory | points only | `TracksTarget` | — |

The `k_*` coefficients live in one config file (`configs/fighter_costs.json`, loaded once at
boot like `impact_profiles.json`, sim-affecting so no hot-reload) so the whole economy can be
retuned without a recompile. Starting values are a guess to be fixed by the arena (§8), not
by hand.

Two things fall out of this that a table can't give:

- **Every extreme has a mechanical reason to be bad.** A 20-HP brick can't jump because the
  impulse it can afford divided by its mass is below the floor. A flyer that buys health
  crosses back under `Thrust/Mass = g` and stops being a flyer.
- **No coefficient has to be balanced against another by hand.** Only the `k_*` scale is
  tuned; the tradeoffs come from the sim.

### 3.3 Action costs

Each action reports its own vector. The physical part is derived from the knobs; the
discrete part is fixed per kind.

| Action kind | Mass | Energy per use | Slots | Points |
|---|---|---|---|---|
| Melee | `k_a · Damage · Reach` | 0 | 1 | 0 |
| Contact (touch damage) | small | 0 | 1 | 0 |
| Lunge | `k_a · Damage · Reach` | `k_dash · LungeSpeed` | 1 | 0 |
| Slam (fall-scaled) | `k_a · Damage` | 0 | 1 | 1 (needs jump or fly) |
| Ranged | `k_a · Damage` | `k_shot · ProjectileSpeed · Damage` | 1 | 0 |
| Rail / terrain-eating | as ranged | `k_shot · … + k_tile · Penetration` | 1 | 2 |
| Lash (frozen-axis reach) | `k_a · Damage · Reach` | 0 | 1 | 1 (needs cling) |
| Place block | 0 | `BuildCost(material)` per tile (same units as `material_strengths.json`) | 1 | 0 |
| Spawn block in air | 0 | `BuildCost(material) · airPremium` | 1 | 1 |
| Fly | (covered by Thrust) | `Thrust · c_hover` per second airborne | 1 movement slot | 0 |

Windup / active / recovery are **not** priced. They are the fighter's *tell* and its
commitment; a designer shortening a windup is making a fighter that is harder to read, which
the arena will reward or punish on its own. If that turns out to be abusable (0-frame
windups), price it then — the interface allows it.

### 3.4 Budget and validation

```csharp
public sealed record FighterBudget(float MaxMass, int MaxSlots, int MaxPoints);
```

Energy has no compile-time cap: reserve and regen are attributes that cost mass, so the
budget on mass bounds them. Validation errors are strings the designer reads and the AI
designer treats as infeasible (§8.3). The compiler refuses to produce a blueprint for an
invalid spec; there is no "over budget but allowed" mode, because the arena's results would
then be meaningless.

---

## 4. Action tuning becomes data (`ActionSpec`)

The one refactor that touches existing code broadly. Today an action's knobs are
`protected virtual` properties; a variant is a subclass. After this, an action is a
flyweight *behaviour* plus a value struct of *knobs*, and the blueprint carries the knobs.

```csharp
public enum ActionKind { Melee, Contact, Lunge, Slam, Ranged, RailShot, Lash,
                         PlaceBlock, SpawnBlockInAir /* …Zeus/Shrike specials later */ }

public struct ActionSpec
{
    public ActionKind Kind;
    public float Windup, Active, Recovery;
    public float MinRange, MaxRange, VerticalSlack;
    public float Damage;
    public Vector2 Knockback;
    public float Reach, HalfWidth, HalfHeight;   // hitbox extents
    public float Speed;                          // lunge speed / projectile speed
    public float Penetration;                    // rail shot tile budget
    public TileType Material;                    // block actions
    public int ActivePriority, PassivePriority;  // default from Kind; a designer may reorder
}
```

Mechanics:

- `EnemyActionState` gains a `Spec` property. The constructors take an `ActionSpec`; the
  existing virtual properties become plain reads of `Spec` (`protected float Windup =>
  Spec.Windup`), so **every subclass body compiles unchanged** — only the property
  declarations move. `PopulateDurations` reads `Spec`.
- `EnemyBlueprint.Actions` stays a `Func<List<EnemyActionState>>`; the compiler builds that
  factory from the spec's `List<ActionSpec>`. Hand-written blueprints (gauntlet trio, Bird,
  Zeus) construct their actions with the specs they use today — an `ActionSpec.Default(kind)`
  per kind captures the current constants so **no existing enemy changes behaviour**.
  `GauntletEnemyTests`, `TemplateEnemyTests`, `ZeusTests`, `ShrikeTests` are the regression
  gate.
- Snapshot: nothing new. Specs are construction inputs, rebuilt on `Rehydrate` through the
  registered blueprint, exactly as the flyweights are today.
- `Strength` is applied at hitbox publish time (`Damage * ctx.Self.Strength`), not baked into
  the spec, so the same action spec on two fighters reads the same in the cost table.

Ordering rule the existing template documents and this must preserve: `effective reach >
trigger range > controller hold band`, or a fighter pins at its trigger boundary swinging at
air (`TemplateEnemy.cs`, framework §7b). The compiler validates it per action.

---

## 5. Brain and targeting

### 5.1 Explicit action choice

`EnemyInput` gains `RequestedAction` (`int`, `-1` = "anything that passes"). In
`SelectAction`, when `RequestedAction >= 0` the candidate scan considers **only** that index
(precondition and incumbency rules unchanged, so a brain can *ask* but the sim still
decides whether the swing is physically available). `WantAttack` stays as the veto. A brain
that never sets `RequestedAction` behaves exactly as today, so existing controllers are
unaffected.

Brains address actions by index into their own spec's `Actions` list — the fighter package
knows its own layout.

### 5.2 Brain memory (the scratch block)

Controllers are stateless because there is no snapshot path for them. A bundled brain wants
at least a timer and a mode. Add a small value block to `EntityData`:

```csharp
public struct BrainScratch { public float F0, F1, F2, F3; public int I0, I1; }
```

carried on `EnemyEntity`, exposed to the brain as `ref ctx.Scratch`, written in
`WriteState` / read in `ReadState`. Fixed-size so the snapshot is still a flat value copy.
The rule for bundled brains, stated in code and tests: **all brain state lives in
`Scratch`; the controller instance has no mutable fields.** `FighterDeterminismTests`
(§9) enforce it by snapshot → mutate → restore → compare, the same gate the gauntlet has.

Determinism rules carry over unchanged: no `System.Random` (a brain that wants noise hashes
`ctx.Frame` and its entity id), no wall clock, no statics, and `Decide` runs once per
`Update`.

### 5.3 Targets and teams (fighter vs fighter)

Replace the single `PlayerCharacter` in the enemy path with a target abstraction:

```csharp
public readonly struct EnemyTarget
{
    public readonly EntityId Id;        // EntityId.None ⇒ the player
    public readonly Vector2  Position;
    public readonly Vector2  Velocity;
    public readonly float    Health;
    public readonly int      Team;
}
```

- `Entity` gains `public int Team` (snapshotted in `EntityData`). Player = team 0 (P2 = 1);
  every existing enemy = team 2 (so nothing changes for the gauntlet); fighters get the team
  their spawn assigns.
- `Simulation.cs:361` keeps passing the player, and additionally passes a **target
  resolver** (`ITargetSource`) that answers "nearest live entity not on my team" from the
  entity list plus the player(s). Same iteration order every frame (the ECS sparse set
  order), so target choice is deterministic and rollback-safe. The chosen target's
  `EntityId` is snapshotted on the fighter so target *stickiness* survives a rollback.
- `EnemyContext.Player` stays for the existing enemies (it is the resolved target cast back
  when the target is the player); new code reads `ctx.Target`. `ToPlayer` / `Dist` /
  `PlayerVisible` / `LastSeenPos` are re-pointed at `Target`. `UpdateTargetMemory` raycasts
  to the target's position. The Zeus / Shrike / gauntlet brains compile against the same
  names and behave identically when the only target is the player.
- The combat pass needs **no change** — it already dispatches on owner mismatch. What
  changes is that a fighter's hitbox will now find another fighter's hurtbox *because the
  fighter aimed there.*
- Friendly fire between teammates: leave it on. It is a cost of stacking fighters and the
  designer can build around it. Revisit if the arena shows it dominating.

---

## 6. The energy meter

`EnemyEntity` gains `Energy` / `EnergyMax` / `EnergyRegen` (floats), ticked in `Update`
before the brain runs, written to two new `EntityData` slots (`Energy`, and `EnergyMax` —
regen is a blueprint constant, rebuilt on rehydrate). Same units as `BuildMeters`, so tile
prices carry over.

Spending: an action's `CheckPreConditions` returns false when `ctx.Self.Energy <
Spec.EnergyCost`, so an unaffordable action simply never triggers — the brain sees it via
`ctx.Self.Energy` and can wait. `Enter` deducts. Per-second costs (flight) deduct in the
movement state's `Update`; when the meter hits zero, `EnemyFlyState.CheckConditions` fails
and the fighter falls. No debt, no partial shots.

Not in this plan: energy pickup from the map, energy transfer between fighters, energy as
damage. All are new cost models (§3.1) later.

---

## 7. Movement states read power, not constants

Three small changes so the physics-derived costs actually bite:

- `EnemyChaseState`: velocity-set → acceleration toward `sign(MoveDir.X) · vTop` with
  `accel = GroundPower / Mass`, capped at `vTop = √(GroundPower / k_drag)`. Existing enemies
  get a `GroundPower` back-solved from their current `Speed` so behaviour is preserved to
  within a frame.
- `EnemyJumpState`: `JumpImpulse / Mass` instead of a constant. Same back-solve.
- `EnemyFlyState`: already divides by mass; add the per-second energy drain and the
  `Thrust/Mass > g` check at compile time (the state itself just does what it's told).

Where these numbers come from at runtime: the blueprint. Add `GroundPower`, `JumpImpulse`,
`Thrust`, `Strength`, `Armor`, `EnergyMax`, `EnergyRegen`, `Team` to `EnemyBlueprint` with
defaults that reproduce today's constants. `BlueprintEnemy`'s ctor copies them onto the
entity; states read them off `ctx.Self`.

---

## 8. Fighters as an optimization problem

The sim is deterministic and headless, and a match is a few thousand `Step` calls. That is
the whole reason "design the best fighter" can be a *game* rather than a spreadsheet.

### 8.1 Harness

`MTile.Tests/Sim/FighterArena.cs` (test-side, like `SimRunner`):

```csharp
// WinnerTeam == -1 ⇒ draw (timeout). HealthLeft/DamageDealt are for the optimizer's
// shaped objective and for reports only — the rating never reads them (§11.2).
public sealed record ArenaResult(int WinnerTeam, int Frames, float[] HealthLeft, float[] DamageDealt);

public static ArenaResult Run(ChunkMap terrain, IReadOnlyList<(FighterSpec spec, Vector2 pos, int team)> fighters,
                              int maxFrames = 60 * 60);
```

Fighters are spawned through `EnemyFactory.Create` after compile. `Simulation` always
constructs a player, so the harness parks it in a sealed pocket in the rock outside the box
and the target source ignores it (§10.3). A fight ends when one team has no live fighters
(kill) or `maxFrames` elapses (**draw** — never resolved on health, §11.1). The terrain is
`SimTerrain.FightBox(...)` (§10); a *bout* is the 12-condition bundle of §10.2.

The arena is **also the balance tool for the `k_*` coefficients**: run a fixed roster of
hand-made fighters across a fixed set of terrains; if one archetype wins everything, its
coefficient is too low. That loop replaces hand-balancing the cost table.

### 8.2 Fitness and terrain

Fitness is not "beats one opponent". It is win rate across a **roster** of opponents on a
**set** of terrains, because the whole point of the terrain-is-the-weapon game is that a
fighter tuned for a flat floor loses among pillars. The terrains are the `fightbox`
variants of §10.2 (flat and pillars, three spawn separations, both sides — 12 conditions
per bout) and the roster is ~6 hand-authored archetypes that span the tradeoff space:

| Archetype | Buys | Point |
|---|---|---|
| Brick | health, armor, melee | proves mass makes you slow |
| Sprinter | ground power, lunge | proves speed costs health |
| Gunner | energy reserve, ranged, target memory | proves energy is a real limit |
| Flyer | thrust, slam | proves `Thrust/Mass > g` binds |
| Builder | energy, place block, spawn-in-air | proves terrain actions are worth their energy |
| Turret | rooted (discount), rail shot, big reserve | proves rooted is a real trade |

These six are also the first content: they ship as registered blueprints and appear on a
new `fighters` stage.

### 8.3 The AI designer

Search over `FighterSpec` with the arena as the objective. A validation failure (§3.4) is
infeasible, not a low score. First cut: random restarts + hill climbing on the continuous
attributes with action lists sampled from the pool — it's a small space and a match is
milliseconds. This is a **tool**, not sim code: it runs in `MTile.Bench` or a new
`MTile.Forge` CLI, never inside the game loop. Its output is a spec, which the compiler turns
into a blueprint like any human-written one.

The brain is the part search can't easily vary (it is code). Give bundled brains a
`BrainConfig` (the floats a controller reads: engage range, retreat threshold, preferred
action order) so the designer can search *within* a brain even if it can't write one.

---

## 9. Phases and gates

Each phase ends with existing tests green and one new test class. New classes must contain
a term the group runner matches (`scripts/test-group.py`): add `"Fighter"` to the `combat`
list in phase 1 so every class below runs in the group.

1. **Action tuning into data.** `ActionSpec` + `ActionKind`, `EnemyActionState.Spec`,
   `ActionSpec.Default(kind)` capturing today's constants. Every existing enemy behaves
   identically. *Gate:* `combat` group green (`GauntletEnemy`, `TemplateEnemy`, `Zeus`,
   `Shrike`, `Bird` classes), plus `FighterActionSpecTests`: a blueprint with a modified
   spec produces the modified behaviour, and a round-trip snapshot mid-windup restores the
   spec's durations.
2. **Meter, scratch, requested action, blueprint attributes.** §5.1, §5.2, §6, §7. *Gate:*
   `FighterDeterminismTests` — a fighter with a stateful brain and a draining meter,
   snapshot → run → restore → re-run → identical checksums. This is the rollback gate; no
   phase 3 until it is green.
3. **Spec, cost model, compiler, six archetypes, `fighters` stage.** §2, §3, §4, §8.2's
   roster. *Gate:* `FighterCostTests` — each archetype compiles under budget; each
   hard-constraint (no-fly, no-jump, rooted exclusions) has a spec that fails it with the
   expected message; a spec over any single currency is refused.
4. **Targets and teams.** §5.3. *Gate:* `FighterCombatTests` — two fighters on opposite
   teams with no player in reach close and damage each other; two on the same team ignore
   each other; the gauntlet trio's tests are unchanged.
5. **Arena box and harness.** §10 (`TerrainRule.Type`, hardened immunity in the tile pass,
   `Levels/fightbox.json`, the `fightbox` stage, `SimTerrain.FightBox`) and §8.1. *Gate:*
   `FighterArenaTests` — the box has no reachable non-hardened tile outside the interior;
   a round robin of the six archetypes over the 12 conditions is deterministic (same result
   table twice); and no archetype wins every bout (the balance smoke test — allowed to be
   red while `k_*` is being tuned, noted in BACKLOG §5 if so).
6. **Ledger and ratings.** §11: content-hashed fight cache, `ledger.jsonl`, Glicko-2 with
   the draw penalty, anchor pinning, `recompute`. *Gate:* `FighterRatingTests` — replaying a
   fixed ledger twice gives identical ratings; a draw lowers both sides when equal-rated and
   still raises the weaker side in an upset; anchors never move.
7. **`MTile.Forge` and intake.** §12: `validate` / `submit` / `bout` / `league` / `ladder` /
   `report` / `replay`, Roslyn compile in an isolated load context, the determinism gate.
   *Gate:* a brain with a hidden mutable field is rejected by `submit`; the six archetypes
   submitted into an empty pool reproduce their pinned ratings within one deviation.
8. **Benchmark protocol.** §13: held-out conditions, the fixed-pool track, the scoring
   script, a first run with one agent. No sim gate; its output is a ledger.
9. **AI designer.** §8.3, as a `forge` subcommand. No sim gate; its output is checked by
   phase 3's tests and scored by phase 8's script.

Phases 1–2 are the risky ones (they touch every enemy). Phases 3–9 are additive; 5–7 can
run in parallel with 3–4 once phase 2 is in (they need the meter and the scratch block but
not the compiler).

---

## 10. The arena: `fightbox`

A large closed box with unbreakable walls, built for 1v1. Two facts about the level loader
shape it:

- **Rules only set solid/open.** `TerrainLoader.ApplyRules` writes `IsSolid` and nothing
  else; material falls through to the enum default, `Stone`. Hardened rock (`H`, the
  bedrock-grade material) is only authorable in hand-written ascii chunks. Add a `Type`
  string to `TerrainRule` (null = today's behaviour) so a rule-based box can be hardened.
  Five lines in `World/TerrainLoader.cs`.
- **Hardened is tough, not immune.** `MaxHP` 120 in `material_strengths.json` and nothing in
  the tile-damage path checks for it, so a rail bolt or a few hundred slashes eventually
  chew through. Two fixes, both cheap: fill *everything* outside the box with hardened (the
  gauntlet's bedrock-seal trick — digging out gains nothing), and skip `TileType.Hardened`
  in `CombatSystem`'s tile pass so the walls are fixed geometry. Hardened already has no
  in-game source and `TileTypes.IsPlaceable/IsGrabbable` already refuse it.

### 10.1 Layout

Interior **64 tiles wide × 32 tall** (704 × 352 px at `Chunk.TileSize` 11), aligned to
chunk boundaries so it is exactly 4 × 2 chunks: tiles `x ∈ [0, 64)`, `y ∈ [−32, 0)`. Floor
surface at tile `y = 0`: three tiles of dirt over hardened bedrock. Walls and ceiling bare
hardened. Mirror-symmetric about `x = 32`.

```
HHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHH   y = -33
H                                                                H
H                       32 tiles of air                          H
H                                                                H
H          A                                      B              H   spawns x = 12, 52
HDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDH   y = 0..2  dirt
HHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHH   y >= 3   bedrock
 x = -1                                                    x = 64
```

`Levels/fightbox.json`, `Extents` 6 (chunks −6..6, so ≥ 32 tiles of rock on every side of
the box), five rules in last-match-wins order:

| Condition | Type | Purpose |
|---|---|---|
| `y >= 0` | Dirt | floor slab |
| `y >= 3` | Hardened | bedrock under the slab |
| `y < -32` | Hardened | ceiling and everything above |
| `x < 0` | Hardened | left wall (overrides the dirt rows) |
| `x >= 64` | Hardened | right wall |

Why those sizes, in tiles:

| Reference | Size | Consequence in a 64 × 32 box |
|---|---|---|
| Rail shot max range (520 px) | 47 | Cannot cover the width from one wall; retreat exists |
| Ranged action max range (360 px) | 33 | A gunner closes about half the box before firing |
| Enemy jump (impulse 260 → ~56 px) | ~5 | Ceiling is six jumps up: flight is a dimension, not a hop |
| Walk speed (100 px/s) | 9 / s | Crossing takes ~7 s: positioning matters |
| Spawn separation | 40 | Inside rail range, outside ranged range — deliberate; the harness varies it |

The dirt slab is the terrain-is-the-weapon concession: builders and diggers have material to
work with; bedrock three tiles down stops anyone tunnelling out.

### 10.2 Variants and conditions

- `fightbox` — flat, as above. The v1 arena.
- `fightbox_pillars` — same box plus symmetric cover: two 3 × 6 stone pillars at
  `x = 20..22` and `41..43`, a 8 × 1 stone ledge centred at `x = 32`, four tiles up. The
  roster's terrain-sensitivity test.
- A **condition** is (terrain, spawn separation, side assignment). Three separations
  (24, 40, 56 tiles) × two sides × two terrains = **12 conditions per bout** (§11.1).
  Sides are swapped so residual asymmetry cancels. Later terrains join the pool; a bout
  draws a seeded subset (§11.5).

### 10.3 Plumbing

- `Stages` gets a `fightbox` entry (`PlayerSpawn` inside the box) for human play against
  a fighter, and `SimTerrain.FightBox(pillars: bool)` builds the same geometry
  programmatically for tests and the harness, so the level path is not on the test path.
- **The player must be excludable from targeting.** `Simulation` always constructs a
  `PlayerCharacter`. For bot-vs-bot the harness parks it in a sealed 3 × 3 pocket in the
  rock far from the box, and the `ITargetSource` of §5.3 must support "teams only, ignore
  the player" or the nearest-enemy rule pulls a fighter toward the pocket. This is a
  requirement on phase 4.
- **Spectating.** `Camera.TrackTarget` follows the player. Watching a bot fight in-game
  needs a render-only camera mode that frames both fighters. Not on the critical path.

---

## 11. Bouts, ratings, and the ledger

### 11.1 A bout is a bundle of conditions

The sim is deterministic: A vs B under one condition has exactly one outcome, forever.
Replaying it teaches nothing. So:

- A **bout** between two fighters is all 12 conditions of §10.2. Each condition yields a
  **fight result**: a kill for one side inside the clock, or a **draw** on timeout.
  Health, damage dealt, and margin do **not** enter the result — resolving timeouts on
  margin makes "land one hit and run" optimal, so it is a draw, always.
- Every fight result is **cached by content hash** `(hash(specA), hash(specB), conditionId,
  simVersion)`. A fighter is immutable once submitted (a change is a new fighter). Any sim
  change bumps `simVersion` and invalidates the cache: determinism does not hold across
  builds (CLAUDE.md, the never-cross-play rule), so a stale result is a wrong result.
- The **clock** is a condition parameter; 60 s (3600 frames) is the placeholder. Shorter
  clocks produce more draws and reward closers; longer reward attrition. The pillar box
  probably wants longer.

Cost: a Release frame is ~30–180 µs on the current bench (`MTile.Bench/baseline.txt`), so
a 60 s fight is ~0.1–0.5 s and a bout is a few seconds per core. All-play-all over a pool
of 100 is hours; over 1000, weeks. Ratings are therefore computed from **sampled** bouts,
which is what the rating system is for.

### 11.2 Draws are possible and penalised

A win scores 1, a loss 0, and a **draw scores `0.5 − d` for each side**, with `d = 0.1` to
start. The two sides' scores no longer sum to one: draws drain rating from the pool. Bout
score for a side is `(wins + (0.5 − d) · draws) / 12`, a fraction the rating update
consumes directly.

What this buys, with no special cases:

- Both sides always prefer a win to a draw, so the fighter ahead chases and finishes, and
  the locomotion costs decide whether it can.
- The fighter behind prefers a draw to a loss, so running when losing is rational — real
  fighting-game behaviour, and the closed box means there is nowhere to run forever. A
  fighter built only to run draws everyone at `0.5 − d`, converges below average, and by
  then matchmaking (§12.4) pairs it only with its peers. Self-limiting.
- Drawing against a much stronger fighter still *gains* rating: the zero-sum term of the
  upset outweighs `d`. Forcing a draw on the number one stays an achievement, and the
  strong side loses more than the weak side gains — top fighters must be able to close.
- Mutual passivity is just a draw. No pseudo-opponent, no activity metric.

Health margin is used in exactly one place: the AI designer's search (§8.3) may use it as a
*shaped* objective, because a win-or-draw result is a step function. The rating never sees
it. Keeping those apart is what stops the search from rediscovering hit-and-run: a fighter
that games the shaped objective still draws in the ledger and sinks.

### 11.3 Glicko-2

Each fighter carries a rating `μ`, a deviation `σ`, and a volatility. Glicko-2 rather than
plain Elo because the deviation is what buys **fast mixing**: a new entrant starts wide, so
its first dozen bouts move it hundreds of points, while an established fighter with a
narrow deviation barely moves when a newcomer beats it. The deviation also drives
matchmaking (§12.4) and the leaderboard's confidence display. Plain Elo with a K that
decays with bouts played is an acceptable stand-in and is ten lines; go to Glicko-2 as
soon as the pool has more than a handful of fighters.

**Anchors pin the scale.** The six archetypes of §8.2 get fixed ratings that never update
(Brick 1300, Sprinter 1400, Gunner 1500, Flyer 1500, Builder 1400, Turret 1300 as a first
guess — they are re-pinned once by an all-play-all among themselves, then frozen). They
stop the scale drifting as draws drain it and as the pool grows, and they give designers a
legible goal ("beat the Brick").

### 11.4 The ledger

Every fight result is appended to `Arena/<pool>/ledger.jsonl`:

```json
{"a":"sha256:…","b":"sha256:…","cond":"fightbox/sep40/aLeft/60s","sim":"v12","result":"A","frames":2210,"t":"2026-10-01T…"}
```

Ratings are a **derived view**: `forge recompute` replays the ledger from zero. That makes
the leaderboard reproducible, lets `d`, the anchor ratings, or the whole formula change
later with full history intact, and means a pool is fully described by its directory
(§12.2). Fighter hashes are content hashes of `spec.json` + `brain.cs`, so a ledger line
never refers to something that can drift.

### 11.5 Overfitting to the arena

A fighter tuned to exactly two terrains and three spawn separations looks better than it
is. Keep a terrain **pool** larger than any bout uses, and let each bout draw its
conditions from a seed derived from the two fighter hashes, so the rating is over the
distribution. For benchmark runs (§13) the scoring conditions are a held-out subset the
designer never iterates on.

---

## 12. Submission and evaluation (the eval suite)

Think of this as an eval suite for pools of submitted fighters. A **pool** is a directory:
fighters, anchors, condition set, sim version, ledger. Humans and agents submit into a
pool through one CLI; everything downstream is mechanical and replayable.

### 12.1 The fighter package on disk

```
Fighters/<name>/
  manifest.json      name, author, agent id (optional), created, notes
  spec.json          FighterSpec fields (§2) — attributes and ActionSpec list
  brain.cs           one class : EnemyController, plus an optional BrainConfig record
  README.md          designer's notes (optional; not read by anything)
```

The hash of `spec.json` + `brain.cs` is the fighter's identity. Nothing else is hashed, so
notes can be edited without minting a new fighter.

### 12.2 `MTile.Forge`

A CLI project (sibling of `MTile.Bench`), never linked into the game. JSON in and out on
every command so any agent can drive it without parsing prose.

| Command | Does |
|---|---|
| `forge validate <dir>` | Compile the brain (Roslyn, isolated `AssemblyLoadContext`), run the cost model, print the cost vector and any violations. Exit 0 iff submittable. |
| `forge submit <dir> --pool <p>` | Validate, determinism gate, placement, calibration (§12.3). Writes the fighter into the pool and its fights into the ledger. Prints the fighter's hash, rating, deviation. |
| `forge bout <hashA> <hashB> --pool <p> [--cond …]` | Run (or fetch from cache) one bout. Prints per-condition results. |
| `forge league --pool <p> --rounds n` | Swiss rounds across the pool (§12.4). |
| `forge ladder --pool <p>` | Leaderboard: rating, deviation, record, anchors marked. |
| `forge report <hash> --pool <p>` | Per-fighter report: cost vector, matchup matrix vs anchors, win/draw/loss by condition, median fight length. |
| `forge recompute --pool <p>` | Replay the ledger into ratings from zero. |
| `forge replay <ledger-line>` | Re-run one fight and dump a trace (`TraceExport`) for inspection. |

### 12.3 Intake

1. **Compile + cost.** As `validate`. Over budget or infeasible is rejected with the
   validator's messages; that text is the designer's (or agent's) feedback.
2. **Determinism gate.** One fight run twice, plus snapshot → restore → re-run from
   mid-fight. Checksum mismatch rejects the fighter. This catches a brain keeping state
   outside the scratch block (§5.2), `System.Random`, statics, and anything else
   `FighterDeterminismTests` would catch — but against the *submitted* code.
3. **Placement.** Bouts against the anchors, six bouts. Initialises `μ` and narrows `σ`
   enough to be placed.
4. **Calibration.** Four bouts against the nearest-rated live fighters — the most
   informative pairings.
5. **Done.** The fighter is on the ladder. Steps 1–4 are under a minute at current fight
   cost.

### 12.4 League rounds

`forge league` pairs the pool Swiss-style: sort by rating, pair neighbours, prefer pairs
that have never met and fighters whose `σ` has grown since their last bout. Incumbents
only ever fight new pairs or new conditions, because everything else is cached. Run it as
a background job after a batch of submissions, or on a timer.

### 12.5 Outputs

`forge ladder` and `forge report` print JSON and a Markdown table. The pool directory is
the artefact: commit it (ledger and fighters are small text) and the whole history is
reviewable in git.

---

## 13. As an AI benchmark

The pipeline is a benchmark with one more layer: a fixed protocol and a frozen
environment. What it measures is worth stating, because it is unusual:

- **Reasoning about trade-offs under a cost model** — the spec half. Nothing about the
  optimum is stated; it must be inferred from the physics and from opponents.
- **Writing deterministic control code** against a real, documented API — the brain half.
  The determinism gate is a hard correctness check most benchmarks lack.
- **Iterating from structured feedback** — validator messages, per-condition results,
  reports, replays — under a budget.

### 13.1 Protocol

An agent is given: this document, `configs/fighter_costs.json`, the `EnemyController` /
`EnemyInput` / `EnemyContext` / `ActionSpec` surface (a generated API file), the archetype
fighters *including their brains* (it is a design benchmark, not a guessing game), and
`forge`. Then a **budget**: `N` submissions and `M` bouts of self-run evaluation. The
agent may run `forge bout` against anything in the pool within `M`.

Score, from the ledger:

| Metric | What it rewards |
|---|---|
| **Peak rating** reached within the budget, on the held-out conditions (§11.5) | Design quality |
| **Rating at k submissions** for k = 1, 3, 10 (the sample-efficiency curve) | Reasoning before iterating |
| **Validity rate** — submissions passing intake | Reading the cost model correctly; writing deterministic code |
| **Improvement over a given fighter** (the improvement track) | Reading and reasoning about existing code |

### 13.2 Tracks

- **Fixed-pool track.** Anchors only; no other submissions in the pool. Fully
  reproducible: same sim version, same anchors, same seeded conditions ⇒ same score
  for the same submissions. This is the number to report.
- **Open-pool track.** Rating against every submission from every agent and human. A
  living leaderboard; moves over time by construction, so it is reported with a date and
  the pool's ledger hash.
- **Improvement track.** Start from a mid-table fighter; the score is the rating delta in
  `k` submissions.

Humans run the same protocol on the same ladder. That is the point of the game half.

### 13.3 Reproducibility and contamination

- The whole run is the ledger. Publish it with the pool directory and the sim version
  tag; anyone can `forge recompute` it.
- Scoring conditions are held out; the public condition set is what the agent iterates on.
  The gap between public and held-out rating is itself a reported number (overfitting).
- The sim is frozen per benchmark version. A sim change is a new benchmark version with a
  new anchor re-pin; old ledgers stay valid under their own tag.
- Anchor brains are public. Hiding them would measure guessing, not design.

### 13.4 Exploits are findings

A search over fighter space is also a search over sim bugs: a physics glitch that lets a
fighter clip through the floor is the optimum until it is fixed. Treat every "how did it
win that" as both a benchmark result and a bug report: `forge replay` the fight, fix the
sim, bump the version, re-pin the anchors, recompute. A benchmark that pressures the sim
this way is a feature, but it means the sim version will move, which is why every ledger
line carries it.

### 13.5 Untrusted code

A submitted brain is arbitrary C# running in-process. Fine for the owner and their agents
on their own machine. Not fine for strangers: that needs a process sandbox with no file or
network access and a CPU cap, and is out of scope until the pool is public.

---

## 14. Open questions

- **Q1 — Windup pricing.** Unpriced by design (§3.3). Revisit if search finds 0-frame tells.
- **Q2 — Friendly fire.** On by default (§5.3). Revisit if teammates dominate the arena.
- **Q3 — Target selection policy.** "Nearest enemy" is the default; should a brain be able
  to override the target (a sniper choosing the flyer)? Cheap to add as
  `EnemyInput.RequestedTarget`; deferred until a brain wants it.
- **Q4 — Player in the arena.** Fighters vs the player is content (the `fighters` stage);
  fighters vs each other is the optimizer. Do we want a mode where the player *designs*
  in-game? That is a UI question and out of scope here.
- **Q5 — Map-coupled energy.** The obvious second cost model. Not before the first one has
  been through the coefficient loop.
- **Q6 — Radius.** Priced as mass here; it also changes reach and hurtbox size, which the
  physics prices implicitly. Watch for "smallest possible body" dominating and add a floor.
- **Q7 — The clock.** 60 s placeholder (§11.1). Decide by feel once fights exist; the pillar
  box likely wants longer. It is a condition parameter, so two clocks can coexist.
- **Q8 — Draw penalty `d`.** 0.1 to start (§11.2). Too small and running-when-behind is
  free; too large and a forced draw against a top fighter stops being an achievement.
  Ledger replay makes it free to retune.
- **Q9 — Anchor ratings.** First guesses in §11.3, re-pinned once by an anchor-only
  all-play-all. Whether to re-pin on every sim version bump, or only when an anchor's
  behaviour actually changed, is open.
- **Q10 — Brain memory size.** Four floats and two ints (§5.2) is a guess. A benchmark
  agent will tell us quickly whether it is enough; growing it is a snapshot-slot change.

## 15. Non-goals

- No player-side changes. No new `ActionState`, no corrector or lattice for bots, no
  `PlayerInput` piloting of fighters.
- No render work beyond what `TelegraphList` already gives an action for free, except the
  spectator camera mode of §10.3, which is render-only and off the critical path.
- No sandboxing of submitted brains (§13.5) until a pool is public.
- No networking implications beyond determinism: fighters are entities, and entities already
  roll back.
- No data-file spec format yet. Specs are C# object initializers, like blueprints. A JSON
  spec loader is trivial once the shape stops moving, and the brain is code anyway.

---

## 16. Campaign status (branch `fighter-plan`, started 2026-09-30)

Phase markers: `[ ]` todo · `[~]` in progress · `[x] done @commit` · `[!]` blocked · `[?]` needs user decision.
Numbering follows §9 as of 04e567e (the fightbox/ledger/Forge/benchmark revision).

- [x] Phase 1 — action tuning into data (`ActionSpec`, `ActionKind`, `EnemyActionState.Spec`) @39dbb8d
- [x] Phase 2 — meter, scratch block, `RequestedAction`, blueprint attributes, power-based movement @7e27b0a
- [x] Phase 3 — `FighterSpec`, cost model, compiler, six archetypes, `fighters` stage @305c196 + 44a9a9e
- [x] Phase 4 — targets and teams @5ef97e8
- [~] Phase 5 — arena box and harness. **Interim:** `FighterArena` (library, `Entities/Enemies/Fighters/FighterArena.cs`) implements the §8.1 harness on three ascii terrains with the player parked far away and resolves a timeout on health @1a8e76c. **Not done:** the §10 fightbox (`TerrainRule.Type`, hardened immunity, player excluded from targeting), and §11.2's rule that a timeout is always a draw scored 0.5−d. Those change the harness's contract, so they are a deliberate rework, not a gap.
- [ ] Phase 6 — ledger and ratings (§11). Not started.
- [ ] Phase 7 — `MTile.Forge` and intake (§12). Not started.
- [ ] Phase 8 — benchmark protocol (§13). Not started.
- [~] Phase 9 — AI designer. **Done as `MTile.Bench --forge`** (`Entities/Enemies/Fighters/FighterForge.cs` + `MTile.Bench/Forge.cs`) @140ac35, per the kickoff decision to house it in Bench; §12.2 now wants it as a `forge` subcommand of `MTile.Forge`, which is a move once that project exists. Its objective is the interim arena's win rate, not the §11 rating.

### Decisions taken at kickoff (user, 2026-09-30)

- **Power model is legacy-when-unset.** A blueprint that leaves `GroundPower` / `JumpImpulse` at 0 keeps today's velocity-set / fixed-impulse behaviour exactly. Only specs that buy power run the accel ÷ mass path. No back-solving of existing enemies.
- **Arena player is a rooted dummy** parked far out of range with idle input; no "no-player" Simulation mode.
- **AI designer lives in `MTile.Bench` behind `--forge`.** No new project.
- **`k_*` coefficients are the campaign's guess**, derived from existing enemy numbers and logged below; the phase 5 balance test is what corrects them.

### Plan-vs-code divergences found at kickoff

- **No subclasses of the pool actions exist.** Zeus / Shrike / Wizard / Warden / Aspid / Template actions are their own `EnemyActionState` classes with private knobs, not subclasses of Melee/Lunge/etc. Phase 1's `ActionSpec` therefore covers the eight pool actions (5 in `EnemyActions.cs`, 3 in `GauntletActions.cs`) only; the specials keep their knobs. §4's "every subclass body compiles unchanged" is moot.
- **Custom walk states are not `EnemyChaseState`.** `TemplateMoveState`, `WardenWalkState`, `WizardWalkState` are separate classes; `WardenHopState` subclasses `EnemyJumpState` with an overridden impulse. §7 touches only the stock Chase/Jump/Fly states; the custom ones stay constant-based.
- **More enemies than the plan lists**: Sparring, Warden, Wizard, Aspid exist beyond the eight named. All must behave identically after phases 1–2; their tests (`MTile.Tests/Sim/*EnemyTests.cs`) are part of the gate.
- **`ctx.Player` has ~34 call sites across 10 files**, including the specials. Phase 4's target abstraction is a broader edit than §5.3 implies but mechanical.
- **Enemy tests live in `MTile.Tests/Sim/`**, not the test root. Headless `SimRunner` defaults to `Dt = 1/30`; the enemy tests drive a real `Simulation` at 1/60 — the arena will do the same.

### Decisions needed

- **[?] Friendly fire between teammates — currently OFF.** §5.3 said "leave it on", but the premise behind that was wrong: the combat pass gates on `Faction`, not on entities, so two `Faction.Enemy` bodies could never hit each other at all. Phase 4 made an Enemy-vs-Enemy pair resolve only across different `Team`s. Turning friendly fire on would need a separate self-immunity check and would make the stock roster (all team 2) start hitting each other in the gauntlet. Left OFF; rule on it when the arena shows whether stacking teammates dominates.
- **[?] Multiplayer targeting.** Enemies now target the nearest opposing player, so in a two-player match they chase P2 when P2 is closer. Before phase 4 they only ever chased P1. This is what §5.3 asks for, but it is a visible change to co-op/PvP feel.
- **Decided (user, 2026-09-30): cross-team projectiles DO hit each other — keep it.** Two ranged kiters (Gunner vs Builder) fire in lockstep along the same line and their energy balls annihilate every time; that is a feature of the space, and reaction-time latency (below) is what will desynchronise identical brains, not a combat rule.
- **Noted, not decided:** with nothing opposing, an enemy falls back to the primary player even on the same team (only reachable by putting a fighter on team 0); Stalker/Turret (pre-framework) still read the player directly and are not target candidates; Aspid fireball / Wizard orb burst only on player hurtboxes and fly through other fighters after hurting them.

### Design additions agreed after the first fights (user + supervisor, 2026-09-30)

Ordered as they should land, since the first three change what the arena measures and the coefficient loop should not start before them.

1. **Readable telegraphs + a priced reaction time.** `EnemyTarget` gains the opponent's tell: current `ActionKind`, windup progress, locked aim, facing. A fighter buys `ReactionFrames` (points; fewer frames costs more) and its brain sees the target as it was that many frames ago (a fixed-size ring of past `EnemyTarget`s on the entity, snapshotted). Windups stay unpriced: once bots can read them, the arena prices them. Identical brains desynchronise through this latency plus a `hash(frame, id)` phase, never through randomness.
2. **Mandatory line of sight for fighters.** Every fighter does the LOS raycast; `TargetMemory` becomes what you buy to keep aiming at where the target WAS. No fighter sees through a Builder's wall.
3. **Density → radius, not radius → mass.** Mass is derived from purchases as today; `Radius = f(Mass, Density)` with density a costed attribute, and the pool actions' reach / hitbox extents become body-relative. Non-circular shapes are a later step, only if the arena asks.
4. **Terrain perception for brains.** A few `SurfaceProbe`-style queries (ground ahead / drop ahead / wall ahead / headroom) so walkers handle the hills and flyers the corridor.

Explicitly NOT doing: a data-driven decision-table brain. Brains stay code, authored by humans or AI agents (§12–13). **Watch brain compute:** a brain is arbitrary C# per fighter per frame, so the intake gate (§12.3) needs a µs-per-`Decide` budget measured in `MTile.Bench`, and the arena should report it per fighter.

Prices (`fighter_costs.json`) are frozen until more fights have been watched — the Brick and rooted-refund readings above are recorded, not acted on.

### Campaign log

(one line per milestone: what, commit, test status)

- 2026-09-30 · Phase 1 · 39dbb8d · `ActionSpec` + `Default(kind)`; eight pool actions read `Spec`; ranged Speed/Damage threaded into the projectiles (`EntityData.ProjDamage`). `FighterActionSpecTests` 12/12 green; `combat` group 283 pass / 10 red, the same 10 player-side reds as before the change (ActionAimSolver ×3, CombatHitstun crush, SlashComboPresentation ×6). `simcore` group: 3 red — `TrainingStageTests` (BACKLOG §5) and both `GauntletStageTests`, whose message is "player only reached x 60 of ~1397", a traversal failure from spawn that predates this work and cannot come from enemy knob plumbing.
- 2026-09-30 · Phase 2 · 7e27b0a · `EnemyInput.RequestedAction` (`int?`, null = anything) narrows `SelectAction` to one candidate; `BrainScratch` on `EnemyEntity` + `EntityData`; `Energy`/`EnergyMax`/`EnergyRegen` meter ticked before `Decide`, gated + spent in `SelectAction`, drained per second by `EnemyFlyState` (`FlightDrain`); blueprint attributes `Strength`, `Armor`, `EnergyMax`, `EnergyRegen`, `GroundPower`, `GroundDrag`, `JumpImpulse`, `Thrust`, `FlightDrain` (all default off = legacy behaviour); `Strength` applied at every pool-action hitbox publish; `Armor` widens the knockback divisor via `Entity.KnockbackMass`. **Found while testing:** a powered walk has to pre-compensate the floor's Coulomb brake (3000 × FrictionScale px/s², capped per step) or an acceleration under it never moves — `EnemyChaseState` now adds back exactly what the solver will strip, read off the body's maintained floor contact. `FighterDeterminismTests` 7/7 (bit-identical round trip with a stateful brain + draining meter; requested action; energy gate + regen; power ÷ mass; flight drain; strength + armor). `combat` 290 pass / same 10 reds; snapshot + rollback suites 33/33.
- 2026-09-30 · Phase 3 (block actions slice) · branch `fighter-3b-block` · `ActionKind.PlaceBlock` / `SpawnBlockInAir` + `Default` rows (Material Dirt = the player's starting block, `EnergyCost = MaterialStrengths.BuildCostFor(Material)` so the default row is honestly priced; the compiler still owns the final number). `Entities/Enemies/EnemyBlockActions.cs`: `EnemyPlaceBlockAction` places one tile via `ChunkMap.TryRequestTile` (supported only) `Reach` px along the aim at the target, settled onto its column — aimed cell if buildable, else the cell below when the aimed one is empty-but-unsupported (a body centre sits on a row boundary, so a hair-upward aim otherwise lands one row up and fails), else up to 2 cells above an occupied one, so repeat uses stack a wall; no buildable cell ⇒ precondition fails, no energy spent. `EnemySpawnBlockInAirAction` conjures one tile via `ChunkMap.ForceSprout` (no support needed) `HalfHeight` px above the target's position at Enter; precondition: range band + destination empty. Both freeze their destination at Enter as an absolute world point in `LockedAim` (round-trips through `EntityData.Aim`; the telegraph draws exactly that cell), fire on the windup→active transition frame, skip a cell overlapping either body, and are Committed. Placed tiles take the normal sprout path (Sprouting for `SproutLifetime`, then Solid); terrain has no gravity, so an air block hangs where it was made. `FighterBlockActionTests` 6/6 (placement cell + frame; exact spend at Enter, two uses then idle on 0.5 × cost, the second use stacks; air block lands over the Enter position of a walking target; occupied destination refused; mid-windup snapshot round-trips bit-identically with a terrain probe for both kinds). `combat terrain` 501 pass / 18 red: the 10 known player-side reds plus 8 terrain reds all listed in BACKLOG §5 (LatticePathPlanner BlockAhead, SproutLiftJump ×3, SproutGraph two-neighbours, CorridorProbe ThreeBlockWall, InfiniteTerrain streaming, SproutCrush pinned body).
- 2026-09-30 · Phase 4 · branch `fighter-4-teams` · `Entity.Team` (default `Teams.Enemies` = 2, snapshotted in `EntityData.Team`), `PlayerCharacter.Team` (fixed at construction: primary 0, secondaries `Teams.ForPlayerIndex` = 1, mirroring `Factions.ForPlayerIndex`), `EnemyBlueprint.Team`; projectiles/helpers stamped with the spawner's team (enemy EnergyBall/RailBolt/AspidFireball/WizardOrb/Bullet; player MassBall/LobbedArea/PullPoint + its ball). `EnemyTarget` + `ITargetSource` (`Simulation` implements; exposed as `IEntitySpawner.Targets`, default null ⇒ the player). Candidates: primary player (always), live secondaries, live `EnemyEntity`s ≠ self, in Step order; nearest opposing wins, ties to the earlier; a sticky target (`EntityData.TargetId`, exposed as `EnemyEntity.CurrentTargetId`) is kept unless the nearest is under 75% of its distance; nothing opposing ⇒ fall back to the primary player. `EnemyContext.Target` added; `ToPlayer`/`Dist`/`PlayerVisible`/`LastSeen*` and `UpdateTargetMemory` measure to it (memory re-seeds on a target switch); every `ctx.Player.Body.Position` under Entities/ re-pointed to `ctx.Target.Position` — no `ctx.Player` read survives. **Divergence from §5.3:** the combat pass did NOT already permit enemy-vs-enemy — `CombatSystem` skips on `hit.Owner == hb.Owner`, and both are `Faction`s, so every Enemy-faction pair was skipped. Added `AcrossTeams`: for Enemy-faction pairs only, the hit resolves when attacker (`hit.Source`) and target are entities on different teams. Consequence: same-team fighters are immune to each other (friendly fire OFF, not on as §5.3 says) — turning it on would need a Source-based self gate and would also make the stock roster (all team 2) hit each other. `FighterCombatTests` 7/7; `combat` 297 pass / the same 10 reds (ActionAimSolver ×3, CombatHitstun crush, SlashComboPresentation ×6); snapshot/rollback/desync/checksum 33/33; `MTile.sln` builds.
- 2026-09-30 · Phase 1 · b5a0d61 · `ActionSpec` + `Default(kind)`; eight pool actions read `Spec`; ranged Speed/Damage threaded into the projectiles (`EntityData.ProjDamage`). `FighterActionSpecTests` 12/12 green; `combat` group 283 pass / 10 red, the same 10 player-side reds as before the change (ActionAimSolver ×3, CombatHitstun crush, SlashComboPresentation ×6). `simcore` group: 3 red — `TrainingStageTests` (BACKLOG §5) and both `GauntletStageTests`, whose message is "player only reached x 60 of ~1397", a traversal failure from spawn that predates this work and cannot come from enemy knob plumbing.
- 2026-09-30 · Phase 2 · 3653266 · `EnemyInput.RequestedAction` (`int?`, null = anything) narrows `SelectAction` to one candidate; `BrainScratch` on `EnemyEntity` + `EntityData`; `Energy`/`EnergyMax`/`EnergyRegen` meter ticked before `Decide`, gated + spent in `SelectAction`, drained per second by `EnemyFlyState` (`FlightDrain`); blueprint attributes `Strength`, `Armor`, `EnergyMax`, `EnergyRegen`, `GroundPower`, `GroundDrag`, `JumpImpulse`, `Thrust`, `FlightDrain` (all default off = legacy behaviour); `Strength` applied at every pool-action hitbox publish; `Armor` widens the knockback divisor via `Entity.KnockbackMass`. **Found while testing:** a powered walk has to pre-compensate the floor's Coulomb brake (3000 × FrictionScale px/s², capped per step) or an acceleration under it never moves — `EnemyChaseState` now adds back exactly what the solver will strip, read off the body's maintained floor contact. `FighterDeterminismTests` 7/7 (bit-identical round trip with a stateful brain + draining meter; requested action; energy gate + regen; power ÷ mass; flight drain; strength + armor). `combat` 290 pass / same 10 reds; snapshot + rollback suites 33/33.
- 2026-09-30 · Phase 3a · (branch `fighter-3a-cost`) · `FighterSpec` (+ BrainConfig fields `EngageRange`/`StandoffRange`/`HoverHeight`/`AlertRange`/`RetreatBelowHealth`/`PreferredAction`, `Team`), `Cost`/`FighterBudget`/`ICostModel`/`PhysicsCostModel`, `FighterCosts` (+ `configs/fighter_costs.json`, loaded once in Game1, copied by Desktop + Web, in `ConfigLayoutTests`), `FighterCompiler` (cost report, violations, §4 ordering rule, movement list from what was bought, compiler OVERWRITES each action's `EnergyCost` with the priced energy), three Scratch-only brains (closer / kiter / hover-dive), six archetypes registered from `RegisterBuiltIns`, `EntityKind` Brick…FighterTurret + `FighterSlot0..7` (unregistered fighter kinds throw on rehydrate), `fighters` stage on flat.json. `FighterCostTests` 35/35; `ConfigLayoutTests` 4/4; `combat` 325 pass / the same 10 player-side reds; snapshot + rollback + `FighterDeterminism` 40/40. **Found:** `EnemyChaseState`'s brake pre-compensation (`min(brake, |vCmd|)`) only survives the solver when a frame's command is ≥ half the floor brake, so from rest a walker needs `GroundPower/Mass > 1500·FrictionScale` — 180 px/s² at the stock 0.12; the Brick moved 0.1 px in 4 s. Compiled fighters use `FrictionScale 0.02` (threshold 30 < a_min 40) until the chase state compensates the full brake. The turret archetype's kind is `FighterTurret` (`Turret` is TurretEnemy's). Builder still ships ranged-only: the block kinds and `Team → EnemyBlueprint.Team` wait on merging `fighter-plan` into this branch.

#### Starting `k_*` coefficients (phase 3a guess — phase 5's arena corrects them)

| Coefficient | Value | Anchor / reasoning |
|---|---|---|
| `k_body` | 0.0025 / px² | Brute R12 → 0.360 |
| `k_hp` | 0.10 / HP | Brute 3 HP → 0.300 |
| `k_a` | 0.005 / (dmg·px) | stock melee 1.0 × 22 → 0.110; reach-less kinds priced at `ReachRef` 22 px |
| `k_gp` | 0.002 | GroundPower 100 (≈70 px/s top at drag 0.02) → 0.200 |
| `k_j` | 0.00075 | JumpImpulse 312 (≈260 px/s at M 1.2) → 0.234 · **Brute total 1.204 ≈ 1.2** |
| `k_t` | 0.0005 | must be ≪ 1/600; flight needs `T > 857·m0`. Bird body 0.42 + T 700 → M 0.77 (Bird's 0.8), T/M 905 ≈ stock 900 |
| `k_str` / `k_arm` | 0.40 per +1 Strength / 0.50 per Armor | armor is shove-only mass, so cheaper than mass |
| `k_e` / `k_r` | 0.03 per unit / 0.15 per unit/s | the mass budget is what bounds energy |
| `k_cling` | 0.25 | fixed |
| `k_dash` | 0.002 energy per px/s | stock lunge 0.52 / use |
| `k_shot` | 0.002 energy per (px/s·dmg) | stock energy ball 1.0 / shot |
| `k_tile` | 0.5 per penetration | stock rail 4.5 + 1.5 = 6.0 / shot |
| `c_hover` | 0.001 per Thrust per s | Thrust 900 → 0.9 /s |
| `AirPremium` | 1.5 | reserved for `SpawnBlockInAir` |
| `a_min` / `v_min` | 40 px/s² / 150 px/s | design floor (chase pre-compensates Coulomb) / clears a one-tile step with margin |
| points | Rooted −2, memory 1, slam 1, rail 2, lash 1, spawn-in-air 1 | |
| default budget | Mass 2.5 · Slots 4 · Points 2 | |

Roster under it: Brick 2.437 (walk 45 px/s², at the mass cap) · Sprinter 1.866 (139 px/s², jump 214 px/s) · Gunner 1.140 · Flyer 1.269 (T/M 709 > 600) · Builder 1.569 · Turret 2.155 (points 1 after the rooted refund).
- 2026-09-30 · Phase 3 close-out (supervisor) · merged `fighter-3a-cost` (7cf107e) and finished what the worker could not after its stash was blocked: block kinds priced in `PhysicsCostModel.ActionCost` (PlaceBlock = BuildCost(material); SpawnBlockInAir = × AirPremium + 1 point) and wired in `FighterCompiler.CreateAction`; Builder gets Ranged + PlaceBlock + SpawnBlockInAir; `FighterSpec.Team` copied onto the blueprint. **Fixed the phase-2 walk bug the worker found:** `EnemyChaseState` capped its brake pre-compensation at |vCmd|, so a walker from rest never moved under stock friction; it now adds the full brake, and compiled fighters go back to FrictionScale 0.10 (the 0.02 workaround is gone). Fighter classes 70/70 + ConfigLayout 4/4 (after a Desktop build), snapshot/rollback 33/33, `combat` 338 pass / same 10 reds.
- 2026-09-30 · Phase 5 · (supervisor) · `FighterArena` lives in the library (`Entities/Enemies/Fighters/FighterArena.cs`, with `World/AsciiTerrain.cs` extracted from the test helper so tools can build terrain too): `Run(terrain, entries, playerSpawn, maxFrames, model)` compiles each entry under `FighterSlot0..7`, spawns in entry order, parks the player far left, ends when one team is dead or at maxFrames (draw scored on health fraction), and attributes damage dealt by diffing opponents' health (exact 1v1). Three terrains: flat, corridor (4-tile headroom), stepped hills. `FighterArenaTests` 4/4: a match replays identically; swapped sides replay identically; every archetype lands damage on every terrain; the 90-match round robin (6 archetypes × 3 terrains, both orderings, 720 frames each, ~3 s) has no archetype winning everything. First table: wins/30 — Brick 22, Gunner 17, Turret 13, Builder 11, Sprinter 10, Flyer 10. The Brick sits at the mass cap and still wins most, which says k_hp / k_arm are too cheap relative to k_gp — the first thing the coefficient loop should move. Fixed on the way: the air-block action read `ctx.Player` (null against a fighter after phase 4) — it and the overlap guard now use `ctx.Target` (which gained `Bounds`).
- 2026-09-30 · Phase 6 · branch `fighter-6-forge` · `dotnet run --project MTile.Bench -- --forge [--restarts R] [--steps S] [--seed N] [--frames N] [--both-orders] [--quiet]`. Search core library-side in `Entities/Enemies/Fighters/FighterForge.cs` (`FighterGenome` = spec + `ForgeBrain` choice; `RandomFeasibleSpec`/`RandomFeasible` rejection-sample until `Compile` is valid; `Mutate` changes one gene — Gaussian float at σ 15% of range or a 1-in-5 fresh draw, bool flip (rooting also sells locomotion), brain, preferred action, add/remove/replace/reorder an action, or nudge one action's Damage/Reach/MaxRange/Windup within ±50% of its default; `Evaluate` = win rate vs `FighterRoster.All` × `FighterArena.Terrains` × orders, draw ½, tie-break on mean health-fraction margin; `Search` = random restarts + hill climbing keeping children ≥ parent, one seeded `System.Random`, first candidate evaluated twice as a determinism check). `FighterSpecPrinter` writes a paste-able roster factory + one helper per non-default action (round-trip floats, genes rounded to 3 s.f.). `MTile.Bench/Forge.cs` is args + printing; loads impact/material/fighter configs like Game1. Defaults: 4 restarts × 30 steps, one spawn order (`--both-orders` doubles it), 720-frame matches ⇒ 18 matches / evaluation, 125 evaluations, **82 s** (0.66 s/eval, Debug). Seed 1 finds a rooted energy-ball kiter (armor 1.34, reserve 6.5, target memory, 440 px trigger) at fitness 0.833: 3-0 against every archetype except the Brick (0-3) — Rooted's 2-point refund is left unused (points −1), so the rooted discount looks too generous for a ranged kit. `FighterForgeTests` 4/4 (~0.5 s); Fighter{Forge,Arena,Cost} 43/43; `combat` 346 pass / the same 10 reds (ActionAimSolver ×3, CombatHitstun crush, SlashComboPresentation ×6).
- 2026-09-30 · Fight files + viewer (supervisor, user request) · `FightRecord` (`Entities/Enemies/Fighters/FightRecord.cs`): a bout as data — terrain ascii, `FighterSpecDto` per entry (brain named by `ForgeBrain`), positions, teams, frame cap, plus the result and a per-frame `Simulation.Checksum()` stream; `Record()` runs it, `Replay()` re-runs and names the first divergent frame, `ToStage()` builds a Game1 stage (`Stage.Terrain` now allows code-built terrain). `MTile.Bench -- --record-fight <left> <right> <flat|corridor|hills> <out>` writes one; `MTile.Desktop -- --fight <file>` shows it: camera on the fighters' centroid (`CosmeticUpdateSystem.CameraTarget`), `FightHud` readout + health pips, the take recorder auto-started so Ctrl+P scrubs the bout, F6/F7 pause/step. `MTILE_SCREENSHOT_FRAME=N` picks the auto-capture frame. `FighterRecordTests` 4/4 — including the load-bearing one: the game's stage sim matches the arena checksum for checksum. Samples in `Fights/`.
- 2026-09-30 · §16 iteration (supervisor) · **Sensing boundary:** `FighterController` (sealed `EnemyContext` entry) + `FighterSenses` — free: own state, `TargetCoarse()` (tile-snapped, ≥ CoarseLagFrames old), `Visible`, `TargetStale()`; paid from energy per call: `Target()` (exact + the tell, ReactionFrames old), `Probe(dir)`, `Cell()`; unpaid reads return the last bought value with its age. **Tell:** `EnemyTarget.Tell/TellProgress/TellAim/Facing` from `EnemyEntity.TellKind/TellProgress/TellAim`. **Reaction frames:** `TargetHistory` ring (8 views) + `SenseMemory` on the entity, snapshotted; `ReactionFrames` attribute priced by `KReact` per frame faster than the default. **Mandatory LOS:** every compiled fighter raycasts; `RemembersTarget` (bought `TargetMemory`) tracks a hidden target coarsely, otherwise it is frozen at last sight. **Density → radius:** `Radius` removed from the spec; `R = RadiusPerSqrtMass·√(Mass/Density)`, hitbox reach/extents scale with the body, trigger bands do not; ordering validated against the scaled reach. **Terrain probes:** drop / wall / headroom / grounded. Brains ported (closer dodges a seen strike; hover-dive rations a headroom probe). `IFighterPackage` + reflection discovery + `ExampleBrawler`, and `Plans/FIGHTER_PACKAGE_GUIDE.md` for competing authors. **Found and fixed:** `EnemyEntity._frame` was never snapshotted (now `EntityData.EnemyFrame`) — a latent rollback divergence for every patrolling bird. Corridor headroom corrected to six 11 px tiles. `FighterPerceptionTests` 6/6; all Fighter classes green; `combat` unchanged. Round robin now: Brick 20, Builder 19, Gunner 18, Turret 13, Sprinter 10, Flyer 6 (of 30).
- 2026-09-30 · League tooling (branch `fighter-league`) · `FighterLeague.Run` (library) + `MTile.Bench --league [--only N]… [--frames N] [--out dir] [--no-record] [--quiet]`: packages (by Name) then the roster, every ordered pair × 3 terrains through `FightRecord.Describe/Record` (126 bouts, 1.3 s Release incl. recording), score W=1/D=½ over bouts, tie-break mean health-fraction margin; per-fighter µs/Decide + paid reads/frame via `TimedController` (tool-only), flagged against new `DecideBudgetMicros` (20). `FighterSpecDto.Package` names a package brain so a recorded/viewed package fight keeps its brain (it silently became Closer before). `--record-fight` accepts package names. `ExampleBrawler` did not compile (Lunge MaxRange 110 > effective reach 83.6) — band now 80. `FighterPackageTests` (compile, fresh spec, determinism + record round trip, rollback, readonly brain, compute) + `FighterLeagueTests` 10/10 (7 + 3); Package|Arena|Record filter 15/15; `combat` 366 pass / 10 known reds. 300-frame ladder: Builder .806, Gunner .694, Brick .583, Turret .583, Sprinter .333, ExampleBrawler .250, Flyer .250.
