using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// A shared strip asset. Pixels are converted to rig units before the bone's full
// affine transform (including facing and stretch) is applied.
public sealed class SpriteAttachmentAsset
{
    public string Image { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    public int FrameCount { get; set; }
    public float PivotX { get; set; }
    public float PivotY { get; set; }
    public float RigUnitsPerPixel { get; set; }
    public float[] TipPixels { get; set; }
    public float TrailSeconds { get; set; }

    public bool IsValid(int width, int height)
        => FrameWidth > 0 && FrameHeight > 0 && FrameCount > 0
           && (long)FrameWidth * FrameCount == width && FrameHeight == height
           && float.IsFinite(PivotX) && PivotX >= 0 && PivotX < FrameWidth
           && float.IsFinite(PivotY) && PivotY >= 0 && PivotY < FrameHeight
           && float.IsFinite(RigUnitsPerPixel) && RigUnitsPerPixel > 0
           && float.IsFinite(TrailSeconds) && TrailSeconds >= 0 && TrailSeconds <= 1
           && (TipPixels == null || (TipPixels.Length == FrameCount
               && Array.TrueForAll(TipPixels, t => float.IsFinite(t) && t >= 0 && t + PivotX < FrameWidth)));

    public int FrameAt(float progress)
        => Math.Clamp((int)(progress * FrameCount), 0, FrameCount - 1);
}

// History contains blade base/tip pairs, not a glowing dot. Each segment sweeps
// the actual blade span, tapering in opacity as it ages in world space.
public sealed class AttachmentTrail
{
    public readonly record struct Point(Vector2 Base, Vector2 Tip, float Age, float Weight);
    public readonly List<Point> Points = new();
    private AnimationDocument _clip;
    private float _time;
    private int _facing;
    private bool _feeding;

    public void Clear() { Points.Clear(); _feeding = false; _clip = null; }
    public void Age(float dt, float lifetime)
    {
        for (int i = Points.Count - 1; i >= 0; i--)
        {
            var p = Points[i];
            if (p.Age + dt >= lifetime) Points.RemoveAt(i);
            else Points[i] = p with { Age = p.Age + dt };
        }
        _feeding = false;
    }
    public void Push(AnimationDocument clip, float time, int facing, Vector2 start, Vector2 tip, float weight)
    {
        // Clip switches, rewinds, flips and teleports cannot connect two swings.
        if (!ReferenceEquals(clip, _clip) || time < _time || facing != _facing
            || (Points.Count > 0 && Vector2.Distance(Points[^1].Base, start)
                > MathF.Max(48, Vector2.Distance(start, tip) * 12)))
            Points.Clear();
        _clip = clip; _time = time; _facing = facing;
        Points.Add(new Point(start, tip, 0, weight));
        if (Points.Count > 32) Points.RemoveAt(0);
        _feeding = true;
    }
    public void EndFrame()
    {
        if (!_feeding) _clip = null; // reactivation starts a fresh sweep, old samples may finish fading
    }
}

public sealed class SpriteAttachmentRenderer : IDisposable
{
    // A resolved effect: the sheet and play order, its size in RIG units per ORIGINAL art
    // pixel, and its blend. Spec is the legacy strip behind it (blade tip + trail), or null.
    private sealed record Loaded(SpriteSheet Sheet, int[] Sequence, float Unit, Color Tint, float Glow)
    {
        public SpriteAttachmentAsset Spec => Sheet.Data.Strip;
    }
    private sealed class History
    {
        public readonly AttachmentTrail Trail = new();
        public float Lifetime;
    }
    private readonly GraphicsDevice _device;
    private readonly BasicEffect _effect;
    private readonly SpriteSheetLibrary _sheets;
    private readonly bool _ownsSheets;
    private readonly Dictionary<string, VfxDefinition> _catalog;
    private readonly Dictionary<string, Loaded> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<object, Dictionary<AnimAttachment, History>> _histories = new();
    private readonly VertexPositionColorTexture[] _quad = new VertexPositionColorTexture[4];
    private readonly VertexPositionColorTexture[] _ribbon = new VertexPositionColorTexture[4];
    private static readonly short[] Indices = { 0, 1, 2, 0, 2, 3 };
    private readonly List<AttachmentSample> _samples = new();

    // An attachment's Effect names either a vfx.json catalog entry (any sheet format, sized
    // and blended by the entry) or a legacy strip spec directly (sized by its own
    // RigUnitsPerPixel). Pass the host's library to share textures with VfxPlayer.
    public SpriteAttachmentRenderer(GraphicsDevice device, string assetDirectory = "Assets/AnimationEffects",
                                    SpriteSheetLibrary sheets = null)
    {
        _device = device;
        _sheets = sheets ?? new SpriteSheetLibrary(device, assetDirectory);
        _ownsSheets = sheets == null;
        _catalog = new Dictionary<string, VfxDefinition>(
            VfxPlayer.LoadCatalog(Path.Combine(assetDirectory, "vfx.json")), StringComparer.Ordinal);
        _effect = new BasicEffect(device) { VertexColorEnabled = true, TextureEnabled = true,
            LightingEnabled = false, View = Matrix.Identity };
    }

