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
| `Strength` | multiplies melee damage (swing, lunge, lash, slam, contact); **shots are not scaled** | 0.40 / unit above 1 (min 0.25) |
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

**Windup and recovery are fixed per kind** (the values in `ActionSpec.Default`: melee
0.45 / 0.40 s, lunge 0.35 / 0.45, ranged 0.60 / 0.50, rail 1.35 / 1.15, …). The compiler
overrides whatever you set and says so in the `--spec` report. Timing is the one thing
every opponent can read (below), so it is the arena's, not yours. `Active` is still
yours: for a lunge it is the dash length and it is priced. Projectiles from different
teams **destroy each other** on contact.

**The ordering rule, worked.** For every melee-band action the compiler checks
`effective reach > MaxRange > EngageRange`, with the reach measured on YOUR body
(geometry × Radius / 12). `FighterCompiler.EffectiveReach`:

| Kind | Effective reach (centre to centre) |
|---|---|
| Melee | `8 + Reach·k + 6` |
| Lunge | `Speed·Active + HalfWidth·k + 6` |
| Lash | `Reach·k + 6` |

where `k = Radius / 12` and 6 is the assumed target half-width. A stock lunge (speed 260,
active 0.25, half-width 12) on a 10 px body reaches `65 + 10 + 6 = 81`, so its
`MaxRange` must be under 81 — the default 90 is refused. Compute your radius first
(`CompileResult.Blueprint.Radius`, or `11·√(Mass/Density)`), then set the bands.

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
- `s.Random()` (uniform in [0, 1)), `s.Random(lo, hi)`, `s.RandomInt(n)`, `s.Chance(p)` —
  **the only randomness you may use.** It is a seeded stream whose state is snapshotted
  with your sense memory: the bout seeds it (so the same seed replays the same fight,
  bit for bit), a rollback rewinds it, and your two sides of a mirror match never share
  it. Use it to mix up dodges, timings and approach angles so a read-and-punish brain
  cannot learn you. `ExampleBrawler` draws on it to pick a hop or a step.

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
- Any mutable field on your controller, `System.Random` (or any randomness other than
  `s.Random()`), wall-clock time, or allocation per frame. A package that fails the
  determinism test (snapshot → run → restore → replay must be bit-identical) is out.
- A brain over the compute budget: the league reports microseconds per `Decide`; stay
  under **20 µs** on average.
- The compute budget is `DecideBudgetMicros` in `configs/fighter_costs.json`
  (`FighterCostConfig.DecideBudgetMicros`, default 20); FighterPackageTests and the league
  measure it by wrapping your brain in `TimedController` (mean wall-clock µs per `Decide`).
- Editing any file other than your own.

## 6. How to test it

```bash
dotnet build MTile.Core.csproj                                   # does it compile
dotnet run --project MTile.Bench -- --spec <YourName>            # cost report: mass/slots/points per part, violations, radius, speeds
dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~FighterPackage"   # compiles under budget, deterministic, compute
dotnet run --project MTile.Bench -- --record-fight <YourName> Brick flat Fights/mine.fight.json --frames 720   # one bout, as the league plays it
dotnet run --project MTile.Bench -c Release -- --league --only <YourName>  # you vs everyone, all terrains, both sides
dotnet run --project MTile.Desktop -- --fight Fights/mine.fight.json   # watch it (F6 pause, F7 step, Ctrl+P scrub)
```

League options: `--only <Name>` (repeatable), `--frames N` (default 720), `--seeds N`
(default 1) and `--first-seed N` (default 0), `--out <dir>` (default `Fights/league`: one
`<A>_vs_<B>_<terrain>[_s<seed>].fight.json` per bout plus `league.md`), `--no-record`,
`--quiet`. Use `-c Release` — a Debug build inflates the µs/Decide column.
`--record-fight` takes a roster name, a package name, or a `.fight.json` for either side,
plus `--seed N`; without `--frames` it runs the arena default of 1800 frames.

