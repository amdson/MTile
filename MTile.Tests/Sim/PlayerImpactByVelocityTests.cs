using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The player-vs-material impact spec, expressed as a deterministic drop test.
//
// Heights are derived from MovementConfig defaults (JumpVelocity=-100,
// JumpHoldForce=-1500, MaxJumpHoldTime=0.12, gravity=600):
//   single-jump apex     ≈ 55  px above ground → impact |vy| ≈ 256 px/s
//   double-jump apex     ≈ 110 px above ground → impact |vy| ≈ 363 px/s
//   "terminal" (big fall) ≈ 600 px above ground → impact |vy| ≈ 849 px/s
//
// Spec:
//   single jump (~256 px/s):  no break, no bounce — on ANY material.
//   double jump (~363 px/s):  bounce off Stone + Dirt; break Sand.
//   terminal   (~849 px/s):  bounce off Stone; break Dirt + Sand.
//
// Tuning lives in impact_profiles.json (player) and material_strengths.json
// (per-tile HP) + MovementConfig.MaxGroundEngageVnRel. These tests pin the
// behavioral contract; iterate the values until everything is green.
public class PlayerImpactByVelocityTests
{
    private readonly ITestOutputHelper _out;
    public PlayerImpactByVelocityTests(ITestOutputHelper o) => _out = o;

    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);
    private const int FloorRow = 20;
    private const float FloorTopY = FloorRow * 16f;
    private const float PlayerCenterX = 10 * 16f + 8f;   // center of column 10

    private const float SingleJumpHeight = 55f;
    private const float DoubleJumpHeight = 110f;
    private const float TerminalHeight   = 600f;

    // 20-col wide platform, rows 0..19 empty, rows 20..24 solid.
    // Every solid cell is of the given type — the player can land anywhere
    // on row 20 and the impacted strip contains only `type` cells.
    private static ChunkMap WidePlatform(TileType type)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            var line = new char[20];
            for (int i = 0; i < 20; i++) line[i] = (r >= 20) ? 'X' : 'O';
            sb.Append(line).Append('\n');
        }
        var chunks = SimTerrain.FromAscii(sb.ToString());
        for (int gtx = 0; gtx < 20; gtx++)
        for (int gty = 20; gty < 25; gty++)
        {
            int cx = gtx >> 4, cy = gty >> 4;
            int lx = gtx - cx * 16, ly = gty - cy * 16;
            if (!chunks.TryGet(new Point(cx, cy), out var chunk)) continue;
            chunk.Tiles[lx, ly].Type = type;
        }
        return chunks;
    }

    private record DropResult(
        bool Bounced,
        bool BrokeAnyCell,
        float PeakUpwardVy,
        float PeakImpactVy,
        int FirstBrokenFrame,
        int FramesSimulated);

    // Drops the player from `dropHeight` px above FloorTopY, runs `frames`
    // steps, and reports the outcome. The player's body Impact is shared
    // with ImpactProfiles.Build("player") — we want to test the SHIPPED
    // tuning, not test-local overrides.
    private DropResult RunDrop(TileType platformType, float dropHeight, int frames = 90)
    {
        var terrain = WidePlatform(platformType);
        var player = new PlayerCharacter(new Vector2(PlayerCenterX, FloorTopY - dropHeight));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();

        bool brokeAnyCell = false;
        int firstBrokenFrame = -1;
        int currentFrame = -1;
        terrain.OnTileBroken = (wc, type) =>
        {
            brokeAnyCell = true;
            if (firstBrokenFrame < 0) firstBrokenFrame = currentFrame;
        };

        float peakUpwardVy = 0f;      // most-negative vy (highest upward speed) post-impact
        float peakImpactVy = 0f;      // most-positive vy seen overall (just before impact)
        bool sawImpact = false;
        bool bounced = false;

        for (int f = 0; f < frames; f++)
        {
            currentFrame = f;
            ctrl.InjectInput(new PlayerInput());
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);
            player.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

            float vy = player.Body.Velocity.Y;
            if (vy > peakImpactVy) peakImpactVy = vy;
            // Detect "impact" as the first frame where vy drops sharply from
            // its peak. We only care about post-impact vy < 0 for bounce.
            if (!sawImpact && vy < peakImpactVy * 0.5f) sawImpact = true;
            if (sawImpact)
            {
                if (vy < peakUpwardVy) peakUpwardVy = vy;
                if (vy < -20f) bounced = true;
            }
        }

        return new DropResult(bounced, brokeAnyCell, peakUpwardVy, peakImpactVy, firstBrokenFrame, frames);
    }

    private void LogResult(string name, DropResult r)
    {
        _out.WriteLine($"[{name}] peakImpactVy={r.PeakImpactVy:F2}, peakUpwardVy={r.PeakUpwardVy:F2}, " +
                       $"bounced={r.Bounced}, broke={r.BrokeAnyCell}, firstBrokenFrame={r.FirstBrokenFrame}");
    }

    // ── Single jump (≈256 px/s impact) — no break, no bounce on any material ──

    [Fact]
    public void SingleJump_OntoStone_NoBounceNoBreak()
    {
        var r = RunDrop(TileType.Stone, SingleJumpHeight);
        LogResult("single/stone", r);
        Assert.False(r.Bounced,      "single jump should not bounce off stone");
        Assert.False(r.BrokeAnyCell, "single jump should not break stone");
    }

    [Fact]
    public void SingleJump_OntoDirt_NoBounceNoBreak()
    {
        var r = RunDrop(TileType.Dirt, SingleJumpHeight);
        LogResult("single/dirt", r);
        Assert.False(r.Bounced,      "single jump should not bounce off dirt");
        Assert.False(r.BrokeAnyCell, "single jump should not break dirt");
    }

    [Fact]
    public void SingleJump_OntoSand_NoBounceNoBreak()
    {
        var r = RunDrop(TileType.Sand, SingleJumpHeight);
        LogResult("single/sand", r);
        Assert.False(r.Bounced,      "single jump should not bounce off sand");
        Assert.False(r.BrokeAnyCell, "single jump should not break sand");
    }

    // ── Double jump (≈363 px/s impact) — bounce off stone/dirt, break sand ──

    [Fact]
    public void DoubleJump_OntoStone_BouncesNoBreak()
    {
        var r = RunDrop(TileType.Stone, DoubleJumpHeight);
        LogResult("double/stone", r);
        Assert.True(r.Bounced,        "double jump should bounce off stone");
        Assert.False(r.BrokeAnyCell,  "double jump should not break stone");
    }

    [Fact]
    public void DoubleJump_OntoDirt_BouncesNoBreak()
    {
        var r = RunDrop(TileType.Dirt, DoubleJumpHeight);
        LogResult("double/dirt", r);
        Assert.True(r.Bounced,        "double jump should bounce off dirt");
        Assert.False(r.BrokeAnyCell,  "double jump should not break dirt");
    }

    [Fact]
    public void DoubleJump_OntoSand_BreaksSand()
    {
        var r = RunDrop(TileType.Sand, DoubleJumpHeight);
        LogResult("double/sand", r);
        Assert.True(r.BrokeAnyCell,   "double jump should break sand");
    }

    // ── Terminal velocity (≈849 px/s impact) — bounce off stone, break dirt/sand ──

    [Fact]
    public void Terminal_OntoStone_BouncesNoBreak()
    {
        var r = RunDrop(TileType.Stone, TerminalHeight, frames: 120);
        LogResult("terminal/stone", r);
        Assert.True(r.Bounced,        "terminal velocity should bounce off stone");
        Assert.False(r.BrokeAnyCell,  "terminal velocity should not break stone");
    }

    [Fact]
    public void Terminal_OntoDirt_BreaksDirt()
    {
        var r = RunDrop(TileType.Dirt, TerminalHeight, frames: 120);
        LogResult("terminal/dirt", r);
        Assert.True(r.BrokeAnyCell,   "terminal velocity should break dirt");
    }

    [Fact]
    public void Terminal_OntoSand_BreaksSand()
    {
        var r = RunDrop(TileType.Sand, TerminalHeight, frames: 120);
        LogResult("terminal/sand", r);
        Assert.True(r.BrokeAnyCell,   "terminal velocity should break sand");
    }

    // ── Multi-layer plow-through (the absorption-cap break-through path) ──
    //
    // With the per-hit absorption cap (Physics/PhysicsWorld.cs), a hard impact on
    // stacked sand should:
    //   * Break multiple layers in succession — body retains the surplus impulse
    //     the tile face couldn't absorb (cap ≈ 290 px/s of Δv per sand cell at
    //     player Mass=2.5) and feeds it into the layer below within the same
    //     sweep / next frame.
    //   * Never crush-damage the player — each per-hit Δv is bounded by the cap
    //     and CrushImpulseThreshold is sized above the worst-case 2-cell sand
    //     hit (580 px/s).
    //
    // Geometry note: the player's hex body is 16.46 px wide for a 16 px tile,
    // so when it falls into a 1-tile-wide column it catches the upper corners
    // of the adjacent columns (a 0.23 px overlap that produces diagonal-normal
    // hits with no impact cells in the strip query). To isolate the absorption-
    // cap behavior from corner-catch artifacts the test uses a 1-column-wide
    // sand pillar — the sides of the pillar are empty so nothing catches the
    // hex side vertices on the way down.
    //
    // Drop height is set above the standard "terminal" 600 px (≈849 px/s) so
    // post-impact velocity stays above MovementConfig.MaxGroundEngageVnRel
    // (300 px/s) long enough to plow through 3 layers before the standing FSD
    // spring catches the body. At 800 px (≈980 px/s) the body breaks layers
    // 1+2 in the impact frame and layer 3 the frame after.
    [Fact]
    public void HighDrop_OntoSandStack_BreaksMultipleLayers_NoPlayerDamage()
    {
        const float DropHeight = 800f;
        // 20-col platform: rows 0..19 empty, rows 20..25 sand only in column 10
        // (a 1-column pillar of 6 sand layers), rows 26..28 stone everywhere
        // (catch floor). Player drops from terminal height onto the pillar.
        const int PillarCol = 10;
        var sb = new StringBuilder();
        for (int r = 0; r < 29; r++)
        {
            var line = new char[20];
            for (int i = 0; i < 20; i++)
            {
                bool sand   = r >= 20 && r <  26 && i == PillarCol;
                bool stone  = r >= 26 && r <  29;
                line[i] = (sand || stone) ? 'X' : 'O';
            }
            sb.Append(line).Append('\n');
        }
        var chunks = SimTerrain.FromAscii(sb.ToString());
        for (int gtx = 0; gtx < 20; gtx++)
        for (int gty = 20; gty < 29; gty++)
        {
            int cx = gtx >> 4, cy = gty >> 4;
            int lx = gtx - cx * 16, ly = gty - cy * 16;
            if (!chunks.TryGet(new Point(cx, cy), out var chunk)) continue;
            chunk.Tiles[lx, ly].Type = (gty < 26) ? TileType.Sand : TileType.Stone;
        }

        var player = new PlayerCharacter(new Vector2(PlayerCenterX, FloorTopY - DropHeight));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        float startHealth = player.Health;

        int sandBroken = 0;
        int stoneBroken = 0;
        chunks.OnTileBroken = (wc, type) =>
        {
            if (type == TileType.Sand) sandBroken++;
            else if (type == TileType.Stone) stoneBroken++;
        };

        for (int f = 0; f < 120; f++)
        {
            ctrl.InjectInput(new PlayerInput());
            chunks.TickSprouts(Dt);
            chunks.Impact.Tick(Dt);
            player.Update(ctrl, chunks, new HitboxWorld(), new HurtboxWorld(), Dt);
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);
        }

        _out.WriteLine($"sandBroken={sandBroken}, stoneBroken={stoneBroken}, " +
                       $"health: {startHealth} → {player.Health}, finalY={player.Body.Position.Y:F2}");

        Assert.True(sandBroken >= 3,
            $"expected ≥3 sand layers broken from terminal velocity, got {sandBroken}");
        Assert.Equal(0, stoneBroken);
        Assert.Equal(startHealth, player.Health);
    }
}
