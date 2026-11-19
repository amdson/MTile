using Microsoft.Xna.Framework;

namespace MTile;

// The five seeds on the seed bar, in key order (1..5). Plans/PLANT_SEEDS_PLAN.md §4.
public enum SeedKind { Spitter, Snapper, Thorns, SiegePod, BlockEater }

// What a plant's figure is showing (render reads it; the sim never branches on it).
// Seed/Sprout = growing, Idle = mature and awake, Act = about to act / acting, Dormant =
// buried (§5), Wilt = badly hurt.
public enum PlantPose { Seed, Sprout, Idle, Act, Dormant, Wilt }

// Every rollback-varying field of a plant, one value block inside EntityData. The base
// (PlantEntity) owns the first group; subclasses own the "kind scratch" group, each
// documenting what it stores there. Position is NOT here: plants are Rooted, so the body
// never moves from the cell centre it was built at.
public struct PlantData
{
    // ── Base (PlantEntity) ──────────────────────────────────────────────────
    public EntityId Owner;      // the player who planted it
    public int      CellX, CellY;   // the plant's own (open) cell
    public int      RootDir;    // PlantRoot: which face of the cell holds the root tile
    public float    Growth;     // seconds grown (stops while dormant)
    public bool     Dormant;    // own cell is Solid this frame (buried)

    // ── Kind scratch (subclass-owned; meaning per kind) ─────────────────────
    public float    Timer;      // Spitter: cooldown left before the next wind-up may start
    public float    Clock;      // Spitter: seconds into the current wind-up
    public int      Phase;      // Spitter: 0 = ready/cooling, 1 = winding up
    public Vector2  Aim;        // Spitter: where the wind-up is aimed (world px) — the telegraph
    public int      HitId;      // free for kinds that publish their own hitboxes
    public int      Count;      // free (e.g. a patch size)
}

// Root face, relative to the plant's own cell. Below = the plant stands on a floor.
public enum PlantRoot { Below = 0, Left = 1, Right = 2, Above = 3 }

// Every plant number, in one place. All of them are STARTING POINTS (plan §4-§5: "numbers
// are starting points, to be kept as named constants and tuned by feel").
public static class PlantTuning
{
    // ── Shared rules ────────────────────────────────────────────────────────
    // Live plants per player (thorns are capped separately, §4.3).
    public const int   PlantCapPerPlayer   = 3;
    // A buried plant at FULL health dies after this long covered (§5).
    public const float BuryKillSeconds     = 10f;
    // Seedlings have little health; maturing adds (mature − seedling) on top of whatever
    // the seedling still has, so damage taken while growing is never healed.
    public const float SeedlingHealth      = 0.5f;
    // Health fraction below which an awake plant shows its Wilt pose.
    public const float WiltFraction        = 0.3f;
    // Planting reach from the player centre (px): the block build reach.
    public const float PlantReach          = BlockPaintAction.BuildReach;
    // Body half-size (px): a plant fills most of its 11 px cell.
    public const float BodyHalf            = 4.5f;

    // ── Costs (meter units, BuildMeters — §6.1 "one meter") ────────────────
    // Derived from stone's loaded build cost, as spikes are, so a material retune keeps
    // the relative price. A spitter is about ten stone tiles.
    public const float SpitterCostInStone    = 10f;
    public const float SnapperCostInStone    = 12f;
    public const float ThornsCostInStone     = 8f;
    public const float SiegePodCostInStone   = 16f;
    public const float BlockEaterCostInStone = 12f;

    public static float CostInStone(SeedKind kind) => kind switch
    {
        SeedKind.Spitter    => SpitterCostInStone,
        SeedKind.Snapper    => SnapperCostInStone,
        SeedKind.Thorns     => ThornsCostInStone,
        SeedKind.SiegePod   => SiegePodCostInStone,
        SeedKind.BlockEater => BlockEaterCostInStone,
        _                   => SpitterCostInStone,
    };

    public static float CostFor(SeedKind kind)
        => CostInStone(kind) * MaterialStrengths.BuildCostFor(TileType.Stone);

    // ── Spitter (§4.1) ──────────────────────────────────────────────────────
    public const float SpitterGrowSeconds    = 12f;
    public const float SpitterHealth         = 1.5f;              // mature pool (≈ 5-6 pokes)
    public const float SpitterRangeTiles     = 20f;
    public const float SpitterRange          = SpitterRangeTiles * Chunk.TileSize;
    public const float SpitterShotPeriod     = 1.5f;              // one shot per this, wind-up included
    public const float SpitterWindup         = 0.35f;             // the "about to spit" tell
    public const float SpitterShotSpeed      = 170f;              // px/s — slow, dodgeable
    public const float SpitterShotDamage     = 0.25f;             // poke tier: a held guard blocks it
    public const float SpitterShotLifetime   = SpitterRange / SpitterShotSpeed + 0.25f;
    public const float SpitterMouthOffset    = 5f;                // px from cell centre, away from the root

    // ── Snapper (§4.2) ──────────────────────────────────────────────────────
    public const float SnapperGrowSeconds    = 14f;
    public const float SnapperHealth         = 2.2f;              // sturdier than the spitter (1.5)
    public const float SnapperRangeTiles     = 2f;                // target trigger, head to body centre
    public const float SnapperRange          = SnapperRangeTiles * Chunk.TileSize;
    public const float SnapperWindup         = 0.4f;              // the readable tell
    public const float SnapperBiteActive     = 0.15f;             // seconds the bite hitbox is live
    public const float SnapperRearm          = 4f;                // spent, harmless, after a bite
    public const float SnapperTrackSpeed     = 40f;               // px/s the aim may chase the target during wind-up
    public const float SnapperBiteReach      = 2.6f * Chunk.TileSize;   // box length from the head
    public const float SnapperBiteWidth      = 1.6f * Chunk.TileSize;
    public const float SnapperBiteDamage     = 0.7f;              // heavy tier: beats a held guard (> 0.5)
    public const float SnapperKnockback      = 150f;
    public const float SnapperHitstun        = 0.35f;
    public const float SnapperLaunchDecay    = 350f;              // as GroundSlash3
    public const float SnapperHeadOffset     = 6f;                // px from cell centre, away from the root
}
