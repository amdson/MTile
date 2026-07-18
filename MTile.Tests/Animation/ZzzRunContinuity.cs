using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// TEMP diagnostic probe (anim-probe workflow) — reproduce "large jerks / discontinuities
// while running". Runs the REAL animator per frame and records continuity metrics:
//   phase + Δphase          — cadence solver hops / freezes
//   clip                    — Run↔Fall/Walk thrash from grounded/speed flicker
//   max per-bone Δrotation  — pose pops in the final (eased, solved) pose
//   body-local landmark Δ   — the on-screen metric: how far a toe/hand/head moves
//                             per frame in the body's frame (big = visible pop)
//   rigRootRel + Δ          — the COM-anchored draw root relative to the body
//                             (a jump here shifts the WHOLE figure = worst jerk)
// Two harnesses: the real sim (hold right on flat ground, exactly the game's sample
// path) and a constant-velocity control (isolates the solver from sim noise).
// Reports to .probe/run_continuity_sim.md / run_continuity_const.md. Delete when done.
public class ZzzRunContinuity
{
    private const float Dt = 1f / 30f;
    private const float Scale = 0.6f;   // Game1.SkeletonScale

    private sealed class Trace
    {
        public readonly List<string> Rows = new();
        public readonly List<(int frame, float mag, string what)> Jerks = new();
        public readonly List<string> ClipSwitches = new();

        private CharacterAnimator _anim;
        private Skeleton _skel;
        private float[] _prevRot;
        private Vector2[] _prevLm;
        private Vector2 _prevRootRel;
        private AnimClip _prevClip;
        private float _prevPhase;
        private bool _has;
        private int[] _lmBones;
        private static readonly string[] LmNames = { "foot_l", "foot_r", "arm_l_lower", "arm_r_lower", "head" };

        public Trace(CharacterAnimator anim)
        {
            _anim = anim; _skel = anim.Skeleton;
            _prevRot = new float[_skel.Count];
            _prevLm = new Vector2[LmNames.Length];
            _lmBones = Array.ConvertAll(LmNames, n => _skel.IndexOf(n));
        }

        // rootRel: the draw root's offset from the body position (COM anchor + d.y), world px.
        public void Record(int f, float phase, bool grounded, Vector2 vel, int facing,
                           string state, Vector2 rootRel)
        {
            var clip = _anim.State.Clip;
            // Body-local screen frame: facing flip + SkeletonScale + the root offset, so
            // landmark deltas measure exactly what the eye sees minus smooth body motion.
            int dir = facing == 0 ? 1 : facing;
            var root = Affine2.FromTRS(rootRel, 0f, new Vector2(dir * Scale, Scale));
            var world = _anim.Pose.ComputeWorld(root);

            float maxRot = 0f; string maxBone = "-";
            for (int b = 0; b < _skel.Count; b++)
            {
                float r = _anim.Pose.Local[b].Rotation;
                if (_has)
                {
                    float d = MathF.Abs(MathHelper.WrapAngle(r - _prevRot[b]));
                    if (d > maxRot) { maxRot = d; maxBone = _skel.Bones[b].Name; }
                }
                _prevRot[b] = r;
            }

            float maxLm = 0f; string maxLmName = "-";
            var lmNow = new Vector2[_lmBones.Length];
            for (int i = 0; i < _lmBones.Length; i++)
            {
                lmNow[i] = world[_lmBones[i]].Translation;
                if (_has)
                {
                    float d = Vector2.Distance(lmNow[i], _prevLm[i]);
                    if (d > maxLm) { maxLm = d; maxLmName = LmNames[i]; }
                }
                _prevLm[i] = lmNow[i];
            }

            float dPhase = _has ? phase - _prevPhase : 0f;
            if (dPhase < -0.5f) dPhase += 1f;   // loop wrap is not a jerk
            float dRootRel = _has ? Vector2.Distance(rootRel, _prevRootRel) : 0f;

            if (_has && clip != _prevClip)
                ClipSwitches.Add($"f={f,4}  {_prevClip} -> {clip}  (phase {_prevPhase:0.000}->{phase:0.000}, grounded={grounded}, vy={vel.Y:0.0})");

            if (_has)
            {
                Rows.Add($"| {f,4} | {phase,6:0.000} | {dPhase,7:+0.000;-0.000} | {clip,-8} | {(grounded ? "g" : ".")} " +
                         $"| {vel.X,6:0.0} | {vel.Y,6:0.0} | {maxRot,6:0.000} {maxBone,-11} | {maxLm,6:0.00} {maxLmName,-11} | {dRootRel,6:0.00} | {state} |");
                Jerks.Add((f, maxLm, $"lm={maxLmName} rot={maxRot:0.000}@{maxBone} clip={clip} dphase={dPhase:+0.000;-0.000} droot={dRootRel:0.00} grounded={grounded} vy={vel.Y:0.0} state={state}"));
            }

            _prevClip = clip; _prevPhase = phase; _prevRootRel = rootRel; _has = true;
        }

