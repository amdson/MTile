using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;

namespace MTile;

// THE STEP-UP AUTHORING PILOT'S TOOLS (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Pilot one
// step-up scene" / "Downstream step-placement use"; workplan chunk 7). Two headless
// operations over a clip that carries contact labels:
//
//   Bake   — derive the clip's scene path p(t) from its planted feet: while a foot is in
//            stance its scene position F = p(t) + (q_f(t) − c(t)) is constant, so the body
//            moves by minus the foot's body-relative sweep; flight holds the last velocity.
//            Writes the body_path track (one Point per keyframe), sets Motion = Track and,
//            when the clip has no scene yet, guides that show the authored geometry: a ground
//            line at the lowest stance and one block per raised stance. Reproducible from
//            the clip's own labels — the source clip stays the input (the plan's rule).
//   Check  — the intent diagnostics: per stance, how far the planted foot drifts in scene
//            space; per swing, how deep the toe path dips into the scene's block guides or
//            under the ground line (a toe-point check, like the planner's — not a limb or
//            body-polygon proof).
public static class ClipSceneBake
{
    public sealed class BakeResult
    {
        public Vector2 CycleDisplacement;   // p(1) − p(0), rig units
        public int     Keys;                // body_path points written
        public int     Guides;              // scene guides created (0 when a scene already existed)
        public string  Warning;             // e.g. a loop without a t = 1 key
    }

    public sealed class StanceCheck
    {
        public string Node; public float Touchdown, Liftoff; public Vector2 Foot; public float Drift;
        public bool    OnCorner;   // the touchdown sole sits on a scene guide's exposed top corner
        public bool    Tagged;     // the span is already tagged Corner
    }
    // How close (rig units) a touchdown sole must be to a guide's corner to count as on it.
    public const float CornerEps = 3f;
    public sealed class SwingCheck  { public string Node; public float Start, End; public float MaxPenetration; public float MinClearance; public float AtU; }
    public sealed class CheckResult
    {
        public string Source;               // motion source in effect
        public Vector2 CycleDisplacement;
        public readonly List<StanceCheck> Stances = new();
        public readonly List<SwingCheck>  Swings  = new();
        public float MaxDrift;
        public float MaxPenetration;
    }

    private const int Grid = 128;
    private const float MinStepRise = 4f;   // rig units — smaller stance height differences are bob, not steps

    // Body-relative foot offset q_f(t) − c(t) (rig units, canonical facing) — the same
    // quantity ClipStrideTrack.OffsetAt compiles; kept here for the whole-clip walk.
    private sealed class Offsets
    {
        private readonly AnimationDocument _doc;
        private readonly SkeletonPose _a, _b, _c, _d, _dst;
        public Offsets(AnimationDocument doc, Skeleton rig)
        { _doc = doc; _a = rig.CreatePose(); _b = rig.CreatePose(); _c = rig.CreatePose(); _d = rig.CreatePose(); _dst = rig.CreatePose(); }
        public Vector2 At(int bone, float phase)
        {
            float p = phase - MathF.Floor(phase);
            if (phase >= 1f && p == 0f) p = 1f;   // the final endpoint, sampled explicitly
            AnimationSampler.SampleSmooth(_doc, p, _a, _b, _c, _d, _dst);
            var w = _dst.ComputeWorld(Affine2.Identity);
            Vector2 tip = w[bone].Translation;
            if (BodyPath.TrySampleAnchor(_doc, p, out var com, out _)) tip -= com;
            return tip;
        }
    }

    // The scene path on the grid (index i ↔ phase i/Grid), from the stance sweeps. A planted
    // interval moves the body by minus the foot's sweep (the mean over planted feet); flight
    // moves it at the cycle's MEAN stance velocity — the same constant rate the timing stage
    // paces by (ClipStrideTrack.CycleDisplacement), so the baked D equals the stance-sweep
    // displacement exactly and does not depend on the spline's tangents at liftoff.
    private static Vector2[] Walk(AnimationDocument doc, ClipStrideTrack gait, Offsets off)
    {
        var step = new Vector2[Grid + 1];
        var planted = new bool[Grid + 1];
        Vector2 sum = Vector2.Zero; int plantedSteps = 0;
        for (int i = 1; i <= Grid; i++)
        {
            float t0 = (i - 1) / (float)Grid, t1 = i / (float)Grid;
            Vector2 acc = Vector2.Zero; int n = 0;
            foreach (var f in gait.Feet)
            {
                int s0 = f.StanceAt(t0 - MathF.Floor(t0), out _), s1 = f.StanceAt(t1 >= 1f ? t1 - 1f + 1e-6f : t1, out _);
                if (s0 < 0 || s1 != s0) continue;   // not planted across this interval
                acc += -(off.At(f.Bone, t1) - off.At(f.Bone, t0)); n++;
            }
            if (n > 0) { step[i] = acc / n; planted[i] = true; sum += step[i]; plantedSteps++; }
        }
        Vector2 mean = plantedSteps > 0 ? sum / plantedSteps : Vector2.Zero;
        var p = new Vector2[Grid + 1];
        for (int i = 1; i <= Grid; i++) p[i] = p[i - 1] + (planted[i] ? step[i] : mean);
        return p;
    }

