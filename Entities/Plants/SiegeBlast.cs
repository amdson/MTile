using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ── THE SIEGE POD'S SHOT ────────────────────────────────────────────────────
// Everything that happens in the one frame a ripe siege pod fires
// (Plans/PLANT_SEEDS_PLAN.md §4.4, decisions 5 and 9), as a stateless unit the pod
// entity calls in one line, plus the read-only reach preview its growth telegraph draws.
// Two components, in this order, in the same call:
//
//   1. BLAST — a disc of BlastRadius around the muzzle. Tiles: BlastTileDamage to every
//      solid cell in the disc the blast can reach, nearest-first (TileReach.DamageDisc,
//      the same occluded front-to-back rule every other explosion uses), so it eats dirt
//      and sand outright and chips stone. Bodies: a heavy, launching hit, occluded from
//      the muzzle, pushed outward.
//   2. LANCE — a narrow beam along the aim that carries a DAMAGE BUDGET (in "dirt-cell
//      units", LanceBudget) down the line. Walking outward, every solid cell it meets takes
//      what it needs to break THROUGH ChunkMap.DamageCell (so OnTileBroken, the journal,
//      charged-block chains and any damage scaling DamageCell applies all happen as for any
//      other hit), the lance pays for that out of the budget and goes on with the rest; the
//      cell that the remaining budget can't break keeps the partial damage and is where the
//      lance stops. Runs AFTER the blast's tiles, so it never pays for what the blast cleared.
//      Bodies: a heavy, launching hit along the lance's ACTUAL reach — nothing past where it
//      stopped.
//
// PER-MATERIAL TUNING. A material's LanceCost is how many budget units an ordinary,
// undamaged cell of it costs — its own named number, deliberately not proportional to
// MaxHP. Against a uniform wall that gives, per lane (see the march below):
//      Stone 4  → 3 cells through, stops IN the 4th     Dirt 1 → 12 cells
//      Sand/Foam 0.04 → ~300 cells, past the lance's length (effectively unlimited)
//      Hardened 1000 → stops at the first cell, which takes a sliver and stands
//   and on a mixed line the costs add: 1 stone + 8 dirt is the whole budget.
// The budget is converted to tile damage per cell (MaxHP / LanceCost per unit) at the
// LIVE MaxHP, so the guarantee holds whatever material_strengths.json says, and the spend
// is measured from what DamageCell actually needed: a pre-damaged cell costs its remaining
// fraction; a cell whose incoming damage DamageCell scales down (the planned thorn
// reinforcement, §4.3) costs proportionally more — Spend() observes the shortfall and pays
// the scaled price, it never assumes a private HP table.
//
// THE MARCH, for a lance LanceLanes cells wide. Let the MAJOR axis be whichever of x / y
// the aim leans along most (|aim.x| ≥ |aim.y| → x). The lance advances one major-axis cell
// ("a layer") per step, starting with the cell after the muzzle's own; at each step the aim
// line's minor coordinate m is taken at that column's centre, and the lance covers the band
// [m − W·ts/2, m + W·ts/2] split into W LANES, each exactly one cell of that column (lane j is
// floor(m/ts + j − W/2 + ½)). Each lane carries its OWN full budget and stops on its own.
// So a wall of uniform thickness across the major axis (a vertical wall for a mostly
// horizontal shot, a floor for a mostly vertical one) costs every lane the same and the
// "3 stone / 12 dirt" guarantee holds at ANY aim angle; a wall struck obliquely costs more
// (sloped armour — the lane crosses more cells), which is the honest geometric answer.
// Rounding note: with an even W, a muzzle exactly on a cell centre puts the lanes on the
// muzzle's row/column and the next one in +minor (for a horizontal shot: its row and the row
// below). The pod entity picks the muzzle point; nudge it if that matters.
// Order: steps outward, lanes 0..W−1 within a step — stable and deterministic. Lane cells
// never repeat (distinct rows within a column, distinct columns across steps), so the order
// cannot change what any cell takes; it only fixes the order OnTileBroken fires in.
// Non-solid cells cost nothing: Empty and Sprouting pass (DamageCell ignores sprouts); a
// Spike is knocked off (DamageCell's any-hit rule) and passed.
//
// NOT TEAM-FILTERED (decision 9). Every hitbox here is Faction.Hazard — a faction no hurtbox
// carries — so CombatSystem's "same faction ⇒ skip" gate never fires: the shot lands on its
// owner, the owner's plants and entities, the other player and neutral bodies alike. Tile
// damage never looked at ownership anyway. Source should be the POD's own EntityId, not the
// planting player's: CombatSystem routes guard push-back and hit-confirm to Source, and a
// parry far away must not shove the player who planted the pod.
//
// One HitId for the whole shot (blast + lance), so a body inside both is hit once; the blast
// pieces are published first, so a body in the disc takes the blast's outward launch.
//
// Determinism: no RNG, no statics written, no allocation-order dependence (the disc's cells
// are sorted by distance then (gtx, gty)). The reach preview reads terrain only.
public static class SiegeBlast
{
    // ── Lance ────────────────────────────────────────────────────────────────
    public const int   LanceLanes        = 2;      // width in cells (the "~2 tiles"); ≤ SiegeReach.MaxLanes
    public const float LanceLengthTiles  = 120f;   // reach along the aim, in tiles
    public static float LanceLength => LanceLengthTiles * Chunk.TileSize;