        public string Report(string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.AppendLine("## Top 15 jerk frames (by body-local landmark delta, px/frame; body motion excluded)");
            Jerks.Sort((a, b) => b.mag.CompareTo(a.mag));
            for (int i = 0; i < Math.Min(15, Jerks.Count); i++)
                sb.AppendLine($"- f={Jerks[i].frame,4}  lmDelta={Jerks[i].mag,6:0.00}px  {Jerks[i].what}");
            sb.AppendLine();
            sb.AppendLine($"## Clip switches ({ClipSwitches.Count})");
            foreach (var s in ClipSwitches) sb.AppendLine("- " + s);
            sb.AppendLine();
            sb.AppendLine("## Full trace");
            sb.AppendLine("| f | phase | dphase | clip | gnd | vx | vy | maxRotDelta bone | maxLmDelta lm | dRootRel | state |");
            sb.AppendLine("|---|-------|--------|------|-----|----|----|------------------|---------------|----------|-------|");
            foreach (var r in Rows) sb.AppendLine(r);
            return sb.ToString();
        }
    }

    [Fact]
    public void SimDriven_HoldRight_Trace()
    {
        // 150-tile flat floor; hold right from rest — includes the idle->walk->run
        // spin-up and the steady run, i.e. exactly what the player experiences.
        var floor = new StringBuilder();
        for (int r = 0; r < 3; r++) floor.AppendLine(new string('X', 150));
        var terrain = SimTerrain.FromAscii(floor.ToString(), originTileY: 10);

        var anim = NewAnimator();
        var trace = new Trace(anim);

        var cfg = new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 360,
            Players = { new SimPlayer
            {
                StartPosition = new Vector2(40f, 140f),
                Script        = InputScript.Always(new PlayerInput { Right = true }),
            } },
        };
        SimRunner.RunMulti(cfg, onFrame: (f, players) =>
        {
            var p = players[0];
            anim.Update(CharacterAnimSample.From(p, Dt));
            Vector2 rootRel = AttackGlowSystem.RigRoot(p, anim, Scale) - p.Body.Position;
            trace.Record(f, anim.State.Phase, p.IsGrounded, p.Body.Velocity, p.Facing,
                         p.CurrentStateName, rootRel);
        });

