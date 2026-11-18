using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// Sensor hitboxes (Hitbox.Sensor + Sweep): CombatSystem reports the earliest touch along
// the sweep to the Source's IContactSensor and applies nothing. Geometry only — the
// thrown clod built on it is covered by BlockThrowTests.
public class CombatSensorTests
{
    private static readonly float Ts = Chunk.TileSize;
    private const float Half = 4f;

    private sealed class Probe : IHittable, IContactSensor
    {
        public EntityId Id { get; } = new EntityId(7);
        public Faction Faction => Faction.Player1;
        public SensorContact? Contact;
        public void PublishHurtboxes(HurtboxWorld world) { }
        public Vector2 OnHit(in Hitbox hit, in Hurtbox myHurtbox) => hit.KnockbackImpulse;
        public void OnSensorContact(in Hitbox sensor, in SensorContact c) => Contact = c;
    }

    private sealed class Dummy : IHittable
    {
        public EntityId Id { get; init; } = new EntityId(50);
        public Faction Faction { get; init; } = Faction.Enemy;
        public BoundingBox Region;
        public int Hits;
        public void PublishHurtboxes(HurtboxWorld world) => world.Publish(new Hurtbox(Region, Faction, Id));
        public Vector2 OnHit(in Hitbox hit, in Hurtbox myHurtbox) { Hits++; return hit.KnockbackImpulse; }
    }

    private static SensorContact? Sense(ChunkMap chunks, Vector2 center, Vector2 sweep,
                                        HitTargets targets = HitTargets.All, params Dummy[] bodies)
        => Sense(chunks, center, sweep, null, targets, bodies);

    private static SensorContact? Sense(ChunkMap chunks, Vector2 center, Vector2 sweep, Polygon shape,
                                        HitTargets targets = HitTargets.All, params Dummy[] bodies)
    {
        var probe     = new Probe();
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        foreach (var b in bodies) b.PublishHurtboxes(hurtboxes);
        hitboxes.Publish(new Hitbox(
            new BoundingBox(center.X - Half, center.Y - Half, center.X + Half, center.Y + Half),
            hitId: 1, damage: 5f, knockbackImpulse: new Vector2(500f, 0f),
            owner: probe.Faction, source: probe.Id, targets: targets,
            shape: shape, shapePos: center,
            sweep: sweep, sensor: true));
        IHittable Resolve(EntityId id)
        {
            if (id == probe.Id) return probe;
            foreach (var b in bodies) if (b.Id == id) return b;
            return null;
        }
        new CombatSystem().Apply(chunks, hitboxes, hurtboxes, Resolve);
        return probe.Contact;
    }

    // Floor top at y = 3·Ts.
    private static ChunkMap Floor() => SimTerrain.FromAscii("....\n....\n....\nXXXX");

    [Fact]
    public void Falling_TouchesTheFloorFace()
    {
        var c = Sense(Floor(), new Vector2(1.5f * Ts, 3 * Ts - Half - 2f), new Vector2(0f, 10f));
        Assert.True(c.HasValue);
        Assert.True(c.Value.IsTerrain);
        Assert.Equal(0.2f, c.Value.T, 3);
        Assert.Equal(3 * Ts - Half, c.Value.Center.Y, 3);
        Assert.Equal(new Vector2(0f, -1f), c.Value.Normal);
    }

    [Fact]
    public void FastSweep_DoesNotTunnelAThinWall()
    {
        var wall = SimTerrain.FromAscii("..X....\n..X....");   // one column, x ∈ [2Ts, 3Ts)
        var c = Sense(wall, new Vector2(0.5f * Ts, 0.5f * Ts), new Vector2(6 * Ts, 0f));
        Assert.True(c.HasValue);
        Assert.Equal(2 * Ts - Half, c.Value.Center.X, 3);
    }

    [Fact]
    public void StartingOverlap_CountsOnlyAheadOfTheMotion()
    {
        var p = new Vector2(1.5f * Ts, 3 * Ts - Half + 2f);   // 2 px into the floor
        Assert.Null(Sense(Floor(), p, new Vector2(0f, -10f)));   // leaving it
        var c = Sense(Floor(), p, new Vector2(0f, 10f));         // driving into it
        Assert.True(c.HasValue);
        Assert.Equal(0f, c.Value.T);
        Assert.NotNull(Sense(Floor(), p, Vector2.Zero));         // at rest: any overlap
    }

    [Fact]
    public void Buried_FliesOutWithoutTouching()
    {
        var rock = SimTerrain.FromAscii("....\n....\nXXXX\nXXXX");
        Assert.Null(Sense(rock, new Vector2(1.5f * Ts, 3.5f * Ts), new Vector2(0f, -2 * Ts)));
    }

    [Fact]
    public void Body_TouchedFirst_AndNothingIsApplied()
    {
        var chunks = Floor();
        var foe  = new Dummy { Region = new BoundingBox(2 * Ts, 0f, 2 * Ts + 10f, 3 * Ts) };
        var ally = new Dummy { Id = new EntityId(51), Faction = Faction.Player1,
                               Region = new BoundingBox(Ts, 0f, Ts + 10f, 3 * Ts) };
        var c = Sense(chunks, new Vector2(0.5f * Ts, Ts), new Vector2(3 * Ts, 0f), HitTargets.All, ally, foe);
        Assert.True(c.HasValue);
        Assert.Equal(foe.Id, c.Value.Target);                  // own faction passed through
        Assert.Equal(2 * Ts - Half, c.Value.Center.X, 3);
        Assert.Equal(0, foe.Hits);
        Assert.Equal(0, ally.Hits);
        Assert.Null(Sense(chunks, new Vector2(0.5f * Ts, Ts), new Vector2(3 * Ts, 0f), HitTargets.TilesOnly, foe));
    }

    // A rounded sensor (regular polygon Shape) meets a cell's corner at its radius, where
    // its bounding box would have touched √2 farther out on a diagonal approach.
    [Fact]
    public void RoundShape_ReachesACornerAtItsRadius()
    {
        var cell   = SimTerrain.FromAscii("....\n....\n..X.\n....");   // corner at (2Ts, 2Ts)
        var corner = new Vector2(2 * Ts, 2 * Ts);
        var start  = corner - new Vector2(10f, 10f);
        var sweep  = new Vector2(10f, 10f);

        var box   = Sense(cell, start, sweep);
        var round = Sense(cell, start, sweep, Polygon.CreateRegular(Half, 12));
        Assert.True(box.HasValue && round.HasValue);
        Assert.True(round.Value.T > box.Value.T, $"round {round.Value.T:F3} vs box {box.Value.T:F3}");
        float dist = Vector2.Distance(round.Value.Center, corner);
        Assert.InRange(dist, 0.95f * Half, 1.01f * Half);   // 12-gon inradius is 0.966·R
    }

    [Fact]
    public void TerrainUnderASensor_TakesNoDamage()
    {
        var chunks = Floor();
        Sense(chunks, new Vector2(1.5f * Ts, 3 * Ts), Vector2.Zero);
        Assert.Empty(chunks.Damage.Damaged);
    }
}
