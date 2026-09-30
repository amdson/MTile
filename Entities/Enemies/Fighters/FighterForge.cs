using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The AI designer (Plans/FIGHTER_DESIGN_PLAN.md §8.3): random restarts + hill climbing
// over FighterSpec, with the arena as the objective. The search core lives here, library
// side, so tests can drive it; `MTile.Bench --forge` (MTile.Bench/Forge.cs) is only
// argument parsing and printing.
//
// TOOL CODE, NOT SIM CODE. Nothing in the game loop calls into this file. It uses
// System.Random — seeded, never wall-clock — which is fine precisely because the forge
// runs offline: its randomness decides WHICH specs get built, never how a match plays.
// Every match it runs is the deterministic FighterArena, so the same spec always scores
// the same, and the same seed always walks the same search.
//
// Registry hygiene: FighterArena.Run registers every spec it is handed under the scratch
// kinds FighterSlot0..7 (overwriting whatever was there), so an evaluation leaks nothing
// into the next one, and the roster's own kinds (Brick..FighterTurret) are never touched
// — the opponents are fresh FighterRoster specs the arena re-points at a slot.

// The bundled brains a genome can pick from (FighterBrains.cs). A brain is code, so the
// forge chooses one and searches within its BrainConfig floats (§8.3).
public enum ForgeBrain { Closer, Kiter, HoverDive }

// A point in the search space: a spec plus the brain choice (a FighterSpec carries its
// brain as a factory delegate, which cannot be read back, so the choice rides alongside).
// Genomes are immutable once built — every mutation clones.
public sealed class FighterGenome
{
    public FighterSpec Spec  { get; }
    public ForgeBrain  Brain { get; }

    public FighterGenome(FighterSpec spec, ForgeBrain brain)
    {
        Spec  = FighterForge.CloneSpec(spec, brain);
        Brain = brain;
    }

    // A fresh spec for one match — the arena overwrites Kind and Team on what it is given.
    public FighterSpec ToSpec() => FighterForge.CloneSpec(Spec, Brain);
}

// One opponent's line in a score: results across every terrain × order it was played on.
public sealed record ForgeMatchup(string Opponent, int Wins, int Losses, int Draws);

// Win rate across the roster × terrains × orders, draw = ½, tie-broken on the mean
// health-fraction margin (candidate's fraction left − opponent's). Ordered by
// (Fitness, Margin); exact float comparison, which is honest because the arena is
// deterministic.
public sealed class ForgeScore : IComparable<ForgeScore>
{
    public float Points    { get; init; }   // wins + ½ draws
    public int   Matches   { get; init; }
    public float MarginSum { get; init; }
    public IReadOnlyList<ForgeMatchup> PerOpponent { get; init; } = Array.Empty<ForgeMatchup>();

    public float Fitness => Matches == 0 ? 0f : Points / Matches;
    public float Margin  => Matches == 0 ? 0f : MarginSum / Matches;

    public int CompareTo(ForgeScore other)
    {
        if (other == null) return 1;
        int c = Fitness.CompareTo(other.Fitness);
        return c != 0 ? c : Margin.CompareTo(other.Margin);
    }

    public bool SameAs(ForgeScore o) => o != null && Points == o.Points && Matches == o.Matches && MarginSum == o.MarginSum;

    public override string ToString() => $"fitness {Fitness:0.000} (margin {Margin:+0.000;-0.000}, {Points:0.#}/{Matches})";
}

