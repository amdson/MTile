using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The shrike (EntityKind.Shrike) — the bird that hunts. Patrols like a Bird until the
// player is inside ShrikeController.DetectRange, hovers for a wind-up beat, dives at
// DiveSpeed, and detonates on whatever it reaches first.
//
// What is pinned here is the set of things that fail SILENTLY, because a shrike that
// has quietly stopped hunting still looks exactly like a bird sitting in the sky:
//
//   * it patrols at all when nobody is near (a broken range gate = a permanent dive)
//   * it hovers before committing — the wind-up is the entire tell, and a dive that
//     starts on the frame of detection is unreactable rather than hard
//   * it actually closes the distance, and the dive ends in a blast that damages the
//     player and craters the terrain
//   * it does NOT go off while patrolling past terrain. The terrain fuse is armed on
//     proximity to the player; lose that gate and every shrike on the hill detonates
//     against the first rim it brushes, seconds after the stage loads.
public class ShrikeTests(ITestOutputHelper output)
{
    // Open sky over a floor, wide enough that a patrol leg never reaches the edges.
    // Floor is tile row 3 → its top surface is world y = 48.
    private const float FloorTopY = 3 * Chunk.TileSize;

    private static ChunkMap Ground() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -8, originTileY: 0);

    // Same walk-up-from-the-output-directory trick as BirdTests/ZeusHillTests: the
    // stage files live at the repo root, not beside the test binary. Skip rather than
    // fail if they aren't found, so a packaging change isn't read as a regression.
    private static string LevelsDir()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = System.IO.Path.Combine(d.FullName, "Levels");
            if (System.IO.File.Exists(System.IO.Path.Combine(c, "hill.json"))) return c;
            d = d.Parent;
        }
        return null;
    }

    private static EnemyEntity FindShrike(Simulation sim)
    {
        foreach (var e in sim.Entities)
            if (e is EnemyEntity en && en.Kind == EntityKind.Shrike) return en;
        return null;
    }

    private static Simulation WithShrike(Vector2 shrikePos, Vector2 playerPos, ChunkMap chunks = null)
        => new Simulation(chunks ?? Ground(), playerPos,
               g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Shrike, shrikePos)));

    [Fact]
    public void PatrolsBackAndForthWhileThePlayerIsOutOfRange()
    {
        // Player parked well outside DetectRange (170), so nothing here is dive
        // behaviour — this is the shrike's Bird half.
        var start  = new Vector2(100f, 24f);
        var sim    = WithShrike(start, new Vector2(-600f, 40f));
        var shrike = FindShrike(sim);
        Assert.NotNull(shrike);

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        int reversals = 0, lastSign = 0;
        float prevX = shrike.Body.Position.X;

        for (int f = 0; f < 600; f++)   // 10s — ShrikeController.LegSeconds is 2.0
        {
            sim.Step(default);
            var p = shrike.Body.Position;
            float dx = p.X - prevX; prevX = p.X;

            int sign = dx > 0.5f ? 1 : dx < -0.5f ? -1 : 0;
            if (sign != 0 && lastSign != 0 && sign != lastSign) reversals++;
            if (sign != 0) lastSign = sign;

            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
        }

        output.WriteLine($"reversals={reversals}, X span={maxX - minX:F1}, Y drift={maxY - minY:F1}");
        Assert.False(shrike.IsDead, "A patrolling shrike detonated with nobody near it.");
        Assert.True(reversals >= 3, $"Patrol should reverse repeatedly; saw {reversals} reversals.");
        // One leg is 2.0s × EnemyFlyState's 80 px/s cruise ≈ 160px.
        Assert.InRange(maxX - minX, 90f, 400f);
        // GravityScale 0 means level flight, not a slow sink.
        Assert.True(maxY - minY < 1f, $"Shrike lost {maxY - minY:F2}px of altitude — is GravityScale still 0?");
    }

    // The wind-up is the whole tell. ShrikeDiveState hovers for WindupSeconds (0.35)
    // before it commits, and a regression that skips the hover produces an attack the
    // player cannot react to — which reads as "the game cheated", not as difficulty.
    [Fact]
    public void HoversBeforeItCommitsToTheDive()
    {
        // 120px away — inside DetectRange (170), far enough that the approach is
        // visible in the numbers rather than instantaneous.
        var start  = new Vector2(120f, 20f);
        var sim    = WithShrike(start, new Vector2(0f, 20f));
        var shrike = FindShrike(sim);
        Assert.NotNull(shrike);

        // 0.35s of wind-up ≈ 21 frames. Sample just inside it (18) and well past it.
        float distAt(int frames)
        {
            for (int f = 0; f < frames; f++) sim.Step(default);
            return (shrike.Body.Position - sim.Player.Body.Position).Length();
        }

        float d0      = (start - sim.Player.Body.Position).Length();
        float dHover  = distAt(18);
        float dCommit = distAt(12);          // 30 frames total — 0.5s, past the wind-up

        output.WriteLine($"dist: start {d0:F1} → end of hover {dHover:F1} → mid-dive {dCommit:F1}");

        // During the hover it brakes out of its patrol cruise and holds station. It
        // starts the frame with patrol momentum, so a few px of coast is expected;
        // what must NOT happen is a dive's worth of travel.
        Assert.True(d0 - dHover < 20f,
            $"Shrike closed {d0 - dHover:F1}px during its wind-up — is it still hovering?");
        // Then it commits, and 0.15s of dive covers far more ground than the whole hover.
        Assert.True(dHover - dCommit > 25f,
            $"Shrike only closed {dHover - dCommit:F1}px after the wind-up — did the dive fire?");
    }

    [Fact]
    public void DivesIntoThePlayerAndDetonates()
    {
        var sim    = WithShrike(new Vector2(120f, 20f), new Vector2(0f, 20f));
        var shrike = FindShrike(sim);
        Assert.NotNull(shrike);

        // 1.5s: hover (0.35) + ~0.4s of dive to cross 120px, with margin for the
        // solver and for a shrike that overshoots once and comes back around.
        for (int f = 0; f < 90; f++) sim.Step(default);

        float pct = sim.Player.Combat.DamagePercent;
        output.WriteLine($"percent after the dive = {pct:F1}, shrike dead = {shrike.IsDead}");

        Assert.True(pct > 0f, "The dive never landed a blast on the player.");
        // It is a one-shot creature: the blast is also its death.
        Assert.True(shrike.IsDead, "Shrike survived its own detonation.");
        Assert.Null(FindShrike(sim));
    }

    // "Explodes on impact" has to mean the terrain too, or a dodged dive costs the
    // player nothing and the shrike is only ever a contact hazard with extra steps.
    [Fact]
    public void CratersTheGroundWhereItGoesOff()
    {
        var chunks = Ground();
        // Player standing on the floor, shrike a little above and to the side: whether
        // it reaches the player or the ground first, the blast is at floor level.
        var sim = WithShrike(new Vector2(90f, FloorTopY - 30f),
                             new Vector2(0f, FloorTopY - 10f), chunks);

        // Floor cells under and around the impact, before.
        var watched = new List<(int gtx, int gty)>();
        for (int gtx = -3; gtx <= 7; gtx++) watched.Add((gtx, 3));
        foreach (var (gtx, gty) in watched)
            Assert.Equal(TileState.Solid, chunks.GetCellState(gtx, gty));

        for (int f = 0; f < 90; f++) sim.Step(default);

        int broken = 0;
        foreach (var (gtx, gty) in watched)
            if (chunks.GetCellState(gtx, gty) != TileState.Solid) broken++;

        output.WriteLine($"floor cells destroyed by the blast: {broken}");
        Assert.True(broken > 0, "The shrike went off without touching the terrain.");
    }

    // The terrain fuse is armed by proximity to the player (ShrikeDetonateAction.ArmRange),
    // NOT by contact alone. Without that gate a shrike patrolling a lane with a rim in it
    // detonates on the first frame it grazes — which on the hill means the flocks
    // dismantle their own decks seconds after the stage loads, with no player involved.
    [Fact]
    public void DoesNotDetonateBrushingTerrainWhileOnPatrol()
    {
        var chunks = Ground();
        // Dragging along the floor, with the player a long way off.
        var sim    = WithShrike(new Vector2(60f, FloorTopY - 6f), new Vector2(-600f, 20f), chunks);
        var shrike = FindShrike(sim);
        Assert.NotNull(shrike);

        for (int f = 0; f < 300; f++) sim.Step(default);

        output.WriteLine($"after 5s on the floor: dead={shrike.IsDead}, pos={shrike.Body.Position}");
        Assert.False(shrike.IsDead, "Shrike detonated against terrain with no player in range.");
        // And the floor it was scraping along is intact.
        Assert.Equal(TileState.Solid, chunks.GetCellState(3, 3));
    }

    // The stage wiring: shrikes are actually on the hill, and in open air rather than
    // buried in a deck — a shrike spawned inside terrain would arm and go off the
    // instant a player climbed within range of it.
    [Fact]
    public void HillStageSpawnsShrikesInOpenAir()
    {
        var levels = LevelsDir();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }
        var chunks = new ChunkMap();
        TerrainLoader.Load(System.IO.Path.Combine(levels, "hill.json"), chunks);

        var stage = Stages.Get("hill");
        var sim   = new Simulation(chunks, stage.PlayerSpawn, stage.Populate);

        var shrikes = new List<EnemyEntity>();
        foreach (var e in sim.Entities)
            if (e is EnemyEntity en && en.Kind == EntityKind.Shrike) shrikes.Add(en);
        Assert.True(shrikes.Count >= 2, $"Expected the hill to spawn shrikes; found {shrikes.Count}.");

        foreach (var s in shrikes)
            Assert.False(TileQuery.IsSolidAt(chunks, s.Body.Position.X, s.Body.Position.Y),
                         $"Shrike spawned inside terrain at {s.Body.Position}.");

        // The player spawns on the plain, far below the lowest shrike deck, so nothing
        // should be hunting yet — they patrol, and they all survive the first 5s.
        var starts = shrikes.ConvertAll(s => s.Body.Position.X);
        float maxDrift = 0f;
        for (int f = 0; f < 300; f++)
        {
            sim.Step(default);
            for (int i = 0; i < shrikes.Count; i++)
                maxDrift = MathF.Max(maxDrift, MathF.Abs(shrikes[i].Body.Position.X - starts[i]));
        }
        output.WriteLine($"max shrike patrol drift on the hill = {maxDrift:F1}px");
        Assert.True(maxDrift > 30f, "Shrikes on the hill never moved.");
        foreach (var s in shrikes)
            Assert.False(s.IsDead, "A shrike on the hill went off before the player got near it.");
    }
}
