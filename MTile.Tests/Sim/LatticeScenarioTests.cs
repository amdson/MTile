using System;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Plans/LATTICE_SCENARIOS.md, one test per encoded row, under FoldEngine
// "lattice" (LatticeTracker: short-horizon channel QP over the path's first
// stretch — band + progress rows, BuildFold channels with frozen masks).
// These pin the CORRECT behavior from the table, not today's —
// rows the engine cannot do yet are Skip'ped with the row's blocker so they
// read as the checklist for the next cycle (un-skip, make it pass). Rows 5,
// 10, 12, 14 are deliberately not encoded yet (behavior not settled).
//
// Geometry: tile 16; hexagon half-width 6, half-height 10.4, margin 2,
// standing hover 10 → rest center = floor top − 20.4; a 2-high (32 px)
// opening fits standing by ~1 px. Walk 100 px/s; a running jump (walking
// speed) rises ≈ 49 px over ≈ 88 px of ground.
public class LatticeScenarioTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _prevEngine = Swap("lattice");
    private static string Swap(string e) { var p = MovementConfig.Current.FoldEngine; MovementConfig.Current.FoldEngine = e; return p; }
    public void Dispose() => MovementConfig.Current.FoldEngine = _prevEngine;

    private const int Ts = Chunk.TileSize;
    private static readonly float Rest = 2f * PlayerCharacter.Radius - 3.6f;   // 20.4: hover rest below a floor top
    private static readonly PlayerInput Right = new() { Right = true };
    private static readonly PlayerInput RightJump = new() { Right = true, Space = true };
    private static readonly PlayerInput Jump = new() { Space = true };
    private static readonly PlayerInput CrawlRight = new() { Right = true, Down = true };

    // rows[r][c] → 'X' solid. Built by a per-cell predicate.
    private static ChunkMap Terrain(int rows, int cols, Func<int, int, bool> solid)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++) sb.Append(solid(r, c) ? 'X' : 'O');
            if (r < rows - 1) sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    private static Vector2 OnFloor(float x, int floorRow) => new(x, floorRow * Ts - Rest);

    // A jump is Space held through the hold window (0.12 s); a one-frame tap
    // is a 12 px hop.
    private const int JumpHoldFrames = 12;
    private static PlayerInput JumpInput(int framesSincePress, bool right) =>
        framesSincePress < JumpHoldFrames ? (right ? RightJump : Jump) : (right ? Right : default);

    // ── Row 1: bumpy corridor ────────────────────────────────────────────
    // Alternating 1-high bumps and 1-low lips in a 3-tile corridor; hold
    // right. Traverses at speed, never leaves the standing fold to crouch.
    [Fact]
    public void Row01_BumpyCorridor_TraversesAtSpeed_WithoutCrouching()
    {
        var chunks = Terrain(7, 64, (r, c) =>
            r == 6 || (c >= 16 && (r == 2 || (r == 3 && c % 4 == 3) || (r == 5 && c % 4 == 1))));
        var sim = new Simulation(chunks, OnFloor(24f, 6));
        bool crouched = false;
        for (int f = 0; f < 600; f++)
        {
            sim.Step(Right);
            crouched |= sim.Player.CurrentStateName.Contains("Crouch");
        }
        float x = sim.Player.Body.Position.X, avg = (x - 24f) / 10f;
        output.WriteLine($"row1: x={x:F1} avg={avg:F1} px/s crouched={crouched}");
        Assert.True(x > 600f, $"stalled in the corridor at x={x:F1} (mouth at 256)");
        Assert.True(avg > 55f, $"too slow: {avg:F1} px/s");
        Assert.False(crouched, "the path is the duck — no crouch state change");
    }

    // ── Row 2: jump into a 2-high tunnel ─────────────────────────────────
    // Tunnel (ceiling row 3, floor row 6: 32 px interior) from x = 224. The
    // player walks right and jumps at x = 70 so the running-jump arc (≈ 54 px
    // apex) arrives on its descent above the mouth's free band (centers
    // y ∈ [78.4, 83.6]); the Fall-state solve with hover off + the corner
    // channel must bring the body in low and clean. Diagnostic line prints
    // the arrival height at the lip's C-obstacle edge (x = 210).
    [Fact]
    public void Row02_JumpIntoTunnel_EntersLowAndClean()
    {
        var chunks = Terrain(7, 48, (r, c) => r == 6 || (r == 3 && c >= 14));
        var sim = new Simulation(chunks, OnFloor(40f, 6));
        const float jumpX = 70f, mouthX = 14 * Ts, lipX = mouthX - 14f;
        int pressed = -1; float yAtLip = float.NaN;
        for (int f = 0; f < 240; f++)
        {
            if (pressed < 0 && sim.Player.Body.Position.X >= jumpX) pressed = f;
            sim.Step(pressed < 0 ? Right : JumpInput(f - pressed, right: true));
            var q = sim.Player.Body.Position;
            if (float.IsNaN(yAtLip) && q.X >= lipX) yAtLip = q.Y;
        }
        var end = sim.Player.Body.Position;
        output.WriteLine($"row2: y at lip edge={yAtLip:F1} (band 78.4..83.6), end=({end.X:F1},{end.Y:F1})");
        Assert.True(pressed >= 0, "never reached the jump point");
        Assert.True(end.X > mouthX + 3 * Ts, $"did not enter the tunnel: x={end.X:F1} (mouth at {mouthX})");
        Assert.True(end.Y > 64f + 12.4f && end.Y < 96f - 10.4f,
            $"not riding inside the tunnel: y={end.Y:F1}");
    }

    // ── Row 3: covered jump ──────────────────────────────────────────────
    // 2-high slab over x < 128, floor row 6. NEAR: body 3 px inside the
    // slab's end, under the last tile's corner bevel — the (1,−1) climb is
    // admissible and the body rises out to the right. FAR: body deep under
    // the slab — no rising edge, honest bonk, no shuffle toward the exit.
    [Fact]
    public void Row03_CoveredJump_NearEdge_RisesOutDiagonally()
    {
        var chunks = Terrain(7, 24, (r, c) => r == 6 || (r == 3 && c < 8));
        var sim = new Simulation(chunks, OnFloor(125f, 6));
        float minY = float.MaxValue, xAtMin = 0f;
        for (int f = 0; f < 90; f++)
        {
            sim.Step(JumpInput(f, right: false));
            var p = sim.Player.Body.Position;
            if (p.Y < minY) { minY = p.Y; xAtMin = p.X; }
        }
        output.WriteLine($"row3 near: apex y={minY:F1} at x={xAtMin:F1}");
        Assert.True(minY < 64f - 10.4f, $"never cleared the slab: apex y={minY:F1} (slab bottom 64)");
        Assert.True(xAtMin > 128f + 6f, $"rose without clearing the slab's edge: x={xAtMin:F1}");
    }

    [Fact]
    public void Row03_CoveredJump_FarFromEdge_BonksWithoutShuffling()
    {
        var chunks = Terrain(7, 24, (r, c) => r == 6 || (r == 3 && c < 8));
        var start = OnFloor(80f, 6);
        var sim = new Simulation(chunks, start);
        float minY = float.MaxValue, maxDx = 0f;
        for (int f = 0; f < 90; f++)
        {
            sim.Step(JumpInput(f, right: false));
            var p = sim.Player.Body.Position;
            minY = MathF.Min(minY, p.Y);
            maxDx = MathF.Max(maxDx, MathF.Abs(p.X - start.X));
        }
        output.WriteLine($"row3 far: apex y={minY:F1} max |dx|={maxDx:F1}");
        Assert.True(minY > 64f + 10.4f - 2f, $"passed the slab: apex y={minY:F1}");
        Assert.True(maxDx < 6f, $"shuffled {maxDx:F1} px toward the exit — the cutoff should bonk");
    }

    // ── Row 4: rest ──────────────────────────────────────────────────────
    [Fact]
    public void Row04_Rest_NoBobNoDrift()
    {
        var chunks = Terrain(4, 24, (r, c) => r == 3);
        var start = OnFloor(64f, 3);
        var sim = new Simulation(chunks, start);
        for (int f = 0; f < 120; f++) sim.Step(default);
        var v = sim.Player.Body.Velocity; var p = sim.Player.Body.Position;
        Assert.True(MathF.Abs(v.X) < 5f, $"drifting: vx={v.X:F2}");
        Assert.True(MathF.Abs(v.Y) < 5f, $"bobbing: vy={v.Y:F2}");
        Assert.True(MathF.Abs(p.X - start.X) < 2f, $"walked {p.X - start.X:F1} px with no input");
    }

    // ── Row 6: tall wall ─────────────────────────────────────────────────
    [Fact]
    public void Row06_TallWall_HonestStop()
    {
        var chunks = Terrain(7, 24, (r, c) => r == 6 || c >= 12);
        var sim = new Simulation(chunks, OnFloor(40f, 6));
        for (int f = 0; f < 300; f++) sim.Step(Right);
        var p = sim.Player.Body.Position;
        float wallX = 12 * Ts, rest = 6 * Ts - Rest;
        Assert.True(p.X > wallX - 3 * Ts, $"never reached the wall: x={p.X:F1}");
        Assert.True(p.X < wallX - 4f, $"into the wall: x={p.X:F1} (face {wallX})");
        Assert.True(MathF.Abs(p.Y - rest) < 8f, $"should stand at the wall, not climb: y={p.Y:F1}");
    }

    // ── Row 7: free-standing 2-high wall ─────────────────────────────────
    // The path routes over (accepted); the legs cannot deliver a 32 px rise
    // from a walk, so the give-up must turn it into row 6's honest stop —
    // the body ends AT the wall at hover, not floating up its face.
    [Fact]
    public void Row07_FreeStandingTwoHighWall_GiveUpIsHonestStop()
    {
        var chunks = Terrain(7, 24, (r, c) => r == 6 || (c == 12 && r >= 4));
        var sim = new Simulation(chunks, OnFloor(40f, 6));
        float minY = float.MaxValue;
        for (int f = 0; f < 300; f++) { sim.Step(Right); minY = MathF.Min(minY, sim.Player.Body.Position.Y); }
        var p = sim.Player.Body.Position;
        float wallX = 12 * Ts, rest = 6 * Ts - Rest;
        output.WriteLine($"row7: end=({p.X:F1},{p.Y:F1}) minY={minY:F1} rest={rest:F1}");
        Assert.True(p.X > wallX - 3 * Ts, $"never reached the wall: x={p.X:F1}");
        Assert.True(p.X < wallX - 4f, $"into / over the wall: x={p.X:F1}");
        Assert.True(MathF.Abs(p.Y - rest) < 8f, $"not standing at the wall: y={p.Y:F1} (rest {rest:F1})");
        Assert.True(minY > rest - 8f, $"strained up the face: minY={minY:F1}");
    }

    // ── Row 8: ledge drop while walking ──────────────────────────────────
    // Upper floor (row 3) for x < 160, lower floor (row 6) beyond: a 48 px
    // drop. Full carry through the drop (no grab), descent no faster than
    // free fall (no dive), re-bound at hover on the lower floor.
    [Fact]
    public void Row08_LedgeDrop_FullCarry_NoDive_Rebinds()
    {
        var chunks = Terrain(7, 40, (r, c) => r == 6 || (r == 3 && c < 10));
        var sim = new Simulation(chunks, OnFloor(40f, 3));
        float maxVy = 0f;
        for (int f = 0; f < 240; f++) { sim.Step(Right); maxVy = MathF.Max(maxVy, sim.Player.Body.Velocity.Y); }
        var p = sim.Player.Body.Position;
        float lowerRest = 6 * Ts - Rest;
        float freeFall = MathF.Sqrt(2f * Simulation.WorldGravityY * 3 * Ts);
        output.WriteLine($"row8: end=({p.X:F1},{p.Y:F1}) maxVy={maxVy:F1} freeFall={freeFall:F1}");
        Assert.True(MathF.Abs(p.Y - lowerRest) < 5f, $"not at hover on the lower floor: y={p.Y:F1} (rest {lowerRest:F1})");
        Assert.True(p.X > 40f + 0.8f * 100f * 4f - 30f, $"the ledge grabbed the carry: x={p.X:F1}");
        Assert.True(maxVy < freeFall + 15f, $"dived faster than free fall: {maxVy:F1} > {freeFall:F1}");
    }

    // ── Row 9: neutral jump in open air ──────────────────────────────────
    [Fact]
    public void Row09_NeutralJump_NoSidewaysDrift()
    {
        var chunks = Terrain(7, 24, (r, c) => r == 6);
        var start = OnFloor(100f, 6);
        var sim = new Simulation(chunks, start);
        float minY = float.MaxValue, maxDx = 0f;
        for (int f = 0; f < 120; f++)
        {
            sim.Step(JumpInput(f, right: false));
            var p = sim.Player.Body.Position;
            minY = MathF.Min(minY, p.Y);
            maxDx = MathF.Max(maxDx, MathF.Abs(p.X - start.X));
        }
        output.WriteLine($"row9: rise={start.Y - minY:F1} max |dx|={maxDx:F1}");
        Assert.True(start.Y - minY > 30f, $"did not jump: rose {start.Y - minY:F1} px");
        Assert.True(maxDx < 2f, $"drifted {maxDx:F1} px on a neutral jump");
    }

    // ── Row 10: diagonal hop over a block ────────────────────────────────
    // Hold Right, and jump ~40 px before a 1-high block: u = (1,−1)^, the
    // path rises over the block's C-obstacle, the legs launch along it, the
    // body lands beyond and keeps running. On the engine this is
    // JumpingState with dir held (RunningJumpState yields).
    [Fact]
    public void Row10_DiagonalHop_ClearsBlock_AndContinues()
    {
        var chunks = Terrain(7, 40, (r, c) => r == 6 || (r == 5 && c == 12));
        var sim = new Simulation(chunks, OnFloor(40f, 6));
        const float blockX = 12 * Ts, jumpX = blockX - 14f - 40f;
        int pressed = -1; float minY = float.MaxValue; bool wasJumping = false;
        for (int f = 0; f < 180; f++)
        {
            if (pressed < 0 && sim.Player.Body.Position.X >= jumpX) pressed = f;
            sim.Step(pressed < 0 ? Right : JumpInput(f - pressed, right: true));
            minY = MathF.Min(minY, sim.Player.Body.Position.Y);
            wasJumping |= sim.Player.CurrentStateName.Contains("Jump");
        }
        var end = sim.Player.Body.Position;
        output.WriteLine($"row10: apex y={minY:F1} end=({end.X:F1},{end.Y:F1}) jumped={wasJumping}");
        Assert.True(wasJumping, "never entered a jump state");
        Assert.True(minY < 6 * Ts - Rest - 20f, $"did not rise: apex {minY:F1}");
        Assert.True(end.X > blockX + 2 * Ts, $"did not clear the block: x={end.X:F1} (block at {blockX})");
        Assert.True(MathF.Abs(end.Y - (6 * Ts - Rest)) < 8f, $"not back at hover past the block: y={end.Y:F1}");
    }

    // ── Row 11: crouch at a 1-high block ─────────────────────────────────
    // Crawling right (Down held) into a 1-high block: stays low and stops at
    // it — a crouch never mounts ledges.
    [Fact]
    public void Row11_CrouchAtBlock_StaysLow_HonestStop()
    {
        var chunks = Terrain(7, 24, (r, c) => r == 6 || (r == 5 && c >= 12));
        var sim = new Simulation(chunks, OnFloor(40f, 6));
        bool crouched = false; float minY = float.MaxValue;
        for (int f = 0; f < 300; f++)
        {
            sim.Step(CrawlRight);
            crouched |= sim.Player.CurrentStateName.Contains("Crouch");
            minY = MathF.Min(minY, sim.Player.Body.Position.Y);
        }
        var p = sim.Player.Body.Position;
        float blockX = 12 * Ts, floorTop = 6 * Ts;
        output.WriteLine($"row11: end=({p.X:F1},{p.Y:F1}) minY={minY:F1} crouched={crouched}");
        Assert.True(crouched, "never entered the crouch");
        Assert.True(p.X > blockX - 3 * Ts, $"never reached the block: x={p.X:F1}");
        Assert.True(p.X < blockX - 4f, $"mounted / entered the block: x={p.X:F1}");
        Assert.True(minY > floorTop - Rest - 6f, $"rose toward the block top: minY={minY:F1}");
    }

    // ── Row 13: landing on flat, holding right ───────────────────────────
    // Spawned 4 tiles up. Impact honesty: the descent reaches the speed the
    // uncorrected body reaches (a control run with the corrector off — air
    // drag means that is below free fall); then hover re-binds and the carry
    // resumes.
    [Fact]
    public void Row13_Landing_ImpactHonest_ThenRebinds()
    {
        var chunks = Terrain(10, 40, (r, c) => r == 9);
        float floorTop = 9 * Ts;
        float MaxVy(bool corrector)
        {
            bool prev = MovementConfig.Current.AmbientCorrectorEnabled;
            MovementConfig.Current.AmbientCorrectorEnabled = corrector;
            try
            {
                var s = new Simulation(chunks, new Vector2(40f, floorTop - Rest - 4 * Ts));
                float m = 0f;
                for (int f = 0; f < 60; f++) { s.Step(Right); m = MathF.Max(m, s.Player.Body.Velocity.Y); }
                return m;
            }
            finally { MovementConfig.Current.AmbientCorrectorEnabled = prev; }
        }
        float control = MaxVy(corrector: false);
        var sim = new Simulation(chunks, new Vector2(40f, floorTop - Rest - 4 * Ts));
        float maxVy = 0f;
        for (int f = 0; f < 180; f++) { sim.Step(Right); maxVy = MathF.Max(maxVy, sim.Player.Body.Velocity.Y); }
        var p = sim.Player.Body.Position; var v = sim.Player.Body.Velocity;
        output.WriteLine($"row13: maxVy={maxVy:F1} control(no corrector)={control:F1} end=({p.X:F1},{p.Y:F1}) vx={v.X:F1}");
        Assert.True(maxVy > 0.95f * control, $"air-braked the fall: {maxVy:F1} vs uncorrected {control:F1}");
        Assert.True(MathF.Abs(p.Y - (floorTop - Rest)) < 5f, $"not re-bound at hover: y={p.Y:F1}");
        Assert.True(v.X > 70f, $"carry did not resume: vx={v.X:F1}");
    }

    // ── Row 15: wall slide, undisturbed ──────────────────────────────────
    // Airborne beside a tall wall, pressing into it. On the engine the slide
    // hands the Fall profile: the plan is blocked at the face, so the engine
    // adds nothing to the descent — no lift, no brake beyond the state's own
    // drag (equilibrium 80 px/s: SlideDrag·vy/SlideTerminalSpeed = g), which
    // a corrector-off control run measures. The held direction presses the
    // body into the wall (the FSD holds it); it never moves away. Lands at
    // hover beside the wall and stands.
    [Fact]
    public void Row15_WallSlide_UndisturbedDescent()
    {
        var chunks = Terrain(10, 24, (r, c) => r == 9 || c == 12);
        float faceX = 12 * Ts, floorTop = 9 * Ts;
        var start = new Vector2(faceX - 14f - 1f, 2 * Ts);
        (float maxVy, float minVyHigh, float minX, bool slid) Run(bool corrector)
        {
            bool prev = MovementConfig.Current.AmbientCorrectorEnabled;
            MovementConfig.Current.AmbientCorrectorEnabled = corrector;
            try
            {
                var sim = new Simulation(chunks, start);
                float maxVy = 0f, minVyHigh = float.MaxValue, minX = float.MaxValue; bool slid = false;
                for (int f = 0; f < 240; f++)
                {
                    sim.Step(Right);
                    var b = sim.Player.Body;
                    if (!sim.Player.CurrentStateName.Contains("WallSlid")) continue;
                    slid = true;
                    maxVy = MathF.Max(maxVy, b.Velocity.Y);
                    minX = MathF.Min(minX, b.Position.X);
                    if (f > 30 && floorTop - b.Position.Y > Rest + 30f) minVyHigh = MathF.Min(minVyHigh, b.Velocity.Y);
                }
                return (maxVy, minVyHigh, minX, slid);
            }
            finally { MovementConfig.Current.AmbientCorrectorEnabled = prev; }
        }
        var control = Run(corrector: false);
        var r = Run(corrector: true);
        var simEnd = new Simulation(chunks, start);
        for (int f = 0; f < 240; f++) simEnd.Step(Right);
        var p = simEnd.Player.Body.Position;
        output.WriteLine($"row15: slid={r.slid} slide maxVy={r.maxVy:F1} (control {control.maxVy:F1}) min vy high on the wall={r.minVyHigh:F1} minX={r.minX:F1} (start {start.X}) end=({p.X:F1},{p.Y:F1}) {simEnd.Player.CurrentStateName}");
        Assert.True(r.slid && control.slid, "never entered the wall slide");
        Assert.True(MathF.Abs(r.maxVy - control.maxVy) < 3f, $"the slide's own drag was overridden: max vy {r.maxVy:F1} vs control {control.maxVy:F1}");
        Assert.True(r.minVyHigh > 40f, $"held up on the wall: vy fell to {r.minVyHigh:F1} while sliding");
        Assert.True(r.minX > start.X - 1f, $"pushed off the wall: x {r.minX:F1} < start {start.X}");
        Assert.True(MathF.Abs(p.Y - (floorTop - Rest)) < 5f, $"not at hover on the floor: y={p.Y:F1}");
    }

    // ── Row 16: double jump in open air ──────────────────────────────────
    // Falling in open air, press jump (held 12 frames) with or without a
    // direction. The launch is the state's own impulse + hold force (nothing
    // to push against in free air) and the engine has no actuators there, so
    // the rise equals a corrector-off control run's; neutral has no drift.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Row16_DoubleJump_RiseIsTheStates_NoDriftNeutral(bool right)
    {
        var chunks = Terrain(10, 40, (r, c) => r == 9);
        var start = new Vector2(100f, 2 * Ts);
        var input = right ? Right : default;
        var inputJump = right ? RightJump : Jump;
        (float rise, float maxDx, bool jumped) Run(bool corrector)
        {
            bool prev = MovementConfig.Current.AmbientCorrectorEnabled;
            MovementConfig.Current.AmbientCorrectorEnabled = corrector;
            try
            {
                var sim = new Simulation(chunks, start);
                float minY = float.MaxValue, pressY = 0f, pressX = 0f, maxDx = 0f; bool jumped = false;
                for (int f = 0; f < 120; f++)
                {
                    sim.Step(f >= 10 && f < 10 + JumpHoldFrames ? inputJump : input);
                    var b = sim.Player.Body;
                    if (f == 10) { pressY = b.Position.Y; pressX = b.Position.X; }
                    if (f < 10) continue;
                    minY = MathF.Min(minY, b.Position.Y);
                    maxDx = MathF.Max(maxDx, MathF.Abs(b.Position.X - pressX));
                    jumped |= sim.Player.CurrentStateName.Contains("DoubleJump");
                }
                return (pressY - minY, maxDx, jumped);
            }
            finally { MovementConfig.Current.AmbientCorrectorEnabled = prev; }
        }
        var control = Run(corrector: false);
        var r = Run(corrector: true);
        output.WriteLine($"row16 right={right}: rise={r.rise:F1} (control {control.rise:F1}) max|dx|={r.maxDx:F1} jumped={r.jumped}");
        Assert.True(r.jumped, "never entered the double jump");
        Assert.True(MathF.Abs(r.rise - control.rise) < 2f, $"the engine changed the double jump's rise: {r.rise:F1} vs control {control.rise:F1}");
        if (!right) Assert.True(r.maxDx < 1f, $"drifted {r.maxDx:F1} px on a neutral double jump");
    }

    // ── Row 17: wall jump ────────────────────────────────────────────────
    // Sliding down a tall wall (pressing into it), press jump holding INTO
    // the wall (the classic: kick off, arc back, re-slide) or AWAY (kick off
    // and fly clear). The kick-off, hold force and air steering are the
    // state's; the engine has no actuators in free air, so the arc equals a
    // corrector-off control run's; the into case re-enters the slide.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Row17_WallJump_ArcIsTheStates(bool into)
    {
        var chunks = Terrain(10, 40, (r, c) => r == 9 || c == 24);
        float faceX = 24 * Ts;
        var start = new Vector2(faceX - 14f - 1f, 2 * Ts);
        var slide = Right;
        var hold = into ? RightJump : new PlayerInput { Left = true, Space = true };
        var after = into ? Right : new PlayerInput { Left = true };
        (float rise, float minX, bool jumped, bool reslid) Run(bool corrector)
        {
            bool prev = MovementConfig.Current.AmbientCorrectorEnabled;
            MovementConfig.Current.AmbientCorrectorEnabled = corrector;
            try
            {
                var sim = new Simulation(chunks, start);
                float minY = float.MaxValue, pressY = 0f, minX = float.MaxValue; bool jumped = false, reslid = false;
                for (int f = 0; f < 150; f++)
                {
                    sim.Step(f < 20 ? slide : f < 20 + JumpHoldFrames ? hold : after);
                    var b = sim.Player.Body; string st = sim.Player.CurrentStateName;
                    if (f == 20) pressY = b.Position.Y;
                    if (f < 20) continue;
                    // The arc is measured until the body re-crosses its launch
                    // height: below that the runs differ by the landing itself
                    // (the legs catch at hover; a corrector-off body falls to
                    // the collision floor) and a corrector-off Standing has no walk.
                    if (jumped && minY < pressY - 5f && b.Position.Y >= pressY) break;
                    minY = MathF.Min(minY, b.Position.Y);
                    minX = MathF.Min(minX, b.Position.X);
                    jumped |= st.Contains("WallJump");
                    reslid |= jumped && f > 30 && st.Contains("WallSlid");
                }
                return (pressY - minY, minX, jumped, reslid);
            }
            finally { MovementConfig.Current.AmbientCorrectorEnabled = prev; }
        }
        var control = Run(corrector: false);
        var r = Run(corrector: true);
        output.WriteLine($"row17 into={into}: rise={r.rise:F1} (control {control.rise:F1}) minX={r.minX:F1} (control {control.minX:F1}) jumped={r.jumped} reslid={r.reslid}");
        Assert.True(r.jumped, "never entered the wall jump");
        Assert.True(MathF.Abs(r.rise - control.rise) < 2f, $"the engine changed the wall jump's rise: {r.rise:F1} vs control {control.rise:F1}");
        Assert.True(MathF.Abs(r.minX - control.minX) < 3f, $"the engine changed the kick-off's reach: {r.minX:F1} vs control {control.minX:F1}");
        if (into) Assert.True(r.reslid, "did not arc back onto the wall");
    }
}
