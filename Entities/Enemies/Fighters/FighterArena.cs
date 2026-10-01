using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;

namespace MTile;

// Fighter-vs-fighter match runner (Plans/FIGHTER_DESIGN_PLAN.md §8.1). Library-side so
// the tests AND the forge CLI (MTile.Bench --forge) share it; never called from the game
// loop. It drives a real Simulation so the phase ordering, combat pass,
// targeting and physics are the ones the game uses. A match is a few thousand
// Step calls, which is what makes "design the best fighter" an optimisation
// problem rather than a spreadsheet.
//
// The player is a rooted-by-distance dummy: spawned at the far left of the terrain
// with idle input, so far from the fighters that "nearest opposing target" is always
// the other fighter. (Fighters are on teams ≥ 1; the player is team 0, so once a side
// is dead the survivor will turn toward the player — by then the match is over.)
//
// Damage dealt has no attacker hook in the sim, so it is attributed by diffing each
// fighter's health frame to frame and crediting the loss to its live opponents —
// exact in a 1v1, split evenly in a team fight.
public sealed record ArenaResult(int WinnerTeam, int Frames, float[] HealthLeft, float[] DamageDealt)
{
    public bool Draw => WinnerTeam < 0;
}

public sealed record ArenaEntry(FighterSpec Spec, Vector2 Pos, int Team);

public static class FighterArena
{
    public const int DefaultMaxFrames = 60 * 30;   // 30 s

    // Runs one match. Specs are compiled with `model` (default: the physics model with
    // its default budget) and registered under the scratch kinds FighterSlot0.. so any
    // spec — hand-written or searched — can fight without an EntityKind of its own.
    // Spawn order is entry order, which fixes ECS iteration order and therefore target
    // tie-breaks; the same inputs always produce the same result. `seed` is the bout
    // seed each brain's random stream is derived from (ArenaCondition explains it);
    // the positions in `fighters` are the caller's — a seeded condition's or not.
    public static ArenaResult Run(ChunkMap terrain, IReadOnlyList<ArenaEntry> fighters, Vector2 playerSpawn,
                                  int maxFrames = DefaultMaxFrames, ICostModel model = null,
                                  List<ulong> checksums = null, ulong seed = 0)
    {
        var bots = new EnemyEntity[fighters.Count];
        var sim  = new Simulation(terrain, playerSpawn, g => Populate(g, fighters, model, bots, seed));

        var health = new float[bots.Length];
        var dealt  = new float[bots.Length];
        for (int i = 0; i < bots.Length; i++) health[i] = bots[i].Health;

        int  frame   = 0;
        bool decided = false;
        for (; frame < maxFrames; frame++)
        {
            sim.Step(default);
            // Optional per-frame fingerprint — what a FightRecord stores so a replay can
            // prove it is the same fight (or name the first frame where it is not).
            checksums?.Add(sim.Checksum());

            // Attribute this frame's losses to live opponents.
            for (int i = 0; i < bots.Length; i++)
            {
                float now  = MathF.Max(bots[i].Health, 0f);
                float lost = health[i] - now;
                health[i]  = now;
                if (lost <= 0f) continue;
                int opponents = 0;
                for (int j = 0; j < bots.Length; j++)
                    if (bots[j].Team != bots[i].Team && !bots[j].IsDead) opponents++;
                if (opponents == 0) continue;
                for (int j = 0; j < bots.Length; j++)
                    if (bots[j].Team != bots[i].Team && !bots[j].IsDead) dealt[j] += lost / opponents;
            }

            if (TeamsAlive(bots) <= 1) { frame++; decided = true; break; }
        }

        // Only an elimination decides a bout. A timeout is a draw whatever the health
        // left (plan §11.1): resolving it on margin made "land one hit and hide" the
        // optimal strategy, and two waves of packages proved it. HealthLeft/DamageDealt
        // are still reported for the forge's shaped objective and for reading a fight.
        return new ArenaResult(decided ? Winner(bots, health, fighters) : -1, frame, health, dealt);
    }

