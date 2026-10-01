using System;
using System.Collections.Generic;

namespace MTile;

// Bradley–Terry ratings on the Elo scale, fitted over a list of games by Hunter's MM
// iteration (each player's strength γ_i ← W_i / Σ_j n_ij / (γ_i + γ_j), with W_i its wins and
// n_ij the games it played against j). A draw is half a win for each side. One virtual drawn
// game against a strength-1 opponent is added per player, so a player that never wins (or
// never loses) still gets a finite rating. R = 1500 + 400·log10 γ, shifted so the mean over
// the `anchor` names is 1500: the pool is the yardstick, submissions are measured against it.
// Pure arithmetic on the game list — deterministic, no clock, no randomness.
public static class FighterRating
{
    public const double Anchor = 1500, Scale = 400;

    public static Dictionary<string, double> Fit(IEnumerable<(string A, string B, double ScoreA)> games,
                                                 IEnumerable<string> anchor, int iterations = 500, double prior = 1)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        int Id(string n)
        {
            if (!index.TryGetValue(n, out int i)) { i = names.Count; index.Add(n, i); names.Add(n); }
            return i;
        }
        var list = new List<(int A, int B, double S)>();
        foreach (var (a, b, s) in games)
        {
            if (s < 0 || s > 1) throw new ArgumentException($"Game score must be in [0,1]: {a} vs {b} = {s}.");
            list.Add((Id(a), Id(b), s));
        }
        int n = names.Count;
        if (n == 0) return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var wins  = new double[n];
        var count = new Dictionary<(int, int), double>();
        foreach (var (a, b, s) in list)
        {
            wins[a] += s; wins[b] += 1 - s;
            var key = a < b ? (a, b) : (b, a);
            count[key] = count.TryGetValue(key, out double c) ? c + 1 : 1;
        }
        var pairs = new List<(int I, int J, double N)>(count.Count);
        foreach (var kv in count) pairs.Add((kv.Key.Item1, kv.Key.Item2, kv.Value));

        var g = new double[n];
        for (int i = 0; i < n; i++) g[i] = 1;
        var denom = new double[n];
        for (int it = 0; it < iterations; it++)
        {
            for (int i = 0; i < n; i++) denom[i] = prior / (g[i] + 1);     // the virtual draw vs γ = 1
            foreach (var (i, j, m) in pairs)
            {
                double d = m / (g[i] + g[j]);
                denom[i] += d; denom[j] += d;
            }
            double logMean = 0;
            for (int i = 0; i < n; i++)
            {
                g[i] = (wins[i] + 0.5 * prior) / denom[i];
                logMean += Math.Log(g[i]);
            }
            double norm = Math.Exp(-logMean / n);
            for (int i = 0; i < n; i++) g[i] *= norm;
        }

        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = Scale * Math.Log10(g[i]);
        double shift = 0; int anchored = 0;
        foreach (var a in anchor ?? Array.Empty<string>())
            if (index.TryGetValue(a, out int i)) { shift += r[i]; anchored++; }
        shift = anchored > 0 ? shift / anchored : 0;

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++) result[names[i]] = Anchor + r[i] - shift;
        return result;
    }

    // A league's bouts as games: left scores 1 / 0.5 / 0.
    public static IEnumerable<(string A, string B, double ScoreA)> Games(IEnumerable<LeagueBout> bouts)
    {
        foreach (var b in bouts)
            yield return (b.LeftName, b.RightName, b.Result.WinnerTeam == 1 ? 1 : b.Result.WinnerTeam == 2 ? 0 : 0.5);
    }
}
