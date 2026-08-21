using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Repro for "the player gets stuck inside UNBROKEN tiles".
//
// This is distinct from the drill in DiveDrillTests: there the player ends up deep
// underground inside a shaft it legitimately carved. Here it ends up inside terrain that
// was never damaged at all — one row BELOW the bottom of its own hole.
//
// CAUSE — the residual-displacement return in PhysicsWorld.ResolveChunkCollisionsSwept.
// The sweep loop is bounded at `const int maxBounces = 4`, and each break-through burns
// one bounce. A fast dive breaks a tile per bounce, so four layers in it falls out of the
// loop with displacement left over — and the method ends with
//
//     return pos + displacement;      // PhysicsWorld.cs
//
// applying that remainder with NO collision test. At dive speed the leftover is tens of
// pixels, so the body is deposited well past the face it should have stopped at, inside
// intact rock.
//
// Once there, nothing recovers it quickly:
//   * ResolveChunkCollisions' 12-iteration push-out can't escape a body enclosed on
//     several sides — it ejects out of the deepest overlap straight into the next. Its
//     own comment calls this "the wedge case" and notes the body "settles into a stable
//     embedded rest state ... with no damage and no signal that anything went wrong".
//   * The give-up path calls CrushOverlappingSprouts, which skips anything that is not a
//     Sprout. Committed tiles are deliberately spared, so a wedge in permanent terrain has
//     no escape hatch by design.
//   * The stale SurfaceContact zeroes the body's velocity, so the player crawls out at
//     ~5px/s (0.08px per frame) if it escapes at all.
//
// Misalignment is what makes it reachable. An axis-aligned dive dropped exactly on a tile
// centre almost never triggers it; a dive offset within the tile and carrying sideways
// velocity — i.e. every real dive — hits it in ~3% of attempts. In open ground the player
// eventually squeezes free after a fraction of a second; enclosed terrain has no free
// direction and it is permanent.
//
// Ablation: raising maxBounces to 64 takes the buried-frame count from 25 to 0.
//
// TUNING NOTE: as in DiveDrillTests, these read only the player's Mass out of
// configs/impact_profiles.json (shipped 5.5 vs the hardcoded default 2.5) and apply it to
// the body directly. Calling ImpactProfiles.Load would swap a process-wide static and leak
// into every class that runs afterwards — see the comment in ConfigLayoutTests.
public class BuriedInIntactTilesTests
{
    private readonly ITestOutputHelper _out;
    public BuriedInIntactTilesTests(ITestOutputHelper o) => _out = o;

    private const int W = 60;
    private const int H = 200;
    private const int GroundRow = 40;
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
        var path = Path.Combine(root, "configs", "impact_profiles.json");
        using var stream = File.OpenRead(path);
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

    private static float Penetration(ChunkMap chunks, PhysicsBody body, Vector2 pos)
    {
        float worst = 0f;
        foreach (var shape in WorldQuery.SolidShapesInRect(chunks, body.Polygon.GetBoundingBox(pos)))
        {
            var hit = Collision.Check(body.Polygon, pos, 0f, shape.Polygon, shape.Position, 0f);
            if (hit.Intersects && hit.Depth > worst) worst = hit.Depth;
        }
        return worst;
    }

    private sealed class Dive
    {
        public TileType Type;
        public float X, DropPx, Vx;
        public bool Right;
        public override string ToString()
            => $"{Type} x={X:F2} drop={DropPx:F0} vx={Vx:F0} right={Right}";
    }

    // Runs one dive; returns how many frames the body spent substantially inside solid
    // terrain, and the worst penetration reached.
    private static (int buriedFrames, float peakPen, string dump) Run(
        Dive d, ImpactDamage impact, bool wantDump)
    {
        var chunks = Ground(d.Type);
        var player = new PlayerCharacter(new Vector2(d.X, GroundTopY - d.DropPx));
        player.Body.Impact = impact;
        player.Body.Velocity = new Vector2(d.Vx, 0f);
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hb = new HitboxWorld();
        var hu = new HurtboxWorld();
        var input = new PlayerInput { Down = true, Right = d.Right, Left = !d.Right };

        int buried = 0;
        float peak = 0f;
        string dump = null;

        for (int f = 0; f < 600; f++)
        {
            ctrl.InjectInput(input);
            chunks.TickSprouts(Simulation.FixedDt);
            player.Update(ctrl, chunks, hb, hu, Simulation.FixedDt);
            PhysicsWorld.StepSwept(bodies, chunks, Simulation.FixedDt, Simulation.Gravity);

            float pen = Penetration(chunks, player.Body, player.Body.Position);
            if (pen > peak) peak = pen;
            if (pen > 8f)
            {
                buried++;
                if (wantDump && dump == null)
                    dump = $"buried at frame {f}, pos=({player.Body.Position.X:F2},{player.Body.Position.Y:F2}), " +
                           $"pen={pen:F2}\n" + Dump(chunks, player.Body, player.Body.Position);
            }
        }
        return (buried, peak, dump);
    }

