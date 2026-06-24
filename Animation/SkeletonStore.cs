using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace MTile;

// One bone in the serialized skeleton: topology + rest orientation + length. Parents are
// referenced by NAME so reordering bones across edits / format revisions doesn't silently
// re-parent anything (a stable handle the way PoseBoneEntry.Bone is).
//
// The biped is a pure joint chain — every bone attaches at its parent's tip — so a bone is
// fully described by its rest Rotation and Length. Stale Tx/Ty/Sx/Sy in older files are
// silently ignored on load (unknown members) and scrubbed on the next save.
public sealed class SkeletonBoneRecord
{
    public string Name     { get; set; }
    public string Parent   { get; set; }       // null → root
    public float  Rotation { get; set; }        // rest (default) orientation, local radians
    public float  Length   { get; set; }
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
            doc.Bones.Add(new SkeletonBoneRecord
            {
                Name     = b.Name,
                Parent   = b.Parent >= 0 ? skel.Bones[b.Parent].Name : null,
                Rotation = b.Rotation,
                Length   = b.Length,
            });
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
            int idx = r.Parent == null
                ? b.AddRoot(r.Name, r.Rotation, r.Length)
                : b.Add(r.Name, indexByName[r.Parent], r.Rotation, r.Length);
            indexByName[r.Name] = idx;
        }
        return b.Build();
    }
}
