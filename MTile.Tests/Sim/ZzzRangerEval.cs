using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Scratch A/B for the routing duelist: Bulldog vs Ranger (= Bulldog + RouteManeuver,
// Bulldog's wall climb off) against the field on the league arenas, tuning seeds, both
// sides. Routing must not cost it anything on flat ground. Run by hand, Release.
public class ZzzRangerEval(ITestOutputHelper output)
{
    [Fact]
    public void AB()
    {
        var opponents = new (string, DuelBot)[]
        {
            ("Rampart", RampartBot.Create), ("Citadel", CitadelBot.Create), ("Riposte", RiposteBot.Create),
            ("Bulldog", BulldogBot.Create), ("Brawler", BotDuel.Brawler),
        };
        var challengers = new (string, DuelBot)[] { ("Bulldog", BulldogBot.Create), ("Ranger", RangerBot.Create) };
        var jobs = new List<(string C, string O, string A, int S, bool P1, DuelBot Cb, DuelBot Ob)>();
        foreach (var (cn, cb) in challengers) foreach (var (on, ob) in opponents)
        foreach (var a in BotDuel.Arenas) foreach (int s in Enumerable.Range(1, 6)) foreach (bool p1 in new[] { true, false })
            jobs.Add((cn, on, a, s, p1, cb, ob));
        var pts = new ConcurrentDictionary<(string, string), float>();
        Parallel.ForEach(jobs, j =>
        {
            var r = j.P1 ? BotDuel.Run(j.A, j.Cb, j.Ob, j.S) : BotDuel.Run(j.A, j.Ob, j.Cb, j.S);
            int w = j.P1 ? r.Winner : -r.Winner;
            float p = w > 0 ? 1f : w == 0 ? 0.5f : 0f;
            pts.AddOrUpdate((j.C, j.O), p, (_, o) => o + p);
        });
        output.WriteLine("points out of 36 (3 arenas x 6 seeds x 2 sides)");
        output.WriteLine($"{"",-16}" + string.Join("", opponents.Select(o => $"{o.Item1,9}")) + "    total");
        foreach (var (cn, _) in challengers)
            output.WriteLine($"{cn,-16}" + string.Join("", opponents.Select(o => $"{pts.GetValueOrDefault((cn, o.Item1)),9:F1}"))
                             + $"  {opponents.Sum(o => pts.GetValueOrDefault((cn, o.Item1))),7:F1}");
    }
}
