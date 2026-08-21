using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Behaviour of the spring-network impact model (Physics/ImpactSpringField.cs): an impact
// spreads its kinetic energy through the surrounding rock, so a fast body opens a crater
// or a wide tunnel instead of punching a body-width hole.
//
// The player is 10.39px wide against 16px tiles, so the old contact-silhouette model could
// select at most two cells no matter how hard you hit — a crater was geometrically
// unreachable without decoupling "what breaks" from "what the body touches".
public class ImpactCraterTests
{
    private readonly ITestOutputHelper _out;
    public ImpactCraterTests(ITestOutputHelper o) => _out = o;

    private const int W = 60;
    private const int H = 90;
    private const int GroundRow = 30;
    private const float GroundTopY = GroundRow * Chunk.TileSize;

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "MTile.Core.csproj"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    private static ImpactDamage ShippedPlayerImpact()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        using var stream = File.OpenRead(Path.Combine(root, "configs", "impact_profiles.json"));
        var raw = JsonSerializer.Deserialize<Dictionary<string, ImpactProfile>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        Assert.NotNull(raw);
        return raw[ImpactProfiles.Player].ToImpactDamage();
    }

    private static ChunkMap Ground(TileType type)
    {
        var rows = new List<string>();
        for (int y = 0; y < H; y++)
            rows.Add(y < GroundRow ? new string('.', W) : new string('X', W));
        var chunks = SimTerrain.FromAscii(string.Join("\n", rows), 0, 0);
        foreach (var chunk in chunks)
            for (int x = 0; x < Chunk.Size; x++)
                for (int y = 0; y < Chunk.Size; y++)
                    if (chunk.Tiles[x, y].IsSolid) chunk.Tiles[x, y].Type = type;
        return chunks;
    }

    private readonly record struct Hole(int Broken, int Width, int Depth, Vector2 EndVelocity, string Art);

    // Fire a bare body straight down at `speed` (no gravity, so the only energy in play is
    // what we set) and measure the hole it leaves.
    private static Hole Slam(TileType type, float speed, ImpactDamage impact, int frames)
    {
        var chunks = Ground(type);
        var body = new PhysicsBody(
            new PlayerCharacter(Vector2.Zero).Body.Polygon,
            new Vector2(30 * Chunk.TileSize + 8, GroundTopY - 20f))
        {
            Impact = impact,
            Velocity = new Vector2(0f, speed),
        };
        var bodies = new List<PhysicsBody> { body };
        for (int f = 0; f < frames; f++)
            PhysicsWorld.StepSwept(bodies, chunks, Simulation.FixedDt, Vector2.Zero);

        var sb = new StringBuilder();
        int broken = 0, minCol = int.MaxValue, maxCol = int.MinValue, maxRow = int.MinValue;
        for (int y = GroundRow; y <= GroundRow + 9; y++)
        {
            sb.Append("      ");
            for (int dx = -9; dx <= 9; dx++)
            {
                int gx = 30 + dx;
                bool solid = chunks.GetCellState(gx, y) == TileState.Solid;
                if (!solid)
                {
                    broken++;
                    if (gx < minCol) minCol = gx;
                    if (gx > maxCol) maxCol = gx;
                    if (y > maxRow) maxRow = y;
                }
                sb.Append(!solid ? '.' : chunks.Damage.Get(gx, y) > 0.001f ? '+' : '#');
            }
            sb.AppendLine();
        }
        return new Hole(
            broken,
            broken == 0 ? 0 : maxCol - minCol + 1,
            broken == 0 ? 0 : maxRow - GroundRow + 1,
            body.Velocity,
            sb.ToString());
    }

    // The headline: a hard landing must open something wider than the body that made it.
    // The body spans 10.39px — under one 16px tile — so anything driven by the contact
    // silhouette alone tops out at 2 cells wide.
    [Fact]
    public void AHardImpactOpensACraterWiderThanTheBody()
    {
        var impact = ShippedPlayerImpact();
        var hole = Slam(TileType.Dirt, 2400f, impact, 1);
        _out.WriteLine($"broken={hole.Broken} width={hole.Width} depth={hole.Depth}\n{hole.Art}");

        Assert.True(hole.Width >= 7,
            $"a 2400px/s impact opened a hole only {hole.Width} tile(s) wide\n{hole.Art}");
        Assert.True(hole.Broken >= 24,
            $"a 2400px/s impact broke only {hole.Broken} cells\n{hole.Art}");
    }

    // Crater size has to answer to energy, not just to whether the threshold was crossed.
    [Fact]
    public void CraterGrowsWithImpactEnergy()
    {
        var impact = ShippedPlayerImpact();
        var log = new StringBuilder();
        var counts = new List<int>();
        foreach (float v in new[] { 600f, 1200f, 2400f })
        {
            var hole = Slam(TileType.Dirt, v, impact, 1);
            counts.Add(hole.Broken);
            log.AppendLine($"  v={v,5:F0} broken={hole.Broken,3} {hole.Width}wide x {hole.Depth}deep " +
                           $"vEnd={hole.EndVelocity.Y:F0}");
        }
        _out.WriteLine(log.ToString());

        Assert.True(counts[1] > counts[0] && counts[2] > counts[1],
            $"crater did not grow monotonically with energy: {string.Join(" -> ", counts)}\n{log}");
    }

    // Sustained high-speed travel through terrain should bore a wide tunnel, not a
    // one-tile chimney. This is the shape the old model could not produce at any tuning.
    [Fact]
    public void SustainedTravelBoresAWideTunnel()
    {
        var impact = ShippedPlayerImpact();
        var hole = Slam(TileType.Dirt, 2400f, impact, 8);
        _out.WriteLine($"broken={hole.Broken} width={hole.Width} depth={hole.Depth}\n{hole.Art}");

        Assert.True(hole.Width >= 3,
            $"eight frames at 2400px/s bored a shaft only {hole.Width} tile(s) wide\n{hole.Art}");
    }

    // Coupling across the impact is stiffer than coupling along it (LateralBias), so a
    // hard hit should open out rather than burrow. Without that the same energy makes a
    // narrow pit, which reads as a puncture rather than a crater.
    [Fact]
    public void AHardImpactOpensOutRatherThanBurrowing()
    {
        var impact = ShippedPlayerImpact();
        var hole = Slam(TileType.Dirt, 2400f, impact, 1);
        _out.WriteLine($"{hole.Width} wide x {hole.Depth} deep\n{hole.Art}");

        Assert.True(hole.Width > hole.Depth,
            $"a 2400px/s impact bored {hole.Depth} deep but only {hole.Width} wide\n{hole.Art}");
    }

    // Softer rock must give way more readily than hard rock for the same impact — the
    // per-material strengths in configs/material_strengths.json have to reach the crater.
    [Fact]
    public void SofterMaterialCratersMoreEasily()
    {
        var impact = ShippedPlayerImpact();
        var sand = Slam(TileType.Sand, 1200f, impact, 1);
        var stone = Slam(TileType.Stone, 1200f, impact, 1);
        _out.WriteLine($"Sand(MaxHP {TileDamage.MaxHPFor(TileType.Sand)}) broken={sand.Broken}  " +
                       $"Stone(MaxHP {TileDamage.MaxHPFor(TileType.Stone)}) broken={stone.Broken}");

        Assert.True(sand.Broken > stone.Broken,
            $"Sand broke {sand.Broken} cells and Stone {stone.Broken} — material strength is not reaching the crater");
    }

    // A gentle landing must not chew up the floor. The crater is for real impacts.
    [Fact]
    public void AGentleLandingBarelyMarksTheGround()
    {
        var impact = ShippedPlayerImpact();
        var hole = Slam(TileType.Stone, 300f, impact, 4);
        _out.WriteLine($"broken={hole.Broken} width={hole.Width} depth={hole.Depth}\n{hole.Art}");

        Assert.True(hole.Broken == 0,
            $"a 300px/s landing on Stone broke {hole.Broken} cells\n{hole.Art}");
    }
}
