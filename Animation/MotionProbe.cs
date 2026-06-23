using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;

namespace MTile;

// Inspection utility: run the "motion function" (a clip sampled at a phase) all the way
// through forward kinematics to WORLD positions, so limb geometry reads as concrete
// (x, y) numbers instead of being guessed from raw joint angles. Also reports the
// phase-derivative — how each point moves as the clip plays — by central finite
// difference. A dev / authoring aid; render-side, never used by the game loop.
//
// Frame convention (matches the game): the rig root sits at the ORIGIN, the figure
// faces +X, and Y is DOWN. So read +x as "forward" (the way the figure walks/runs) and
// +y as "down" (toward the ground). Velocities are d(position)/d(phase).
//
// Reconstructing a leg as a polyline from the per-bone JOINT positions:
//   hip socket = leg_*_upper.Joint → knee = leg_*_lower.Joint
//   → ankle = foot_*.Joint → toe = foot_*.Tip
// so you can see directly whether the knee leads or trails the hip→ankle line.
public static class MotionProbe
{
    public readonly struct JointSample
    {
        public readonly Vector2 Joint;    // the bone's origin (its joint with its parent)
        public readonly Vector2 Tip;      // origin + Length along local +X (the foot/hand "contact" end)
        public readonly Vector2 TipVel;   // d(Tip)/d(phase), central finite difference
        public JointSample(Vector2 joint, Vector2 tip, Vector2 tipVel)
        { Joint = joint; Tip = tip; TipVel = tipVel; }
    }

    // World joint + tip of one bone at a clip phase, plus the tip's phase-velocity.
    // facing flips X; scale matches the rig→world scale (use 1 for raw rig units while
    // authoring). Allocates scratch poses — it's an offline probe, not a hot path.
    public static JointSample Sample(AnimationDocument clip, Skeleton rig, string bone,
                                     float phase, float scale = 1f, int facing = 1)
    {
        int b = rig.IndexOf(bone);
        if (b < 0) throw new ArgumentException($"no bone '{bone}' in rig '{rig.Name}'");

        var a = rig.CreatePose(); var bk = rig.CreatePose(); var c = rig.CreatePose();
        var d = rig.CreatePose(); var dst = rig.CreatePose();
        var root = Root(scale, facing);
        float len = rig.Bones[b].Length;

        var (joint, tip) = Eval(clip, rig, b, len, phase, root, a, bk, c, d, dst);
        const float e = 1e-3f;
        var (_, tipPlus)  = Eval(clip, rig, b, len, phase + e, root, a, bk, c, d, dst);
        var (_, tipMinus) = Eval(clip, rig, b, len, phase - e, root, a, bk, c, d, dst);
        return new JointSample(joint, tip, (tipPlus - tipMinus) / (2f * e));
    }

    private static (Vector2 joint, Vector2 tip) Eval(
        AnimationDocument clip, Skeleton rig, int b, float len, float phase, in Affine2 root,
        SkeletonPose a, SkeletonPose bk, SkeletonPose c, SkeletonPose d, SkeletonPose dst)
    {
        float p = phase - MathF.Floor(phase);                    // wrap into [0,1) for looping clips
        AnimationSampler.SampleSmooth(clip, p, a, bk, c, d, dst); // C1 spline — matches the game
        var w = dst.ComputeWorld(root);
        return (w[b].Translation, w[b].TransformPoint(new Vector2(len, 0f)));
    }

    private static readonly string[] DefaultJoints =
    {
        "hip", "chest", "head",
        "leg_l_upper", "leg_l_lower", "foot_l",
        "leg_r_upper", "leg_r_lower", "foot_r",
        "arm_l_upper", "arm_l_lower", "arm_r_upper", "arm_r_lower",
    };

