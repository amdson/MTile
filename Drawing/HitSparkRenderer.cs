using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// One-shot sprite bursts at hit points: the radial knife_hit on a body hit, and the
// directional knife_hit_wall where a player's attack strikes terrain (its +X points INTO
// the surface; the spray kicks back out along −X). Strips reuse the attachment asset spec
// (Assets/AnimationEffects/<name>.json); RigUnitsPerPixel is read as world px per pixel
// here, since a burst hangs in the world rather than on a bone. Render-only: spawned
// from presentation hooks, aged by real dt, never read by the sim.
public sealed class HitSparkRenderer : IDisposable
{
    public const string BodyHit = "knife_hit";
    public const string WallHit = "knife_hit_wall";

    private const float BodySeconds = 0.24f;
    private const float WallSeconds = 0.26f;

    private sealed record Loaded(SpriteAttachmentAsset Spec, Texture2D Texture);
    private struct Burst { public Loaded Asset; public Vector2 Pos; public float Rotation, Scale, Age, Life; }

    private readonly GraphicsDevice _device;
    private readonly string _assetDirectory;
    private readonly BasicEffect _effect;
    private readonly Dictionary<string, Loaded> _assets = new();
    private readonly List<Burst> _bursts = new();
    private readonly VertexPositionColorTexture[] _quad = new VertexPositionColorTexture[4];
    private static readonly short[] Indices = { 0, 1, 2, 0, 2, 3 };

    public HitSparkRenderer(GraphicsDevice device, string assetDirectory = "Assets/AnimationEffects")
    {
        _device = device;
        _assetDirectory = assetDirectory;
        _effect = new BasicEffect(device) { VertexColorEnabled = true, TextureEnabled = true,
            LightingEnabled = false, View = Matrix.Identity };
    }

    // `dir` is the hit direction (body) or the into-surface direction (wall).
    public void SpawnBody(Vector2 pos, Vector2 dir, float scale = 1f)
        => Spawn(BodyHit, pos, dir, scale, BodySeconds);
    public void SpawnWall(Vector2 pos, Vector2 dir, float scale = 1f)
        => Spawn(WallHit, pos, dir, scale, WallSeconds);

    private void Spawn(string name, Vector2 pos, Vector2 dir, float scale, float life)
    {
        var asset = Load(name);
        if (asset == null) return;
        float rot = dir.LengthSquared() > 1e-6f ? MathF.Atan2(dir.Y, dir.X) : 0f;
        _bursts.Add(new Burst { Asset = asset, Pos = pos, Rotation = rot, Scale = scale, Life = life });
    }

    public void Update(float dt)
    {
        for (int i = _bursts.Count - 1; i >= 0; i--)
        {
            var b = _bursts[i];
            b.Age += dt;
            if (b.Age >= b.Life) _bursts.RemoveAt(i);
            else _bursts[i] = b;
        }
    }

    public void Clear() => _bursts.Clear();

    // Call outside SpriteBatch, in the world-space effects pass.
    public void Draw(Matrix camera)
    {
        if (_bursts.Count == 0) return;
        _effect.World = camera;
        var vp = _device.Viewport;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1);
        _device.BlendState = BlendState.AlphaBlend;
        _device.DepthStencilState = DepthStencilState.None;
        _device.RasterizerState = RasterizerState.CullNone;
        _device.SamplerStates[0] = SamplerState.LinearClamp;

        foreach (var b in _bursts)
        {
            var spec = b.Asset.Spec;
            int frame = spec.FrameAt(b.Age / b.Life);
            float unit = spec.RigUnitsPerPixel * b.Scale;
            float left = -spec.PivotX * unit, top = -spec.PivotY * unit;
            float right = left + spec.FrameWidth * unit, bottom = top + spec.FrameHeight * unit;
            var xf = Matrix.CreateRotationZ(b.Rotation) * Matrix.CreateTranslation(b.Pos.X, b.Pos.Y, 0);
            Vector2 P(float x, float y) => Vector2.Transform(new Vector2(x, y), xf);
            // Half texel inset prevents adjacent frame bleed under linear filtering.
            float u0 = (frame * spec.FrameWidth + .5f) / b.Asset.Texture.Width;
            float u1 = ((frame + 1) * spec.FrameWidth - .5f) / b.Asset.Texture.Width;
            float v0 = .5f / spec.FrameHeight, v1 = 1 - v0;
            _quad[0] = Vertex(P(left, top), u0, v0);
            _quad[1] = Vertex(P(right, top), u1, v0);
            _quad[2] = Vertex(P(right, bottom), u1, v1);
            _quad[3] = Vertex(P(left, bottom), u0, v1);
            _effect.Texture = b.Asset.Texture;
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, _quad, 0, 4, Indices, 0, 2);
            }
        }
    }

    private static VertexPositionColorTexture Vertex(Vector2 p, float u, float v)
        => new(new Vector3(p, 0), Color.White, new Vector2(u, v));

    // Same loading contract as SpriteAttachmentRenderer: catalog name → json + strip,
    // premultiplied once, a missing asset logged once and the burst skipped.
    private Loaded Load(string name)
    {
        if (_assets.TryGetValue(name, out var cached)) return cached;
        Texture2D texture = null;
        try
        {
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
            Console.WriteLine($"Hit spark '{name}': {e.Message}");
            return _assets[name] = null;
        }
    }

    public void Dispose()
    {
        foreach (var asset in _assets.Values) asset?.Texture.Dispose();
        _effect.Dispose();
        _bursts.Clear();
    }
}
