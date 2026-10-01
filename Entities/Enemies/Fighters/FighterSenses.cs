using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The sensing boundary for fighter brains (Plans/FIGHTER_DESIGN_PLAN.md §16).
//
// A fighter brain never sees EnemyContext. It subclasses FighterController, whose
// EnemyContext entry point is sealed, and gets a FighterSenses: everything it can learn
// about the world comes through this object, which is where the limits live.
//
//   FREE   — Self (own body, meter, FSM state), Dt/Frame, and TargetCoarse(): the
//            target's position snapped to a tile grid and served several frames late,
//            with its health and team but no velocity and no tell.
//   PAID   — Target() (exact position/velocity and the TELL: what action the target is
//            winding up, how far along, where it aims), Probe(dir) (ground ahead, drop
//            ahead, wall ahead, headroom), Cell(gtx, gty). Each costs energy
//            (FighterCostConfig.Sense*); the brain can read Energy and ration.
//   STALE  — an unpaid query returns the last bought value with its age in frames
//            (Target: always; Probe: when the direction matches). Nothing is ever
//            "unknown" once something has been bought; an empty meter means an old
//            picture, not a blindfold.
//   LATE   — exact reads are ReactionFrames old (a bought attribute), served from a
//            snapshotted ring of past views; the coarse read is at least CoarseLagFrames
//            old. Two identical brains therefore never act on the same frame's data at
//            the same instant unless they bought the same reaction time.
//   SIGHT  — every fighter does the line-of-sight raycast. While the target is hidden,
//            the views pushed into the ring are the last-seen position frozen
//            (no velocity, no tell) — or, if the fighter bought TargetMemory, the live
//            position coarsened and without a tell. Visible says whether the newest
//            view is a real sighting.
//
//   RANDOM — Random() / Random(lo, hi) / RandomInt(n) / Chance(p): a seeded stream whose
//            state is snapshotted with the sense memory. Free. The only randomness a
//            brain may use; the bout seeds it (FighterArena).
//
// Determinism: pure functions of sim state plus the entity's snapshotted history and
// memory. No allocation per frame; one instance per entity, re-armed each Update.
public struct TerrainProbe
{
    public bool  Known;          // false ⇒ nothing bought yet in this direction
    public int   AgeFrames;
    public int   Dir;            // +1 right, -1 left
    // Along `Dir` from the body: distance in px to the first cell with no floor under
    // it (a drop), or Range if none; distance to the first solid cell at body height
    // (a wall), or Range if none; and clear tiles above the body (0..4).
    public float DropAhead;
    public float WallAhead;
    public int   Headroom;
    public bool  Grounded;       // a solid cell directly under the body
}

public struct SelfState
{
    public Vector2 Position, Velocity;
    public float   Health, MaxHealth, Energy, EnergyMax, Radius;
    public int     Facing, Team;
    public bool    ActionCommitted, Staggered;
    public ActionKind CurrentAction;     // Special while idle
    public float   ActionProgress;       // windup progress of own action, -1 while idle
}

public sealed class FighterSenses
{
    private readonly EnemyEntity _self;
    private EnemyContext _ctx;
    private FighterCostConfig _k;
    // Per-frame query counters, for the league's compute report.
    public int QueriesThisFrame { get; private set; }
    public int PaidThisFrame    { get; private set; }

    public FighterSenses(EnemyEntity self) { _self = self; }

    // Called by FighterController before the brain runs.
    internal void Begin(in EnemyContext ctx)
    {
        _ctx = ctx;
        _k   = FighterCosts.Current;
        QueriesThisFrame = 0;
        PaidThisFrame    = 0;
    }

    // ── Free ────────────────────────────────────────────────────────────────

    public float Dt    => _ctx.Dt;
    public int   Frame => _ctx.Frame;

    public SelfState Self => new()
    {
        Position        = _self.Body.Position,
        Velocity        = _self.Body.Velocity,
        Health          = _self.Health,
        MaxHealth       = _self.MaxHealth,
        Energy          = _self.Energy,
        EnergyMax       = _self.EnergyMax,
        Radius          = _self.BodyRadius,
        Facing          = _self.Facing,
        Team            = _self.Team,
        ActionCommitted = _self.IsActionCommitted,
        Staggered       = _self.IsStaggered,
        CurrentAction   = _self.TellKind,
        ActionProgress  = _self.TellProgress,
    };

