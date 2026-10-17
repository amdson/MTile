using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Sprite sheets in Aseprite's JSON export format — the de facto 2D interchange format:
// Aseprite, LibreSprite, TexturePacker and free-tex-packer all write it, and
// scripts/import-strip.py turns a loose row of drawings into it. Both of Aseprite's
// layouts load ("frames" as an array or as a filename-keyed hash). Read here:
//   frames[].frame             the rect in the texture
//   frames[].spriteSourceSize  where that (possibly trimmed) rect sits on the frame's canvas
//   frames[].sourceSize        the canvas — every frame shares frame 0's
//   frames[].duration          ms (Aseprite's default 100 when absent)
//   meta.frameTags             named animations: from/to + direction
//   meta.slices                named points; the one called "pivot" is the draw origin
//   meta.scale                 export scale, so callers size in ORIGINAL art pixels
// Rotated (TexturePacker "rotated": true) frames are rejected rather than half-supported.
//
// The older one-row strips (SpriteAttachmentAsset: FrameWidth/FrameCount/PivotX/Y) load
// through FromStrip, so one loader serves both. Pure data — no GraphicsDevice — so it is
// unit-testable; SpriteSheetLibrary attaches textures.
public readonly record struct SpriteSheetFrame(Rectangle Source, Vector2 Offset, float Seconds);

public enum SpriteTagDirection { Forward, Reverse, PingPong, PingPongReverse }

public sealed record SpriteSheetTag(string Name, int From, int To, SpriteTagDirection Direction);

// A slice key applies from its Frame onward, until the next key (Aseprite's semantics).
public readonly record struct SpriteSliceKey(int Frame, Vector2 Point);

public sealed class SpriteSheetData
{
    public const float DefaultFrameSeconds = 0.1f;

    public string Image { get; private init; }
    public SpriteSheetFrame[] Frames { get; private init; }
    public Point Canvas { get; private init; }
    // Sheet px per original art px (Aseprite meta.scale; import-strip --downsample 2 → 0.5).
    public float Scale { get; private init; } = 1f;
    public IReadOnlyDictionary<string, SpriteSheetTag> Tags => _tags;
    public IReadOnlyDictionary<string, SpriteSliceKey[]> Slices => _slices;
    // True for the legacy equal-width strips, whose cells abut with no transparent border.
    public bool AbuttingCells { get; private init; }
    // The legacy spec a strip was loaded from (its RigUnitsPerPixel, TipPixels, trail);
    // null for an Aseprite sheet, whose size lives in the effect catalog instead.
    public SpriteAttachmentAsset Strip { get; private init; }

    private Dictionary<string, SpriteSheetTag> _tags = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SpriteSliceKey[]> _slices = new(StringComparer.OrdinalIgnoreCase);

