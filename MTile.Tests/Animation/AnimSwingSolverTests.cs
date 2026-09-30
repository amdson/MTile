using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

public class AnimSwingSolverTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void SwingResidual_MeasuresTheRenderedPoint_AndItsRootDerivatives(int facing)
    {
        var rig = SkeletonExamples.Load("biped");
        var p = new SolveProblem(rig) { Cfg = new AnimSolverConfig(), InvCharLen = 1f / 20,
            BaseBlend = Enumerable.Repeat(1f, rig.Count).ToArray() };
        var e = new PoseEval(rig, p.Vars) { Root = Affine2.FromTRS(new Vector2(12, 20), 0, new Vector2(facing * .6f, .6f)) };
        int foot = rig.IndexOf("leg_l_lower");
        e.Pose.ComputeWorld(e.Root);
        var x = new float[p.Vars]; x[SolveProblem.IdxDx] = 2; x[SolveProblem.IdxDy] = -5;
        p.Swings.Add((foot, e.Pose.WorldOf(foot).Translation + new Vector2(2, -5)));
        var block = new SwingTargetConstraint();
        var r = new float[2]; block.Residuals(p, e, x, r);
        Assert.All(r, v => Assert.Equal(0f, v));
        var jac = new float[2 * p.Vars]; block.Jacobian(p, e, x, jac, p.Vars, 0);
        foreach (int col in new[] { SolveProblem.IdxDx, SolveProblem.IdxDy })
        {
            x[col] += .01f; block.Residuals(p, e, x, r); x[col] -= .01f;
            for (int row = 0; row < 2; row++)
                Assert.InRange(MathF.Abs(r[row] / .01f - jac[row * p.Vars + col]), 0, .001f);
        }
    }

    [Fact]
    public void PlannedSwing_TriggersSolve_WithoutPlantsOrTerrainPlanes()
    {
        var rig = SkeletonExamples.Load("biped");
        var clip = new AnimationDocument
        {
            Name = "swing", Type = "Stairs", Skeleton = "biped", Loop = true,
            Contacts = new() { new() { Point = "support_l", Start = 0, End = .15f } },
            Keyframes = new()
            {
                Key(0), Key(.5f), Key(1),
            },
        };
        var terrain = SimTerrain.FromAscii(new string('X', 100), originTileX: -20, originTileY: 3);
        var anim = new CharacterAnimator(rig, .6f, new[] { clip });
        int swings = 0;
        // Airborne foot targets are OFF by default (the shipped anim_solver_config.json); this
        // test is the A/B's ON side, so it opts in explicitly and restores the shipped value.
        bool prevSwing = AnimSolverConfig.Current.SwingTargetsEnabled;
        AnimSolverConfig.Current.SwingTargetsEnabled = true;
        try
        {
            for (int i = 0; i < 100; i++)
            {
                anim.Update(new CharacterAnimSample(new Vector2(i * .5f, 3 * Chunk.TileSize - 18),
                    new Vector2(30, 0), 1, true, "StairClimbState", "", 1f / 60,
                    tag: AnimTag.Stairs, chunks: terrain, surfacesNear: false));
                if (!anim.Planner.Plans.Take(anim.Planner.FeetCount).Any(p => p.State == FootPlanState.Swing && p.HasSupport)) continue;
                swings++;
                Assert.True(anim.SolvedThisFrame);
                Assert.Equal(0, anim.BaselineContactCount);
                Assert.True(anim.MaxJacobianError() < .01f);
            }
        }
        finally { AnimSolverConfig.Current.SwingTargetsEnabled = prevSwing; }
        Assert.True(swings > 10, $"only {swings} planned swing frames");

        static AnimationKeyframe Key(float t) => new()
        {
            Time = t,
            Bones = new List<PoseBoneEntry> { new() { Bone = "leg_l_upper", Rotation = 1.2f }, new() { Bone = "leg_l_lower", Rotation = .6f } },
            Additions = new() { new() { Name = "com", Kind = AnimAdditionKind.Point, Py = -11 } },
        };
    }
}