    // Whether the newest view of the target is a real sighting (line of sight held
    // this frame). Free: it is a consequence of the mandatory raycast.
    public bool Visible => _self.PlayerVisible;
    // Seconds since the target was last actually seen. 0 while visible.
    public float HiddenSeconds => _self.LastSeenAge;

    // The free read: coarse position (tile-snapped), lagged by max(CoarseLagFrames,
    // ReactionFrames), health and team, no velocity, no tell.
    public EnemyTarget TargetCoarse()
    {
        QueriesThisFrame++;
        int lag = Math.Max(_k.CoarseLagFrames, _self.ReactionFrames);
        return _self.Targets.Back(lag).Coarse(_k.CoarseQuantPx);
    }

    // How much a query would cost — so a brain can decide before asking.
    public float TargetCost => _k.SenseTargetCost;
    public float ProbeCost  => _k.SenseProbeCost;
    public float CellCost   => _k.SenseCellCost;

    // ── Paid ────────────────────────────────────────────────────────────────

    // The exact view, ReactionFrames old, with the tell. `age` is how many frames old
    // the returned view is (the reaction time when paid; more when served from memory).
    public EnemyTarget Target(out int age)
    {
        QueriesThisFrame++;
        ref var mem = ref _self.SenseMem;
        if (Pay(_k.SenseTargetCost))
        {
            mem.Target      = _self.Targets.Back(_self.ReactionFrames);
            mem.TargetFrame = _ctx.Frame - _self.ReactionFrames;
            PaidThisFrame++;
        }
        age = mem.Target.Known ? _ctx.Frame - mem.TargetFrame : int.MaxValue;
        return mem.Target;
    }

    public EnemyTarget Target() => Target(out _);

    // The last bought exact view without paying for a new one (free). `age` is
    // int.MaxValue when nothing has ever been bought.
    public EnemyTarget TargetStale(out int age)
    {
        ref var mem = ref _self.SenseMem;
        age = mem.Target.Known ? _ctx.Frame - mem.TargetFrame : int.MaxValue;
        return mem.Target;
    }

    // Terrain ahead in a direction: drop, wall, headroom, grounded. Stale when unpaid
    // and the last probe was in the same direction; otherwise Known = false.
    public TerrainProbe Probe(int dir)
    {
        QueriesThisFrame++;
        dir = dir >= 0 ? 1 : -1;
        ref var mem = ref _self.SenseMem;
        if (Pay(_k.SenseProbeCost))
        {
            mem.Probe      = ProbeNow(dir);
            mem.ProbeFrame = _ctx.Frame;
            mem.ProbeDir   = dir;
            PaidThisFrame++;
        }
        if (!mem.Probe.Known || mem.ProbeDir != dir) return new TerrainProbe { Dir = dir };
        var p = mem.Probe;
        p.AgeFrames = _ctx.Frame - mem.ProbeFrame;
        return p;
    }

    // One tile's state. Unpaid ⇒ null.
    public TileState? Cell(int gtx, int gty)
    {
        QueriesThisFrame++;
        if (!Pay(_k.SenseCellCost)) return null;
        PaidThisFrame++;
        return _ctx.Spawner?.Chunks?.GetCellState(gtx, gty) ?? TileState.Empty;
    }

    // ── Random (free) ───────────────────────────────────────────────────────
    // The one source of randomness a brain may use. A seeded xorshift stream whose
    // state lives in the snapshotted sense memory, so a rollback rewinds it with
    // everything else, a replay draws the same numbers, and two identical brains with
    // different seeds desynchronise. Seeded by the bout (FighterArena.Populate: bout
    // seed × entry index) or, for a fighter spawned outside the arena, lazily from its
    // entity id. Never System.Random: that is unsnapshotted state (guide §5).

    // Uniform in [0, 1).
    public float Random()
    {
        ref var mem = ref _self.SenseMem;
        if (mem.Rng == 0)
            mem.Rng = BrainRandom.Seed(BrainRandom.Mix((ulong)(uint)_self.Id.Index, (ulong)(uint)_self.Id.Generation));
        return BrainRandom.Unit(ref mem.Rng);
    }

