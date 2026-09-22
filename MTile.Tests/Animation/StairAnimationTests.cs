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
        float total = 0;
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
                    // Loop-back may repeat less than half a cycle; summing wrapped
                    // phase differences would subtract each successful transition.
                    total += animator.PhaseStep;
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

    // The sample builder's terrain probe (the vault-chain path — StairClimbState declares
    // its own tag, so it is switched off here to exercise the probe).
    [Fact]
    public void StairSample_DoesNotOverrideStoppingBackpedalingDescendingOrDistantFlight()
    {
        bool prev = MovementConfig.Current.StairClimbEnabled;
        MovementConfig.Current.StairClimbEnabled = false;
        try
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
        finally { MovementConfig.Current.StairClimbEnabled = prev; }
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

    [Theory]
    [InlineData("biped_rabbit")]
    public void StairsClip_HasContinuousSupport_ClearSwings_AndAnAuthoredLoop(string rigName)
    {
        var rig = SkeletonExamples.Load(rigName);
        var clip = AnimationStore.LoadAll(StatesDir(rigName)).Single(c => c.Type == "Stairs");
        Assert.True(ClipStrideTrack.TryCompile(clip, rig, out var track, out string error), error);
        Assert.Equal(2, track.Feet.Length);
        Assert.True(BodyPath.TryCycleDisplacement(clip, out var travel));
        Assert.Equal(2 * Chunk.TileSize, travel.X * Game1.SkeletonScale, 3);
        Assert.Equal(-2 * Chunk.TileSize, travel.Y * Game1.SkeletonScale, 3);
        for (int i = 0; i < 200; i++)
            Assert.Contains(track.Feet, f => f.StanceAt(i / 200f, out _) >= 0);
        Assert.True(ClipSceneBake.TryCheck(clip, rig, out var check, out error), error);
        Assert.True(check.MaxDrift < 1f, $"stance drifts {check.MaxDrift} rig units");
        Assert.True(check.MaxPenetration < 0.5f, $"swing penetrates {check.MaxPenetration} rig units");
        Assert.False(ClipLoopBack.Plan(clip, rig, 0.25f, 0.3f).Jumps);
        Assert.DoesNotContain("SEAM MISMATCH", MotionProbe.Digest(clip, rig));
    }

    [Fact]
    public void BipedStairs_RetainsTheAuthoredHopBetweenPlants()
    {
        var rig = SkeletonExamples.Load("biped");
        var clip = AnimationStore.LoadAll(StatesDir("biped")).Single(c => c.Type == "Stairs");
        Assert.True(ClipStrideTrack.TryCompile(clip, rig, out var track, out string error), error);
        Assert.Contains(track.Feet, f => f.StanceAt(.57f, out _) >= 0);
        Assert.All(track.Feet, f => Assert.True(f.StanceAt(.61f, out _) < 0,
            "The authored hop must have no planted foot."));
        Assert.Contains(track.Feet, f => f.StanceAt(.66f, out _) >= 0);
        var loop = ClipLoopBack.Plan(clip, rig, .25f, .3f);
        Assert.True(loop.Jumps);
        Assert.True(loop.Entry < .58f && loop.Exit > .65f,
            $"Loop {loop.Entry:F3}–{loop.Exit:F3} skips the hop.");
    }

    [Theory]
    [InlineData("biped", 1)]
    [InlineData("biped", -1)]
    [InlineData("biped_rabbit", 1)]
    [InlineData("biped_rabbit", -1)]
    public void RenderedStairs_PlantTheFeet_WithoutMidFlightPhaseJumps(string rigName, int direction)
    {
        var saved = new AnimSolverConfig(); saved.CopyFrom(AnimSolverConfig.Current);
        try
        {
            AnimSolverConfig.Load(Path.GetFullPath(Path.Combine(StatesDir(rigName), "../../configs/anim_solver_config.json")));
            var terrain = Terrain(direction);
            var sim = new Simulation(terrain, new Vector2((direction == 1 ? 1.5f : 48.5f) * Chunk.TileSize,
                15 * Chunk.TileSize - PlayerCharacter.Radius));
            var animator = Animator(rigName);
            var surfaces = new SolverSurface[8];
            var predictor = new LatticePathSampler();
            int samples = 0, supported = 0, reentries = 0;
            float errorSum = 0, worstError = 0, worstStep = 0, minKnee = float.MaxValue, maxKnee = float.MinValue;
            int flightFrames = 0;
            Vector2[] previous = null;
            var previousTargets = new Vector2[animator.Skeleton.Count];
            for (int f = 0; f < 200; f++)
            {
                sim.Step(new PlayerInput { Right = direction == 1, Left = direction == -1 });
                int count = TerrainSurfaces.Extract(terrain, animator, sim.Player.Body.Position,
                    sim.Player.Facing, Game1.SkeletonScale, surfaces, out bool near);
                predictor.Bind(sim.Player);
                animator.Update(CharacterAnimSample.From(sim.Player, Dt, surfaces, count, near, terrain, predictor.PredictAt));
                var root = AttackGlowSystem.RigRoot(sim.Player.Body.Position, direction, animator, Game1.SkeletonScale);
                var world = animator.Pose.ComputeWorld(Affine2.FromTRS(root, 0, new Vector2(direction * Game1.SkeletonScale, Game1.SkeletonScale)));
                var tips = world.Select(w => w.Translation).ToArray();
                float forward = direction == 1 ? sim.Player.Body.Position.X / Chunk.TileSize : 50 - sim.Player.Body.Position.X / Chunk.TileSize;
                if (forward > 10 && forward < 16)
                {
                    if (rigName == "biped")
                        foreach (string bone in new[] { "leg_l_lower", "leg_r_lower" })
                        {
                            float bend = MathHelper.WrapAngle(animator.Pose.Local[animator.Skeleton.IndexOf(bone)].Rotation);
                            minKnee = MathF.Min(minKnee, bend); maxKnee = MathF.Max(maxKnee, bend);
                        }
                    if (animator.LastTiming.Reentered) reentries++;
                    bool hasSupport = false;
                    for (int i = 0; i < animator.Planner.FeetCount; i++)
                    {
                        var p = animator.Planner.Plans[i];
                        if (previous != null)
                        {
                            float step = Vector2.Distance(previous[p.Bone], tips[p.Bone]);
                            // Fast authored swings are legitimate. Catch movement beyond the
                            // requested path, without flattening the hop to satisfy a speed cap.
                            float requested = Vector2.Distance(previousTargets[p.Bone], p.Target);
                            Assert.True(step < MathF.Max(8f, requested + 3f),
                                $"frame {f}: foot moved {step:F3}px for target movement {requested:F3}px");
                            worstStep = MathF.Max(worstStep, step);
                        }
                        if (p.State != FootPlanState.Stance || !p.HasSupport) continue;
                        hasSupport = true;
                        if (p.Weight < 0.5f) continue;
                        float err = Vector2.Distance(tips[p.Bone], p.Target);
                        errorSum += err; samples++; worstError = MathF.Max(worstError, err);
                    }
                    if (hasSupport) supported++;
                    else flightFrames++;
                }
                previous = tips;
                for (int i = 0; i < animator.Planner.FeetCount; i++)
                    previousTargets[animator.Planner.Plans[i].Bone] = animator.Planner.Plans[i].Target;
            }
            output.WriteLine($"{rigName} dir={direction}: samples={samples} supported={supported} flight={flightFrames} mean={errorSum / samples:F3} max={worstError:F3} step={worstStep:F3} reentries={reentries}");
            Assert.True(samples > 15 && supported > 20);
            if (rigName == "biped")
            {
                Assert.True(flightFrames > 0, "The runtime removed the hop's flight frames.");
                Assert.True(minKnee >= -0.02f && maxKnee < MathF.PI, $"Knee inverted: {minKnee:F3}..{maxKnee:F3}");
            }
            Assert.True(errorSum / samples < 1.5f);
            Assert.True(worstError < 4f);
            Assert.Equal(0, reentries);
        }
        finally { AnimSolverConfig.Current.CopyFrom(saved); }
    }

    // The Stairs/StepUp split on the sample builder's PROBE path (StairClimbState off):
    // Stairs needs two risers still AHEAD of the body, StepUp is the last riser. A 10-step
    // staircase reads Stairs from the bottom until the second-to-last riser is under the
    // feet, then StepUp for the final one; a 2-step flight reads Stairs at its foot and
    // StepUp for its last riser; a lone step never reads Stairs.
    [Theory]
    [InlineData(10)]
    [InlineData(2)]
    public void ProbePath_StairsTag_NeedsTwoRisersAhead_ThenHandsTheLastRiserToStepUp(int steps)
    {
        bool prev = MovementConfig.Current.StairClimbEnabled;
        MovementConfig.Current.StairClimbEnabled = false;
        try
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
        finally { MovementConfig.Current.StairClimbEnabled = prev; }
    }

    // With StairClimbState (the default): the state declares the tag and owns the whole
    // flight, so Stairs runs to the top with no StepUp/Parkour re-tag inside it, and a lone
    // step still never reads Stairs.
    [Theory]
    [InlineData(10)]
    [InlineData(2)]
    public void StatePath_StairsTag_OwnsTheFlightToTheTop(int steps)
    {
        var terrain = Terrain(1, steps);
        var sim = new Simulation(terrain, new Vector2(16, 15 * Chunk.TileSize - PlayerCharacter.Radius));
        int stairsFrames = 0; bool ended = false, retagged = false;
        for (int f = 0; f < 250; f++)
        {
            sim.Step(new PlayerInput { Right = true });
            var tag = CharacterAnimSample.From(sim.Player, Dt, chunks: terrain).Tag;
            if (tag == AnimTag.Stairs) { stairsFrames++; if (ended) retagged = true; }
            else if (stairsFrames > 0) ended = true;
        }
        Assert.True(stairsFrames > 5, $"Stairs never tagged on a {steps}-step flight ({stairsFrames} frames)");
        Assert.False(retagged, "Stairs re-tagged after the flight ended");
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
        // The first sample establishes position history: 119 intervals at 25 px/s
        // give 0.4958 cycles for a clip using the nominal 100 px cycle distance.
        Assert.True(total > 0.45f, $"{rigName} step-up cadence stalled: {total}");
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
