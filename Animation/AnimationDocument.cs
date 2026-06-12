using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MTile;

// One keyframe: a full pose placed at a point on the normalized [0,1] timeline.
public sealed class AnimationKeyframe
{
    public float                Time     { get; set; }
    public List<PoseBoneEntry>  Bones    { get; set; } = new();
    // Contact annotations active at this keyframe (planted feet / external pins).
    // Null on legacy files → no contacts (airborne); the locomotion solver then
    // falls back to the velocity-driven phase advance. See ContactLabel.
    public List<ContactLabel>   Contacts { get; set; }
}

// Which part of the rig an animation owns when composed as a layer. Movement clips
// are FullBody; an action overlay (slash/stab) is typically UpperBody so the legs
// keep walking underneath. Resolved to a concrete bone set per skeleton by BoneMask.
// FullBody must stay value 0: it is the serialization default, omitted on save
// (WhenWritingDefault) so legacy files stay textually stable.
public enum AnimRegion { FullBody, UpperBody, LowerBody }

// A named animation: a Type-tagged time series of keyframes, serialized as one JSON
// file. Sampling between keyframes (in the editor / a future runtime player) lerps
// the per-bone transforms. This is the unit a sidebar entry represents.
public sealed class AnimationDocument
{
    public string                  Name      { get; set; } = "unnamed";
    public string                  Type      { get; set; } = "Misc";
    // Name of the rig this clip was authored against (Skeletons/<Skeleton>.json).
    // Defaults to "biped" so pre-multirig pose files still resolve cleanly. New
    // captures always write this explicitly; CharacterAnimator filters its
    // bindings to clips whose Skeleton matches the rig it was constructed with.
    public string                  Skeleton  { get; set; } = "biped";
    public float                   Duration  { get; set; } = 1f;     // seconds for the full [0,1] timeline
    public bool                    Loop      { get; set; } = true;
    // Bone region this clip owns when layered (see AnimRegion). Missing in legacy
    // JSON → FullBody; FullBody is omitted on save so legacy files round-trip clean.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public AnimRegion              Region    { get; set; } = AnimRegion.FullBody;
    public List<AnimationKeyframe> Keyframes { get; set; } = new();

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
        // ContactSource reads/writes as "SelfPlant"/"External" rather than 0/1.
        Converters = { new JsonStringEnumConverter() },
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
                if (doc.Keyframes.Count == 0) continue;   // nothing usable (incl. pre-keyframe-era files)
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
