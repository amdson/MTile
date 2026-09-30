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
    // tie-breaks; the same inputs always produce the same result.
    public static ArenaResult Run(ChunkMap terrain, IReadOnlyList<ArenaEntry> fighters, Vector2 playerSpawn,
                                  int maxFrames = DefaultMaxFrames, ICostModel model = null)
    {
        if (fighters.Count > EntityKinds.FighterSlotCount)
            throw new ArgumentException($"At most {EntityKinds.FighterSlotCount} fighters per match.");
        model ??= new PhysicsCostModel();

        var bots = new EnemyEntity[fighters.Count];
        var sim = new Simulation(terrain, playerSpawn, g =>
        {
            for (int i = 0; i < fighters.Count; i++)
            {
                var spec  = fighters[i].Spec;
                spec.Kind = EntityKinds.FighterSlot(i);
                spec.Team = fighters[i].Team;
                var r = FighterCompiler.Register(spec, model);
                if (!r.IsValid)
                    throw new InvalidOperationException($"Fighter '{spec.Name}' is invalid:\n{r.Report()}");
                var e = EnemyFactory.Create(spec.Kind, fighters[i].Pos);
                bots[i] = (EnemyEntity)e;
                g.SpawnEntity(e);
            }
        });

        var health = new float[bots.Length];
        var dealt  = new float[bots.Length];
        for (int i = 0; i < bots.Length; i++) health[i] = bots[i].Health;

        int frame = 0;
        for (; frame < maxFrames; frame++)
        {
            sim.Step(default);

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

            if (TeamsAlive(bots) <= 1) { frame++; break; }
        }

        return new ArenaResult(Winner(bots, health, fighters), frame, health, dealt);
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

    // The team with the most health fraction left. A knockout is the degenerate case
    // (the dead side has 0). Exact tie ⇒ draw (-1).
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

    public static ChunkMap Flat()     => AsciiTerrain.FromAscii(Ascii(roof: false, hills: false));
    public static ChunkMap Corridor() => AsciiTerrain.FromAscii(Ascii(roof: true,  hills: false));
    public static ChunkMap Hills()    => AsciiTerrain.FromAscii(Ascii(roof: false, hills: true));

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
                // Roofed corridor: four tiles (64 px) of headroom — enough for any body in
                // the roster, low enough that a Flyer cannot climb out of reach and an air
                // block has somewhere to hang.
                if (roof && row < FloorRow - 4) solid = true;
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
}
