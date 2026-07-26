using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace MTile;

// Authored reference-arc clip for the ballistic corrector (BALLISTIC_CORRECTOR_PLAN §1):
// a 2D cubic Hermite curve p(t), t ∈ [0,1], in the maneuver's NORMALIZED frame — entry
// at (0,0), gate at (1,-1) by convention (y-down, so up is negative; x in units of the
// measured horizontal gap, y in units of the measured obstacle height). Parametric, so
// vertical phases (mantle pull-up) author fine — there is no monotone-x restriction.
// Tangents are 2D vectors: after retargeting, the entry tangent IS the incoming velocity
// up to the frame's time scaling. ReferencePath retargets the clip at Enter; the clip
// carries no dynamics and does not need to be flyable.
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
    public List<HermiteClipKey> Keys { get; set; } = new();

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
        var doc = JsonSerializer.Deserialize<HermiteClipDocument>(File.ReadAllText(path), Opts);
        if (doc == null) return null;
        doc.Keys ??= new List<HermiteClipKey>();
        doc.SortKeys();
        doc.FilePath = path;
        return doc;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
        FilePath = path;
    }

    // Fresh clip: a plausible vault-ish arc from entry (0,0) to gate (1,-1).
    public static HermiteClipDocument NewDefault(string name)
    {
        var doc = new HermiteClipDocument
        {
            Name = name,
            Keys =
            {
                new HermiteClipKey { X = 0f, Y = 0f, TX = 1.0f, TY = -2.0f },
                new HermiteClipKey { X = 1f, Y = -1f, TX = 1.4f, TY = 0f },
            },
        };
        doc.RederiveT();
        return doc;
    }
}
