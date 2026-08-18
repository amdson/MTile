using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;

namespace MTile.Bench;

// Why does the rig hover during run but sit on the ground during idle?
//
// The rig's vertical placement is AttackGlowSystem.RigRoot: rootY = BodyY − com.Y·scale,
// where com.Y is the clip's authored "com" Point. Probe's `addcom` stamps it as
// soleLocal − 2·Radius/scale, which makes the sole land exactly on the hexagon bottom
// (BodyY + 2·Radius) — the ground line for a grounded body. A clip whose com.Y does NOT
// satisfy that identity places its feet off the ground by (soleLocal − com.Y − 40)·scale,
// and nothing in the solver pulls them back: PlantedContacts is SelfPlant (its target is
// captured FROM the rig, so it holds whatever height the rig had) and ComOffset pulls the
// solved δ back toward zero. So an authoring error here is a permanent visual offset.
//
// This measures the actual drawn foot tip against the ground line, per frame.
internal static class HoverDiag
{
    private const float Scale = 0.6f;

    public static void Run()
    {
        string rigDir = FtolStudy.FindDir("SkeletonStates");
        if (rigDir == null) { Console.WriteLine("no SkeletonStates dir"); return; }
        foreach (var (rig, label, input) in new (string, string, PlayerInput)[]
                 {
                     ("biped", "idle", new PlayerInput()),
                     ("biped", "run",  new PlayerInput { Right = true }),
                     ("biped_rabbit", "run", new PlayerInput { Right = true }),
                 })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (Directory.Exists(clipDir)) Dump(rig, label, clipDir, input);
        }
    }

    private static void Dump(string rig, string label, string clipDir, PlayerInput input)
    {
        List<AnimationDocument> clips;
        Skeleton skel;
        try { clips = AnimationStore.LoadAll(clipDir); skel = SkeletonExamples.Load(rig); }
        catch { return; }
        if (clips == null || clips.Count == 0) return;

        var anim = new CharacterAnimator(skel, Scale, clips);
        var sim  = new Simulation(FtolStudy.FlatFloor(), new Vector2(24f, 72f));
        var surfaces = new SolverSurface[8];
        int fl = skel.IndexOf("foot_l"), fr = skel.IndexOf("foot_r");
        if (fl < 0 || fr < 0) return;

        const int Warm = 180, N = 600;
        for (int f = 0; f < Warm; f++) FtolStudy.Tick(sim, anim, input, surfaces, Scale);

        // Gap = (lowest drawn foot tip) − (ground line). Negative = hovering above it.
        var gaps = new List<float>();
        var trace = new List<float>();
        int grounded = 0;
        float bodyMin = float.MaxValue, bodyMax = -float.MaxValue;
        int withPlane = 0;
        float planeGapSum = 0f; int planeN = 0;
        for (int f = 0; f < N; f++)
        {
            sim.Step(input);
            var p = sim.Player;
            int tc = TerrainSurfaces.Extract(sim.Chunks, anim, p.Body.Position, p.Facing, Scale,
                                             surfaces, out bool near);
            anim.Update(CharacterAnimSample.From(p, Simulation.FixedDt, surfaces, tc, near, sim.Chunks));
            if (!p.IsGrounded) continue;
            grounded++;
            var root = AttackGlowSystem.RigRoot(p, anim, Scale);
            var w = anim.Pose.ComputeWorld(Affine2.FromTRS(root, 0f,
                        new Vector2((p.Facing == 0 ? 1 : p.Facing) * Scale, Scale)));
            int lower = w[fl].Translation.Y > w[fr].Translation.Y ? fl : fr;
            Vector2 toeP = w[lower].Translation;
            float ground = p.Body.Position.Y + 2f * PlayerCharacter.Radius;
            gaps.Add(toeP.Y - ground);
            if (trace.Count < 90) trace.Add(ground - toeP.Y);
            float bg = ground - 6f * Chunk.TileSize;
            bodyMin = MathF.Min(bodyMin, bg); bodyMax = MathF.Max(bodyMax, bg);

            // Is an UPWARD-facing terrain plane already extracted for this toe? That is the
            // datum a real ground-contact constraint would snap the plant target onto.
            for (int i = 0; i < tc; i++)
            {
                var s = surfaces[i];
                if (s.Normal.Y > -0.7f) continue;                    // y-down: upward-facing
                if (((s.BoneMask >> lower) & 1) == 0) continue;
                withPlane++;
                planeGapSum += MathF.Abs(s.Normal.X * (toeP.X - s.Point.X)
                                       + s.Normal.Y * (toeP.Y - s.Point.Y));
                planeN++;
                break;
            }
        }
        if (gaps.Count == 0) { Console.WriteLine(rig + "/" + label + ": never grounded"); return; }
        gaps.Sort();
        anim.TryComReference(out var com);
        // Report as HEIGHT ABOVE the ground line, so bigger = more hover. `closest` is the
        // statistic that matters: over a stride the swing foot is legitimately airborne, so
        // only the stride's lowest moment says whether the rig ever actually touches down.
        float P(double q) => -gaps[Math.Min(gaps.Count - 1, (int)(gaps.Count * q))];
        Console.WriteLine(string.Format(
            "{0,-16} grounded {1,3}/{2}  foot above ground px: closest {3,5:0.00}  p25 {4,5:0.00}  " +
            "median {5,5:0.00}  worst {6,5:0.00}   body above floor {7,5:0.00}..{8,5:0.00}   com.Y {9:0.00}",
            rig + "/" + label, grounded, N, P(0.999), P(0.75), P(0.5), P(0.0), -bodyMax, -bodyMin, com.Y));
        Console.WriteLine(string.Format("{0,-16} upward terrain plane extracted for the lower toe on {1}/{2} grounded frames; mean toe-to-plane {3:0.00} px",
            rig + "/" + label, withPlane, grounded, planeN > 0 ? planeGapSum / planeN : 0f));
        Console.Write("  trace (px above ground, one per frame): ");
        foreach (var g in trace) Console.Write(((int)MathF.Round(g)).ToString() + " ");
        Console.WriteLine();
    }
}
