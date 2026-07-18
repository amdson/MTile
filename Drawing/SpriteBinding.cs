using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace MTile;

// One bone of a sprite binding's bind pose: where the ARTWORK's joint is, expressed as
// the bone's local rotation + length under the pure joint chain. Length here is the
// binding's own (where the drawn elbow sits) — it never writes back to the shared rig;
// proportion mismatch is absorbed by the fraction-parameterized handles (SkinHandleLayout).
public sealed class SpriteBindPoseEntry
{
    public string Bone     { get; set; }
    public float  Rotation { get; set; }
    public float  Length   { get; set; }
}

// One deformation layer of a sprite binding. Layers split the artwork into regions that
// deform independently (each by only its own bones) so topologically unrelated areas —
// an arm drawn beside the torso — never share triangles or influence. Pixels are
// assigned to layers by a color-coded MASK image (SpriteBindingDocument.Mask): each
// opaque sprite pixel goes to the layer whose Color is nearest at that mask pixel.
public sealed class SpriteSkinLayer
{
    public string Name { get; set; }

    // "#RRGGBB" this layer is painted with in the mask. Null → the catch-all layer:
    // it receives every opaque pixel the mask doesn't cover (mask alpha < 128).
    public string Color { get; set; }

    // Bone names whose handles deform this layer. A trailing '*' is a prefix wildcard
    // ("arm_l_*" → arm_l_upper, arm_l_lower). Empty/null → all bones.
    public List<string> Bones { get; set; } = new();

