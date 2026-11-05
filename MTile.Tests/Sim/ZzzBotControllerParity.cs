// Scratch parity check for Plans/BOT_BEHAVIOR_ARCHITECTURE.md M2 (Bulldog family on the
// BotController). Seeds 1-4 x 3 arenas x both sides = 24 bouts per matchup, 3600 frames.
// Run by hand, Release:
//   dotnet test MTile.Tests/MTile.Tests.csproj -c Release --filter "FullyQualifiedName~ZzzBotControllerParity" --logger "console;verbosity=detailed"
//
// before (untouched tree @6c4d420):
//   Bulldog vs Brawler           24.0/24  K/D 76/40  dmg 439.3/270.8
//   Ranger vs Brawler            24.0/24  K/D 78/38  dmg 441.9/267.7
//   Ranger vs Riposte             9.0/24  K/D 63/66  dmg 371.1/382.9
//   Ranger-NoLearn vs Brawler    24.0/24  K/D 84/40  dmg 448.4/284.3
//
// after (M2: BulldogBot on BotController, ChaseStance/StepOutStance, SpikeGuardConstraint,
// RoutePlanner behind the executor; Ranger's learner = BetLedger, softmax, Lambda 0):
//   Bulldog vs Brawler           22.0/24  K/D 78/44  dmg 450.5/292.8
//   Ranger vs Brawler            24.0/24  K/D 87/39  dmg 487.0/266.1
//   Ranger vs Riposte            11.0/24  K/D 63/65  dmg 372.2/380.6
//   Ranger-NoLearn vs Brawler    24.0/24  K/D 83/34  dmg 462.4/280.1
//
// after M3 (maneuvers declare their own Category/Move; ManeuverRegistry deleted; Guard/Pulse/
// waves/throws Still, Dash/DownAir/BulldogPunish Exclusive, Burst Forbid(Space|Down)):
//   Bulldog vs Brawler           23.0/24  K/D 74/45  dmg 423.1/294.5
//   Ranger vs Brawler            23.5/24  K/D 93/39  dmg 489.4/271.1
//   Ranger vs Riposte            11.5/24  K/D 61/64  dmg 367.7/375.5
//   Ranger-NoLearn vs Brawler    24.0/24  K/D 80/43  dmg 457.2/272.8
//
// after M4 bets (hand-authored HP bets on every Bulldog-family maneuver + both stances; Score
// ties broken in list order by explicit 0.01 steps, since float ulps had reordered them):
//   Bulldog vs Brawler           23.0/24  K/D 74/45  dmg 423.1/294.5
//   Ranger vs Brawler            24.0/24  K/D 90/31  dmg 478.0/254.1
//   Ranger vs Riposte            14.0/24  K/D 66/63  dmg 381.1/370.0
//   Ranger-NoLearn vs Brawler    24.0/24  K/D 80/43  dmg 457.2/272.8
//   (Bulldog and NoLearn are bit-identical to M3: argmax over the bets keeps Score's order.)
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class ZzzBotControllerParity(ITestOutputHelper output)
{
    private sealed class Tally
    {
        public float Points, Dealt, Taken;
        public int   Kills, Deaths;
    }

    [Fact]
    public void Table()
    {
        var matchups = new (string Name, DuelBot Me, DuelBot Opp)[]
        {
            ("Bulldog vs Brawler",        BulldogBot.Create, BotDuel.Brawler),
            ("Ranger vs Brawler",         RangerBot.Create,  BotDuel.Brawler),
            ("Ranger vs Riposte",         RangerBot.Create,  RiposteBot.Create),
            ("Ranger-NoLearn vs Brawler", RangerBot.NoLearn, BotDuel.Brawler),
        };
        var jobs = new List<(int M, string A, int S, bool P1)>();
        for (int m = 0; m < matchups.Length; m++)
            foreach (var a in BotDuel.Arenas) foreach (int s in new[] { 1, 2, 3, 4 }) foreach (bool p1 in new[] { true, false })
                jobs.Add((m, a, s, p1));
        var tallies = new ConcurrentDictionary<int, Tally>();
        for (int m = 0; m < matchups.Length; m++) tallies[m] = new Tally();
        // One bout per matchup keeps its brain so the controller trace can be printed.
        var traced = new ConcurrentDictionary<int, BulldogBot>();
        Parallel.ForEach(jobs, j =>
        {
            var (_, make, opp) = matchups[j.M];
            bool keep = j.A == "hall_close" && j.S == 1 && j.P1;
            DuelBot me = (self, seed) =>
            {
                var bot = make(self, seed);
                if (keep && bot is BulldogBot b) traced[j.M] = b;
                return bot;
            };
            var r = j.P1 ? BotDuel.Run(j.A, me, opp, j.S) : BotDuel.Run(j.A, opp, me, j.S);
            int w = j.P1 ? r.Winner : -r.Winner;
            var t = tallies[j.M];
            lock (t)
            {
                t.Points += w > 0 ? 1f : w == 0 ? 0.5f : 0f;
                t.Kills  += j.P1 ? r.KillsByP1 : r.KillsByP2;
                t.Deaths += j.P1 ? r.KillsByP2 : r.KillsByP1;
                t.Dealt  += j.P1 ? r.DamageToP2 : r.DamageToP1;
                t.Taken  += j.P1 ? r.DamageToP1 : r.DamageToP2;
            }
        });
        output.WriteLine("points out of 24 (3 arenas x seeds 1-4 x 2 sides), 3600 frames");
        for (int m = 0; m < matchups.Length; m++)
        {
            var t = tallies[m];
            output.WriteLine($"{matchups[m].Name,-27} {t.Points,5:F1}/24  K/D {t.Kills}/{t.Deaths}  dmg {t.Dealt:F1}/{t.Taken:F1}");
        }
        for (int m = 0; m < matchups.Length; m++)
        {
            if (!traced.TryGetValue(m, out var b) || b.Controller == null) continue;
            output.WriteLine($"\n== {matchups[m].Name}, hall_close s1 as P1: controller trace");
            output.WriteLine(b.Controller.Trace.Report());
            if (b.Ledger != null) output.WriteLine(b.Ledger.Report());
        }
    }
}
