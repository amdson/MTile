using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace MTile;

// Authored reference-arc clip for the ballistic corrector (BALLISTIC_CORRECTOR_PLAN §1):
// a 2D cubic Hermite curve p(t), t ∈ [0,1], authored in PIXELS against a reference
// obstacle, plus two anchor points — Entry and Gate — that say which clip-space points
// bind to the maneuver's measured entry pose and measured gate at retarget time.
// Parametric, so vertical phases (mantle pull-up) author fine — there is no monotone-x
// restriction. Tangents are 2D vectors: after retargeting, the entry tangent IS the
// incoming velocity up to the frame's time scaling. ReferencePath retargets the clip at
// Enter; the clip carries no dynamics and does not need to be flyable.
//
// The anchors are what make the arc reusable: ReferenceFrame normalizes by
// (Gate − Entry) and rescales onto (gateWorld − entryWorld), so one 26×40px pull-up
// arc fits ledges of any height and either facing. Because the map is scale-invariant,
// the authoring box size is a readability choice, not a behavioral one — and keys are
// free to sit before the entry or past the gate.
//
// The anchor defaults, (0,0) and (1,-1), reproduce the old normalized-frame convention
// exactly, so pre-anchor clip files keep loading and mapping identically.
//
// Key T values are auto-derived from chord length by the editor (≈ constant-speed
// parametrization); tangent magnitudes modulate local speed around that.
//
// Authored/edited with:  dotnet run --project MTile.Demo -- --ref <name>
// Stored under ReferenceClips/<name>.json at the repo root.
public sealed class HermiteClipKey
{
    public float T { get; set; }    // curve parameter in [0,1]
    public float X { get; set; }    // position
    public float Y { get; set; }
    public float TX { get; set; }   // tangent dp/dt
    public float TY { get; set; }

    [JsonIgnore] public Vector2 Pos { get => new(X, Y); set { X = value.X; Y = value.Y; } }
    [JsonIgnore] public Vector2 Tan { get => new(TX, TY); set { TX = value.X; TY = value.Y; } }
}

public sealed class HermiteClipDocument
{
    public string Name { get; set; } = "";

    // Seconds the arc is meant to take end to end. It is what makes arc time comparable
    // to ANIMATION clip time: an animation riding this arc advances along it at
    // τ · (animDuration / Duration), so a 0.4s clip on a 0.3s arc reaches the gate a
    // third of the way before its last keyframe. Tooling reads this; the sim takes its
    // pace from the state's own MovementConfig knob (LedgePullRefDuration /
    // DropdownRefDuration), so keep the two in step when retuning.
    public float Duration { get; set; } = 1f;

    // Retarget anchors in clip space. Defaults are the legacy normalized convention.
    public float EntryX { get; set; } = 0f;
    public float EntryY { get; set; } = 0f;
    public float GateX  { get; set; } = 1f;
    public float GateY  { get; set; } = -1f;

    public List<HermiteClipKey> Keys { get; set; } = new();

    [JsonIgnore] public Vector2 Entry { get => new(EntryX, EntryY); set { EntryX = value.X; EntryY = value.Y; } }
    [JsonIgnore] public Vector2 Gate  { get => new(GateX, GateY);   set { GateX  = value.X; GateY  = value.Y; } }

    // Clip-space extent between the anchors — the authoring box, and the quantity the
    // retarget normalizes by (ReferenceFrame owns that math, including its own handling
    // of a degenerate axis). Degenerate axes read as 1 here so callers that only want a
    // characteristic size — the editor's tangent-magnitude limits — never see zero.
    [JsonIgnore] public Vector2 Span
    {
        get
        {
            Vector2 s = Gate - Entry;
            if (MathF.Abs(s.X) < 1e-4f) s.X = 1f;
            if (MathF.Abs(s.Y) < 1e-4f) s.Y = 1f;
            return s;
        }
    }

    [JsonIgnore] public bool IsLegacyNormalized
        => Entry == Vector2.Zero && Gate == new Vector2(1f, -1f);

    [JsonIgnore] public string FilePath;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public void SortKeys() => Keys.Sort((a, b) => a.T.CompareTo(b.T));

    // Re-derive key T values from cumulative chord length (list order is curve order).
    public void RederiveT()
    {
        if (Keys.Count < 2) { if (Keys.Count == 1) Keys[0].T = 0f; return; }
        float total = 0f;
        Span<float> cum = Keys.Count <= 64 ? stackalloc float[Keys.Count] : new float[Keys.Count];
        cum[0] = 0f;
        for (int i = 1; i < Keys.Count; i++)
        {
            total += Vector2.Distance(Keys[i].Pos, Keys[i - 1].Pos);
            cum[i] = total;
        }
        for (int i = 0; i < Keys.Count; i++)
            Keys[i].T = total > 1e-6f ? cum[i] / total : i / (float)(Keys.Count - 1);
    }

