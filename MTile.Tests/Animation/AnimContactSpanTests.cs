using System;
using System.Collections.Generic;
using System.Text.Json;
using MTile;
using Xunit;

namespace MTile.Tests;

// ContactSpan: the interval model that replaced per-keyframe contact labels.
//
// These exist because the solver's own FD-vs-analytic oracle (AnimSolverTests) only runs when
// a clip HAS contacts, and the shipped clips are currently blank stubs — so nothing else in
// the suite exercises the new dw/dφ. The sign of that derivative is what RefreshContacts reads
// to tell a release from an engage (the foot-swap deadlock), so it is pinned here directly.
public class AnimContactSpanTests
{
    private static ContactSpan Span(float start, float end, AnimCurve w = null) => new()
    { Point = "support_l", Start = start, End = end, Weight = w };

    [Fact]
    public void Covers_IsHalfOpen_AndRetriesAcrossTheLoopSeam()
    {
        var s = Span(0.2f, 0.6f);
        Assert.True(s.Covers(0.2f, out float u0)); Assert.Equal(0f, u0, 5);
        Assert.True(s.Covers(0.4f, out float um)); Assert.Equal(0.5f, um, 5);
        Assert.False(s.Covers(0.6f, out _));      // end is exclusive
        Assert.False(s.Covers(0.1f, out _));

        // A stance crossing the seam is ONE span with End past 1 — it must cover both sides.
        var wrap = Span(0.8f, 1.2f);
        Assert.True(wrap.Covers(0.9f, out float a)); Assert.Equal(0.25f, a, 5);
        Assert.True(wrap.Covers(0.1f, out float b)); Assert.Equal(0.75f, b, 5);
        Assert.False(wrap.Covers(0.5f, out _));
    }

    [Fact]
    public void NullWeight_IsTheDefaultRamp_NotAFlatConstant()
    {
        // Load-bearing: a constant weight has zero slope everywhere, so a release would never
        // signal and RefreshContacts' time-fade floor would never engage.
        var s = Span(0f, 1f);
        Assert.Equal(0f, s.WeightAt(0f), 4);
        Assert.Equal(1f, s.WeightAt(0.5f), 4);
        Assert.True(s.SlopeAt(0.05f) > 0f, "the ramp-in must rise");
        Assert.True(s.SlopeAt(0.95f) < 0f, "the ramp-out must fall — that sign IS the release signal");
        Assert.Equal(0f, s.SlopeAt(0.5f), 4);   // the hold is flat
    }

    [Fact]
    public void WeightAndSlope_AreZeroOutsideTheSpan()
    {
        var s = Span(0.3f, 0.7f);
        Assert.Equal(0f, s.WeightAt(0.29f));
        Assert.Equal(0f, s.WeightAt(0.71f));
        Assert.Equal(0f, s.SlopeAt(0.29f));
        Assert.Equal(0f, s.SlopeAt(0.71f));
    }

    // THE property the solver differentiates through, checked two ways.
    //
    // Primary: the analytic slope INTEGRATES to the weight change over the span. That is
    // curvature-immune and needs no exclusions, unlike a pointwise difference.
    [Theory]
    [InlineData(0.0f, 1.0f)]
    [InlineData(0.2f, 0.45f)]     // a short span: slope scales by 1/width
    [InlineData(0.8f, 1.2f)]      // wrapping the seam
    public void SlopeAt_IntegratesToTheWeightChange(float start, float end)
    {
        var s = Span(start, end);
        const int N = 4000;
        float width = end - start, acc = 0f;
        for (int i = 0; i < N; i++)
        {
            float phase = start + width * ((i + 0.5f) / N);
            acc += s.SlopeAt(phase >= 1f ? phase - 1f : phase) * (width / N);
        }
        // The default ramp starts and ends at zero, so the net change is zero; the integral of
        // |slope| is what says the ramp actually moved (twice the peak, up then down).
        Assert.Equal(0f, acc, 2);

        float rise = 0f;
        for (int i = 0; i < N / 2; i++)
        {
            float phase = start + width * ((i + 0.5f) / N);
            rise += s.SlopeAt(phase >= 1f ? phase - 1f : phase) * (width / N);
        }
        Assert.Equal(s.WeightAt(start + width * 0.5f) - s.WeightAt(start), rise, 2);
    }

