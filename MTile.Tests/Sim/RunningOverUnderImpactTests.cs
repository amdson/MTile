using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Coverage for the running-over / running-under-blocks impact-damage spec.
//
// Spec:
//   Running OVER a block (parkour-vault):
//     - Stone, Dirt: must NOT break.
//     - Sand: unspecified by the user — recorded but not asserted.
//   Running UNDER a block (parkour-duck):
//     - Stone, Dirt: must NOT break.
//     - Sand: may break (allowed by the user).
//
// SteeringRamps redirect velocity around the corner; they don't apply forces
// directly. But the position-resolution still resolves the polygon vs the
// real tile in the swept solver, so the body's relative-normal velocity into
// the corner face becomes an impulse — and the impact-damage path can fire.
//
// These tests log per-frame velocity / applied-force / SteeringRamp.LastImpulse
// + SurfaceContact.LastImpulse so we can SEE where the impulses come from and
// how big they are during a vault/duck. The assertions document the spec; the
// logs document the investigation the user asked for.
public class RunningOverUnderImpactTests
{
    private readonly ITestOutputHelper _out;
    public RunningOverUnderImpactTests(ITestOutputHelper o) => _out = o;

    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    // Vault terrain: 4 rows, 20 cols. Floor row 3. Single-block vault platform
    // at row 2 cols 8..15. Player starts at the left on the floor and walks
    // right — exactly the SimulationTests.HoldRight_VaultOneBlock layout, but
    // with caller-supplied tile types so we can vary the material.
    private static ChunkMap VaultTerrain(TileType vaultType, TileType floorType)
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        for (int gtx = 0; gtx < 20; gtx++)
        for (int gty = 0; gty < 4; gty++)
        {
            int cx = gtx >> 4, cy = gty >> 4;
            int lx = gtx - cx * 16, ly = gty - cy * 16;
            if (!terrain.TryGet(new Point(cx, cy), out var chunk)) continue;
            if (chunk.Tiles[lx, ly].State != TileState.Solid) continue;
            chunk.Tiles[lx, ly].Type = gty == 2 ? vaultType : floorType;
        }
        return terrain;
    }

    // Duck-under terrain: 5 rows, 20 cols. Floor row 4. Full ceiling row 1.
    // Single block protruding down at row 2 col 8 — exactly the body's
    // top-clearance line, so the player must use the under-ramp to thread past.
    // Player walks left to right under the corridor.
    private static ChunkMap DuckTerrain(TileType protrusionType, TileType wallType)
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX
            OOOOOOOOXOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        for (int gtx = 0; gtx < 20; gtx++)
        for (int gty = 0; gty < 5; gty++)
        {
            int cx = gtx >> 4, cy = gty >> 4;
            int lx = gtx - cx * 16, ly = gty - cy * 16;
            if (!terrain.TryGet(new Point(cx, cy), out var chunk)) continue;
            if (chunk.Tiles[lx, ly].State != TileState.Solid) continue;
            // Row 2 col 8 protrusion (the corner the body has to thread) gets
            // the test material; everything else is the (irrelevant) wallType.
            chunk.Tiles[lx, ly].Type = (gty == 2 && gtx == 8) ? protrusionType : wallType;
        }
        return terrain;
    }

    private record BrokeEvent(int Frame, int Gtx, int Gty, TileType Type);

    private record TraversalResult(
        List<BrokeEvent> Broken,
        bool VisitedParkour,
        float MaxImpulseMagnitude,
        float MaxAppliedFy,
        float FinalX);

    // Run the player rightward through `terrain`, log per-frame state, and
    // capture broken-cell events + the largest impulse magnitude seen on any
    // body contact.
    private TraversalResult RunRightward(ChunkMap terrain, Vector2 startPos, int frames, string label)
    {
        var player = new PlayerCharacter(startPos);
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();

        var broken = new List<BrokeEvent>();
        int curFrame = -1;
        terrain.OnTileBroken = (wc, type) =>
        {
            int gtx = (int)MathF.Floor(wc.X / 16f);
            int gty = (int)MathF.Floor(wc.Y / 16f);
            broken.Add(new BrokeEvent(curFrame, gtx, gty, type));
        };

        float maxImpulseMag = 0f;
        float maxFy = 0f;
        bool visitedParkour = false;

        _out.WriteLine($"--- {label} ---");
        _out.WriteLine("  f  state                 x        y       vx      vy       fy        contacts (LastImpulse)");
        for (int f = 0; f < frames; f++)
        {
            curFrame = f;
            ctrl.InjectInput(new PlayerInput { Right = true });
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);
            player.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);

            float fyApplied = player.Body.AppliedForce.Y;
            if (MathF.Abs(fyApplied) > MathF.Abs(maxFy)) maxFy = fyApplied;

            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

            float bodyImpulse = player.Body.LastImpulseMagnitude;
            if (bodyImpulse > maxImpulseMag) maxImpulseMag = bodyImpulse;

            string state = player.CurrentStateName;
            if (state.Contains("Parkour")) visitedParkour = true;

            // Sparse trace: anything noteworthy — parkour, big impulse, broken cell.
            bool brokeThisFrame = broken.Count > 0 && broken[^1].Frame == f;
            bool interesting = state.Contains("Parkour") || bodyImpulse > 30f || brokeThisFrame;
            if (interesting)
            {
                var contactsLog = string.Join(", ", player.Body.Constraints
                    .Select(c => $"{c.GetType().Name}={c.LastImpulse.Length():F1}"));
                _out.WriteLine($"  {f,3}  {state,-20}  {player.Body.Position.X,7:F2}  {player.Body.Position.Y,6:F2}  " +
                               $"{player.Body.Velocity.X,6:F2}  {player.Body.Velocity.Y,6:F2}  {fyApplied,7:F2}  " +
                               $"|imp|={bodyImpulse:F1}  [{contactsLog}]");
            }
        }
        _out.WriteLine($"  → maxImpulseMagnitude={maxImpulseMag:F2}, maxAppliedFy={maxFy:F2}, " +
                       $"visitedParkour={visitedParkour}, brokenCount={broken.Count}");
        foreach (var b in broken)
            _out.WriteLine($"     broke f={b.Frame} ({b.Gtx},{b.Gty}) {b.Type}");

        return new TraversalResult(broken, visitedParkour, maxImpulseMag, maxFy, player.Body.Position.X);
    }

    // ── Running OVER a single-block step ──

    [Fact]
    public void RunningOver_StoneStep_DoesNotBreakAnyStone()
    {
        var terrain = VaultTerrain(vaultType: TileType.Stone, floorType: TileType.Stone);
        var r = RunRightward(terrain, new Vector2(12f, 36f), 120, "running over stone step");

        Assert.True(r.VisitedParkour, "expected ParkourState during vault");
        Assert.Empty(r.Broken);
    }

    [Fact]
    public void RunningOver_DirtStep_DoesNotBreakAnyDirt()
    {
        var terrain = VaultTerrain(vaultType: TileType.Dirt, floorType: TileType.Dirt);
        var r = RunRightward(terrain, new Vector2(12f, 36f), 120, "running over dirt step");

        Assert.True(r.VisitedParkour, "expected ParkourState during vault");
        Assert.Empty(r.Broken);
    }

    // Sand vault: investigation only. Sand may break (the user didn't list this
    // case in the spec); the test records what happens and never asserts.
    [Fact]
    public void RunningOver_SandStep_InvestigationOnly()
    {
        var terrain = VaultTerrain(vaultType: TileType.Sand, floorType: TileType.Sand);
        var r = RunRightward(terrain, new Vector2(12f, 36f), 120, "running over sand step");

        Assert.True(r.VisitedParkour, "expected ParkourState during vault (fixture sanity)");
        _out.WriteLine($"Sand vault: {r.Broken.Count} cells broke. (informational; no assert)");
    }

    // ── Running UNDER a single-block protrusion ──

    [Fact]
    public void RunningUnder_StoneProtrusion_DoesNotBreakStone()
    {
        var terrain = DuckTerrain(protrusionType: TileType.Stone, wallType: TileType.Stone);
        // Floor at row 4 (top y=64); player center y = 64 - 12 = 52.
        var r = RunRightward(terrain, new Vector2(12f, 52f), 150, "running under stone protrusion");

        // Don't require Parkour here — the layout might or might not trigger
        // it depending on clearance. The spec is about damage either way.
        Assert.Empty(r.Broken);
    }

    [Fact]
    public void RunningUnder_DirtProtrusion_DoesNotBreakDirt()
    {
        var terrain = DuckTerrain(protrusionType: TileType.Dirt, wallType: TileType.Dirt);
        var r = RunRightward(terrain, new Vector2(12f, 52f), 150, "running under dirt protrusion");
        Assert.Empty(r.Broken);
    }

    // Sand-under: the user said this case is ALLOWED to break.
    [Fact]
    public void RunningUnder_SandProtrusion_InvestigationOnly()
    {
        var terrain = DuckTerrain(protrusionType: TileType.Sand, wallType: TileType.Sand);
        var r = RunRightward(terrain, new Vector2(12f, 52f), 150, "running under sand protrusion");
        _out.WriteLine($"Sand under: {r.Broken.Count} cells broke. (informational; no assert)");
    }
}
