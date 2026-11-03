using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// Clank (todo.txt D, CombatSystem.ResolveClanks): opposing melee hitboxes that touch
// resolve against each other before either reaches a hurtbox. Within
// ClankDamageBand of each other both are cancelled; past it the stronger one lands.
//
// Layout for the unit cases: P1's dummy at x 0..10, P2's at x 30..40, and each side's
// swing spans 5..35 — so each swing overlaps the other swing AND the enemy body.
public class CombatClankTests
{
    private sealed class Dummy : IHittable
    {
        public Faction Faction { get; }
        public EntityId Id { get; }
        public BoundingBox Region;
        public int Hits;
        public Dummy(int id, Faction f, BoundingBox region) { Id = new EntityId(id); Faction = f; Region = region; }
        public void PublishHurtboxes(HurtboxWorld world) => world.Publish(new Hurtbox(Region, Faction, Id));
        public Vector2 OnHit(in Hitbox hit, in Hurtbox myHurtbox) { Hits++; return hit.KnockbackImpulse; }
    }

    private static readonly BoundingBox Swing = new(5f, 0f, 35f, 10f);

    private static Hitbox Box(int hitId, Faction owner, float damage, ClankKind clank,
                              BoundingBox? region = null, Vector2? origin = null, float recoil = 0f)
        => new(region ?? Swing, hitId, damage, new Vector2(owner == Faction.Player1 ? 100f : -100f, 0f),
               owner, EntityId.None, bodyDamage: damage, recoilScale: recoil, origin: origin, clank: clank);

    private sealed class Rig
    {
        public readonly CombatSystem Combat = new();
        public readonly Dummy P1 = new(1, Faction.Player1, new BoundingBox(0f, 0f, 10f, 10f));
        public readonly Dummy P2 = new(2, Faction.Player2, new BoundingBox(30f, 0f, 40f, 10f));
        public readonly List<(int A, int B, bool Metal)> Clanks = new();
        public ChunkMap Chunks = SimTerrain.FromAscii("....");
        public Rig() => Combat.OnClank += (a, b, _, m) => Clanks.Add((a, b, m));

        public void Frame(params Hitbox[] hits)
        {
            var hb = new HitboxWorld();
            var hu = new HurtboxWorld();
            P1.PublishHurtboxes(hu); P2.PublishHurtboxes(hu);
            foreach (var h in hits) hb.Publish(h);
            Combat.Apply(Chunks, hb, hu, id => id == P1.Id ? P1 : id == P2.Id ? P2 : null);
        }
    }

    [Fact]
    public void EvenSwings_CancelEachOther()
    {
        var r = new Rig();
        r.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade), Box(20, Faction.Player2, 0.5f, ClankKind.Blade));
        Assert.Equal(0, r.P1.Hits);
        Assert.Equal(0, r.P2.Hits);
        Assert.Single(r.Clanks);
        Assert.True(r.Clanks[0].Metal);
    }

    [Fact]
    public void WithinBand_StillCancelsBoth()
    {
        var r = new Rig();
        r.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade), Box(20, Faction.Player2, 0.6f, ClankKind.Blade));
        Assert.Equal(0, r.P1.Hits);
        Assert.Equal(0, r.P2.Hits);
    }

    [Fact]
    public void StrongerSwing_WinsTheClank()
    {
        var r = new Rig();
        r.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade), Box(20, Faction.Player2, 1.0f, ClankKind.Blunt));
        Assert.Equal(1, r.P1.Hits);    // P2's heavier blow went through
        Assert.Equal(0, r.P2.Hits);    // P1's was stopped
        Assert.Single(r.Clanks);
        Assert.False(r.Clanks[0].Metal);
    }

    [Fact]
    public void NonMelee_PassesThrough()
    {
        var r = new Rig();
        r.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade), Box(20, Faction.Player2, 0.5f, ClankKind.None));
        Assert.Equal(1, r.P1.Hits);
        Assert.Equal(1, r.P2.Hits);
        Assert.Empty(r.Clanks);
    }

    [Fact]
    public void SameSide_DoesNotClank()
    {
        var r = new Rig();
        r.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade), Box(11, Faction.Player1, 0.5f, ClankKind.Blade));
        Assert.Empty(r.Clanks);
        Assert.Equal(2, r.P2.Hits);    // both of P1's attacks land
    }

    // A clanked attack stays cancelled for the rest of its broadcast, even once the
    // other swing is gone — and that survives a dedupe snapshot round-trip.
    [Fact]
    public void Cancellation_PersistsAcrossFrames_AndSnapshot()
    {
        var r = new Rig();
        r.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade), Box(20, Faction.Player2, 0.5f, ClankKind.Blade));
        r.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade));
        Assert.Equal(0, r.P2.Hits);

        var restored = new Rig();
        restored.Combat.RestoreDedupe(r.Combat.CaptureDedupe());
        restored.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade));
        Assert.Equal(0, restored.P2.Hits);

        // A fresh attack (new HitId) is unaffected.
        r.Frame(Box(11, Faction.Player1, 0.5f, ClankKind.Blade));
        Assert.Equal(1, r.P2.Hits);
    }

    [Fact]
    public void WallBetweenOrigins_NoClank()
    {
        // TileSize-wide cells: a wall in column 1 between origins in columns 0 and 2.
        float t = Chunk.TileSize;
        var r = new Rig { Chunks = SimTerrain.FromAscii(".X.") };
        var span = new BoundingBox(0.5f * t, 0f, 2.5f * t, t);
        r.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade, span, origin: new Vector2(0.5f * t, 0.5f * t)),
                Box(20, Faction.Player2, 0.5f, ClankKind.Blade, span, origin: new Vector2(2.5f * t, 0.5f * t)));
        Assert.Empty(r.Clanks);
    }

    [Fact]
    public void ClankedAttacker_GetsRecoilAndHitstop_LikeAParry()
    {
        var r = new Rig();
        r.Frame(Box(10, Faction.Player1, 0.5f, ClankKind.Blade, recoil: 1f),
                Box(20, Faction.Player2, 0.5f, ClankKind.Blade, recoil: 1f));
        Assert.True(r.Combat.PeekRecoil(10).X < 0f);   // P1 swung right, pushed back left
        Assert.True(r.Combat.PeekRecoil(20).X > 0f);
        // The parry curve on the authored impulse (0 for a light swing like this one).
        float stop = CombatState.HitstopSecondsFor(HitResolver.ImpactStrength(new Vector2(100f, 0f)));
        Assert.Equal(stop, r.Combat.PeekHitstop(10));
        Assert.Equal(stop, r.Combat.PeekHitstop(20));
    }
}

