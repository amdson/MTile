using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;

namespace MTile.Tests.Sim;

// Shared arena for the Plant*Tests: 64 columns, a floor from row 12 down. Row 12 (the
// surface) is DIRT except columns 30-35, which are STONE; everything below is stone. The
// primary player stands at column 4. Not a test class itself.
internal static class PlantTestArena
{
    public const int   FloorRow   = 12;
    public const int   StoneFrom  = 30, StoneTo = 35;
    public const float Ts         = Chunk.TileSize;
    public const float Dt         = Simulation.FixedDt;

    public static ChunkMap Terrain()
    {
        var rows = new List<string>();
        for (int y = 0; y < 16; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 64; x++) sb.Append(y >= FloorRow ? 'X' : 'O');
            rows.Add(sb.ToString());
        }
        var chunks = SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
        for (int x = 0; x < 64; x++)
            for (int y = FloorRow; y < 16; y++)
                chunks.EditorSetCell(x, y, TileState.Solid,
                    y == FloorRow && (x < StoneFrom || x > StoneTo) ? TileType.Dirt : TileType.Stone);
        return chunks;
    }

    public static Vector2 Spawn(int col) => new(col * Ts + Ts * 0.5f, FloorRow * Ts - 14f);
    public static Vector2 CellCenter(int x, int y) => new((x + 0.5f) * Ts, (y + 0.5f) * Ts);

    public static Simulation Make(System.Action<Simulation> populate = null)
        => new(Terrain(), Spawn(4), populate);

    public static void Run(Simulation s, float seconds)
    {
        int n = (int)System.MathF.Round(seconds / Dt);
        for (int i = 0; i < n; i++) s.Step(default);
    }

    public static void Run2(Simulation s, float seconds)
    {
        int n = (int)System.MathF.Round(seconds / Dt);
        for (int i = 0; i < n; i++) s.Step(default, default);
    }

    // Planting is crouched, close work (PlantRules.InReach): hold Down until the crouch has
    // sunk (about 8 frames after CrouchedState enters), so the floor beside and beneath the
    // body is in reach.
    public static void Crouch(Simulation s, float seconds = 0.5f)
    {
        int n = (int)System.MathF.Round(seconds / Dt);
        for (int i = 0; i < n; i++) s.Step(new PlayerInput { Down = true });
    }

    // Slide the body over column `col`, keeping its height (a crouched body stays crouched
    // for a direct TryPlant/Evaluate call made before the next step).
    public static void PutOver(PlayerCharacter p, int col)
        => p.Body.Position = new Vector2((col + 0.5f) * Ts, p.Body.Position.Y);

    public static List<PlantEntity> Plants(Simulation s) => s.Entities.OfType<PlantEntity>().Where(p => !p.IsDead).ToList();

    public static SpitterPlant Spitter(Simulation s, int cx, int cy, PlayerCharacter owner)
        => new(new PlantSite(cx, cy, PlantRoot.Below), owner.Id, owner.Faction, owner.Team);

    public static PlantRequest Request(PlayerCharacter owner, int cx, int cy, SeedKind seed = SeedKind.Spitter)
        => new()
        {
            Seed = seed, Owner = owner.Id, Faction = owner.Faction, Team = owner.Team,
            OwnerPos = owner.Body.Position, OwnerBounds = owner.Body.Bounds, CellX = cx, CellY = cy,
        };
}