    // Cubic Hermite p(t) over the keys; linear extrapolation past the ends using the
    // endpoint tangents (retarget sampling may probe slightly outside [0,1]).
    public Vector2 Eval(float t)
    {
        if (Keys.Count == 0) return Vector2.Zero;
        if (Keys.Count == 1) return Keys[0].Pos + (t - Keys[0].T) * Keys[0].Tan;
        var first = Keys[0];
        var last = Keys[^1];
        if (t <= first.T) return first.Pos + (t - first.T) * first.Tan;
        if (t >= last.T) return last.Pos + (t - last.T) * last.Tan;

        int i = SegmentIndex(t);
        var (k0, k1) = (Keys[i], Keys[i + 1]);
        float h = k1.T - k0.T;
        float s = (t - k0.T) / h;
        float s2 = s * s, s3 = s2 * s;
        return (2f * s3 - 3f * s2 + 1f) * k0.Pos
             + (s3 - 2f * s2 + s) * h * k0.Tan
             + (-2f * s3 + 3f * s2) * k1.Pos
             + (s3 - s2) * h * k1.Tan;
    }

    public Vector2 EvalTangent(float t)
    {
        if (Keys.Count == 0) return Vector2.Zero;
        if (Keys.Count == 1 || t <= Keys[0].T) return Keys[0].Tan;
        if (t >= Keys[^1].T) return Keys[^1].Tan;

        int i = SegmentIndex(t);
        var (k0, k1) = (Keys[i], Keys[i + 1]);
        float h = k1.T - k0.T;
        float s = (t - k0.T) / h;
        float s2 = s * s;
        return (6f * s2 - 6f * s) * (k0.Pos - k1.Pos) / h
             + (3f * s2 - 4f * s + 1f) * k0.Tan
             + (3f * s2 - 2f * s) * k1.Tan;
    }

    private int SegmentIndex(float t)
    {
        for (int i = Keys.Count - 2; i >= 1; i--)
            if (t >= Keys[i].T) return i;
        return 0;
    }

    public static HermiteClipDocument Load(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var doc = Load(stream);
        if (doc != null) doc.FilePath = path;
        return doc;
    }

    // Stream overload for host-portable loading (TitleContent — web builds have
    // no direct file I/O). FilePath stays null; only the path overload sets it.
    public static HermiteClipDocument Load(Stream stream)
    {
        var doc = JsonSerializer.Deserialize<HermiteClipDocument>(stream, Opts);
        if (doc == null) return null;
        doc.Keys ??= new List<HermiteClipKey>();
        doc.SortKeys();
        return doc;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
        FilePath = path;
    }

    // Fresh clip: a plausible vault-ish arc across a 26×40px reference ledge.
    public static HermiteClipDocument NewDefault(string name)
    {
        var doc = new HermiteClipDocument
        {
            Name = name,
            Entry = Vector2.Zero,
            // A 16×16 px up-and-across box — a neutral starting vault arc. The span is
            // a MANEUVER SHAPE sized to the player's body, so it is body-relative px and
            // deliberately independent of Chunk.TileSize. This used to be 26×−40, which
            // is RefLedgeW/RefLedgeH verbatim: every arc made in the editor silently
            // started life ledge-pull-shaped, and any that never had its anchors
            // retouched still is. Author the real span deliberately.
            Gate  = new Vector2(16f, -16f),
            Keys =
            {
                new HermiteClipKey { X = 0f,   Y = 0f,    TX = 16f, TY = -32f },
                new HermiteClipKey { X = 16f,  Y = -16f,  TX = 24f, TY = 0f   },
            },
        };
        doc.RederiveT();
        return doc;
    }

    // Rescale clip space in place about the Entry anchor (positions and tangents alike,
    // key T values untouched). The retarget map normalizes by the anchor span, so this
    // leaves the mapped world arc bit-identical — it only changes the units the editor
    // shows. Used to convert legacy normalized clips to a pixel authoring box.
    public void RescaleClipSpace(Vector2 factor)
    {
        Vector2 e = Entry;
        foreach (var k in Keys)
        {
            k.Pos = e + (k.Pos - e) * factor;
            k.Tan = k.Tan * factor;
        }
        Gate = e + (Gate - e) * factor;
    }
}