    // True when the JSON root looks like an Aseprite export rather than a legacy strip spec.
    public static bool IsSheetJson(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("frames", out _);

    public static SpriteSheetData Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    public static SpriteSheetData Parse(JsonElement root)
    {
        if (!IsSheetJson(root)) throw new InvalidDataException("No \"frames\" — not an Aseprite sheet.");
        var framesEl = root.GetProperty("frames");
        var list = new List<JsonElement>();
        if (framesEl.ValueKind == JsonValueKind.Array)
            foreach (var f in framesEl.EnumerateArray()) list.Add(f);
        else if (framesEl.ValueKind == JsonValueKind.Object)
            foreach (var p in framesEl.EnumerateObject()) list.Add(p.Value);   // hash: document order
        if (list.Count == 0) throw new InvalidDataException("Sheet has no frames.");

        var frames = new SpriteSheetFrame[list.Count];
        Point canvas = default;
        for (int i = 0; i < list.Count; i++)
        {
            var f = list[i];
            if (f.TryGetProperty("rotated", out var rot) && rot.ValueKind == JsonValueKind.True)
                throw new NotSupportedException($"Frame {i} is rotated; export without rotation.");
            var src = Rect(f.GetProperty("frame"));
            if (src.Width <= 0 || src.Height <= 0) throw new InvalidDataException($"Frame {i} is empty.");
            var offset = Vector2.Zero;
            if (f.TryGetProperty("spriteSourceSize", out var sss))
                offset = new Vector2(sss.GetProperty("x").GetSingle(), sss.GetProperty("y").GetSingle());
            var size = f.TryGetProperty("sourceSize", out var ss)
                ? new Point(ss.GetProperty("w").GetInt32(), ss.GetProperty("h").GetInt32())
                : new Point(src.Width, src.Height);
            if (i == 0) canvas = size;
            float seconds = f.TryGetProperty("duration", out var d) ? d.GetSingle() / 1000f : DefaultFrameSeconds;
            if (!(seconds > 0f)) seconds = DefaultFrameSeconds;
            frames[i] = new SpriteSheetFrame(src, offset, seconds);
        }

        string image = null;
        float scale = 1f;
        var tags = new Dictionary<string, SpriteSheetTag>(StringComparer.OrdinalIgnoreCase);
        var slices = new Dictionary<string, SpriteSliceKey[]>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("meta", out var meta))
        {
            if (meta.TryGetProperty("image", out var im)) image = im.GetString();
            // Aseprite writes scale as a string ("1"); accept a number too.
            if (meta.TryGetProperty("scale", out var sc))
            {
                float s = sc.ValueKind == JsonValueKind.Number ? sc.GetSingle()
                        : float.TryParse(sc.GetString(), System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1f;
                if (s > 0f && float.IsFinite(s)) scale = s;
            }
            if (meta.TryGetProperty("frameTags", out var ft) && ft.ValueKind == JsonValueKind.Array)
                foreach (var t in ft.EnumerateArray())
                {
                    string name = t.GetProperty("name").GetString();
                    int from = t.GetProperty("from").GetInt32(), to = t.GetProperty("to").GetInt32();
                    if (from < 0 || to < from || to >= frames.Length)
                        throw new InvalidDataException($"Tag '{name}' spans {from}..{to} of {frames.Length} frames.");
                    tags[name] = new SpriteSheetTag(name, from, to, ParseDirection(
                        t.TryGetProperty("direction", out var dir) ? dir.GetString() : null));
                }
            if (meta.TryGetProperty("slices", out var sl) && sl.ValueKind == JsonValueKind.Array)
                foreach (var s in sl.EnumerateArray())
                {
                    var keys = new List<SpriteSliceKey>();
                    foreach (var k in s.GetProperty("keys").EnumerateArray())
                    {
                        var b = Rect(k.GetProperty("bounds"));
                        // A slice's pivot is relative to its bounds; without one, its center.
                        var p = k.TryGetProperty("pivot", out var pv)
                            ? new Vector2(b.X + pv.GetProperty("x").GetSingle(), b.Y + pv.GetProperty("y").GetSingle())
                            : new Vector2(b.X + b.Width * 0.5f, b.Y + b.Height * 0.5f);
                        keys.Add(new SpriteSliceKey(k.TryGetProperty("frame", out var kf) ? kf.GetInt32() : 0, p));
                    }
                    keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                    slices[s.GetProperty("name").GetString()] = keys.ToArray();
                }
        }
        if (string.IsNullOrWhiteSpace(image)) throw new InvalidDataException("meta.image is missing.");

        return new SpriteSheetData
        {
            Image = image, Frames = frames, Canvas = canvas, Scale = scale, _tags = tags, _slices = slices,
        };
    }

    // The legacy equal-width strip, viewed as a sheet: FrameCount cells in one row, one
    // pivot, every frame the default duration (callers time strips by window, not by frame).
    public static SpriteSheetData FromStrip(SpriteAttachmentAsset spec)
    {
        var frames = new SpriteSheetFrame[spec.FrameCount];
        for (int i = 0; i < frames.Length; i++)
            frames[i] = new SpriteSheetFrame(
                new Rectangle(i * spec.FrameWidth, 0, spec.FrameWidth, spec.FrameHeight), Vector2.Zero, DefaultFrameSeconds);
        var slices = new Dictionary<string, SpriteSliceKey[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["pivot"] = new[] { new SpriteSliceKey(0, new Vector2(spec.PivotX, spec.PivotY)) },
        };
        return new SpriteSheetData
        {
            Image = spec.Image, Frames = frames, Canvas = new Point(spec.FrameWidth, spec.FrameHeight),
            _slices = slices, AbuttingCells = true, Strip = spec,
        };
    }

    // Every frame lies inside the texture.
    public bool FitsTexture(int width, int height)
        => Array.TrueForAll(Frames, f => f.Source.X >= 0 && f.Source.Y >= 0
                                         && f.Source.Right <= width && f.Source.Bottom <= height);

    // A named slice's point on the canvas at `frame` (the last key at or before it).
    public bool TryGetPoint(string slice, int frame, out Vector2 point)
    {
        point = default;
        if (!_slices.TryGetValue(slice, out var keys) || keys.Length == 0) return false;
        point = keys[0].Point;
        foreach (var k in keys)
        {
            if (k.Frame > frame) break;
            point = k.Point;
        }
        return true;
    }

    // The draw origin on the canvas: the "pivot" slice, else the canvas center.
    public Vector2 PivotAt(int frame)
        => TryGetPoint("pivot", frame, out var p) ? p : new Vector2(Canvas.X * 0.5f, Canvas.Y * 0.5f);

    // Frame indices in play order. A null/unknown tag plays every frame forward.
    // Ping-pong doesn't repeat its turnaround frames — Aseprite's own playback.
    public int[] Sequence(string tag)
    {
        int from = 0, to = Frames.Length - 1;
        var dir = SpriteTagDirection.Forward;
        if (tag != null && _tags.TryGetValue(tag, out var t)) { from = t.From; to = t.To; dir = t.Direction; }
        int n = to - from + 1;
        var fwd = new int[n];
        for (int i = 0; i < n; i++) fwd[i] = from + i;
        if (dir == SpriteTagDirection.Reverse || dir == SpriteTagDirection.PingPongReverse) Array.Reverse(fwd);
        if (dir is SpriteTagDirection.Forward or SpriteTagDirection.Reverse || n < 3) return fwd;
        var seq = new int[2 * n - 2];
        for (int i = 0; i < n; i++) seq[i] = fwd[i];
        for (int i = 1; i < n - 1; i++) seq[n - 1 + i] = fwd[n - 1 - i];
        return seq;
    }

    public float Duration(int[] sequence)
    {
        float total = 0f;
        foreach (int f in sequence) total += Frames[f].Seconds;
        return total;
    }

    // The frame showing at `seconds` into the sequence; clamps to the last frame unless looping.
    public int FrameAt(int[] sequence, float seconds, bool loop)
    {
        float total = Duration(sequence);
        if (!(seconds > 0f)) return sequence[0];
        if (loop) seconds %= total;
        else if (seconds >= total) return sequence[^1];
        foreach (int f in sequence)
        {
            if (seconds < Frames[f].Seconds) return f;
            seconds -= Frames[f].Seconds;
        }
        return sequence[^1];
    }

    // The frame at `progress` ∈ [0,1] through the sequence, keeping the authored frame
    // durations' proportions — for effects stretched to a fixed lifetime.
    public int FrameAtProgress(int[] sequence, float progress)
        => FrameAt(sequence, Math.Clamp(progress, 0f, 1f) * Duration(sequence), loop: false);

    private static Rectangle Rect(JsonElement r)
        => new(r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(),
               r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32());

    private static SpriteTagDirection ParseDirection(string s) => s?.ToLowerInvariant() switch
    {
        "reverse"          => SpriteTagDirection.Reverse,
        "pingpong"         => SpriteTagDirection.PingPong,
        "pingpong_reverse" => SpriteTagDirection.PingPongReverse,
        _                  => SpriteTagDirection.Forward,
    };
}

public sealed record SpriteSheet(SpriteSheetData Data, Texture2D Texture)
{
    private Texture2D _silhouette;

