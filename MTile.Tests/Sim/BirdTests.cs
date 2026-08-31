using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The bird (EntityKind.Bird) — a flying contact-damage hazard that patrols left and
// right. It is pure blueprint: PatrolController for the brain, EnemyFlyState for
// locomotion, EnemyContactAction for the damage. What is worth pinning is the set of
// things that fail silently, because a bird that has quietly stopped working still
// looks like a bird sitting in the sky:
//
//   * the patrol actually reverses (a brain stuck on one heading flies off forever)
//   * it holds altitude (GravityScale 0 + fly state; regress either and it sinks)
//   * contact damage repeats on its cooldown, and ONLY on its cooldown — the whole
//     reason the hazard is an action with a fresh HitId per Enter is that a single
//     long-lived hitbox would damage each target exactly once, ever
//   * flight survives a contact hit (EnemyContactAction must not set Committed)
public class BirdTests(ITestOutputHelper output)
{
    // Open sky over a floor, wide enough that a patrol leg never reaches the edges.
    private static ChunkMap Ground() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -8, originTileY: 0);

    // Same walk-up-from-the-output-directory trick as ZeusHillTests/GauntletStageTests:
    // the stage files live at the repo root, not beside the test binary. Skip rather
    // than fail if they aren't found, so a packaging change isn't read as a regression.
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

    private static EnemyEntity FindBird(Simulation sim)
    {
        foreach (var e in sim.Entities)
            if (e is EnemyEntity en && en.Kind == EntityKind.Bird) return en;
        return null;
    }

    private static Simulation WithBird(Vector2 birdPos, Vector2 playerPos)
        => new Simulation(Ground(), playerPos,
               g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Bird, birdPos)));

    [Fact]
    public void PatrolsBackAndForthAtAConstantAltitude()
    {
        // Player parked far away so nothing here is contact behaviour.
        var start = new Vector2(100f, 24f);
        var sim   = WithBird(start, new Vector2(-100f, 20f));
        var bird  = FindBird(sim);
        Assert.NotNull(bird);

        // Count heading reversals off the sign of per-frame dx, which is what
        // "back and forth" actually means. Two reversals = a full there-and-back.
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        int reversals = 0, lastSign = 0;
        float prevX = bird.Body.Position.X;

        for (int f = 0; f < 600; f++)   // 10s — PatrolController.LegSeconds is 2.4
        {
            sim.Step(default);
            var p = bird.Body.Position;
            float dx = p.X - prevX; prevX = p.X;

            int sign = dx > 0.5f ? 1 : dx < -0.5f ? -1 : 0;
            if (sign != 0 && lastSign != 0 && sign != lastSign) reversals++;
            if (sign != 0) lastSign = sign;

            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
        }

        output.WriteLine($"reversals={reversals}, X span={maxX - minX:F1}, Y drift={maxY - minY:F1}");
        Assert.True(reversals >= 3, $"Patrol should reverse repeatedly; saw {reversals} reversals.");
        // Bounded: it patrols a lane rather than wandering off. One leg is
        // 2.4s x EnemyFlyState's 80 px/s cruise ~= 190px, so a full sweep is ~2 legs.
        Assert.InRange(maxX - minX, 100f, 420f);
        // GravityScale 0 means level flight, not a slow sink. Exact equality would be
        // over-fitting; a pixel of slack still catches a regression to gravity.
        Assert.True(maxY - minY < 1f, $"Bird lost {maxY - minY:F2}px of altitude — is GravityScale still 0?");
    }

    [Fact]
    public void DamagesThePlayerOnContact()
    {
        // Bird spawned on top of the player: contact is immediate.
        var sim = WithBird(new Vector2(0f, 20f), new Vector2(0f, 20f));
        for (int f = 0; f < 30; f++) sim.Step(default);

        float hp = sim.Player.Combat.DamageTaken;
        output.WriteLine($"HP lost to contact = {hp:F2}");
        Assert.True(hp > 0f, "Touching the bird should cost the player HP.");
    }

    // The cooldown IS the mechanic. Too fast and standing next to a bird is instant
    // death; not repeating at all is the failure mode a single persistent hitbox
    // would have (CombatSystem dedupes by HitId, so it would land exactly once).
    [Fact]
    public void ContactDamageRepeatsOnItsCooldownAndNotEveryFrame()
    {
        var sim  = WithBird(new Vector2(0f, 20f), new Vector2(0f, 20f));
        var bird = FindBird(sim);

        var hitFrames = new List<int>();
        float last = 0f;
        for (int f = 0; f < 240; f++)
        {
            // Pin the player ON the bird. The question here is the COOLDOWN, and a
            // free body answers a different one: the first touch knocks the player off
            // the bird's lane and whether they drift back into it is a fact about the
            // knockback number, not about the hazard's timing. (This test used to pass
            // by that accident — contact knockback was hard enough to carry the player
            // along with the patrol — and stopped when the knockback pass halved it.)
            sim.Player.Body.Position = bird.Body.Position;
            sim.Player.Body.Velocity = Vector2.Zero;
            sim.Step(default);
            float hp = sim.Player.Combat.DamageTaken;
            if (hp > last) { hitFrames.Add(f); last = hp; }
        }

        output.WriteLine($"hit frames: {string.Join(",", hitFrames)} (total {last:F2} HP)");
        Assert.True(hitFrames.Count >= 2, $"Sustained contact should tick more than once; got {hitFrames.Count}.");

        // ActiveWindow 0.10s + Cooldown 0.85s = 0.95s ~= 57 frames at FixedDt.
        int gap = hitFrames[1] - hitFrames[0];
        Assert.InRange(gap, 40, 80);
    }

    // EnemyContactAction deliberately leaves Committed false. If it ever sets it,
    // EnemyFlyState's `!IsActionCommitted` precondition drops and the bird stops
    // flying the first time it touches anybody — which, at GravityScale 0, strands it
    // motionless in the air rather than producing an obvious crash.
    [Fact]
    public void KeepsFlyingAfterLandingAContactHit()
    {
        var sim  = WithBird(new Vector2(0f, 20f), new Vector2(0f, 20f));
        var bird = FindBird(sim);
        Assert.NotNull(bird);

        for (int f = 0; f < 30; f++) sim.Step(default);
        Assert.True(sim.Player.Combat.DamageTaken > 0f, "precondition: the bird should have hit by now");

        float xAfterHit = bird.Body.Position.X;
        for (int f = 0; f < 60; f++) sim.Step(default);
        float travelled = MathF.Abs(bird.Body.Position.X - xAfterHit);

        output.WriteLine($"travelled {travelled:F1}px in the second after a contact hit");
        Assert.True(travelled > 20f, $"Bird stopped flying after a hit (moved {travelled:F1}px) — is it setting Committed?");
    }

    // The stage wiring: birds are actually on the hill, and clear of the terrain
    // rather than spawned inside the slope.
    [Fact]
    public void HillStageSpawnsPatrollingBirds()
    {
        var levels = LevelsDir();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }
        var chunks = new ChunkMap();
        TerrainLoader.Load(System.IO.Path.Combine(levels, "hill.json"), chunks);

        var stage = Stages.Get("hill");
        var sim   = new Simulation(chunks, stage.PlayerSpawn, stage.Populate);

        var birds = new List<EnemyEntity>();
        foreach (var e in sim.Entities)
            if (e is EnemyEntity en && en.Kind == EntityKind.Bird) birds.Add(en);
        Assert.True(birds.Count >= 2, $"Expected the hill to spawn birds; found {birds.Count}.");

        // Spawned in open air: a bird buried in the hillside would stall against it
        // and never patrol, which looks like a broken brain rather than a bad spawn.
        foreach (var b in birds)
            Assert.False(TileQuery.IsSolidAt(chunks, b.Body.Position.X, b.Body.Position.Y),
                         $"Bird spawned inside terrain at {b.Body.Position}.");

        // And they still patrol once the real stage is running.
        var starts = birds.ConvertAll(b => b.Body.Position.X);
        float maxDrift = 0f;
        for (int f = 0; f < 300; f++)
        {
            sim.Step(default);
            for (int i = 0; i < birds.Count; i++)
                maxDrift = MathF.Max(maxDrift, MathF.Abs(birds[i].Body.Position.X - starts[i]));
        }
        output.WriteLine($"max patrol drift on the hill = {maxDrift:F1}px");
        Assert.True(maxDrift > 30f, "Birds on the hill never moved.");
    }
}
