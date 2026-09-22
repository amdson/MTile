using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MTile.Tests;

public class AnimJointLimitTests
{
    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(3)]
    public void BoundsFollowEquivalentAngularBranches(int turns)
    {
        var knee = new Bone("knee", -1, 0, 10, 0, 3.05f);
        float offset = turns * MathF.Tau;
        Assert.True(knee.RotationBounds(-.2f + offset, out float min, out float max));
        Assert.InRange(MathF.Abs(min - offset), 0, .00001f);
        Assert.InRange(MathF.Abs(max - offset - 3.05f), 0, .00001f);
    }

    [Fact]
    public void SkeletonRoundTripPreservesOptionalBounds()
    {
        var builder = new SkeletonBuilder("limits");
        int root = builder.AddRoot("hip", 0);
        builder.Add("knee", root, .5f, 10, 0, 3.05f);
        var doc = SkeletonStore.Capture("limits", builder.Build());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc)));
        var loaded = SkeletonStore.LoadFromStream(stream);
        Assert.NotNull(loaded);
        Assert.Null(loaded.Bones[0].MinRotation);
        Assert.Equal(0f, loaded.Bones[1].MinRotation);
        Assert.Equal(3.05f, loaded.Bones[1].MaxRotation);
    }
}
