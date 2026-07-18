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
        public readonly Vector2 Joint;    // the bone's NEAR joint = its parent's tip (= parent's world translation)
        public readonly Vector2 Tip;      // the bone's FAR tip = its world translation (where its children attach)
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

        var (joint, tip) = Eval(clip, rig, b, phase, root, a, bk, c, d, dst);
        const float e = 1e-3f;
        var (_, tipPlus)  = Eval(clip, rig, b, phase + e, root, a, bk, c, d, dst);
        var (_, tipMinus) = Eval(clip, rig, b, phase - e, root, a, bk, c, d, dst);
        return new JointSample(joint, tip, (tipPlus - tipMinus) / (2f * e));
    }

    private static (Vector2 joint, Vector2 tip) Eval(
        AnimationDocument clip, Skeleton rig, int b, float phase, in Affine2 root,
        SkeletonPose a, SkeletonPose bk, SkeletonPose c, SkeletonPose d, SkeletonPose dst)
    {
        float p = phase - MathF.Floor(phase);                    // wrap into [0,1) for looping clips
        AnimationSampler.SampleSmooth(clip, p, a, bk, c, d, dst); // C1 spline — matches the game
        var w = dst.ComputeWorld(root);
        // R·T·S joint chain: a bone's far tip IS its world translation; its near joint is its
        // parent's tip (parent's translation). Do NOT add Length along +X — that overshoots a bone.
        int par = rig.Bones[b].Parent;
        Vector2 joint = par >= 0 ? w[par].Translation : w[b].Translation;
        return (joint, w[b].Translation);
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
        if (AnimationSampler.SeamMismatch(clip, out string seamBone, out float seamDelta))
            sb.AppendLine($"# FLAGS: SEAM MISMATCH — looping {clip.Type} clip's first/last keyframe poses "
                        + $"differ ({seamBone} by {seamDelta:0.000} rad). The loop degrades to one-shot seam "
                        + "semantics: pose pop + cadence stall every stride. Make the last keyframe an exact "
                        + "copy of the first, OR move it before t=1 (open-tail loop: the sampler wraps "
                        + "last→first over the remaining phase).");

        // STEEP intervals: |Δrotation| / interval-width far above a natural swing rate reads
        // as a single-frame limb snap at locomotion phase rates (~0.07 phase/frame ⇒ a
        // 15 rad/phase ramp is ~1 rad in ONE frame). Walk's steepest authored interval is
        // ~4 rad/phase; flag anything ≥ 8 so staircased keyframes (hold → whip) get retimed.
        // For an open-tail loop the wrap segment [last, first+1] is a real interval too —
        // include it in the scan (i = count-1 pairs last with first at +1 cycle).
        bool openTail = AnimationSampler.IsCyclic(clip) && AnimationSampler.HasOpenTail(clip);
        int pairs = clip.Keyframes.Count - 1 + (openTail ? 1 : 0);
        for (int i = 0; i < pairs; i++)
        {
            var ka = clip.Keyframes[i];
            bool wrap = i + 1 == clip.Keyframes.Count;
            var kb = wrap ? clip.Keyframes[0] : clip.Keyframes[i + 1];
            float w = wrap ? kb.Time + 1f - ka.Time : kb.Time - ka.Time;
            if (w <= 1e-5f || ka.Bones == null || kb.Bones == null) continue;
            float maxD = 0f; string worst = null;
            foreach (var ea in ka.Bones)
            {
                if (ea.Bone == null) continue;
                foreach (var eb in kb.Bones)
                    if (eb.Bone == ea.Bone)
                    {
                        float d = MathF.Abs(MathHelper.WrapAngle(eb.Rotation - ea.Rotation));
                        if (d > maxD) { maxD = d; worst = ea.Bone; }
                        break;
                    }
            }
            float slope = maxD / w;
            if (slope >= 8f)
                sb.AppendLine($"# FLAGS: STEEP interval t={ka.Time:0.000}-{(wrap ? kb.Time + 1f : kb.Time):0.000}{(wrap ? " (wrap)" : "")} — {worst} moves "
                            + $"{maxD:0.000} rad over {w:0.000} phase ({slope:0.0} rad/phase; walk max ≈ 4). "
                            + "Reads as a limb snap at locomotion rates — spread the motion over more phase.");
        }
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
        // Under the R·T·S joint chain, world[i].Translation is bone i's FAR end — which is exactly
        // an anatomical landmark: leg_upper→KNEE, leg_lower→ANKLE, foot→TOE, chest→shoulders,
        // head→crown, arm_lower→HAND. A bone's near joint is its parent's far end.
        Vector2 P(string n) { int i = rig.IndexOf(n); return i < 0 ? Vector2.Zero : w[i].Translation; }

        float ground = float.NegativeInfinity;
        for (int i = 0; i < rig.Count; i++)
            ground = MathF.Max(ground, w[i].Translation.Y);

        Vector2 hip = P("hip"), chestTop = P("chest"), headTop = P("head");
        sb.AppendLine($"  ground(y)={ground,6:0.0}   hip=({hip.X,5:0.0},{hip.Y,5:0.0})"
                    + $"   lean(chestTop.x-hip.x)={chestTop.X - hip.X,5:0.0}   headTop.y={headTop.Y,5:0.0}");

        var flags = new List<string>();
        foreach (var s in new[] { "l", "r" })
        {
            // Anatomical landmarks are bone far ends: knee = leg_upper tip, ankle = leg_lower tip,
            // toe = foot tip.
            Vector2 knee = P($"leg_{s}_upper"), ankle = P($"leg_{s}_lower"), toe = P($"foot_{s}");
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
            Vector2 hand = P($"arm_{s}_lower");
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
        float lyMin = 1e9f, lyMax = -1e9f, lxMin = 1e9f, lxMax = -1e9f;
        float ryMin = 1e9f, ryMax = -1e9f, rxMin = 1e9f, rxMax = -1e9f;
        float groundMax = -1e9f, groundPhase = 0f;
        for (int k = 0; k <= samples; k++)
        {
            float ph = (float)k / samples;
            var w = World(clip, rig, ph, root);
            if (fl >= 0) { var toe = w[fl].Translation;
                lyMin = MathF.Min(lyMin, toe.Y); lyMax = MathF.Max(lyMax, toe.Y);
                lxMin = MathF.Min(lxMin, toe.X); lxMax = MathF.Max(lxMax, toe.X); }
            if (fr >= 0) { var toe = w[fr].Translation;
                ryMin = MathF.Min(ryMin, toe.Y); ryMax = MathF.Max(ryMax, toe.Y);
                rxMin = MathF.Min(rxMin, toe.X); rxMax = MathF.Max(rxMax, toe.X); }
            float g = -1e9f;
            for (int i = 0; i < rig.Count; i++)
                g = MathF.Max(g, w[i].Translation.Y);
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
                // tip = world translation (headTop/chestTop/hand/toe are these bones' far ends).
                Vector2 dc = wc[bi].Translation - wr[bi].Translation;
                string note = dc.Length() < 1.0f ? "~same"
                    : MathF.Abs(dc.X) > MathF.Abs(dc.Y) ? (dc.X > 0 ? "forward" : "back")
                                                        : (dc.Y > 0 ? "lower" : "higher");
                sb.AppendLine($"  {labels[i],-9} Δ=({dc.X,6:0.0},{dc.Y,6:0.0})  |Δ|={dc.Length(),5:0.0}  {note}");
            }
        }
        return sb.ToString();
    }
}
