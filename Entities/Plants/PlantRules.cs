using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Where a plant would sit: its own open cell and the solid cell it roots in.
public readonly struct PlantSite
{
    public readonly int       CellX, CellY;
    public readonly PlantRoot Root;
    public PlantSite(int cx, int cy, PlantRoot root) { CellX = cx; CellY = cy; Root = root; }

    public Point   RootCell => PlantRules.RootCell(CellX, CellY, Root);
    public Vector2 Center   => new((CellX + 0.5f) * Chunk.TileSize, (CellY + 0.5f) * Chunk.TileSize);
    // Unit vector from the root face into the cell, i.e. the way the plant grows.
    public Vector2 Up       => PlantRules.UpOf(Root);
}

// Why a planting would or would not take. Ok is the only success; the rest are what the
// cursor ghost tints red for (in this priority order: the first failing rule wins).
public enum PlantVerdict { Ok, NotImplemented, OutOfReach, NoRoot, Occupied, CapReached, CantAfford }

// Everything a planting request carries. Built by BlockPlaceAction (seed mode) from the
// player's EnvironmentContext; tests build it by hand. Aim (world px) is only read by the
// siege pod: the vector from the pod's cell centre to the cursor.
public struct PlantRequest
{
    public SeedKind Seed;
    public EntityId Owner;      // the planting player's World id
    public Faction  Faction;    // the owner's faction — the plant's hurtbox and hitboxes carry it
    public int      Team;       // the owner's team (spike rule)
    public Vector2  OwnerPos;   // the planter's body centre (reach falls back to this)
    public BoundingBox OwnerBounds; // the planter's body box, for the reach check (default:
                                // the standing player polygon at OwnerPos — PlantRules.BodyBounds)
    public int      CellX, CellY;
    public Vector2  Aim;        // siege pod: cell centre → cursor (zero elsewhere)
}

// The planting rules as pure statics, shared by SeedPlanting.TryPlant, the cursor ghost
// (Drawing/PlantGhost.cs) and future per-kind exceptions (the block-eater's enemy-block
// root). Nothing here mutates anything.
public static class PlantRules
{
    // Faces tried in this order, so a cell with a dirt floor stands on it.
    private static readonly PlantRoot[] RootOrder =
        { PlantRoot.Below, PlantRoot.Left, PlantRoot.Right, PlantRoot.Above };

    public static Point RootCell(int cx, int cy, PlantRoot r) => r switch
    {
        PlantRoot.Below => new Point(cx, cy + 1),
        PlantRoot.Left  => new Point(cx - 1, cy),
        PlantRoot.Right => new Point(cx + 1, cy),
        _               => new Point(cx, cy - 1),
    };

    public static Vector2 UpOf(PlantRoot r) => r switch
    {
        PlantRoot.Below => new Vector2(0f, -1f),
        PlantRoot.Left  => new Vector2(1f, 0f),
        PlantRoot.Right => new Vector2(-1f, 0f),
        _               => new Vector2(0f, 1f),
    };

    // Sprite rotation that turns a figure drawn upright (root below, +y down) onto `r`.
    public static float RotationOf(PlantRoot r) => r switch
    {
        PlantRoot.Below => 0f,
        PlantRoot.Left  => MathHelper.PiOver2,
        PlantRoot.Right => -MathHelper.PiOver2,
        _               => MathHelper.Pi,
    };

    // The cell can hold a plant at all: plainly empty (not solid, not sprouting, not a
    // spike tile) and not a pending build ghost.
    public static bool CellOpen(ChunkMap chunks, int cx, int cy)
        => chunks.GetCellState(cx, cy) == TileState.Empty && !chunks.Graph.TryGet(cx, cy, out _);

    // A cell is a root for the shared rule when it is Solid DIRT (decision 10).
    public static bool IsDirtRoot(ChunkMap chunks, int x, int y)
        => chunks.GetCellState(x, y) == TileState.Solid && chunks.GetCellType(x, y) == TileType.Dirt;

