using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

public class StairAnimationTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    private static ChunkMap Terrain(int direction, int steps = 10)
    {
        var rows = new StringBuilder();
        for (int y = 0; y < 18; y++)
        {
            for (int x = 0; x < 50; x++)
            {
                int forward = direction == 1 ? x : 49 - x;
                int top = 15 - Math.Clamp(forward - 7, 0, steps);
                rows.Append(y >= top ? 'X' : 'O');
            }
            rows.AppendLine();
        }
        return SimTerrain.FromAscii(rows.ToString());
    }

    [Theory]
    [InlineData(1, "biped")]
    [InlineData(-1, "biped")]
    [InlineData(1, "biped_rabbit")]
    [InlineData(-1, "biped_rabbit")]
    public void StairTraversal_KeepsOneCycleAcrossMovementTransitions_ThenExits(int direction, string rig)
    {
        var terrain = Terrain(direction);
        float startX = (direction == 1 ? 1.5f : 48.5f) * Chunk.TileSize;
        var sim = new Simulation(terrain, new Vector2(startX, 15 * Chunk.TileSize - PlayerCharacter.Radius));
        var animator = Animator(rig);
        var surfaces = new SolverSurface[32];
        int stairFrames = 0, middleFrames = 0, missed = 0;
        bool leftStairs = false;
        float phase = 0, total = 0;
        for (int f = 0; f < 200; f++)
        {
            sim.Step(new PlayerInput { Right = direction == 1, Left = direction == -1 });
            int count = TerrainSurfaces.Extract(terrain, animator, sim.Player.Body.Position,
                sim.Player.Facing, Game1.SkeletonScale, surfaces, out bool near);
            var s = CharacterAnimSample.From(sim.Player, Dt, surfaces, count, near, terrain);
            animator.Update(s);
            float forward = direction == 1 ? s.Position.X / Chunk.TileSize : 50 - s.Position.X / Chunk.TileSize;
            if (forward > 10 && forward < 16)
            {
                middleFrames++;
                if (s.Tag != AnimTag.StepUp) missed++;
            }
            if (s.Tag == AnimTag.StepUp)
            {
                Assert.Equal(AnimClip.StepUp, animator.State.Clip);
                if (stairFrames++ > 0)
                {
                    float delta = animator.State.Phase - phase;
                    total += delta < -0.5f ? delta + 1 : delta;
                }
                phase = animator.State.Phase;
            }
            if (forward > 21)
            {
                Assert.NotEqual(AnimTag.StepUp, s.Tag);
                leftStairs = true;
                break;
            }
        }
        output.WriteLine($"stair frames={stairFrames}, middle={middleFrames}, misses={missed}, phase={total}");
        Assert.True(stairFrames > 15);
        Assert.True(middleFrames > 10);
        Assert.Equal(0, missed);
        Assert.True(total > 0.5f, $"stair cadence froze: {total}");
        Assert.True(leftStairs);
    }

    [Fact]
    public void StairSample_DoesNotOverrideStoppingBackpedalingDescendingOrDistantFlight()
    {
        var terrain = Terrain(1);
        var sim = new Simulation(terrain, new Vector2(16, 15 * Chunk.TileSize - PlayerCharacter.Radius));
        bool entered = false;
        for (int f = 0; f < 100; f++)
        {
            sim.Step(new PlayerInput { Right = true });
            if (CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag == AnimTag.StepUp)
            { entered = true; break; }
        }
        Assert.True(entered);
        foreach (var velocity in new[] { Vector2.Zero, new Vector2(-50, -30), new Vector2(50, 50) })
        {
            sim.Player.Body.Velocity = velocity;
            Assert.NotEqual(AnimTag.StepUp, CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag);
        }
        sim.Player.Body.Velocity = new Vector2(50, -50);
        sim.Player.Body.Position -= new Vector2(0, 100);
        Assert.NotEqual(AnimTag.StepUp, CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FlatGroundAndSingleLedge_DoNotSelectStairs(int steps)
    {
        var terrain = Terrain(1, steps);
        var sim = new Simulation(terrain, new Vector2(16, 15 * Chunk.TileSize - PlayerCharacter.Radius));
        for (int f = 0; f < 100; f++)
        {
            sim.Step(new PlayerInput { Right = true });
            Assert.NotEqual(AnimTag.StepUp, CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag);
        }
    }

    [Theory]
    [InlineData("biped", 1, 25f)]
    [InlineData("biped", -1, 90f)]
    [InlineData("biped_rabbit", 1, 90f)]
    [InlineData("biped_rabbit", -1, 25f)]
    public void AuthoredStairCycle_AdvancesUphill_AndHasNoGeometryFlags(string rigName, int facing, float speed)
    {
        var animator = Animator(rigName);
        var clip = AnimationStore.LoadAll(StatesDir(rigName)).Single(c => c.Type == "StepUp");
        Assert.True(clip.Loop);
        Assert.DoesNotContain("FLAGS:", MotionProbe.Digest(clip, SkeletonExamples.Load(rigName)));
        float previous = 0, total = 0;
        for (int f = 0; f < 120; f++)
        {
            animator.Update(new CharacterAnimSample(new Vector2(facing * speed * f * Dt, -speed * f * Dt),
                new Vector2(facing * speed, -speed), facing, false, "FallingState", "", Dt, tag: AnimTag.StepUp));
            float delta = animator.State.Phase - previous;
            total += delta < -0.5f ? delta + 1 : delta;
            previous = animator.State.Phase;
        }
        Assert.True(total > 0.5f, $"{rigName} stair cadence froze: {total}");
    }

    private static CharacterAnimator Animator(string rig)
        => new(SkeletonExamples.Load(rig), Game1.SkeletonScale, AnimationStore.LoadAll(StatesDir(rig)));

    private static string StatesDir(string rig)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string path = Path.Combine(dir.FullName, "SkeletonStates", rig);
            if (Directory.Exists(path)) return path;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(rig);
    }
}
