# Enemy Capability Framework — Design Spec

**Status:** draft / not yet implemented. 

**Goal:** a parameterized capability layer for a marquee NPC ("the boss") — pre-coded
attacks, terrain plays, telegraphs, and resource/limitation systems — over which we
write *arbitrary controller code* to make behavior interesting and challenging. The
moves are dumb and reusable; the controller is the brain.

## Design constraints (non-negotiable, from the existing engine)

1. **Determinism + snapshot-safe.** The sim is rollback-bound (`Plans/ROLLBACK_ROADMAP.md`).
   Every bit of AI/move/controller state must be plain data captured in a snapshot, and
   advance purely from `(state, input, FixedDt)`. **No `System.Random`, no wall-clock, no
   sim-affecting statics.** Randomness comes from a *seeded, snapshotted* PRNG (`DetRng`).
3. **Reuse the engine's own pattern.** This deliberately mirrors `PlayerCharacter` +
   `ActionState`/`ActionVars`: **flyweight move objects + a plain-data per-activation
   vars struct + a registry**. If you understand the player action FSM, you understand
   this. Snapshotting "just works" the same way (registry index + value struct).
4. **Sim owns timing; render owns pixels.** A telegraph's *timing and danger* is sim
   state; its *appearance* is cosmetic and drawn by `Game1`, never fed back.

## Layering

```
   EnemyController  (the brain — arbitrary, swappable logic)
        │ chooses / scores moves, reads a plain-data Blackboard + DetRng
        ▼
   EnemyMove (flyweight)   ── Windup → Active → Recovery, publishes hitboxes,
        │ per-activation     drives the body, spawns projectiles/terrain,
        │ state in           declares its Telegraph + resource cost + gates
        ▼ EnemyMoveVars
   EnemyResources   stamina · charge meter · posture/poise · cooldowns · charges
   Telegraph        sim-side tell descriptor (kind, progress, pos) → Game1 renders
        ▼
   BossEntity : Entity   host — owns registry + vars + resources + blackboard + rng,
                         captured/restored via WriteState/ReadState
```

Mapping to existing analogues so it's familiar:

| This framework | Mirrors in player code |
|---|---|
| `BossEntity` | `PlayerCharacter` |
| `EnemyMove` (flyweight) | `ActionState` |
| `EnemyMoveVars` (value struct) | `ActionVars` |
| `_moveRegistry` + current index | `_actionRegistry` + `_currentAction` |
| `EnemyController.ChooseMove` | the action-FSM precondition/priority scan |
| `EnemyContext` | `EnvironmentContext` |
| `BossSnapshot` | `PlayerSnapshot` |

---

## 1. `BossEntity : Entity`

The host. An `Entity` subtype (`EntityKind.Boss`) so it rides every existing system —
physics body, `IHittable`/hurtbox, `CombatSystem`, knockback, snapshot — for free.

