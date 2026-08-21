using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Repro for "a fast body phases through the tile surface and gets stuck inside".
//
// CAUSE — PhysicsWorld.ResolveChunkCollisionsSwept (Physics/PhysicsWorld.cs:265-440).
// The sweep walks at most `maxBounces = 4` iterations. Every exit path funnels into
// the closing `return pos + displacement`, which applies whatever displacement is
// left with NO collision test. On the `!anyHit` exit that is correct — the path
// really is clear. On budget exhaustion it is not: the body still has unconsumed
// displacement aimed into terrain, and it teleports there.
//
// The budget is exhausted by the break-through path in the same method. When inbound
// normal velocity exceeds what the impacted cells can absorb (`dvCapMag`), the cells
// break and the body keeps the surplus, so it plows on and consumes one bounce per
// tile layer. Four layers in a single 1/60 step and the loop is spent.
//
// The landing site is intact terrain, and nothing recovers from it:
//   * the discrete depenetration pre-pass at the top of the next StepSwept ejects
//     from the single deepest overlap, which for a fully enclosed body just moves it
//     into a neighbour — all 12 iterations cycle without escaping;
//   * CrushOverlappingSprouts, the loop's give-up hatch, deliberately spares
//     committed tiles, so it does nothing here;
//   * the stale SurfaceContact zeroes the normal velocity every frame.
// The body is frozen inside the terrain permanently (velocity exactly zero, overlap
// equal to its own width). Verified by ablation: returning `pos` instead of
// `pos + displacement` on the exhausted exit removes the embedding at every speed.
//
// REACHABILITY — the threshold in this flat-slab harness, measured by
// EmbedThresholdIsAboveNormalPlaySpeeds below, is ~14800 px/s into Stone and ~7900
// into Sand/Foam. A survey of the codebase puts the player's realistic ceiling near
// 2000-3200 px/s (knockback at 100-200% DamagePercent) and the fastest body in the
// game, RailBoltProjectile, at 1500 px/s. So this defect is real and deterministic
// but the flat-wall trigger sits above normal play; a ~20k-trial fuzz of a real
// PlayerCharacter at 300-3200 px/s over walls, corners, slots, pillars, stairs and
// bumpy corridors produced no embedding at all.
public class HighSpeedTunnelingTests
{
    private readonly ITestOutputHelper _out;
    public HighSpeedTunnelingTests(ITestOutputHelper o) => _out = o;

    private const float WallFaceX = 160f;   // 10 empty columns, then solid

    private static ChunkMap Slab(TileType type = TileType.Stone)
    {
        var rows = new List<string>();
        for (int r = 0; r < 30; r++)
            rows.Add(new string('.', 10) + new string('X', 30));
        var chunks = SimTerrain.FromAscii(string.Join("\n", rows), 0, 0);
        foreach (var chunk in chunks)
            for (int x = 0; x < Chunk.Size; x++)
                for (int y = 0; y < Chunk.Size; y++)
                    if (chunk.Tiles[x, y].IsSolid) chunk.Tiles[x, y].Type = type;
        return chunks;
    }

    private static PhysicsBody PlayerBody(Vector2 pos) =>
        new(new PlayerCharacter(Vector2.Zero).Body.Polygon, pos)
        {
            Impact = ImpactProfiles.Build(ImpactProfiles.Player),
        };

    // Deepest overlap between the body at `pos` and any solid shape. A value near the
    // body's own width (10.39px) means the body is entirely inside solid tiles.
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

