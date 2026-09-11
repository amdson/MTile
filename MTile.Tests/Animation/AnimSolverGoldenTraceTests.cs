using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Golden traces of the cadence/static solve's objective (workplan chunk 1.5's guard): for a set
// of scripted scenarios, the residual vector and dense analytic Jacobian at every recorded
// solve frame's accepted x — and at a fixed off-optimum perturbation of it — must be
// BIT-IDENTICAL to the recorded files. A behavior-preserving refactor of the constraint layer
// must not move a single float; anything that changes a residual, a Jacobian entry, or the LM
// result (x is recorded too) shows up here as the first differing frame/row/column.
//
// Regenerate (only when a change is MEANT to alter the objective):
//     MTILE_GOLDEN_RECORD=1 dotnet test MTile.Tests --filter FullyQualifiedName~AnimSolverGoldenTrace
// Files: MTile.Tests/Animation/Golden/<scenario>.bin.gz (gzip; header, then per frame
// frameIndex, n, m, x[n], r[m], J[m·n], r2[m], J2[m·n]).
//
// HISTORY: recorded before the chunk 1.5 refactor and replayed against it. The pre-refactor
// capture re-evaluated the objective after Update had overwritten the prior anchors (Δφ_prev,
// the emitted d) with the solved values, so the momentum row and the two com smoothness rows
// read 0 at x; the frozen SolveProblem keeps the anchors the solver actually used. Every other
// entry — x, both Jacobians, every other residual row — replayed bit-identical over 330 frames,
// and the three anchor rows matched the leak relation exactly; the files were then re-recorded
// from the faithful capture. biped_parkour_grip was re-recorded once more for the held-one-shot
// fix (SolveProblem.TimeAt): the arc-jump base clip is a 0.45 s clock-mode one-shot held at
// t = 1 through the grip, and the static solve used to correct its FIRST frame (Wrap01(1) = 0),
// so the hand needed 0.3–0.6 rad of Δθ to reach a corner 1.5 px away; it now needs ~1e-4.
// Re-recorded again for the timing stage (chunk 5, T1): the four locomotion scenarios changed
// by design — Δφ is now locked at 0 in the joint solve (the row layout is unchanged, the
// momentum/floor rows read 0), contacts are captured at the phase the timing stage produced,
// and the solved x has no Δφ component. Re-recorded once more for T5: the momentum and
// rate-floor rows (both identically zero since T1) were deleted, so every scenario's row
// layout shrank by two; nothing else changed. Re-recorded for chunk 4's helper-feet removal:
// the biped rig lost foot_l/foot_r (two fewer Δθ variables, two fewer no-pen rows per
// surface), its planted contacts moved 0.15 px onto the lower legs' ends (support_l/r), and
// the terrain tips are enumerated in a different order (support points appended), which
// reorders the no-pen rows — the rabbit scenarios changed only by that ordering.
public class AnimSolverGoldenTraceTests
{
    private readonly ITestOutputHelper _o;
    public AnimSolverGoldenTraceTests(ITestOutputHelper o) => _o = o;

    private const float Scale = 0.6f;
    private const float Dt = Simulation.FixedDt;
    private const int TS = Chunk.TileSize;
    private const string Magic = "MTGT1";

