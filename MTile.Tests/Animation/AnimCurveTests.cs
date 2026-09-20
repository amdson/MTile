using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MTile;
using Xunit;

namespace MTile.Tests;

// The scalar annotation curve (Animation/AnimCurve.cs): a null curve is a constant, the
// domain is closed and stretches with its span, and the analytic slope agrees with a finite
// difference everywhere INSIDE the span — the property that lets a solver differentiate
// through an annotation without the exclusion the old piecewise-linear feather needed.
public class AnimCurveTests
{
    [Fact]
    public void NullOrSingleKey_ReadsAsAConstant()
    {
        Assert.Equal(1f, AnimCurve.ValueAt(null, 0.5f));
        Assert.Equal(0.25f, AnimCurve.ValueAt(null, 0.5f, fallback: 0.25f));
        Assert.Equal(0f, AnimCurve.SlopeAt(null, 0.5f));

        var one = AnimCurve.Constant(0.6f);
        for (float u = 0f; u <= 1f; u += 0.125f) Assert.Equal(0.6f, AnimCurve.ValueAt(one, u), 5);
        Assert.Equal(0f, AnimCurve.SlopeAt(one, 0.5f));
    }

    [Fact]
    public void Domain_IsClosed_AndClampsOutside()
    {
        var c = AnimCurve.Ramp();
        // u = 1 evaluates the LAST key; it does not wrap to the first. The span owns
        // wrapping, never the curve.
        Assert.Equal(c.Keys[^1].V, AnimCurve.ValueAt(c, 1f), 5);
        Assert.Equal(c.Keys[0].V,  AnimCurve.ValueAt(c, 0f), 5);
        Assert.Equal(c.Keys[0].V,  AnimCurve.ValueAt(c, -3f), 5);
        Assert.Equal(c.Keys[^1].V, AnimCurve.ValueAt(c, 4f), 5);
        Assert.Equal(0f, AnimCurve.SlopeAt(c, -0.1f));
        Assert.Equal(0f, AnimCurve.SlopeAt(c, 1.1f));
    }

    [Fact]
    public void Ramp_RisesToPeak_HoldsThenFalls_AndStaysInRange()
    {
        var c = AnimCurve.Ramp(peak: 1f, rampIn: 0.2f, rampOut: 0.2f);
        Assert.Equal(0f, AnimCurve.ValueAt(c, 0f), 5);
        Assert.Equal(1f, AnimCurve.ValueAt(c, 0.2f), 5);
        Assert.Equal(1f, AnimCurve.ValueAt(c, 0.5f), 5);   // the hold
        Assert.Equal(1f, AnimCurve.ValueAt(c, 0.8f), 5);
        Assert.Equal(0f, AnimCurve.ValueAt(c, 1f), 5);

        // Flat tangents at the plateau keys mean the hold never bulges past the peak, and
        // the ramps never undershoot below zero on the way in or out.
        for (int i = 0; i <= 64; i++)
        {
            float v = AnimCurve.ValueAt(c, i / 64f);
            Assert.InRange(v, -1e-4f, 1f + 1e-4f);
        }
    }

    [Fact]
    public void Span_StretchesTheShape_SoRetimingPreservesIt()
    {
        var c = AnimCurve.Ramp(rampIn: 0.25f, rampOut: 0.25f);
        // The same fractional position through two spans of very different length reads the
        // same value: that IS the stretch behavior, chosen over absolute-time ramps.
        foreach (float frac in new[] { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 1f })
        {
            float shortSpan = AnimCurve.ValueOverSpan(c, 0.10f + frac * 0.05f, 0.10f, 0.15f);
            float longSpan  = AnimCurve.ValueOverSpan(c, 0.10f + frac * 0.80f, 0.10f, 0.90f);
            Assert.Equal(shortSpan, longSpan, 4);
        }
        // ...and the slope scales by 1/width, so the SHORT span is the steeper one. Sample
        // at u = 0.125, mid ramp-in — the plateau's slope is zero on BOTH spans, which says
        // nothing about scaling.
        float sSlope = MathF.Abs(AnimCurve.SlopeOverSpan(c, 0.10f + 0.125f * 0.05f, 0.10f, 0.15f));
        float lSlope = MathF.Abs(AnimCurve.SlopeOverSpan(c, 0.10f + 0.125f * 0.80f, 0.10f, 0.90f));
        Assert.True(sSlope > 0f, "the ramp should have a nonzero slope at u = 0.125");
        Assert.Equal(0.80f / 0.05f, sSlope / lSlope, 2);   // exactly the width ratio
    }

