using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Wave 3 (opus-2, Thornbull) reported: when its touch killed Brick a few frames BEFORE the
// rollback gate's frame-60 snapshot, the replay after Restore diverged from the first step —
// the brain worked around it by never attacking before frame 65. Cause: ReadState derived
// `Committed = has an action`, but a Contact touch runs UNCOMMITTED (flight must survive the
// hit), so a restore taken during its 0.85 s recovery locked a body that was free in the live
// run. The flag is now snapshotted (EntityData.ActionCommitted). These pin it: a snapshot
// taken while a touch is in flight — kill or no kill — must replay bit-identically.
public class FighterRollbackKillTests(ITestOutputHelper output)
{
    // A Thornbull-shaped rammer: a heavy touch, fast legs, big body.
    private static FighterSpec Rammer(float damage)
    {
        var s = FighterRoster.Brick();
        s.Name = "TestRammer";
        s.Health = 5f; s.Armor = 0f; s.Strength = 1f; s.Density = 0.6f;
        s.GroundPower = 450f; s.JumpImpulse = 0f;
        s.EnergyReserve = 2f; s.EnergyRegen = 0.8f;
        s.Actions.Clear();
        var c = ActionSpec.Default(ActionKind.Contact);
        c.Damage = damage; c.Knockback = default;
        s.Actions.Add(c);
        s.EngageRange = 20f;
        return s;
    }

    private static List<ArenaEntry> Pair(FighterSpec left, FighterSpec right) => new()
    {
        new(left,  FighterArena.LeftSpawn,  1),
        new(right, FighterArena.RightSpawn, 2),
    };

    // Steps until the rammer's first touch lands (Brick loses health), then `after` more
    // frames, snapshots, and checks N frames of replay. Returns the frame of the first hit.
    private int SnapshotAfterFirstTouch(FighterSpec rammer, int after, int n, bool expectKill)
    {
        var r = FighterCompiler.Compile(rammer, new PhysicsCostModel());
        Assert.True(r.IsValid, string.Join("\n", r.Violations));

        var entries = Pair(rammer, FighterRoster.Brick());
        var bots = new EnemyEntity[2];
        var sim = new Simulation(FighterArena.Flat(), FighterArena.PlayerPark,
                                 g => FighterArena.Populate(g, entries, null, bots));
        float full = bots[1].Health;
        int hit = -1, frame = 0;
        while (hit < 0 && frame < 300) { sim.Step(default); frame++; if (bots[1].Health < full) hit = frame; }
        Assert.True(hit > 0, "the rammer never touched Brick in 300 frames");
        for (int f = 0; f < after; f++) { sim.Step(default); frame++; }
        Assert.Equal(expectKill, bots[1].IsDead);
        Assert.False(bots[0].IsActionCommitted, "a Contact touch is never committed");
        output.WriteLine($"first touch at frame {hit}, snapshot at frame {frame}, Brick dead: {bots[1].IsDead}, rammer action: {bots[0].CurrentActionName}");
        var snap = sim.Snapshot();

        var live = new List<ulong>(n);
        for (int f = 0; f < n; f++) { sim.Step(default); live.Add(sim.Checksum()); }
        sim.Restore(snap);
        for (int f = 0; f < n; f++)
        {
            sim.Step(default);
            ulong c = sim.Checksum();
            Assert.True(c == live[f], $"replay diverged {f + 1} steps after a restore taken {after} frames after the touch (frame {frame}).");
        }
        return hit;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    public void SnapshotDuringTheTouchsRecovery_AfterAKill_ReplaysBitIdentically(int framesAfterTouch)
        => SnapshotAfterFirstTouch(Rammer(damage: 9f), framesAfterTouch, 120, expectKill: true);

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    public void SnapshotDuringTheTouchsRecovery_NoKill_ReplaysBitIdentically(int framesAfterTouch)
        => SnapshotAfterFirstTouch(Rammer(damage: 1f), framesAfterTouch, 120, expectKill: false);

    [Fact]
    public void SnapshotLongAfterTheKill_ReplaysBitIdentically()
        => SnapshotAfterFirstTouch(Rammer(damage: 9f), 120, 120, expectKill: true);
}
