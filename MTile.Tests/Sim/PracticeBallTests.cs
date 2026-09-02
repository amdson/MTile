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
    private const float FloorTopY = FloorRow * (float)Chunk.TileSize;

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
        var spawn   = new Vector2(10 * (float)Chunk.TileSize + Chunk.TileSize / 2f, FloorTopY - 5 * (float)Chunk.TileSize);
        var ball    = EntityFactory.Practice(spawn);

        // How far the ball actually has to fall before it can touch the floor —
        // derived from the scenario geometry (spawn/floor), not a literal pixel
        // count, so it scales with Chunk.TileSize.
        float dropDistance = FloorTopY - spawn.Y;

        float maxY = spawn.Y;               // deepest descent seen (Y-down)
        bool respawned = false;
        // Consecutive frames spent "at rest" (near-zero velocity) inside the
        // contact band without being back at spawn. One such frame is expected
        // and harmless: the swept solver can resolve the floor contact (zeroing
        // velocity) a frame before the entity's own Update() re-probes and calls
        // Break() — Update runs before the physics step each frame, so there is
        // an inherent one-frame latency between "solver stopped it" and "Break()
        // fires". Two or more in a row means it genuinely settled instead of
        // breaking.
        int consecutiveRestFrames = 0;
        int maxConsecutiveRestFrames = 0;
        Run(ball, terrain, frames: 120, perFrame: f =>
        {
            if (ball.Body.Position.Y > maxY) maxY = ball.Body.Position.Y;
            // Back at spawn AFTER having genuinely descended. Checked post-step,
            // so the respawn frame already carries one tick of gravity — "dead-
            // stopped" here means "far slower than any fall that reached the
            // floor" (impact velocity is ~350+ px/s; one gravity tick is 10).
            if (!respawned && maxY > spawn.Y + dropDistance * 0.5f
                && Vector2.Distance(ball.Body.Position, spawn) < 2f
                && ball.Body.Velocity.Length() < 20f)
                respawned = true;

            bool atRestNearFloor = ball.Body.Velocity.Length() < 20f
                && ball.Body.Position.Y > FloorTopY - PracticeBallRestingClearance
                && Vector2.Distance(ball.Body.Position, spawn) > 2f;
            consecutiveRestFrames = atRestNearFloor ? consecutiveRestFrames + 1 : 0;
            if (consecutiveRestFrames > maxConsecutiveRestFrames) maxConsecutiveRestFrames = consecutiveRestFrames;
        });

        output.WriteLine($"deepest Y = {maxY:0.0} (spawn {spawn.Y:0.0}, floor top {FloorTopY:0.0})");
        Assert.True(maxY > spawn.Y + dropDistance * 0.5f, "ball never fell — test setup broken");
        Assert.True(respawned, "ball touched the floor but never respawned at its spawn point");
        // A single frame of solver-stopped-then-not-yet-broken is the expected
        // Update-before-physics latency (see comment above); anything more means
        // it settled into a resting contact instead of breaking.
        Assert.True(maxConsecutiveRestFrames <= 1,
            $"ball settled into a resting contact near the floor instead of breaking ({maxConsecutiveRestFrames} consecutive frames at rest)");
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
