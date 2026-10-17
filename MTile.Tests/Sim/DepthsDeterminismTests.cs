using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Plans/WEB_COOP.md step 5: the co-op "depths" stage must survive rollback before it is
// played online. Three sims run the same scripted two-player session:
//   A  plain straight run (the reference),
//   B  rolls back exactly as RollbackSession does: snapshot at frame k, step ahead n
//      frames on WRONG predicted inputs, Restore, replay with the true inputs,
//   C  Snapshot->Restore round trip before every frame.
// Checksum (+ stage vars, respawn point, player states) must match A every frame.
// Stage-level events (teleports, kills) are scripted per frame and applied identically on
// every pass over that frame, the way network inputs are.
public class DepthsDeterminismTests(ITestOutputHelper output)
{
    private static Simulation Build()
        => new(new GameConfig { SpawnSecondPlayer = true }, Stages.Get("depths"));

    private static DepthsMap Layout() => DepthsLayout.Build(7, Stages.DepthsShapes());

    private static PlayerInput Input(int f, int who, bool wrong)
    {
        int t = wrong ? f + 11 * (who + 3) : f;      // wrong prediction = a different script
        var i = new PlayerInput
        {
            Right = ((t / 40 + who) % 3) != 1,
            Left  = ((t / 40 + who) % 3) == 1,
            Up    = t % 97 < 4,
            Down  = t % 131 < 3,
            Space = t % (23 + 5 * who) < 4,
            Shift = t % 150 < 20,
            LeftClick  = (t / 7 + who) % 4 != 0,
            RightClick = (t / 53) % 5 == 2,
            Num1 = t % 211 == 0, Num2 = t % 211 == 70, Num3 = t % 211 == 140,
        };
        float a = t * 0.13f + who;
        i.MouseWorldPosition = new Vector2(MathF.Cos(a), MathF.Sin(a)) * 90f;
        return i;
    }

    // Aim relative to the player so attacks actually swing at nearby enemies.
    private static PlayerInput Aimed(Simulation g, int f, int who, bool wrong)
    {
        var i = Input(f, who, wrong);
        var p = who == 0 ? g.Player : g.SecondaryPlayers[0].Player;
        i.MouseWorldPosition += p.Body.Position;
        return i;
    }

    private static void Kill(Simulation g)
    {
        foreach (var e in g.Entities.ToList()) if (Stages.DepthsIsHostile(e)) e.Health = 0f;
    }

    private static void Tele(Simulation g, DepthsRoom r)
    {
        var s = r.FloorSpots[0] - new Vector2(0f, 6f);
        g.Player.Body.Position = s; g.Player.Body.Velocity = Vector2.Zero;
        var p2 = g.SecondaryPlayers[0].Player;
        p2.Body.Position = s + new Vector2(24f, 0f); p2.Body.Velocity = Vector2.Zero;
    }

    // Scripted stage events at frame f. Reads only the sim + the fixed layout.
    private static void Hook(Simulation g, DepthsMap map, int f)
    {
        var rooms = map.Rooms;
        switch (f)
        {
            case 5:   Tele(g, rooms[0]); break;
            case 300: Kill(g); break;                         // clear wave(s) of room 0
            case 480: Kill(g); break;
            case 700: Kill(g); break;
            case 900: g.StageVars[0] = 4; Tele(g, rooms[4]); break;   // The Nursery: nests
            case 1250: g.SecondaryPlayers[0].Player.Health = -1f; break;  // co-op death + respawn
            case 1500: Kill(g); break;
            case 1700: Kill(g); break;
            case 1900: g.StageVars[0] = 7; Tele(g, rooms[7]); break;  // boss room
            case 2400: g.Player.Health = -1f; break;
            case 2600: Kill(g); break;
        }
    }

    private static string Sig(Simulation g)
    {
        var sp = g.SecondaryPlayers[0].Player;
        return $"{g.Checksum():X16}|{string.Join(",", g.StageVars)}|{g.RespawnPoint}|{g.Frame}|"
             + $"{g.Player.CurrentStateName}/{g.Player.CurrentActionName}|{sp.CurrentStateName}/{sp.CurrentActionName}|"
             + $"{g.Entities.Count}";
    }

    [Fact]
    public void Depths_StaysDeterministic_UnderRollbackAndSnapshotRoundTrip()
    {
        const int N = 2700, Block = 40, Ahead = 6;
        var map = Layout();
        var a = Build(); var b = Build(); var c = Build();
        int rollbacks = 0, maxEnt = 0;
        var kinds = new SortedSet<string>(); var p2Hp = new List<float>();

        void Run(Simulation g, int f, bool wrong)
        {
            Hook(g, map, f);
            g.Step(Aimed(g, f, 0, wrong), Aimed(g, f, 1, wrong));
        }

        for (int f0 = 0; f0 < N; f0 += Block)
        {
            int f1 = Math.Min(N, f0 + Block);
            var refSigs = new List<string>();
            for (int f = f0; f < f1; f++)
            {
                Run(a, f, false);
                refSigs.Add(Sig(a));
                maxEnt = Math.Max(maxEnt, a.Entities.Count);
                foreach (var e in a.Entities) kinds.Add(e.Kind.ToString());
                p2Hp.Add(a.SecondaryPlayers[0].Player.Health);
            }

            // C: snapshot round trip before each frame.
            for (int f = f0; f < f1; f++)
            {
                c.Restore(c.Snapshot());
                Run(c, f, false);
                Assert.True(refSigs[f - f0] == Sig(c), $"round-trip desync at frame {f}\n A {refSigs[f - f0]}\n C {Sig(c)}");
            }

            // B: rollback at a point inside the block, then straight on to the end.
            int k = f0 + Block / 2;
            for (int f = f0; f < k; f++) Run(b, f, false);
            var snap = b.Snapshot();
            for (int i = 0; i < Ahead; i++) Run(b, k + i, true);   // mispredicted
            b.Restore(snap);
            rollbacks++;
            for (int f = k; f < f1; f++)
            {
                Run(b, f, false);
                Assert.True(refSigs[f - f0] == Sig(b), $"rollback desync at frame {f} (rolled back from {k})\n A {refSigs[f - f0]}\n B {Sig(b)}");
            }
        }
        output.WriteLine($"frames={N} rollbacks={rollbacks} maxEntities={maxEnt} deaths={a.PlayerDeaths} kinds=[{string.Join(",", kinds)}] p2HpMin={p2Hp.Min()} vars={string.Join(",", a.StageVars)}");
        Assert.True(maxEnt > 3, "scenario never spawned enemies");
    }
}
