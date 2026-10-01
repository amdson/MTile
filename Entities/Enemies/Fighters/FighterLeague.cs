using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace MTile;

// The fighter league (Plans/FIGHTER_PACKAGE_GUIDE.md §6): every discovered package plus
// the six roster archetypes, round robin over every ORDERED pair (so both spawn sides
// are played) on every arena terrain, under every bout seed. Each bout goes through
// FightRecord.Describe + Record — the same path `--record-fight` uses — so a league
// bout can be saved and watched (`MTile.Desktop -- --fight <file>`) exactly as it was
// scored.
//
// Seeds (ArenaCondition): with one seed (the default, seed 0) every bout starts from
// the canonical 180 px line-up and the ladder is a single deterministic round robin.
// With N seeds each (pair, terrain) is played N times — N spawn geometries, N random
// streams per brain — and a score is a mean over conditions with a spread. That is
// the robust evaluation: a fighter tuned to one start loses its edge under the draw,
// and the ± column says how much of a ranking gap is real.
//
// Scoring: win 1, loss 0, draw 0.5 − DrawPenalty for both sides (plan §11.2, d = 0.1
// from configs/fighter_costs.json), divided by bouts played; a timeout is a draw
// (FighterArena, plan §11.1). Ties are broken on name (ordinal) so the order is total — NOT on health
// margin, which would be the timeout-on-health incentive at one remove. The mean
// health-fraction margin (own fraction left minus the opponent's) is still reported, as
// are mean µs per Decide and paid sense reads per frame, measured by wrapping each
// brain in a TimedController.
//
// TOOL CODE ONLY (MTile.Bench --league, FighterLeagueTests). It reads the wall clock
// (Stopwatch) and writes files; it is library-side so the CLI and the tests share it,
// and it is never called from the game loop. Everything that decides the ladder is
// deterministic: participants in a fixed order (packages by Name, then
// FighterRoster.All order), bouts in a fixed order, fresh timers per bout. Only the
// µs column is wall-clock and varies run to run.
public sealed class LeagueSettings
{
    // Play only bouts involving one of these participants (case-insensitive). Empty ⇒ all.
    public List<string> Only    { get; } = new();
    // Restrict the field to these participants (case-insensitive). Empty ⇒ everyone.
    public List<string> Include { get; } = new();
    // Terrain names from FighterArena.Terrains. Empty ⇒ all of them, in arena order.
    public List<string> Terrains { get; } = new();
    public int    Frames    { get; set; } = 720;
    // Bout seeds FirstSeed .. FirstSeed + Seeds - 1, each a condition (ArenaCondition)
    // every (pair, terrain) is played under. 1 seed from 0 = the canonical league.
    public int    Seeds     { get; set; } = 1;
    public ulong  FirstSeed { get; set; } = 0;
    // Bouts played at once. Results do not depend on it; µs/Decide gets noisier above 1.
    public int    Threads   { get; set; } = Environment.ProcessorCount;
    // Directory for <A>_vs_<B>_<terrain>[_s<seed>].fight.json files; null ⇒ do not save.
    public string OutDir    { get; set; }
    public string SimVersion { get; set; } = "";
    // Called after each bout (index, total) — progress printing.
    public Action<LeagueBout, int, int> OnBout { get; set; }
}

public sealed record LeagueParticipant(string Name, string Author, bool IsPackage, Func<FighterSpec> Make);

// Margin: left's health fraction left minus right's (negative ⇒ right came out ahead).
public sealed record LeagueBout(int Left, int Right, string LeftName, string RightName, string Terrain, ulong Seed,
                                ArenaResult Result, double Margin, DecideTimer LeftTimer, DecideTimer RightTimer, string SavedTo);

