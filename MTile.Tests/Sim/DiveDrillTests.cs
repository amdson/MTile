using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Repro for "hold Down, fall fast, end up buried deep in the terrain".
//
// STATUS. The one-tile chimney this originally pinned is gone: impacts now spread their
// energy through a spring network (Physics/ImpactSpringField.cs), so a dive opens a
// crater several tiles across and the shaft it bores is correspondingly wider. Depth is
// much reduced too — a 5000px drop into Dirt used to bury the player ~45 tiles and now
// buries ~15.
//
// WHAT IS STILL WRONG. Deep enough dives still sink further than they should, and the
// remaining cause is not the impact model at all: holding Down adds FastFallForce (1000)
// on top of gravity (600) in LocomotionStates.cs:30, and AirDrag is applied only
// horizontally (AirControl.Apply), so downward speed has NO terminal clamp. A 5000px drop
// arrives at ~3600px/s, and kinetic energy goes as v², so the dive carries far more than
// the crater around it can absorb — the neighbourhood saturates and the surplus can only
// become forward motion. Capping fast-fall speed is a movement-tuning change rather than
// a physics one, so it is deliberately left alone here.
//
// TUNING NOTE: the rest of the suite runs on hardcoded defaults, because ConfigLayoutTests
// deliberately never calls the real loaders (they swap process-wide statics, and this
// assembly runs un-parallelised for exactly that reason). For this bug the gap matters:
// the default player Mass is 2.5 but configs/impact_profiles.json ships 5.5, and impact
// energy is ½·Mass·v², so the real player hits over twice as hard as the defaults imply.
// These tests therefore read only the player's Mass out of the shipped config and apply it
// to the body directly, rather than calling Load and leaking the change into every class
// that runs afterwards.
public class DiveDrillTests
{
    private readonly ITestOutputHelper _out;
    public DiveDrillTests(ITestOutputHelper o) => _out = o;

    private const int W = 40;
    private const int H = 400;             // deep enough that the body never reaches the bottom
    private const int GroundRow = 60;
    private const float GroundTopY = GroundRow * Chunk.TileSize;
    private const float RestY = GroundTopY - 12f;   // body centre resting on an undamaged surface

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

