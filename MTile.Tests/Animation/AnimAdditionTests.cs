using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

public class AnimAdditionTests
{
    // Mirror AnimationStore.Opts: enum-as-string + omit-null is what additions rely on.
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Addition_RoundTrips_WithStringKindAndFloats()
    {
        var doc = new AnimationDocument { Name = "x", Type = "StabAction" };
        doc.Keyframes.Add(new AnimationKeyframe
        {
            Time = 0f,
            Additions = new List<AnimAddition>
            {
                new() { Name = "spear_tip", Kind = AnimAdditionKind.Point, Px = 12.5f, Py = -3f },
                new() { Name = "stab_ray", Kind = AnimAdditionKind.Vector, Px = 0f, Py = 0f, Dx = 20f, Dy = 1f, Parent = "arm_r_lower" },
            },
        });

        string json = JsonSerializer.Serialize(doc, Opts);
        Assert.Contains("\"Vector\"", json);     // enum as string, not 1
        Assert.Contains("\"spear_tip\"", json);

        var back = JsonSerializer.Deserialize<AnimationDocument>(json, Opts)!;
        var adds = back.Keyframes[0].Additions;
        Assert.Equal(2, adds.Count);
        Assert.Equal(AnimAdditionKind.Point, adds[0].Kind);
        Assert.Equal(12.5f, adds[0].Px, 3);
        Assert.Equal(AnimAdditionKind.Vector, adds[1].Kind);
        Assert.Equal(20f, adds[1].Dx, 3);
        Assert.Equal("arm_r_lower", adds[1].Parent);
    }

    [Fact]
    public void Additions_NullByDefault_OmittedOnSave()
    {
        var doc = new AnimationDocument { Name = "x", Type = "Walk" };
        doc.Keyframes.Add(new AnimationKeyframe { Time = 0f });
        Assert.DoesNotContain("Additions", JsonSerializer.Serialize(doc, Opts));
    }

    [Fact]
    public void Sampler_LerpsByName_BetweenKeyframes()
    {
        var doc = Two(
            new AnimAddition { Name = "p", Kind = AnimAdditionKind.Point, Px = 0f, Py = 0f },
            new AnimAddition { Name = "p", Kind = AnimAdditionKind.Point, Px = 10f, Py = -4f });

        var mid = AnimAdditionSampler.Sample(doc, 0.5f);
        Assert.Single(mid);
        Assert.Equal(5f, mid[0].Px, 3);
        Assert.Equal(-2f, mid[0].Py, 3);
    }

    [Fact]
    public void Sampler_Holds_WhenPresentOnlyInEarlierKeyframe()
    {
        var doc = new AnimationDocument { Name = "x", Type = "Misc" };
        doc.Keyframes.Add(new AnimationKeyframe
        {
            Time = 0f,
            Additions = new List<AnimAddition> { new() { Name = "p", Px = 7f, Py = 0f } },
        });
        doc.Keyframes.Add(new AnimationKeyframe { Time = 1f });   // "p" removed here

        var mid = AnimAdditionSampler.Sample(doc, 0.5f);
        Assert.Single(mid);
        Assert.Equal(7f, mid[0].Px, 3);   // held from the earlier frame
    }

    [Fact]
    public void CloneEffectiveAt_ReturnsLatestSetAtOrBefore_T()
    {
        var doc = new AnimationDocument { Name = "x", Type = "Misc" };
        doc.Keyframes.Add(new AnimationKeyframe
        {
            Time = 0f, Additions = new List<AnimAddition> { new() { Name = "a", Px = 1f } },
        });
        doc.Keyframes.Add(new AnimationKeyframe
        {
            Time = 0.6f, Additions = new List<AnimAddition> { new() { Name = "a", Px = 9f } },
        });

        var eff = AnimAdditionSampler.CloneEffectiveAt(doc, 0.7f);
        Assert.Single(eff);
        Assert.Equal(9f, eff[0].Px, 3);
        // Clone (not aliased): mutating the result must not touch the source.
        eff[0].Px = 0f;
        Assert.Equal(9f, doc.Keyframes[1].Additions[0].Px, 3);
    }

    [Fact]
    public void WithBone_AppendsBone_KeepsExistingAndGrowsPose()
    {
        var s = SkeletonExamples.Biped();
        int hand = s.IndexOf("arm_r_lower");
        var s2 = s.WithBone("weapon", hand, new BoneTransform(new Vector2(5f, 0f), 0f, Vector2.One), 3f);

        Assert.Equal(s.Count + 1, s2.Count);
        int w = s2.IndexOf("weapon");
        Assert.True(w >= 0);
        Assert.Equal(hand, s2.Bones[w].Parent);          // parent index stable (bone appended)
        for (int i = 0; i < s.Count; i++)                // existing bones unchanged
            Assert.Equal(s.Bones[i].Name, s2.Bones[i].Name);
        Assert.Equal(s2.Count, s2.CreatePose().Local.Length);
    }

    private static AnimationDocument Two(AnimAddition a0, AnimAddition a1)
    {
        var doc = new AnimationDocument { Name = "x", Type = "Misc" };
        doc.Keyframes.Add(new AnimationKeyframe { Time = 0f, Additions = new List<AnimAddition> { a0 } });
        doc.Keyframes.Add(new AnimationKeyframe { Time = 1f, Additions = new List<AnimAddition> { a1 } });
        return doc;
    }
}
