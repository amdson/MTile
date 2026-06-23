using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace MTile;

// One bone in the serialized skeleton: topology + bind transform. Parents are
// referenced by NAME so reordering bones across edits / format revisions doesn't
// silently re-parent anything (a stable handle the way PoseBoneEntry.Bone is).
//
// The biped is a pure joint chain, so a bone's attach is almost always its parent's
// tip (parent.Length, 0). Tx/Ty/Sx/Sy are therefore NULLABLE and omitted from the
// file when they hold the implied chain default (attach = parent tip, scale = 1);
// only off-chain bones (the root's world offset, a knife jutting sideways from a hand)
// carry explicit values. ResolveBind fills the gaps at load.
public sealed class SkeletonBoneRecord
{
    public string Name     { get; set; }
    public string Parent   { get; set; }       // null → root
    public float? Tx       { get; set; }        // omitted ⇒ parent.Length (chain attach)
    public float? Ty       { get; set; }        // omitted ⇒ 0
    public float  Rotation { get; set; }        // bind (rest) orientation, local radians
    public float? Sx       { get; set; }        // omitted ⇒ 1
    public float? Sy       { get; set; }        // omitted ⇒ 1
    public float  Length   { get; set; }

    // Resolve the bind transform, deriving any omitted attach from the chain. A root
    // (no parent) defaults its attach to the origin; a child defaults to its parent's tip.
    public BoneTransform ResolveBind(float parentLength)
        => new(new Vector2(Tx ?? parentLength, Ty ?? 0f), Rotation, new Vector2(Sx ?? 1f, Sy ?? 1f));
}

// A serializable rig: bone proportions live here, NOT in the per-keyframe pose. The
// animation editor and the runtime both load this so every clip draws against the
// same figure. Animations carry only per-bone Rotation (PoseData) — translations,
// scales, and bone lengths come from the shared rig.
public sealed class SkeletonDocument
{
    public string                   Name  { get; set; } = "biped";
    public List<SkeletonBoneRecord> Bones { get; set; } = new();
}

public static class SkeletonStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Load Skeletons/<name>.json from `dir`. Returns null if missing or malformed
    // (caller falls back to a procedural builder).
    public static Skeleton Load(string dir, string name)
    {
        try
        {
            string path = Path.Combine(dir, name + ".json");
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<SkeletonDocument>(File.ReadAllText(path), Opts);
            if (doc?.Bones == null || doc.Bones.Count == 0) return null;
            return Build(doc);
        }
        catch { return null; }
    }

    public static void Save(SkeletonDocument doc, string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, doc.Name + ".json"),
                          JsonSerializer.Serialize(doc, Opts));
    }

    public static SkeletonDocument Capture(string name, Skeleton skel)
    {
        var doc = new SkeletonDocument { Name = name };
        for (int i = 0; i < skel.Count; i++)
        {
            var b = skel.Bones[i];
            // Mirror ResolveBind: emit attach/scale only when they DEVIATE from the chain
            // default (parent tip, scale 1), so a clean joint chain serializes to just
            // Name/Parent/Rotation/Length.
            float defTx = b.Parent >= 0 ? skel.Bones[b.Parent].Length : 0f;
            var rec = new SkeletonBoneRecord
            {
                Name     = b.Name,
                Parent   = b.Parent >= 0 ? skel.Bones[b.Parent].Name : null,
                Rotation = b.Bind.Rotation,
                Length   = b.Length,
            };
            if (b.Bind.Translation.X != defTx) rec.Tx = b.Bind.Translation.X;
            if (b.Bind.Translation.Y != 0f)    rec.Ty = b.Bind.Translation.Y;
            if (b.Bind.Scale.X != 1f)          rec.Sx = b.Bind.Scale.X;
            if (b.Bind.Scale.Y != 1f)          rec.Sy = b.Bind.Scale.Y;
            doc.Bones.Add(rec);
        }
        return doc;
    }

    // Build a runtime Skeleton from a SkeletonDocument. Parents are resolved by name,
    // and the records are re-ordered so each parent precedes its children (the order
    // SkeletonBuilder enforces). Unknown parent names throw — better to fail loud at
    // load than to silently re-root a bone.
    private static Skeleton Build(SkeletonDocument doc)
    {
        var byName = new Dictionary<string, SkeletonBoneRecord>(doc.Bones.Count);
        foreach (var r in doc.Bones) byName[r.Name] = r;

        var ordered = new List<SkeletonBoneRecord>(doc.Bones.Count);
        var seen    = new HashSet<string>();
        void Visit(SkeletonBoneRecord r)
        {
            if (!seen.Add(r.Name)) return;
            if (r.Parent != null)
            {
                if (!byName.TryGetValue(r.Parent, out var p))
                    throw new InvalidDataException($"Skeleton '{doc.Name}': bone '{r.Name}' parent '{r.Parent}' not found.");
                Visit(p);
            }
            ordered.Add(r);
        }
        foreach (var r in doc.Bones) Visit(r);

        var indexByName = new Dictionary<string, int>(ordered.Count);
        var b = new SkeletonBuilder(doc.Name);
        foreach (var r in ordered)
        {
            float parentLen = r.Parent != null ? byName[r.Parent].Length : 0f;
            var bind = r.ResolveBind(parentLen);
            int idx = r.Parent == null
                ? b.AddRoot(r.Name, bind, r.Length)
                : b.Add(r.Name, indexByName[r.Parent], bind, r.Length);
            indexByName[r.Name] = idx;
        }
        return b.Build();
    }
}
