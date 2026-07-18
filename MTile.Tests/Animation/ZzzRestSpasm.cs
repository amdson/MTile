using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// TEMP probe — delete when done. Reproduces "spastic skeleton fluctuations at rest":
// idle body held perfectly still with feet embedded a few px in the floor (the real
// resting misalignment), full game-loop pattern (TerrainSurfaces.Extract from LAST
// frame's pose each tick, then Update). Measures frame-to-frame emitted-pose jitter
// after settling — at true rest this should be ~0; spasms show up as sustained large
// per-frame angle/tip deltas. Sweeps embed depth. Report: .probe/rest_spasm.md
public class ZzzRestSpasm
{
    private readonly ITestOutputHelper _o;
    public ZzzRestSpasm(ITestOutputHelper o) => _o = o;

    private const float Dt = Simulation.FixedDt;
    private const float Scale = 0.6f;
    private const float GroundY = 160f;          // originTileY 10 → floor top face

    [Fact]
    public void Probe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Rest-state spasm probe — idle, still body, feet embedded N px in floor");
        sb.AppendLine("# jitter = max |Δrotation| between consecutive EMITTED poses (rad/frame), after 60-frame settle");
        sb.AppendLine();

        var chunks = FloorMap();

        // Natural lowest foot-tip Y for an idle stance at a reference body position —
        // measured with NO terrain so we can place the body to embed by exactly N px.
        float natToe = NaturalToeY(new Vector2(100f, 100f));
        sb.AppendLine($"natural toe Y at body(100,100): {natToe:0.00}  (body→toe drop {natToe - 100f:0.00}px)");
        sb.AppendLine();

        foreach (float embed in new[] { 0f, 1f, 2f, 4f, 6f, 8f })
        {
            var pos = new Vector2(100f, 100f + (GroundY + embed - natToe));
            var anim = NewAnimator();
            // settle with no terrain first (mimics arriving at rest)
            for (int i = 0; i < 20; i++)
                anim.Update(Sample(pos, null, 0, false));

            var buf = new SolverSurface[8];
            var prev = new float[anim.Skeleton.Count];
            for (int k = 0; k < prev.Length; k++) prev[k] = anim.Pose.Local[k].Rotation;

            float worstJit = 0f; int worstFrame = -1;
            float sustained = 0f;              // max jitter in the LAST 2 seconds (steady limit cycle?)
            int bigJumps = 0;                  // frames with any bone moving > 0.3 rad in one tick
            float worstToePen = 0f, worstToeLift = 0f;
            var trace = new StringBuilder();

            const int Frames = 600;            // 10 s
            for (int f = 0; f < Frames; f++)
            {
                int tc = TerrainSurfaces.Extract(chunks, anim, pos, +1, Scale, buf, out bool near);
                anim.Update(Sample(pos, buf, tc, near));

                float jit = 0f; int jitBone = -1;
                for (int k = 0; k < prev.Length; k++)
                {
                    float d = MathF.Abs(WrapAngle(anim.Pose.Local[k].Rotation - prev[k]));
                    if (d > jit) { jit = d; jitBone = k; }
                    prev[k] = anim.Pose.Local[k].Rotation;
                }
                if (f >= 60)
                {
                    if (jit > worstJit) { worstJit = jit; worstFrame = f; }
                    if (f >= Frames - 120) sustained = MathF.Max(sustained, jit);
                    if (jit > 0.3f)
                    {
                        bigJumps++;
                        if (bigJumps <= 25)
                            trace.AppendLine($"  f={f} jit={jit:0.000} bone={anim.Skeleton.Bones[jitBone].Name} planes={tc} near={near} toeY={ToeY(anim, pos):0.0}");
                    }
                    float toe = ToeY(anim, pos);
                    worstToePen  = MathF.Max(worstToePen, toe - GroundY);
                    worstToeLift = MathF.Max(worstToeLift, GroundY - toe);
                }
            }

            string line = $"embed={embed,3:0}px | worstJit={worstJit:0.000} rad (f={worstFrame}) sustained(last2s)={sustained:0.000} bigJumps(>0.3)={bigJumps}/540 | toe: pen={worstToePen:0.0} lift={worstToeLift:0.0}";
            sb.AppendLine(line);
            if (trace.Length > 0) sb.AppendLine(trace.ToString());
            _o.WriteLine(line);
        }

