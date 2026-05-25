using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MTile;

// One keyframe: a full pose placed at a point on the normalized [0,1] timeline.
public sealed class AnimationKeyframe
{
    public float                Time  { get; set; }
    public List<PoseBoneEntry>  Bones { get; set; } = new();
}

// A named animation: a Type-tagged time series of keyframes, serialized as one JSON
// file. Sampling between keyframes (in the editor / a future runtime player) lerps
// the per-bone transforms. This is the unit a sidebar entry represents.
public sealed class AnimationDocument
{
    public string                  Name      { get; set; } = "unnamed";
    public string                  Type      { get; set; } = "Misc";
    public float                   Duration  { get; set; } = 1f;     // seconds for the full [0,1] timeline
    public bool                    Loop      { get; set; } = true;
    public List<AnimationKeyframe> Keyframes { get; set; } = new();

    // Legacy single-pose field from the earlier pose-only format. Migrated to a
    // t=0 keyframe on load, then nulled so it isn't written back.
    public List<PoseBoneEntry> Bones { get; set; }

    [JsonIgnore] public string FilePath { get; set; }

    public void SortKeyframes() => Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
}

public static class AnimationStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static List<AnimationDocument> LoadAll(string dir)
    {
        var list = new List<AnimationDocument>();
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<AnimationDocument>(File.ReadAllText(path), Opts);
                if (doc == null) continue;
                doc.Keyframes ??= new List<AnimationKeyframe>();

                // Migrate a legacy single-pose file into a one-keyframe animation.
                if (doc.Keyframes.Count == 0 && doc.Bones is { Count: > 0 })
                    doc.Keyframes.Add(new AnimationKeyframe { Time = 0f, Bones = doc.Bones });
                doc.Bones = null;

                if (doc.Keyframes.Count == 0) continue;   // nothing usable
                doc.SortKeyframes();
                doc.FilePath = path;
                list.Add(doc);
            }
            catch { /* skip malformed files rather than crash the editor */ }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Type + "/" + a.Name, b.Type + "/" + b.Name));
        return list;
    }

    public static void Save(AnimationDocument doc, string dir)
    {
        Directory.CreateDirectory(dir);
        if (string.IsNullOrEmpty(doc.FilePath))
            doc.FilePath = Path.Combine(dir, Sanitize(doc.Name) + ".json");
        File.WriteAllText(doc.FilePath, JsonSerializer.Serialize(doc, Opts));
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}