```csharp
public sealed class BossEntity : Entity
{
    public override EntityKind Kind => EntityKind.Boss;

    // Flyweight moves, constructed once in a fixed order (index == snapshot identity).
    private readonly List<EnemyMove> _moves = new();
    private int _currentMove = -1;               // -1 == idle
    private EnemyMoveVars _vars;                  // per-activation state (value struct)

    private EnemyResources _resources;            // meters / cooldowns (value struct)
    private DetRng _rng;                          // seeded, snapshotted PRNG
    private EnemyController _controller;           // brain (logic only; data is below)
    private ControllerBlackboard _brain;          // controller's plain-data memory
    private Telegraph _telegraph;                  // current tell (sim side)
    private int _facing = -1;
    private int _frame;

    public Telegraph CurrentTelegraph => _telegraph;   // read-only, for Game1
    public float Posture => _resources.Posture;        // for a HUD bar

    public BossEntity(Vector2 pos, int seed)
        : base(new PhysicsBody(Polygon.CreateRegular(14f, 6), pos), health: 40f)
    {
        Mass = 6f; Faction = Faction.Enemy; Color = Color.MediumPurple;
        Sprite = Sprites.Boss(14f);            // add a sprite in Sprites.cs
        _rng = new DetRng(seed);
        _resources = EnemyResources.Initial();
        _controller = new PhasedController();   // swap this to retune behavior

        // Order matters only as snapshot identity + a tiebreak; gates do real selection.
        _moves.Add(new HeavyOverhead());     // 0
        _moves.Add(new PillarUppercut());    // 1
        _moves.Add(new ChargedBolt());       // 2
        _moves.Add(new WallRaise());         // 3
        _moves.Add(new DashReposition());    // 4
        // …
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        _frame++;
        var chunks = (spawner as IChunkProvider)?.Chunks;   // same cast LobbedArea uses

        var ctx = new EnemyContext {
            Dt = dt, Frame = _frame, Self = this, Player = player,
            Hitboxes = hitboxes, Spawner = spawner, Chunks = chunks,
        };

        _resources.Tick(dt);                 // bleed cooldowns, regen stamina/charge

        // Face the player only while NOT mid-move (committed moves lock facing in OnStart).
        if (_currentMove < 0)
        {
            var dx = player.Body.Position.X - Body.Position.X;
            if (MathF.Abs(dx) > 1f) _facing = dx >= 0 ? 1 : -1;
        }

        // 1) If a move is running, advance it. It reports DONE / CANCELLABLE / COMMITTED.
        MoveStatus status = MoveStatus.Idle;
        if (_currentMove >= 0)
            status = _moves[_currentMove].Tick(ctx, ref _vars, ref _resources, ref _rng, ref _telegraph, _facing);

        // 2) Let the controller pick a new move when idle or in a cancellable tail.
        if (_currentMove < 0 || status == MoveStatus.Done || status == MoveStatus.Cancellable)
        {
            int next = _controller.ChooseMove(ctx, _moves, in _resources, ref _brain, ref _rng, status);
            if (next >= 0 && next != _currentMove && _moves[next].CanStart(ctx, in _resources))
            {
                if (_currentMove >= 0) _moves[_currentMove].OnExit(ctx, ref _vars, ref _telegraph);
                _currentMove = next;
                _vars = default;
                _moves[next].OnStart(ctx, ref _vars, ref _resources, _facing);  // pays cost, locks facing, sets telegraph
            }
            else if (status == MoveStatus.Done)
            {
                _moves[_currentMove].OnExit(ctx, ref _vars, ref _telegraph);
                _currentMove = -1;
                _telegraph = Telegraph.None;
            }
        }
    }

    // Posture/poise + stagger reaction (see §4). Base applies HP damage + knockback.
    public override void OnHit(in Hitbox hit, in Hurtbox myHurtbox)
    {
        base.OnHit(hit, myHurtbox);
        if (IsDead) return;
        _resources.Posture -= hit.KnockbackImpulse.Length() * EnemyResources.PostureLossPerImpulse;
        // Interrupt non-armored moves; a posture break forces a Staggered move.
        bool armored = _currentMove >= 0 && _moves[_currentMove].HasArmor(in _vars);
        if (_resources.Posture <= 0f) { ForceMove(MoveIds.Staggered); _resources.Posture = 0f; }
        else if (!armored && _currentMove >= 0) { ForceMove(MoveIds.Flinch); }
    }
}
```

`Update` returning a `MoveStatus` from `Tick` is the **commitment lever**: a `Committed`
heavy can't be cancelled (guaranteeing your punish window); a `Cancellable` tail lets the
controller chain or bail.

---

## 2. `EnemyMove` (flyweight) + `EnemyMoveVars`

Stateless logic, like `ActionState`. All per-activation state lives in the `ref`-passed
`EnemyMoveVars`, so capture is a struct copy.