    // Budget in dirt-cell units, per lane. The slack is float headroom so the designed
    // break counts (3 stone, 12 dirt, 1 stone + 8 dirt) land with a hair to spare instead of
    // falling one ulp short; the cell where a lane stops takes that hair as partial damage.
    public const float LanceBudget      = 12f;
    public const float LanceBudgetSlack = 0.01f;

    // What one ORDINARY UNDAMAGED cell costs, in budget units. Their own numbers — NOT MaxHP.
    public const float LanceCostDirt     = 1f;
    public const float LanceCostStone    = 4f;      // = four dirt: 3 through, stops in the 4th
    public const float LanceCostSand     = 0.04f;   // 300 cells per budget: unlimited in practice
    public const float LanceCostFoam     = 0.04f;
    public const float LanceCostHardened = 1000f;   // stops dead; the cell keeps ~0.1% of its HP
    public const float LanceCostDefault  = 1f;      // any material not listed above

    // Body hit along the lance (heavy tier: beats a held guard, launches).
    public const float LanceBodyDamage = 1.6f;
    public const float LanceKnockback  = 1000f;    // impulse; player mass 2.5 → 400 px/s before KnockbackSpeedScale

    // ── Blast ────────────────────────────────────────────────────────────────
    public const float BlastRadius     = 55f;      // RayEmitter's end-of-life disc
    public const float BlastTileDamage = 5.0f;     // > dirt (2.0) and sand: clears them; chips stone (12)
    public const float BlastBodyDamage = 1.8f;     // top of the heavy band
    public const float BlastKnockback  = 1200f;    // impulse, radially outward
    public const int   BlastWedges     = 12;       // the disc's body shape: 12 pie slices

    // ── Shared heavy-hit feel ────────────────────────────────────────────────
    public const float LaunchDecay     = 350f;     // as the clod burst core
    public const float HitstunSeconds  = 0.6f;
    public const float StunSeconds     = 0.4f;     // declared, not inferred (CombatState)

    // Threshold below which a lane's budget is spent.
    private const float MinUnits = 1e-4f;
    // DamageCell retries per cell: the first offer assumes damage lands 1:1; if DamageCell
    // scaled it down, the next offer pays the observed rate. Two suffice for a constant
    // scale; the rest absorb float shortfall.
    private const int MaxOffers = 4;

    private static readonly Color LanceTint = new(255, 170, 60);
    private static readonly Color BlastTint = Color.OrangeRed;

