using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// ClipTransitionGraph (Animation/ClipTransitionGraph.cs): the two-clip motion-graph edge
// search on a synthetic rig — a hip with a swinging leg and a "support" point on its tip.
// Pins that a clip transitions to itself at the same phase for free, that a phase-shifted
// copy is found at the shifted phase, that a contact-state disagreement costs extra, and
// that non-looping clips clamp their windows instead of wrapping.
public class AnimTransitionGraphTests
{
    private static Skeleton Rig()
    {
        var b = new SkeletonBuilder("tiny");
        int hip = b.AddRoot("hip", 0f, 4f);
        b.Add("leg", hip, MathHelper.PiOver2, 10f);
        b.AddPoint(new NamedPoint { Id = "support", Bone = "leg", Role = "support" });
        return b.Build();
    }

    private static AnimationKeyframe Key(float t, float legRot) => new()
    {
        Time = t,
        Bones = new List<PoseBoneEntry> { new() { Bone = "leg", Rotation = legRot } },
    };

    // A one-cycle leg swing: rotation follows a sine over the cycle, keyed at `shift` offset.
    private static AnimationDocument Swing(string name, float shift, bool loop = true, ContactSpan[] contacts = null)
    {
        var keys = new List<AnimationKeyframe>();
        const int K = 8;
        for (int k = 0; k <= K; k++)
        {
            float t = k / (float)K;
            float u = t + shift;
            keys.Add(Key(t, 0.6f * System.MathF.Sin(MathHelper.TwoPi * u)));
        }
        return new AnimationDocument
        {
            Name = name, Type = "Misc", Skeleton = "tiny", Loop = loop, Duration = 1f,
            Keyframes = keys,
            Contacts = contacts == null ? null : new List<ContactSpan>(contacts),
        };
    }

    [Fact]
    public void SameClip_IsFreeOnTheDiagonal_AndBestEntryIsTheSamePhase()
    {
        var clip = Swing("a", 0f);
        var g = ClipTransitionGraph.Build(clip, clip, Rig());
        for (int i = 0; i < g.Samples; i++) Assert.True(g.Cost[i, i] < 1e-6f, $"Cost[{i},{i}] = {g.Cost[i, i]}");
        var e = g.BestEntry(0.25f);
        Assert.Equal(0.25f, e.ToPhase, 3);
        Assert.True(e.Cost < 1e-6f);
        Assert.NotNull(g.Best);
    }

    [Fact]
    public void PhaseShiftedCopy_IsFoundAtTheShiftedPhase()
    {
        var a = Swing("a", 0f);
        var b = Swing("b", 0.5f);   // b(t) == a(t + 0.5)
        var g = ClipTransitionGraph.Build(a, b, Rig());
        // Leaving a at 0.75 should enter b at 0.25, since b(0.25) == a(0.75).
        var e = g.BestEntry(0.75f);
        Assert.Equal(0.25f, e.ToPhase, 2);
        Assert.True(e.Cost < 1e-4f, $"cost {e.Cost}");
        // The cheapest transition overall lies on that shifted diagonal.
        var best = g.Best.Value;
        float wrapped = best.ToPhase - best.FromPhase; wrapped -= System.MathF.Floor(wrapped);
        Assert.Equal(0.5f, wrapped, 2);
    }

    [Fact]
    public void ContactStateDisagreement_CostsExtra_OnlyWhenBothClipsAnnotate()
    {
        var planted   = new[] { new ContactSpan { Point = "support", Start = 0f, End = 0.5f } };
        var unplanted = new[] { new ContactSpan { Point = "support", Start = 0.5f, End = 1f } };
        var a = Swing("a", 0f, contacts: planted);
        var same = Swing("s", 0f, contacts: planted);
        var flipped = Swing("f", 0f, contacts: unplanted);
        var silent = Swing("n", 0f);
        var o = new TransitionOptions { ContactMismatch = 3f, Window = 0 };

        float agree    = ClipTransitionGraph.Build(a, same, Rig(), o).CostAt(0.25f, 0.25f);
        float disagree = ClipTransitionGraph.Build(a, flipped, Rig(), o).CostAt(0.25f, 0.25f);
        float noSpans  = ClipTransitionGraph.Build(a, silent, Rig(), o).CostAt(0.25f, 0.25f);

        Assert.True(agree < 1e-6f, $"agree {agree}");
        Assert.Equal(3f, disagree, 4);          // one support point, one mismatch
        Assert.True(noSpans < 1e-6f, $"an unannotated clip must not be penalised ({noSpans})");
    }

    [Fact]
    public void NonLoopingClips_ClampTheirWindows()
    {
        var a = Swing("a", 0f, loop: false);
        var b = Swing("b", 0f, loop: false);
        var g = ClipTransitionGraph.Build(a, b, Rig(), new TransitionOptions { Window = 3 });
        Assert.True(g.Cost[0, 0] < 1e-6f);
        Assert.True(g.Cost[g.Samples - 1, g.Samples - 1] < 1e-6f);
        Assert.Contains(g.Transitions, t => t.Cost < 1e-6f);
    }
}