```csharp
public enum MovePhase  : byte { Windup, Active, Recovery }
public enum MoveStatus : byte { Idle, Running, Committed, Cancellable, Done }

public abstract class EnemyMove
{
    // Frame/second budgets — the windup/active/recovery triad. Telegraph length is
    // usually == Windup; difficulty tuning shortens windup, not just +damage.
    public abstract float Windup   { get; }
    public abstract float Active   { get; }
    public abstract float Recovery { get; }

    // Which cooldown family this move belongs to (so the controller can't chain two
    // heavies). And what it costs to start.
    public abstract CooldownFamily Family { get; }
    public virtual  float StaminaCost  => 0f;
    public virtual  float ChargeCost   => 0f;

    // Gate: range band, grounded-only, meter availability, phase unlock, cooldown ready.
    public abstract bool CanStart(in EnemyContext ctx, in EnemyResources res);

    // Lock facing, pay costs, arm telegraph, snapshot aim/target into vars.
    public abstract void OnStart(in EnemyContext ctx, ref EnemyMoveVars v, ref EnemyResources res, int facing);

    // Per-frame: advance phase timer, publish hitboxes during Active, drive body,
    // spawn projectiles/terrain. Returns status (Committed during heavy windup/active).
    public abstract MoveStatus Tick(in EnemyContext ctx, ref EnemyMoveVars v,
                                    ref EnemyResources res, ref DetRng rng,
                                    ref Telegraph tel, int facing);

    public virtual void OnExit(in EnemyContext ctx, ref EnemyMoveVars v, ref Telegraph tel) {}

    // Hyperarmor: if true, OnHit won't interrupt this move (still takes damage).
    public virtual bool HasArmor(in EnemyMoveVars v) => false;

    // ── shared helpers every concrete move reuses ───────────────────────────────
    protected static MovePhase PhaseOf(in EnemyMoveVars v, float windup, float active)
        => v.TimeInMove < windup ? MovePhase.Windup
         : v.TimeInMove < windup + active ? MovePhase.Active : MovePhase.Recovery;

    // Mint a single HitId on Active-entry so a multi-frame hitbox dedupes to one hit.
    protected static void EnsureHitId(in EnemyContext ctx, ref EnemyMoveVars v)
    { if (v.HitId == 0) v.HitId = ctx.Spawner.HitIds.Next(); }
}

// Superset of every move's per-activation fields, unioned (mutually exclusive in time),
// exactly like ActionVars. Value type → struct-copy snapshot.
public struct EnemyMoveVars
{
    public float   TimeInMove;
    public int     HitId;
    public int     LockedFacing;
    public Vector2 Aim;          // ranged: locked aim at windup end
    public Vector2 TargetCell;   // terrain moves: where to erupt
    public float   Charge;       // chargeable moves
    public int     SubCount;     // barrage shot index, spike segment, etc.
    public int     SubTimer;     // frames since last sub-emit
    public bool    Fired;        // one-shot guards
}
```

---

## 3. `EnemyContext`

Per-`Update` bundle, analogous to `EnvironmentContext`. Built fresh each frame, never
stored, so it carries no snapshot weight.

```csharp
public readonly struct EnemyContext
{
    public float Dt; public int Frame;
    public BossEntity Self;
    public PlayerCharacter Player;
    public HitboxWorld Hitboxes;
    public IEntitySpawner Spawner;     // .HitIds, .SpawnEntity
    public ChunkMap Chunks;            // terrain reads/writes (may be null in tests)

    public Vector2 ToPlayer => Player.Body.Position - Self.Body.Position;
    public float   Dist     => ToPlayer.Length();
}
```

---

## 4. `EnemyResources` — meters, cooldowns, limits

All the "what makes it interesting" dials. Plain data; ticks deterministically.

