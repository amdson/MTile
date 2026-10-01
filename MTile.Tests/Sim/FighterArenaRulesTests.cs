using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MTile.Tests.Sim;

// The arena's per-kind rules the compiler enforces on every fighter, closed after wave 3
// (guide §2, §13): action priorities are fixed per kind (so a second copy of an action can
// never preempt the first one's recovery), and a Contact box is the body — radius plus
// ContactMargin — whatever HalfWidth the spec asked for, and is priced on the stock box.
public class FighterArenaRulesTests
{
    private static FighterSpec Walker()
    {
        var s = FighterRoster.Brick();
        s.Actions.Clear();
        return s;
    }

    private static ActionSpec Ranged(int active, int passive)
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.ActivePriority = active; a.PassivePriority = passive;
        return a;
    }

    [Fact]
    public void Priorities_AreFixedPerKind_AndReported()
    {
        // Twinbarrel's lever: two guns whose Passive (29) beats the other's Active (28).
        // (Ranged defaults are 28/22, so the spec's 28/29 inverts them.)
        var s = Walker();
        s.EnergyReserve = 6f;
        s.Actions.Add(Ranged(28, 29));
        s.Actions.Add(Ranged(28, 29));
        var r = FighterCompiler.Compile(s, new PhysicsCostModel());
        Assert.True(r.IsValid, string.Join("\n", r.Violations));
        var d = ActionSpec.Default(ActionKind.Ranged);
        Assert.Contains(r.Notes, n => n.Contains($"priorities are fixed at {d.ActivePriority}/{d.PassivePriority - FighterCompiler.PassiveOffset}") && n.Contains("spec said 28/29"));

        var built = r.Blueprint.Actions();
        Assert.Equal(2, built.Count);
        foreach (var a in built)
        {
            Assert.Equal(d.ActivePriority,  a.Spec.ActivePriority);
            Assert.Equal(d.PassivePriority - FighterCompiler.PassiveOffset, a.Spec.PassivePriority);
            // The invariant the loophole needed to break: no kind's Passive beats its own Active.
            Assert.True(a.Spec.PassivePriority < a.Spec.ActivePriority);
        }
    }

    [Fact]
    public void CompiledFighter_ActionsAreNeverPreempted_EvenAcrossKinds()
    {
        // Contact (Active 12) + PlaceBlock (Passive 18) could cut a touch's 0.85 s cooldown with
        // the plain defaults. Compiled, every Passive sits below every Active.
        var s = Walker();
        s.EnergyReserve = 6f;
        s.Actions.Add(ActionSpec.Default(ActionKind.Contact));
        var block = ActionSpec.Default(ActionKind.PlaceBlock);
        block.Material = TileType.Dirt;
        s.Actions.Add(block);
        s.Actions.Add(ActionSpec.Default(ActionKind.Ranged));
        var r = FighterCompiler.Compile(s, new PhysicsCostModel());
        Assert.True(r.IsValid, string.Join("\n", r.Violations));
        var built = r.Blueprint.Actions();
        int minActive = built.Min(a => a.Spec.ActivePriority);
        int maxPassive = built.Max(a => a.Spec.PassivePriority);
        Assert.True(maxPassive < minActive, $"max passive {maxPassive} must be below min active {minActive}");
        // Candidate order among idle choices is the stock one (Ranged 22 > PlaceBlock 18 > Contact 10).
        var byPassive = built.OrderByDescending(a => a.Spec.PassivePriority).Select(a => a.Spec.Kind).ToList();
        Assert.Equal(new[] { ActionKind.Ranged, ActionKind.PlaceBlock, ActionKind.Contact }, byPassive);
    }

    [Fact]
    public void EveryKind_PassiveBelowOwnActive()
    {
        // Same-kind preemption is impossible under the defaults, for every kind the compiler fixes.
        foreach (ActionKind k in Enum.GetValues(typeof(ActionKind)))
        {
            if (k == ActionKind.Special) continue;
            ActionSpec d;
            try { d = ActionSpec.Default(k); } catch (Exception) { continue; }
            Assert.True(d.PassivePriority < d.ActivePriority, $"{k}: {d.PassivePriority} !< {d.ActivePriority}");
        }
    }

    [Fact]
    public void ContactBox_IsTheBody_WhateverTheSpecSaid_AndCostIgnoresIt()
    {
        var model = new PhysicsCostModel();
        var small = Walker();
        var c1 = ActionSpec.Default(ActionKind.Contact);
        small.Actions.Add(c1);
        var r1 = FighterCompiler.Compile(small, model);
        Assert.True(r1.IsValid, string.Join("\n", r1.Violations));

        var huge = Walker();
        var c2 = ActionSpec.Default(ActionKind.Contact);
        c2.HalfWidth = 280f; c2.HalfHeight = 280f;              // Gravemantle's box
        huge.Actions.Add(c2);
        var r2 = FighterCompiler.Compile(huge, model);
        Assert.True(r2.IsValid, string.Join("\n", r2.Violations));

        // Same price: the box is not for sale.
        Assert.Equal(r1.ActionCosts[0].Cost.Mass, r2.ActionCosts[0].Cost.Mass, 6);
        Assert.Equal(r1.Total.Mass, r2.Total.Mass, 6);
        Assert.Contains(r2.Notes, n => n.Contains("Contact box is the body") && n.Contains("280"));
        Assert.DoesNotContain(r1.Notes, n => n.Contains("Contact box"));

        // Same box: radius + margin, for both.
        float radius = FighterCompiler.DerivedRadius(r1.Total.Mass, small);
        float expect = radius + FighterCosts.Current.ContactMargin;
        var a1 = r1.Blueprint.Actions().Single();
        var a2 = r2.Blueprint.Actions().Single();
        Assert.Equal(expect, a1.Spec.HalfWidth, 3);
        Assert.Equal(expect, a1.Spec.HalfHeight, 3);
        Assert.Equal(expect, a2.Spec.HalfWidth, 3);
        Assert.Equal(expect, a2.Spec.HalfHeight, 3);
        Assert.True(expect < 40f, $"a body-sized box, not a field: {expect}");
    }

    [Fact]
    public void ContactBox_FollowsTheBody_NotTheGeometryScale()
    {
        // A low-density (big) body gets a bigger touch box because it IS bigger; a dense one
        // gets a smaller box. Either way it is radius + margin, not HalfWidth × scale.
        var model = new PhysicsCostModel();
        var big = Walker(); big.Density = 0.5f; big.Actions.Add(ActionSpec.Default(ActionKind.Contact));
        var dense = Walker(); dense.Density = 2f; dense.Actions.Add(ActionSpec.Default(ActionKind.Contact));
        var rb = FighterCompiler.Compile(big, model);
        var rd = FighterCompiler.Compile(dense, model);
        Assert.True(rb.IsValid && rd.IsValid);
        float eb = FighterCompiler.DerivedRadius(rb.Total.Mass, big)   + FighterCosts.Current.ContactMargin;
        float ed = FighterCompiler.DerivedRadius(rd.Total.Mass, dense) + FighterCosts.Current.ContactMargin;
        Assert.Equal(eb, rb.Blueprint.Actions().Single().Spec.HalfWidth, 3);
        Assert.Equal(ed, rd.Blueprint.Actions().Single().Spec.HalfWidth, 3);
        Assert.True(eb > ed);
    }
}
