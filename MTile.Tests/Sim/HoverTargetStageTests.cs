using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// The hover_targets stage (Stages "hover_targets"): three weightless balls that float
// in place until hit, fall under gravity once hit, and despawn HoverTarget.RespawnSeconds after
// their first hit — the stage ticker then puts a fresh one back at the slot.
public class HoverTargetStageTests
{
    private static Simulation BuildSim()
    {
        // Open air with a floor well below the targets (y ≈ 38): they never touch it.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -8, originTileY: 0);
        return new Simulation(terrain, new Vector2(-60f, 30f), Stages.Get("hover_targets").Populate);
    }

    private static HoverTarget[] Targets(Simulation sim) =>
        sim.Entities.OfType<HoverTarget>().Where(e => !e.IsDead).OrderBy(e => e.Mass).ToArray();

    [Fact]
    public void Targets_Float_ThenDespawnAndRespawnAfterAHit()
    {
        var sim = BuildSim();
        sim.Step(default);
        var t = Targets(sim);
        Assert.Equal(3, t.Length);
        Assert.True(t[0].Mass < 2.5f && t[1].Mass == 2.5f && t[2].Mass > 2.5f);

        var home = t[1].Body.Position;
        for (int i = 0; i < 60; i++) sim.Step(default);
        Assert.Equal(home, t[1].Body.Position);   // weightless: no drift without a hit

        int hitId = sim.HitIds.Next();
        t[1].OnHit(new Hitbox(t[1].Body.Bounds, hitId, 1f, new Vector2(300f, 0f),
                              Faction.Player1, sim.Player.Id), default);
        for (int i = 0; i < 30; i++) sim.Step(default);
        Assert.True(t[1].Body.Position.X > home.X + 1f, "a hit should send it coasting");
        Assert.True(t[1].Body.Position.Y > home.Y + 1f, "a hit should switch gravity on");

        int respawnFrames = SimFrames.FromSeconds(HoverTarget.RespawnSeconds, Simulation.FixedDt);
        for (int i = 0; i < respawnFrames; i++) sim.Step(default);
        Assert.True(t[1].IsDead);
        var fresh = Targets(sim);
        Assert.Equal(3, fresh.Length);
        Assert.NotSame(t[1], fresh[1]);
        Assert.Equal(home, fresh[1].Body.Position);
    }

    [Fact]
    public void DespawnCountdown_SurvivesASnapshotRoundTrip()
    {
        var sim = BuildSim();
        sim.Step(default);
        var t = Targets(sim)[0];
        t.OnHit(new Hitbox(t.Body.Bounds, sim.HitIds.Next(), 1f, new Vector2(100f, 0f),
                           Faction.Player1, sim.Player.Id), default);
        sim.Step(default);

        var snap = sim.Snapshot();
        sim.Restore(snap);
        var restored = Assert.IsType<HoverTarget>(sim.Resolve(t.Id));
        Assert.True(restored.Hit);
        Assert.Equal(1f, restored.GravityScale);
        Assert.Equal(t.Mass, restored.Mass);
    }
}
