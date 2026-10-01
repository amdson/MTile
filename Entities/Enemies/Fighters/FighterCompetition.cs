using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MTile;

// The fighter-package competition (Plans/FIGHTER_COMPETITION.md, from wave 3): LLM authors
// each submit one package file and are scored one-versus-pool, never all-to-all. A thin
// layer over FighterLeague.Run(LeagueSettings) with Include/Only.
//
// The protocol in five lines:
//   1. Pool  — the frozen field of wave N: every participant that exists when the wave is cut.
//   2. Split — rank the pool by the latest ladder, snake-deal it into Dev (visible) and
//      Holdout (hidden); roster bots + ExampleBrawler are always Dev and are not dealt.
//      The sort is stable on the input (score desc, ties keep input order): a parsed ladder
//      is already in the league's full-precision order, which 3-decimal scores can't recover.
//   3. Hide  — the competition base branch `git rm`s the Holdout files (HoldoutFiles).
//   4. Score — each submission vs Holdout (PRIMARY), vs Dev (overfit gap = dev − holdout),
//      plus a head-to-head league among submissions (tiebreak only).
//   5. Roll  — pool N+1 = pool N ∪ wave-N submissions, re-split from a fresh full ladder.
//
// Pool files live in Fights/competition/wave<N>.pool.json (committed; the report goes beside
// it as wave<N>.md). CLI: MTile.Bench --competition split|files|score|roll.
//
// TOOL CODE ONLY, like FighterLeague: never called from the game loop. Everything ranked
// on is deterministic (same pool + submissions ⇒ same numbers and the same Markdown);
// the wall clock appears only in CompetitionReport.Elapsed and CompetitionPool.Created,
// neither of which is ranked on or printed in the report body's tables.
public sealed class CompetitionPool
{
    public int          Wave      { get; set; }
    public string       Created   { get; set; }        // ISO date (yyyy-MM-dd, UTC)
    public string       Source    { get; set; }        // ladder file / description the split came from
    public List<string> Dev       { get; set; } = new();
    public List<string> Holdout   { get; set; } = new();
    public List<string> Terrains  { get; set; } = new();   // empty ⇒ FighterArena.Terrains order
    public int          Frames    { get; set; } = 1800;
    public int          Seeds     { get; set; } = 16;
    public ulong        FirstSeed { get; set; } = 100;

