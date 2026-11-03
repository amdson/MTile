using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The duelist league (Plans/DUELIST_LEAGUE.md). Not a regression test — a Zzz harness,
// excluded from every test group; run by hand:
//   dotnet test MTile.Tests -c Release --filter FullyQualifiedName~ZzzDuelistLeague
// Seeds 11-16 are held out: the entrants tuned on seeds 1-4.
public class ZzzDuelistLeague(ITestOutputHelper output)
{
    [Fact]
    public void RunLeague()
    {
        var bots = new List<(string Name, DuelBot Bot)>
        {
            ("Bulldog", BulldogBot.Create),
            ("Rampart", RampartBot.Create),
            ("Riposte", RiposteBot.Create),
            ("Brawler", BotDuel.Brawler),
        };
        int[] seeds = { 11, 12, 13, 14, 15, 16 };
        var log = new StringBuilder();
        // Head to head: points[i,j] = points i took off j.
        var pts = new float[bots.Count, bots.Count];
        var games = new int[bots.Count, bots.Count];
        var perArena = new Dictionary<(int, string), float>();
        var table = BotDuel.RoundRobin(bots, seeds, log: line =>
        {
            log.AppendLine(line);
            // "<A> (P1) vs <B> (P2): <arena> s<seed>: ... → P1|P2|draw"
            int a = bots.FindIndex(b => line.StartsWith(b.Name + " (P1)"));
            int b2 = bots.FindIndex(b => line.Contains(" vs " + b.Name + " (P2)"));
            string arena = BotDuel.Arenas.First(x => line.Contains(": " + x + " s"));
            float pa = line.EndsWith("P1") ? 1f : line.EndsWith("P2") ? 0f : 0.5f;
            pts[a, b2] += pa; pts[b2, a] += 1f - pa; games[a, b2]++; games[b2, a]++;
            perArena[(a, arena)] = perArena.GetValueOrDefault((a, arena)) + pa;
            perArena[(b2, arena)] = perArena.GetValueOrDefault((b2, arena)) + 1f - pa;
        });

        var sb = new StringBuilder();
        sb.AppendLine("STANDINGS");
        foreach (var s in table) sb.AppendLine(s.ToString());
        sb.AppendLine().AppendLine("HEAD TO HEAD (row's points vs column, out of games)");
        sb.Append($"{"",-10}"); foreach (var b in bots) sb.Append($"{b.Name,12}"); sb.AppendLine();
        for (int i = 0; i < bots.Count; i++)
        {
            sb.Append($"{bots[i].Name,-10}");
            for (int j = 0; j < bots.Count; j++)
                sb.Append(i == j ? $"{"-",12}" : $"{pts[i, j],7:F1}/{games[i, j],-4}");
            sb.AppendLine();
        }
        sb.AppendLine().AppendLine("POINTS BY ARENA");
        for (int i = 0; i < bots.Count; i++)
            sb.AppendLine($"{bots[i].Name,-10}" + string.Join("  ", BotDuel.Arenas.Select(a => $"{a} {perArena.GetValueOrDefault((i, a)):F1}")));
        output.WriteLine(sb.ToString());
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Plans"))) dir = dir.Parent;
        File.WriteAllText(Path.Combine(dir!.FullName, "Plans", "DUELIST_LEAGUE_raw.txt"), sb + "\nBOUTS\n" + log);
    }
}