        string outDir = Path.Combine(FindUp("MTile.sln"), ".probe");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "rest_spasm.md"), sb.ToString());
    }

    // Sim-driven scenarios: the spasm may need dynamics (arriving at rest, corners,
    // walls) rather than a statically violated plane. Exact game-loop pattern:
    // Extract from LAST pose → Update, every sim frame.
    [Fact]
    public void Scenarios()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sim scenario spasm probe — jitter = max |Δrot| per frame (rad), measured after frame 30");
        sb.AppendLine();

        var floor = FloorMap200();
        var wallMap = WallMap(out float wallX);
        var ledgeMap = LedgeMap();
        var stairMap = StairMap();

        RunScenario(sb, "run-stop", floor, new Vector2(60f, 140f),
            new InputScript().Then(new PlayerInput { Right = true }).For(120).Then(default(PlayerInput)).Forever(), 600);
        RunScenario(sb, "run-stop-left", floor, new Vector2(400f, 140f),
            new InputScript().Then(new PlayerInput { Left = true }).For(120).Then(default(PlayerInput)).Forever(), 600);
        RunScenario(sb, "wall-push", wallMap, new Vector2(wallX - 120f, 140f),
            InputScript.Always(new PlayerInput { Right = true }), 600);
        RunScenario(sb, "wall-stop", wallMap, new Vector2(wallX - 120f, 140f),
            new InputScript().Then(new PlayerInput { Right = true }).For(120).Then(default(PlayerInput)).Forever(), 600);
        RunScenario(sb, "ledge-stand", ledgeMap, new Vector2(154f, 140f),
            InputScript.Always(default(PlayerInput)), 600);
        RunScenario(sb, "ledge-approach", ledgeMap, new Vector2(60f, 140f),
            new InputScript().Then(new PlayerInput { Right = true }).For(28).Then(default(PlayerInput)).Forever(), 600);
        RunScenario(sb, "stair-run", stairMap, new Vector2(40f, 140f),
            InputScript.Always(new PlayerInput { Right = true }), 600);
        RunScenario(sb, "stair-stop", stairMap, new Vector2(40f, 140f),
            new InputScript().Then(new PlayerInput { Right = true }).For(90).Then(default(PlayerInput)).Forever(), 600);
        RunScenario(sb, "step-stand", stairMap, new Vector2(16f * 12f - 4f, 140f),
            InputScript.Always(default(PlayerInput)), 600);
        RunScenario(sb, "drop-land", floor, new Vector2(300f, 40f),
            InputScript.Always(default(PlayerInput)), 600);

        string outDir = Path.Combine(FindUp("MTile.sln"), ".probe");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "rest_spasm_scenarios.md"), sb.ToString());
    }

    private void RunScenario(StringBuilder sb, string name, ChunkMap chunks, Vector2 start,
                             InputScript script, int frames)
    {
        var anim = NewAnimator();      // guarded (terrain planes, game loop)
        var ctrl = NewAnimator();      // control (no terrain surfaces)
        var buf = new SolverSurface[8];
        float[] prev = null, prevC = null;
        Vector2[] prevTip = null, prevTipC = null;   // world tip positions (all bones)
        float worstJit = 0f; int worstFrame = -1; string worstState = "";
        float worstJitC = 0f; int worstFrameC = -1;
        float worstPx = 0f, worstPxC = 0f; int worstPxFrame = -1;
        int bigJumps = 0, bigJumpsC = 0;
        var trace = new StringBuilder();

        var cfg = new SimConfigMulti
        {
            Terrain = chunks,
            Frames  = frames,
            Players = { new SimPlayer { StartPosition = start, Script = script } },
        };
        SimRunner.RunMulti(cfg, onFrame: (f, players) =>
        {
            var p = players[0];
            int tc = TerrainSurfaces.Extract(chunks, anim, p.Body.Position, p.Facing, Scale, buf, out bool near);
            anim.Update(CharacterAnimSample.From(p, Dt, buf, tc, near));
            ctrl.Update(CharacterAnimSample.From(p, Dt));

            // World tips in a BODY-RELATIVE frame (root from RigRoot minus body pos), so
            // body translation itself doesn't count as jitter but solved root offsets DO.
            var wg = WorldTips(anim, p);
            var wc = WorldTips(ctrl, p);
            if (prev == null)
            {
                prev  = new float[anim.Skeleton.Count];
                prevC = new float[anim.Skeleton.Count];
                for (int k = 0; k < prev.Length; k++)
                { prev[k] = anim.Pose.Local[k].Rotation; prevC[k] = ctrl.Pose.Local[k].Rotation; }
                prevTip = wg; prevTipC = wc;
                return;
            }
            float jit = 0f, jitC = 0f, px = 0f, pxC = 0f; int jitBone = -1, pxBone = -1;
            for (int k = 0; k < prev.Length; k++)
            {
                float d = MathF.Abs(WrapAngle(anim.Pose.Local[k].Rotation - prev[k]));
                if (d > jit) { jit = d; jitBone = k; }
                prev[k] = anim.Pose.Local[k].Rotation;
                float dc = MathF.Abs(WrapAngle(ctrl.Pose.Local[k].Rotation - prevC[k]));
                if (dc > jitC) jitC = dc;
                prevC[k] = ctrl.Pose.Local[k].Rotation;
                float dp = (wg[k] - prevTip[k]).Length();
                if (dp > px) { px = dp; pxBone = k; }
                float dpc = (wc[k] - prevTipC[k]).Length();
                if (dpc > pxC) pxC = dpc;
            }
            prevTip = wg; prevTipC = wc;
            if (f < 30) return;
            if (jit  > worstJit)  { worstJit  = jit;  worstFrame  = f; worstState = p.CurrentStateName; }
            if (jitC > worstJitC) { worstJitC = jitC; worstFrameC = f; }
            if (px > worstPx) { worstPx = px; worstPxFrame = f; }
            worstPxC = MathF.Max(worstPxC, pxC);
            if (jitC > 0.3f) bigJumpsC++;
            if (jit > 0.3f)
            {
                bigJumps++;
                if (bigJumps <= 30)
                    trace.AppendLine($"  f={f} jit={jit:0.000} (ctrl {jitC:0.000}) px={px:0.0} bone={anim.Skeleton.Bones[jitBone].Name} state={p.CurrentStateName} vel=({p.Body.Velocity.X:0.#},{p.Body.Velocity.Y:0.#}) planes={tc} near={near} phase={anim.State.Phase:0.000}");
            }
        });

        string line = $"{name,-14} | guarded: worstJit={worstJit:0.000} (f={worstFrame}, {worstState}) bigJumps={bigJumps} worstPx={worstPx:0.0} (f={worstPxFrame})"
                    + $" | control: worstJit={worstJitC:0.000} (f={worstFrameC}) bigJumps={bigJumpsC} worstPx={worstPxC:0.0}";
        sb.AppendLine(line);
        if (trace.Length > 0) sb.AppendLine(trace.ToString());
        _o.WriteLine(line);
    }

    // All bone tips in world space, relative to the BODY (root = RigRoot − bodyPos):
    // captures solved root offsets (δ/d.x/com) + angles, but not body translation.
    private static Vector2[] WorldTips(CharacterAnimator anim, PlayerCharacter p)
    {
        var rootPos = AttackGlowSystem.RigRoot(p.Body.Position, p.Facing, anim, Scale) - p.Body.Position;
        int dir = p.Facing == 0 ? 1 : p.Facing;
        var world = anim.Pose.ComputeWorld(Affine2.FromTRS(rootPos, 0f, new Vector2(dir * Scale, Scale)));
        var tips = new Vector2[anim.Skeleton.Count];
        for (int k = 0; k < tips.Length; k++) tips[k] = world[k].Translation;
        return tips;
    }

    private static ChunkMap FloorMap200()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 3; r++) sb.AppendLine(new string('X', 200));
        return SimTerrain.FromAscii(sb.ToString(), originTileY: 10);
    }

    // Floor with a wall column rising at gtx=30: run right into the wall's left face.
    private static ChunkMap WallMap(out float wallX)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 8; r++) sb.AppendLine(new string('O', 30) + "X");
        for (int r = 0; r < 3; r++) sb.AppendLine(new string('X', 40));
        var map = SimTerrain.FromAscii(sb.ToString(), originTileY: 2);
        wallX = 30 * 16f;   // left face of the column
        return map;
    }

    // Staircase rising right: floor top at gty=10 (y=160); 1-tile-high, 2-tile-wide steps
    // starting at gtx=12. Running right climbs them; standing at a step face puts foot
    // tips inside the corner-slop of both the step's TOP and SIDE planes.
    private static ChunkMap StairMap()
    {
        const int W = 40, StepH = 6;
        var grid = new char[8 + 3, W];
        for (int r = 0; r < 8 + 3; r++)
            for (int c = 0; c < W; c++)
            {
                bool solid = r >= 8;                             // floor rows (gty 10..12)
                if (!solid && c >= 12)
                {
                    int k = Math.Min((c - 12) / 2 + 1, StepH);   // step height above floor at this col
                    solid = (8 - r) <= k;
                }
                grid[r, c] = solid ? 'X' : 'O';
            }
        var sb = new StringBuilder();
        for (int r = 0; r < 8 + 3; r++) { for (int c = 0; c < W; c++) sb.Append(grid[r, c]); sb.AppendLine(); }
        return SimTerrain.FromAscii(sb.ToString(), originTileY: 2);
    }

    // Floor tiles gtx 0..9 only — right edge (the ledge corner) at x=160.
    private static ChunkMap LedgeMap()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 3; r++) sb.AppendLine(new string('X', 10));
        return SimTerrain.FromAscii(sb.ToString(), originTileY: 10);
    }

    // A limb forced INSIDE a thin block: a 1-tile-thick wall column placed so the idle
    // hand tip sits mid-wall. Both the wall's left AND right faces are exposed and within
    // MaxDepth of the buried tip → two OPPOSING hard planes. Expect: trapped hand and/or
    // frame-to-frame thrash. This is the user-reported "limb stuck in block" case.
    [Fact]
    public void BuriedLimb()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Buried-limb probe — idle hand tip inside a 1-tile wall");

        // Find the natural idle hand position first (no terrain).
        var probe = NewAnimator();
        var pos = new Vector2(100f, 100f);
        for (int i = 0; i < 40; i++) probe.Update(Sample(pos, null, 0, false));
        var rootPos = AttackGlowSystem.RigRoot(pos, +1, probe, Scale);
        var world = probe.Pose.ComputeWorld(Affine2.FromTRS(rootPos, 0f, new Vector2(Scale, Scale)));
        int hand = probe.Skeleton.IndexOf("arm_r_lower");
        Vector2 natHand = world[hand].Translation;
        sb.AppendLine($"natural r-hand: ({natHand.X:0.0},{natHand.Y:0.0})");

        // 1-tile-wide wall column whose horizontal center is the hand: hand is buried ~8px
        // from BOTH faces.
        int wallGtx = (int)MathF.Floor(natHand.X / 16f);
        float shift = (wallGtx * 16f + 8f) - natHand.X;   // move body so hand = wall center
        pos = new Vector2(100f + shift, 100f);
        var wall = new StringBuilder();
        for (int r = 0; r < 12; r++) wall.AppendLine("X");
        var chunks = SimTerrain.FromAscii(wall.ToString(), originTileX: wallGtx,
                                          originTileY: (int)MathF.Floor((natHand.Y - 96f) / 16f));

        var anim = NewAnimator();
        for (int i = 0; i < 20; i++) anim.Update(Sample(pos, null, 0, false));

        var buf = new SolverSurface[8];
        var prev = new float[anim.Skeleton.Count];
        for (int k = 0; k < prev.Length; k++) prev[k] = anim.Pose.Local[k].Rotation;

        float worstJit = 0f; int bigJumps = 0; float handX = 0f;
        int opposing = 0;
        for (int f = 0; f < 300; f++)
        {
            int tc = TerrainSurfaces.Extract(chunks, anim, pos, +1, Scale, buf, out bool near);
            // count frames carrying BOTH wall faces for the hand
            bool hasL = false, hasR = false;
            for (int i = 0; i < tc; i++)
            {
                if ((buf[i].BoneMask & (1 << hand)) == 0) continue;
                if (buf[i].Normal.X < -0.9f) hasL = true;
                if (buf[i].Normal.X > 0.9f) hasR = true;
            }
            if (hasL && hasR) opposing++;
            anim.Update(Sample(pos, buf, tc, near));

            float jit = 0f;
            for (int k = 0; k < prev.Length; k++)
            {
                float d = MathF.Abs(WrapAngle(anim.Pose.Local[k].Rotation - prev[k]));
                jit = MathF.Max(jit, d);
                prev[k] = anim.Pose.Local[k].Rotation;
            }
            if (f >= 10)
            {
                worstJit = MathF.Max(worstJit, jit);
                if (jit > 0.3f) bigJumps++;
            }
            var rp = AttackGlowSystem.RigRoot(pos, +1, anim, Scale);
            handX = anim.Pose.ComputeWorld(Affine2.FromTRS(rp, 0f, new Vector2(Scale, Scale)))[hand].Translation.X;
        }
        float wallL = wallGtx * 16f, wallR = wallL + 16f;
        bool escaped = handX <= wallL + 0.5f || handX >= wallR - 0.5f;
        string line = $"opposing-plane frames={opposing}/300 worstJit={worstJit:0.000} bigJumps={bigJumps} " +
                      $"final handX={handX:0.0} wall=[{wallL:0},{wallR:0}] escaped={escaped}";
        sb.AppendLine(line);
        _o.WriteLine(line);

        string outDir = Path.Combine(FindUp("MTile.sln"), ".probe");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "buried_limb.md"), sb.ToString());
    }

    private static CharacterAnimSample Sample(Vector2 pos, SolverSurface[] buf, int tc, bool near)
        => new(pos, Vector2.Zero, +1, true, "StandingState", "", Dt,
               surfaces: buf, surfaceCount: tc, surfacesNear: near);

    private static ChunkMap FloorMap()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 3; r++) sb.AppendLine(new string('X', 60));
        return SimTerrain.FromAscii(sb.ToString(), originTileY: 10);
    }

    private static CharacterAnimator NewAnimator()
        => new(SkeletonExamples.Biped(), Scale, AnimationStore.LoadAll(StatesDir()));

    private static float NaturalToeY(Vector2 pos)
    {
        var anim = NewAnimator();
        for (int i = 0; i < 40; i++)
            anim.Update(Sample(pos, null, 0, false));
        return ToeY(anim, pos);
    }

    // World Y of the LOWER foot tip under the same root the game draws with.
    private static float ToeY(CharacterAnimator anim, Vector2 bodyPos)
    {
        var rootPos = AttackGlowSystem.RigRoot(bodyPos, +1, anim, Scale);
        var world = anim.Pose.ComputeWorld(Affine2.FromTRS(rootPos, 0f, new Vector2(Scale, Scale)));
        int fl = anim.Skeleton.IndexOf("foot_l"), fr = anim.Skeleton.IndexOf("foot_r");
        return MathF.Max(world[fl].Translation.Y, world[fr].Translation.Y);
    }

    private static float WrapAngle(float a)
    {
        while (a >  MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    private static string FindUp(string marker)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null) { if (File.Exists(Path.Combine(d.FullName, marker))) return d.FullName; d = d.Parent; }
        throw new DirectoryNotFoundException(marker);
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