    // Dev then Holdout.
    [JsonIgnore]
    public IEnumerable<string> All
    {
        get
        {
            foreach (var n in Dev) yield return n;
            foreach (var n in Holdout) yield return n;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static CompetitionPool Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Competition pool file not found: {path}", path);
        CompetitionPool pool;
        try { pool = JsonSerializer.Deserialize<CompetitionPool>(File.ReadAllText(path), JsonOpts); }
        catch (JsonException ex) { throw new InvalidDataException($"Competition pool file {path} is not valid JSON: {ex.Message}", ex); }
        if (pool == null) throw new InvalidDataException($"Competition pool file {path} is empty.");
        pool.Dev      ??= new();
        pool.Holdout  ??= new();
        pool.Terrains ??= new();
        return pool;
    }

    public void Save(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts) + "\n");
    }

    // Roster names (FighterRoster.All order) + "ExampleBrawler".
    public static IReadOnlyList<string> DefaultAlwaysDev()
    {
        var list = new List<string>();
        foreach (var make in FighterRoster.All) list.Add(make().Name);
        list.Add("ExampleBrawler");
        return list;
    }

    // Stratified snake deal. `ranked` is sorted here by score descending, STABLE on its input
    // order: equal scores keep the order they were passed in. A ladder parsed from a league
    // file is already in the league's full-precision score-then-name order (its printed
    // 3-decimal scores can tie where the real ones don't), so it is reproduced exactly.
    // Callers that build `ranked` by hand must pass it in rank order. Deterministic for a
    // given input. Names in `alwaysDev` go to Dev and are not dealt; the
    // rest are dealt rank 1 → Dev, 2 → Holdout, 3 → Holdout, 4 → Dev, 5 → Dev, … Both lists
    // come out in ladder order, with each name in its canonical (participant) spelling.
    public static CompetitionPool Split(int wave, IReadOnlyList<(string Name, double Score)> ranked,
                                        IEnumerable<string> alwaysDev = null, string source = null)
    {
        if (ranked == null) throw new ArgumentNullException(nameof(ranked));
        var all  = FighterLeague.AllParticipants();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<(string Name, double Score, int Index)>(ranked.Count);
        foreach (var (name, score) in ranked)
        {
            var p = all.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"'{name}' is not a league participant " +
                                                   $"({string.Join(", ", all.ConvertAll(x => x.Name))}).");
            if (!seen.Add(p.Name)) throw new ArgumentException($"'{name}' appears twice in the ranked list.");
            rows.Add((p.Name, score, rows.Count));
        }
        rows.Sort((x, y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            return c != 0 ? c : x.Index.CompareTo(y.Index);
        });

        var fixedDev = new HashSet<string>(alwaysDev ?? DefaultAlwaysDev(), StringComparer.OrdinalIgnoreCase);
        var pool = new CompetitionPool
        {
            Wave    = wave,
            Created = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Source  = source ?? "",
        };
        int dealt = 0;
        foreach (var (name, _, _) in rows)
        {
            if (fixedDev.Contains(name)) { pool.Dev.Add(name); continue; }
            int m = dealt++ % 4;
            (m == 0 || m == 3 ? pool.Dev : pool.Holdout).Add(name);
        }
        return pool;
    }

    // Repo-relative paths (forward slashes) of Entities/Enemies/Fighters/Packages/<Name>.cs for
    // every Holdout name — ready for `git rm` in any checkout. Throws if a file is missing under
    // `repoRoot` or does not declare that Name: the file is the unit of hiding, so the package
    // class must live in a file named after its Name.
    public IReadOnlyList<string> HoldoutFiles(string repoRoot)
    {
        var list = new List<string>(Holdout.Count);
        foreach (var name in Holdout)
        {
            string rel  = $"Entities/Enemies/Fighters/Packages/{name}.cs";
            string full = Path.Combine(repoRoot ?? "", "Entities", "Enemies", "Fighters", "Packages", name + ".cs");
            if (!File.Exists(full))
                throw new FileNotFoundException($"Holdout '{name}' has no package file {rel} — a hidden package must live in a file named after its Name.", full);
            if (!File.ReadAllText(full).Contains("\"" + name + "\"", StringComparison.Ordinal))
                throw new InvalidDataException($"{rel} does not declare the package name \"{name}\".");
            list.Add(rel);
        }
        return list;
    }

    // Participants present in FighterLeague.AllParticipants() but not in the pool — this
    // wave's submissions, in participant order.
    public List<string> Submissions()
    {
        var inPool = new HashSet<string>(All, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var p in FighterLeague.AllParticipants())
            if (!inPool.Contains(p.Name)) list.Add(p.Name);
        return list;
    }
}