    private Loaded Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_assets.TryGetValue(name, out var cached)) return cached;
        try
        {
            // Effect is a catalog name, never an arbitrary path from a clip.
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains(".."))
                throw new InvalidDataException("Effect must be a catalog name.");
            if (_catalog.TryGetValue(name, out var def))
            {
                var sheet = _sheets.Get(def.Sheet) ?? throw new InvalidDataException($"Sheet '{def.Sheet}' did not load.");
                if (!(def.PixelSize > 0f)) throw new InvalidDataException("PixelSize must be positive.");
                // Catalog sizes are WORLD px; the attachment frame is rig space.
                return _assets[name] = new Loaded(sheet, sheet.Data.Sequence(def.Tag),
                    def.PixelSize / Game1.SkeletonScale, VfxPlayer.ParseTint(def.Tint), Math.Clamp(def.Glow, 0f, 1f));
            }
            var strip = _sheets.Get(name) ?? throw new InvalidDataException("Sheet did not load.");
            var spec = strip.Data.Strip
                ?? throw new InvalidDataException("An Aseprite sheet needs a vfx.json entry to give it a size.");
            if (!spec.IsValid(strip.Texture.Width, strip.Texture.Height))
                throw new InvalidDataException("Invalid strip dimensions or parameters.");
            return _assets[name] = new Loaded(strip, strip.Data.Sequence(null), spec.RigUnitsPerPixel, Color.White, 0f);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Attachment '{name}': {e.Message}");
            return _assets[name] = null;
        }
    }

    // Includes a binding's inactive start/end so legacy glow cannot flash back
    // on during the same clip. Failed assets retain the old visual fallback.
    public bool ReplacesEffect(CharacterAnimator animator, string effect)
    {
        animator.SampleAttachments(_samples);
        foreach (var s in _samples)
            if (s.Attachment.Effect == effect && Load(effect) != null) return true;
        return false;
    }

    public void DrawAnimator(Matrix camera, CharacterAnimator animator, in Affine2 root, float dt)
    {
        animator.SampleAttachments(_samples);
        Draw(camera, animator.Pose, root, _samples, animator, dt);
    }

    // Call outside SpriteBatch. owner identifies one actor; null gives a pure,
    // history-free sample suitable for a scrubber or still-frame exporter.
    public void Draw(Matrix camera, SkeletonPose pose, in Affine2 root,
                     IReadOnlyList<AttachmentSample> samples, object owner = null, float dt = 0)
    {
        var world = pose.ComputeWorld(root);
        int facing = root.M11 * root.M22 - root.M12 * root.M21 < 0 ? -1 : 1;
        Dictionary<AnimAttachment, History> history = null;
        if (owner != null)
        {
            if (!_histories.TryGetValue(owner, out history))
                _histories[owner] = history = new();
            foreach (var h in history.Values) h.Trail.Age(Math.Clamp(dt, 0, 1), h.Lifetime);
        }
        Prepare(camera);
        // First feed all histories, then draw the trail under the current blades.
        foreach (var sample in samples)
        {
            var a = sample.Attachment;
            var asset = Load(a.Effect);
            // The anchor was resolved by AttachmentSampling.Append, which had the clip in hand
            // — a clip-local point is not resolvable from here.
            if (asset == null || !a.TryProgress(sample.Time, out float t)) continue;
            var spec = asset.Spec;
            var transform = AttachmentSampling.Transform(world, root, pose.Skeleton, sample.Point, a);
            if (history != null && spec != null && spec.TrailSeconds > 0 && spec.TipPixels != null && a.EmitsTrail(sample.Time))
            {
                if (!history.TryGetValue(a, out var h)) history[a] = h = new History();
                h.Lifetime = spec.TrailSeconds;
                float length = spec.TipPixels[a.FrameAt(sample.Time, t, spec.FrameCount)] * spec.RigUnitsPerPixel;
                var start = transform.TransformPoint(new Vector2(length * (1 - Math.Clamp(a.TrailWidth, 0, 1)), 0));
                h.Trail.Push(sample.Clip, sample.Time, facing, start,
                    transform.TransformPoint(new Vector2(length, 0)), sample.Weight * Math.Clamp(a.TrailOpacity, 0, 1)
                    * Math.Clamp((1 - t) * 3, 0, 1));
            }
        }
        if (history != null)
            foreach (var h in history.Values)
            {
                DrawTrail(h);
                h.Trail.EndFrame();
            }
        _effect.TextureEnabled = true;
        Span<Vector2> corners = stackalloc Vector2[4];
        Span<Vector2> uvs = stackalloc Vector2[4];
        foreach (var sample in samples)
        {
            var a = sample.Attachment;
            var asset = Load(a.Effect);
            if (asset == null || !a.TryProgress(sample.Time, out float t)) continue;
            // The window (FrameTimes / Progress curve) picks a step through the play order.
            int frame = asset.Sequence[a.FrameAt(sample.Time, t, asset.Sequence.Length)];
            var transform = AttachmentSampling.Transform(world, root, pose.Skeleton, sample.Point, a);
            asset.Sheet.Quad(frame, asset.Unit, corners, uvs);
            // Premultiplied: vertex alpha is how much the effect occludes, so Glow (→ 0 alpha)
            // adds instead of covering — VfxPlayer's convention.
            var color = new Color(asset.Tint.ToVector3() * sample.Weight);
            color.A = (byte)MathF.Round(255f * sample.Weight * (1f - asset.Glow));
            for (int i = 0; i < 4; i++)
                _quad[i] = Vertex(transform.TransformPoint(corners[i]), color, uvs[i].X, uvs[i].Y);
            _effect.Texture = asset.Sheet.Texture;
            Submit(_quad);
        }
    }

    private void Prepare(Matrix camera)
    {
        _effect.World = camera;
        var vp = _device.Viewport;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1);
        _device.BlendState = BlendState.AlphaBlend;
        _device.DepthStencilState = DepthStencilState.None;
        _device.RasterizerState = RasterizerState.CullNone;
        _device.SamplerStates[0] = SamplerState.LinearClamp;
    }
    private static VertexPositionColorTexture Vertex(Vector2 p, Color c, float u = 0, float v = 0)
        => new(new Vector3(p, 0), c, new Vector2(u, v));
    // Streak look: an additive pale-blue sheet over the swept blade span (faint at the
    // hand, bright at the tip), plus a hot white band hugging the tip path so the cutting
    // edge reads as a crisp arc. Both fade with sample age.
    private static readonly Color TrailSheet = new(150, 200, 255);
    private static readonly Color TrailEdge  = new(235, 248, 255);
    private const float EdgeBand = .22f;   // fraction of the blade, measured in from the tip

    private void DrawTrail(History h)
    {
        _effect.TextureEnabled = false;
        _device.BlendState = BlendState.Additive;
        var points = h.Trail.Points;
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i];
            var prev = points[Math.Max(0, i - 2)];
            var next = points[Math.Min(points.Count - 1, i + 1)];
            // Smooth the ribbon between real pose samples; a fast swing often has
            // only a few rendered poses, and straight quads make its arc polygonal.
            Vector2 start = a.Base, tip = a.Tip;
            float Opacity(float t) => MathF.Pow(Math.Clamp(1 - MathHelper.Lerp(a.Age, b.Age, t) / h.Lifetime, 0, 1), 1.5f)
                                      * MathHelper.Lerp(a.Weight, b.Weight, t);
            float wa = Opacity(0);
            for (int step = 1; step <= 6; step++)
            {
                float t = step / 6f;
                var end = Vector2.CatmullRom(prev.Base, a.Base, b.Base, next.Base, t);
                var endTip = Vector2.CatmullRom(prev.Tip, a.Tip, b.Tip, next.Tip, t);
                float wb = Opacity(t);
                _ribbon[0] = Vertex(start, TrailSheet * (wa * .1f));
                _ribbon[1] = Vertex(tip, TrailSheet * (wa * .8f));
                _ribbon[2] = Vertex(endTip, TrailSheet * (wb * .8f));
                _ribbon[3] = Vertex(end, TrailSheet * (wb * .1f));
                Submit(_ribbon);
                // Edge band: the outer slice of the blade, solid at the tip, fading inward.
                var inA = Vector2.Lerp(tip, start, EdgeBand);
                var inB = Vector2.Lerp(endTip, end, EdgeBand);
                _ribbon[0] = Vertex(inA, TrailEdge * 0f);
                _ribbon[1] = Vertex(tip, TrailEdge * wa);
                _ribbon[2] = Vertex(endTip, TrailEdge * wb);
                _ribbon[3] = Vertex(inB, TrailEdge * 0f);
                Submit(_ribbon);
                start = end; tip = endTip; wa = wb;
            }
        }
        _device.BlendState = BlendState.AlphaBlend;
    }
    private void Submit(VertexPositionColorTexture[] vertices)
    {
        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, vertices, 0, 4, Indices, 0, 2);
        }
    }
    public void ClearHistory() => _histories.Clear();
    public void Dispose()
    {
        if (_ownsSheets) _sheets.Dispose();
        _effect.Dispose();
        _histories.Clear();
    }
}