    // Compile each entry under its slot kind and spawn it, in entry order. Shared by
    // Run and by the in-game fight stage (FightRecord.ToStage), so what you watch is
    // spawned exactly as what was scored. Each brain's random stream is seeded from
    // the bout seed and its entry index — so a seed names the whole bout, and the two
    // sides never share a stream.
    public static void Populate(Simulation g, IReadOnlyList<ArenaEntry> fighters, ICostModel model, EnemyEntity[] bots = null,
                                ulong seed = 0)
    {
        if (fighters.Count > EntityKinds.FighterSlotCount)
            throw new ArgumentException($"At most {EntityKinds.FighterSlotCount} fighters per match.");
        model ??= new PhysicsCostModel();
        for (int i = 0; i < fighters.Count; i++)
        {
            var spec  = fighters[i].Spec;
            spec.Kind = EntityKinds.FighterSlot(i);
            spec.Team = fighters[i].Team;
            // Kept in the sim's own blueprint table, never the process-wide EnemyFactory
            // registry: concurrent bouts (league --threads) share the slot kinds, and a
            // shared table would let one bout spawn (or rehydrate) another's fighter.
            var r = FighterCompiler.Compile(spec, model);
            if (!r.IsValid)
                throw new InvalidOperationException($"Fighter '{spec.Name}' is invalid:\n{r.Report()}");
            g.Blueprints[spec.Kind] = r.Blueprint;
            var e = new BlueprintEnemy(r.Blueprint, fighters[i].Pos);
            e.SeedBrain(BrainRandom.Mix(seed, (ulong)i + 1));
            if (bots != null) bots[i] = e;
            g.SpawnEntity(e);
        }
    }

    private static int TeamsAlive(EnemyEntity[] bots)
    {
        int mask = 0, count = 0;
        foreach (var b in bots)
        {
            if (b.IsDead) continue;
            int bit = 1 << Math.Clamp(b.Team, 0, 30);
            if ((mask & bit) != 0) continue;
            mask |= bit; count++;
        }
        return count;
    }

    // After an elimination: the team with the most health fraction left (the dead side
    // has 0, so in a 1v1 this is simply the survivor). Everyone dead on the same frame ⇒
    // draw (-1). Never called on a timeout.
    private static int Winner(EnemyEntity[] bots, float[] health, IReadOnlyList<ArenaEntry> entries)
    {
        var frac = new Dictionary<int, float>();
        for (int i = 0; i < bots.Length; i++)
        {
            int t = entries[i].Team;
            frac.TryGetValue(t, out float f);
            frac[t] = f + health[i] / MathF.Max(bots[i].MaxHealth, 1e-3f);
        }
        int best = -1; float bestF = -1f; bool tie = false;
        foreach (var (t, f) in frac)
        {
            if (f > bestF + 1e-6f) { best = t; bestF = f; tie = false; }
            else if (MathF.Abs(f - bestF) <= 1e-6f) tie = true;
        }
        return tie ? -1 : best;
    }

    // ── Standard terrains (§8.2): flat floor, roofed corridor, stepped hills ──────
    // All 120 tiles (1920 px) wide, floor top at FloorTopY, with the player's parking
    // spot at the far left and the fighting ground on the right.

    public const int   Width     = 120;
    public const int   FloorRow  = 10;
    public const float FloorTopY = FloorRow * Chunk.TileSize;   // 160
    public static readonly Vector2 PlayerPark = new(2 * Chunk.TileSize, FloorTopY - 12f);
    // Opponents 180 px apart (inside every bundled brain's AlertRange of 260).
    public static readonly Vector2 LeftSpawn  = new(84 * Chunk.TileSize, FloorTopY - 14f);   // 1344
    public static readonly Vector2 RightSpawn = new(95 * Chunk.TileSize + 4f, FloorTopY - 14f); // 1524

    public static ChunkMap Flat()     => AsciiTerrain.FromAscii(TerrainAscii("flat"));
    public static ChunkMap Corridor() => AsciiTerrain.FromAscii(TerrainAscii("corridor"));
    public static ChunkMap Hills()    => AsciiTerrain.FromAscii(TerrainAscii("hills"));

    // The ascii behind a named terrain — what a FightRecord stores, so a saved fight is
    // self-contained even if these generators change later.
    public static string TerrainAscii(string name) => name switch
    {
        "flat"     => Ascii(roof: false, hills: false),
        "corridor" => Ascii(roof: true,  hills: false),
        "hills"    => Ascii(roof: false, hills: true),
        _ => throw new ArgumentException($"Unknown arena terrain '{name}' (flat / corridor / hills).", nameof(name)),
    };

    public static IReadOnlyList<(string name, Func<ChunkMap> make)> Terrains { get; } = new (string, Func<ChunkMap>)[]
    {
        ("flat", Flat), ("corridor", Corridor), ("hills", Hills),
    };

