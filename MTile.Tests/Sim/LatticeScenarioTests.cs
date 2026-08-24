using System;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Plans/LATTICE_SCENARIOS.md, one test per encoded row, under FoldEngine
// "lattice" (the qp channel stack riding the lattice path's reference).
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
    [Fact(Skip = "LATTICE_SCENARIOS row 3 — jump states not on the engine; CoveredJumpState owns the launch (plan §7.3). No rise today (apex 76.5)")]
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
    [Fact(Skip = "LATTICE_SCENARIOS row 7 — with the qp legs mask the legs strain the body 12 px up the face toward the over-the-top path (minY 63.3); the legs-at-support mask (AmbientCorrector, LegsAtSupport) holds it honest at the cost of corridor speed")]
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
    [Fact(Skip = "LATTICE_SCENARIOS row 8 — full carry and no dive pass; the landing settles 5 px below hover (80.7 vs 75.6) under the channel stack")]
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

    // ── Row 11: crouch at a 1-high block ─────────────────────────────────
    // Crawling right (Down held) into a 1-high block: stays low and stops at
    // it — a crouch never mounts ledges.
    [Fact(Skip = "LATTICE_SCENARIOS row 11 — known gap: edges carry no climb band, the crouch mounts the block (plan §3.3 note)")]
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
    [Fact(Skip = "LATTICE_SCENARIOS row 13 — descent reaches 250 of the uncorrected 270 px/s (92.6%) under the channel stack; the remaining brake is not the legs (identical with the at-support mask)")]
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
}