    // A readable per-joint table swept across the clip's phase [0,1]. Pass specific
    // joint names to narrow it, or none for the full locomotion-relevant set.
    public static string Report(AnimationDocument clip, Skeleton rig, float scale = 1f,
                                int facing = 1, int samples = 16, params string[] joints)
    {
        if (joints == null || joints.Length == 0) joints = DefaultJoints;

        var sb = new StringBuilder();
        sb.AppendLine($"# motion probe: clip '{clip.Name}' (Type={clip.Type}, {clip.Keyframes.Count} keyframes)"
                    + $"  facing={(facing < 0 ? "-1" : "+1")}  scale={scale}");
        sb.AppendLine("# frame: +x = forward, +y = DOWN.  vel = d(tip)/d(phase) (central FD).");
        sb.AppendLine("# leg polyline: hip=leg_*_upper.joint  knee=leg_*_lower.joint  ankle=foot_*.joint  toe=foot_*.tip");

        foreach (var j in joints)
        {
            int bi = rig.IndexOf(j);
            if (bi < 0) { sb.AppendLine($"\n## {j}  (not in rig)"); continue; }
            float len = rig.Bones[bi].Length;
            sb.AppendLine();
            sb.AppendLine($"## {j}" + (len > 0f ? $"  (Length={len:0.#}, tip = joint+{len:0.#} along bone)"
                                                : "  (Length=0, tip==joint)"));
            sb.AppendLine("  phase |  jointX  jointY |   tipX   tipY |     vX      vY");
            for (int k = 0; k <= samples; k++)
            {
                float ph = (float)k / samples;
                var s = Sample(clip, rig, j, ph, scale, facing);
                sb.AppendLine($"  {ph,5:0.00} | {s.Joint.X,7:0.0} {s.Joint.Y,7:0.0} | "
                            + $"{s.Tip.X,6:0.0} {s.Tip.Y,6:0.0} | {s.TipVel.X,7:0.0} {s.TipVel.Y,7:0.0}");
            }
        }
        return sb.ToString();
    }

    // Rig root: origin, faces `facing` (flips X), rig→world `scale` (1 = raw rig units).
    private static Affine2 Root(float scale, int facing)
        => Affine2.FromTRS(Vector2.Zero, 0f, new Vector2((facing == 0 ? 1 : facing) * scale, scale));

    // World transforms of every bone at a phase, sampled with the game's C1 spline. No wrap —
    // pass keyframe times in [0,1] directly so t=1 reads the FINAL pose, not the loop seam.
    private static Affine2[] World(AnimationDocument clip, Skeleton rig, float phase, in Affine2 root)
    {
        var a = rig.CreatePose(); var b = rig.CreatePose(); var c = rig.CreatePose();
        var d = rig.CreatePose(); var dst = rig.CreatePose();
        AnimationSampler.SampleSmooth(clip, phase, a, b, c, d, dst);
        return dst.ComputeWorld(root);
    }