    // The sheet as a flat white silhouette (premultiplied: rgb = a), built on first use —
    // drawn over a frame at partial opacity it lerps that frame toward white (hit flash).
    public Texture2D Silhouette
    {
        get
        {
            if (_silhouette != null) return _silhouette;
            var px = new Color[Texture.Width * Texture.Height];
            Texture.GetData(px);
            for (int i = 0; i < px.Length; i++) px[i] = new Color(px[i].A, px[i].A, px[i].A, px[i].A);
            _silhouette = new Texture2D(Texture.GraphicsDevice, Texture.Width, Texture.Height);
            _silhouette.SetData(px);
            return _silhouette;
        }
    }

    public void DisposeTextures()
    {
        Texture.Dispose();
        _silhouette?.Dispose();
    }

    // The frame's quad in local space, pivot at the origin, sized at `pixelSize` world
    // units per ORIGINAL art pixel. Corners in TL, TR, BR, BL order, with matching UVs.
    public void Quad(int frame, float pixelSize, Span<Vector2> corners, Span<Vector2> uvs)
    {
        var f = Data.Frames[frame];
        var pivot = Data.PivotAt(frame);
        float unit = pixelSize / Data.Scale;
        float left = (f.Offset.X - pivot.X) * unit, top = (f.Offset.Y - pivot.Y) * unit;
        float right = left + f.Source.Width * unit, bottom = top + f.Source.Height * unit;
        corners[0] = new Vector2(left, top);  corners[1] = new Vector2(right, top);
        corners[2] = new Vector2(right, bottom); corners[3] = new Vector2(left, bottom);
        // Abutting strip cells need a half-texel inset against neighbor bleed under linear
        // filtering; packed sheets carry a transparent border and map edge to edge.
        float inset = Data.AbuttingCells ? 0.5f : 0f;
        float tw = Texture.Width, th = Texture.Height;
        float u0 = (f.Source.Left + inset) / tw, u1 = (f.Source.Right - inset) / tw;
        float v0 = (f.Source.Top + inset) / th, v1 = (f.Source.Bottom - inset) / th;
        uvs[0] = new Vector2(u0, v0); uvs[1] = new Vector2(u1, v0);
        uvs[2] = new Vector2(u1, v1); uvs[3] = new Vector2(u0, v1);
    }
}

// A sheet frame through SpriteBatch, pivot at `at`. Origin stays zero and the corner is
// placed by hand: SpriteBatch's origin under a flip is not interpreted the same way by
// every XNA descendant (DesktopGL vs KNI), but a zero origin flips identically everywhere.
// `flipX` mirrors the art across its pivot in sheet space, before rotation.
public static class SheetBatch
{
    public static void Draw(SpriteBatch batch, SpriteSheet sheet, int frame, Vector2 at, float rotation,
                            float unit, bool flipX, Color color, Texture2D texture = null)
    {
        var data = sheet.Data;
        var f = data.Frames[frame];
        var pivot = data.PivotAt(frame);
        var corner = new Vector2(flipX ? pivot.X - f.Offset.X - f.Source.Width : f.Offset.X - pivot.X,
                                 f.Offset.Y - pivot.Y) * unit;
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        var pos = at + new Vector2(corner.X * c - corner.Y * s, corner.X * s + corner.Y * c);
        batch.Draw(texture ?? sheet.Texture, pos, f.Source, color, rotation, Vector2.Zero, unit,
                   flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
    }
}

// Name → sheet, loaded on first use from <dir>/<name>.json (either format) plus its
// image, premultiplied once. A missing or broken sheet is logged once and yields null,
// so a bad asset costs one effect, never the frame. Render-only.
public sealed class SpriteSheetLibrary : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly string _directory;
    private readonly Dictionary<string, SpriteSheet> _sheets = new(StringComparer.OrdinalIgnoreCase);

