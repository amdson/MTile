using System.Text.Json;
using System.Text.Json.Serialization;
using MTile;
using Xunit;

namespace MTile.Tests;

public class BoneMaskTests
{
    // Mirror AnimationStore.Opts (private there): enum-as-string + omit defaults is
    // exactly the serialization behavior the Region field depends on.
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] UpperBones =
        { "chest", "head", "arm_l_upper", "arm_l_lower", "arm_r_upper", "arm_r_lower" };
    private static readonly string[] LowerBones =
        { "hip", "leg_l_upper", "leg_l_lower", "foot_l", "leg_r_upper", "leg_r_lower", "foot_r" };

    [Fact]
    public void Biped_UpperBody_IsChestSubtree()
    {
        var s = SkeletonExamples.Biped();
        var mask = BoneMask.Resolve(s, AnimRegion.UpperBody);
        foreach (var name in UpperBones) Assert.True(mask[s.IndexOf(name)], $"{name} should be UpperBody");
        foreach (var name in LowerBones) Assert.False(mask[s.IndexOf(name)], $"{name} should not be UpperBody");
    }

    [Fact]
    public void Biped_LowerBody_IsComplementOfUpper()
    {
        var s = SkeletonExamples.Biped();
        var upper = BoneMask.Resolve(s, AnimRegion.UpperBody);
        var lower = BoneMask.Resolve(s, AnimRegion.LowerBody);
        var full  = BoneMask.Resolve(s, AnimRegion.FullBody);
        for (int i = 0; i < s.Count; i++)
        {
            Assert.True(full[i]);
            Assert.True(upper[i] != lower[i],
                $"bone {s.Bones[i].Name}: upper/lower must partition the rig");
        }
    }

    [Fact]
    public void RigWithoutChest_UpperBodyIsEmpty()
    {
        var b = new SkeletonBuilder("blob");
        int root = b.AddRoot("body", default);
        b.Add("tail", root, default);
        var s = b.Build();

        var upper = BoneMask.Resolve(s, AnimRegion.UpperBody);
        for (int i = 0; i < s.Count; i++) Assert.False(upper[i]);
    }

    [Fact]
    public void Region_MissingInJson_DefaultsToFullBody()
    {
        var doc = JsonSerializer.Deserialize<AnimationDocument>(
            "{\"Name\":\"legacy\",\"Type\":\"Walk\"}", Opts)!;
        Assert.Equal(AnimRegion.FullBody, doc.Region);
    }

    [Fact]
    public void Region_FullBody_OmittedOnSave()
    {
        var doc = new AnimationDocument { Name = "x", Type = "Walk" };
        string json = JsonSerializer.Serialize(doc, Opts);
        Assert.DoesNotContain("Region", json);
    }

    [Fact]
    public void Region_UpperBody_RoundTripsAsString()
    {
        var doc = new AnimationDocument { Name = "x", Type = "GroundSlash1", Region = AnimRegion.UpperBody };
        string json = JsonSerializer.Serialize(doc, Opts);
        Assert.Contains("\"UpperBody\"", json);
        var back = JsonSerializer.Deserialize<AnimationDocument>(json, Opts)!;
        Assert.Equal(AnimRegion.UpperBody, back.Region);
    }
}
