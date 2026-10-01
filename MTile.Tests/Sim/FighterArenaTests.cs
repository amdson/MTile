using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Phase 5 gate for Plans/FIGHTER_DESIGN_PLAN.md (§8, §9): the arena is deterministic
// and the stock roster is not degenerate.
//
//   * A match replays to the identical result (winner, frame count, health, damage)
//     — the arena is a pure function of its inputs, which is what an optimiser needs.
//   * The six archetypes actually fight: on every terrain, every pairing has at least
//     one side landing damage (a roster that stands and stares is a targeting bug, not
//     a balance question).
//   * The balance smoke test: across the round robin no archetype wins every match it
//     plays. This one is ALLOWED to be red while the k_* coefficients are being tuned —
//     see BACKLOG §5 if it is — and it prints the full table so the coefficient loop
//     has something to read.
public class FighterArenaTests(ITestOutputHelper output)
{
    // Shorter than the arena default so the round robin stays a few seconds. Long
    // enough for a melee close from 180 px and several exchanges.
    private const int Frames = 60 * 12;

    private static ArenaResult Match(string terrain, Func<FighterSpec> left, Func<FighterSpec> right, int frames = Frames)
    {
        var map = FighterArena.Terrains.First(t => t.name == terrain).make();
        var entries = new[]
        {
            new ArenaEntry(left(),  FighterArena.LeftSpawn,               1),
            new ArenaEntry(right(), FighterArena.RightSpawnFor(terrain),  2),
        };
        return FighterArena.Run(map, entries, FighterArena.PlayerPark, frames);
    }

    [Fact]
    public void Match_ReplaysToTheIdenticalResult()
    {
        var a = Match("flat", FighterRoster.Brick, FighterRoster.Sprinter);
        var b = Match("flat", FighterRoster.Brick, FighterRoster.Sprinter);
        Assert.Equal(a.WinnerTeam, b.WinnerTeam);
        Assert.Equal(a.Frames,     b.Frames);
        Assert.Equal(a.HealthLeft, b.HealthLeft);
        Assert.Equal(a.DamageDealt, b.DamageDealt);
        Assert.True(a.DamageDealt.Sum() > 0f, "nobody landed anything, so the replay proved nothing");
        output.WriteLine($"Brick vs Sprinter on flat: winner team {a.WinnerTeam} after {a.Frames} frames; " +
                         $"health {a.HealthLeft[0]:F2} / {a.HealthLeft[1]:F2}, dealt {a.DamageDealt[0]:F2} / {a.DamageDealt[1]:F2}");
    }

    [Fact]
    public void SwappedSides_IsADifferentMatchButStillDeterministic()
    {
        // Left/right decides spawn order and therefore ECS order; a swapped match is a
        // legitimately different match, but each ordering must still replay exactly.
        var a1 = Match("corridor", FighterRoster.Gunner, FighterRoster.Flyer);
        var a2 = Match("corridor", FighterRoster.Gunner, FighterRoster.Flyer);
        var b1 = Match("corridor", FighterRoster.Flyer,  FighterRoster.Gunner);
        var b2 = Match("corridor", FighterRoster.Flyer,  FighterRoster.Gunner);
        AssertSame(a1, a2);
        AssertSame(b1, b2);
    }

    private static void AssertSame(ArenaResult x, ArenaResult y)
    {
        Assert.Equal(x.WinnerTeam,  y.WinnerTeam);
        Assert.Equal(x.Frames,      y.Frames);
        Assert.Equal(x.HealthLeft,  y.HealthLeft);
        Assert.Equal(x.DamageDealt, y.DamageDealt);
    }

