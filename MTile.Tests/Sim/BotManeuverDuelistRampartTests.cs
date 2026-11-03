using System;
using System.Collections.Generic;
using System.Linq;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Rampart (Net/Bot/Duelists/Rampart): the terrain-zoner duelist. Name matches the
// "BotManeuver" term, so it runs in the bot group.
public class BotManeuverDuelistRampartTests(ITestOutputHelper output)
{
    [Fact]
    public void Scratch()
    {
        var env = Environment.GetEnvironmentVariable("RAMPART_SCRATCH");
        if (string.IsNullOrEmpty(env)) return;
        // "arena,arena;seed,seed;frames"
        var parts = env.Split(';');
        var arenas = parts[0].Split(',');
        var seeds = parts[1].Split(',').Select(int.Parse).ToArray();
        int frames = int.Parse(parts[2]);
        var made = new List<RampartBot>();
        DuelBot mine = (self, seed) => { var b = new RampartBot(seed, self); made.Add(b); return b; };
        foreach (var arena in arenas)
        foreach (int seed in seeds)
        for (int side = 0; side < 2; side++)
        {
            made.Clear();
            var r = side == 0 ? BotDuel.Run(arena, mine, BotDuel.Brawler, seed, frames)
                              : BotDuel.Run(arena, BotDuel.Brawler, mine, seed, frames);
            int w = side == 0 ? r.Winner : -r.Winner;
            output.WriteLine($"{(side == 0 ? "R=P1" : "R=P2")} {r}  => {(w > 0 ? "WIN" : w < 0 ? "LOSS" : "draw")}");
            string F(Dictionary<string, float> d) => string.Join(" ", d.OrderByDescending(k => k.Value).Select(k => $"{k.Key.Replace("Rampart", "R").Replace("Maneuver", "")}:{k.Value:F1}"));
            output.WriteLine("   dealt " + F(made[0].Dealt));
            output.WriteLine("   taken " + F(made[0].Taken));
            output.WriteLine("   " + string.Join(" ", made[0].Starts.OrderByDescending(k => k.Value).Select(k => $"{k.Key.Replace("Rampart", "R").Replace("Maneuver", "")}:{k.Value}")));
        }
    }
}
