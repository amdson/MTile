using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Sand-specific repros for the "blocks nowhere near me break" family of bugs.
// Sand has HP = 0.5 (half of Dirt, quarter of Stone), so it surfaces issues
// that Stone is too durable to expose: sub-threshold impulse paths that
// accumulate, micro-overlap leakage, etc.
//
// All tests use a real PlayerCharacter (so the StandingState FSD spring is in
// the picture) — that's how the bug shows up in real play.
public class SandImpactDamageTests
{
    private readonly ITestOutputHelper _out;
    public SandImpactDamageTests(ITestOutputHelper o) => _out = o;

    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);
    private const int FloorRow = 20;
    private const float FloorTopY = FloorRow * 16f;

    // 20-col grid; rows 0..19 empty, rows 20..24 solid.
    // typeFor(col, row) lets the caller pick the material per cell.
    private static ChunkMap WidePlatform(Func<int, int, TileType> typeFor)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            var line = new char[20];
            for (int i = 0; i < 20; i++) line[i] = (r >= 20) ? 'X' : 'O';
            sb.Append(line).Append('\n');
        }
        var chunks = SimTerrain.FromAscii(sb.ToString());

        // SimTerrain leaves every solid cell at default Type = Stone. Override
        // per-cell from the caller-supplied lookup.
        for (int gtx = 0; gtx < 20; gtx++)
        for (int gty = 20; gty < 25; gty++)
        {
            int cx = gtx >> 4, cy = gty >> 4;   // tile per chunk = 16
            int lx = gtx - cx * 16, ly = gty - cy * 16;
            if (!chunks.TryGet(new Point(cx, cy), out var chunk)) continue;
            chunk.Tiles[lx, ly].Type = typeFor(gtx, gty);
        }
        return chunks;
    }

    private record BrokenCell(int Gtx, int Gty, TileType Type, int Frame);

    private (List<BrokenCell> broke, Dictionary<(int gtx, int gty), float> residual)
        DropAndCollect(ChunkMap terrain, PhysicsBody body, PlayerCharacter player, int frames,
                       Func<int, PlayerInput> inputFor = null)
    {
        var bodies = new List<PhysicsBody> { body };
        var ctrl = new Controller();
        var broke = new List<BrokenCell>();
        int currentFrame = -1;
        terrain.OnTileBroken = (wc, type) =>
        {
            int gtx = (int)System.MathF.Floor(wc.X / 16f);
            int gty = (int)System.MathF.Floor(wc.Y / 16f);
            broke.Add(new BrokenCell(gtx, gty, type, currentFrame));
        };

        for (int f = 0; f < frames; f++)
        {
            currentFrame = f;
            var input = inputFor?.Invoke(f) ?? new PlayerInput();
            ctrl.InjectInput(input);
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);
            player.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
        }

        var residual = new Dictionary<(int gtx, int gty), float>();
        foreach (var kv in terrain.Damage.Damaged) residual[kv.Key] = kv.Value;
        return (broke, residual);
    }

    private void Report(string label, List<BrokenCell> broke, Dictionary<(int gtx, int gty), float> residual)
    {
        _out.WriteLine($"--- {label}: broke {broke.Count} ---");
        foreach (var b in broke)
            _out.WriteLine($"  f={b.Frame,3} cell=({b.Gtx},{b.Gty}) type={b.Type}");
        _out.WriteLine($"--- {label}: residual HP {residual.Count} ---");
        foreach (var kv in residual)
            _out.WriteLine($"  ({kv.Key.gtx},{kv.Key.gty}) HP={kv.Value:F3}");
    }

    // 1) All-sand platform, player drops straight down. With Sand's low HP and
    // the spring-padded accumulator, this is the case most likely to fire chip
    // damage from a real player landing.
    [Fact]
    public void SandPlatform_PlayerDropStraightDown_ReportBreaks()
    {
        var terrain = WidePlatform((_, _) => TileType.Sand);
        float spawnX = 10 * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - 200f));
        float halfWidth = player.Body.Bounds.Right - player.Body.Position.X;
        _out.WriteLine($"player halfWidth = {halfWidth:F3}, spawnX={spawnX}, bounds=[{player.Body.Bounds.Left:F2}, {player.Body.Bounds.Right:F2}]");

        var (broke, residual) = DropAndCollect(terrain, player.Body, player, 60);
        Report("sand drop straight down", broke, residual);
    }

    // 2) Player drops on a STONE column but the columns immediately to the
    // left/right are SAND. Tests whether the column-bleed leak chips/breaks
    // sand the player never actually contacts.
    [Fact]
    public void StoneCenter_SandSides_PlayerLandsOnStone_SandCellsTakeDamage()
    {
        // Col 10 = Stone (player lands here); cols 9 and 11 = Sand.
        var terrain = WidePlatform((gtx, gty) =>
            gty == FloorRow && (gtx == 9 || gtx == 11) ? TileType.Sand : TileType.Stone);

        float spawnX = 10 * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - 200f));
        _out.WriteLine($"bounds=[{player.Body.Bounds.Left:F3}, {player.Body.Bounds.Right:F3}]  (col 10 is x=[160,176])");

        var (broke, residual) = DropAndCollect(terrain, player.Body, player, 60);
        Report("stone center, sand sides", broke, residual);

        // Bug surfaces as ANY damage attributed to cols 9 or 11 at row 20.
        var leaked = new List<string>();
        foreach (var b in broke)
            if (b.Gty == FloorRow && b.Gtx != 10) leaked.Add($"BROKE ({b.Gtx},{b.Gty})");
        foreach (var kv in residual)
            if (kv.Key.gty == FloorRow && kv.Key.gtx != 10) leaked.Add($"DAMAGED ({kv.Key.gtx},{kv.Key.gty}) HP={kv.Value:F3}");
        Assert.True(leaked.Count == 0,
            "sand columns adjacent to the player's landing column took damage even though the player only contacted col 10: "
            + string.Join("; ", leaked));
    }

    // 3) Walking across sand: player walks left + right along a sand platform.
    // Walking impulse is low per-frame, but the impact accumulator persists
    // across frames with a 0.23s half-life. If accumulation builds up on cells
    // the player isn't currently over, that's a sign sub-threshold impulses
    // are being misattributed.
    [Fact]
    public void WalkingOnSand_NoBreaksAlongTheWay()
    {
        var terrain = WidePlatform((_, _) => TileType.Sand);
        // Spawn standing on the platform, well inside it.
        float spawnX = 5 * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - 8f));

        // Walk right for 30 frames, left for 30 frames.
        PlayerInput InputFor(int f) => new PlayerInput { Right = f < 30, Left = f >= 30 };

        var (broke, residual) = DropAndCollect(terrain, player.Body, player, 60, InputFor);
        Report("walking on sand", broke, residual);

        Assert.True(broke.Count == 0, $"walking shouldn't break any sand tile; broke {broke.Count}");
    }

    // 4) Player drops onto a SINGLE sand cell in the middle of a stone field.
    // Where does the damage actually land? If damage leaks to adjacent stone
    // cells via the column-bleed, the sand cell may survive while a neighbour
    // takes the hit. Diagnostic / observational only.
    [Fact]
    public void SingleSandCellInStoneField_PlayerDropOnIt_ReportBreaks()
    {
        // Just one sand cell at (10, 20); everything else is Stone.
        var terrain = WidePlatform((gtx, gty) =>
            (gtx == 10 && gty == FloorRow) ? TileType.Sand : TileType.Stone);

        float spawnX = 10 * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - 200f));

        var (broke, residual) = DropAndCollect(terrain, player.Body, player, 60);
        Report("single sand at (10,20), drop centered", broke, residual);
    }

    // 5) Height sweep: drop the player onto sand from a range of heights. Per the
    // FSD-catch analysis, the spring is supposed to absorb the landing impulse —
    // but if at some velocity range the body shoots past the FSD's catch window
    // in one frame, or the catch fails to gate impact damage, we'd see breaks.
    [Theory]
    [InlineData(20f)]
    [InlineData(40f)]
    [InlineData(80f)]
    [InlineData(160f)]
    [InlineData(320f)]
    [InlineData(640f)]
    [InlineData(1280f)]
    [InlineData(2560f)]
    public void SandPlatform_PlayerDropHeightSweep_ReportBreaks(float dropHeight)
    {
        var terrain = WidePlatform((_, _) => TileType.Sand);
        float spawnX = 10 * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - dropHeight));
        // 180 frames = 6 sec @ 30fps, enough to land from even 2560px (impact vy ≈ 1750).
        var (broke, residual) = DropAndCollect(terrain, player.Body, player, 180);

        _out.WriteLine($"=== dropHeight = {dropHeight} ===");
        Report($"h={dropHeight}", broke, residual);
        _out.WriteLine($"final body pos = {player.Body.Position}, vy = {player.Body.Velocity.Y:F1}");
    }

    // 6) Height sweep alongside a sand-flanked stone center: focus on whether
    // the column-bleed propagates to adjacent SAND cells (HP 0.5 — much easier
    // to break via accumulated leakage) at various impact speeds.
    [Theory]
    [InlineData(50f)]
    [InlineData(100f)]
    [InlineData(200f)]
    [InlineData(400f)]
    [InlineData(800f)]
    [InlineData(1600f)]
    public void StoneCenter_SandSides_HeightSweep_ReportLeaks(float dropHeight)
    {
        var terrain = WidePlatform((gtx, gty) =>
            gty == FloorRow && (gtx == 9 || gtx == 11) ? TileType.Sand : TileType.Stone);

        float spawnX = 10 * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - dropHeight));
        var (broke, residual) = DropAndCollect(terrain, player.Body, player, 180);

        _out.WriteLine($"=== dropHeight = {dropHeight} ===");
        Report($"stone center / sand sides, h={dropHeight}", broke, residual);

        // Bug surfaces as ANY non-col-10 cell at floor row taking damage.
        var leaked = new List<string>();
        foreach (var b in broke)
            if (b.Gty == FloorRow && b.Gtx != 10) leaked.Add($"BROKE ({b.Gtx},{b.Gty})");
        foreach (var kv in residual)
            if (kv.Key.gty == FloorRow && kv.Key.gtx != 10) leaked.Add($"DAMAGED ({kv.Key.gtx},{kv.Key.gty}) HP={kv.Value:F3}");
        if (leaked.Count > 0)
            _out.WriteLine($"  !! LEAKED at h={dropHeight}: " + string.Join("; ", leaked));
    }
}