// What a search is scored against, and how hard it looks. Tests shrink the opponents,
// terrains and frames; the CLI uses the defaults.
public sealed class ForgeSettings
{
    public IReadOnlyList<Func<FighterSpec>> Opponents { get; init; } = FighterRoster.All;
    public IReadOnlyList<(string name, Func<ChunkMap> make)> Terrains { get; init; } = FighterArena.Terrains;
    // false ⇒ the candidate always spawns left (entry 0). true ⇒ also the swapped match,
    // which is a legitimately different match (ECS order decides target tie-breaks).
    public bool BothOrders { get; init; } = false;
    public int  Frames     { get; init; } = 720;
    public int  Restarts   { get; init; } = 4;
    public int  Steps      { get; init; } = 30;
    // Rejection-sampling caps: a random spec, and a feasible mutation of one.
    public int  MaxSampleAttempts   { get; init; } = 20000;
    public int  MaxMutationAttempts { get; init; } = 64;
    // Evaluate the first candidate twice and throw if the scores differ.
    public bool VerifyDeterminism { get; init; } = true;
    // Null ⇒ a PhysicsCostModel over FighterCosts.Current, built when the search starts
    // (so a FighterCosts.Load before the search is honoured).
    public ICostModel Model { get; init; }
}

public sealed record ForgeRestart(int Index, FighterGenome Start, ForgeScore StartScore,
                                  FighterGenome End, ForgeScore EndScore, int Accepted, int Evaluations);

public sealed record ForgeResult(FighterGenome Best, ForgeScore BestScore,
                                 IReadOnlyList<ForgeRestart> Restarts, int Evaluations);

public static class FighterForge
{
    public const string DefaultName = "Forged";

    // Buyable pool kinds (ActionKind.Special is not for sale).
    private static readonly ActionKind[] Buyable =
    {
        ActionKind.Melee, ActionKind.Contact, ActionKind.Lunge, ActionKind.Slam, ActionKind.Ranged,
        ActionKind.RailShot, ActionKind.PounceSlam, ActionKind.Lash, ActionKind.PlaceBlock,
        ActionKind.SpawnBlockInAir,
    };

    public const int MaxActions = 4;

    // ── Continuous genes: (range, accessor) ───────────────────────────────────
    private readonly record struct FloatGene(string Name, float Min, float Max,
                                             Func<FighterSpec, float> Get, Action<FighterSpec, float> Set);

    private static readonly FloatGene[] Floats =
    {
        new("Health",             1f,    10f,  s => s.Health,             (s, v) => s.Health = v),
        new("Strength",           0.25f, 2.5f, s => s.Strength,           (s, v) => s.Strength = v),
        new("Armor",              0f,    2f,   s => s.Armor,              (s, v) => s.Armor = v),
        new("EnergyReserve",      0f,    20f,  s => s.EnergyReserve,      (s, v) => s.EnergyReserve = v),
        new("EnergyRegen",        0f,    3f,   s => s.EnergyRegen,        (s, v) => s.EnergyRegen = v),
        new("GroundPower",        0f,    400f, s => s.GroundPower,        (s, v) => s.GroundPower = v),
        new("JumpImpulse",        0f,    600f, s => s.JumpImpulse,        (s, v) => s.JumpImpulse = v),
        new("Thrust",             0f,    1500f,s => s.Thrust,             (s, v) => s.Thrust = v),
        new("Radius",             7f,    15f,  s => s.Radius,             (s, v) => s.Radius = v),
        new("EngageRange",        0f,    300f, s => s.EngageRange,        (s, v) => s.EngageRange = v),
        new("StandoffRange",      0f,    200f, s => s.StandoffRange,      (s, v) => s.StandoffRange = v),
        new("HoverHeight",        20f,   140f, s => s.HoverHeight,        (s, v) => s.HoverHeight = v),
        new("AlertRange",         150f,  500f, s => s.AlertRange,         (s, v) => s.AlertRange = v),
        new("RetreatBelowHealth", 0f,    0.8f, s => s.RetreatBelowHealth, (s, v) => s.RetreatBelowHealth = v),
    };

    // ── Sampling ──────────────────────────────────────────────────────────────

    // A random spec that compiles, from a seed — the restart's starting point. Throws if
    // MaxSampleAttempts draws all fail (the ranges above are what keep that from happening).
    public static FighterGenome RandomFeasibleSpec(int seed, ForgeSettings settings = null)
    {
        settings ??= new ForgeSettings();
        return RandomFeasible(new Random(seed), settings.Model ?? new PhysicsCostModel(), settings.MaxSampleAttempts);
    }