    // The shipped player ImpactDamage, read straight out of configs/impact_profiles.json
    // WITHOUT touching ImpactProfiles' process-wide table.
    private static ImpactDamage ShippedPlayerImpact()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root, "configs", "impact_profiles.json");
        Assert.True(File.Exists(path), $"missing {path}");

        using var stream = File.OpenRead(path);
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var raw = JsonSerializer.Deserialize<Dictionary<string, ImpactProfile>>(stream, opts);
        Assert.NotNull(raw);
        Assert.True(raw.TryGetValue(ImpactProfiles.Player, out var profile), "no 'player' profile");
        return profile.ToImpactDamage();
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

    // Drop from `dropPx` holding Down; returns how far below the original surface the
    // body ends up, the impact speed, and the width of the hole it left.
    private static (float drillPx, float impactVy, int shaftWidth) Dive(TileType type, int dropPx, ImpactDamage impact)
    {
        var chunks = Ground(type);
        var player = new PlayerCharacter(new Vector2(20 * Chunk.TileSize + 8, GroundTopY - dropPx));
        player.Body.Impact = impact;
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hb = new HitboxWorld();
        var hu = new HurtboxWorld();

        var down = new PlayerInput { Down = true };
        float impactVy = 0f; bool landed = false;
        for (int f = 0; f < 1500; f++)
        {
            if (!landed && player.Body.Position.Y + 12f >= GroundTopY)
            { impactVy = player.Body.Velocity.Y; landed = true; }
            ctrl.InjectInput(down);
            chunks.TickSprouts(Simulation.FixedDt);
            player.Update(ctrl, chunks, hb, hu, Simulation.FixedDt);
            PhysicsWorld.StepSwept(bodies, chunks, Simulation.FixedDt, Simulation.Gravity);
        }

        // Widest point of the shaft, not the width at the body's final row. The last row
        // is the freshly-punched tip, which is always about one body wide whatever the
        // rest of the tunnel looks like — measuring there reports "1 tile" even for a
        // shaft that is plainly a crater further up.
        int bottom = (int)MathF.Floor(player.Body.Position.Y / Chunk.TileSize);
        int shaft = 0;
        for (int ty = GroundRow; ty <= bottom; ty++)
        {
            int run = 0;
            for (int tx = 0; tx < W; tx++)
                if (!WorldQuery.IsSolidAt(chunks, tx * Chunk.TileSize + 8, ty * Chunk.TileSize + 8)) run++;
            if (run > shaft) shaft = run;
        }

        return (player.Body.Position.Y - RestY, impactVy, shaft);
    }

    // Holding Down through a fall must not bury the player many tiles under the surface.
    // Still fails on the tallest drops — see the note on fast-fall's missing terminal
    // velocity at the top of this file.
    [Theory]
    [InlineData(TileType.Dirt)]
    [InlineData(TileType.Sand)]
    public void DivingDoesNotBuryThePlayer(TileType type)
    {
        var impact = ShippedPlayerImpact();

        var log = new StringBuilder();
        log.AppendLine($"{type} MaxHP={TileDamage.MaxHPFor(type)}  shippedPlayerMass={impact.Mass}  " +
                       $"FastFallForce={MovementConfig.Current.FastFallForce}");
        log.AppendLine("dropPx | impactVy | drillPx | drillTiles | shaftWidth");

        float worst = 0f; int worstDrop = 0;
        foreach (int dropPx in new[] { 300, 600, 1200, 2500, 5000 })
        {
            var (drill, vy, shaft) = Dive(type, dropPx, impact);
            log.AppendLine($"{dropPx,6} | {vy,8:F0} | {drill,7:F1} | {drill / Chunk.TileSize,10:F1} | {shaft,10}");
            if (drill > worst) { worst = drill; worstDrop = dropPx; }
        }
        _out.WriteLine(log.ToString());

        // Punching a shallow crater is the intended "terrain is the weapon" behaviour.
        // Sinking more than a body-height below the surface is not.
        Assert.True(worst <= 2f * Chunk.TileSize,
            $"{type}: a {worstDrop}px dive buried the player {worst:F0}px " +
            $"({worst / Chunk.TileSize:F1} tiles) below the surface\n{log}");
    }

    // The shaft a dive carves must be wider than the body that made it. It used to be
    // exactly one tile: the damaged set came from the contact silhouette, and a 10.39px
    // body inset by 2px a side lands on a single 16px column however hard it hits.
    [Fact]
    public void DiveCarvesAOneTileWideChimney()
    {
        var (drill, vy, shaft) = Dive(TileType.Sand, 5000, ShippedPlayerImpact());
        _out.WriteLine($"impactVy={vy:F0} drill={drill:F0}px ({drill / Chunk.TileSize:F1} tiles) shaftWidth={shaft} tile(s)");

        Assert.True(drill < 3f * Chunk.TileSize || shaft > 1,
            $"dive drilled {drill / Chunk.TileSize:F1} tiles down a shaft only {shaft} tile wide — " +
            "the player ends up inside a chimney rather than a crater");
    }

    // Pins the tuning gap that hides this bug from the rest of the suite. Passes today;
    // it is a measurement, and it documents why every other test understates the effect.
    [Fact]
    public void SuiteDefaultsUnderstateTheShippedPlayerMass()
    {
        float defaultMass = ImpactProfiles.Build(ImpactProfiles.Player).Mass;   // no Load called
        float shippedMass = ShippedPlayerImpact().Mass;

        _out.WriteLine($"ImpactProfiles player Mass — hardcoded default {defaultMass}, shipped {shippedMass}. " +
                       $"capDvMag scales as 1/Mass, so the shipped player breaks through tiles at " +
                       $"{defaultMass / shippedMass:P0} of the speed the defaults imply.");

        Assert.True(shippedMass >= defaultMass, "shipped mass unexpectedly lighter than the default");
    }
}