// End to end: two real players in one Simulation swing at each other on the same
// frame — the slashes clank and neither takes damage; one swinging alone lands.
public class CombatClankSimTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // The shipped duel stage (MTile.Net.BotDuel's plain_close arena): both walk in
    // until `gap` px apart (so each faces the other), stand, then press on one frame.
    private (float D1, float D2, int Clanks) Duel(float gap, bool p1Swings, bool p2Swings)
    {
        var sim = MTile.Net.BotDuel.BuildArena("plain_close");
        int clanks = 0;
        sim.OnClank += (_, _, _, _) => clanks++;
        var p1 = sim.Player; var p2 = sim.SecondaryPlayers[0].Player;
        int pressAt = -1;
        for (int f = 0; f < 900; f++)
        {
            float d = p2.Body.Position.X - p1.Body.Position.X;
            bool walk = pressAt < 0 && d > gap;
            if (pressAt < 0 && !walk && p1.IsGrounded && p2.IsGrounded
                && System.MathF.Abs(p1.Body.Velocity.X) < 1f && System.MathF.Abs(p2.Body.Velocity.X) < 1f)
                pressAt = f + 10;
            bool press = pressAt >= 0 && f >= pressAt && f < pressAt + 2;
            var a = new PlayerInput { Right = walk, LeftClick = press && p1Swings,
                                      MouseWorldPosition = p2.Body.Position };
            var b = new PlayerInput { Left = walk, LeftClick = press && p2Swings,
                                      MouseWorldPosition = p1.Body.Position };
            sim.Step(a, b);
            if (pressAt >= 0 && f >= pressAt && f < pressAt + 12)
                output.WriteLine($"f{f} {p1.CurrentActionName} {p2.CurrentActionName} gap {d:F1}");
            if (pressAt >= 0 && f > pressAt + 60) break;
        }
        Assert.True(pressAt >= 0, "players never settled");
        return (p1.Combat.DamageTaken, p2.Combat.DamageTaken, clanks);
    }

    [Theory]
    [InlineData(22f)]    // bodies stop each other at ~21 px
    [InlineData(28f)]
    public void SimultaneousSlashes_Clank_NobodyHurt(float gap)
    {
        var solo = Duel(gap, p1Swings: true, p2Swings: false);
        output.WriteLine($"solo: {solo}");
        Assert.True(solo.D2 > 0f, "a lone slash at this gap must land, or the clank case proves nothing");

        var both = Duel(gap, p1Swings: true, p2Swings: true);
        output.WriteLine($"both: {both}");
        Assert.Equal(1, both.Clanks);
        Assert.Equal(0f, both.D1);
        Assert.Equal(0f, both.D2);
    }
}