    // True when `bone` is matched by this layer's Bones patterns.
    public bool IncludesBone(string bone)
    {
        if (Bones == null || Bones.Count == 0) return true;
        foreach (var p in Bones)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (p.EndsWith("*", StringComparison.Ordinal))
            {
                if (bone.StartsWith(p.Substring(0, p.Length - 1), StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(p, bone, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

// A sprite→skeleton binding (SpriteBindings/<name>.json): which PNG, how image pixels map
// into rig-local units, and the bind pose that aligns the rig over the drawing. One
// binding drives every clip — the runtime deformation follows the live pose. Render-only.
public sealed class SpriteBindingDocument
{
    public string Skeleton { get; set; } = "biped";
    public string Image    { get; set; }            // sibling file next to the .json

    // rigLocal = imagePx * ImageToRigScale + (ImageToRigTx, ImageToRigTy)
    public float ImageToRigScale { get; set; } = 1f;
    public float ImageToRigTx    { get; set; }
    public float ImageToRigTy    { get; set; }

    public int MeshStep       { get; set; } = 10;   // grid spacing, image px
    public int AlphaThreshold { get; set; } = 8;    // cell kept if any pixel alpha ≥ this

    // Deformation-quality knobs (see MlsDeformer / SkinHandleLayout):
    //   WeightAlpha — falloff exponent of the MLS kernel (1 soft/broad … 2 tight/local).
    //                 Raise toward 2 when limbs grab pixels belonging to other limbs.
    //   HandleStep  — fraction spacing of handle samples along each bone (0.5 = mid+tip,
    //                 0.25 = quarters). Smaller = more local rigidity near joints.
    //   MaxHandles  — per-vertex handle budget after pruning (per-frame cost knob).
    public float WeightAlpha  { get; set; } = 2f;
    public float HandleStep   { get; set; } = 0.25f;
    public int   MaxHandles   { get; set; } = 8;

    // Optional layer split (see SpriteSkinLayer): a color-coded mask PNG (sibling file,
    // same dimensions as Image) plus the layer list in BACK-TO-FRONT draw order.
    // Absent → the whole sprite is one layer deformed by all bones.
    public string Mask { get; set; }
    public List<SpriteSkinLayer> Layers { get; set; }

    // Max per-channel distance (0–255) for a mask pixel to match a layer Color; anything
    // further falls to the catch-all layer. Lets the mask be painted ON A COPY of the
    // artwork — unpainted art pixels miss every layer color and land in the catch-all.
    // Raise if soft brush edges leave slivers unassigned; lower if art colors are close
    // to a paint color.
    public int MaskTolerance { get; set; } = 90;

    public List<SpriteBindPoseEntry> BindPose { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; set; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Null if missing or malformed (callers treat "no binding" as "draw the stick figure").
    public static SpriteBindingDocument Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<SpriteBindingDocument>(File.ReadAllText(path), Opts);
            if (doc?.Image == null) return null;
            doc.FilePath = path;
            return doc;
        }
        catch { return null; }
    }

    public void Save(string path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No path to save the binding to.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
        FilePath = path;
    }

    // Resolve the PNG path (Image is stored relative to the .json).
    [System.Text.Json.Serialization.JsonIgnore]
    public string ImagePath =>
        FilePath == null ? Image : Path.Combine(Path.GetDirectoryName(FilePath), Image);

    [System.Text.Json.Serialization.JsonIgnore]
    public string MaskPath =>
        Mask == null ? null : FilePath == null ? Mask : Path.Combine(Path.GetDirectoryName(FilePath), Mask);

    public Vector2 ImageToRig(Vector2 imagePx)
        => imagePx * ImageToRigScale + new Vector2(ImageToRigTx, ImageToRigTy);

    public Vector2 RigToImage(Vector2 rig)
        => (rig - new Vector2(ImageToRigTx, ImageToRigTy)) / ImageToRigScale;

    // Build the bind pose on `skel`: rig defaults, overridden per entry by name. Unknown
    // bone names are skipped (a binding survives rig growth; new bones sit at bind).
    public SkeletonPose CreateBindPose(Skeleton skel)
    {
        var pose = skel.CreatePose();
        foreach (var e in BindPose)
        {
            int i = skel.IndexOf(e.Bone);
            if (i < 0) continue;
            pose.Local[i] = new BoneTransform(Vector2.UnitX * e.Length, e.Rotation);
        }
        return pose;
    }

    // Capture a pose (rotations + the binding's lengths from Translation.X) into BindPose.
    public void CaptureBindPose(SkeletonPose pose)
    {
        BindPose.Clear();
        for (int i = 0; i < pose.Count; i++)
            BindPose.Add(new SpriteBindPoseEntry
            {
                Bone     = pose.Skeleton.Bones[i].Name,
                Rotation = pose.Local[i].Rotation,
                Length   = pose.Local[i].Translation.X,
            });
    }
}

// Where MLS handles sit on a rig: (bone, fraction-along-segment) pairs, fixed by the rig's
// topology. Sampling the SAME layout under the bind pose and the live pose is what makes
// the deformation proportion-tolerant — a handle is "60% along the forearm" in both
// spaces, regardless of how long each space draws the forearm.
//
// Per bone: a handle at the tip (t=1), plus a mid-segment handle (t=0.5) when the bone is
// long enough for one to matter. A child's t=0 is its parent's t=1, so joints are covered
// without duplicates; the root's "tip" is its joint.
public sealed class SkinHandleLayout
{
    private readonly int[]   _bone;
    private readonly int[]   _parent;   // -1 → root: sample the bone's own translation
    private readonly float[] _t;

    public int Count => _bone.Length;

    private SkinHandleLayout(int[] bone, int[] parent, float[] t)
    {
        _bone = bone; _parent = parent; _t = t;
    }

    // minSampledLength (rig units): bones shorter than this (feet, zero-length roots)
    // get only their tip handle — interior samples would sit on top of it. `step` is the
    // fraction spacing of interior samples (0.5 → mid+tip, 0.25 → quarters+tip): denser
    // handles constrain the rotation field more locally around joints. `includeBone`
    // restricts the layout to a bone subset (a layer's bones); null → all bones.
    public static SkinHandleLayout Create(Skeleton skel, float minSampledLength = 2f, float step = 0.5f,
                                          Func<string, bool> includeBone = null)
    {
        step = MathHelper.Clamp(step, 0.05f, 1f);
        var bone = new List<int>(); var parent = new List<int>(); var t = new List<float>();
        for (int i = 0; i < skel.Count; i++)
        {
            if (includeBone != null && !includeBone(skel.Bones[i].Name)) continue;
            int p = skel.Bones[i].Parent;
            if (p >= 0 && skel.Bones[i].Length >= minSampledLength)
                for (float f = step; f < 1f - 1e-3f; f += step)
                {
                    bone.Add(i); parent.Add(p); t.Add(f);
                }
            bone.Add(i); parent.Add(p); t.Add(1f);
        }
        return new SkinHandleLayout(bone.ToArray(), parent.ToArray(), t.ToArray());
    }

    // Evaluate handle positions under resolved world transforms (world[i].Translation is
    // bone i's far tip under the R·T·S chain, so a segment is parentTip → ownTip).
    public void Sample(ReadOnlySpan<Affine2> world, Span<Vector2> dest)
    {
        if (dest.Length < Count) throw new ArgumentException("Handle span too small.");
        for (int h = 0; h < Count; h++)
        {
            Vector2 tip = world[_bone[h]].Translation;
            dest[h] = _parent[h] < 0
                ? tip
                : Vector2.Lerp(world[_parent[h]].Translation, tip, _t[h]);
        }
    }
}
