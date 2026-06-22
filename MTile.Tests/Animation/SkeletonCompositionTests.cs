using System.Collections.Generic;
using MTile;
using Xunit;

namespace MTile.Tests;

// Clip-local ExtraBones layered onto the base rig (see SkeletonComposition): a slash's
// knife lives in its clip file, not the shared biped rig.
public class SkeletonCompositionTests
{
    private static SkeletonBoneRecord Knife() => new()
    {
        Name = "knife", Parent = "arm_r_lower", Tx = 0.03f, Ty = -4.5f, Length = 0f,
    };

    [Fact]
    public void Compose_AppendsExtraBone_ParentedToBase()
    {
        var skel = SkeletonExamples.Biped();
        Assert.Equal(-1, skel.IndexOf("knife"));     // base rig has no knife post-migration

        var rig = SkeletonComposition.Compose(skel, new List<SkeletonBoneRecord> { Knife() });
        int k = rig.IndexOf("knife");
        Assert.True(k >= 0, "composed rig should contain the knife");
        Assert.Equal(rig.IndexOf("arm_r_lower"), rig.Bones[k].Parent);
        Assert.True(k > rig.IndexOf("arm_r_lower"), "child must be ordered after its parent");
    }

    [Fact]
    public void Compose_DedupsByName_AndSkipsUnknownParent()
    {
        var skel = SkeletonExamples.Biped();
        var rig = SkeletonComposition.Compose(skel, new List<SkeletonBoneRecord>
        {
            Knife(), Knife(),                                                  // dup name
            new() { Name = "orphan", Parent = "does_not_exist", Length = 0f }, // bad parent
        });
        Assert.Equal(skel.Count + 1, rig.Count);          // only one knife, no orphan
        Assert.Equal(-1, rig.IndexOf("orphan"));
    }

    [Fact]
    public void WithClipBones_LayersUnionAcrossClips_FilteredByRig()
    {
        var skel = SkeletonExamples.Biped();
        var clips = new[]
        {
            new AnimationDocument { Name = "walk", Type = "Walk", Skeleton = "biped" }, // no extras
            new AnimationDocument { Name = "slash", Type = "GroundSlash1", Skeleton = "biped",
                                    ExtraBones = new List<SkeletonBoneRecord> { Knife() } },
            new AnimationDocument { Name = "other", Type = "GroundSlash2", Skeleton = "lizard",
                                    ExtraBones = new List<SkeletonBoneRecord>
                                    { new() { Name = "tail", Parent = "hip" } } },  // different rig
        };
        var rig = SkeletonComposition.WithClipBones(skel, clips);
        Assert.True(rig.IndexOf("knife") >= 0, "knife from the matching-rig slash clip is layered in");
        Assert.Equal(-1, rig.IndexOf("tail"));            // lizard clip ignored for the biped rig
    }

    [Fact]
    public void Animator_ResolvesClipLocalBone()
    {
        var skel = SkeletonExamples.Biped();
        var slash = new AnimationDocument
        {
            Name = "slash", Type = "GroundSlash1", Skeleton = "biped",
            Region = AnimRegion.UpperBody,
            ExtraBones = new List<SkeletonBoneRecord> { Knife() },
        };
        slash.Keyframes.Add(new AnimationKeyframe { Time = 0f, Bones = PoseData.Capture(skel.CreatePose()) });
        slash.Keyframes.Add(new AnimationKeyframe { Time = 1f, Bones = PoseData.Capture(skel.CreatePose()) });

        var anim = new CharacterAnimator(skel, 0.6f, new[] { slash });
        Assert.True(anim.Skeleton.IndexOf("knife") >= 0,
            "the animator's composed rig should resolve the slash's clip-local knife bone");
    }
}