    public static float LanceCostFor(TileType type) => type switch
    {
        TileType.Dirt     => LanceCostDirt,
        TileType.Stone    => LanceCostStone,
        TileType.Sand     => LanceCostSand,
        TileType.Foam     => LanceCostFoam,
        TileType.Hardened => LanceCostHardened,
        _                 => LanceCostDefault,
    };

    // ── Entry points ─────────────────────────────────────────────────────────

    // Fire the pod: blast tiles, lance tiles, then publish the body hitboxes (one fresh
    // HitId from `hitIds`). Call it from the pod's Update on the frame it ripens — entities
    // update before CombatSystem.Apply, which resolves the bodies this same frame against
    // the terrain as Fire left it. `hitboxes`/`hitIds` may be null (tiles only).
    // Returns where everything went, for the pod's own end-of-life visuals and for tests.
    public static SiegeReach Fire(ChunkMap chunks, HitboxWorld hitboxes, HitIdAllocator hitIds,
                                  Vector2 muzzle, Vector2 aim, EntityId source)
        => Fire(chunks, hitboxes, hitIds, muzzle, aim, source, chunks != null ? chunks.DamageCell : null);

    // Test seam: `damageCell` stands in for ChunkMap.DamageCell in the LANCE's spend, so a
    // test can model a system that scales a cell's incoming damage (the planned thorn
    // reinforcement lives inside DamageCell, which has no hook today). Production passes
    // chunks.DamageCell itself.
    internal static SiegeReach Fire(ChunkMap chunks, HitboxWorld hitboxes, HitIdAllocator hitIds,
                                    Vector2 muzzle, Vector2 aim, EntityId source,
                                    Func<int, int, float, bool> damageCell)
    {
        aim = Unit(aim);
        int blastBroken = chunks != null ? TileReach.DamageDisc(chunks, muzzle, BlastRadius, BlastTileDamage) : 0;
        var reach = March(chunks, muzzle, aim, damageCell);
        reach.BlastCellsBroken = blastBroken;
        if (hitboxes != null)
        {
            int hitId = hitIds?.Next() ?? 0;
            reach.HitId = hitId;
            PublishBlast(hitboxes, muzzle, hitId, source);
            PublishLance(hitboxes, in reach, hitId, source);
        }
        return reach;
    }

    // Where the lance WOULD end if the pod fired now (and the blast disc), without touching
    // terrain. Same march as Fire, with each cell's cost read instead of paid. Exact as long
    // as DamageCell applies damage 1:1 — under a damage-scaling system it is optimistic (it
    // cannot see the scale without hitting the cell). It does not account for the blast's
    // own tile damage either (Fire clears the disc first), which only matters for cells
    // within BlastRadius of the muzzle; the lance then costs at most as much as the preview
    // says, so the preview can only under-, never over-promise cover near the pod... except
    // through the scaling caveat above.
    public static SiegeReach Preview(ChunkMap chunks, Vector2 muzzle, Vector2 aim)
        => March(chunks, muzzle, Unit(aim), damageCell: null);

    // Growth telegraph: the would-be lance (one line per lane, a tick where each stops) and
    // the blast ring. `progress` ∈ [0,1] is growth; it brightens and thickens the lines.
    // Sim-side: appends shapes only.
    public static SiegeReach Telegraph(TelegraphList t, ChunkMap chunks, Vector2 muzzle, Vector2 aim,
                                       float progress)
    {
        var reach = Preview(chunks, muzzle, aim);
        Telegraph(t, in reach, progress);
        return reach;
    }