// Parses the "## Ladder" code block of a Fights/*_league.md written by LeagueResult.Markdown().
// Column positions come from the header line (rank, name, author, score, …), so free-text
// authors with spaces parse too. Returns (Name, Score) in ladder order.
public static class LeagueLadderFile
{
    public static List<(string Name, double Score)> ParseScores(string markdown)
    {
        if (markdown == null) throw new ArgumentNullException(nameof(markdown));
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length && lines[i].Trim() != "## Ladder") i++;
        while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) i++;
        if (i >= lines.Length) throw new FormatException("No \"## Ladder\" code block found (expected a Fights/*_league.md).");
        i++;
        if (i >= lines.Length) throw new FormatException("The ladder block is empty.");

        string header = lines[i++];
        int nameAt   = header.IndexOf("name",   StringComparison.Ordinal);
        int authorAt = header.IndexOf("author", StringComparison.Ordinal);
        int scoreAt  = header.IndexOf("score",  StringComparison.Ordinal);
        if (!header.TrimStart().StartsWith("rank", StringComparison.Ordinal) || nameAt < 0 || authorAt < nameAt || scoreAt < authorAt)
            throw new FormatException($"Ladder header not recognised: '{header}'.");
        int scoreEnd = scoreAt + "score".Length;   // right-aligned, width 6

        var result = new List<(string Name, double Score)>();
        for (; i < lines.Length; i++)
        {
            string row = lines[i];
            if (row.TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
            if (row.Trim().Length == 0) continue;
            if (row.Length < scoreEnd) throw new FormatException($"Ladder row too short: '{row}'.");
            string name  = row.Substring(nameAt, authorAt - nameAt).Trim();
            string score = row.Substring(scoreEnd - 6, 6).Trim();
            if (!double.TryParse(score, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new FormatException($"Ladder row has no score: '{row}'.");
            result.Add((name, v));
        }
        if (result.Count == 0) throw new FormatException("The ladder block has no rows.");
        return result;
    }
}

public sealed class CompetitionScore
{
    public string Name, Author;
    public LeagueStanding Holdout, Dev;    // the submission's standing in each one-vs-pool league
    public double HoldoutScore => Holdout.Score;
    public double DevScore     => Dev.Score;
    public double OverfitGap   => DevScore - HoldoutScore;
    public LeagueStanding HeadToHead;      // null when < 2 submissions
    public int Rank;                       // 1-based, after sorting
}

public sealed class CompetitionReport
{
    public CompetitionPool Pool;
    public IReadOnlyList<CompetitionScore> Scores;   // holdout score desc, then head-to-head score desc, then ordinal name
    public LeagueResult HeadToHead;                  // null when < 2 submissions
    public IReadOnlyList<string> Disqualified;       // submissions whose spec does not compile, with reason
    public TimeSpan Elapsed;                         // wall clock — never printed in Markdown()

    private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    // Bit-identical run to run: no timing anywhere, invariant culture for our own numbers.
    public string Markdown()
    {
        var p = Pool;
        var terrains = new List<string>();
        foreach (var (name, _) in FighterArena.Terrains)
            if (p.Terrains.Count == 0 || p.Terrains.Exists(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
                terrains.Add(name);
        ulong lastSeed = p.FirstSeed + (ulong)Math.Max(p.Seeds - 1, 0);

        var sb = new StringBuilder();
        sb.Append("# Fighter competition — wave ").Append(p.Wave.ToString(CultureInfo.InvariantCulture)).Append("\n\n");
        sb.Append($"{Scores.Count.ToString(CultureInfo.InvariantCulture)} submissions scored one-versus-pool against " +
                  $"Dev ({p.Dev.Count.ToString(CultureInfo.InvariantCulture)}) and Holdout ({p.Holdout.Count.ToString(CultureInfo.InvariantCulture)}); " +
                  $"{p.Frames.ToString(CultureInfo.InvariantCulture)} frames per bout, terrains {string.Join(", ", terrains)}, " +
                  $"seeds {p.FirstSeed.ToString(CultureInfo.InvariantCulture)}..{lastSeed.ToString(CultureInfo.InvariantCulture)}. " +
                  $"Pool from: {(string.IsNullOrEmpty(p.Source) ? "(unspecified)" : p.Source)}" +
                  (string.IsNullOrEmpty(p.Created) ? "" : $", cut {p.Created}") + ".\n\n");
        sb.Append($"Score: win 1, loss 0, draw {F(FighterCosts.Current.DrawScore, "0.##")}, over bouts played (both spawn orders). " +
                  "Ranked on the holdout score, then the head-to-head score, then name. " +
                  "`gap` = dev − holdout (overfit to the visible field). `±se` is the standard error over seeds.\n\n");

        int nw = 4, aw = 6;
        foreach (var s in Scores) { nw = Math.Max(nw, s.Name.Length); aw = Math.Max(aw, (s.Author ?? "").Length); }
        sb.Append("## Ranking\n\n```\n");
        sb.Append($"{"rank",4}  {"name".PadRight(nw)}  {"author".PadRight(aw)}  {"holdout",7}  {"±se",5}  {"dev",6}  {"±se",5}  {"gap",6}  {"h2h",6}  {"W-D-L hold",11}  {"W-D-L dev",11}\n");
        foreach (var s in Scores)
        {
            sb.Append(s.Rank.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("  ")
              .Append(s.Name.PadRight(nw)).Append("  ")
              .Append((s.Author ?? "").PadRight(aw)).Append("  ")
              .Append(F(s.HoldoutScore, "0.000").PadLeft(7)).Append("  ")
              .Append(F(s.Holdout.ScoreSe, "0.000").PadLeft(5)).Append("  ")
              .Append(F(s.DevScore, "0.000").PadLeft(6)).Append("  ")
              .Append(F(s.Dev.ScoreSe, "0.000").PadLeft(5)).Append("  ")
              .Append(F(s.OverfitGap, "+0.000;-0.000;0.000").PadLeft(6)).Append("  ")
              .Append((s.HeadToHead == null ? "-" : F(s.HeadToHead.Score, "0.000")).PadLeft(6)).Append("  ")
              .Append(Wdl(s.Holdout).PadLeft(11)).Append("  ")
              .Append(Wdl(s.Dev).PadLeft(11)).Append('\n');
        }
        sb.Append("```\n\n");

        if (Disqualified.Count > 0)
        {
            sb.Append("## Disqualified\n\n");
            foreach (var d in Disqualified) sb.Append("- ").Append(d.Replace("\n", "\n  ")).Append('\n');
            sb.Append('\n');
        }

        if (HeadToHead != null)
        {
            sb.Append("## Head-to-head ladder\n\nSubmissions only; the tiebreak after the holdout score.\n\n```\n")
              .Append(HeadToHead.LadderText(timing: false)).Append("```\n\n");
            sb.Append("## Head-to-head matrices\n\n");
            foreach (var t in HeadToHead.Terrains) sb.Append("```\n").Append(HeadToHead.MatrixText(t)).Append("```\n\n");
        }

        sb.Append("## Pool\n\n");
        sb.Append("Dev (visible): ").Append(p.Dev.Count > 0 ? string.Join(", ", p.Dev) : "(none)").Append("\n\n");
        sb.Append("Holdout (hidden): ").Append(p.Holdout.Count > 0 ? string.Join(", ", p.Holdout) : "(none)").Append('\n');
        return sb.ToString();
    }

    private static string Wdl(LeagueStanding s) =>
        $"{s.Wins.ToString(CultureInfo.InvariantCulture)}-{s.Draws.ToString(CultureInfo.InvariantCulture)}-{s.Losses.ToString(CultureInfo.InvariantCulture)}";
}

public static class FighterCompetition
{
    // Frames / seeds / terrains from the pool; OutDir null (nothing saved).
    public static LeagueSettings Settings(CompetitionPool pool)
    {
        var s = new LeagueSettings { Frames = pool.Frames, Seeds = pool.Seeds, FirstSeed = pool.FirstSeed, OutDir = null };
        s.Terrains.AddRange(pool.Terrains);
        return s;
    }

    // One submission, one-versus-pool: a league with Include = side ∪ {sub}, Only = {sub},
    // against Holdout and against Dev. Throws if the submission is in the pool, is not a
    // participant, or does not compile.
    public static CompetitionScore Score(CompetitionPool pool, string submission, Action<LeagueBout, int, int> onBout = null)
    {
        var p = Participant(submission);
        foreach (var n in pool.All)
            if (string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{p.Name}' is in the wave-{pool.Wave} pool — a submission must be new.");
        return new CompetitionScore
        {
            Name    = p.Name,
            Author  = p.Author,
            Holdout = VsSide(pool, pool.Holdout, p.Name, onBout),
            Dev     = VsSide(pool, pool.Dev,     p.Name, onBout),
        };
    }

    // submissions null ⇒ pool.Submissions(). Scores each (refusing any whose spec does not
    // compile), runs the head-to-head league when ≥ 2 remain, sorts and ranks.
    public static CompetitionReport Run(CompetitionPool pool, IReadOnlyList<string> submissions = null,
                                        Action<string> log = null, Action<LeagueBout, int, int> onBout = null)
    {
        var sw = Stopwatch.StartNew();
        var subs = submissions != null ? new List<string>(submissions) : pool.Submissions();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in subs)
            if (!seen.Add(n)) throw new ArgumentException($"Submission '{n}' is listed twice.");

        var model  = new PhysicsCostModel();
        var scores = new List<CompetitionScore>();
        var dq     = new List<string>();
        foreach (var n in subs)
        {
            var part = Participant(n);
            var r = FighterCompiler.Compile(part.Make(), model);
            if (!r.IsValid)
            {
                dq.Add($"{part.Name}: does not compile under the default budget\n{r.Report()}");
                log?.Invoke($"{part.Name}: DISQUALIFIED (does not compile)");
                continue;
            }
            log?.Invoke($"{part.Name}: scoring vs holdout ({pool.Holdout.Count}) and dev ({pool.Dev.Count})");
            var s = Score(pool, part.Name, onBout);
            log?.Invoke(string.Format(CultureInfo.InvariantCulture, "{0}: holdout {1:0.000}  dev {2:0.000}  gap {3:+0.000;-0.000;0.000}",
                                      s.Name, s.HoldoutScore, s.DevScore, s.OverfitGap));
            scores.Add(s);
        }

        LeagueResult h2h = null;
        if (scores.Count >= 2)
        {
            log?.Invoke($"head-to-head: {scores.Count} submissions");
            var st = Settings(pool);
            st.OnBout = onBout;
            foreach (var s in scores) st.Include.Add(s.Name);
            h2h = FighterLeague.Run(st);
            foreach (var s in scores)
                s.HeadToHead = StandingOf(h2h, s.Name);
        }

        scores.Sort((x, y) =>
        {
            int c = y.HoldoutScore.CompareTo(x.HoldoutScore);
            if (c != 0) return c;
            c = (y.HeadToHead?.Score ?? 0).CompareTo(x.HeadToHead?.Score ?? 0);
            return c != 0 ? c : string.CompareOrdinal(x.Name, y.Name);
        });
        for (int i = 0; i < scores.Count; i++) scores[i].Rank = i + 1;

        sw.Stop();
        return new CompetitionReport { Pool = pool, Scores = scores, HeadToHead = h2h, Disqualified = dq, Elapsed = sw.Elapsed };
    }

    // Next wave's pool: prev.All ∪ prev.Submissions(), re-split from `ranked` (a fresh full
    // ladder in rank order — every one of those names must appear in it; extra names are
    // ignored; ties keep `ranked` order, as in Split). Wave is
    // prev.Wave + 1; frames/seeds/terrains carry over.
    public static CompetitionPool Roll(CompetitionPool prev, IReadOnlyList<(string Name, double Score)> ranked, string source = null)
    {
        var field = new List<string>(prev.All);
        field.AddRange(prev.Submissions());
        var inField = new HashSet<string>(field, StringComparer.OrdinalIgnoreCase);
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows    = new List<(string Name, double Score)>(field.Count);   // in `ranked` order (Split's tiebreak)
        foreach (var (n, s) in ranked)
        {
            if (!seen.Add(n)) throw new ArgumentException($"'{n}' appears twice in the ranked list.");
            if (inField.Contains(n)) rows.Add((n, s));
        }
        foreach (var n in field)
            if (!seen.Contains(n))
                throw new ArgumentException($"'{n}' (wave-{prev.Wave} pool or submission) is missing from the ranked ladder.");
        var next = CompetitionPool.Split(prev.Wave + 1, rows, source: source);
        next.Terrains  = new List<string>(prev.Terrains);
        next.Frames    = prev.Frames;
        next.Seeds     = prev.Seeds;
        next.FirstSeed = prev.FirstSeed;
        return next;
    }

    private static LeagueStanding VsSide(CompetitionPool pool, List<string> side, string sub, Action<LeagueBout, int, int> onBout)
    {
        var st = Settings(pool);
        st.OnBout = onBout;
        st.Include.AddRange(side);
        st.Include.Add(sub);
        st.Only.Add(sub);
        var r = FighterLeague.Run(st);
        return StandingOf(r, sub)
               ?? throw new InvalidOperationException($"'{sub}' was not scored: {string.Join("; ", r.Disqualified)}");
    }

    private static LeagueStanding StandingOf(LeagueResult r, string name)
    {
        foreach (var s in r.Standings)
            if (string.Equals(s.Participant.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    private static LeagueParticipant Participant(string name)
    {
        var all = FighterLeague.AllParticipants();
        return all.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"Submission '{name}' is not a league participant " +
                                              $"({string.Join(", ", all.ConvertAll(x => x.Name))}).");
    }
}