    private static Vector2 SampleGrid(Vector2[] p, float t)
    {
        float x = MathHelper.Clamp(t, 0f, 1f) * Grid;
        int i = Math.Min((int)x, Grid - 1);
        return Vector2.Lerp(p[i], p[i + 1], x - i);
    }

    // `flat`: keep the run only — a level gait's stance Y sweep is the legs' geometry and the
    // body's bob (the biped walk "sinks" 9 rig units per cycle by its sweeps), which the
    // runtime's vertical offset absorbs; it is not a path to author. A stair bakes in 2-D.
    public static bool TryBake(AnimationDocument doc, Skeleton rig, out BakeResult result, out string error, bool flat = false)
    {
        result = null;
        if (!ClipStrideTrack.TryCompile(doc, rig, out var gait, out error)) return false;
        if (gait.Feet.Length == 0) { error = $"'{doc.Name}' has no contact labels to bake a path from"; return false; }
        var off = new Offsets(doc, rig);
        var p = Walk(doc, gait, off);
        if (flat) for (int i = 0; i < p.Length; i++) p[i].Y = 0f;
        var r = new BakeResult { CycleDisplacement = p[Grid] - p[0] };

        bool hasEnd = false;
        foreach (var kf in doc.Keyframes)
        {
            kf.Additions ??= new List<AnimAddition>();
            kf.Additions.RemoveAll(a => a.Kind == AnimAdditionKind.Point && a.Name == BodyPath.ChannelName && a.Parent == null);
            var v = SampleGrid(p, kf.Time);
            kf.Additions.Add(new AnimAddition { Name = BodyPath.ChannelName, Kind = AnimAdditionKind.Point, Px = v.X, Py = v.Y });
            r.Keys++;
            if (kf.Time >= 1f - 1e-4f) hasEnd = true;
        }
        if (doc.Loop && !hasEnd)
            r.Warning = "loop without a t = 1 key: the body_path's cycle displacement stops at the last key";
        doc.Motion = MotionSource.Track;

        if (doc.Scene == null || doc.Scene.Guides.Count == 0)
        {
            // Scene guides from the stance geometry: every planted foot's scene position over
            // one cycle plus the next cycle's first, sorted along the path.
            var feet = new List<Vector2>();
            foreach (var f in gait.Feet)
                foreach (var st in f.Stances)
                {
                    if (st.Persistent) continue;
                    Vector2 F = SampleGrid(p, st.Touchdown) + off.At(f.Bone, st.Touchdown);
                    feet.Add(F);
                    if (doc.Loop) feet.Add(F + r.CycleDisplacement);
                }
            feet.Sort((a, b) => a.X.CompareTo(b.X));
            var scene = new ClipScene();
            float groundY = float.MinValue;
            foreach (var F in feet) groundY = MathF.Max(groundY, F.Y);
            SceneGuideOps.AddGround(scene, groundY); r.Guides++;
            for (int i = 0; i < feet.Count; i++)
            {
                var F = feet[i];
                if (F.Y > groundY - MinStepRise) continue;   // on the ground line (a flat gait's bob is not a step)
                float run = i > 0 ? F.X - feet[i - 1].X : (i + 1 < feet.Count ? feet[i + 1].X - F.X : 10f);
                bool last = i == feet.Count - 1;
                var g = SceneGuideOps.AddBlock(scene, F.X - 0.5f * run, F.Y, last ? 2f * run : run, groundY - F.Y);
                g.Label = $"step {r.Guides}";
                r.Guides++;
            }
            doc.Scene = scene;
        }
        result = r;
        return true;
    }