    public static void Telegraph(TelegraphList t, in SiegeReach reach, float progress)
    {
        float p = MathHelper.Clamp(progress, 0f, 1f);
        var col = LanceTint * (0.25f + 0.6f * p);
        for (int j = 0; j < reach.LaneCount; j++)
        {
            var lane = reach[j];
            t.Line(lane.Start, lane.End, col, 1f + p);
            if (lane.Stopped)
            {
                var n = new Vector2(-reach.Aim.Y, reach.Aim.X) * (Chunk.TileSize * 0.5f);
                t.Line(lane.End - n, lane.End + n, Color.White * (0.4f + 0.6f * p), 1.5f);
            }
        }
        t.Ring(reach.Muzzle, BlastRadius, BlastTint * (0.2f + 0.6f * p), 20, 1f + p);
    }

    // ── The march ────────────────────────────────────────────────────────────

    // damageCell == null ⇒ preview: read costs, touch nothing.
    private static SiegeReach March(ChunkMap chunks, Vector2 muzzle, Vector2 aim,
                                    Func<int, int, float, bool> damageCell)
    {
        bool apply = damageCell != null;
        const float ts = Chunk.TileSize;
        var reach = new SiegeReach { Muzzle = muzzle, Aim = aim, LaneCount = LanceLanes };

        bool xMajor = MathF.Abs(aim.X) >= MathF.Abs(aim.Y);
        float dMaj  = xMajor ? aim.X : aim.Y;
        float dMin  = xMajor ? aim.Y : aim.X;
        int   s     = dMaj >= 0f ? 1 : -1;
        float slope = dMin / dMaj;                       // |slope| ≤ 1; dMaj ≠ 0 for a unit aim
        float pMaj  = xMajor ? muzzle.X : muzzle.Y;
        float pMin  = xMajor ? muzzle.Y : muzzle.X;
        int   c0    = (int)MathF.Floor(pMaj / ts);
        float majorExtent = LanceLength * MathF.Abs(dMaj);   // how far along the major axis the lance reaches

        Span<float> units   = stackalloc float[LanceLanes];
        Span<bool>  stopped = stackalloc bool[LanceLanes];
        for (int j = 0; j < LanceLanes; j++)
        {
            units[j] = LanceBudget + LanceBudgetSlack;
            reach.SetLane(j, new SiegeLane { StopX = int.MinValue, StopY = int.MinValue });
        }
        int live = LanceLanes;

        for (int i = 1; live > 0; i++)
        {
            int   c  = c0 + s * i;
            float mc = (c + 0.5f) * ts;                  // column centre on the major axis
            if ((mc - pMaj) * s > majorExtent) break;
            float m  = pMin + (mc - pMaj) * slope;       // aim line's minor coordinate there
            for (int j = 0; j < LanceLanes; j++)
            {
                if (stopped[j]) continue;
                int minorCell = (int)MathF.Floor(m / ts + j - LanceLanes * 0.5f + 0.5f);
                int gtx = xMajor ? c : minorCell, gty = xMajor ? minorCell : c;
                var lane = reach[j];
                var state = chunks?.GetCellState(gtx, gty) ?? TileState.Empty;
                if (state == TileState.Spike)
                {
                    if (apply) damageCell(gtx, gty, 1f);           // any hit knocks a spike off
                    continue;
                }
                if (state != TileState.Solid) continue;

                bool broke = apply ? Spend(chunks, damageCell, gtx, gty, ref units[j])
                                   : Afford(chunks, gtx, gty, ref units[j]);
                if (broke) { lane.Broken++; reach.SetLane(j, lane); continue; }

                stopped[j] = true; live--;
                lane.Stopped = true; lane.StopX = gtx; lane.StopY = gty;
                // The lane ends at the near face of the cell that held it.
                lane.EndMajor = s > 0 ? c * ts : (c + 1) * ts;
                reach.SetLane(j, lane);
            }
        }

        // Geometry: each lane's centre line from the muzzle to where it ended.
        for (int j = 0; j < LanceLanes; j++)
        {
            var lane = reach[j];
            if (!lane.Stopped) lane.EndMajor = pMaj + s * majorExtent;
            lane.UnitsLeft = MathF.Max(0f, units[j]);
            float off = (j - LanceLanes * 0.5f + 0.5f) * ts;     // lane centre's minor offset
            lane.Start = ToWorld(xMajor, pMaj, pMin + off);
            lane.End   = ToWorld(xMajor, lane.EndMajor, pMin + (lane.EndMajor - pMaj) * slope + off);
            lane.MinorLo = off - ts * 0.5f;
            lane.MinorHi = off + ts * 0.5f;
            reach.SetLane(j, lane);
        }
        reach.XMajor = xMajor;
        reach.Slope  = slope;
        return reach;
    }