    // The property the solver depends on: d/dphase is analytic and matches a central
    // difference across the whole span interior, INCLUDING the key boundaries the old
    // linear feather had corners at.
    [Fact]
    public void SlopeOverSpan_MatchesAFiniteDifference_AcrossTheWholeInterior()
    {
        var c = AnimCurve.Ramp(rampIn: 0.3f, rampOut: 0.2f);
        c.Keys[1].Tan = 2.5f;                       // a broken tangent, to exercise the override
        const float start = 0.2f, end = 0.75f, h = 1e-4f;
        for (int i = 1; i < 200; i++)
        {
            float t = start + (end - start) * (i / 200f);
            if (t - h <= start || t + h >= end) continue;
            float fd = (AnimCurve.ValueOverSpan(c, t + h, start, end)
                      - AnimCurve.ValueOverSpan(c, t - h, start, end)) / (2f * h);
            float an = AnimCurve.SlopeOverSpan(c, t, start, end);
            Assert.True(MathF.Abs(fd - an) < 2e-2f * (1f + MathF.Abs(an)),
                        $"t={t:0.0000}: analytic {an} vs fd {fd}");
        }
        Assert.Equal(0f, AnimCurve.SlopeOverSpan(c, start - 0.01f, start, end));
        Assert.Equal(0f, AnimCurve.SlopeOverSpan(c, end + 0.01f, start, end));
    }

    [Fact]
    public void AuthoredTangent_OverridesTheDerivedOne()
    {
        static AnimCurve Line() => new()
        {
            Keys = new List<AnimCurveKey>
            {
                new() { T = 0f,   V = 0f },
                new() { T = 0.5f, V = 1f },
                new() { T = 1f,   V = 0f },
            },
        };
        var auto = Line();
        var broken = Line();
        broken.Keys[1].Tan = 0f;                    // flatten the apex
        // The auto tangent at the apex is the (0 → 0) secant, i.e. also flat — so break it
        // to something that is definitely NOT the secant and confirm the curve follows.
        broken.Keys[1].Tan = 3f;
        Assert.NotEqual(AnimCurve.ValueAt(auto, 0.6f), AnimCurve.ValueAt(broken, 0.6f), 3);
        Assert.Equal(3f, AnimCurve.SlopeAt(broken, 0.5f), 3);
    }

    [Fact]
    public void UnevenKeySpacing_DoesNotWhipTheCurve()
    {
        // A tight pair next to a wide one: tangents are secants in the PARAMETER, so the
        // curve must stay inside the authored value range rather than overshooting.
        var c = new AnimCurve
        {
            Keys = new List<AnimCurveKey>
            {
                new() { T = 0f,    V = 0f },
                new() { T = 0.02f, V = 1f },
                new() { T = 1f,    V = 1f },
            },
        };
        for (int i = 0; i <= 200; i++)
            Assert.InRange(AnimCurve.ValueAt(c, i / 200f), -0.35f, 1.35f);
    }

    [Fact]
    public void DegenerateSpan_IsSafe()
    {
        var c = AnimCurve.Ramp();
        Assert.Equal(0f, AnimCurve.SlopeOverSpan(c, 0.5f, 0.5f, 0.5f));
        // A zero-width span has no interior, so the value falls to the domain start.
        Assert.Equal(AnimCurve.ValueAt(c, 0f), AnimCurve.ValueOverSpan(c, 0.5f, 0.5f, 0.5f), 5);
    }

    // "Add a handle here so I can bend it later" must not bend it NOW. The trap is that an auto
    // tangent is a secant through a key's NEIGHBOURS, so a naive insert silently re-derives the
    // tangents either side and the line moves under the cursor.
    [Theory]
    [InlineData(0.07f)]
    [InlineData(0.5f)]
    [InlineData(0.93f)]
    public void InsertPreservingShape_DoesNotMoveTheLine(float u)
    {
        var c = AnimCurve.Ramp(rampIn: 0.25f, rampOut: 0.2f);
        var before = new float[201];
        for (int i = 0; i <= 200; i++) before[i] = AnimCurve.ValueAt(c, i / 200f);

        int n = c.Keys.Count;
        int at = c.InsertPreservingShape(u);
        Assert.Equal(n + 1, c.Keys.Count);
        Assert.Equal(u, c.Keys[at].T, 3);
        for (int i = 0; i < c.Keys.Count - 1; i++)
            Assert.True(c.Keys[i].T < c.Keys[i + 1].T, "keys must stay sorted");

        for (int i = 0; i <= 200; i++)
            Assert.Equal(before[i], AnimCurve.ValueAt(c, i / 200f), 3);
    }

    [Fact]
    public void RoundTrips_AndOmitsDerivedTangents()
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var c = AnimCurve.Ramp(rampIn: 0.2f, rampOut: 0.2f);
        c.Keys[1].Tan = null;                        // derived — must not be written
        c.Keys[2].Tan = 1.25f;                       // authored — must survive

        string json = JsonSerializer.Serialize(c, opts);
        var back = JsonSerializer.Deserialize<AnimCurve>(json, opts);
        Assert.Equal(c.Keys.Count, back.Keys.Count);
        Assert.Null(back.Keys[1].Tan);
        Assert.Equal(1.25f, back.Keys[2].Tan.Value, 5);
        for (int i = 0; i <= 32; i++)
            Assert.Equal(AnimCurve.ValueAt(c, i / 32f), AnimCurve.ValueAt(back, i / 32f), 5);

        // Clone is deep: editing the copy must not reach back into the original.
        var clone = c.Clone();
        clone.Keys[0].V = 0.5f;
        Assert.Equal(0f, c.Keys[0].V);
    }
}