    public static bool TryCheck(AnimationDocument doc, Skeleton rig, out CheckResult result, out string error)
    {
        result = null;
        if (!ClipStrideTrack.TryCompile(doc, rig, out var gait, out error)) return false;
        if (gait.Feet.Length == 0) { error = $"'{doc.Name}' has no contact labels"; return false; }
        var motion = ClipMotion.Resolve(doc);
        var off = new Offsets(doc, rig);
        var r = new CheckResult { Source = motion.Source.ToString(), CycleDisplacement = motion.CycleDisplacement };
        Vector2 Scene(int bone, float t) => motion.ExtendedBodyAt(t) + off.At(bone, t);

        const int N = 32;
        foreach (var f in gait.Feet)
        {
            foreach (var st in f.Stances)
            {
                if (st.Persistent) continue;
                var c = new StanceCheck { Node = f.Node, Touchdown = st.Touchdown, Liftoff = st.Liftoff, Foot = Scene(f.Bone, st.Touchdown),
                                          Tagged = st.Corner, OnCorner = NearGuideCorner(doc, Scene(f.Bone, st.Touchdown)) };
                for (int i = 0; i <= N; i++)
                {
                    float t = st.Touchdown + (st.Liftoff - st.Touchdown) * i / N;
                    c.Drift = MathF.Max(c.Drift, (Scene(f.Bone, t) - c.Foot).Length());
                }
                r.Stances.Add(c); r.MaxDrift = MathF.Max(r.MaxDrift, c.Drift);
            }
            foreach (var sw in f.Swings)
            {
                var c = new SwingCheck { Node = f.Node, Start = sw.Start, End = sw.End, MinClearance = float.MaxValue };
                for (int i = 1; i < N; i++)
                {
                    float u = i / (float)N, t = sw.Start + (sw.End - sw.Start) * u;
                    Vector2 s = Scene(f.Bone, t);
                    float pen = 0f, clear = float.MaxValue;
                    if (doc.Scene != null)
                        foreach (var g in doc.Scene.Guides)
                        {
                            if (g.Hidden) continue;
                            if (g.Kind == SceneGuideKind.Ground) { pen = MathF.Max(pen, s.Y - g.Y); continue; }
                            if (s.X < g.X || s.X > g.X + g.W) continue;
                            clear = MathF.Min(clear, g.Y - s.Y);
                            if (s.Y > g.Y && s.Y < g.Y + g.H) pen = MathF.Max(pen, s.Y - g.Y);
                        }
                    if (pen > c.MaxPenetration) { c.MaxPenetration = pen; c.AtU = u; }
                    c.MinClearance = MathF.Min(c.MinClearance, clear);
                }
                if (c.MinClearance == float.MaxValue) c.MinClearance = float.NaN;
                r.Swings.Add(c); r.MaxPenetration = MathF.Max(r.MaxPenetration, c.MaxPenetration);
            }
        }
        result = r;
        return true;
    }