    // Pay for one solid cell through DamageCell. Returns true if it broke (budget reduced by
    // what it took), false if the budget ran out on it (budget → 0, the cell keeps the damage).
    private static bool Spend(ChunkMap chunks, Func<int, int, float, bool> damageCell,
                              int gtx, int gty, ref float units)
    {
        var   type    = chunks.GetCellType(gtx, gty);
        float maxHp   = TileDamage.MaxHPFor(type);
        float perUnit = maxHp / LanceCostFor(type);       // tile damage one budget unit buys here
        float rate    = 1f;                               // observed: damage that LANDS per damage offered
        for (int k = 0; k < MaxOffers && units > MinUnits; k++)
        {
            float before = chunks.Damage.Get(gtx, gty);
            float want   = (maxHp - before) / rate;       // offer needed to break at the observed rate
            float avail  = units * perUnit;
            float offer  = MathF.Min(want, avail);
            units -= offer / perUnit;
            if (damageCell(gtx, gty, offer)) return true;
            if (chunks.GetCellState(gtx, gty) != TileState.Solid) return true;   // gone some other way
            float landed = chunks.Damage.Get(gtx, gty) - before;
            if (landed <= 0f) { units = 0f; return false; }  // fully absorbed: nothing gets past
            rate = landed / offer;
        }
        units = 0f;
        return false;
    }

    // The preview's counterpart of Spend: the same arithmetic at rate 1, terrain untouched.
    private static bool Afford(ChunkMap chunks, int gtx, int gty, ref float units)
    {
        var   type    = chunks.GetCellType(gtx, gty);
        float maxHp   = TileDamage.MaxHPFor(type);
        float perUnit = maxHp / LanceCostFor(type);
        float need    = (maxHp - chunks.Damage.Get(gtx, gty)) / perUnit;
        if (units > MinUnits && units >= need) { units -= need; return true; }
        units = 0f;
        return false;
    }

    // ── Body hitboxes ────────────────────────────────────────────────────────

    private static void PublishBlast(HitboxWorld hitboxes, Vector2 c, int hitId, EntityId source)
    {
        float step = MathHelper.TwoPi / BlastWedges;
        for (int i = 0; i < BlastWedges; i++)
        {
            float a0 = i * step, a1 = a0 + step, ac = a0 + 0.5f * step;
            var poly = new Polygon(new[] { Vector2.Zero, Dir(a0) * BlastRadius, Dir(a1) * BlastRadius });
            hitboxes.Publish(new Hitbox(
                poly.GetBoundingBox(c), hitId, BlastTileDamage, Dir(ac) * BlastKnockback,
                Faction.Hazard, source, BlastTint,
                targets: HitTargets.EntitiesOnly,          // tiles were done directly, above
                bodyDamage: BlastBodyDamage,
                shape: poly, shapePos: c,
                hitstunSecondsOverride: HitstunSeconds,
                origin: c,                                 // terrain shields bodies from the blast
                launchDecay: LaunchDecay,
                stunSeconds: StunSeconds));
        }
    }

