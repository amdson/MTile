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
            bool onStairs = s.Tag is AnimTag.Stairs or AnimTag.StepUp;
            if (forward > 10 && forward < 16)
            {
                middleFrames++;
                // Mid-staircase there are always two risers ahead: the Stairs clip, never StepUp.
                if (s.Tag != AnimTag.Stairs) missed++;
            }
            if (onStairs)
            {
                Assert.Equal(s.Tag == AnimTag.Stairs ? AnimClip.Stairs : AnimClip.StepUp, animator.State.Clip);
                if (stairFrames++ > 0)
                {
                    float delta = animator.State.Phase - phase;
                    total += delta < -0.5f ? delta + 1 : delta;
                }
                phase = animator.State.Phase;
            }
            if (forward > 21)
            {
                Assert.False(onStairs, $"still tagged {s.Tag} past the staircase");
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
            if (CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag is AnimTag.Stairs or AnimTag.StepUp)
            { entered = true; break; }
        }
        Assert.True(entered);
        foreach (var velocity in new[] { Vector2.Zero, new Vector2(-50, -30), new Vector2(50, 50) })
        {
            sim.Player.Body.Velocity = velocity;
            AssertNotStairs(CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag);
        }
        sim.Player.Body.Velocity = new Vector2(50, -50);
        sim.Player.Body.Position -= new Vector2(0, 100);
        AssertNotStairs(CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag);
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
            AssertNotStairs(CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag);
        }
    }

    private static void AssertNotStairs(AnimTag tag)
        => Assert.False(tag is AnimTag.Stairs or AnimTag.StepUp, $"tagged {tag}");

    // The Stairs/StepUp split: Stairs needs two risers still AHEAD of the body, StepUp is the
    // last riser (or a short flight). A 10-step staircase reads Stairs from the bottom until
    // the second-to-last riser is under the feet, then StepUp for the final one; a 2-step
    // flight reads Stairs at its foot and StepUp for its last riser; a lone step never
    // reads Stairs.
    [Theory]
    [InlineData(10)]
    [InlineData(2)]
    public void StairsTag_NeedsTwoRisersAhead_ThenHandsTheLastRiserToStepUp(int steps)
    {
        var terrain = Terrain(1, steps);
        var sim = new Simulation(terrain, new Vector2(16, 15 * Chunk.TileSize - PlayerCharacter.Radius));
        int stairsFrames = 0, stepUpFrames = 0; bool stepUpAfterStairs = false, stairsAfterStepUp = false;
        for (int f = 0; f < 250; f++)
        {
            sim.Step(new PlayerInput { Right = true });
            var tag = CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag;
            if (tag == AnimTag.Stairs) { stairsFrames++; if (stepUpFrames > 0) stairsAfterStepUp = true; }
            if (tag == AnimTag.StepUp) { stepUpFrames++; if (stairsFrames > 0) stepUpAfterStairs = true; }
        }
        Assert.True(stairsFrames > 5, $"Stairs never tagged on a {steps}-step flight ({stairsFrames} frames)");
        Assert.True(stepUpAfterStairs, "the last riser should hand off to StepUp");
        Assert.False(stairsAfterStepUp, "Stairs re-tagged after the hand-off to StepUp");
    }

    [Fact]
    public void StairsTag_SelectsTheStairsClip_AboveStepUp()
    {
        var terrain = Terrain(1);
        var sim = new Simulation(terrain, new Vector2(1.5f * Chunk.TileSize, 15 * Chunk.TileSize - PlayerCharacter.Radius));
        var animator = Animator("biped");
        var surfaces = new SolverSurface[32];
        bool sawStairs = false;
        for (int f = 0; f < 200; f++)
        {
            sim.Step(new PlayerInput { Right = true });
            int count = TerrainSurfaces.Extract(terrain, animator, sim.Player.Body.Position,
                sim.Player.Facing, Game1.SkeletonScale, surfaces, out bool near);
            var s = CharacterAnimSample.From(sim.Player, Dt, surfaces, count, near, terrain);
            animator.Update(s);
            if (s.Tag == AnimTag.Stairs) { sawStairs = true; Assert.Equal(AnimClip.Stairs, animator.State.Clip); }
        }
        Assert.True(sawStairs);
    }

    [Theory]
    [InlineData("biped", 1, 25f)]
    [InlineData("biped", -1, 90f)]
    [InlineData("biped_rabbit", 1, 90f)]
    [InlineData("biped_rabbit", -1, 25f)]
    public void AuthoredStairCycle_AdvancesUphill_AndLoopsCleanly(string rigName, int facing, float speed)
    {
        var animator = Animator(rigName);
        var clip = AnimationStore.LoadAll(StatesDir(rigName)).Single(c => c.Type == "StepUp");
        Assert.True(clip.Loop);
        // The seam must be clean. (The digest's STEEP heuristic fires on the pilot's climb-first
        // swing keys — the knee lifts within a short interval by design; workplan chunk 7.)
        Assert.DoesNotContain("SEAM MISMATCH", MotionProbe.Digest(clip, SkeletonExamples.Load(rigName)));
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
