# Fighter package guide — how to build a competitive fighter

You are writing **one C# file** that defines a fighter: a body and kit (a `FighterSpec`)
plus the brain that pilots it (a `FighterController`). It fights other packages and the
stock roster in a deterministic headless arena. Win rate across opponents and terrains is
the score. Read this whole page before writing code.

## 1. The contract

- Create `Entities/Enemies/Fighters/Packages/<YourName>.cs`, `namespace MTile;`, with one
  public class implementing `IFighterPackage` (`Name`, `Author`, `Spec()`), a
  parameterless constructor, and your brain as a nested or same-file class extending
  `FighterController`. Copy `Packages/ExampleBrawler.cs` to start.
- **Touch nothing else.** No edits to shared files, the roster, the cost table, the
  arena, the sim, or the tests. The league discovers your class by reflection.
- `Spec()` returns a fresh `FighterSpec` each call. `Kind` is `EntityKind.FighterSlot0`
  (the arena re-points it). Leave `Sprite` null.
- Your spec must compile with zero violations under the default budget:
  `FighterCompiler.Compile(spec, new PhysicsCostModel())`. `CompileResult.Report()`
  prints the cost table and every violation. The budget today: **Mass 2.5, Slots 4,
  Points 2** (`configs/fighter_costs.json`).

## 2. What you can buy (`FighterSpec`)

Everything has a price in **mass**; mass is what the sim divides your power by. There is
no body-size knob: **radius is derived**, `R = 11·√(Mass / Density)`. Dense is small and
hard to hit (and costs mass above 1); light-for-size is big and free.

| Field | Does | Price |
|---|---|---|
| `Health` | HP pool (a stock melee hit is 1.0) | 0.10 mass / HP |
| `Strength` | multiplies every hitbox's damage | 0.40 / unit above 1 (min 0.25) |
| `Armor` | extra knockback divisor | 0.50 / unit |
| `EnergyReserve`, `EnergyRegen` | the meter: shots, dashes, flight, **and sensing** | 0.03 / unit, 0.15 / unit·s⁻¹ |
| `GroundPower` | walk accel = power / mass, top speed = √(power / 0.02) | 0.002 / unit (≥ 40 px/s² of accel or you cannot walk) |
| `JumpImpulse` | launch = impulse / mass (≥ 150 px/s or refused) | 0.00075 / unit |
| `Thrust` | flight accel = thrust / mass (**must exceed 600**, and leave margin to climb); drains `Thrust × 0.001` energy/s airborne; needs a reserve | 0.0005 / unit |
| `Cling` | wall/ceiling crawl (excludes Thrust) | 0.25 |
| `Density` | body size, see above (0.5–3) | 0.30 / unit above 1 |
| `ReactionFrames` | how old your exact reads are (0–7; default 6) | 0.04 mass per frame faster than 6 |
| `TargetMemory` | a hidden target is tracked coarsely through terrain instead of frozen where last seen | 1 point |
| `Rooted` | cannot move at all (no walk/jump/fly/cling) | **refunds** 2 points |
| `Sides` | polygon sides (cosmetic) | — |

Movement modes take slots: jump, fly and cling cost one each; walking is free.

### Actions (`Actions`, up to 4 total slots including movement modes)

Start from `ActionSpec.Default(ActionKind.X)` and change knobs. Reach / hitbox extents
scale with your body; **trigger bands (`MinRange`/`MaxRange`) do not**, and your brain's
`EngageRange` must sit under your melee `MaxRange`, which must sit under the effective
reach, or the compiler refuses (the fighter would swing at air).

| Kind | What | Energy per use | Points |
|---|---|---|---|
| `Melee` | forward swing, reach ~22 | 0 | 0 |
| `Lunge` | dash + body hitbox | 0.002 × speed | 0 |
| `Ranged` | energy ball, aimed at the target at windup start | 0.002 × speed × damage | 0 |
| `RailShot` | fast tile-breaking bolt | + 0.5 × penetration | 2 |
| `Slam` / `PounceSlam` | fall-triggered | 0 | 1 (needs jump or fly) |
| `Lash` | frozen-axis reach | 0 | 1 (needs cling) |
| `Contact` | touch damage | 0 | 0 |
| `PlaceBlock` | a tile at `Reach` along your aim | material build cost | 0 |
| `SpawnBlockInAir` | a tile over the target's head | 1.5 × build cost | 1 |