public sealed class LeagueStanding
{
    public LeagueParticipant Participant { get; init; }
    public int    Played, Wins, Draws, Losses;
    public double Score;          // (W + (0.5 − d)·D) / Played
    public double Margin;         // mean health-fraction margin
    // Over seeds: the score under each seed (index into LeagueResult.Seeds), its
    // standard error (sample std / √seeds; 0 with one seed) and its range.
    public double[] ScoreBySeed;
    public double   ScoreSe, ScoreMin, ScoreMax;
    public DecideTimer Compute { get; } = new();
    public bool   OverBudget;
}

public sealed class LeagueResult
{
    public IReadOnlyList<LeagueParticipant> Participants { get; init; }
    public IReadOnlyList<string>            Terrains     { get; init; }
    public IReadOnlyList<ulong>             Seeds        { get; init; }
    public IReadOnlyList<LeagueBout>        Bouts        { get; init; }
    // Ranked: score, then name. Margin is reported, never ranked on.
    public IReadOnlyList<LeagueStanding>    Standings    { get; init; }
    // Participants refused before play (their spec does not compile), with the reason.
    public IReadOnlyList<string>            Disqualified { get; init; }
    public int      Frames         { get; init; }
    public float    BudgetMicros   { get; init; }
    public TimeSpan Elapsed        { get; init; }
    // Matchups = ordered pairs × terrains played; Unanimous = those whose outcome (row
    // win / draw / loss) was the same under every seed. Equal with one seed.
    public int      Matchups       { get; init; }
    public int      Unanimous      { get; init; }

    public bool Seeded => Seeds.Count > 1;