```csharp
public enum CooldownFamily : byte { None, Light, Heavy, Ranged, Terrain, Mobility, COUNT }

public struct EnemyResources
{
    public float Stamina;       public const float StaminaMax = 100f;
    public float Charge;        public const float ChargeMax  = 100f;   // builds → ultimate
    public float Posture;       public const float PostureMax = 100f;   // stagger bar (§1.OnHit)
    public int   DashCharges;                                            // discrete ammo

    // One timer per family; a move sets its family's timer on start, Tick bleeds them.
    // Fixed-size array → trivially snapshotted (deep-copied on capture).
    public float[] Cooldowns;   // length (int)CooldownFamily.COUNT

    public const float PostureLossPerImpulse = 0.15f;
    public const float StaminaRegenPerSec = 18f;
    public const float ChargeGainPerSec   = 6f;     // race the player to interrupt
    public const float PostureRegenPerSec = 8f;     // regens when not recently hit

    public static EnemyResources Initial() => new() {
        Stamina = StaminaMax, Charge = 0f, Posture = PostureMax,
        DashCharges = 2, Cooldowns = new float[(int)CooldownFamily.COUNT],
    };

    public void Tick(float dt)
    {
        Stamina = MathF.Min(StaminaMax, Stamina + StaminaRegenPerSec * dt);
        Charge  = MathF.Min(ChargeMax,  Charge  + ChargeGainPerSec   * dt);
        Posture = MathF.Min(PostureMax, Posture + PostureRegenPerSec * dt);
        for (int i = 0; i < Cooldowns.Length; i++)
            if (Cooldowns[i] > 0f) Cooldowns[i] = MathF.Max(0f, Cooldowns[i] - dt);
    }

    public bool Ready(CooldownFamily f) => Cooldowns[(int)f] <= 0f;
    public void Trigger(CooldownFamily f, float seconds) => Cooldowns[(int)f] = seconds;
}
```

This single struct already expresses every limitation from the brainstorm: stamina-gated
flurries, a charge meter racing toward an ultimate, posture/poise stagger, discrete dash
charges, and per-family cooldowns that stop two heavies chaining.

---

## 5. `Telegraph` — the sim↔render bridge

The tell's *existence, timing, and geometry* are sim state (deterministic, snapshotted);
its pixels are drawn by `Game1` and never read back. This keeps "challenging but fair"
honest: the danger window is authoritative, not a render artifact.

```csharp
public enum TelegraphKind : byte { None, ChargeRing, GroundMarker, DangerLine, LandingReticle, Beam, Flash }

public struct Telegraph
{
    public TelegraphKind Kind;
    public float   Progress;   // 0→1 across windup; Game1 maps to ring radius / fill / alpha
    public Vector2 Pos;        // world-space anchor
    public Vector2 Dir;        // for lines/beams
    public float   Radius;     // for rings / AoE footprints
    public float   Severity;   // 0..1 → color ramp + screen-shake amplitude in Game1
    public static readonly Telegraph None = default;
}
```

Game1 hook (cosmetic, in the existing Draw, world-space pass):
```csharp
foreach (var e in _sim.Entities)
    if (e is BossEntity boss) DrawTelegraph(boss.CurrentTelegraph);
```
`DrawTelegraph` is pure rendering — reuse `DrawContext.Ring/Line`, `Effects`, screen shake.

---

## 6. Determinism & snapshot integration (the one coordination point)

`BossEntity` carries more state than the flat `EntitySnapshot` union holds. Rather than
bloat that struct with boss-only fields, add **one nullable reference** to it and let the
boss fill it in `WriteState`/`ReadState` (the existing subtype hook). This is the **only**
edit to a file the main agent owns — flag it in the PR.

