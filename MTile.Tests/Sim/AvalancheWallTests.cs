using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// todo #4 — walls block avalanches. A MassBall flies through cells its OWN wave requested
// (AvalancheWaves tags them) and stops on entering any other occupied cell, dumping what's
// left on the near side; its spill is refused by foreign solid so nothing leaks through.
// Plus #2's wall pin: a non-rider wedged between the wave and a wall takes real crush.
public class AvalancheWallTests(ITestOutputHelper output)
{
    private static float Ts => Chunk.TileSize;
    private const int Cols = 50, Rows = 20, FloorRow = 12;
    private static readonly float RestOffset = PlayerCharacter.Radius * (1f + MathF.Sin(MathF.PI / 3f));

    // Floor from FloorRow down, side walls at the map edges, and an optional full-height
    // wall over columns [wallFrom, wallTo).
    private static ChunkMap Arena(int wallFrom = -1, int wallTo = -1)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
                sb.Append(y >= FloorRow || x < 2 || x >= Cols - 1 || (x >= wallFrom && x < wallTo) ? 'X' : 'O');
            sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    private static Vector2 Standing(float col) => new(col * Ts + Ts * 0.5f, FloorRow * Ts - RestOffset);
    private static Vector2 CellCenter(int gtx, int gty) => new(gtx * Ts + Ts * 0.5f, gty * Ts + Ts * 0.5f);

    private static Simulation NewSim(ChunkMap terrain, Vector2 p1, Vector2 p2)
    {
        var sim = new Simulation(terrain, p1);
        sim.AddSecondaryPlayer(p2);
        return sim;
    }

    private static MassBall Launch(Simulation sim, Vector2 pos, Vector2 vel, float mass = 240f)
    {
        var ball = new MassBall(pos, vel, mass, TileType.Dirt, Faction.Player2);
        sim.SpawnEntity(ball);
        return ball;
    }

    // Every occupied (Solid or Sprouting) Dirt cell — the wave's material; the arena is Stone.
    private static List<(int x, int y)> DirtCells(ChunkMap t)
    {
        var cells = new List<(int, int)>();
        for (int y = 0; y < Rows; y++)
        for (int x = 0; x < Cols; x++)
            if (t.GetCellState(x, y) != TileState.Empty && t.GetCellType(x, y) == TileType.Dirt)
                cells.Add((x, y));
        return cells;
    }

    private static void Step(Simulation sim, int frames)
    {
        for (int f = 0; f < frames; f++) sim.Step(default, default);
    }

