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
// labels reference points by id and ONLY by id (ContactLabel.Point) — a bare bone name still
// resolves, as that bone's End, so a rig needs no point declared to be posed against.
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

    // The bone index a contact label pins (the solver's contact identity). THROWS on a label
    // the clip's data cannot honor, rather than dropping it: an unresolvable id, or a point
    // that is not an exact bone tip. Contact consumers pin TIPS — NamedPoint's offset and
    // Start end are expressible but no contact consumer implements them, so a label using one
    // used to vanish silently from the solve. Loud is the only honest option until they are
    // implemented. Callers asking "does any label sit on bone i?" are asking about valid data;
    // a clip that reaches them malformed is a bug upstream in authoring.
    public static int BoneOf(Skeleton rig, AnimationDocument clip, string point)
    {
        if (!TryResolvePoint(rig, clip, point, out var rp))
            throw new InvalidOperationException(
                $"contact '{point ?? "(null)"}' in clip '{clip?.Name ?? "?"}' resolves to no point or bone " +
                $"of rig '{rig.Name}' — name a point in Skeletons/{rig.Name}.json or the clip's Points.");
        if (!rp.IsExactTip)
            throw new InvalidOperationException(
                $"contact '{rp.Id}' in clip '{clip?.Name ?? "?"}' is not an exact bone tip " +
                $"({rig.Bones[rp.Bone].Name}.{rp.End}, offset {rp.Offset}). Contacts pin bone tips; " +
                $"offset and Start-end points are not supported by the solver yet.");
        return rp.Bone;
    }

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