Windup / active / recovery are **free**: shorter windups are harder to read, but your
opponents can read them (below), so it is a bet, not a freebie. Projectiles from
different teams **destroy each other** on contact.

## 3. What your brain can know (`FighterSenses`)

Your brain's only input is a `FighterSenses` and a `ref BrainScratch` (4 floats, 2 ints —
your entire memory). Every call is deterministic.

**Free**
- `s.Self` — your position, velocity, health, energy, radius, facing, team, whether
  you are mid-action or staggered, and your own action's windup progress.
- `s.Dt`, `s.Frame`.
- `s.TargetCoarse()` — the opponent's position snapped to the tile grid, **at least 6
  frames old**, plus health and team. No velocity, no tell.
- `s.Visible` / `s.HiddenSeconds` — whether line of sight holds right now.
- `s.TargetStale(out age)` — the last exact read you bought, and how old it is.
- `s.TargetCost`, `s.ProbeCost`, `s.CellCost` — prices, so you can ration.

**Paid from your energy meter** (an unpaid call returns the stale value)
- `s.Target(out age)` (0.02) — exact position and velocity **ReactionFrames old**, and the
  **tell**: `Tell` (the action kind the opponent is performing), `TellProgress` (0..1
  through the windup, 1 = active, −1 idle), `TellAim`, `Facing`. This is how you dodge.
- `s.Probe(dir)` (0.05) — along `dir` (±1): distance to the first drop, to the first
  wall, headroom in tiles above you, whether you are grounded.
- `s.Cell(gtx, gty)` (0.01) — one tile's state.

**Sight.** Everyone raycasts. While the target is hidden, your reads return it frozen
where it was last seen (or, with `TargetMemory`, tracked to the nearest tile with no
tell). `TellProgress` is only ever real while `Visible`.

## 4. What your brain outputs (`EnemyInput`)

`MoveDir` (x for walking, full vector for flying/clinging), `Jump`, `AimWorld` (drives
facing and ranged aim), `WantAttack` (global permission), `RequestedAction` (index into
your `Actions`, or `null` for "whatever passes"). A request is a **restriction**: asking
for an out-of-range action attacks nothing.

The sim still decides: an action fires only if its precondition passes (range band,
fall speed, line of sight for rail shots) and you can pay its energy.

## 5. Rules that get you disqualified

- Reading anything but `FighterSenses` and your `BrainScratch`. The type system blocks
  `EnemyContext`; do not reach around it with statics or reflection.
- Any mutable field on your controller, `System.Random`, wall-clock time, or allocation
  per frame. A package that fails the determinism test (snapshot → run → restore →
  replay must be bit-identical) is out.
- A brain over the compute budget: the league reports microseconds per `Decide`; stay
  under **20 µs** on average.
- Editing any file other than your own.

## 6. How to test it

```bash
dotnet build MTile.Core.csproj                                   # does it compile
dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~FighterPackage"   # compiles under budget, deterministic, compute
dotnet run --project MTile.Bench -- --record-fight <YourName> Brick flat Fights/mine.fight.json   # one bout, 720 frames
dotnet run --project MTile.Bench -- --league --only <YourName>  # you vs everyone, all terrains, both sides
dotnet run --project MTile.Desktop -- --fight Fights/mine.fight.json   # watch it (F6 pause, F7 step, Ctrl+P scrub)
```

The arena: three terrains (flat, roofed corridor with four tiles of headroom, stepped
hills), opponents start 180 px apart, 720 frames per bout, a timeout is scored on
health left. `FighterArena.Run` is the exact scorer. The stock roster (Brick, Sprinter,
Gunner, Flyer, Builder, Turret in `FighterRoster.cs`) and `ExampleBrawler` are the
opponents you must beat; their brains (`FighterBrains.cs`) are deliberately simple.

## 7. Things worth knowing

- A 1v1 on flat ground is a knife fight: the stock Brick wins most of them by being
  heavy. Terrain, timing and the tell are where a better brain earns its wins.
- Energy is one meter for attacks, flight and looking. A brain that reads the exact
  target every frame spends 1.2 energy/s; the stock Gunner's shot costs 1.0.
- Coarse reads are at least 6 frames (0.1 s) old and tile-snapped. Exact reads are
  `ReactionFrames` old. Buying 0 reaction frames costs 0.24 mass.
- Knockback divides by mass + armor; a heavy fighter is hard to launch.
- The brain of the stock `FighterCloserBrain` shows the pattern: a mode in `I0`, a timer
  in `F0`, a dodge triggered by `TellProgress`.
