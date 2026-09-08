using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public sealed class AnimAttachment
{
    public string Bone { get; set; }
    public string Effect { get; set; }
    public float Start { get; set; }
    public float End { get; set; } = 1f;
    public float Scale { get; set; } = 1f;
    public float Rotation { get; set; }
    public float[] FrameTimes { get; set; } // optional clip times at which strip frames begin
    public float? TrailStart { get; set; }
    public float? TrailEnd { get; set; }
    public float TrailWidth { get; set; } = 1f; // outer fraction of the blade swept by the ribbon
    public float TrailOpacity { get; set; } = 1f;
    public AnimAttachment Clone()
    {
        var copy = (AnimAttachment)MemberwiseClone();
        copy.FrameTimes = FrameTimes == null ? null : (float[])FrameTimes.Clone();
        return copy;
    }

    public int FrameAt(float time, float progress, int count)
    {
        if (FrameTimes?.Length == count)
        {
            bool valid = true;
            for (int i = 0; i < count; i++)
                valid &= float.IsFinite(FrameTimes[i]) && FrameTimes[i] >= Start && FrameTimes[i] < End
                    && (i == 0 || FrameTimes[i] >= FrameTimes[i - 1]);
            if (valid)
            {
                int frame = 0;
                while (frame + 1 < count && time >= FrameTimes[frame + 1]) frame++;
                return frame;
            }
        }
        return Math.Clamp((int)(progress * count), 0, count - 1);
    }

    public bool EmitsTrail(float time) => time >= (TrailStart ?? Start) && time < (TrailEnd ?? End);

    public bool TryProgress(float time, out float progress)
    {
        progress = 0;
        if (!float.IsFinite(time) || !float.IsFinite(Start) || !float.IsFinite(End)
            // JSON decimal endpoints and 1-SettleShare can differ by one float ULP.
            || Start < 0 || End > 1 || End <= Start || time < Start || time >= End - 1e-6f
            || !float.IsFinite(Scale) || Scale <= 0 || !float.IsFinite(Rotation)) return false;
        progress = (time - Start) / (End - Start);
        return true;
    }
}

public readonly record struct AttachmentSample(AnimationDocument Clip, AnimAttachment Attachment,
    float Time, float Weight);

public static class AttachmentSampling
{
    // Later layers replace the same bone/effect binding, including outside its active
    // window; otherwise a masked-out base effect could shine through an overlay.
    public static void Append(AnimationDocument clip, float time, float weight,
                              List<AttachmentSample> output, Skeleton rig, float[] boneWeights = null)
    {
        if (clip?.Attachments == null) return;
        foreach (var a in clip.Attachments)
        {
            if (a == null || string.IsNullOrWhiteSpace(a.Bone) || string.IsNullOrWhiteSpace(a.Effect)) continue;
            int b = rig.IndexOf(a.Bone);
            if (b < 0) continue;
            float w = boneWeights == null ? weight : boneWeights[b];
            if (w <= 0) continue;
            output.RemoveAll(s => s.Attachment.Bone == a.Bone && s.Attachment.Effect == a.Effect);
            output.Add(new AttachmentSample(clip, a, time, w));
        }
    }

    public static Affine2 Transform(in Affine2 bone, AnimAttachment a)
        => bone * Affine2.FromTRS(Vector2.Zero, a.Rotation, new Vector2(a.Scale));
}