    // Secondary: a pointwise central difference, on a grid deliberately OFFSET off the curve's
    // keys. Not an exclusion of a non-differentiable point — the curve is C1 everywhere — but
    // the default ramp's shoulders have high curvature (dV/du falls 0.12 → 0 across the last
    // 0.3% of the ramp segment), and in float32 there is no step both small enough to resolve
    // that and large enough to survive cancellation. Away from the shoulders the two agree
    // tightly, which is the part the old piecewise-linear feather could never claim at all.
    [Theory]
    [InlineData(0.0f, 1.0f)]
    [InlineData(0.2f, 0.45f)]
    [InlineData(0.8f, 1.2f)]
    public void SlopeAt_MatchesAFiniteDifference_AwayFromTheShoulders(float start, float end)
    {
        var s = Span(start, end);
        const float h = 1e-4f;
        float width = end - start;
        int checkedPoints = 0;
        for (int i = 0; i < 97; i++)
        {
            float u = (i + 0.5f) / 97f;
            if (MathF.Abs(u - 0.15f) < 0.01f || MathF.Abs(u - 0.85f) < 0.01f) continue;   // the shoulders
            if (u < 0.02f || u > 0.98f) continue;                                          // the span's own edges
            float phase = start + u * width;
            float Wrap(float x) => x >= 1f ? x - 1f : x < 0f ? x + 1f : x;
            float fd = (s.WeightAt(Wrap(phase + h)) - s.WeightAt(Wrap(phase - h))) / (2f * h);
            float an = s.SlopeAt(Wrap(phase));
            Assert.True(MathF.Abs(fd - an) < 5e-2f * (1f + MathF.Abs(an)),
                        $"span [{start},{end}] u={u:0.000}: analytic {an} vs fd {fd}");
            checkedPoints++;
        }
        Assert.True(checkedPoints > 80, $"only {checkedPoints} points checked — the filter is too greedy");
    }

    [Fact]
    public void ShortSpan_RampsProportionally_NotAtAFixedWidth()
    {
        // The old model had ONE FeatherWidth for every contact in the game. A span's ramp is a
        // fraction of itself, so a quick plant eases quickly and a long one slowly.
        var quick = Span(0.0f, 0.1f);
        var slow  = Span(0.0f, 0.8f);
        Assert.Equal(quick.WeightAt(0.05f), slow.WeightAt(0.40f), 4);   // same fraction, same weight
        Assert.True(MathF.Abs(quick.SlopeAt(0.0075f)) > MathF.Abs(slow.SlopeAt(0.06f)) * 4f);
    }

    [Fact]
    public void OverlappingSpans_CrossFade_WithOppositeSlopeSigns()
    {
        // Two feet swapping: the outgoing one is releasing (slope < 0) exactly where the
        // incoming one is engaging (slope > 0). That overlap IS the crossover the fixed-width
        // feather used to synthesize.
        var outgoing = Span(0.0f, 0.55f);
        var incoming = Span(0.45f, 1.0f);
        bool sawCrossfade = false;
        for (int i = 0; i <= 100; i++)
        {
            float t = 0.45f + (0.55f - 0.45f) * (i / 100f);
            float wo = outgoing.WeightAt(t), wi = incoming.WeightAt(t);
            if (wo <= 0f || wi <= 0f) continue;
            sawCrossfade = true;
            Assert.True(outgoing.SlopeAt(t) <= 1e-4f, $"t={t}: the outgoing foot should not be rising");
            Assert.True(incoming.SlopeAt(t) >= -1e-4f, $"t={t}: the incoming foot should not be falling");
        }
        Assert.True(sawCrossfade, "the overlap should produce a window where both feet carry weight");
    }