    public SpriteSheetLibrary(GraphicsDevice device, string directory = "Assets/AnimationEffects")
    {
        _device = device;
        _directory = directory;
    }

    public SpriteSheet Get(string name)
    {
        if (name == null) return null;
        if (_sheets.TryGetValue(name, out var cached)) return cached;
        Texture2D texture = null;
        try
        {
            SpriteSheetData data;
            using (var stream = TitleContent.TryOpenRead(Path.Combine(_directory, name + ".json")))
            {
                if (stream == null) throw new FileNotFoundException("Sheet definition missing.");
                using var doc = JsonDocument.Parse(stream);
                data = SpriteSheetData.IsSheetJson(doc.RootElement)
                    ? SpriteSheetData.Parse(doc.RootElement)
                    : FromStrip(doc.RootElement);
            }
            using var png = TitleContent.TryOpenRead(Path.Combine(_directory, data.Image));
            if (png == null) throw new FileNotFoundException(data.Image);
            texture = Texture2D.FromStream(_device, png);
            if (!data.FitsTexture(texture.Width, texture.Height))
                throw new InvalidDataException($"Frames fall outside the {texture.Width}x{texture.Height} image.");
            var pixels = new Color[texture.Width * texture.Height];
            texture.GetData(pixels);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.FromNonPremultiplied(pixels[i].R, pixels[i].G, pixels[i].B, pixels[i].A);
            texture.SetData(pixels);
            return _sheets[name] = new SpriteSheet(data, texture);
        }
        catch (Exception e)
        {
            texture?.Dispose();
            Console.WriteLine($"Sprite sheet '{name}': {e.Message}");
            return _sheets[name] = null;
        }
    }

    private static SpriteSheetData FromStrip(JsonElement root)
    {
        var spec = root.Deserialize<SpriteAttachmentAsset>();
        if (spec == null || string.IsNullOrWhiteSpace(spec.Image)) throw new InvalidDataException("Missing image.");
        if (spec.FrameCount <= 0 || spec.FrameWidth <= 0 || spec.FrameHeight <= 0)
            throw new InvalidDataException("Invalid strip dimensions.");
        return SpriteSheetData.FromStrip(spec);
    }

    public void Dispose()
    {
        foreach (var s in _sheets.Values) s?.DisposeTextures();
        _sheets.Clear();
    }
}