    // A semantic pose DIGEST: the derived, human-meaningful readouts an author actually needs —
    // ground line, planted feet, knee/elbow direction, hand height, torso lean — at each
    // keyframe, plus an assembled-clip trajectory summary that catches the in-between graze/bob
    // the keyframes hide. The interpretation layer on top of Report's raw coordinate table.
    // Auto-flags the failure mode the numbers make unambiguous (recurvatum / backward knee).
    // +x = forward, +y = DOWN; scale 1 = raw rig units.
    public static string Digest(AnimationDocument clip, Skeleton rig, float scale = 1f, int facing = 1)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# pose digest: '{clip.Name}'  Type={clip.Type}  Region={clip.Region}  "
                    + $"{clip.Keyframes.Count} keyframes  Loop={clip.Loop}");
        sb.AppendLine("# +x = forward, +y = DOWN.  ground = lowest point of the pose; "
                    + "'planted' = a tip within 1.0 of it.  C1 sampling (matches the game).");
        var root = Root(scale, facing);

        foreach (var kf in clip.Keyframes)
        {
            float t = MathHelper.Clamp(kf.Time, 0f, 1f);
            string contacts = kf.Contacts != null && kf.Contacts.Count > 0
                ? "  contacts=" + string.Join(",", kf.Contacts.ConvertAll(c => c.Node)) : "";
            sb.AppendLine();
            sb.AppendLine($"## t={t:0.00}{contacts}");
            DigestPose(sb, rig, World(clip, rig, t, root));
        }

        sb.AppendLine();
        sb.AppendLine("## trajectory (assembled clip, 24 samples)");
        TrajectorySummary(sb, clip, rig, root, 24);
        return sb.ToString();
    }

    // The per-pose readout block: ground line + lean, then each leg (knee direction via the
    // signed cross test, planted/clearance) and each arm (hand height bucket + front/back).
    private static void DigestPose(StringBuilder sb, Skeleton rig, Affine2[] w)
    {
        Vector2 J(string n) { int i = rig.IndexOf(n); return i < 0 ? Vector2.Zero : w[i].Translation; }
        Vector2 T(string n) { int i = rig.IndexOf(n); return i < 0 ? Vector2.Zero
                                  : w[i].TransformPoint(new Vector2(rig.Bones[i].Length, 0f)); }

        float ground = float.NegativeInfinity;
        for (int i = 0; i < rig.Count; i++)
        {
            ground = MathF.Max(ground, w[i].Translation.Y);
            ground = MathF.Max(ground, w[i].TransformPoint(new Vector2(rig.Bones[i].Length, 0f)).Y);
        }

        Vector2 hip = J("hip"), chestTop = T("chest"), headTop = T("head");
        sb.AppendLine($"  ground(y)={ground,6:0.0}   hip=({hip.X,5:0.0},{hip.Y,5:0.0})"
                    + $"   lean(chestTop.x-hip.x)={chestTop.X - hip.X,5:0.0}   headTop.y={headTop.Y,5:0.0}");

        var flags = new List<string>();
        foreach (var s in new[] { "l", "r" })
        {
            // Joint-chain rig: joints now align with anatomy — leg_*_upper.joint = hip,
            // leg_*_lower.joint = KNEE, foot_*.joint = ANKLE, foot_*.tip = TOE.
            Vector2 knee = J($"leg_{s}_lower"), ankle = J($"foot_{s}"), toe = T($"foot_{s}");
            Vector2 dd = ankle - hip; float L = dd.Length();
            // signed cross (knee-hip)×(ankle-hip): + = knee on the front side (correct), - = recurvatum.
            float side = L > 1e-4f ? ((knee.X - hip.X) * dd.Y - (knee.Y - hip.Y) * dd.X) / L : 0f;
            string dir = side > 0.2f ? "knee-FWD" : side < -0.2f ? "knee-RECURV" : "knee-straight";
            if (side < -0.2f) flags.Add($"leg_{s} recurvatum (backward knee)");
            bool planted = toe.Y >= ground - 1.0f;
            sb.AppendLine($"  leg_{s}: {dir,-13} toe=({toe.X,5:0.0},{toe.Y,5:0.0})  "
                        + $"clearance={ground - toe.Y,5:0.0}  {(planted ? "PLANTED" : "swing")}");
        }
        foreach (var s in new[] { "l", "r" })
        {
            Vector2 hand = T($"arm_{s}_lower");
            string lvl = hand.Y < headTop.Y ? "above-head" : hand.Y < chestTop.Y ? "head/chest"
                       : hand.Y < hip.Y ? "torso" : "below-hip";
            string fb = hand.X > hip.X + 1f ? "front" : hand.X < hip.X - 1f ? "back" : "center";
            sb.AppendLine($"  arm_{s}: hand=({hand.X,5:0.0},{hand.Y,5:0.0})  {lvl,-10} {fb}");
        }
        if (flags.Count > 0) sb.AppendLine("  FLAGS: " + string.Join("; ", flags));
    }

    // Assembled-clip trajectory: each foot tip's y-range (= bob if it's the planted foot; a clear
    // rise if it's the swing foot) and x-sweep, plus the cycle's lowest point. Catches the
    // in-between graze/bob a per-keyframe read can't see (skill principle #3).
    private static void TrajectorySummary(StringBuilder sb, AnimationDocument clip, Skeleton rig,
                                          in Affine2 root, int samples)
    {
        int fl = rig.IndexOf("foot_l"), fr = rig.IndexOf("foot_r");
        float lenL = fl >= 0 ? rig.Bones[fl].Length : 0f, lenR = fr >= 0 ? rig.Bones[fr].Length : 0f;
        float lyMin = 1e9f, lyMax = -1e9f, lxMin = 1e9f, lxMax = -1e9f;
        float ryMin = 1e9f, ryMax = -1e9f, rxMin = 1e9f, rxMax = -1e9f;
        float groundMax = -1e9f, groundPhase = 0f;
        for (int k = 0; k <= samples; k++)
        {
            float ph = (float)k / samples;
            var w = World(clip, rig, ph, root);
            if (fl >= 0) { var toe = w[fl].TransformPoint(new Vector2(lenL, 0f));
                lyMin = MathF.Min(lyMin, toe.Y); lyMax = MathF.Max(lyMax, toe.Y);
                lxMin = MathF.Min(lxMin, toe.X); lxMax = MathF.Max(lxMax, toe.X); }
            if (fr >= 0) { var toe = w[fr].TransformPoint(new Vector2(lenR, 0f));
                ryMin = MathF.Min(ryMin, toe.Y); ryMax = MathF.Max(ryMax, toe.Y);
                rxMin = MathF.Min(rxMin, toe.X); rxMax = MathF.Max(rxMax, toe.X); }
            float g = -1e9f;
            for (int i = 0; i < rig.Count; i++)
            {
                g = MathF.Max(g, w[i].Translation.Y);
                g = MathF.Max(g, w[i].TransformPoint(new Vector2(rig.Bones[i].Length, 0f)).Y);
            }
            if (g > groundMax) { groundMax = g; groundPhase = ph; }
        }
        sb.AppendLine($"  foot_l toe: y[{lyMin,5:0.0}..{lyMax,5:0.0}] (bob {lyMax - lyMin,4:0.0})  "
                    + $"x[{lxMin,5:0.0}..{lxMax,5:0.0}] (sweep {lxMax - lxMin,4:0.0})");
        sb.AppendLine($"  foot_r toe: y[{ryMin,5:0.0}..{ryMax,5:0.0}] (bob {ryMax - ryMin,4:0.0})  "
                    + $"x[{rxMin,5:0.0}..{rxMax,5:0.0}] (sweep {rxMax - rxMin,4:0.0})");
        sb.AppendLine($"  lowest point over cycle: y={groundMax,5:0.0} at phase {groundPhase:0.00}  "
                    + "(planted-foot bob should be ~0; the swing foot should show a clear y rise)");
    }

    // Per-keyframe DIFF of key tips vs a reference clip sampled at the same time — confirms a pose
    // actually departs from a baseline (the classic blind-authoring miss: "this reads the same as
    // idle / the rest pose"). Reports clip-minus-reference tip deltas for head/chest/hands/toes.
    public static string Diff(AnimationDocument clip, AnimationDocument reference, Skeleton rig,
                              float scale = 1f, int facing = 1)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# diff: '{clip.Name}' minus '{reference.Name}'   (Δtip, +x = fwd / +y = down)");
        var root = Root(scale, facing);
        string[] bones  = { "head", "chest", "arm_l_lower", "arm_r_lower", "foot_l", "foot_r" };
        string[] labels = { "headTop", "chestTop", "hand_l", "hand_r", "toe_l", "toe_r" };

        foreach (var kf in clip.Keyframes)
        {
            float t = MathHelper.Clamp(kf.Time, 0f, 1f);
            var wc = World(clip, rig, t, root);
            var wr = World(reference, rig, t, root);
            sb.AppendLine();
            sb.AppendLine($"## t={t:0.00}");
            for (int i = 0; i < bones.Length; i++)
            {
                int bi = rig.IndexOf(bones[i]);
                if (bi < 0) continue;
                var off = new Vector2(rig.Bones[bi].Length, 0f);
                Vector2 dc = wc[bi].TransformPoint(off) - wr[bi].TransformPoint(off);
                string note = dc.Length() < 1.0f ? "~same"
                    : MathF.Abs(dc.X) > MathF.Abs(dc.Y) ? (dc.X > 0 ? "forward" : "back")
                                                        : (dc.Y > 0 ? "lower" : "higher");
                sb.AppendLine($"  {labels[i],-9} Δ=({dc.X,6:0.0},{dc.Y,6:0.0})  |Δ|={dc.Length(),5:0.0}  {note}");
            }
        }
        return sb.ToString();
    }
}
