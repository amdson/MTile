using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Xna.Framework;
using MTile;

namespace MTile.Tests;

public class AttachmentTests
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MTile.Core.csproj"))) d = d.Parent;
        return d!.FullName;
    }

    [Theory]
    [InlineData(.19f, false, 0)]
    [InlineData(.2f, true, 0)]
    [InlineData(.5f, true, .5f)]
    [InlineData(.8f, false, 0)]
    [InlineData(1f, false, 0)]
    [InlineData(float.NaN, false, 0)]
    public void WindowUsesClipTime_AndDisappearsAtEnd(float time, bool active, float progress)
    {
        var a = new AnimAttachment { Start = .2f, End = .8f };
        Assert.Equal(active, a.TryProgress(time, out float t));
        Assert.Equal(progress, t, 5);
    }

    [Fact]
    public void ActualRabbitSlashes_UseSameClockAsSkeleton_AndVanishOnInterruption()
    {
        var clips = AnimationStore.LoadAll(Path.Combine(Root(), "SkeletonStates", "biped_rabbit"));
        var slashes = clips.Where(c => c.Type.Contains("Slash")).ToArray();
        Assert.Equal(9, slashes.Length);
        foreach (var clip in slashes)
        {
            var animator = new CharacterAnimator(SkeletonExamples.Load("biped_rabbit"), .6f, clips);
            var samples = new List<AttachmentSample>();
            animator.Update(new CharacterAnimSample(Vector2.Zero, Vector2.Zero, 1, true,
                "StandingState", clip.Type, 1f / 60, actionProgress: .5f));
            animator.SampleAttachments(samples);
            var s = Assert.Single(samples);
            Assert.Same(clip, s.Clip);
            Assert.Equal((1 - clip.SettleShare) * .5f, s.Time, 5);
            Assert.True(s.Attachment.TryProgress(s.Time, out float t));
            Assert.InRange(t, 0f, 1f); // authored windows need not span the whole swing
            Assert.Equal("knife", s.Attachment.Bone);
            Assert.InRange(s.Weight, .001f, 1);

            animator.Update(new CharacterAnimSample(Vector2.Zero, Vector2.Zero, 1, true,
                "StandingState", "RecoveryAction", 1f / 60, recoveryFramesLeft: 10));
            animator.SampleAttachments(samples);
            s = Assert.Single(samples);
            Assert.False(s.Attachment.TryProgress(s.Time, out _));

            animator.Update(new CharacterAnimSample(Vector2.Zero, Vector2.Zero, 1, true,
                "StandingState", "ReadyAction", 1f / 60));
            animator.SampleAttachments(samples);
            Assert.Empty(samples); // pose fade-out must not keep the knife alive
        }
    }

    [Fact]
    public void AttachmentUsesFullBoneTransform_IncludingMirrorAndStretch()
    {
        var b = new SkeletonBuilder("test");
        int arm = b.AddRoot("arm", MathF.PI / 4, 5);
        b.Add("knife", arm, -MathF.PI / 2);
        var pose = b.Build().CreatePose();
        var a = new AnimAttachment { Scale = 2, Rotation = .2f };
        Affine2 At(int facing)
        {
            var root = Affine2.FromTRS(new Vector2(20, 30), 0, new Vector2(facing * .6f, .6f));
            var world = pose.ComputeWorld(root);
            Assert.Equal(world[arm].Translation, world[1].Translation); // zero-length socket sits at hand
            return AttachmentSampling.Transform(world[1], a);
        }
        var right = At(1); var left = At(-1);
        foreach (var p in new[] { Vector2.Zero, new Vector2(7, 2), new Vector2(7, -2) })
        {
            var r = right.TransformPoint(p); var l = left.TransformPoint(p);
            Assert.Equal(40 - r.X, l.X, 4);
            Assert.Equal(r.Y, l.Y, 4);
        }
    }

    [Fact]
    public void TrailBreaksAcrossCombosRewindsFlipsInterruptionsAndTeleports()
    {
        var trail = new AttachmentTrail();
        var clip = new AnimationDocument();
        void Push(AnimationDocument c, float time, int facing = 1, float x = 0)
        {
            trail.Age(.01f, .065f);
            trail.Push(c, time, facing, new Vector2(x, 0), new Vector2(x + 8, 0), 1);
            trail.EndFrame();
        }
        Push(clip, .2f); Push(clip, .3f);
        Assert.Equal(2, trail.Points.Count);
        Push(clip, .1f); Assert.Single(trail.Points);
        Push(new AnimationDocument(), .2f); Assert.Single(trail.Points);
        Push(clip, .3f, -1); Assert.Single(trail.Points);
        Push(clip, .4f, -1, 100); Assert.Single(trail.Points);
        trail.Age(.01f, .065f); trail.EndFrame(); // interrupted, but residual trail may fade
        Push(clip, .5f, -1, 101); Assert.Single(trail.Points);
        trail.Age(.1f, .065f); Assert.Empty(trail.Points);
    }

    [Fact]
    public void OverlayBindingWinsWithoutDuplicate_AndRespectsBoneMask()
    {
        var b = new SkeletonBuilder("test"); b.AddRoot("hand", 0);
        var rig = b.Build();
        var first = new AnimationDocument { Attachments = new() { new() { Bone = "hand", Effect = "knife" } } };
        var second = new AnimationDocument { Attachments = new() { new() { Bone = "hand", Effect = "knife", Start = .8f } } };
        var samples = new List<AttachmentSample>();
        AttachmentSampling.Append(first, .5f, 1, samples, rig);
        AttachmentSampling.Append(second, .3f, 1, samples, rig, new[] { 0f });
        Assert.Same(first, Assert.Single(samples).Clip);
        AttachmentSampling.Append(second, .3f, 1, samples, rig, new[] { .5f });
        Assert.Same(second, Assert.Single(samples).Clip);
        Assert.False(samples[0].Attachment.TryProgress(samples[0].Time, out _));
    }

    [Fact]
    public void SavedAttachmentRoundTripsWithoutLosingExtraBones()
    {
        var clip = AnimationStore.LoadAll(Path.Combine(Root(), "SkeletonStates", "biped_rabbit"))
            .Single(c => c.Name == "groundslash1");
        string dir = Path.Combine(Path.GetTempPath(), "mtile-attachment-" + Guid.NewGuid());
        try
        {
            clip.FilePath = null;
            AnimationStore.Save(clip, dir);
            var loaded = Assert.Single(AnimationStore.LoadAll(dir));
            Assert.Equal("knife", Assert.Single(loaded.Attachments).Effect);
            Assert.Equal("arm_r_lower", Assert.Single(loaded.ExtraBones).Parent);
            Assert.Equal(clip.Attachments[0].End, loaded.Attachments[0].End);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void KnifeStripHasRealAlpha_EmptyCellBorders_AndFadingTail()
    {
        string dir = Path.Combine(Root(), "Assets", "AnimationEffects");
        var spec = JsonSerializer.Deserialize<SpriteAttachmentAsset>(File.ReadAllText(Path.Combine(dir, "knife.json")))!;
        byte[] png = File.ReadAllBytes(Path.Combine(dir, spec.Image));
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        Assert.Equal(6, png[25]); // RGBA, not opaque RGB with a painted background
        Assert.True(spec.IsValid(width, height));
        using var idat = new MemoryStream();
        for (int offset = 8; offset < png.Length;)
        {
            int n = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            if (System.Text.Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT") idat.Write(png, offset + 8, n);
            offset += n + 12;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var pixels = new MemoryStream(); z.CopyTo(pixels);
        byte[] raw = pixels.ToArray();
        var maxAlpha = new int[spec.FrameCount];
        for (int y = 0; y < height; y++)
        {
            int row = y * (width * 4 + 1);
            Assert.Equal(0, raw[row]); // prepared asset deliberately uses PNG filter None
            for (int x = 0; x < width; x++)
            {
                byte a = raw[row + 1 + x * 4 + 3];
                if (y == 0 || y == height - 1 || x % spec.FrameWidth == 0 || x % spec.FrameWidth == spec.FrameWidth - 1)
                    Assert.Equal(0, a);
                maxAlpha[x / spec.FrameWidth] = Math.Max(maxAlpha[x / spec.FrameWidth], a);
            }
        }
        Assert.All(maxAlpha, a => Assert.True(a > 0));
        Assert.True(maxAlpha[7] < maxAlpha[6] && maxAlpha[6] < maxAlpha[5] && maxAlpha[5] < maxAlpha[4]);
    }
}