    // The exact seed the fuzz surfaced. The player breaks two rows, then the leftover
    // displacement drops it into the INTACT row beneath its own hole.
    [Fact]
    public void ADiveDepositsThePlayerInsideAnUndamagedTile()
    {
        var d = new Dive { Type = TileType.Dirt, X = 484.97f, DropPx = 4219f, Vx = 219f, Right = true };
        var (buried, peak, dump) = Run(d, ShippedPlayerImpact(), wantDump: true);
        _out.WriteLine($"{d}\nburiedFrames={buried} peakPen={peak:F2} (body is 10.39px wide)\n{dump}");

        Assert.True(buried == 0,
            $"{d}: the body spent {buried} frames ({buried / 60f:F2}s) more than 8px inside solid " +
            $"terrain, peak penetration {peak:F2}px of a 10.39px-wide body.\n{dump}");
    }

    // The general invariant: a dive must never leave the body inside terrain it did not
    // break, whatever its sub-tile alignment or sideways velocity.
    [Fact]
    public void NoMisalignedDiveEndsUpInsideIntactTerrain()
    {
        var impact = ShippedPlayerImpact();
        var rng = new Random(777);
        int trials = 0, buriedTrials = 0, worstFrames = 0;
        float worstPen = 0f;
        Dive worst = null;

        foreach (TileType type in new[] { TileType.Dirt, TileType.Sand, TileType.Stone })
            for (int i = 0; i < 250; i++)
            {
                var d = new Dive
                {
                    Type = type,
                    X = 30 * Chunk.TileSize + (float)rng.NextDouble() * Chunk.TileSize,
                    DropPx = 300f + (float)rng.NextDouble() * 5000f,
                    Vx = -400f + (float)rng.NextDouble() * 800f,
                    Right = rng.NextDouble() < 0.5,
                };
                trials++;
                var (buried, peak, _) = Run(d, impact, wantDump: false);
                if (buried > 0)
                {
                    buriedTrials++;
                    if (buried > worstFrames) { worstFrames = buried; worstPen = peak; worst = d; }
                }
            }

        _out.WriteLine($"{buriedTrials}/{trials} dives buried the player in intact terrain; " +
                       $"worst {worstFrames} frames ({worstFrames / 60f:F2}s) at pen {worstPen:F2}px — {worst}");

        Assert.True(buriedTrials == 0,
            $"{buriedTrials} of {trials} misaligned dives left the player inside unbroken tiles " +
            $"(worst: {worstFrames} frames at {worstPen:F2}px penetration, {worst})");
    }

    // '#' intact & undamaged, 'd' damaged but solid, '.' broken; uppercase = body is inside it.
    private static string Dump(ChunkMap chunks, PhysicsBody body, Vector2 pos)
    {
        int cx = (int)MathF.Floor(pos.X / Chunk.TileSize);
        int cy = (int)MathF.Floor(pos.Y / Chunk.TileSize);
        var bb = body.Polygon.GetBoundingBox(pos);

        var sb = new StringBuilder();
        sb.AppendLine($"body occupies x[{bb.Left:F2},{bb.Right:F2}] y[{bb.Top:F2},{bb.Bottom:F2}] => " +
                      $"columns {(int)MathF.Floor(bb.Left / Chunk.TileSize)}..{(int)MathF.Floor(bb.Right / Chunk.TileSize)}, " +
                      $"rows {(int)MathF.Floor(bb.Top / Chunk.TileSize)}..{(int)MathF.Floor(bb.Bottom / Chunk.TileSize)}");
        sb.AppendLine("('#'=intact undamaged, 'd'=damaged, '.'=broken; 'P'/'D' = body is inside it)");
        for (int y = cy - 6; y <= cy + 3; y++)
        {
            sb.Append($"  row {y,4}: ");
            for (int x = cx - 5; x <= cx + 5; x++)
            {
                bool solid = WorldQuery.IsSolidAt(chunks, x * Chunk.TileSize + 8, y * Chunk.TileSize + 8);
                float dmg = chunks.Damage.Get(x, y);
                char ch = !solid ? '.' : (dmg > 0.0001f ? 'd' : '#');
                bool inBody = x * Chunk.TileSize + Chunk.TileSize > bb.Left && x * Chunk.TileSize < bb.Right
                           && y * Chunk.TileSize + Chunk.TileSize > bb.Top && y * Chunk.TileSize < bb.Bottom;
                sb.Append(inBody && solid ? (ch == '#' ? 'P' : 'D') : ch);
                sb.Append(' ');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