    private static void PublishLance(HitboxWorld hitboxes, in SiegeReach reach, int hitId, EntityId source)
    {
        float pMaj = reach.XMajor ? reach.Muzzle.X : reach.Muzzle.Y;
        float pMin = reach.XMajor ? reach.Muzzle.Y : reach.Muzzle.X;
        for (int j = 0; j < reach.LaneCount; j++)
        {
            var lane = reach[j];
            if (MathF.Abs(lane.EndMajor - pMaj) < 1f) continue;   // stopped at the muzzle's face
            // The lane's band as a parallelogram, from the muzzle's column line to where it
            // ended. Not occluded: its length already IS the terrain answer.
            float eMin = pMin + (lane.EndMajor - pMaj) * reach.Slope;
            var verts = new[]
            {
                ToWorld(reach.XMajor, pMaj,          pMin + lane.MinorLo) - reach.Muzzle,
                ToWorld(reach.XMajor, lane.EndMajor, eMin + lane.MinorLo) - reach.Muzzle,
                ToWorld(reach.XMajor, lane.EndMajor, eMin + lane.MinorHi) - reach.Muzzle,
                ToWorld(reach.XMajor, pMaj,          pMin + lane.MinorHi) - reach.Muzzle,
            };
            var poly = new Polygon(verts);
            hitboxes.Publish(new Hitbox(
                poly.GetBoundingBox(reach.Muzzle), hitId, 0f, reach.Aim * LanceKnockback,
                Faction.Hazard, source, LanceTint,
                targets: HitTargets.EntitiesOnly,          // tiles were paid for in the march
                bodyDamage: LanceBodyDamage,
                shape: poly, shapePos: reach.Muzzle,
                hitstunSecondsOverride: HitstunSeconds,
                launchDecay: LaunchDecay,
                stunSeconds: StunSeconds));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Vector2 Unit(Vector2 v)
        => v.LengthSquared() > 1e-12f ? Vector2.Normalize(v) : new Vector2(0f, -1f);

    private static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));

    private static Vector2 ToWorld(bool xMajor, float major, float minor)
        => xMajor ? new Vector2(major, minor) : new Vector2(minor, major);
}

// One lane of the lance: its centre line, whether terrain stopped it and where, and what
// it spent. EndMajor / MinorLo / MinorHi are the band geometry Fire's body hitbox uses.
public struct SiegeLane
{
    public Vector2 Start, End;        // lane centre line, muzzle → where it ended
    public bool    Stopped;           // false = ran its full length
    public int     StopX, StopY;      // the cell that held it (int.MinValue when not stopped)
    public int     Broken;            // cells this lane broke
    public float   UnitsLeft;         // budget left (0 when stopped)
    public float   EndMajor;          // major-axis px where the lane ends
    public float   MinorLo, MinorHi;  // band edges relative to the aim line, px
}

// The shot's footprint (Fire) or would-be footprint (Preview). Fixed-capacity lanes so a
// per-frame preview allocates nothing.
public struct SiegeReach
{
    public const int MaxLanes = 4;

    public Vector2 Muzzle, Aim;
    public int     LaneCount;
    public int     HitId;               // 0 for a preview / tiles-only fire
    public int     BlastCellsBroken;    // Fire only
    public float   BlastRadius => SiegeBlast.BlastRadius;
    internal bool  XMajor;
    internal float Slope;

    private SiegeLane _l0, _l1, _l2, _l3;

    public readonly SiegeLane this[int i] => i switch
    {
        0 => _l0, 1 => _l1, 2 => _l2, 3 => _l3,
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };

    internal void SetLane(int i, SiegeLane lane)
    {
        switch (i)
        {
            case 0: _l0 = lane; break;
            case 1: _l1 = lane; break;
            case 2: _l2 = lane; break;
            case 3: _l3 = lane; break;
            default: throw new ArgumentOutOfRangeException(nameof(i));
        }
    }

    // How far (px along the aim) the furthest lane got — the lance's reach.
    public readonly float Reach
    {
        get
        {
            float r = 0f;
            for (int j = 0; j < LaneCount; j++)
                r = MathF.Max(r, Vector2.Dot(this[j].End - this[j].Start, Aim));
            return r;
        }
    }
}