```csharp
// In EntitySnapshot (the single shared edit):
public BossSnapshot Boss;   // null for non-boss entities; deep-copied on capture

// New file BossSnapshot.cs — all plain data, no live refs:
public sealed class BossSnapshot
{
    public int CurrentMove; public EnemyMoveVars Vars; public EnemyResources Resources;
    public DetRngState Rng;  public ControllerBlackboard Brain;
    public Telegraph Telegraph; public int Facing; public int Frame;
}

// In BossEntity:
protected override void WriteState(ref EntitySnapshot s) {
    base.WriteState(ref s);
    s.Boss = new BossSnapshot {
        CurrentMove = _currentMove, Vars = _vars,
        Resources = _resources.DeepCopy(),   // copies the Cooldowns[] array
        Rng = _rng.Capture(), Brain = _brain, Telegraph = _telegraph,
        Facing = _facing, Frame = _frame,
    };
}
protected override void ReadState(in EntitySnapshot s) {
    base.ReadState(in s);
    var b = s.Boss;
    _currentMove = b.CurrentMove; _vars = b.Vars;
    _resources = b.Resources.DeepCopy(); _rng.Restore(b.Rng);
    _brain = b.Brain; _telegraph = b.Telegraph; _facing = b.Facing; _frame = b.Frame;
}
```
Also add `EntityKind.Boss` + a `Rehydrate` case (`new BossEntity(Body.Position, seed:0)` then
`RestoreInto` overwrites everything). The seed is restored via `DetRngState`, so the ctor
seed is irrelevant after restore.

`DetRng` is a tiny seeded PRNG (e.g. xorshift32) as a value struct with `Capture()/Restore()`
returning/taking a `DetRngState { uint S; }`. **Every** stochastic choice (feint-or-real,
move variety, jitter) draws from it. `ControllerBlackboard` is a plain struct of value
fields only (see §8). Fixed-size arrays only — no `List`, no dictionaries — so capture is
cheap and aliasing-free.

> **Snapshot rule of thumb for this subsystem:** if a move or controller needs to remember
> something between frames, it goes in `EnemyMoveVars`, `EnemyResources`, or
> `ControllerBlackboard` — never as a private field on a flyweight `EnemyMove` or on the
> `EnemyController`. Those two are stateless by contract (exactly like `ActionState`).

---

## 7. Worked example moves

### 7a. HeavyOverhead — the "respect me" slam

```csharp
public sealed class HeavyOverhead : EnemyMove
{
    public override float Windup => 0.70f;   // long, readable
    public override float Active => 0.12f;
    public override float Recovery => 0.55f; // your guaranteed punish window on whiff
    public override CooldownFamily Family => CooldownFamily.Heavy;
    public override float StaminaCost => 35f;

    public override bool CanStart(in EnemyContext ctx, in EnemyResources res)
        => res.Ready(Family) && res.Stamina >= StaminaCost
        && ctx.Dist < 60f && MathF.Abs(ctx.ToPlayer.Y) < 30f;

    public override bool HasArmor(in EnemyMoveVars v) => v.TimeInMove < Windup; // armored windup

    public override void OnStart(in EnemyContext ctx, ref EnemyMoveVars v, ref EnemyResources res, int facing)
    {
        v.LockedFacing = facing;
        res.Stamina -= StaminaCost;
        res.Trigger(Family, 3.0f);
    }

    public override MoveStatus Tick(in EnemyContext ctx, ref EnemyMoveVars v, ref EnemyResources res,
                                    ref DetRng rng, ref Telegraph tel, int facing)
    {
        v.TimeInMove += ctx.Dt;
        var phase = PhaseOf(v, Windup, Active);
        ctx.Self.Body.Velocity.X *= 0.8f;   // root in place — readable

        if (phase == MovePhase.Windup)
        {
            tel = new Telegraph { Kind = TelegraphKind.Flash, Pos = ctx.Self.Body.Position,
                                  Progress = v.TimeInMove / Windup, Severity = 1f };
            return MoveStatus.Committed;     // cannot be cancelled
        }
        if (phase == MovePhase.Active)
        {
            EnsureHitId(ctx, ref v);
            var c = ctx.Self.Body.Position + new Vector2(v.LockedFacing * 26f, 6f);
            var region = new BoundingBox(c.X - 26f, c.Y - 20f, c.X + 26f, c.Y + 22f);
            ctx.Hitboxes?.Publish(new Hitbox(region, v.HitId, 1.5f,
                new Vector2(v.LockedFacing * 520f, 120f),   // knock toward the ground/wall → crush combo
                Faction.Enemy, ctx.Self, Color.OrangeRed, HitTargets.All));   // also cracks tiles
            tel = Telegraph.None;
            return MoveStatus.Committed;
        }
        return v.TimeInMove >= Windup + Active + Recovery ? MoveStatus.Done : MoveStatus.Running;
    }
}
```

