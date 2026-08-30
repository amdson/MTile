using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// The white-hit-flash (Drawing/HitFlash.cs). Two things worth pinning: the tracker's
// edge-detect (a stamp that stops advancing must not re-fire the flash, or a target
// stays lit forever), and that the entity-side stamp survives a snapshot round-trip —
// that is what keeps a rollback replay from re-flashing every hit it re-simulates.
public class HitFlashTests
{
    private static readonly EntityId A = new(1);
    private static readonly EntityId B = new(2);

    [Fact]
    public void Stamp_FiresOnce_AndDecaysToZero()
    {
        var t = new HitFlashTracker { Seconds = 0.10f, Peak = 1f };
        Assert.Equal(0f, t.Intensity(A));

        t.Stamp(A, 42);
        Assert.Equal(1f, t.Intensity(A), 3);

        // Re-stamping the SAME hit each frame must not restart the flash — it decays.
        t.Tick(0.05f); t.Stamp(A, 42);
        Assert.Equal(0.5f, t.Intensity(A), 2);

        t.Tick(0.05f); t.Stamp(A, 42);
        Assert.Equal(0f, t.Intensity(A));

        // Long after, still dark — the stamp is stale, not a live flash.
        for (int i = 0; i < 10; i++) { t.Tick(0.1f); t.Stamp(A, 42); }
        Assert.Equal(0f, t.Intensity(A));

        // A NEW hit relights it.
        t.Stamp(A, 43);
        Assert.Equal(1f, t.Intensity(A), 3);
    }

    [Fact]
    public void Targets_AreIndependent_AndZeroStampIsIgnored()
    {
        var t = new HitFlashTracker { Seconds = 0.10f, Peak = 1f };
        t.Stamp(A, 7);
        t.Stamp(B, 0);                       // never hit
        Assert.True(t.Intensity(A) > 0f);
        Assert.Equal(0f, t.Intensity(B));
    }

    // End-to-end through the wiring the game actually uses: a hit on a second player
    // lights that player and nobody else, and fades on real (render) dt.
    [Fact]
    public void HitFlashSystem_LightsThePlayerThatWasHit()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var sim = new Simulation(chunks, new Vector2(60f, 40f));
        var (p2, _) = sim.AddSecondaryPlayer(new Vector2(200f, 40f));
        p2.Faction = Faction.Player2;

        // A thrown clod on a course into p2 — the same path the block throw takes.
        sim.SpawnEntity(new LobbedAreaProjectile(new Vector2(120f, 40f), new Vector2(400f, 0f),
                                                 budget: 6, tileType: TileType.Dirt,
                                                 hitId: sim.HitIds.Next(), owner: Faction.Player1));

        var flash = new HitFlashSystem();
        float lit = 0f;
        for (int f = 0; f < 60 && lit <= 0f; f++)
        {
            sim.Step(default);
            flash.Collect(sim, 1f / 60f);
            lit = flash.Intensity(p2);
        }

        Assert.True(lit > 0f, "the player that got hit should flash");
        Assert.Equal(0f, flash.Intensity(sim.Player));   // the thrower is untouched

        // And it fades rather than sticking on.
        for (int i = 0; i < 30; i++) flash.Collect(sim, 1f / 60f);
        Assert.Equal(0f, flash.Intensity(p2));
    }

    [Fact]
    public void Whiten_BrightensWithoutTouchingAlpha()
    {
        var c = new Color(40, 80, 120, 255);
        Assert.Equal(c, HitFlashTracker.Whiten(c, 0f));
        Assert.Equal(Color.White, HitFlashTracker.Whiten(c, 1f));
        var half = HitFlashTracker.Whiten(c, 0.5f);
        Assert.True(half.R > c.R && half.G > c.G && half.B > c.B);
        Assert.Equal(255, half.A);
        // Translucent input stays valid premultiplied colour: RGB never exceeds alpha.
        var faded = HitFlashTracker.Whiten(new Color(10, 10, 10, 100), 1f);
        Assert.Equal(100, faded.A);
        Assert.True(faded.R <= faded.A);
    }

    [Fact]
    public void EntityHitStamp_IsSetOnHit_AndSurvivesASnapshotRoundTrip()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOO
            OOOOOOOOOOOO
            XXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var sim = new Simulation(chunks, new Vector2(40f, 20f));

        var ball = new PracticeBall(new Vector2(100f, 20f));
        sim.SpawnEntity(ball);
        sim.Step(default);
        Assert.Equal(0, ball.LastHitId);

        int hitId = sim.HitIds.Next();
        ball.OnHit(new Hitbox(ball.Body.Bounds, hitId, 0f, Vector2.Zero,
                              Faction.Player1, sim.Player.Id), default);
        Assert.Equal(hitId, ball.LastHitId);

        // The stamp is what the render shell edge-detects, so a restore has to bring it
        // back — otherwise a rollback replay re-fires the flash for hits already shown.
        var snap = sim.Snapshot();
        ball.LastHitId = 0;
        sim.Restore(snap);
        var restored = Assert.IsType<PracticeBall>(sim.Resolve(ball.Id));
        Assert.Equal(hitId, restored.LastHitId);
    }
}