    // LIFT THE SWINGS — the pilot's re-authoring step. For every keyframe strictly inside a
    // swing of `node` (or every labeled foot when null), pose the limb so the toe follows a
    // climb-first scene path from the takeoff foot F0 to the landing foot F1:
    //     x(u) = F0.x + Δx · u^1.5                 (the run trails)
    //     y(u) = F0.y + Δy · (1 − (1 − u)^3)      (the rise leads, for a rising step)
    //            − lift · sin(πu)                  (clearance over the edge)
    // in scene space, converted back to a root-local IK target through the baked path and the
    // com anchor. Rotations of the limb chain only; stances, the other limbs and the path are
    // untouched. Requires a baked body_path (Motion = Track).
    public static bool TryLiftSwings(AnimationDocument doc, Skeleton rig, string node, float lift,
                                     out string report, out string error)
    {
        report = null;
        if (!ClipStrideTrack.TryCompile(doc, rig, out var gait, out error)) return false;
        var motion = ClipMotion.Resolve(doc);
        if (motion.Source != MotionSource.Track) { error = $"'{doc.Name}' has no baked body_path (run bakepath first)"; return false; }
        var off = new Offsets(doc, rig);
        var sb = new StringBuilder();
        var pose = rig.CreatePose();
        int edited = 0;
        foreach (var f in gait.Feet)
        {
            if (node != null && !string.Equals(f.Node, node, StringComparison.OrdinalIgnoreCase)) continue;
            int[] chain = PoseIk.DefaultChain(rig, f.Bone);
            if (chain.Length == 0) continue;
            foreach (var sw in f.Swings)
            {
                Vector2 F0 = motion.ExtendedBodyAt(sw.Start) + off.At(f.Bone, sw.Start);
                Vector2 F1 = motion.ExtendedBodyAt(sw.End)   + off.At(f.Bone, sw.End);
                Vector2 d = F1 - F0;
                foreach (var kf in doc.Keyframes)
                {
                    // Keys inside the swing, on either side of the loop seam.
                    float t = kf.Time;
                    if (t <= sw.Start - 1e-4f && doc.Loop) t += 1f;
                    if (t <= sw.Start + 1e-4f || t >= sw.End - 1e-4f) continue;
                    float u = (t - sw.Start) / (sw.End - sw.Start);
                    float ry = d.Y < 0f ? 1f - MathF.Pow(1f - u, 3f) : u;
                    Vector2 F = new(F0.X + d.X * MathF.Pow(u, 1.5f), F0.Y + d.Y * ry - lift * MathF.Sin(MathF.PI * u));
                    Vector2 bodyRel = F - motion.ExtendedBodyAt(t);
                    Vector2 target = bodyRel;
                    if (BodyPath.TrySampleAnchor(doc, kf.Time, out var com, out _)) target += com;   // root-local
                    PoseData.Apply(kf.Bones, pose);
                    var res = PoseIk.Solve(rig, pose, f.Bone, target, chain, priorWeight: 0.2f);
                    kf.Bones = PoseData.Capture(pose);
                    edited++;
                    sb.AppendLine($"  {f.Node} key t={kf.Time:0.00} (u={u:0.00}) toe → scene ({F.X:0.0},{F.Y:0.0}) miss {res.Miss:0.00} rig");
                }
            }
        }
        if (edited == 0) { error = "no keyframe lies inside a swing"; return false; }
        report = sb.ToString();
        return true;
    }

    // Is `p` (scene, rig units) on an exposed top corner of a block guide — a corner not
    // covered by another block's top at the same height?
    public static bool NearGuideCorner(AnimationDocument doc, Vector2 p)
    {
        var guides = doc?.Scene?.Guides;
        if (guides == null) return false;
        foreach (var g in guides)
        {
            if (g.Kind != SceneGuideKind.Block) continue;
            foreach (float cx in new[] { g.X, g.X + g.W })
            {
                if (MathF.Abs(p.X - cx) > CornerEps || MathF.Abs(p.Y - g.Y) > CornerEps) continue;
                // Exposed: no other block's top runs through this corner at the same height.
                bool covered = false;
                foreach (var o in guides)
                    if (!ReferenceEquals(o, g) && o.Kind == SceneGuideKind.Block && MathF.Abs(o.Y - g.Y) < 1e-3f
                        && cx > o.X + 1e-3f && cx < o.X + o.W - 1e-3f) { covered = true; break; }
                if (!covered) return true;
            }
        }
        return false;
    }

    // Write Corner on every span whose touchdown sole the check found on a guide corner.
    // Returns the number of spans newly tagged.
    public static int TagCorners(AnimationDocument doc, CheckResult check)
    {
        int n = 0;
        if (doc?.Contacts == null) return 0;
        foreach (var s in check.Stances)
        {
            if (!s.OnCorner) continue;
            foreach (var cs in doc.Contacts)
                if (cs.Point == s.Node && MathF.Abs(cs.Start - s.Touchdown) < 1e-4f && !cs.Corner) { cs.Corner = true; n++; }
        }
        return n;
    }

    public static string Describe(CheckResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"motion {r.Source}: cycle displacement D = ({r.CycleDisplacement.X:0.0}, {r.CycleDisplacement.Y:0.0}) rig");
        foreach (var s in r.Stances)
            sb.AppendLine($"  stance {s.Node,-9} [{s.Touchdown:0.00}, {s.Liftoff:0.00})  foot=({s.Foot.X:0.0},{s.Foot.Y:0.0})  drift {s.Drift:0.00} rig"
                          + (s.Tagged ? "  corner" : s.OnCorner ? "  on a corner (untagged)" : ""));
        foreach (var s in r.Swings)
            sb.AppendLine($"  swing  {s.Node,-9} [{s.Start:0.00}, {s.End:0.00})  block penetration {s.MaxPenetration:0.00} rig"
                          + (s.MaxPenetration > 0f ? $" at u={s.AtU:0.00}" : "")
                          + (float.IsNaN(s.MinClearance) ? "" : $"  min clearance over blocks {s.MinClearance:0.00} rig"));
        return sb.ToString();
    }
}
