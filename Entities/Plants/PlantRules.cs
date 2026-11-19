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
// player's EnvironmentContext; tests build it by hand. Aim is press → release (world px),
// captured for every seed even though only the siege pod reads it.
public struct PlantRequest
{
    public SeedKind Seed;
    public EntityId Owner;      // the planting player's World id
    public Faction  Faction;    // the owner's faction — the plant's hurtbox and hitboxes carry it
    public int      Team;       // the owner's team (spike rule)
    public Vector2  OwnerPos;   // for the reach check
    public int      CellX, CellY;
    public Vector2  Aim;        // press → release drag vector (zero for press-commit seeds)
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
                // filled by milestone M6 (enemy-owned block root); dirt rule until then
                return TryDirtSite(chunks, cx, cy, out site);
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

    public static bool InReach(Vector2 ownerPos, int cx, int cy)
    {
        var c = new Vector2((cx + 0.5f) * Chunk.TileSize, (cy + 0.5f) * Chunk.TileSize);
        return Vector2.DistanceSquared(ownerPos, c) <= PlantTuning.PlantReach * PlantTuning.PlantReach;
    }

    // A live plant already stands in this cell.
    public static bool Occupied(IEnumerable<Entity> entities, int cx, int cy)
    {
        if (entities == null) return false;
        foreach (var e in entities)
            if (e is PlantEntity p && !p.IsDead && p.CellX == cx && p.CellY == cy) return true;
        return false;
    }

    // ── Caps ────────────────────────────────────────────────────────────────
    // Which shared cap a seed counts against. Thorns are capped separately (§4.3); the
    // siege pod additionally allows one alive per player (its parcel adds that check in
    // its TryPlant case or here).
    public enum CapGroup { Plants, Thorns }

    public static CapGroup GroupOf(SeedKind seed) => seed == SeedKind.Thorns ? CapGroup.Thorns : CapGroup.Plants;

    public static int CapFor(CapGroup g) => g switch
    {
        CapGroup.Plants => PlantTuning.PlantCapPerPlayer,
        // filled by milestone M4 (thorn patch cap, start 2)
        _               => 2,
    };

    // Live plants of `owner` in `group`, in any order (a count is order-free).
    public static int LiveCount(IEnumerable<Entity> entities, EntityId owner, CapGroup group)
    {
        int n = 0;
        if (entities == null) return 0;
        foreach (var e in entities)
            if (e is PlantEntity p && !p.IsDead && p.Owner == owner && GroupOf(p.Seed) == group) n++;
        return n;
    }

    public static int LiveCount(IEnumerable<Entity> entities, EntityId owner, SeedKind seed)
    {
        int n = 0;
        if (entities == null) return 0;
        foreach (var e in entities)
            if (e is PlantEntity p && !p.IsDead && p.Owner == owner && p.Seed == seed) n++;
        return n;
    }

    // The whole legality check, in verdict order. `meters` may be null (skip the cost
    // rule — the ghost of a player with no meters, a test).
    public static PlantVerdict Evaluate(in PlantRequest req, ChunkMap chunks, IEnumerable<Entity> entities,
                                        BuildMeters meters, out PlantSite site)
    {
        site = default;
        if (!SeedPlanting.IsImplemented(req.Seed))            return PlantVerdict.NotImplemented;
        if (!InReach(req.OwnerPos, req.CellX, req.CellY))     return PlantVerdict.OutOfReach;
        if (!TryFindSite(req.Seed, chunks, req.CellX, req.CellY, out site)) return PlantVerdict.NoRoot;
        if (Occupied(entities, req.CellX, req.CellY))         return PlantVerdict.Occupied;
        var g = GroupOf(req.Seed);
        if (LiveCount(entities, req.Owner, g) >= CapFor(g))   return PlantVerdict.CapReached;
        if (meters != null && meters.BuildMove + meters.EruptMove < PlantTuning.CostFor(req.Seed))
            return PlantVerdict.CantAfford;
        return PlantVerdict.Ok;
    }
}