    // The shared dirt rule: an open cell with a solid dirt face. First face in RootOrder.
    public static bool TryDirtSite(ChunkMap chunks, int cx, int cy, out PlantSite site)
    {
        site = default;
        if (chunks == null || !CellOpen(chunks, cx, cy)) return false;
        foreach (var r in RootOrder)
        {
            var rc = RootCell(cx, cy, r);
            if (!IsDirtRoot(chunks, rc.X, rc.Y)) continue;
            site = new PlantSite(cx, cy, r);
            return true;
        }
        return false;
    }

    // The per-kind site rule. Everything roots by the dirt rule except the block-eater,
    // which roots on an enemy-owned block of any material (§4) — its parcel replaces its
    // case. Thorns need dirt only for the seed cell, which is this same rule.
    public static bool TryFindSite(SeedKind seed, ChunkMap chunks, int cx, int cy, out PlantSite site)
    {
        switch (seed)
        {
            case SeedKind.BlockEater:
                // Handled in Evaluate (it needs the planter's faction); never a dirt site.
                site = default;
                return false;
            default:
                return TryDirtSite(chunks, cx, cy, out site);
        }
    }

    // Does the root still hold? The plant dies the moment its root tile is not Solid.
    public static bool RootHolds(ChunkMap chunks, int cx, int cy, PlantRoot r)
    {
        var rc = RootCell(cx, cy, r);
        return chunks.GetCellState(rc.X, rc.Y) == TileState.Solid;
    }

    // ── Reach ───────────────────────────────────────────────────────────────
    // Planting is close work: the block the plant ROOTS in (the block-eater: the target
    // block itself) must be beside or beneath the planter's body. Concretely the root cell
    // must overlap the body's bounding box grown by PlantReachMargin to the left, right and
    // below — not above — and its centre must not be above the top of the body (no
    // ceilings, nothing higher than the head). Ceiling roots (PlantRoot.Above) are refused
    // outright for the same reason.

    // The planter's body box. A request built from a live context carries it; a hand-built
    // one (tests, bots) may leave it default and get the standing player polygon at OwnerPos.
    private static readonly Polygon StandardBody = PlayerCharacter.CreateBodyPolygon();

    public static BoundingBox BodyBounds(in PlantRequest req)
        => req.OwnerBounds.Width > 0f ? req.OwnerBounds : StandardBody.GetBoundingBox(req.OwnerPos);

    public static bool InReach(BoundingBox body, int rx, int ry)
    {
        const float T = Chunk.TileSize;
        float m = PlantTuning.PlantReachMargin;
        float l = rx * T, r = l + T, t = ry * T;
        if (!(r > body.Left - m && l < body.Right + m)) return false;   // beside, within the margin
        if (t + 0.5f * T < body.Top) return false;                      // never above the head
        return t < body.Bottom + m;                                     // down to the margin below
    }

    // TryDirtSite restricted to roots in reach of `body`: the first face in RootOrder whose
    // root is dirt AND in reach, ceilings excluded. Picks the floor under/beside the
    // planter, else a wall face beside them.
    public static bool TryDirtSiteInReach(ChunkMap chunks, int cx, int cy, BoundingBox body, out PlantSite site)
    {
        site = default;
        if (chunks == null || !CellOpen(chunks, cx, cy)) return false;
        foreach (var r in RootOrder)
        {
            if (r == PlantRoot.Above) continue;
            var rc = RootCell(cx, cy, r);
            if (!IsDirtRoot(chunks, rc.X, rc.Y) || !InReach(body, rc.X, rc.Y)) continue;
            site = new PlantSite(cx, cy, r);
            return true;
        }
        return false;
    }

    // A live plant already stands in this cell.
    public static bool Occupied(IEnumerable<Entity> entities, int cx, int cy)
    {
        if (entities == null) return false;
        foreach (var e in entities)
            if (e is PlantEntity p && !p.IsDead && !p.Spent && p.CellX == cx && p.CellY == cy) return true;
        return false;
    }

    // ── Caps ────────────────────────────────────────────────────────────────
    // Which shared cap a seed counts against. Thorns are capped separately (§4.3); the
    // siege pod additionally allows one alive per player (checked in Evaluate below).
    public enum CapGroup { Plants, Thorns }