    [Fact]
    public void AuthoredWeight_OverridesTheDefault_AndRoundTrips()
    {
        var s = Span(0.1f, 0.9f, AnimCurve.Constant(0.4f));
        Assert.Equal(0.4f, s.WeightAt(0.5f), 5);
        Assert.Equal(0f, s.SlopeAt(0.5f), 5);

        var opts = new JsonSerializerOptions { WriteIndented = true };
        var back = JsonSerializer.Deserialize<ContactSpan>(JsonSerializer.Serialize(s, opts), opts);
        Assert.Equal(0.4f, back.WeightAt(0.5f), 5);
        Assert.Equal(s.Start, back.Start, 5);
        Assert.Equal(s.End, back.End, 5);

        // An unauthored weight stays absent in the json and reads as the default ramp.
        string flat = JsonSerializer.Serialize(Span(0f, 1f), opts);
        Assert.DoesNotContain("Weight", flat);
        Assert.Equal(1f, JsonSerializer.Deserialize<ContactSpan>(flat, opts).WeightAt(0.5f), 4);

        // Clone is deep.
        var clone = s.Clone();
        clone.Weight.Keys[0].V = 0.9f;
        Assert.Equal(0.4f, s.Weight.Keys[0].V, 5);
    }

    // The editor edits a span's curve in place. DefaultWeight is a shared static, so handing it
    // out directly would let one span's first tweak retune every unauthored contact in the
    // project — a corruption with no visible cause. EnsureWeight forks instead.
    [Fact]
    public void EnsureWeight_ForksTheSharedDefault_InsteadOfHandingItOut()
    {
        float defaultMid = AnimCurve.ValueAt(ContactSpan.DefaultWeight, 0.5f);

        var a = Span(0f, 1f);
        var w = a.EnsureWeight();
        Assert.NotSame(ContactSpan.DefaultWeight, w);
        Assert.Same(w, a.Weight);                      // and it is now authored on the span
        w.Keys[0].V = 0.77f;

        Assert.Equal(defaultMid, AnimCurve.ValueAt(ContactSpan.DefaultWeight, 0.5f), 5);
        Assert.Equal(0f, ContactSpan.DefaultWeight.Keys[0].V, 5);
        Assert.Equal(0f, Span(0f, 1f).WeightAt(0f), 4);  // an untouched span is unaffected

        // Calling it twice keeps the same instance — it is a materializer, not a reset.
        Assert.Same(w, a.EnsureWeight());
    }

    [Fact]
    public void DocumentRoundTrip_KeepsSpansAndOmitsAnEmptyList()
    {
        var doc = new AnimationDocument
        {
            Name = "t", Type = "Misc", Skeleton = "biped", Duration = 1f, Loop = true,
            Keyframes = new List<AnimationKeyframe> { new() { Time = 0f, Bones = new List<PoseBoneEntry>() } },
            Contacts = new List<ContactSpan>
            {
                new() { Point = "support_l", Start = 0f,    End = 0.5f },
                new() { Point = "support_r", Start = 0.5f,  End = 1.0f, Source = ContactSource.PlannedSupport },
            },
        };
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mtile_span_" + Guid.NewGuid().ToString("N"));
        try
        {
            AnimationStore.Save(doc, dir);
            var back = AnimationStore.LoadAll(dir)[0];
            Assert.Equal(2, back.Contacts.Count);
            Assert.Equal("support_r", back.Contacts[1].Point);
            Assert.Equal(ContactSource.PlannedSupport, back.Contacts[1].Source);
            Assert.Equal(0.5f, back.Contacts[1].Start, 5);

            // A clip with no contacts writes no key at all.
            var bare = new AnimationDocument
            {
                Name = "bare", Type = "Misc", Skeleton = "biped", Duration = 1f,
                Keyframes = new List<AnimationKeyframe> { new() { Time = 0f, Bones = new List<PoseBoneEntry>() } },
            };
            AnimationStore.Save(bare, dir);
            Assert.DoesNotContain("Contacts", System.IO.File.ReadAllText(bare.FilePath));
            Assert.Null(AnimationStore.LoadAll(dir).Find(x => x.Name == "bare").Contacts);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }
}