The arena: three terrains (flat, roofed corridor with six tiles of headroom, stepped
hills). The league defaults to 720 frames per bout; competition scoring uses 1800 (§12). **Only an elimination wins; a timeout is a draw whatever
the health left**, and the ladder breaks ties on name, never on margin — so there is no
such thing as landing one hit and hiding. **A draw scores 0.4 for both sides** (0.5 −
`DrawPenalty` 0.1, `configs/fighter_costs.json`, plan §11.2): draws drain points, so the
side ahead wants to finish and a fighter built only to run converges below average.
`FighterArena.Run` is the exact scorer. **A bout is seeded** (`ArenaCondition`): seed 0 is the canonical
start, opponents 180 px apart at fixed spots; every other seed draws the separation
(120–250 px) and slides the pair along the ground (±60 px, so on the hills either side
may start on a step), and seeds each brain's `s.Random()` stream. The real score is the
`--seeds 16` league: the mean over seeds with its standard error (`±se`) and range, and
`league.md` also tabulates your score under each seed. A fighter tuned to the canonical
start and nothing else shows up there as a wide range. The stock roster (Brick,
Sprinter, Gunner, Flyer, Builder, Turret in `FighterRoster.cs`) and `ExampleBrawler` are
the opponents you must beat; their brains (`FighterBrains.cs`) are deliberately simple.

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

## 8. Lessons from wave one (six agents, 2026-09-30)

Everything here was learned the hard way by the first six packages. Read it as the
current meta, which the next wave exists to break.

- **Windup, active and recovery were the biggest lever and they were free.** A 0.1 s melee
  windup landed before a ReactionFrames-old read could trigger the stock dodge; a 0.3 s rail
  windup fired three times as often and gave nothing to dodge. **Closed 2026-10-01:**
  windup and recovery are now fixed per kind (§2); only Active is yours.
- **The rooted refund is big.** Rooted (−2 points) pays for a RailShot and target memory
  with a point to spare, and the rail's 6 energy per shot is covered by a reserve of ~14
  and 2/s regen within mass. That is the wave-one champion (Sentinel, 0.93). It cannot
  move: closing on it is the counter, if you survive the line.
- **Speed is `√(GroundPower / 0.02)`**: 120 power is 77 px/s, 220 is 105 px/s. Kiters
  hold ~100 px, so a melee body under ~100 px/s never catches them. Jump floors divide by
  TOTAL mass (`JumpImpulse / Mass ≥ 150`), so a heavy spec needs 370+ impulse.
- **Flight cruises at 80 px/s** (`EnemyFlyState.CruiseSpeed`) whatever the thrust; thrust
  only buys climb and turn. A flyer cannot chase a kiter.
- **A Lunge costs ~0.35 mass** at stock damage and speed (reach price plus the energy
  reserve and regen it needs), not the 0 the action table suggests.
- **Mixed kits:** the brain's `EngageRange` must sit under every melee action's `MaxRange`
  (ordering rule), so a kiter with a jab keeps `EngageRange` small and does its standoff
  logic itself.
- **Projectiles are 1D.** A shot is aimed at where you were at windup start, so closing
  along its line does not dodge it; a timed jump does (fire when the estimated arrival is
  0.2–0.5 s out). Backing off along the line just loses ground.
- **Tells vanish with line of sight.** On the hills a ledge hides the target for stretches;
  a brain that waits for a tell on a ledge stands still. Walk toward the last view, jump
  when stalled.
- **Timeouts were scored on health fraction** in waves one and two, so early damage plus
  a blinding wall was a win. **Closed 2026-10-01:** that was never the plan's rule; a
  timeout is now a draw, full stop (§6). Walling only pays if you also finish.
- **`RequestedAction` is a restriction.** Requesting a PlaceBlock out of its 40–160 band
  attacks nothing, and requesting it in a loop walls forever. Pick explicitly.
- **Energy rationing wins.** Exact reads every 3rd frame when close, every 6th when far,
  stale reads in between; every brain that read every frame starved its own meter.
- **`SpawnBlockInAir` does no damage.** The block hangs. It blocks sight lines and dives,
  nothing more.

## 9. Lessons from wave two (six more agents, 2026-10-01)

- **Strength was the cheapest damage in the game.** It multiplied every hitbox at publish
  time, including a rail bolt, and costs 0.40 mass per unit above 1 with NO energy price —
  energy scales with the spec's Damage, not Strength. Bulwark (0.96, 104-0-4): a rooted
  rail with spec Damage 1.0 (4.5 energy a shot) and Strength 3.6, so the bolt landed for
  3.6 and most of the field died at frame 12, the first possible shot. **Closed
  2026-10-01:** Strength no longer scales projectiles (§2); a shot's damage is its
  spec Damage, the number its energy price is built on.
- **Walls still beat one-shot turrets.** Bulwark's only losses are to Sapper, which
  advances behind placed blocks and won the timeout on health (a rule since retired —
  §6). Line of sight is the one thing a rail cannot buy around.