        Dump("run_continuity_sim.md", trace.Report("Sim-driven hold-right (real sample path, 360 frames @30fps)"));
    }

    [Theory]
    [InlineData(90f)]    // steady run
    [InlineData(60f)]    // near the walk/run threshold
    public void ConstantVelocity_Trace(float vx)
    {
        var anim = NewAnimator();
        var trace = new Trace(anim);
        float x = 0f;
        for (int f = 0; f < 300; f++)
        {
            x += vx * Dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), +1, true, "RunningState", "", Dt));
            // No player here: root offset is just the animator's vertical bob.
            trace.Record(f, anim.State.Phase, true, new Vector2(vx, 0f), +1,
                         "RunningState", new Vector2(0f, anim.VerticalOffset));
        }
        Dump($"run_continuity_const_{(int)vx}.md",
             trace.Report($"Constant velocity vx={vx} (solver isolated, 300 frames @30fps)"));
    }

    // DECOMPOSITION: where do the limb-angle discontinuities come from? Three series of the
    // same bone rotation over the same frames:
    //   C = the animator's FINAL pose      (what the player sees: clip @ solved φ + Δθ + ease)
    //   A = RAW clip sampled at the SOLVED φ trace  (isolates the solver's φ dynamics)
    //   B = RAW clip sampled at UNIFORM φ  (mean rate — isolates the clip's own content)
    // If A carries the spikes, the Δφ hops are the cause; if C ≫ A, the Δθ channel adds them;
    // if even B is spiky, the clip content itself has near-step keyframes.
    [Fact]
    public void JerkDecomposition_ConstantRun()
    {
        var clips = AnimationStore.LoadAll(Path.Combine(FindUp("MTile.sln"), "SkeletonStates"));
        var run = clips.Find(d => d.Name == "run");
        Assert.True(run != null, "run.json not found");
        var skel = SkeletonExamples.Biped();
        var anim = new CharacterAnimator(skel, Scale, clips);

        string[] boneNames = { "leg_l_upper", "leg_r_upper", "leg_l_lower", "leg_r_lower" };
        int[] bones = Array.ConvertAll(boneNames, n => skel.IndexOf(n));

        const int N = 300; const float vx = 90f;
        var phi   = new float[N];
        var dphi  = new float[N];
        var final = new float[N, 4];
        float x = 0f, unwrapped = 0f, prevPhi = 0f;
        for (int f = 0; f < N; f++)
        {
            x += vx * Dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), +1, true, "RunningState", "", Dt));
            phi[f] = anim.State.Phase;
            float d = phi[f] - prevPhi; if (d < -0.5f) d += 1f;
            dphi[f] = f == 0 ? 0f : d;
            if (f > 0) unwrapped += dphi[f];
            prevPhi = phi[f];
            for (int b = 0; b < 4; b++) final[f, b] = anim.Pose.Local[bones[b]].Rotation;
        }
        float meanRate = unwrapped / (N - 1);

        // Raw-clip series at solved φ (A) and uniform φ (B).
        var pa = skel.CreatePose(); var pb = skel.CreatePose();
        var pc = skel.CreatePose(); var pd = skel.CreatePose(); var dest = skel.CreatePose();
        var rawSolved  = new float[N, 4];
        var rawUniform = new float[N, 4];
        for (int f = 0; f < N; f++)
        {
            AnimationSampler.SampleSmooth(run, phi[f], pa, pb, pc, pd, dest);
            for (int b = 0; b < 4; b++) rawSolved[f, b] = dest.Local[bones[b]].Rotation;
            float u = (f * meanRate) % 1f;
            AnimationSampler.SampleSmooth(run, u, pa, pb, pc, pd, dest);
            for (int b = 0; b < 4; b++) rawUniform[f, b] = dest.Local[bones[b]].Rotation;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Jerk decomposition, constant vx=90, 300 frames (deltas = |wrap(rot_f - rot_f-1)| rad/frame)");
        sb.AppendLine($"mean dphase = {meanRate:0.0000}");
        sb.AppendLine();
        sb.AppendLine("| bone | series | p50 | p90 | max | frames>0.4rad |");
        sb.AppendLine("|------|--------|-----|-----|-----|---------------|");
        var deltasC = new float[N]; // reused per bone/series for stats
        for (int b = 0; b < 4; b++)
        {
            foreach (var (name, src) in new (string, float[,])[] { ("C final", final), ("A raw@solvedPhi", rawSolved), ("B raw@uniformPhi", rawUniform) })
            {
                int over = 0;
                var ds = new List<float>();
                for (int f = 20; f < N; f++)
                {
                    float d = MathF.Abs(MathHelper.WrapAngle(src[f, b] - src[f - 1, b]));
                    ds.Add(d); if (d > 0.4f) over++;
                }
                ds.Sort();
                sb.AppendLine($"| {boneNames[b],-11} | {name,-16} | {ds[ds.Count / 2]:0.000} | {ds[(int)(ds.Count * 0.9)]:0.000} | {ds[^1]:0.000} | {over} |");
            }
        }

        // Top C spikes with the A delta at the same frame and the Δθ residual delta (C−A).
        sb.AppendLine();
        sb.AppendLine("## Top 12 final-pose spikes (leg_l_upper) with attribution");
        sb.AppendLine("| f | phase | dphase | dC | dA(raw@phi) | d(C-A)=Δθ motion |");
        sb.AppendLine("|---|-------|--------|----|-------------|------------------|");
        var spikes = new List<(int f, float dc)>();
        for (int f = 21; f < N; f++)
            spikes.Add((f, MathF.Abs(MathHelper.WrapAngle(final[f, 0] - final[f - 1, 0]))));
        spikes.Sort((p, q) => q.dc.CompareTo(p.dc));
        foreach (var (f, dc) in spikes.GetRange(0, 12))
        {
            float da = MathF.Abs(MathHelper.WrapAngle(rawSolved[f, 0] - rawSolved[f - 1, 0]));
            float resNow  = MathHelper.WrapAngle(final[f, 0] - rawSolved[f, 0]);
            float resPrev = MathHelper.WrapAngle(final[f - 1, 0] - rawSolved[f - 1, 0]);
            float dres = MathF.Abs(MathHelper.WrapAngle(resNow - resPrev));
            sb.AppendLine($"| {f,4} | {phi[f]:0.000} | {dphi[f]:+0.000;-0.000} | {dc:0.000} | {da:0.000} | {dres:0.000} |");
        }
        Dump("run_jerk_decomposition.md", sb.ToString());
    }

    private static CharacterAnimator NewAnimator()
        => new(SkeletonExamples.Biped(), Scale,
               AnimationStore.LoadAll(Path.Combine(FindUp("MTile.sln"), "SkeletonStates")));

    private static void Dump(string file, string content)
    {
        string dir = Path.Combine(FindUp("MTile.sln"), ".probe");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), content);
    }

    private static string FindUp(string marker)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null) { if (File.Exists(Path.Combine(d.FullName, marker))) return d.FullName; d = d.Parent; }
        throw new DirectoryNotFoundException(marker);
    }
}
