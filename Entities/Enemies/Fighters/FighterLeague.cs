using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MTile;

// The fighter league (Plans/FIGHTER_PACKAGE_GUIDE.md §6): every discovered package plus
// the six roster archetypes, round robin over every ORDERED pair (so both spawn sides
// are played) on every arena terrain. Each bout goes through FightRecord.Describe +
// Record — the same path `--record-fight` uses — so a league bout can be saved and
// watched (`MTile.Desktop -- --fight <file>`) exactly as it was scored.
//
// Scoring: win 1, draw ½, divided by bouts played; ties broken on mean health-fraction
// margin (own fraction left minus the opponent's), then on name (ordinal) so the order
// is total. Per participant it also reports mean µs per Decide and paid sense reads per
// frame, measured by wrapping each brain in a TimedController.
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
    // Directory for <A>_vs_<B>_<terrain>.fight.json files; null ⇒ do not save.
    public string OutDir    { get; set; }
    public string SimVersion { get; set; } = "";
    // Called after each bout (index, total) — progress printing.
    public Action<LeagueBout, int, int> OnBout { get; set; }
}

public sealed record LeagueParticipant(string Name, string Author, bool IsPackage, Func<FighterSpec> Make);

// Margin: left's health fraction left minus right's (negative ⇒ right came out ahead).
public sealed record LeagueBout(int Left, int Right, string LeftName, string RightName, string Terrain, ArenaResult Result, double Margin,
                                DecideTimer LeftTimer, DecideTimer RightTimer, string SavedTo);

public sealed class LeagueStanding
{
    public LeagueParticipant Participant { get; init; }
    public int    Played, Wins, Draws, Losses;
    public double Score;          // (W + ½D) / Played
    public double Margin;         // mean health-fraction margin
    public DecideTimer Compute { get; } = new();
    public bool   OverBudget;
}

public sealed class LeagueResult
{
    public IReadOnlyList<LeagueParticipant> Participants { get; init; }
    public IReadOnlyList<string>            Terrains     { get; init; }
    public IReadOnlyList<LeagueBout>        Bouts        { get; init; }
    // Ranked: score, then margin, then name.
    public IReadOnlyList<LeagueStanding>    Standings    { get; init; }
    // Participants refused before play (their spec does not compile), with the reason.
    public IReadOnlyList<string>            Disqualified { get; init; }
    public int      Frames         { get; init; }
    public float    BudgetMicros   { get; init; }
    public TimeSpan Elapsed        { get; init; }