- **A shooter on a crawler works.** Lancer (0.95) is a Cling body — fixed 60 px/s crawl,
  no jump, no walk speed — with a 0.25 s shot, a lash and a jab; its balls shoot down
  incoming balls and it never stops advancing. It loses only to Bulwark.
- **A Ranged action costs ~0.11 mass**, regardless of speed or damage; the table's "0"
  is wrong. Its energy cost is 0.002 × speed × damage per shot.
- **Health and strength are a cheaper counter to shooters than dodge logic.** Phalanx
  (0.88) took Harrier's chassis, swapped in an 800 px/s 0.25 s shot, and spent the rest on
  Health 6 and Strength 1.3; that beat the wave-one champion without a smarter dodge.

## 10. What the seeded league changed (16 seeds, 2026-10-01)

`Fights/wave2_seeded_league.md` is the same 19-fighter field under 16 seeds (16,416
bouts, 117 s). Read it before trusting any single-seed number.

- **The top three are real.** Bulwark 0.963 ± 0.008, Lancer 0.935 ± 0.003, Phalanx
  0.884 ± 0.003 — their worst seed is still above everyone else's best. Bulwark beats
  Lancer 16-0 on flat and corridor from every start; only the hills (10-1-5) give Lancer
  a line.
- **Seed 0 was the field's best seed.** Sentinel, Wraith, Lancer and Skirmish all scored
  their maximum on the canonical start, because that is the start every author tuned
  against. Sentinel fell from 0.630 to 0.558 and from 7th to 9th; Mason rose from 0.574
  to 0.697 and from 9th to 6th. One deterministic round robin is a sample of one.
- **Separation is a dimension.** Mason climbs from 0.67 at 120–160 px to 0.75 at 200–250
  (room to wall); Sentinel falls from 0.57 to 0.51 over the same range; Builder gains
  0.09. A brain whose score moves with the start distance has an opening it has not
  solved.
- **Three quarters of matchups are decided, a quarter are coin flips.** 784 of 1026
  (ordered pair × terrain) ended the same way under every seed. Bulwark (left) vs Sapper
  on flat is 8-8: the canonical "Sapper beats Bulwark" was one draw from that coin.
- **The spread is small for most.** Typical `±se` is 0.003–0.008 over 16 seeds; Mason
  (0.015) and Builder (0.013) are the condition-sensitive ones. Sixteen seeds are enough
  to rank; fewer than eight are not.

## 11. Retune 1: fixed timing, no Strength on shots (2026-10-01)

Two rules changed after wave two (§2): windup and recovery are fixed per kind, and
Strength no longer scales projectiles. `Fights/wave2_fixedtiming_league.md` is the
16-seed league with the first change alone, `Fights/wave2_retuned_league.md` with both.

- **Bulwark 0.963 → 0.389 (16th).** Timing alone took it to 0.799; losing the 3.6×
  bolt did the rest. A rooted rail now fires every 2.5 s for 1.0 damage.
- **Lancer 0.886 and Phalanx 0.880 are the new top**, Harrier 0.784 third: fast
  shooters on mobile bodies. Kestrel rose from 14th to 5th (0.568) — a flyer that
  could never beat an instant rail can beat a slow one.
- **Sapper 0.708 → 0.483.** Its walls only paid against a one-shot rail; against the
  field they are a slower way to lose.
- **Draws matter now.** Under the old health-on-timeout rule 1298 of 16,416 bouts still
  drew and 1071 of them had no damage at all. Builder, Mason and Harrier vs Lancer, and
  Breacher vs Brick, draw on every seed and terrain: with a 0.6 s windup a jump-dodge or
  a wall answers every shot inside 720 frames. With timeouts now always draws (§6) it is
  3487 of 16,416 (21%, `Fights/wave2_draws_league.md`, scored at draw = 0.4): Builder
  fell from 0.535 to 0.358 and Gunner from 0.528 to 0.329, each with over 900 draws,
  while Lancer 0.855, Phalanx 0.838 and Harrier 0.759 kept the top three. At 1800
  frames (`Fights/wave2_long_league.md`: Lancer 0.887, Phalanx 0.876, Harrier 0.791)
  draws drop to 15% and a sixth of the kills land after frame 720, but 667 bouts still
  end with no damage: kiter against kiter, wall against shooter. A brain that cannot
  finish does not score, at any clock, and under the penalty every draw costs 0.1 of a
  point relative to the old ½ — Mason loses 0.04 to its 702 draws.

