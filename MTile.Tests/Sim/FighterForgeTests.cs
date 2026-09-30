using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Phase 6 gate for Plans/FIGHTER_DESIGN_PLAN.md (§8.3): the AI designer's search core
// (FighterForge) is reproducible, never produces an infeasible spec, scores
// deterministically inside [0, 1], and hill-climbs monotonically. Shrunk settings (one
// or two opponents, one terrain, short matches) keep the class to a few seconds.
public class FighterForgeTests(ITestOutputHelper output)
{
    private static readonly ForgeSettings Small = new()
    {
        Opponents  = new Func<FighterSpec>[] { FighterRoster.Brick, FighterRoster.Gunner },
        Terrains   = FighterArena.Terrains.Take(1).ToArray(),
        BothOrders = false,
        Frames     = 300,
    };

    [Fact]
    public void RandomFeasibleSpec_CompilesAndIsReproducible()
    {
        var model = new PhysicsCostModel();
        for (int seed = 1; seed <= 5; seed++)
        {
            var a = FighterForge.RandomFeasibleSpec(seed);
            var b = FighterForge.RandomFeasibleSpec(seed);
            var r = FighterCompiler.Compile(a.ToSpec(), model);
            Assert.True(r.IsValid, r.Report());
            Assert.Equal(FighterSpecPrinter.ToCSharp(a), FighterSpecPrinter.ToCSharp(b));
        }
        Assert.NotEqual(FighterSpecPrinter.ToCSharp(FighterForge.RandomFeasibleSpec(1)),
                        FighterSpecPrinter.ToCSharp(FighterForge.RandomFeasibleSpec(2)));
        output.WriteLine(FighterSpecPrinter.ToCSharp(FighterForge.RandomFeasibleSpec(1)));
    }

    [Fact]
    public void Mutation_NeverTouchesTheParent_AndFeasibleChildrenCompile()
    {
        var model  = new PhysicsCostModel();
        var rng    = new Random(7);
        var parent = FighterForge.RandomFeasibleSpec(3);
        string before = FighterSpecPrinter.ToCSharp(parent);
        int feasible = 0, infeasible = 0;
        for (int i = 0; i < 400; i++)
        {
            var child = FighterForge.Mutate(parent, rng);
            var r = FighterCompiler.Compile(child.ToSpec(), model);
            // Either rejected with a reason, or a blueprint — never a half state.
            Assert.Equal(r.IsValid, r.Blueprint != null);
            if (r.IsValid) feasible++; else { infeasible++; Assert.NotEmpty(r.Violations); }
            Assert.True(child.Spec.Actions.Count <= FighterForge.MaxActions);
        }
        Assert.Equal(before, FighterSpecPrinter.ToCSharp(parent));
        Assert.True(feasible > 0, "no mutation of a feasible spec was feasible");

        for (int i = 0; i < 50; i++)
        {
            var c = FighterForge.MutateFeasible(parent, rng, model);
            if (c != null) Assert.True(FighterCompiler.Compile(c.ToSpec(), model).IsValid);
        }
        output.WriteLine($"400 raw mutations: {feasible} feasible, {infeasible} rejected");
    }

    [Fact]
    public void Fitness_OfAnArchetype_IsInUnitRange_AndDeterministic()
    {
        var gunner = new FighterGenome(FighterRoster.Gunner(), ForgeBrain.Kiter);
        var a = FighterForge.Evaluate(gunner, Small);
        var b = FighterForge.Evaluate(gunner, Small);
        Assert.InRange(a.Fitness, 0f, 1f);
        Assert.Equal(Small.Opponents.Count * Small.Terrains.Count, a.Matches);
        Assert.True(a.SameAs(b), $"{a} then {b}");
        Assert.Equal(0, a.CompareTo(b));
        output.WriteLine($"Gunner vs Brick+Gunner on flat: {a}");
    }

    [Fact]
    public void ShortSearch_NeverEndsBelowItsStart()
    {
        var settings = new ForgeSettings
        {
            Opponents  = Small.Opponents, Terrains = Small.Terrains, BothOrders = false, Frames = 300,
            Restarts   = 1, Steps = 3, VerifyDeterminism = true,
        };
        var result = FighterForge.Search(11, settings);
        var run    = Assert.Single(result.Restarts);
        Assert.True(run.EndScore.CompareTo(run.StartScore) >= 0, $"{run.StartScore} → {run.EndScore}");
        Assert.True(FighterCompiler.Compile(result.Best.ToSpec(), new PhysicsCostModel()).IsValid);
        // The kept score is the best spec's real score (re-evaluating gives the same number).
        Assert.True(FighterForge.Evaluate(result.Best, settings).SameAs(result.BestScore));
        output.WriteLine($"{run.StartScore} → {run.EndScore}, {result.Evaluations} evaluations");
    }
}
