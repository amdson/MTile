using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MTile;

// A labeled non-bone construct attached to an animation: a reference point or a
// vector. Used to encode the relationship between the pose and a parametrized attack
// (e.g. a "spear_tip" point where the glow dot sits, a "stab_ray" vector). Authored in
// the editor per-keyframe, interpolated across keyframes, carried forward like contacts.
// Coordinates are stored as floats (System.Text.Json skips Vector2's fields), local to
// the optional Parent bone — or to the character root when Parent is null.
public enum AnimAdditionKind { Point, Vector }

public sealed class AnimAddition
{
    public string           Name   { get; set; }            // label, e.g. "spear_tip"
    public AnimAdditionKind Kind   { get; set; }            // Point | Vector
    public string           Parent { get; set; }            // anchor bone name; null = root space
    public float            Px     { get; set; }            // position (local to Parent / root)
    public float            Py     { get; set; }
    public float            Dx     { get; set; }            // Vector: components from the point
    public float            Dy     { get; set; }

    public AnimAddition Clone() => new()
    {
        Name = Name, Kind = Kind, Parent = Parent, Px = Px, Py = Py, Dx = Dx, Dy = Dy,
    };
}

// One keyframe: a full pose placed at a point on the normalized [0,1] timeline.
public sealed class AnimationKeyframe
{
    public float                Time      { get; set; }
    public List<PoseBoneEntry>  Bones     { get; set; } = new();
    // Labeled points/vectors authored on this keyframe (see AnimAddition). Carried
    // forward when a new keyframe is sampled.
    public List<AnimAddition>   Additions { get; set; }
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
    // Blend weight applied to bones OUTSIDE this clip's Region when it's layered as an
    // overlay (the Region's own bones always blend at full slot weight). 0 = a hard mask
    // (the legacy / default behavior: an UpperBody slash leaves the legs entirely to the
    // base clip). >0 lets a "whole-body" overlay lightly drive its off-region bones — e.g.
    // a Pulse cast authored Region=UpperBody with OffRegionWeight=0.3 braces the legs at
    // 30% without taking them over. Omitted on save when 0 so legacy files round-trip clean.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float                   OffRegionWeight { get; set; } = 0f;
    // Action overlays only: the share of this clip's [0,1] timeline that is the SETTLE — the
    // follow-through played over the action's recovery countdown rather than over the action
    // itself. The swing (the part the action's reported progress sweeps) is [0, 1−SettleShare];
    // the tail [1−SettleShare, 1] is remapped onto the frames RecoveryAction counts down after
    // the action exits, so end-lag reads as the same motion finishing instead of an idle. 0
    // (default, omitted on save) = the whole clip plays inside the action, the pre-settle
    // behavior. See CharacterAnimator's action-overlay resolve.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float                   SettleShare { get; set; } = 0f;
    // EXPLICIT motion intent (ClipMotion): Track when the clip authors its own body_path,
    // InPlace for a deliberately stationary clip. Null (legacy) resolves to whichever the
    // clip's data implies — a track if one is authored, else stationary.
    //
    // A clip used to be able to name a ReferenceArc and have its placement RESOLVED from
    // that file, which put a clip's path in another document and gave placement three
    // competing sources. Arcs are now references you map onto the clip's own path on demand
    // (ClipArcMap / `probe mapcom`, Scene ▸ Map com to arc) and display overlays you can
    // keep alongside it (ClipScene.Overlays) — one placement channel, owned by the clip.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MotionSource?           Motion { get; set; }
    // CLIP-LOCAL reference arcs (ClipArcs). An arc edited from inside the clip editor is
    // forked to here and belongs to this clip alone: it travels with a clone or a retarget,
    // cannot orphan when a clip is renamed, and never writes back to the shared
    // ReferenceClips/ file it came from. A Scene overlay with Local = true names one of
    // these; without the flag the name resolves to the shared arc instead.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<HermiteClipDocument> Arcs { get; set; }
    // Fixed scene reference geometry (ClipScene): ground line + blocks in clip scene space.
    // Null keeps the editor's legacy floor-line/obstacle-block preview; an explicit Scene —
    // including an empty one — replaces it. Reference data only; never a runtime collider.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClipScene               Scene { get; set; }
    // Clip-specific named points (EndpointResolver): locations on the rig this clip names
    // (a marker on a hand, a custom support point). Shared anatomical points live on the rig.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<NamedPoint>        Points { get; set; }
    // Clip-local bones layered onto the base rig (named by Skeleton) for THIS clip
    // only — e.g. a "knife" held in the hand during a slash, which shouldn't bloat the
    // shared biped rig that walk/idle draw against. Each must Parent an existing base
    // bone by name. Null/omitted on clips that add nothing (the common case), so legacy
    // files round-trip unchanged. Composed in by SkeletonComposition: the editor layers
    // the active clip's set; the runtime layers the union across all bound clips.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SkeletonBoneRecord> ExtraBones { get; set; }
    // Render attachments sampled on this clip's normalized timeline. Bone is an
    // existing joint (or a clip-local orientation bone); Effect names a shared asset.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnimAttachment> Attachments { get; set; }
    // Contacts as explicit phase intervals (planted feet / external pins / planner
    // requests). CLIP-LEVEL, not per-keyframe: a contact's lifetime is its own property, so
    // it can start between keys and a keyframe retime no longer drags it along. Null = no
    // contacts (airborne), and the locomotion solver falls back to velocity-driven phase
    // advance. See ContactSpan.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ContactSpan>    Contacts { get; set; }
    public List<AnimationKeyframe> Keyframes { get; set; } = new();