## 12. The competition protocol (from wave 3)

From wave 3 you are scored against fighters you cannot see.

**What you can see.** Your worktree is `fighter-plan` with half the existing packages
deleted. What is left is the Dev half: the six roster bots, `ExampleBrawler`, and about half
the other packages, dealt by rank so both halves span the meta. Every tool works on it: the
league, `--forge`, `--spec`, `--record-fight`, the viewer. The past ladders in
`Fights/*_league.md` and the lessons in §8 to §11 stay: they name the hidden bots and what
beat them. That is published history; use it.

**What you cannot.** The other half's code. Their files are not in your tree, so no tool
sees them. The git history still holds them. Do not read it: no `git log`, `git show` or
checkout of anything under `Packages/` that is not in your tree. The supervisor spot-checks,
and a package built from hidden code is out.

**How you are scored.** Your package plays the hidden half (Holdout), every opponent, all
three terrains, both sides, 1800 frames per bout, on seeds 100..115. You develop on seeds
0..15 (the league's default range); the scoring seeds are fresh, so no spawn geometry can be
tuned to. Win 1, loss 0, draw 0.4, a timeout is a draw. Your holdout score is your rank. The
same run against the Dev half is reported beside it, with the gap (dev minus holdout). Ties
on the holdout score break on a head-to-head league among this wave's submissions, then on
name. Nobody re-plays the pool among itself; it is you against the pool.

**What overfitting costs you.** Nothing you tune against a visible opponent counts unless it
also works on the hidden ones. A brain that reads one Dev bot's habits, or a spec sized to
beat the visible top three, scores on the Dev line and loses on the line that ranks. Build a
fighter that beats the kinds of opponent the ladders describe: kiters, rails, walls,
crawlers, brawlers. Test across all 16 dev seeds, not seed 0; a wide range there is the gap
you will show on the holdout. A large positive gap is reported, and it says you tuned to
opponents instead of designing.

**What to deliver.** One new file, `Entities/Enemies/Fighters/Packages/<YourName>.cs`, with
the class's `Name` equal to the file name, committed on your branch. Nothing else touched:
the supervisor runs `git diff --name-only` from the base to your branch, and anything but
that one added file rejects the whole branch. The rules of §1 and §5 hold: it compiles with
zero violations under the default budget, it is deterministic, and it stays under the
compute budget. A spec that does not compile is disqualified, not ranked.

Supervisor's side of all this: `Plans/FIGHTER_COMPETITION.md`.

## 13. Wave three: holdout scoring and contact coverage

Full results: [`Fights/competition/wave3.md`](../Fights/competition/wave3.md);
submission provenance, class results and caveats: [wave-3 progress](../Fights/competition/wave3_progress.md).

- **A saturated dev field does not settle the competition.** Official scores use fresh
  seeds 100–115. Thornback and Stonecreep score 0.934 and 0.925 against Dev but only
  0.704 and 0.699 against Holdout. Twinbarrel reaches 0.992 holdout; the contact rammers
  Thornbull and Thornrush reach 0.985 and 0.977. Preserve the fixed pool and report the gap.
- **Contact reach is a powerful purchase under this cost model.** Gravemantle spends
  0.98 total mass on two Contact actions, each Damage 0.35 / HalfWidth 280. Density 0.5
  gives radius 24.5 and scales each box to roughly 572 px half-extent. This buys coverage
  across typical spawn separations without projectile travel or windup. It scores
  1.000 against both the hidden and visible pools. This is a pricing finding, not
  evidence that contact geometry is balanced.
- **Fixed timing does not fix action throughput when priorities are author-controlled.**
  Twinbarrel alternates ranged actions after firing to skip recovery. Gravemantle
  alternates its two contact actions every frame with PassivePriority 13 > ActivePriority
  12. The compiler accepts both. Close this loophole in a later version and preserve
  this wave's results against the version actually scored.
- **Knockback can prevent a finish.** Gravemantle's development experiments produced
  draws when repeated hits launched targets beyond reach; zero knockback retained them
  in the damage field. Its rejected single-contact design also lost to synthetic rammers
  despite a perfect dev score. Test plausible stronger opponents as well as the public pool.
- **Keep protocol caveats visible.** Codex read the wave progress and exploit findings
  with the user's permission before submitting one bot. The four Claude classes each
  submitted two, with the earlier brief discrepancy documented in the progress file.
  These are competition results, not a controlled model comparison.