    // Scenario = (rig, frames driven through the animator, which frames to record).
    // Sim-driven ones run the game's exact render-side path (sim.Step → TerrainSurfaces.Extract
    // → CharacterAnimSample.From → anim.Update), the same loop MTile.Bench --anim-baseline uses.
    [Theory]
    [InlineData("biped_stairs_run")]          // planted contacts + planner swing targets + terrain no-pen
    [InlineData("biped_rabbit_stairs_run")]   // same on the stretch-animated rig (the ṫ_j channel)
    [InlineData("biped_slash_while_running")] // action overlay (Π(1−w) attenuation) + aim row
    [InlineData("biped_rabbit_low_ceiling")]  // low-ceiling driver override of the com weight
    [InlineData("biped_parkour_grip")]        // static solve: hard pin through an overlay-owned bone
    [InlineData("biped_wall_slide")]          // static solve: half-plane no-pen off locomotion
    public void Objective_MatchesGoldenTrace(string scenario)
    {
        var frames = Record(scenario);
        Assert.True(frames.Count > 0, $"{scenario}: no solve frames were recorded");
        string path = GoldenPath(scenario);
        bool record = Environment.GetEnvironmentVariable("MTILE_GOLDEN_RECORD") == "1";
        if (record || !File.Exists(path))
        {
            Assert.True(record, $"{scenario}: golden file missing at {path} — run with MTILE_GOLDEN_RECORD=1 to create it");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Save(path, frames);
            _o.WriteLine($"recorded {frames.Count} frames → {path} ({new FileInfo(path).Length / 1024} KB)");
            return;
        }
        var golden = Load(path);
        Assert.True(golden.Count == frames.Count,
            $"{scenario}: {frames.Count} solve frames recorded vs {golden.Count} in the golden file (solve set changed)");
        for (int i = 0; i < golden.Count; i++)
        {
            var (gf, gt) = golden[i]; var (cf, ct) = frames[i];
            Assert.True(gf == cf, $"{scenario}: frame index {cf} vs golden {gf} at record {i}");
            Assert.True(gt.N == ct.N && gt.M == ct.M, $"{scenario} f{cf}: layout n={ct.N} m={ct.M} vs golden n={gt.N} m={gt.M}");
            var diffs = new List<string>();
            Same("x",  gt.X,  ct.X,  ct, diffs);
            Same("r",  gt.R,  ct.R,  ct, diffs);
            Same("J",  gt.J,  ct.J,  ct, diffs);
            Same("r2", gt.R2, ct.R2, ct, diffs);
            Same("J2", gt.J2, ct.J2, ct, diffs);
            if (diffs.Count > 0)
                Assert.Fail($"{scenario} f{cf} differs from the golden trace:\n" + string.Join("\n", diffs));
        }
        _o.WriteLine($"{scenario}: {frames.Count} frames bit-identical");
    }

    // Exact comparison; every differing entry of this array (block-labelled, capped) so the
    // failure says WHICH rows moved across all five arrays, not just the first entry.
    private static void Same(string what, float[] g, float[] c, CharacterAnimator.SolveTrace t, List<string> diffs)
    {
        if (g.Length != c.Length) { diffs.Add($"  {what}: length {c.Length} vs golden {g.Length}"); return; }
        int shown = 0;
        for (int i = 0; i < g.Length; i++)
            if (g[i] != c[i] || float.IsNaN(c[i]))
            {
                if (shown++ >= 16) { diffs.Add($"  {what}: …more"); break; }
                int row = what.StartsWith("J") ? i / t.N : i;
                string where = what.StartsWith("J") ? $"row {row} col {i % t.N}" : $"row {row}";
                diffs.Add($"  {what} {where} [{t.BlockOf(row)}]: golden {g[i]:R} vs current {c[i]:R}");
            }
    }

    // ------------------------------------------------------------------ scenarios