    private static string Ascii(bool roof, bool hills)
    {
        var sb = new StringBuilder();
        for (int row = 0; row < FloorRow + 2; row++)
        {
            sb.Append('\n');
            for (int col = 0; col < Width; col++)
            {
                bool solid = row >= FloorRow;
                // Roofed corridor: six tiles (66 px at the 11 px tile) of headroom — enough
                // that a flyer can still get over a walker, low enough that it cannot climb
                // out of reach and an air block has somewhere to hang.
                if (roof && row < FloorRow - 6) solid = true;
                if (hills)
                {
                    // Two steps up between the spawns and a mound behind the right one:
                    //   cols 88–90 one tile, 91–110 two tiles, 100–104 three tiles.
                    int rise = col is >= 88 and <= 90 ? 1 : col is >= 91 and <= 110 ? 2 : 0;
                    if (col is >= 100 and <= 104) rise = 3;
                    if (row >= FloorRow - rise) solid = true;
                }
                sb.Append(solid ? 'X' : 'O');
            }
        }
        return sb.ToString();
    }

    // Right spawn sits on the hills' two-tile step; the flat/corridor value otherwise.
    public static Vector2 RightSpawnFor(string terrain)
        => terrain == "hills" ? RightSpawn - new Vector2(0f, 2 * Chunk.TileSize) : RightSpawn;

    // A body standing on the ground at column x: 14 px above the top of the first
    // solid cell at or above the floor row (walked upward from the floor so a roof
    // never counts). Reproduces LeftSpawn / RightSpawnFor exactly at their columns.
    public static Vector2 GroundSpawn(ChunkMap map, float x)
    {
        int gtx = (int)MathF.Floor(x / Chunk.TileSize);
        int row = FloorRow;
        while (row > 0 && map.GetCellState(gtx, row - 1) == TileState.Solid) row--;
        return new Vector2(x, row * Chunk.TileSize - 14f);
    }

    // The two sides' entries for a condition, left on team 1, right on team 2 — the
    // league's and the recorder's convention.
    public static List<ArenaEntry> Entries(in ArenaCondition c, FighterSpec left, FighterSpec right, ChunkMap map = null)
    {
        map ??= AsciiTerrain.FromAscii(TerrainAscii(c.Terrain));
        return new List<ArenaEntry>
        {
            new(left,  c.LeftSpawn(map),  1),
            new(right, c.RightSpawn(map), 2),
        };
    }
}

// A seeded start (Plans/FIGHTER_DESIGN_PLAN.md §10.2, §11.5): where the two fighters
// stand, and the seed each brain's random stream is derived from. Seed 0 is the
// CANONICAL start — the legacy spawns, 180 px apart — so everything that ran before
// seeds existed still runs the same; any other seed draws the separation
// (MinSeparation..MaxSeparation) and slides the pair along the fighting ground
// (±CentreShift). Spawn heights follow the terrain, so on the hills a draw may put
// either side on a step. The same seed means the same geometry on every terrain and
// for every pair, which is what makes one seed a "round" of a league.
//
// Separation stays under the stock brains' AlertRange (260) so no condition starts
// with a roster fighter asleep; the ceiling is the arena's, not a brain's.
public readonly record struct ArenaCondition(string Terrain, ulong Seed, float Separation, float CentreX)
{
    public const float MinSeparation = 120f;
    public const float MaxSeparation = 250f;
    public const float CentreShift   = 60f;

    public static float CanonicalSeparation => FighterArena.RightSpawn.X - FighterArena.LeftSpawn.X;   // 180
    public static float CanonicalCentre     => (FighterArena.LeftSpawn.X + FighterArena.RightSpawn.X) * 0.5f;

    public static ArenaCondition Derive(string terrain, ulong seed)
    {
        if (seed == 0) return new ArenaCondition(terrain, 0, CanonicalSeparation, CanonicalCentre);
        ulong s = BrainRandom.Seed(seed);
        float sep   = MathF.Round(MinSeparation + (MaxSeparation - MinSeparation) * BrainRandom.Unit(ref s));
        float shift = MathF.Round(-CentreShift + 2f * CentreShift * BrainRandom.Unit(ref s));
        return new ArenaCondition(terrain, seed, sep, MathF.Round(CanonicalCentre) + shift);
    }

    public bool IsCanonical => Seed == 0;

    public Vector2 LeftSpawn(ChunkMap map)
        => IsCanonical ? FighterArena.LeftSpawn : FighterArena.GroundSpawn(map, CentreX - Separation * 0.5f);
    public Vector2 RightSpawn(ChunkMap map)
        => IsCanonical ? FighterArena.RightSpawnFor(Terrain) : FighterArena.GroundSpawn(map, CentreX + Separation * 0.5f);

    public override string ToString()
        => IsCanonical ? $"{Terrain} seed 0 (canonical, {Separation:0} px apart)"
                       : $"{Terrain} seed {Seed} ({Separation:0} px apart, centre {CentreX:0})";
}