### 7b. PillarUppercut — terrain launcher (signature)

Windup glows the cell under the player; Active sprouts a column there to launch them. Pure
terrain attack, on-theme. Counter: move off the marked cell during the tell.

```csharp
public override void OnStart(in EnemyContext ctx, ref EnemyMoveVars v, ref EnemyResources res, int facing)
{
    res.Trigger(Family, 4f);
    // Snapshot the target cell at windup start so it's dodgeable (doesn't track).
    var p = ctx.Player.Body.Position;
    v.TargetCell = new Vector2(MathF.Floor(p.X / Chunk.TileSize), MathF.Floor(p.Y / Chunk.TileSize) + 1);
}
// Tick: Windup → ChargeRing telegraph on the cell center. Active → ctx.Chunks?.TryRequestTile(
//   (int)v.TargetCell.X, (int)v.TargetCell.Y, TileType.Stone) + a brief upward hitbox so
//   it both launches via the sprout collision and juggles. Family = Terrain.
```

### 7c. ChargedBolt — committed ranged punish

`CanStart`: mid/long range, `Ready(Ranged)`. `OnStart`: lock facing, `Trigger(Ranged, 1.5f)`.
Windup: `DangerLine` telegraph along `Aim` (locked at windup *end* so a late dodge works).
Active: `ctx.Spawner.SpawnEntity(new EnergyBallProjectile(muzzle, v.Aim * speed, ctx.Spawner.HitIds.Next()))`
— reuses the existing projectile + its own snapshot. Recovery: long, punishable.

These three already exercise: armored windup, knock-into-terrain crush setup, terrain
mutation, projectile spawning, and three telegraph kinds — i.e. the whole scaffold.

---

## 8. The controller (the brain)

Logic only; its memory is the plain-data `ControllerBlackboard`. Swap controllers to
retune the whole fight without touching moves.

```csharp
public struct ControllerBlackboard
{
    public byte  Phase;            // 0/1/2 escalation (HP thresholds)
    public float DecisionTimer;    // throttle re-decisions
    public int   LastMove;
    // Habit tracking — cheap counters, decayed over time, drive anti-pattern play:
    public int   PlayerJumps;      // ++ when player airborne-attacks
    public int   PlayerParries;    // ++ when player guards
    public float HabitDecay;
}

public interface EnemyController
{
    // Return a move index to start, or -1 to keep doing nothing this frame.
    int ChooseMove(in EnemyContext ctx, IReadOnlyList<EnemyMove> moves,
                   in EnemyResources res, ref ControllerBlackboard bb,
                   ref DetRng rng, MoveStatus current);
}
```

A `PhasedController` sketch composes the personality patterns from the brainstorm:
- **Phase by HP:** `bb.Phase` steps at 66%/33%; higher phases unlock terrain moves + shorten
  effective decision delay.
- **Spacing FSM:** pick from range-banded pools (close → Heavy/Pillar, mid → Bolt/Dash-in,
  far → mortar/charge-the-ultimate). Mirrors `StalkerEnemy`'s Chase→…→Recover skeleton.
- **Anti-pattern:** if `bb.PlayerJumps` is high, weight anti-airs; if `bb.PlayerParries` is
  high, weight a guard-break / unblockable grab and *feint* more.
- **Feints:** with `rng`-driven probability, start a move whose windup it cancels into a
  block or dash (a dedicated `Feint` move, or a `Cancellable` windup variant) to bait the
  dodge, then punish.
