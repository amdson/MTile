using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// A rendered add-on hung off a NAMED POINT on the rig, over a window of the clip.
//
// Point, not a bone name: an attachment and a contact are the two things an endpoint can
// carry, and they now anchor the same way. A raw bone name used to mean a bone rename
// silently orphaned every effect on it — the same fragility ContactLabel.Node had. A bare
// bone name still RESOLVES (as that bone's End) so nothing needs a point declared to be
// posed against; declaring one is what makes the reference rename-safe.
public sealed class AnimAttachment
{
    public string Point { get; set; }
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
    float Time, float Weight, ResolvedPoint Point);

public static class AttachmentSampling
{
    // Later layers replace the same bone/effect binding, including outside its active
    // window; otherwise a masked-out base effect could shine through an overlay.
    // The resolve happens HERE, where the clip is in hand — a clip-local point cannot be
    // resolved from the renderer, which only sees the sample. Unresolvable attachments are
    // skipped rather than thrown on: unlike a contact, a missing effect anchor costs a
    // cosmetic, and the renderer must not take the frame down over one.
    public static void Append(AnimationDocument clip, float time, float weight,
                              List<AttachmentSample> output, Skeleton rig, float[] boneWeights = null)
    {
        if (clip?.Attachments == null) return;
        foreach (var a in clip.Attachments)
        {
            if (a == null || string.IsNullOrWhiteSpace(a.Point) || string.IsNullOrWhiteSpace(a.Effect)) continue;
            if (!EndpointResolver.TryResolvePoint(rig, clip, a.Point, out var rp)) continue;
            float w = boneWeights == null ? weight : boneWeights[rp.Bone];
            if (w <= 0) continue;
            output.RemoveAll(s => s.Attachment.Point == a.Point && s.Attachment.Effect == a.Effect);
            output.Add(new AttachmentSample(clip, a, time, w, rp));
        }
    }

    // The frame an attachment hangs in: the point's world POSITION carrying its bone's
    // orientation, facing and stretch, then the attachment's own rotation and scale. For an
    // exact tip — every attachment authored so far — this is the bone's world transform
    // unchanged, so offset and Start-end points are a strict addition here rather than a
    // reinterpretation. (Contacts still refuse them; the solver pins tips only.)
    public static Affine2 Transform(Affine2[] world, in Affine2 root, Skeleton rig,
                                    in ResolvedPoint p, AnimAttachment a)
    {
        var bone = world[p.Bone];
        if (!p.IsExactTip)
        {
            Vector2 at = EndpointResolver.World(world, root, rig, p);
            bone = new Affine2(bone.M11, bone.M12, bone.M21, bone.M22, at.X, at.Y);
        }
        return bone * Affine2.FromTRS(Vector2.Zero, a.Rotation, new Vector2(a.Scale));
    }
}
