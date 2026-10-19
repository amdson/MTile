using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The terrain half of rollback: block paint / place / burst / peel-and-throw and the
// avalanches they set off, under the same three-sim check as DepthsDeterminismTests:
//   A  plain straight run (the reference),
//   B  rollback as RollbackSession does it: snapshot at frame k, step ahead on WRONG
//      predicted inputs, Restore, replay the true ones,
//   C  Snapshot->Restore round trip before every frame.
// Two players on wall_dummy (flat stone floor, tall wall) cycle through every build
// gesture while walking and jumping into what they build, so the movement states that
// hold soft contacts (wall slide, ledge hang, jumps) meet terrain that changes under them.
public class TerrainRollbackTests(ITestOutputHelper output)
{
    private static Simulation Build()
        => new(new GameConfig { SpawnSecondPlayer = true }, Stages.Get("wall_dummy"));

    // One gesture per 70-frame phase, players a phase apart. `wrong` shifts the script
    // in time — a different, equally plausible input stream for the mispredicted pass.
    private static PlayerInput Input(Simulation g, int f, int who, bool wrong)
    {
        var p = who == 0 ? g.Player : g.SecondaryPlayers[0].Player;
        int t = (wrong ? f + 17 * (who + 2) : f) + who * 70;
        int phase = (t / 70) % 6, k = t % 70;
        int dir = ((t / 210) % 2 == 0) ? 1 : -1;      // mostly toward the wall, sometimes back
        float wob = MathF.Sin(t * 0.21f) * 12f;
        var i = new PlayerInput
        {
            Right = dir > 0 && k % 23 < 15,
            Left  = dir < 0 && k % 23 < 15,
            Space = t % 37 < 5,
            Up    = t % 89 < 6,
            Down  = t % 113 < 3,
        };
        Vector2 aim;
        switch (phase)
        {
            case 0:   // drag-build: paint along the floor ahead
                i.RightClick = true;
                aim = new Vector2(dir * (24f + k * 0.6f), 14f + wob);
                break;
            case 1:   // precise single-block placement, stacking walls/ledges ahead
                i.Shift = true; i.RightClick = k % 12 < 8;
                aim = new Vector2(dir * 26f, -8f - (k % 3) * 12f);
                break;
            case 2:   // block burst into air: RMB to settle, then LMB
                i.RightClick = true; i.LeftClick = k > 10 && k % 14 < 6;
                aim = new Vector2(dir * 100f, -20f + wob);
                break;
            case 3:   // peel the floor, then swipe-release (the throw). The press waits
                      // out the previous phase's recovery (a slash tail is ~36f), which
                      // the block grab won't start over.
                i.Shift = true; i.LeftClick = k >= 40 && k < 60;
                aim = k < 60 ? new Vector2(dir * 20f + wob, 30f)
                             : new Vector2(dir * (20f + (k - 60) * 12f), 30f - (k - 60) * 12f);
                break;
            case 4:   // charge inside solid, sweep out fast, release in air: the eruption
                      // (a MassBall), which is what sets off an avalanche
                i.RightClick = k < 52;
                aim = k < 40 ? new Vector2(dir * 10f + wob, 34f)
                             : new Vector2(dir * (10f + (k - 40) * 14f), 34f - (k - 40) * 14f);
                break;
            default:  // movement only: run and jump at whatever was built
                i.Space = k % 19 < 7;
                aim = new Vector2(dir * 60f, 0f);
                break;
        }
        i.MouseWorldPosition = p.Body.Position + aim;
        return i;
    }

    private static string Sig(Simulation g)
    {
        var sp = g.SecondaryPlayers[0].Player;
        return $"{g.Checksum():X16}|{g.Frame}|"
             + $"{g.Player.CurrentStateName}/{g.Player.CurrentActionName}|{sp.CurrentStateName}/{sp.CurrentActionName}|"
             + $"{g.Entities.Count}|{g.Chunks.ActiveSprouts.Count}/{g.Chunks.PendingSprouts.Count}";
    }

    [Fact]
    public void BuildingAndAvalanches_StayDeterministic_UnderRollbackAndSnapshotRoundTrip()
    {
        const int N = 3000, Block = 40, Ahead = 6;
        var a = Build(); var b = Build(); var c = Build();
        int rollbacks = 0, maxSprouts = 0, terrainChanges = 0;
        var kinds = new SortedSet<string>(); var states = new SortedSet<string>(); var actions = new SortedSet<string>();
        ulong lastHash = a.Chunks.TerrainHash;

        void Run(Simulation g, int f, bool wrong) => g.Step(Input(g, f, 0, wrong), Input(g, f, 1, wrong));

        for (int f0 = 0; f0 < N; f0 += Block)
        {
            int f1 = Math.Min(N, f0 + Block);
            var refSigs = new List<string>();
            for (int f = f0; f < f1; f++)
            {
                Run(a, f, false);
                refSigs.Add(Sig(a));
                foreach (var e in a.Entities) kinds.Add(e.Kind.ToString());
                foreach (var pc in new[] { a.Player, a.SecondaryPlayers[0].Player })
                { states.Add(pc.CurrentStateName); actions.Add(pc.CurrentActionName); }
                maxSprouts = Math.Max(maxSprouts, a.Chunks.ActiveSprouts.Count + a.Chunks.PendingSprouts.Count);
                if (a.Chunks.TerrainHash != lastHash) { terrainChanges++; lastHash = a.Chunks.TerrainHash; }
            }

            for (int f = f0; f < f1; f++)
            {
                c.Restore(c.Snapshot());
                Run(c, f, false);
                Assert.True(refSigs[f - f0] == Sig(c), $"round-trip desync at frame {f}\n A {refSigs[f - f0]}\n C {Sig(c)}");
            }

            int k = f0 + Block / 2;
            for (int f = f0; f < k; f++) Run(b, f, false);
            var snap = b.Snapshot();
            for (int i = 0; i < Ahead; i++) Run(b, k + i, true);
            b.Restore(snap);
            rollbacks++;
            for (int f = k; f < f1; f++)
            {
                Run(b, f, false);
                Assert.True(refSigs[f - f0] == Sig(b), $"rollback desync at frame {f} (rolled back from {k})\n A {refSigs[f - f0]}\n B {Sig(b)}");
            }
        }
        output.WriteLine($"frames={N} rollbacks={rollbacks} terrainChanges={terrainChanges} maxSprouts={maxSprouts}");
        output.WriteLine($"entities=[{string.Join(",", kinds)}]");
        output.WriteLine($"states=[{string.Join(",", states)}]");
        output.WriteLine($"actions=[{string.Join(",", actions)}]");

        // Coverage: the scenario must actually build, cascade and move against it, or
        // a pass proves nothing.
        Assert.True(terrainChanges > 50, $"terrain barely changed ({terrainChanges})");
        Assert.True(maxSprouts > 0, "no sprouts ever grew");
        Assert.Contains("BlockPaintAction", actions);
        Assert.Contains("BlockPlaceAction", actions);
    }
}