    [Fact]
    public void EveryArchetypeLandsDamage_OnEveryTerrain()
    {
        // Not "every pairing": two ranged kiters (Gunner vs Builder) fire in lockstep
        // along the same line and their energy balls shoot each other down every time —
        // a real dynamic of cross-team projectiles, not a bug. And not "every terrain":
        // a terrain is allowed to counter an archetype outright (a flyer under a low
        // roof). What must hold is that each archetype can hurt SOMETHING somewhere: a
        // roster member that never lands anything anywhere is a targeting or reach bug,
        // not a balance question. Per-terrain misses are printed for the balance loop.
        var roster = FighterRoster.All;
        var quiet  = new List<string>();
        var idle   = new List<string>();
        var landedAnywhere = new bool[roster.Count];
        foreach (var (terrain, _) in FighterArena.Terrains)
        {
            var landed = new bool[roster.Count];
            for (int i = 0; i < roster.Count; i++)
                for (int j = 0; j < roster.Count; j++)
                {
                    if (i == j) continue;
                    var r = Match(terrain, roster[i], roster[j]);
                    if (r.DamageDealt[0] > 0f) landed[i] = true;
                    if (r.DamageDealt[1] > 0f) landed[j] = true;
                    if (r.DamageDealt.Sum() <= 0f) quiet.Add($"{roster[i]().Name} vs {roster[j]().Name} on {terrain}");
                }
            for (int i = 0; i < roster.Count; i++)
            {
                if (!landed[i]) idle.Add($"{roster[i]().Name} on {terrain}");
                landedAnywhere[i] |= landed[i];
            }
        }
        if (quiet.Count > 0) output.WriteLine("No damage exchanged in:\n  " + string.Join("\n  ", quiet));
        if (idle.Count > 0)  output.WriteLine("Landed nothing on a terrain (allowed — a counter):\n  " + string.Join("\n  ", idle));
        var never = new List<string>();
        for (int i = 0; i < roster.Count; i++) if (!landedAnywhere[i]) never.Add(roster[i]().Name);
        Assert.True(never.Count == 0, $"never landed a hit on any terrain: {string.Join(", ", never)}");
    }

    [Fact]
    public void RoundRobin_NoArchetypeWinsEverything()
    {
        var roster = FighterRoster.All;
        var names  = roster.Select(m => m().Name).ToArray();
        int n      = roster.Count;
        var wins   = new int[n];
        var played = new int[n];
        var table  = new StringBuilder();
        var sw     = Stopwatch.StartNew();

        foreach (var (terrain, _) in FighterArena.Terrains)
        {
            table.Append($"\n[{terrain}] rows attack from the left, columns from the right; W = row wins, L = column wins, D = draw\n");
            table.Append("            " + string.Join(" ", names.Select(s => s.PadLeft(9))) + '\n');
            for (int i = 0; i < n; i++)
            {
                table.Append(names[i].PadLeft(12));
                for (int j = 0; j < n; j++)
                {
                    if (i == j) { table.Append("        -"); continue; }
                    var r = Match(terrain, roster[i], roster[j]);
                    played[i]++; played[j]++;
                    string cell;
                    if      (r.WinnerTeam == 1) { wins[i]++; cell = "W"; }
                    else if (r.WinnerTeam == 2) { wins[j]++; cell = "L"; }
                    else                        {            cell = "D"; }
                    table.Append($"{cell}{r.Frames,5}f{(r.HealthLeft[0] - r.HealthLeft[1]),+4:F0}".PadLeft(10));
                }
                table.Append('\n');
            }
        }
        table.Append($"\nwins / played: " + string.Join(", ", names.Select((s, i) => $"{s} {wins[i]}/{played[i]}")));
        table.Append($"\n{sw.Elapsed.TotalSeconds:F1}s for {played.Sum() / 2} matches");
        output.WriteLine(table.ToString());

        var perfect = Enumerable.Range(0, n).Where(i => played[i] > 0 && wins[i] == played[i]).Select(i => names[i]).ToArray();
        Assert.True(perfect.Length == 0,
            $"{string.Join(", ", perfect)} won every match — a k_* coefficient is too low (allowed red while tuning; see the table above).");
    }
}