    [JsonIgnore] public string FilePath { get; set; }

    // Lazily-computed by AnimationSampler.IsCyclic: true iff the clip is a seamless loop
    // (Loop set AND the first/last keyframe poses match, as locomotion cycles duplicate the
    // seam). Drives whether C1 interpolation wraps tangents across the seam (cyclic) or zeros
    // them at the ends (one-shot). Null = not yet determined. Not serialized.
    [JsonIgnore] internal bool? CyclicCache;

    public void SortKeyframes() => Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
}

public static class AnimationStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // ContactSource reads/writes as "SelfPlant"/"External" rather than 0/1; its own
        // converter goes first so the generic enum converter never sees it.
        Converters = { new ContactSourceConverter(), new JsonStringEnumConverter() },
    };

    // ContactSource by name, accepting the retired "PlannedSupport" as SelfPlant: the
    // 2026-09-18 clip backup (Backups/clips-*.tar.gz, the only copy of the pre-stub
    // library) serializes it, and the planner no longer needs a per-point opt-in.
    private sealed class ContactSourceConverter : JsonConverter<ContactSource>
    {
        public override ContactSource Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number) return (ContactSource)r.GetInt32();
            string s = r.GetString();
            return Enum.TryParse<ContactSource>(s, ignoreCase: true, out var v) ? v : ContactSource.SelfPlant;
        }
        public override void Write(Utf8JsonWriter w, ContactSource v, JsonSerializerOptions o)
            => w.WriteStringValue(v.ToString());
    }

    public static List<AnimationDocument> LoadAll(string dir)
    {
        var list = new List<AnimationDocument>();
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.GetFiles(dir, "*.json"))
        {
            var doc = ParseClip(File.ReadAllText(path), path);
            if (doc != null) list.Add(doc);
        }
        Sort(list);
        return list;
    }

    // Directory-less form of LoadAll for hosts that can't enumerate (WASM fetches over
    // HTTP): `dir` is a relative, forward-slashed title path whose index.json lists the
    // clip filenames in it. The manifest is generated at build time by MTile.Web's
    // GenerateClipManifests target. Missing/unreadable manifest → empty list; individual
    // malformed clips are skipped, same as LoadAll.
    public static List<AnimationDocument> LoadAllFromManifest(string dir)
    {
        var list = new List<AnimationDocument>();
        dir = dir.TrimEnd('/');
        string[] names;
        try
        {
            string manifest = ReadTitleText(dir + "/index.json");
            if (manifest == null) return list;
            names = JsonSerializer.Deserialize<string[]>(manifest, Opts);
        }
        catch { return list; }
        if (names == null) return list;

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string path = dir + "/" + name;
            string text;
            try { text = ReadTitleText(path); }
            catch { continue; }
            if (text == null) continue;
            var doc = ParseClip(text, path);
            if (doc != null) list.Add(doc);
        }
        Sort(list);
        return list;
    }

    private static string ReadTitleText(string path)
    {
        using var stream = TitleContent.TryOpenRead(path);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Shared per-file parse. Returns null for anything unusable (malformed JSON, no
    // keyframes) rather than throwing — a bad clip must not crash the editor or game.
    private static AnimationDocument ParseClip(string json, string path)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<AnimationDocument>(json, Opts);
            if (doc == null) return null;
            doc.Keyframes ??= new List<AnimationKeyframe>();
            if (doc.Keyframes.Count == 0) return null;   // nothing usable (incl. pre-keyframe-era files)
            doc.SortKeyframes();
            doc.FilePath = path;
            return doc;
        }
        catch { return null; }
    }

    private static void Sort(List<AnimationDocument> list) =>
        list.Sort((a, b) => string.CompareOrdinal(a.Type + "/" + a.Name, b.Type + "/" + b.Name));

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
