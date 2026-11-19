using Microsoft.Xna.Framework;

namespace MTile;

// THE one sim-side entry point for planting (Plans/PLANT_SEEDS_PLAN.md §3). Every seed —
// whichever gesture, the player's or a bot's — goes through TryPlant: shared legality
// (PlantRules.Evaluate: reach, root, occupancy, cap, cost), then one `case` per seed that
// builds the plant, then the shared commit (spend + spawn). A case returns false for "not
// planted", and nothing is spent.
//
// Adding a seed (milestones M3-M6): fill your `case` below and flip your line in
// IsImplemented. Keep to your own lines so the parallel parcels merge cleanly.
public static class SeedPlanting
{
    // The seeds whose case is filled in. PlantRules.Evaluate reports NotImplemented for the
    // rest, so the ghost shows them illegal and TryPlant spends nothing.
    public static bool IsImplemented(SeedKind seed) => seed switch
    {
        SeedKind.Spitter    => true,
        SeedKind.Snapper    => true,
        SeedKind.Thorns     => true,
        SeedKind.SiegePod   => false,   // milestone M3
        SeedKind.BlockEater => true,
        _                   => false,
    };

    // Seeds planted on RELEASE rather than on press: the siege pod's press places, drag
    // aims, release commits (§3). BlockPlaceAction reads this to decide when to call
    // TryPlant; for these req.Aim is the press → release drag vector.
    public static bool CommitsOnRelease(SeedKind seed) => seed == SeedKind.SiegePod;

    // Plant req.Seed at (req.CellX, req.CellY). Returns true iff a plant was spawned and
    // paid for. `meters` is the owner's BuildMeters (null = free, for tests/stages);
    // `spawner` must be the sim (its LiveEntities is the cap/occupancy source).
    public static bool TryPlant(in PlantRequest req, BuildMeters meters, ChunkMap chunks, IEntitySpawner spawner)
    {
        if (chunks == null || spawner == null) return false;
        var verdict = PlantRules.Evaluate(in req, chunks, spawner.LiveEntities, meters, out var site);
        if (verdict != PlantVerdict.Ok) return false;

        PlantEntity plant;
        switch (req.Seed)
        {
            case SeedKind.Spitter:
                plant = new SpitterPlant(site, req.Owner, req.Faction, req.Team);
                break;
            case SeedKind.Snapper:
                plant = new SnapperPlant(site, req.Owner, req.Faction, req.Team);
                break;
            case SeedKind.Thorns:
                // Not an entity: a thorn patch is tile state (World/ThornPatches.cs). Evaluate
                // already asked CanPlantThornPatch this frame, so the plant cannot fail after
                // the spend.
                if (meters != null && !meters.TrySpendUnits(PlantTuning.CostFor(req.Seed))) return false;
                return chunks.TryPlantThornPatch(req.CellX, req.CellY, (byte)req.Team);
            case SeedKind.SiegePod:
                return false; // filled by milestone M3 (req.Aim is the drag vector)
            case SeedKind.BlockEater:
                // Not an entity either: an infection in World/BlockEaterInfection.cs, planted ON
                // an enemy-owned block. Evaluate asked CanPlantBlockEater this frame.
                if (meters != null && !meters.TrySpendUnits(PlantTuning.CostFor(req.Seed))) return false;
                return chunks.TryPlantBlockEater(req.CellX, req.CellY, BlockOwnership.OwnerFor(req.Faction));
            default:
                return false;
        }

        return Commit(plant, req.Seed, meters, spawner);
    }

    // Pay, then spawn. Spending is all-or-nothing, so a failed spend leaves no plant.
    private static bool Commit(PlantEntity plant, SeedKind seed, BuildMeters meters, IEntitySpawner spawner)
    {
        if (meters != null && !meters.TrySpendUnits(PlantTuning.CostFor(seed))) return false;
        spawner.SpawnEntity(plant);
        return true;
    }

    // The request the player's own context implies, for the cursor cell.
    public static PlantRequest RequestFrom(EnvironmentContext ctx, SeedKind seed, int cx, int cy, Vector2 aim)
        => new()
        {
            Seed     = seed,
            Owner    = ctx.SelfId,
            Faction  = ctx.Faction,
            Team     = ctx.Team,
            OwnerPos = ctx.Body.Position,
            CellX    = cx,
            CellY    = cy,
            Aim      = aim,
        };
}