- **Mistake injection:** every N seconds force a `Cancellable` idle so a skilled player gets
  a guaranteed pressure window — the fairness valve.

Habit counters update in `BossEntity.Update` (or here) from observable player state
(`player.CurrentActionName`, `player.IsGrounded`, `player.Combat`), decayed by `HabitDecay`
so the boss adapts but also forgets.

---

## 9. Integration checklist (what touches the live tree)

Additive new files (no conflicts):
- `Entities/BossEntity.cs`, `Entities/EnemyMove.cs`, `Entities/EnemyMoveVars.cs`,
  `Entities/EnemyResources.cs`, `Entities/EnemyContext.cs`, `Entities/Telegraph.cs`,
  `Entities/EnemyController.cs` (+ `PhasedController`), `Entities/DetRng.cs`,
  `Entities/BossSnapshot.cs`, and one file per move (or a `Moves/` subfolder).
- `Drawing/Sprites.cs`: add `Sprites.Boss(...)` (additive method).

Shared touchpoints (coordinate / small):
- **`EntitySnapshot`**: add `public BossSnapshot Boss;` + `EntityKind.Boss` + a `Rehydrate`
  case. **This is the one real coordination point with the main agent's snapshot work.**
- `Game1.Draw`: add the `DrawTelegraph` cosmetic hook (+ optional posture HUD bar).
- A `Stage.Populate` (or a new `boss` stage in `Stages`): `g.SpawnEntity(new BossEntity(pos, seed))`.

Do **not** touch: `PhysicsWorld`, `CombatSystem`, `PlayerCharacter`, `Simulation.Step`
ordering. The boss is just another `Entity` to all of them.

## 10. Build milestones

1. **Skeleton + one move.** `BossEntity` + `EnemyMove`/`Vars` + `EnemyContext` + a hardcoded
   controller that only ever does `HeavyOverhead`. No telegraph render yet. Prove it spawns,
   attacks, takes damage, dies.
2. **Telegraph bridge.** `Telegraph` struct + `Game1.DrawTelegraph`. Make the windup readable.
3. **Resources.** `EnemyResources` (stamina + one cooldown family + posture). Wire posture
   into `OnHit` for a stagger reaction. Verify a heavy can't chain.
4. **Snapshot.** `BossSnapshot` + `DetRng` + the `EntitySnapshot` seam. Add a
   `SnapshotRoundTripTests` case: spawn a boss, run mid-move, snapshot, run, restore, assert
   identical trace. **This is the gate** — do it before adding many moves, while state is small.
5. **Move library.** Flesh out the brainstorm: terrain plays, ranged, traps. Each is a new
   flyweight; snapshot already covers them via `EnemyMoveVars`.
6. **Controller.** Replace the hardcoded brain with `PhasedController`: spacing FSM, phases,
   habit tracking, feints, mistake injection.

## 11. Open questions / decisions to revisit

- **Multi-hitbox moves** (spin, barrage): one `HitId` per *logical* hit, or per move? Spin
  wants per-tick-window ids so each rotation can re-hit; barrage wants one per projectile.
  `EnemyMoveVars.SubCount` + minting a fresh id per sub-emit handles both.
- **Controller as data vs code:** start with `interface EnemyController` (code). If we ever
  want data-driven/authored behavior or per-instance variation, move to a behavior-tree or
  scored-utility table — but the snapshot model (blackboard struct) stays the same.
- **Telegraph audio/shake** are cosmetic, but their *trigger* must be sim-derived
  (`Telegraph.Severity`) so replays line up.
- **Difficulty knobs:** prefer shrinking telegraph windows and tightening punish windows over
  raising damage — the framework already isolates those as per-move fields.
- **Posture vs hitstun overlap:** decide whether a posture break routes through the same
  hitstun/`CombatState` machinery the player uses, or a boss-local stagger move (sketch uses
  the latter for independence).
```