    // Uniform in [lo, hi).
    public float Random(float lo, float hi) => lo + (hi - lo) * Random();

    // Uniform in 0..n-1 (0 when n <= 1).
    public int RandomInt(int n) => n <= 1 ? 0 : (int)(Random() * n);

    // True with probability p.
    public bool Chance(float p) => Random() < p;

    // ── Internals ───────────────────────────────────────────────────────────

    private bool Pay(float cost)
    {
        if (cost <= 0f) return true;
        if (_self.Energy < cost) return false;
        _self.Energy -= cost;
        return true;
    }

    public const float ProbeRangePx = 6 * Chunk.TileSize;

    private TerrainProbe ProbeNow(int dir)
    {
        var chunks = _ctx.Spawner?.Chunks;
        var pos    = _self.Body.Position;
        float ts   = Chunk.TileSize;
        int gtx = (int)MathF.Floor(pos.X / ts);
        // Body row: the cell the body's centre is in; floor row: the one under its feet.
        int bodyRow  = (int)MathF.Floor(pos.Y / ts);
        int floorRow = (int)MathF.Floor((pos.Y + _self.BodyRadius + 1f) / ts);

        var p = new TerrainProbe { Known = true, Dir = dir, DropAhead = ProbeRangePx, WallAhead = ProbeRangePx };
        if (chunks == null) return p;

        p.Grounded = chunks.GetCellState(gtx, floorRow) == TileState.Solid;
        int cells = (int)(ProbeRangePx / ts);
        for (int i = 1; i <= cells; i++)
        {
            int x = gtx + dir * i;
            if (p.WallAhead >= ProbeRangePx && chunks.GetCellState(x, bodyRow) == TileState.Solid)
                p.WallAhead = (i - 0.5f) * ts;
            if (p.DropAhead >= ProbeRangePx && chunks.GetCellState(x, floorRow) != TileState.Solid
                                            && chunks.GetCellState(x, floorRow + 1) != TileState.Solid)
                p.DropAhead = (i - 0.5f) * ts;
        }
        int head = 0;
        for (int i = 1; i <= 4; i++)
        {
            if (chunks.GetCellState(gtx, bodyRow - i) == TileState.Solid) break;
            head++;
        }
        p.Headroom = head;
        return p;
    }
}

// The brain random stream behind FighterSenses.Random: pure functions on a 64-bit
// state, so the state can sit in a snapshotted struct. splitmix64 to turn a seed (or
// two) into a well-mixed non-zero state, xorshift64* to step it. Identical on every
// runtime — integer only, no floating-point until the final scale.
public static class BrainRandom
{
    // A seed's stream state: splitmix64 of the seed, forced non-zero (xorshift's
    // only fixed point is 0, and 0 doubles as "not seeded yet" in SenseMemory).
    public static ulong Seed(ulong seed)
    {
        ulong z = seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z == 0 ? 0x2545F4914F6CDD1DUL : z;
    }

    // Combine two values into one seed (a bout seed and an entry index, say).
    public static ulong Mix(ulong a, ulong b) => Seed(a) ^ (Seed(b ^ 0xD1B54A32D192ED03UL) * 0x9E3779B97F4A7C15UL);

    // Advance the state (xorshift64*) and return the next 64 bits.
    public static ulong Next(ref ulong state)
    {
        ulong x = state;
        x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
        state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    // Uniform float in [0, 1): the top 24 bits over 2^24, so every value is exactly
    // representable and 1.0 is never returned.
    public static float Unit(ref ulong state) => (Next(ref state) >> 40) * (1f / 16777216f);
}

// Base class for every fighter brain. The EnemyContext entry is sealed: a fighter
// brain can only reach the world through FighterSenses, and its memory is the
// snapshotted BrainScratch handed in by ref. Config lives on the instance (init-only,
// copied from the spec at construction); nothing on `this` may be written in Decide.
public abstract class FighterController : EnemyController
{
    public sealed override EnemyInput Decide(in EnemyContext ctx)
    {
        var self   = ctx.Self;
        var senses = self.Senses;
        senses.Begin(in ctx);
        return Decide(senses, ref self.Scratch);
    }

    protected abstract EnemyInput Decide(FighterSenses s, ref BrainScratch memory);
}
