using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace MTile;

// ENDPOINTS AND NAMED POINTS (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Endpoint data
// contract"; workplan chunk 4). An ENDPOINT is a location on the rig: a bone's Start (its
// joint — the parent's tip, or the root origin) or its End (its far tip). A NAMED POINT gives
// such a location a stable identity and a role ("support_l"), optionally with a local offset
// in the bone's frame (zero = the exact endpoint). Shared anatomical points live with the rig
// (Skeleton.Points); clip-specific ones with the clip (AnimationDocument.Points). Contact
// labels reference points by id (ContactLabel.Point); the legacy ContactLabel.Node keeps
// resolving to its exact current location — a bone's End — through the same resolver.
//
// Under the R·T·S chain every bone's far tip is world[i].Translation (SkeletonPose.
// ComputeWorld), and its Start is the parent's tip — so an endpoint never needs a "+Length"
// term, and an offset uses the bone's own world frame (orientation, facing and stretch
// included). One resolver serves picking, rendering, contacts, IK, the probe and attachments.
public enum BoneEnd { Start, End }

public sealed class NamedPoint
{
    public string  Id    { get; set; }       // stable id contacts reference ("support_l")
    public string  Bone  { get; set; }       // bone name
    public BoneEnd End   { get; set; } = BoneEnd.End;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string  Role  { get; set; }       // anatomical role, e.g. "support" / "hand"; retargeting maps by role
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float   Ox    { get; set; }       // local offset in the bone's frame at the endpoint (rig units)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float   Oy    { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string  Label { get; set; }

    [JsonIgnore] public Vector2 Offset => new(Ox, Oy);
    public NamedPoint Clone() => (NamedPoint)MemberwiseClone();
}

// A resolved reference: which bone, which end, what offset — ready to evaluate on a pose.
public readonly struct ResolvedPoint
{
    public readonly string  Id;          // the point id, or the legacy node name
    public readonly int     Bone;        // index into the rig
    public readonly BoneEnd End;
    public readonly Vector2 Offset;
    public bool IsExactTip => End == BoneEnd.End && Offset == Vector2.Zero;
    public ResolvedPoint(string id, int bone, BoneEnd end, Vector2 offset) { Id = id; Bone = bone; End = end; Offset = offset; }
}

public static class EndpointResolver
{
    // Resolve a contact label's target on `rig` for `clip`. Precedence: the clip's own
    // points, then the rig's, by id (ContactLabel.Point); then the legacy node name as that
    // bone's End with no offset. A label naming BOTH a point and a node that disagree is
    // ambiguous and rejected (false) — the migration must not leave conflicting records.
    public static bool TryResolve(Skeleton rig, AnimationDocument clip, ContactLabel label, out ResolvedPoint rp)
    {
        rp = default;
        if (label == null) return false;
        bool havePoint = !string.IsNullOrEmpty(label.Point) && TryResolvePoint(rig, clip, label.Point, out rp);
        if (!string.IsNullOrEmpty(label.Node))
        {
            int b = rig.IndexOf(label.Node);
            if (havePoint) return b < 0 || b == rp.Bone;          // both given: they must agree
            if (b < 0) return false;
            rp = new ResolvedPoint(label.Node, b, BoneEnd.End, Vector2.Zero);
            return true;
        }
        return havePoint;
    }

    // Resolve a point id (clip first, then rig), or a bare bone name as its End.
    public static bool TryResolvePoint(Skeleton rig, AnimationDocument clip, string id, out ResolvedPoint rp)
    {
        rp = default;
        if (string.IsNullOrEmpty(id)) return false;
        var np = clip?.Points?.Find(p => p.Id == id) ?? Find(rig.Points, id);
        if (np != null)
        {
            int b = rig.IndexOf(np.Bone);
            if (b < 0) return false;
            rp = new ResolvedPoint(np.Id, b, np.End, np.Offset);
            return true;
        }
        int bone = rig.IndexOf(id);
        if (bone < 0) return false;
        rp = new ResolvedPoint(id, bone, BoneEnd.End, Vector2.Zero);
        return true;
    }

    private static NamedPoint Find(IReadOnlyList<NamedPoint> pts, string id)
    {
        if (pts == null) return null;
        for (int i = 0; i < pts.Count; i++) if (pts[i].Id == id) return pts[i];
        return null;
    }

    // The bone index a contact label pins (the solver's contact identity), or -1. Contact
    // consumers pin bone TIPS: a point that is not an exact End is reported through
    // `exactTip` so a caller can refuse it rather than silently pin the wrong place.
    public static int BoneOf(Skeleton rig, AnimationDocument clip, ContactLabel label, out bool exactTip)
    {
        if (!TryResolve(rig, clip, label, out var rp)) { exactTip = false; return -1; }
        exactTip = rp.IsExactTip;
        return rp.Bone;
    }
    public static int BoneOf(Skeleton rig, AnimationDocument clip, ContactLabel label) => BoneOf(rig, clip, label, out _);

    // World position of a resolved point on a computed pose. `root` is the transform the
    // pose was computed under (a root bone's Start is its origin).
    public static Vector2 World(Affine2[] world, in Affine2 root, Skeleton rig, in ResolvedPoint p)
    {
        if (p.End == BoneEnd.End) return p.Offset == Vector2.Zero ? world[p.Bone].Translation : world[p.Bone].TransformPoint(p.Offset);
        int parent = rig.Bones[p.Bone].Parent;
        Vector2 start = parent >= 0 ? world[parent].Translation : new Vector2(root.Tx, root.Ty);
        if (p.Offset == Vector2.Zero) return start;
        // The bone's frame at its Start: same linear part as at its End, translated back.
        var w = world[p.Bone];
        return start + w.TransformVector(p.Offset);
    }

    // The role-named support points of a rig ("support_l"/"support_r" ...), for tools that
    // used to assume a bone called foot_l / foot_r exists.
    public static IEnumerable<NamedPoint> WithRole(Skeleton rig, string role)
    {
        if (rig.Points == null) yield break;
        foreach (var p in rig.Points) if (p.Role == role) yield return p;
    }
}