    // The ladder. `timing: false` drops the wall-clock column (and the budget flag that
    // depends on it) — the part that must be bit-identical run to run.
    public string LadderText(bool timing = true)
    {
        int nw = 4;
        foreach (var s in Standings) nw = Math.Max(nw, s.Participant.Name.Length);
        int aw = 6;
        foreach (var s in Standings) aw = Math.Max(aw, s.Participant.Author.Length);
        var sb = new StringBuilder();
        sb.Append($"{"rank",4}  {"name".PadRight(nw)}  {"author".PadRight(aw)}  {"score",6}  {"W-D-L",9}  {"margin",7}");
        if (timing) sb.Append($"  {"µs/Decide",9}");
        sb.Append($"  {"paid reads/frame",16}\n");
        for (int r = 0; r < Standings.Count; r++)
        {
            var s = Standings[r];
            sb.Append($"{r + 1,4}  {s.Participant.Name.PadRight(nw)}  {s.Participant.Author.PadRight(aw)}  " +
                      $"{s.Score,6:0.000}  {($"{s.Wins}-{s.Draws}-{s.Losses}"),9}  {s.Margin,7:+0.000;-0.000;0.000}");
            if (timing) sb.Append($"  {s.Compute.MeanMicros,9:0.00}");
            sb.Append($"  {s.Compute.PaidPerCall,16:0.000}");
            if (timing && s.OverBudget) sb.Append($"  OVER BUDGET (> {BudgetMicros:0.#} µs)");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // One terrain's results: rows spawn left, columns spawn right. W = row wins, L = column
    // wins, D = draw; then frames played and the row's health-fraction margin. Blank where
    // the pair was not played (--only).
    public string MatrixText(string terrain)
    {
        int n = Participants.Count;
        var cell = new LeagueBout[n, n];
        foreach (var b in Bouts) if (b.Terrain == terrain) cell[b.Left, b.Right] = b;
        int w = 14;
        foreach (var p in Participants) w = Math.Max(w, p.Name.Length + 1);
        var sb = new StringBuilder();
        sb.Append($"[{terrain}] rows spawn left, columns spawn right; W = row wins, L = column wins, D = draw; frames, row margin\n");
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
                else if (cell[i, j] is not { } b) c = "";
                else
                {
                    var r = b.Result;
                    string wl = r.WinnerTeam == 1 ? "W" : r.WinnerTeam == 2 ? "L" : "D";
                    c = $"{wl}{r.Frames,4}f{b.Margin,6:+0.00;-0.00;0.00}";
                }
                sb.Append(c.PadLeft(w));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public string Markdown()
    {
        var sb = new StringBuilder();
        sb.Append("# Fighter league\n\n");
        sb.Append($"{Participants.Count} participants, {Bouts.Count} bouts, {Frames} frames each, " +
                  $"terrains {string.Join(", ", Terrains)}; {Elapsed.TotalSeconds:F1} s. " +
                  $"Compute budget {BudgetMicros:0.#} µs per Decide (`DecideBudgetMicros`).\n\n");
        sb.Append("Score: win 1, draw ½, over bouts played; tie-break on mean health-fraction margin.\n\n");
        sb.Append("## Ladder\n\n```\n").Append(LadderText()).Append("```\n\n");
        if (Disqualified.Count > 0)
        {
            sb.Append("## Disqualified\n\n");
            foreach (var d in Disqualified) sb.Append("- ").Append(d.Replace("\n", "\n  ")).Append('\n');
            sb.Append('\n');
        }
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

        // ── Schedule: terrain, then left, then right ────────────────────────────
        var schedule = new List<(int a, int b, string t)>();
        foreach (var t in terrains)
            for (int i = 0; i < field.Count; i++)
                for (int j = 0; j < field.Count; j++)
                {
                    if (i == j) continue;
                    if (settings.Only.Count > 0 && !Contains(settings.Only, field[i].Name)
                                                && !Contains(settings.Only, field[j].Name)) continue;
                    schedule.Add((i, j, t));
                }

        // ── Play ────────────────────────────────────────────────────────────────
        var maxHp = new float[field.Count];
        for (int i = 0; i < field.Count; i++) maxHp[i] = Math.Max(field[i].Make().Health, 1e-3f);
        var bouts = new List<LeagueBout>(schedule.Count);
        for (int k = 0; k < schedule.Count; k++)
        {
            var (i, j, t) = schedule[k];
            var entries = new List<ArenaEntry>
            {
                new(field[i].Make(), FighterArena.LeftSpawn,         1),
                new(field[j].Make(), FighterArena.RightSpawnFor(t),  2),
            };
            var rec = FightRecord.Describe(t, entries, FighterArena.PlayerPark, settings.Frames);
            rec.SimVersion = settings.SimVersion ?? "";
            rec.Notes      = $"league bout: {field[i].Name} (left) vs {field[j].Name} (right) on {t}";

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
                saved = Path.Combine(settings.OutDir, $"{field[i].Name}_vs_{field[j].Name}_{t}.fight.json");
                rec.Save(saved);
            }
            double margin = res.HealthLeft[0] / maxHp[i] - res.HealthLeft[1] / maxHp[j];
            var bout = new LeagueBout(i, j, field[i].Name, field[j].Name, t, res, margin, timers[0], timers[1], saved);
            bouts.Add(bout);
            settings.OnBout?.Invoke(bout, k, schedule.Count);
        }

        // ── Score ───────────────────────────────────────────────────────────────
        var budget = FighterCosts.Current.DecideBudgetMicros;
        var standings = new LeagueStanding[field.Count];
        for (int i = 0; i < field.Count; i++) standings[i] = new LeagueStanding { Participant = field[i] };
        foreach (var b in bouts)
        {
            Tally(standings[b.Left],  b.Result.WinnerTeam, 1,  b.Margin, b.LeftTimer);
            Tally(standings[b.Right], b.Result.WinnerTeam, 2, -b.Margin, b.RightTimer);
        }
        foreach (var s in standings)
        {
            if (s.Played > 0) { s.Score = (s.Wins + 0.5 * s.Draws) / s.Played; s.Margin /= s.Played; }
            s.OverBudget = s.Compute.MeanMicros > budget;
        }
        var ranked = new List<LeagueStanding>(standings);
        ranked.Sort((x, y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            if (c != 0) return c;
            c = y.Margin.CompareTo(x.Margin);
            return c != 0 ? c : string.CompareOrdinal(x.Participant.Name, y.Participant.Name);
        });

        sw.Stop();
        return new LeagueResult
        {
            Participants = field, Terrains = terrains, Bouts = bouts, Standings = ranked,
            Disqualified = dq, Frames = settings.Frames, BudgetMicros = budget, Elapsed = sw.Elapsed,
        };
    }

    private static void Tally(LeagueStanding s, int winnerTeam, int team, double margin, DecideTimer t)
    {
        s.Played++;
        if (winnerTeam == team) s.Wins++;
        else if (winnerTeam < 0) s.Draws++;
        else s.Losses++;
        s.Margin += margin;
        s.Compute.Add(t);
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