    [Fact]
    public void WaveStopsAtWall_NothingBeyondOrInside()
    {
        var sim = NewSim(Arena(22, 24), Standing(4), Standing(6));
        var ball = Launch(sim, CellCenter(12, FloorRow - 2), new Vector2(320f, 0f));
        Step(sim, 240);

        var dirt = DirtCells(sim.Chunks);
        output.WriteLine($"dirt cells {dirt.Count}, max x {dirt.Max(c => c.x)}; stopped {ball.StoppedAtWall}");
        Assert.True(ball.StoppedAtWall, "the ball should have stopped at the wall face");
        Assert.True(dirt.Count > 10, $"the wave should still build ({dirt.Count} cells)");
        Assert.DoesNotContain(dirt, c => c.x >= 22);
        for (int y = 0; y < FloorRow; y++)
        for (int x = 22; x < 24; x++)
        {
            Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(x, y));
            Assert.Equal(TileType.Stone, sim.Chunks.GetCellType(x, y));
        }
        // The dump banks up against the face.
        Assert.Contains(dirt, c => c.x == 21);
    }

    // Control: with no wall the same launch carries well past where the wall stood — the
    // stop above is the wall's doing, not the ball running out.
    [Fact]
    public void NoWall_SameLaunchFliesPastTheWallLine()
    {
        var sim = NewSim(Arena(), Standing(4), Standing(6));
        var ball = Launch(sim, CellCenter(12, FloorRow - 2), new Vector2(320f, 0f));
        Step(sim, 240);
        var dirt = DirtCells(sim.Chunks);
        output.WriteLine($"dirt cells {dirt.Count}, max x {dirt.Max(c => c.x)}");
        Assert.False(ball.StoppedAtWall);
        Assert.Contains(dirt, c => c.x >= 24);
    }

    // A one-tile barricade built by the LAUNCHER (P2's own earlier build, untagged) stops
    // its wave too, and the spill can't leak through a wall that thin.
    [Fact]
    public void OwnBarricade_StopsTheWave_NoLeakThroughOneThickWall()
    {
        var terrain = Arena();
        byte p2 = BlockOwnership.OwnerFor(Faction.Player2);
        var sim = NewSim(terrain, Standing(4), Standing(6));
        // Barricade column at x = 20, grown bottom-up from the floor.
        for (int i = 0; i < 200 && terrain.GetCellState(20, 0) != TileState.Solid; i++)
        {
            for (int y = FloorRow - 1; y >= 0; y--) terrain.TryRequestTile(20, y, TileType.Stone, owner: p2);
            sim.Step(default, default);
        }
        for (int y = 0; y < FloorRow; y++) Assert.Equal(TileState.Solid, terrain.GetCellState(20, y));

        var ball = Launch(sim, CellCenter(12, FloorRow - 2), new Vector2(320f, 0f));
        Step(sim, 240);
        var dirt = DirtCells(terrain);
        output.WriteLine($"dirt cells {dirt.Count}, max x {dirt.Max(c => c.x)}; stopped {ball.StoppedAtWall}");
        Assert.True(ball.StoppedAtWall);
        Assert.DoesNotContain(dirt, c => c.x >= 20);
    }

    // A barricade still sprouting when the wave arrives counts as well.
    [Fact]
    public void GrowingBarricade_StopsTheWave()
    {
        var terrain = Arena();
        var sim = NewSim(terrain, Standing(4), Standing(6));
        var ball = Launch(sim, CellCenter(12, FloorRow - 2), new Vector2(320f, 0f));
        bool stopped = false;
        for (int f = 0; f < 240; f++)
        {
            // Keep re-requesting so the column is always sprouting/solid ahead of the ball.
            for (int y = FloorRow - 1; y >= 0; y--) terrain.TryRequestTile(20, y, TileType.Stone);
            sim.Step(default, default);
            stopped |= ball.StoppedAtWall;
        }
        var dirt = DirtCells(terrain);
        output.WriteLine($"dirt cells {dirt.Count}, max x {(dirt.Count > 0 ? dirt.Max(c => c.x) : -1)}");
        Assert.True(stopped);
        Assert.DoesNotContain(dirt, c => c.x >= 20 && c.y >= FloorRow - 3);
    }

    // The ball must not stop on its own deposits: a wave across open floor runs its whole
    // course, building a long ribbon. Launched from INSIDE the floor (the lagging paint ball
    // at release) rising out of it — the launch solid doesn't count either.
    [Fact]
    public void OwnDeposits_AndLaunchSolid_DoNotStopIt()
    {
        var sim = NewSim(Arena(), Standing(4), Standing(6));
        var ball = Launch(sim, CellCenter(12, FloorRow), new Vector2(300f, -80f));
        bool everStopped = false;
        int lastAlive = 0;
        for (int f = 0; f < 240; f++)
        {
            sim.Step(default, default);
            everStopped |= ball.StoppedAtWall;
            if (!ball.IsDead) lastAlive = f;
        }
        var dirt = DirtCells(sim.Chunks);
        int span = dirt.Max(c => c.x) - dirt.Min(c => c.x);
        output.WriteLine($"dirt {dirt.Count} span {span} alive until {lastAlive}");
        Assert.False(everStopped);
        Assert.True(span >= 10, $"wave should run a long course (span {span})");
    }

    // Rollback: snapshot mid-flight (cells tagged, wall not reached), run on past the
    // stop, restore, replay — identical checksums every frame.
    [Fact]
    public void SnapshotMidFlight_ReplaysIdentically()
    {
        var sim = NewSim(Arena(22, 24), Standing(4), Standing(6));
        Launch(sim, CellCenter(12, FloorRow - 2), new Vector2(320f, 0f));
        Step(sim, 15);
        var snap = sim.Snapshot();
        var first = new List<ulong>();
        for (int f = 0; f < 90; f++) { sim.Step(default, default); first.Add(sim.Checksum()); }
        sim.Restore(snap);
        for (int f = 0; f < 90; f++)
        {
            sim.Step(default, default);
            Assert.Equal(first[f], sim.Checksum());
        }
        Assert.DoesNotContain(DirtCells(sim.Chunks), c => c.x >= 22);
    }

    // #2: a NON-rider between an incoming wave and a wall is pinned and crushed. A plain
    // standing player is caught by the wave and RIDES it into the face (TerrainCarried —
    // exempt this pass, see RiderAtWall below), so the non-rider here is a victim already
    // stunned by a hit (Stunned outranks TerrainCarried), the combo the crush rewards.
    [Theory]
    [InlineData(20f, 10)]
    [InlineData(20.5f, 10)]
    [InlineData(20.9f, 10)]
    [InlineData(20.5f, 14)]
    public void NonRider_PinnedAgainstWall_TakesHeavyCrush(float col, int launchCol)
    {
        var sim = NewSim(Arena(22, 24), Standing(col), Standing(4));
        var p1 = sim.Player;
        Launch(sim, CellCenter(launchCol, FloorRow - 1), new Vector2(320f, 0f));
        float hp0 = p1.Health;
        bool rode = false;
        int pinned = 0;
        for (int f = 0; f < 90; f++)
        {
            // Keep the victim stunned through the wave's arrival (re-hit every 0.5 s).
            if (f % 30 == 10) p1.Combat.OnHitRegistered(p1.Frame, 300f, 1f / 60f, hitstunSecondsOverride: 0.6f);
            sim.Step(default, default);
            rode |= p1.CurrentStateName == nameof(TerrainCarriedState);
            if (p1.Body.WavePinned) pinned++;
        }
        float lost = hp0 - p1.Health;
        output.WriteLine($"lost {lost:F2}; pinned frames {pinned}; rode {rode}");
        Assert.False(rode, "this is the NON-rider case");
        Assert.True(lost >= 1.5f, $"pinned against the wall should crush hard; lost {lost:F2}");
    }

    // The rider exemption: a plain standing victim is carried into the wall face. Riding is
    // off-limits this pass, so the wall-pin bill must never land on a TerrainCarried body.
    [Fact]
    public void RiderAtWall_IsNotBilledThePin()
    {
        var sim = NewSim(Arena(22, 24), Standing(19), Standing(4));
        var p1 = sim.Player;
        Launch(sim, CellCenter(10, FloorRow - 1), new Vector2(320f, 0f));
        float hp0 = p1.Health;
        bool rode = false;
        for (int f = 0; f < 90; f++)
        {
            sim.Step(default, default);
            rode |= p1.CurrentStateName == nameof(TerrainCarriedState);
        }
        float lost = hp0 - p1.Health;
        output.WriteLine($"lost {lost:F2}; rode {rode}");
        Assert.True(rode);
        Assert.True(lost < 1.5f, $"rider took a pin-sized bill: {lost:F2}");
    }

    // Avalanche-only: ordinary (untagged) building that squeezes a stunned body against a
    // wall gets today's sprout-crush treatment, not the wall-pin bill.
    [Fact]
    public void UntaggedBuild_AgainstWall_IsNotAWavePin()
    {
        var terrain = Arena(22, 24);
        var sim = NewSim(terrain, Standing(20.5f), Standing(4));
        var p1 = sim.Player;
        float hp0 = p1.Health;
        int pinned = 0;
        for (int f = 0; f < 90; f++)
        {
            if (f % 30 == 0) p1.Combat.OnHitRegistered(p1.Frame, 300f, 1f / 60f, hitstunSecondsOverride: 0.6f);
            if (f >= 5)
                for (int x = 15; x <= 21; x++)
                for (int y = FloorRow - 1; y >= FloorRow - 3; y--)
                    terrain.TryRequestTile(x, y, TileType.Dirt);
            sim.Step(default, default);
            if (p1.Body.WavePinned) pinned++;
        }
        float lost = hp0 - p1.Health;
        output.WriteLine($"lost {lost:F2}; pinned {pinned}");
        Assert.Equal(0, pinned);
    }
}