    // The ladder. `timing: false` drops the wall-clock column (and the budget flag that
    // depends on it) — the part that must be bit-identical run to run. With more than
    // one seed the score carries its standard error over seeds and its range.
    public string LadderText(bool timing = true)
    {
        int nw = 4;
        foreach (var s in Standings) nw = Math.Max(nw, s.Participant.Name.Length);
        int aw = 6;
        foreach (var s in Standings) aw = Math.Max(aw, s.Participant.Author.Length);
        var sb = new StringBuilder();
        sb.Append($"{"rank",4}  {"name".PadRight(nw)}  {"author".PadRight(aw)}  {"score",6}");
        if (Seeded) sb.Append($"  {"±se",5}  {"min-max",11}");
        sb.Append($"  {"W-D-L",9}  {"margin",7}");
        if (timing) sb.Append($"  {"µs/Decide",9}");
        sb.Append($"  {"paid reads/frame",16}\n");
        for (int r = 0; r < Standings.Count; r++)
        {
            var s = Standings[r];
            sb.Append($"{r + 1,4}  {s.Participant.Name.PadRight(nw)}  {s.Participant.Author.PadRight(aw)}  {s.Score,6:0.000}");
            if (Seeded) sb.Append($"  {s.ScoreSe,5:0.000}  {($"{s.ScoreMin:0.000}-{s.ScoreMax:0.000}"),11}");
            sb.Append($"  {($"{s.Wins}-{s.Draws}-{s.Losses}"),9}  {s.Margin,7:+0.000;-0.000;0.000}");
            if (timing) sb.Append($"  {s.Compute.MeanMicros,9:0.00}");
            sb.Append($"  {s.Compute.PaidPerCall,16:0.000}");
            if (timing && s.OverBudget) sb.Append($"  OVER BUDGET (> {BudgetMicros:0.#} µs)");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Elo per participant (best first), then the mean per author.
    public string RatingsText()
    {
        var names = new List<string>();
        foreach (var s in Standings) names.Add(s.Participant.Name);
        var r = FighterRating.Fit(FighterRating.Games(Bouts), names);
        var rows = new List<LeagueStanding>(Standings);
        rows.Sort((a, b) => r[b.Participant.Name].CompareTo(r[a.Participant.Name]));
        int nw = 4, aw = 6;
        foreach (var s in rows) { nw = Math.Max(nw, s.Participant.Name.Length); aw = Math.Max(aw, s.Participant.Author.Length); }
        var sb = new StringBuilder();
        sb.Append($"{"rank",4}  {"name".PadRight(nw)}  {"author".PadRight(aw)}  {"rating",6}  {"score",6}\n");
        for (int i = 0; i < rows.Count; i++)
            sb.Append($"{i + 1,4}  {rows[i].Participant.Name.PadRight(nw)}  {rows[i].Participant.Author.PadRight(aw)}  " +
                      $"{r[rows[i].Participant.Name],6:0}  {rows[i].Score,6:0.000}\n");
        var byAuthor = new Dictionary<string, List<double>>();
        foreach (var s in rows)
            (byAuthor.TryGetValue(s.Participant.Author, out var l) ? l : byAuthor[s.Participant.Author] = new()).Add(r[s.Participant.Name]);
        var authors = new List<KeyValuePair<string, List<double>>>(byAuthor);
        authors.Sort((a, b) => b.Value.Average().CompareTo(a.Value.Average()));
        sb.Append($"\n{"author".PadRight(aw)}  {"bots",4}  {"mean",6}\n");
        foreach (var a in authors) sb.Append($"{a.Key.PadRight(aw)}  {a.Value.Count,4}  {a.Value.Average(),6:0}\n");
        return sb.ToString();
    }

    // One terrain's results: rows spawn left, columns spawn right. With one seed a cell
    // is W/L/D (row's view), frames played and the row's health-fraction margin; with
    // several it is the row's W-D-L count over seeds and its mean margin. Blank where
    // the pair was not played (--only).
    public string MatrixText(string terrain)
    {
        int n = Participants.Count;
        var cells = new List<LeagueBout>[n, n];
        foreach (var b in Bouts)
            if (b.Terrain == terrain) (cells[b.Left, b.Right] ??= new()).Add(b);
        int w = 14;
        foreach (var p in Participants) w = Math.Max(w, p.Name.Length + 1);
        var sb = new StringBuilder();
        sb.Append(Seeded
            ? $"[{terrain}] rows spawn left, columns spawn right; row W-D-L over {Seeds.Count} seeds, mean row margin\n"
            : $"[{terrain}] rows spawn left, columns spawn right; W = row wins, L = column wins, D = draw; frames, row margin\n");
        sb.Append(new string(' ', w));
        foreach (var p in Participants) sb.Append(p.Name.PadLeft(w));
        sb.Append('\n');
        for (int i = 0; i < n; i++)
        {
            sb.Append(Participants[i].Name.PadLeft(w));
            for (int j = 0; j < n; j++)
            {
                string c;
                if (i == j) c = "-";
                else if (cells[i, j] is not { } list) c = "";
                else if (!Seeded)
                {
                    var b = list[0];
                    var r = b.Result;
                    string wl = r.WinnerTeam == 1 ? "W" : r.WinnerTeam == 2 ? "L" : "D";
                    c = $"{wl}{r.Frames,4}f{b.Margin,6:+0.00;-0.00;0.00}";
                }
                else
                {
                    int wins = 0, draws = 0, losses = 0; double margin = 0;
                    foreach (var b in list)
                    {
                        if (b.Result.WinnerTeam == 1) wins++; else if (b.Result.WinnerTeam == 2) losses++; else draws++;
                        margin += b.Margin;
                    }
                    c = $"{wins}-{draws}-{losses}{margin / list.Count,6:+0.00;-0.00;0.00}";
                }
                sb.Append(c.PadLeft(w));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Score under each seed, one row per participant in ladder order: which fighters
    // are condition-sensitive, and which seeds were hard for whom.
    public string SeedTableText()
    {
        int nw = 4;
        foreach (var s in Standings) nw = Math.Max(nw, s.Participant.Name.Length);
        var sb = new StringBuilder();
        sb.Append($"{"name".PadRight(nw)}  {"mean",6}  {"±se",5}");
        foreach (var seed in Seeds) sb.Append($"  {("s" + seed),6}");
        sb.Append('\n');
        foreach (var s in Standings)
        {
            sb.Append($"{s.Participant.Name.PadRight(nw)}  {s.Score,6:0.000}  {s.ScoreSe,5:0.000}");
            for (int k = 0; k < Seeds.Count; k++) sb.Append($"  {s.ScoreBySeed[k],6:0.000}");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public string Markdown()
    {
        var sb = new StringBuilder();
        sb.Append("# Fighter league\n\n");
        sb.Append($"{Participants.Count} participants, {Bouts.Count} bouts, {Frames} frames each, " +
                  $"terrains {string.Join(", ", Terrains)}, " +
                  (Seeded ? $"{Seeds.Count} seeds ({Seeds[0]}..{Seeds[^1]}: seeded spawn geometry + brain streams, see ArenaCondition)"
                          : $"seed {Seeds[0]}" + (Seeds[0] == 0 ? " (the canonical 180 px start)" : "")) +
                  $"; {Elapsed.TotalSeconds:F1} s. Compute budget {BudgetMicros:0.#} µs per Decide (`DecideBudgetMicros`).\n\n");
        sb.Append($"Score: win 1, loss 0, draw {FighterCosts.Current.DrawScore:0.##} for both sides " +
                  $"(0.5 − DrawPenalty {FighterCosts.Current.DrawPenalty:0.##}, plan §11.2), over bouts played; " +
                  "a timeout is a draw; ties broken on name. " +
                  "Margin (mean health fraction left minus the opponent's) is informational.");
        if (Seeded)
            sb.Append($" `±se` is the standard error of the score over seeds; `min-max` its range. " +
                      $"{Unanimous} of {Matchups} matchups (ordered pair × terrain) ended the same way under every seed " +
                      $"({100.0 * Unanimous / Math.Max(Matchups, 1):0}%).");
        sb.Append("\n\n");
        sb.Append("## Ladder\n\n```\n").Append(LadderText()).Append("```\n\n");
        sb.Append("## Ratings\n\nBradley–Terry fit on the Elo scale over every bout (a draw is half a win), " +
                  "field mean 1500. By author: the mean over the author's bots.\n\n```\n").Append(RatingsText()).Append("```\n\n");
        if (Disqualified.Count > 0)
        {
            sb.Append("## Disqualified\n\n");
            foreach (var d in Disqualified) sb.Append("- ").Append(d.Replace("\n", "\n  ")).Append('\n');
            sb.Append('\n');
        }
        if (Seeded)
            sb.Append("## Score by seed\n\n```\n").Append(SeedTableText()).Append("```\n\n");
        sb.Append("## Matrices\n\n");
        foreach (var t in Terrains) sb.Append("```\n").Append(MatrixText(t)).Append("```\n\n");
        return sb.ToString();
    }
}

public static class FighterLeague
{
    // Packages in Discover() order (by Name), then the roster in FighterRoster.All order.
    public static List<LeagueParticipant> AllParticipants()
    {
        var list = new List<LeagueParticipant>();
        foreach (var p in FighterPackages.Discover())
        {
            var pkg = p;
            list.Add(new LeagueParticipant(pkg.Name, pkg.Author, true, pkg.Spec));
        }
        foreach (var make in FighterRoster.All)
            list.Add(new LeagueParticipant(make().Name, "roster", false, make));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in list)
            if (!seen.Add(p.Name))
                throw new InvalidOperationException($"Two league participants are named '{p.Name}' — package names must be unique and must not reuse a roster name.");
        return list;
    }

    public static LeagueResult Run(LeagueSettings settings)
    {
        settings ??= new LeagueSettings();
        if (settings.Seeds < 1) throw new ArgumentException("--seeds must be at least 1.");
        var sw = Stopwatch.StartNew();

        // ── Field ─────────────────────────────────────────────────────────────
        var all = AllParticipants();
        CheckNames(settings.Include, all, "--include");
        CheckNames(settings.Only,    all, "--only");
        var field = new List<LeagueParticipant>();
        var dq    = new List<string>();
        var model = new PhysicsCostModel();
        foreach (var p in all)
        {
            if (settings.Include.Count > 0 && !Contains(settings.Include, p.Name)) continue;
            var r = FighterCompiler.Compile(p.Make(), model);
            if (!r.IsValid) { dq.Add($"{p.Name}: does not compile under the default budget\n{r.Report()}"); continue; }
            field.Add(p);
        }

        var terrains = new List<string>();
        foreach (var (name, _) in FighterArena.Terrains)
            if (settings.Terrains.Count == 0 || Contains(settings.Terrains, name)) terrains.Add(name);
        foreach (var t in settings.Terrains)
            if (!terrains.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Unknown arena terrain '{t}' (flat / corridor / hills).");

        var seeds = new List<ulong>(settings.Seeds);
        for (int k = 0; k < settings.Seeds; k++) seeds.Add(settings.FirstSeed + (ulong)k);
        bool seeded = seeds.Count > 1;

        // ── Schedule: terrain, then left, then right, then seed ─────────────────
        var schedule = new List<(int a, int b, string t, int k)>();
        foreach (var t in terrains)
            for (int i = 0; i < field.Count; i++)
                for (int j = 0; j < field.Count; j++)
                {
                    if (i == j) continue;
                    if (settings.Only.Count > 0 && !Contains(settings.Only, field[i].Name)
                                                && !Contains(settings.Only, field[j].Name)) continue;
                    for (int k = 0; k < seeds.Count; k++) schedule.Add((i, j, t, k));
                }

        // ── Play ────────────────────────────────────────────────────────────────
        var maxHp = new float[field.Count];
        for (int i = 0; i < field.Count; i++) maxHp[i] = Math.Max(field[i].Make().Health, 1e-3f);
        // Bouts are independent sims, so they run in parallel; each lands in its schedule slot,
        // so the result (and everything scored from it) is the same as a serial run.
        var slots = new LeagueBout[schedule.Count];
        int done = 0;
        var gate = new object();
        var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, settings.Threads) };
        System.Threading.Tasks.Parallel.For(0, schedule.Count, options, n =>
        {
            var (i, j, t, k) = schedule[n];
            ulong seed = seeds[k];
            var rec = FightRecord.Describe(t, field[i].Make(), field[j].Make(), seed, settings.Frames);
            rec.SimVersion = settings.SimVersion ?? "";
            rec.Notes      = $"league bout: {field[i].Name} (left) vs {field[j].Name} (right) on {rec.Notes}";

            // Fresh timers per bout: tool-side counters never carry across bouts.
            var timers = new[] { new DecideTimer(), new DecideTimer() };
            var res = rec.Record(instrument: es =>
            {
                for (int e = 0; e < es.Count; e++)
                    es[e].Spec.Brain = TimedController.Wrap(es[e].Spec.Brain, timers[e]);
            });

            string saved = null;
            if (settings.OutDir != null)
            {
                string tag = seeded ? $"_s{seed}" : "";
                saved = Path.Combine(settings.OutDir, $"{field[i].Name}_vs_{field[j].Name}_{t}{tag}.fight.json");
                rec.Save(saved);
            }
            double margin = res.HealthLeft[0] / maxHp[i] - res.HealthLeft[1] / maxHp[j];
            var bout = new LeagueBout(i, j, field[i].Name, field[j].Name, t, seed, res, margin, timers[0], timers[1], saved);
            slots[n] = bout;
            if (settings.OnBout != null) lock (gate) settings.OnBout(bout, done++, schedule.Count);
        });
        var bouts = new List<LeagueBout>(slots);

        // ── Score ───────────────────────────────────────────────────────────────
        var budget    = FighterCosts.Current.DecideBudgetMicros;
        var drawScore = FighterCosts.Current.DrawScore;
        var standings = new LeagueStanding[field.Count];
        var pointsBySeed = new double[field.Count, seeds.Count];
        var playedBySeed = new int[field.Count, seeds.Count];
        var seedIndex = new Dictionary<ulong, int>();
        for (int k = 0; k < seeds.Count; k++) seedIndex[seeds[k]] = k;
        for (int i = 0; i < field.Count; i++) standings[i] = new LeagueStanding { Participant = field[i] };
        foreach (var b in bouts)
        {
            int k = seedIndex[b.Seed];
            pointsBySeed[b.Left,  k] += Tally(standings[b.Left],  b.Result.WinnerTeam, 1,  b.Margin, b.LeftTimer);
            pointsBySeed[b.Right, k] += Tally(standings[b.Right], b.Result.WinnerTeam, 2, -b.Margin, b.RightTimer);
            playedBySeed[b.Left, k]++;
            playedBySeed[b.Right, k]++;
        }
        for (int i = 0; i < field.Count; i++)
        {
            var s = standings[i];
            if (s.Played > 0) { s.Score = (s.Wins + drawScore * s.Draws) / s.Played; s.Margin /= s.Played; }
            s.OverBudget = s.Compute.MeanMicros > budget;
            s.ScoreBySeed = new double[seeds.Count];
            double sum = 0, sumSq = 0; s.ScoreMin = double.MaxValue; s.ScoreMax = double.MinValue;
            for (int k = 0; k < seeds.Count; k++)
            {
                double v = playedBySeed[i, k] > 0 ? pointsBySeed[i, k] / playedBySeed[i, k] : 0;
                s.ScoreBySeed[k] = v;
                sum += v; sumSq += v * v;
                s.ScoreMin = Math.Min(s.ScoreMin, v); s.ScoreMax = Math.Max(s.ScoreMax, v);
            }
            int m = seeds.Count;
            s.ScoreSe = m > 1 ? Math.Sqrt(Math.Max(0, (sumSq - sum * sum / m) / (m - 1)) / m) : 0;
        }
        var ranked = new List<LeagueStanding>(standings);
        ranked.Sort((x, y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            return c != 0 ? c : string.CompareOrdinal(x.Participant.Name, y.Participant.Name);
        });

        // Unanimity: per (left, right, terrain), did every seed give the same outcome?
        var outcomes = new Dictionary<(int, int, string), (int first, bool same)>();
        foreach (var b in bouts)
        {
            var key = (b.Left, b.Right, b.Terrain);
            if (outcomes.TryGetValue(key, out var o)) outcomes[key] = (o.first, o.same && o.first == b.Result.WinnerTeam);
            else outcomes[key] = (b.Result.WinnerTeam, true);
        }
        int unanimous = 0;
        foreach (var o in outcomes.Values) if (o.same) unanimous++;

        sw.Stop();
        return new LeagueResult
        {
            Participants = field, Terrains = terrains, Seeds = seeds, Bouts = bouts, Standings = ranked,
            Disqualified = dq, Frames = settings.Frames, BudgetMicros = budget, Elapsed = sw.Elapsed,
            Matchups = outcomes.Count, Unanimous = unanimous,
        };
    }

    // Returns the points this bout gave `s` (1 win, 0.5 − DrawPenalty draw, 0 loss).
    private static double Tally(LeagueStanding s, int winnerTeam, int team, double margin, DecideTimer t)
    {
        s.Played++;
        double points;
        if (winnerTeam == team) { s.Wins++;   points = 1; }
        else if (winnerTeam < 0) { s.Draws++; points = FighterCosts.Current.DrawScore; }
        else { s.Losses++; points = 0; }
        s.Margin += margin;
        s.Compute.Add(t);
        return points;
    }

    private static bool Contains(List<string> names, string name)
    {
        foreach (var n in names) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void CheckNames(List<string> names, List<LeagueParticipant> all, string flag)
    {
        foreach (var n in names)
            if (!all.Exists(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"{flag} '{n}' is not a league participant " +
                                            $"({string.Join(", ", all.ConvertAll(p => p.Name))}).");
    }
}
