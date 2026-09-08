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
    private sealed record Loaded(SpriteAttachmentAsset Spec, Texture2D Texture);
    private sealed class History
    {
        public readonly AttachmentTrail Trail = new();
        public float Lifetime;
    }
    private readonly GraphicsDevice _device;
    private readonly BasicEffect _effect;
    private readonly string _assetDirectory;
    private readonly Dictionary<string, Loaded> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<object, Dictionary<AnimAttachment, History>> _histories = new();
    private readonly VertexPositionColorTexture[] _quad = new VertexPositionColorTexture[4];
    private readonly VertexPositionColorTexture[] _ribbon = new VertexPositionColorTexture[4];
    private static readonly short[] Indices = { 0, 1, 2, 0, 2, 3 };
    private readonly List<AttachmentSample> _samples = new();

    public SpriteAttachmentRenderer(GraphicsDevice device, string assetDirectory = "Assets/AnimationEffects")
    {
        _device = device;
        _assetDirectory = assetDirectory;
        _effect = new BasicEffect(device) { VertexColorEnabled = true, TextureEnabled = true,
            LightingEnabled = false, View = Matrix.Identity };
    }

    private Loaded Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_assets.TryGetValue(name, out var cached)) return cached;
        Texture2D texture = null;
        try
        {
            // Effect is a catalog name, never an arbitrary path from a clip.
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains(".."))
                throw new InvalidDataException("Effect must be a catalog name.");
            using var stream = TitleContent.TryOpenRead(Path.Combine(_assetDirectory, name + ".json"));
            if (stream == null) throw new FileNotFoundException("Effect definition missing.");
            var spec = JsonSerializer.Deserialize<SpriteAttachmentAsset>(stream);
            if (spec == null || string.IsNullOrWhiteSpace(spec.Image)) throw new InvalidDataException("Missing image.");
            using var png = TitleContent.TryOpenRead(Path.Combine(_assetDirectory, spec.Image));
            if (png == null) throw new FileNotFoundException(spec.Image);
            texture = Texture2D.FromStream(_device, png);
            if (!spec.IsValid(texture.Width, texture.Height)) throw new InvalidDataException("Invalid strip dimensions or parameters.");
            var pixels = new Color[texture.Width * texture.Height];
            texture.GetData(pixels);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.FromNonPremultiplied(pixels[i].R, pixels[i].G, pixels[i].B, pixels[i].A);
            texture.SetData(pixels);
            return _assets[name] = new Loaded(spec, texture);
        }
        catch (Exception e)
        {
            texture?.Dispose();
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
            int bone = pose.Skeleton.IndexOf(a.Bone);
            if (asset == null || bone < 0 || !a.TryProgress(sample.Time, out float t)) continue;
            var spec = asset.Spec;
            var transform = AttachmentSampling.Transform(world[bone], a);
            if (history != null && spec.TrailSeconds > 0 && spec.TipPixels != null && a.EmitsTrail(sample.Time))
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
        foreach (var sample in samples)
        {
            var a = sample.Attachment;
            var asset = Load(a.Effect);
            int bone = pose.Skeleton.IndexOf(a.Bone);
            if (asset == null || bone < 0 || !a.TryProgress(sample.Time, out float t)) continue;
            var spec = asset.Spec;
            int frame = a.FrameAt(sample.Time, t, spec.FrameCount);
            var transform = AttachmentSampling.Transform(world[bone], a);
            float unit = spec.RigUnitsPerPixel;
            float left = -spec.PivotX * unit, top = -spec.PivotY * unit;
            float right = left + spec.FrameWidth * unit, bottom = top + spec.FrameHeight * unit;
            // Half texel inset prevents adjacent frame bleed under linear filtering.
            float u0 = (frame * spec.FrameWidth + .5f) / asset.Texture.Width;
            float u1 = ((frame + 1) * spec.FrameWidth - .5f) / asset.Texture.Width;
            float v0 = .5f / spec.FrameHeight, v1 = 1 - v0;
            Color color = Color.White * sample.Weight;
            _quad[0] = Vertex(transform.TransformPoint(new Vector2(left, top)), color, u0, v0);
            _quad[1] = Vertex(transform.TransformPoint(new Vector2(right, top)), color, u1, v0);
            _quad[2] = Vertex(transform.TransformPoint(new Vector2(right, bottom)), color, u1, v1);
            _quad[3] = Vertex(transform.TransformPoint(new Vector2(left, bottom)), color, u0, v1);
            _effect.Texture = asset.Texture;
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
    private void DrawTrail(History h)
    {
        _effect.TextureEnabled = false;
        var points = h.Trail.Points;
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i];
            var prev = points[Math.Max(0, i - 2)];
            var next = points[Math.Min(points.Count - 1, i + 1)];
            // Smooth the ribbon between real pose samples; a fast swing often has
            // only a few rendered poses, and straight quads make its arc polygonal.
            Vector2 start = a.Base, tip = a.Tip;
            float Opacity(float t) => MathF.Pow(Math.Clamp(1 - MathHelper.Lerp(a.Age, b.Age, t) / h.Lifetime, 0, 1), 2)
                                      * MathHelper.Lerp(a.Weight, b.Weight, t) * .7f;
            float wa = Opacity(0);
            for (int step = 1; step <= 4; step++)
            {
                float t = step / 4f;
                var end = Vector2.CatmullRom(prev.Base, a.Base, b.Base, next.Base, t);
                var endTip = Vector2.CatmullRom(prev.Tip, a.Tip, b.Tip, next.Tip, t);
                float wb = Opacity(t);
                _ribbon[0] = Vertex(start, Color.White * (wa * .15f));
                _ribbon[1] = Vertex(tip, Color.White * wa);
                _ribbon[2] = Vertex(endTip, Color.White * wb);
                _ribbon[3] = Vertex(end, Color.White * (wb * .15f));
                Submit(_ribbon);
                start = end; tip = endTip; wa = wb;
            }
        }
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
        foreach (var asset in _assets.Values) asset?.Texture.Dispose();
        _effect.Dispose();
        _histories.Clear();
    }
}
