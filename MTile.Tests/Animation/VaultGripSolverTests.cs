using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Vault grip pins: the payoff of FixedPoint (Phase 2) + the off-locomotion solve trigger
// (Phase 3) + Δθ-post-compose. During a parkour vault the lead hand is owned by the VaultHands
// OVERLAY (slot 1), so this is the first test that pins an overlay-owned bone — it only reaches
// because Δθ is applied AFTER composition (pre-compose Δθ would be overwritten by the overlay).
public class VaultGripSolverTests
{
    private readonly ITestOutputHelper _o;
    public VaultGripSolverTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Solver_VaultGrip_HandReachesLedgeCornerThroughOverlay()
    {
        var clips = AnimationStore.LoadAll(StatesDir());
        var skel  = SkeletonExamples.Biped();
        var anim  = new CharacterAnimator(skel, 0.6f, clips, useSolver: true);
        int hand  = anim.Skeleton.IndexOf("arm_l_lower");
        int up    = anim.Skeleton.IndexOf("arm_l_upper");
        int chest = anim.Skeleton.IndexOf("chest");

        const float dt = 1f / 30f, progress = 0.6f;   // inside the grab window [0.45, 0.85]
        var pos = new Vector2(0f, 0f);
        const int facing = +1;

        // Warm up the composed vault pose (vault legs base + VaultHands overlay) with NO grip so
        // the overlay eases fully in (weight→1: the hand becomes OVERLAY-OWNED) and the eased pose
        // settles. Then read where the hand naturally sits and place the corner a small offset off
        // it, so the required correction is small and steady — a clean read of whether the pin
        // reaches an overlay-owned bone at all.
        for (int i = 0; i < 24; i++)
            anim.Update(new CharacterAnimSample(pos, Vector2.Zero, facing, false, "ParkourState", "", dt,
                movementProgress: progress));

        anim.TryComReference(out var comL);
        var root = Affine2.FromTRS(new Vector2(pos.X, pos.Y - comL.Y * 0.6f), 0f, new Vector2(facing * 0.6f, 0.6f));
        Vector2 natHand = anim.Pose.ComputeWorld(root)[hand].Translation;
        var corner = natHand + new Vector2(1.5f, -1.5f);
        _o.WriteLine($"natHand=({natHand.X:0.00},{natHand.Y:0.00}) corner=({corner.X:0.00},{corner.Y:0.00})");

        float maxReach = 0f, maxArm = 0f, maxChest = 0f, maxJacErr = 0f; int solves = 0;
        for (int i = 0; i < 24; i++)
        {
            anim.Update(new CharacterAnimSample(pos, Vector2.Zero, facing, false, "ParkourState", "", dt,
                movementProgress: progress, hasGrip: true, gripTarget: corner));
            string rep = anim.SolveScaleReport();
            if (rep == "(no solve)") continue;
            solves++;
            Vector2 tip = anim.SolvedBoneTipWorld(hand);
            maxReach  = MathF.Max(maxReach, (tip - corner).Length());
            maxArm    = MathF.Max(maxArm, MathF.Max(MathF.Abs(anim.AngleCorrection(hand)), MathF.Abs(anim.AngleCorrection(up))));
            maxChest  = MathF.Max(maxChest, MathF.Abs(anim.AngleCorrection(chest)));
            maxJacErr = MathF.Max(maxJacErr, anim.MaxJacobianError());
            if (i % 6 == 0) _o.WriteLine($"f{i,2}: reach={(tip - corner).Length():0.00} armΔθ={maxArm:0.000} chestΔθ={maxChest:0.000} jacErr={anim.MaxJacobianError():0.0000}  {rep}");
        }

        _o.WriteLine($"=> solves={solves} maxReach={maxReach:0.00}px maxArmΔθ={maxArm:0.000} maxChestΔθ={maxChest:0.000} maxJacErr={maxJacErr:0.0000}");

        // The off-locomotion solve ran during the vault (the trigger fires on a pin, not contacts).
        Assert.True(solves > 0, "no vault-grip solve ran");
        // The hand reaches the ledge corner — DESPITE the VaultHands overlay fully owning the arm
        // (this is the Δθ-post-compose enabling change working; pre-compose Δθ couldn't move it).
        Assert.True(maxReach < 1.5f, $"hand didn't reach the corner ({maxReach:0.00}px) — pin not driving the overlay-owned arm");
        // ...via a real, nonzero arm correction...
        Assert.True(maxArm > 0.02f, $"arm Δθ never engaged ({maxArm:0.000})");
        // ...the arm does the work, not the torso (stiff core keeps the body steady)...
        Assert.True(maxChest < 0.2f, $"torso swung to reach the grip (chest Δθ {maxChest:0.000})");
        // ...and the analytic Jacobian is exact WITH an overlay active (the unattenuated-Δθ column,
        // a case the locomotion FD-vs-analytic test never exercised).
        Assert.True(maxJacErr < 0.05f, $"Jacobian disagrees with FD under an overlay (relErr {maxJacErr:0.0000})");
    }

    private static string StatesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "SkeletonStates");
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        return "SkeletonStates";
    }
}
