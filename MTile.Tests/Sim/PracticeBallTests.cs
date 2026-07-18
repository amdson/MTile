using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// PracticeBall — the juggling drill target (breaks on tile contact, respawns at
// its spawn point with velocity zeroed and health refilled).
public class PracticeBallTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);
    private const int FloorRow = 20;
    private const float FloorTopY = FloorRow * 16f;

    private static ChunkMap FlatFloor()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            for (int i = 0; i < 20; i++) sb.Append(r >= FloorRow ? 'X' : 'O');
            sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    private sealed class FakeSpawner : IEntitySpawner
    {
        public ChunkMap Chunks { get; init; }
        public HitIdAllocator HitIds { get; } = new();
        public void SpawnEntity(Entity e) { }
    }

    // Mirrors Simulation's phase shape: entity Update (the break probe), then the
    // physics step. Returns after `frames` steps.
    private static void Run(PracticeBall ball, ChunkMap terrain, int frames,
                            System.Action<int> perFrame = null)
    {
        var spawner = new FakeSpawner { Chunks = terrain };
        var bodies  = new List<PhysicsBody> { ball.Body };
        for (int f = 0; f < frames; f++)
        {
            ball.Update(Dt, null, null, spawner);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            perFrame?.Invoke(f);
        }
    }

    // Dropped from its spawn point (five tiles up), the ball must fall, touch the
    // floor, and reappear at the spawn point dead-stopped — never settling into a
    // resting contact on the ground.
    [Fact]
    public void FallsToFloor_BreaksAndRespawnsAtSpawn()
    {
        var terrain = FlatFloor();
        var spawn   = new Vector2(10 * 16f + 8f, FloorTopY - 5 * 16f);
        var ball    = EntityFactory.Practice(spawn);

        float maxY = spawn.Y;               // deepest descent seen (Y-down)
        bool respawned = false;
        Run(ball, terrain, frames: 120, perFrame: f =>
        {
            if (ball.Body.Position.Y > maxY) maxY = ball.Body.Position.Y;
            // Back at spawn AFTER having genuinely descended. Checked post-step,
            // so the respawn frame already carries one tick of gravity — "dead-
            // stopped" here means "far slower than any fall that reached the
            // floor" (impact velocity is ~350+ px/s; one gravity tick is 10).
            if (!respawned && maxY > spawn.Y + 40f
                && Vector2.Distance(ball.Body.Position, spawn) < 2f
                && ball.Body.Velocity.Length() < 20f)
                respawned = true;
        });

        output.WriteLine($"deepest Y = {maxY:0.0} (spawn {spawn.Y:0.0}, floor top {FloorTopY:0.0})");
        Assert.True(maxY > spawn.Y + 40f, "ball never fell — test setup broken");
        Assert.True(respawned, "ball touched the floor but never respawned at its spawn point");
        // And it must not be resting on the floor at the end — a fresh fall is in
        // progress or it just respawned; either way it sits above the contact band.
        Assert.True(ball.Body.Position.Y < FloorTopY - PracticeBallRestingClearance,
            $"ball is resting near the floor (Y={ball.Body.Position.Y:0.0})");
    }

    // Ball radius (6) + contact pad — anything closer than this to the floor top
    // would re-break next Update, so a settled ball can never sit inside it.
    private const float PracticeBallRestingClearance = 8f;

    // Damage chipped off during a rally refills on break — the ball is a
    // permanent fixture, not a consumable.
    [Fact]
    public void Break_RefillsHealth()
    {
        var terrain = FlatFloor();
        var spawn   = new Vector2(10 * 16f + 8f, FloorTopY - 5 * 16f);
        var ball    = EntityFactory.Practice(spawn);

        var hit = new Hitbox(ball.Body.Bounds, hitId: 1, damage: 5f,
                             knockbackImpulse: new Vector2(0f, 60f),   // downward tap
                             owner: Faction.Player1, source: new EntityId(1));
        ball.OnHit(hit, new Hurtbox(ball.Body.Bounds, ball.Faction, ball.Id));
        Assert.True(ball.Health < ball.MaxHealth);

        Run(ball, terrain, frames: 120);
        Assert.Equal(ball.MaxHealth, ball.Health);
    }
}
