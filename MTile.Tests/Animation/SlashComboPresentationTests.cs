using System.Reflection;
using Microsoft.Xna.Framework;
using MTile;

namespace MTile.Tests;

public class SlashComboPresentationTests
{
    private static List<AnimationDocument> Clips()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "SkeletonStates"))) d = d.Parent;
        return AnimationStore.LoadAll(Path.Combine(d!.FullName, "SkeletonStates", "biped_rabbit"));
    }
    private static float Value(ActionState action, string property)
        => (float)action.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(action)!;

    [Theory]
    [InlineData(1, .20f, 5, 3)]
    [InlineData(2, .18f, 4, 3)]
    [InlineData(3, .27f, 8, 4)]
    public void ExistingHitWindowsHaveFullBlade_AndTrail(int index, float duration, int startup, int active)
    {
        ActionState action = index == 1 ? new GroundSlash1() : index == 2 ? new GroundSlash2() : new GroundSlash3();
        Assert.Equal(duration, Value(action, "Duration"), 5);
        Assert.Equal(startup / 60f, Value(action, "HurtboxStartSeconds"), 5);
        Assert.Equal(active / 60f, Value(action, "HurtboxActiveSeconds"), 5);
        var clip = Clips().Single(c => c.Type == action.GetType().Name);
        var a = Assert.Single(clip.Attachments);
        Assert.False(a.EmitsTrail(a.Start));
        for (int f = startup; f < startup + active; f++)
        {
            float time = f / 60f / duration * (1 - clip.SettleShare);
            Assert.True(a.TryProgress(time, out float p));
            Assert.InRange(a.FrameAt(time, p, 8), 3, 4); // two full blade cells, no forming/dissolving art
            Assert.True(a.EmitsTrail(time));
        }
        Assert.False(a.TryProgress(1 - clip.SettleShare, out _));
    }

    [Fact]
    public void ChainedAttacksShareTheirHandoffPose()
    {
        var clips = Clips();
        var rig = SkeletonComposition.WithClipBones(SkeletonExamples.Load("biped_rabbit"), clips);
        var scratch = Enumerable.Range(0, 4).Select(_ => rig.CreatePose()).ToArray();
        var from = rig.CreatePose(); var to = rig.CreatePose();
        foreach (int n in new[] { 1, 2 })
        {
            var a = clips.Single(c => c.Type == $"GroundSlash{n}");
            var b = clips.Single(c => c.Type == $"GroundSlash{n + 1}");
            AnimationSampler.SampleSmooth(a, 1 - a.SettleShare, scratch[0], scratch[1], scratch[2], scratch[3], from);
            AnimationSampler.SampleSmooth(b, 0, scratch[0], scratch[1], scratch[2], scratch[3], to);
            foreach (string bone in new[] { "chest", "head", "arm_r_upper", "arm_r_lower", "knife", "arm_l_upper", "arm_l_lower" })
                Assert.InRange(MathF.Abs(MathHelper.WrapAngle(from.Local[rig.IndexOf(bone)].Rotation - to.Local[rig.IndexOf(bone)].Rotation)), 0, .002f);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void RuntimeAnimatorMakesAlternatingCuts_AndEndsWithoutAttachment(int facing)
    {
        const float dt = 1f / 60;
        var anim = new CharacterAnimator(SkeletonExamples.Load("biped_rabbit"), .6f, Clips());
        void Tick(string action, float progress = -1, int recovery = 0) => anim.Update(new CharacterAnimSample(
            Vector2.Zero, Vector2.Zero, facing, true, "StandingState", action, dt,
            actionProgress: progress, recoveryFramesLeft: recovery));
        for (int f = 0; f < 45; f++) Tick("ReadyAction");
        foreach (ActionState action in new ActionState[] { new GroundSlash1(), new GroundSlash2(), new GroundSlash3() })
        {
            float duration = Value(action, "Duration"), start = Value(action, "HurtboxStartSeconds");
            float end = start + Value(action, "HurtboxActiveSeconds");
            float? first = null; float last = 0;
            for (int f = 0; f * dt < duration; f++)
            {
                Tick(action.GetType().Name, f * dt / duration);
                if (f * dt < start - 1e-6f || f * dt > end + 1e-6f) continue;
                var world = anim.Pose.ComputeWorld(Affine2.Identity);
                float angle = world[anim.Skeleton.IndexOf("knife")].Angle;
                first ??= angle; last = angle;
            }
            Assert.NotNull(first);
            float sign = action is GroundSlash2 ? -1 : 1;
            Assert.True(MathHelper.WrapAngle(last - first!.Value) * sign > .5f, action.GetType().Name + " did not cut decisively through its active window");
        }
        for (int f = 18; f > 0; f--) Tick("RecoveryAction", recovery: f);
        for (int f = 0; f < 20; f++) Tick("ReadyAction");
        var effects = new List<AttachmentSample>(); anim.SampleAttachments(effects);
        Assert.Empty(effects);
    }

    [Fact]
    public void AuthoredFrameScheduleClonesIndependently_AndInvalidScheduleFallsBack()
    {
        var a = new AnimAttachment { FrameTimes = new[] { 0f, .2f, .8f } };
        var copy = a.Clone(); copy.FrameTimes[1] = .6f;
        Assert.Equal(1, a.FrameAt(.4f, .4f, 3));
        Assert.Equal(0, copy.FrameAt(.4f, .4f, 3));
        a.FrameTimes[1] = float.NaN;
        Assert.Equal(1, a.FrameAt(.4f, .4f, 3));
    }
}