    public static FighterGenome RandomFeasible(Random rng, ICostModel model, int maxAttempts = 20000)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var g = RandomGenome(rng);
            if (IsFeasible(g, model)) return g;
        }
        throw new InvalidOperationException($"No feasible fighter in {maxAttempts} random draws.");
    }

    public static bool IsFeasible(FighterGenome g, ICostModel model)
        => FighterCompiler.Compile(g.ToSpec(), model).IsValid;

    private static FighterGenome RandomGenome(Random rng)
    {
        var s = NewSpec();
        s.Health        = Q(Uniform(rng, 1f, 8f));
        s.Strength      = rng.NextDouble() < 0.5 ? 1f : Q(Uniform(rng, 0.5f, 1.8f));
        s.Armor         = rng.NextDouble() < 0.6 ? 0f : Q(Uniform(rng, 0f, 1.5f));
        s.Radius        = Q(Uniform(rng, 8f, 14f));
        s.EnergyReserve = rng.NextDouble() < 0.2 ? 0f : Q(Uniform(rng, 1f, 20f));
        s.EnergyRegen   = s.EnergyReserve <= 0f ? 0f : Q(Uniform(rng, 0f, 2.5f));

        s.Rooted = rng.NextDouble() < 0.15;
        if (!s.Rooted)
        {
            if (rng.NextDouble() < 0.25)
            {
                s.Thrust = Q(Uniform(rng, 700f, 1500f));
                if (s.EnergyReserve <= 0f) { s.EnergyReserve = Q(Uniform(rng, 2f, 10f)); s.EnergyRegen = Q(Uniform(rng, 0.5f, 2f)); }
            }
            s.GroundPower = rng.NextDouble() < (s.Thrust > 0f ? 0.3 : 0.9) ? Q(Uniform(rng, 80f, 400f)) : 0f;
            s.JumpImpulse = rng.NextDouble() < 0.4 ? Q(Uniform(rng, 200f, 600f)) : 0f;
            s.Cling       = s.Thrust <= 0f && rng.NextDouble() < 0.15;
        }
        s.TargetMemory = rng.NextDouble() < 0.3;

        int n = rng.NextDouble() switch { < 0.05 => 0, < 0.45 => 1, < 0.80 => 2, < 0.95 => 3, _ => 4 };
        for (int i = 0; i < n; i++) s.Actions.Add(RandomAction(rng));

        var brain = (ForgeBrain)rng.Next(3);
        if (s.Thrust > 0f && rng.NextDouble() < 0.6) brain = ForgeBrain.HoverDive;

        // BrainConfig. The §4 ordering rule needs EngageRange under every melee-band
        // trigger, so draw it under the smallest one when there is one (the compiler is
        // still the judge).
        float cap = 300f;
        foreach (var a in s.Actions)
            if (FighterCompiler.EffectiveReach(a) != null) cap = MathF.Min(cap, a.MaxRange * 0.95f);
        s.EngageRange        = Q(Uniform(rng, 0f, MathF.Max(cap, 0f)));
        s.StandoffRange      = brain == ForgeBrain.Kiter ? Q(Uniform(rng, 0f, MathF.Min(s.EngageRange, 200f))) : 0f;
        s.HoverHeight        = Q(Uniform(rng, 40f, 120f));
        s.AlertRange         = Q(Uniform(rng, 220f, 450f));
        s.RetreatBelowHealth = rng.NextDouble() < 0.5 ? 0f : Q(Uniform(rng, 0.2f, 0.7f));
        s.PreferredAction    = s.Actions.Count == 0 || rng.NextDouble() < 0.3 ? -1 : rng.Next(s.Actions.Count);
        return new FighterGenome(s, brain);
    }

    private static ActionSpec RandomAction(Random rng)
    {
        var a = ActionSpec.Default(Buyable[rng.Next(Buyable.Length)]);
        var d = ActionSpec.Default(a.Kind);
        if (rng.NextDouble() < 0.5) a.Damage   = Q(d.Damage   * Uniform(rng, 0.5f, 1.5f));
        if (rng.NextDouble() < 0.5) a.Reach    = Q(d.Reach    * Uniform(rng, 0.5f, 1.5f));
        if (rng.NextDouble() < 0.5) a.MaxRange = Q(d.MaxRange * Uniform(rng, 0.5f, 1.5f));
        if (rng.NextDouble() < 0.5) a.Windup   = Q(d.Windup   * Uniform(rng, 0.5f, 1.5f));
        return a;
    }

    // ── Mutation ──────────────────────────────────────────────────────────────

    // One gene changed: a float (Gaussian, σ = 15% of its range, or 1-in-5 a fresh uniform
    // draw so a zeroed locomotion buy can come back), a bool flipped, the brain changed,
    // the preferred action re-picked, or the action list edited (add / remove / replace a
    // kind / reorder / nudge one knob within ±50% of its default). May be infeasible;
    // the parent is never modified.
    public static FighterGenome Mutate(FighterGenome parent, Random rng)
    {
        var s     = parent.ToSpec();
        var brain = parent.Brain;
        int op    = rng.Next(Floats.Length + 8);

        if (op < Floats.Length)
        {
            var g = Floats[op];
            float v = rng.NextDouble() < 0.2
                ? Uniform(rng, g.Min, g.Max)
                : g.Get(s) + Gaussian(rng) * 0.15f * (g.Max - g.Min);
            g.Set(s, Q(Math.Clamp(v, g.Min, g.Max)));
        }
        else switch (op - Floats.Length)
        {
            case 0: s.Cling = !s.Cling; break;
            case 1: s.TargetMemory = !s.TargetMemory; break;
            case 2:
                // Rooting a walker is only ever feasible with its locomotion sold, so the
                // flip does both (and un-rooting leaves it immobile — a later float step
                // buys legs back).
                s.Rooted = !s.Rooted;
                if (s.Rooted) { s.GroundPower = 0f; s.JumpImpulse = 0f; s.Thrust = 0f; s.Cling = false; }
                break;
            case 3:
                brain = (ForgeBrain)(((int)brain + 1 + rng.Next(2)) % 3);
                break;
            case 4:
                s.PreferredAction = rng.Next(s.Actions.Count + 1) - 1;
                break;
            case 5:   // add, or remove when full
                if (s.Actions.Count < MaxActions && (s.Actions.Count == 0 || rng.NextDouble() < 0.5))
                    s.Actions.Insert(rng.Next(s.Actions.Count + 1), RandomAction(rng));
                else if (s.Actions.Count > 0)
                    RemoveAction(s, rng.Next(s.Actions.Count));
                break;
            case 6:   // replace one action's kind, or swap two
                if (s.Actions.Count == 0) s.Actions.Add(RandomAction(rng));
                else if (s.Actions.Count >= 2 && rng.NextDouble() < 0.4)
                {
                    int i = rng.Next(s.Actions.Count), j = rng.Next(s.Actions.Count - 1);
                    if (j >= i) j++;
                    (s.Actions[i], s.Actions[j]) = (s.Actions[j], s.Actions[i]);
                    if      (s.PreferredAction == i) s.PreferredAction = j;
                    else if (s.PreferredAction == j) s.PreferredAction = i;
                }
                else s.Actions[rng.Next(s.Actions.Count)] = RandomAction(rng);
                break;
            default:  // nudge one knob of one action
                if (s.Actions.Count == 0) { s.Actions.Add(RandomAction(rng)); break; }
                int k = rng.Next(s.Actions.Count);
                s.Actions[k] = NudgeAction(s.Actions[k], rng);
                break;
        }
        return new FighterGenome(s, brain);
    }

    // Mutate until the child compiles (or give up: null).
    public static FighterGenome MutateFeasible(FighterGenome parent, Random rng, ICostModel model, int maxAttempts = 64)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var c = Mutate(parent, rng);
            if (IsFeasible(c, model)) return c;
        }
        return null;
    }

    private static void RemoveAction(FighterSpec s, int i)
    {
        s.Actions.RemoveAt(i);
        if      (s.PreferredAction == i) s.PreferredAction = -1;
        else if (s.PreferredAction > i)  s.PreferredAction--;
    }

    private static ActionSpec NudgeAction(ActionSpec a, Random rng)
    {
        var d = ActionSpec.Default(a.Kind);
        static float Nudge(Random r, float v, float def)
            => def == 0f ? v : Q(Math.Clamp(v + Gaussian(r) * 0.15f * def, 0.5f * def, 1.5f * def));
        switch (rng.Next(4))
        {
            case 0:  a.Damage   = Nudge(rng, a.Damage,   d.Damage);   break;
            case 1:  a.Reach    = Nudge(rng, a.Reach,    d.Reach);    break;
            case 2:  a.MaxRange = Nudge(rng, a.MaxRange, d.MaxRange); break;
            default: a.Windup   = Nudge(rng, a.Windup,   d.Windup);   break;
        }
        return a;
    }

    // ── Fitness ───────────────────────────────────────────────────────────────

    // Every opponent × terrain × order, candidate on team 1 when it spawns left and team 2
    // when it spawns right (the round-robin test's convention). Deterministic.
    public static ForgeScore Evaluate(FighterGenome g, ForgeSettings settings)
    {
        var model = settings.Model ?? new PhysicsCostModel();
        float points = 0f, margin = 0f;
        int matches = 0;
        var lines = new List<ForgeMatchup>(settings.Opponents.Count);

        foreach (var makeOpponent in settings.Opponents)
        {
            int w = 0, l = 0, dr = 0;
            string oppName = makeOpponent().Name;
            foreach (var (terrain, make) in settings.Terrains)
            {
                for (int order = 0; order < (settings.BothOrders ? 2 : 1); order++)
                {
                    var me  = g.ToSpec();
                    var opp = makeOpponent();
                    float meMax = me.Health, oppMax = opp.Health;
                    bool meLeft = order == 0;
                    var left  = meLeft ? me : opp;
                    var right = meLeft ? opp : me;
                    var entries = new[]
                    {
                        new ArenaEntry(left,  FighterArena.LeftSpawn,              1),
                        new ArenaEntry(right, FighterArena.RightSpawnFor(terrain), 2),
                    };
                    var r = FighterArena.Run(make(), entries, FighterArena.PlayerPark, settings.Frames, model);
                    int meIdx = meLeft ? 0 : 1, meTeam = meLeft ? 1 : 2;
                    if      (r.Draw)                 { points += 0.5f; dr++; }
                    else if (r.WinnerTeam == meTeam) { points += 1f;   w++;  }
                    else                             {                 l++;  }
                    margin += r.HealthLeft[meIdx] / MathF.Max(meMax, 1e-3f)
                            - r.HealthLeft[1 - meIdx] / MathF.Max(oppMax, 1e-3f);
                    matches++;
                }
            }
            lines.Add(new ForgeMatchup(oppName, w, l, dr));
        }
        return new ForgeScore { Points = points, Matches = matches, MarginSum = margin, PerOpponent = lines };
    }

    // ── Search ────────────────────────────────────────────────────────────────

    // Random restarts + hill climbing (§8.3 first cut). Each restart draws a random
    // feasible spec, then for `Steps` steps evaluates one feasible mutation and keeps it
    // if it scores ≥ the parent (so sideways moves drift across plateaus, and the kept
    // score never goes down). Infeasible specs are never scored. `onRestart` fires after
    // each restart with its summary (the CLI prints it).
    public static ForgeResult Search(int seed, ForgeSettings settings, Action<ForgeRestart> onRestart = null)
    {
        var rng   = new Random(seed);
        var model = settings.Model ?? new PhysicsCostModel();
        var s     = settings.Model == null ? WithModel(settings, model) : settings;

        FighterGenome best = null;
        ForgeScore bestScore = null;
        var restarts = new List<ForgeRestart>();
        int evals = 0;

        for (int r = 0; r < s.Restarts; r++)
        {
            var g     = RandomFeasible(rng, model, s.MaxSampleAttempts);
            var score = Evaluate(g, s); evals++;
            if (r == 0 && s.VerifyDeterminism)
            {
                var again = Evaluate(g, s); evals++;
                if (!score.SameAs(again))
                    throw new InvalidOperationException(
                        $"Arena is not deterministic: the same spec scored {score} then {again}.");
            }
            var start = g; var startScore = score;
            int accepted = 0, restartEvals = 1;

            for (int step = 0; step < s.Steps; step++)
            {
                var child = MutateFeasible(g, rng, model, s.MaxMutationAttempts);
                if (child == null) continue;
                var cs = Evaluate(child, s); evals++; restartEvals++;
                if (cs.CompareTo(score) >= 0) { g = child; score = cs; accepted++; }
            }

            var summary = new ForgeRestart(r, start, startScore, g, score, accepted, restartEvals);
            restarts.Add(summary);
            if (bestScore == null || score.CompareTo(bestScore) > 0) { best = g; bestScore = score; }
            onRestart?.Invoke(summary);
        }
        return new ForgeResult(best, bestScore, restarts, evals);
    }

    private static ForgeSettings WithModel(ForgeSettings s, ICostModel m) => new()
    {
        Opponents = s.Opponents, Terrains = s.Terrains, BothOrders = s.BothOrders, Frames = s.Frames,
        Restarts = s.Restarts, Steps = s.Steps, MaxSampleAttempts = s.MaxSampleAttempts,
        MaxMutationAttempts = s.MaxMutationAttempts, VerifyDeterminism = s.VerifyDeterminism, Model = m,
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FighterSpec NewSpec() => new()
    {
        Name  = DefaultName,
        Kind  = EntityKind.FighterSlot0,   // a placeholder; the arena re-points it per match
        Brain = BrainFactory(ForgeBrain.Closer),
    };

    public static Func<FighterSpec, EnemyController> BrainFactory(ForgeBrain b) => b switch
    {
        ForgeBrain.Kiter     => s => new FighterKiterBrain(s),
        ForgeBrain.HoverDive => s => new FighterHoverDiveBrain(s),
        _                    => s => new FighterCloserBrain(s),
    };

    // Deep copy (Actions is a list of value structs) with the brain factory rebuilt from
    // the choice. Every field FighterSpec has.
    public static FighterSpec CloneSpec(FighterSpec s, ForgeBrain brain) => new()
    {
        Name = s.Name, Kind = s.Kind,
        Health = s.Health, Strength = s.Strength, Armor = s.Armor,
        EnergyReserve = s.EnergyReserve, EnergyRegen = s.EnergyRegen,
        GroundPower = s.GroundPower, JumpImpulse = s.JumpImpulse, Thrust = s.Thrust,
        Cling = s.Cling, TargetMemory = s.TargetMemory, Rooted = s.Rooted,
        Radius = s.Radius, Sides = s.Sides, Team = s.Team,
        Color = s.Color, Sprite = s.Sprite,
        Actions = new List<ActionSpec>(s.Actions),
        Brain = BrainFactory(brain),
        EngageRange = s.EngageRange, StandoffRange = s.StandoffRange, HoverHeight = s.HoverHeight,
        AlertRange = s.AlertRange, RetreatBelowHealth = s.RetreatBelowHealth,
        PreferredAction = s.PreferredAction,
    };

    private static float Uniform(Random rng, float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);

    // Box–Muller, one draw.
    private static float Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    // Round to 3 significant figures so the printed initializer reads like a human wrote
    // it. The printer writes round-trip floats, so what is pasted is exactly what scored.
    internal static float Q(float v)
    {
        if (v == 0f || !float.IsFinite(v)) return v;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(v))) - 2);
        return (float)(Math.Round(v / mag) * mag);
    }
}