    // A player-shaped body flung into a thick wall must come to rest in the tunnel it
    // carved, never embedded in intact terrain.
    [Theory]
    [InlineData(16000f)]
    [InlineData(20000f)]
    [InlineData(24000f)]
    public void FastBodyNeverEndsInsideSolidTerrain(float speed)
    {
        var chunks = Slab();
        var body = PlayerBody(new Vector2(120f, 200f));
        body.Velocity = new Vector2(speed, 0f);
        var bodies = new List<PhysicsBody> { body };

        var log = new StringBuilder();
        for (int f = 0; f < 60; f++)
        {
            PhysicsWorld.StepSwept(bodies, chunks, Simulation.FixedDt, Vector2.Zero);
            if (f < 3)
                log.AppendLine($"  f{f} pos={body.Position} v={body.Velocity} pen={Penetration(chunks, body, body.Position):F2}");
        }
        _out.WriteLine(log.ToString());

        float pen = Penetration(chunks, body, body.Position);
        float depthPastFace = body.Position.X - WallFaceX;
        _out.WriteLine($"speed={speed} finalPos={body.Position} v={body.Velocity} pen={pen:F2} depthPastFace={depthPastFace:F1}");

        Assert.True(pen < 1f, $"body ended embedded {pen:F2}px inside solid terrain at {body.Position}");

        // Depth past the face is deliberately NOT asserted. At these speeds the body
        // legitimately spends its whole bounce budget breaking tiles and comes to rest
        // inside the tunnel that carving opened up — which is the intended "terrain is
        // the weapon" outcome, not entrapment. The defect was always ending that travel
        // inside INTACT rock, and `pen` above is what pins it. Kept as a measurement so a
        // future change to maxBounces or the impact cap shows up here.
        _out.WriteLine($"  carved {depthPastFace:F1}px ({depthPastFace / Chunk.TileSize:F1} tiles) past the face");
    }

    // The invariant the solver actually violates: one step must never move a body from
    // free space to a position overlapping solid terrain.
    [Fact]
    public void SingleStepNeverLandsInsideSolid()
    {
        var offenders = new StringBuilder();

        for (int speed = 2000; speed <= 24000; speed += 500)
        {
            var chunks = Slab();
            var body = PlayerBody(new Vector2(120f, 200f));
            body.Velocity = new Vector2(speed, 0f);
            var bodies = new List<PhysicsBody> { body };

            for (int f = 0; f < 10; f++)
            {
                PhysicsWorld.StepSwept(bodies, chunks, Simulation.FixedDt, Vector2.Zero);
                float pen = Penetration(chunks, body, body.Position);
                if (pen > 1f)
                {
                    offenders.AppendLine($"  speed {speed}: frame {f} ended {pen:F2}px inside solid at {body.Position}");
                    break;
                }
            }
        }

        _out.WriteLine(offenders.Length == 0 ? "(none)" : offenders.ToString());
        Assert.True(offenders.Length == 0, "steps that landed inside solid terrain:\n" + offenders);
    }

    // Documents how far above normal play the trigger sits, and how material softness
    // lowers it (softer cells absorb less, so each break-through bounce is cheaper and
    // the body arrives at the budget limit still carrying displacement).
    // Passes today — it is a measurement, not an assertion about correctness.
    [Fact]
    public void EmbedThresholdIsAboveNormalPlaySpeeds()
    {
        var report = new StringBuilder();
        report.AppendLine("material | MaxHP | lowest speed whose step ends inside solid tiles");

        foreach (TileType type in new[] { TileType.Stone, TileType.Dirt, TileType.Sand, TileType.Foam })
        {
            int firstEmbed = -1;
            for (int speed = 500; speed <= 24000 && firstEmbed < 0; speed += 100)
            {
                var chunks = Slab(type);
                var body = PlayerBody(new Vector2(120f, 200f));
                body.Velocity = new Vector2(speed, 0f);
                var bodies = new List<PhysicsBody> { body };

                for (int f = 0; f < 10; f++)
                {
                    PhysicsWorld.StepSwept(bodies, chunks, Simulation.FixedDt, Vector2.Zero);
                    if (Penetration(chunks, body, body.Position) > 1f) { firstEmbed = speed; break; }
                }
            }
            report.AppendLine($"{type,-8} | {TileDamage.MaxHPFor(type),-5} | {(firstEmbed < 0 ? "never" : firstEmbed + " px/s")}");
            Assert.True(firstEmbed < 0 || firstEmbed > 3200,
                $"{type}: embedding now starts at {firstEmbed} px/s, inside the player's reachable range — " +
                "the tunneling bug is no longer just theoretical, fix ResolveChunkCollisionsSwept's residual displacement");
        }

        _out.WriteLine(report.ToString());
    }
}