    public static CapGroup GroupOf(SeedKind seed) => seed == SeedKind.Thorns ? CapGroup.Thorns : CapGroup.Plants;

    public static int CapFor(CapGroup g) => g switch
    {
        CapGroup.Plants => PlantTuning.PlantCapPerPlayer,
        // Thorns are not entities; their caps live in ThornPatches and are checked in Evaluate.
        _               => int.MaxValue,
    };

    // Live plants of `owner` in `group`, in any order (a count is order-free).
    public static int LiveCount(IEnumerable<Entity> entities, EntityId owner, CapGroup group)
    {
        int n = 0;
        if (entities == null) return 0;
        foreach (var e in entities)
            if (e is PlantEntity p && !p.IsDead && !p.Spent && p.Owner == owner && GroupOf(p.Seed) == group) n++;
        return n;
    }

    public static int LiveCount(IEnumerable<Entity> entities, EntityId owner, SeedKind seed)
    {
        int n = 0;
        if (entities == null) return 0;
        foreach (var e in entities)
            if (e is PlantEntity p && !p.IsDead && !p.Spent && p.Owner == owner && p.Seed == seed) n++;
        return n;
    }

    // The whole legality check, in verdict order. `meters` may be null (skip the cost
    // rule — the ghost of a player with no meters, a test).
    public static PlantVerdict Evaluate(in PlantRequest req, ChunkMap chunks, IEnumerable<Entity> entities,
                                        BuildMeters meters, out PlantSite site)
    {
        site = default;
        if (!SeedPlanting.IsImplemented(req.Seed))            return PlantVerdict.NotImplemented;
        var body = BodyBounds(in req);
        if (req.Seed == SeedKind.BlockEater)
        {
            if (!InReach(body, req.CellX, req.CellY))         return PlantVerdict.OutOfReach;
            // The one seed that roots ON a block: a solid cell owned by another player, any
            // material (§4.5). Its own cap (one live patch per player) is inside CanPlant.
            site = new PlantSite(req.CellX, req.CellY, PlantRoot.Below);
            if (!chunks.CanPlantBlockEater(req.CellX, req.CellY, BlockOwnership.OwnerFor(req.Faction)))
                return PlantVerdict.NoRoot;
            if (meters != null && meters.BuildMove + meters.EruptMove < PlantTuning.CostFor(req.Seed))
                return PlantVerdict.CantAfford;
            return PlantVerdict.Ok;
        }
        // Every other seed roots by the dirt rule, on a root in reach. A cell that has a dirt
        // root, just none the planter can reach, reports OutOfReach (the ghost's red).
        if (!TryDirtSiteInReach(chunks, req.CellX, req.CellY, body, out site))
            return TryFindSite(req.Seed, chunks, req.CellX, req.CellY, out _) ? PlantVerdict.OutOfReach
                                                                                : PlantVerdict.NoRoot;
        if (Occupied(entities, req.CellX, req.CellY))         return PlantVerdict.Occupied;
        // Thorns are tile state, capped by ThornPatches (patches and cells per team, §4.3).
        if (req.Seed == SeedKind.Thorns && !chunks.CanPlantThornPatch(req.CellX, req.CellY, (byte)req.Team))
            return PlantVerdict.CapReached;
        var g = GroupOf(req.Seed);
        if (LiveCount(entities, req.Owner, g) >= CapFor(g))   return PlantVerdict.CapReached;
        // The siege pod: additionally one alive per player (§4.4). A pod that has fired is
        // Spent, so it frees the slot the moment it fires.
        if (req.Seed == SeedKind.SiegePod
            && LiveCount(entities, req.Owner, SeedKind.SiegePod) >= PlantTuning.SiegePodPerPlayer)
            return PlantVerdict.CapReached;
        if (meters != null && meters.BuildMove + meters.EruptMove < PlantTuning.CostFor(req.Seed))
            return PlantVerdict.CantAfford;
        return PlantVerdict.Ok;
    }
}