    private List<(int Frame, CharacterAnimator.SolveTrace Trace)> Record(string scenario)
    {
        var (rig, frames, from, to, stride) = scenario switch
        {
            "biped_stairs_run"          => ("biped",        Sim(Stairs(), new Vector2(1.5f * TS, 15 * TS - PlayerCharacter.Radius), (f, p) => Right, 200), 30, 200, 2),
            "biped_rabbit_stairs_run"   => ("biped_rabbit", Sim(Stairs(), new Vector2(1.5f * TS, 15 * TS - PlayerCharacter.Radius), (f, p) => Right, 200), 30, 200, 2),
            "biped_slash_while_running" => ("biped",        Sim(Flat(), FlatSpawn, Slash, 300), 175, 300, 2),
            "biped_rabbit_low_ceiling"  => ("biped_rabbit", Sim(Corridor(), new Vector2(24f, 14 * TS - PlayerCharacter.Radius), (f, p) => Right, 300), 60, 300, 2),
            "biped_parkour_grip"        => ("biped",        ParkourGrip(), 24, 48, 1),
            "biped_wall_slide"          => ("biped",        WallSlide(), 20, 50, 1),
            _ => throw new ArgumentException(scenario),
        };
        var clips = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), rig));
        var anim = new CharacterAnimator(SkeletonExamples.Load(rig), Scale, clips);
        var outp = new List<(int, CharacterAnimator.SolveTrace)>();
        int i = 0;
        foreach (var s in frames(anim))
        {
            anim.Update(s);
            if (i >= from && i < to && (i - from) % stride == 0)
            {
                var t = anim.CaptureSolveTrace();
                if (t != null) outp.Add((i, t));
            }
            i++;
        }
        return outp;
    }

    private delegate IEnumerable<CharacterAnimSample> Frames(CharacterAnimator anim);

    private static readonly PlayerInput Right = new() { Right = true };
    private static readonly Vector2 FlatSpawn = new(3.5f * TS, 10 * TS - PlayerCharacter.Radius);
    private static PlayerInput Slash(int f, PlayerCharacter p) => new()
    {
        Right = true,
        LeftClick = f >= 180 && (f - 180) % 45 < 1,
        MouseWorldPosition = p.Body.Position + new Vector2(60f * (p.Facing == 0 ? 1 : p.Facing), 0f),
    };

    private static Frames Sim(ChunkMap chunks, Vector2 spawn, Func<int, PlayerCharacter, PlayerInput> input, int frames)
        => anim => SimFrames(anim, chunks, spawn, input, frames);

    private static IEnumerable<CharacterAnimSample> SimFrames(CharacterAnimator anim, ChunkMap chunks, Vector2 spawn,
                                                              Func<int, PlayerCharacter, PlayerInput> input, int frames)
    {
        var sim = new Simulation(chunks, spawn);
        var predictor = new LatticePathSampler();
        var surfaces = new SolverSurface[8];
        for (int f = 0; f < frames; f++)
        {
            var p = sim.Player;
            sim.Step(input(f, p));
            p = sim.Player;
            int tc = TerrainSurfaces.Extract(sim.Chunks, anim, p.Body.Position, p.Facing, Scale, surfaces, out bool near);
            predictor.Bind(p);
            yield return CharacterAnimSample.From(p, Dt, surfaces, tc, near, sim.Chunks, predictor.PredictAt);
        }
    }

    // ParkourGripSolverTests' fixture: 24 warm-up frames of the composed vault pose, then the
    // hand pinned to a corner 1.5px off its natural spot (a hard pin on an overlay-owned bone).
    private static Frames ParkourGrip() => anim => ParkourGripFrames(anim);
    private static IEnumerable<CharacterAnimSample> ParkourGripFrames(CharacterAnimator anim)
    {
        const float dt = 1f / 30f, progress = 0.6f;
        var pos = Vector2.Zero;
        int hand = anim.Skeleton.IndexOf("arm_l_lower");
        for (int i = 0; i < 24; i++)
            yield return new CharacterAnimSample(pos, Vector2.Zero, +1, false, "ArcJumpState", "", dt,
                tag: AnimTag.ArcJump, movementProgress: progress);
        anim.TryComReference(out var comL);
        var root = Affine2.FromTRS(pos + BodyPath.RootOffset(comL, +1, Scale), 0f, new Vector2(Scale, Scale));
        var corner = anim.Pose.ComputeWorld(root)[hand].Translation + new Vector2(1.5f, -1.5f);
        for (int i = 0; i < 24; i++)
            yield return new CharacterAnimSample(pos, Vector2.Zero, +1, false, "ArcJumpState", "", dt,
                tag: AnimTag.ArcJump, movementProgress: progress, hasGrip: true, gripTarget: corner);
    }

    // NoPenetrationSolverTests' fixture: the wall-slide pose settles, then a wall plane cuts
    // 1.5px into the outward hand (static solve, half-plane rows).
    private static Frames WallSlide() => anim => WallSlideFrames(anim);
    private static IEnumerable<CharacterAnimSample> WallSlideFrames(CharacterAnimator anim)
    {
        const float dt = 1f / 30f;
        var pos = Vector2.Zero;
        int hand = anim.Skeleton.IndexOf("arm_l_lower");
        for (int i = 0; i < 20; i++)
            yield return new CharacterAnimSample(pos, Vector2.Zero, +1, false, "WallSlidingState", "", dt, tag: AnimTag.WallSlide);
        anim.TryComReference(out var comL);
        var root = Affine2.FromTRS(pos + BodyPath.RootOffset(comL, +1, Scale), 0f, new Vector2(Scale, Scale));
        float wallX = anim.Pose.ComputeWorld(root)[hand].Translation.X - 1.5f;
        var surf = new SolverSurface(new Vector2(wallX, 0f), new Vector2(-1f, 0f), 0.5f);
        for (int i = 0; i < 30; i++)
            yield return new CharacterAnimSample(pos, Vector2.Zero, +1, false, "WallSlidingState", "", dt,
                tag: AnimTag.WallSlide, surfaces: new[] { surf });
    }

    // Terrain (MTile.Bench AnimBaseline's shapes): thick floors so the thin-floor underside
    // pathology stays out of the trace.
    private static ChunkMap Flat() => Build(18, 300, (r, c) => r >= 10);
    private static ChunkMap Stairs() => Build(24, 60, (r, c) => r >= 15 - Math.Clamp(c - 7, 0, 10));
    private static ChunkMap Corridor() => Build(22, 64, (r, c) =>
    {
        bool tunnel = c >= 16;
        return r >= 14 || (r <= 10 && tunnel) || (r == 11 && tunnel && c % 4 == 3) || (r == 13 && tunnel && c % 4 == 1);
    });
    private static ChunkMap Build(int h, int w, Func<int, int, bool> solid)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++) sb.Append(solid(r, c) ? 'X' : 'O');
            if (r < h - 1) sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: 0);
    }

    // ------------------------------------------------------------------ golden I/O

    private static void Save(string path, List<(int Frame, CharacterAnimator.SolveTrace Trace)> frames)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new BinaryWriter(gz);
        w.Write(Magic);
        w.Write(frames.Count);
        foreach (var (f, t) in frames)
        {
            w.Write(f); w.Write(t.N); w.Write(t.M);
            foreach (var arr in new[] { t.X, t.R, t.J, t.R2, t.J2 })
                foreach (float v in arr) w.Write(v);
        }
    }

    private static List<(int Frame, CharacterAnimator.SolveTrace Trace)> Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz);
        Assert.Equal(Magic, r.ReadString());
        int count = r.ReadInt32();
        var outp = new List<(int, CharacterAnimator.SolveTrace)>(count);
        for (int k = 0; k < count; k++)
        {
            int f = r.ReadInt32(), n = r.ReadInt32(), m = r.ReadInt32();
            var t = new CharacterAnimator.SolveTrace { N = n, M = m, X = Read(r, n), R = Read(r, m), J = Read(r, m * n), R2 = Read(r, m), J2 = Read(r, m * n) };
            outp.Add((f, t));
        }
        return outp;
    }

    private static float[] Read(BinaryReader r, int count)
    {
        var a = new float[count];
        for (int i = 0; i < count; i++) a[i] = r.ReadSingle();
        return a;
    }

    private static string GoldenPath(string scenario)
        => Path.Combine(FindDir("MTile.Tests"), "Animation", "Golden", scenario + ".bin.gz");

    private static string FindDir(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, name);
            if (Directory.Exists(c) && (name != "MTile.Tests" || Directory.Exists(Path.Combine(c, "Animation")))) return c;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException(name);
    }
}
