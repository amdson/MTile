using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// One entry of the effect catalog (Assets/AnimationEffects/vfx.json): which sheet plays,
// how long, how big, which way it faces, and how it blends.
public sealed class VfxDefinition
{
    public string Sheet { get; set; }
    public string Tag { get; set; }             // null = every frame
    public float? Seconds { get; set; }         // null = the tag's authored frame durations
    // World px per ORIGINAL art pixel — for a one-shot spawn and for a clip attachment
    // alike (SpriteAttachmentRenderer converts through the rig scale), so one entry looks
    // the same size wherever it plays.
    public float PixelSize { get; set; } = 0.09f;
    // "Direction": sheet +X along the spawn direction. "AgainstDirection": sheet +X
    // opposite it (a spray drawn flying toward −X then flies along the direction).
    // "Random": any angle. "None": upright.
    public string Align { get; set; } = "Direction";
    public bool RandomMirror { get; set; }      // flip across the facing axis half the time
    public float ScaleJitter { get; set; }      // ± fraction, per spawn
    public string Tint { get; set; }            // "#rrggbb"; null = the art's own color
    public float Glow { get; set; }             // 0 = alpha blend … 1 = fully additive
    // Which way the art itself points, in degrees in sheet space (0 = right, −90 = up).
    // Spawn/draw rotations aim the art's forward, so art drawn pointing up needs −90.
    public float ArtForward { get; set; }
}

// One-shot sprite effects in world space, named in a data catalog and fired from the
// presentation seam (Game1.PresentThisFrame, HitFeelSystem). Render-only: aged by real
// dt, never read by the sim, so random variation here is fine.
//
// Blending without a state change per effect: textures are premultiplied, the pass uses
// premultiplied AlphaBlend (One, InvSrcAlpha), and the vertex color's alpha scales how
// much the effect OCCLUDES. Alpha 1 is ordinary compositing; alpha 0 adds the color
// on top — so Glow is just 1 − vertex alpha.
public sealed class VfxPlayer : IDisposable
{
    public const string CatalogPath = "Assets/AnimationEffects/vfx.json";

    private struct Instance
    {
        public SpriteSheet Sheet; public int[] Sequence; public VfxDefinition Def;
        public Vector2 Pos; public float Rotation, Scale, MirrorY, Age, Life;
        public Color Color;
    }

    private readonly GraphicsDevice _device;
    private readonly SpriteSheetLibrary _sheets;
    private readonly BasicEffect _effect;
    private readonly Dictionary<string, VfxDefinition> _catalog;
    private readonly HashSet<string> _warned = new();
    private readonly List<Instance> _live = new();
    private readonly Random _rng = new();
    private readonly VertexPositionColorTexture[] _quad = new VertexPositionColorTexture[4];
    private static readonly short[] Indices = { 0, 1, 2, 0, 2, 3 };

    public VfxPlayer(GraphicsDevice device, SpriteSheetLibrary sheets, string catalogPath = CatalogPath)
    {
        _device = device;
        _sheets = sheets;
        _effect = new BasicEffect(device) { VertexColorEnabled = true, TextureEnabled = true,
            LightingEnabled = false, View = Matrix.Identity };
        _catalog = LoadCatalog(catalogPath);
    }

    public static Dictionary<string, VfxDefinition> ParseCatalog(string json)
        => JsonSerializer.Deserialize<Dictionary<string, VfxDefinition>>(json, new JsonSerializerOptions
           {
               ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
           }) ?? new();

    // Missing or broken → empty (logged): a bad catalog costs effects, never the frame.
    public static Dictionary<string, VfxDefinition> LoadCatalog(string path)
    {
        try
        {
            using var stream = TitleContent.TryOpenRead(path);
            if (stream == null) { Console.WriteLine($"VFX catalog missing: {path}"); return new(); }
            using var reader = new StreamReader(stream);
            return new Dictionary<string, VfxDefinition>(ParseCatalog(reader.ReadToEnd()),
                                                         StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Console.WriteLine($"VFX catalog '{path}': {e.Message}");
            return new();
        }
    }

    public bool Has(string name) => _catalog.ContainsKey(name);

    // `dir` orients the effect per its Align; `tint` multiplies over the catalog tint.
    public void Spawn(string name, Vector2 pos, Vector2 dir, float scale = 1f, Color? tint = null)
    {
        if (!_catalog.TryGetValue(name, out var def))
        {
            if (_warned.Add(name)) Console.WriteLine($"VFX '{name}': not in the catalog.");
            return;
        }
        var sheet = _sheets.Get(def.Sheet);
        if (sheet == null) return;   // the library already logged why

        var seq = sheet.Data.Sequence(def.Tag);
        float life = def.Seconds ?? sheet.Data.Duration(seq);
        if (!(life > 0f)) return;

        float aim = dir.LengthSquared() > 1e-6f ? MathF.Atan2(dir.Y, dir.X) : 0f;
        float rot = def.Align?.ToLowerInvariant() switch
        {
            "againstdirection" => aim + MathF.PI,
            "random"           => (float)(_rng.NextDouble() * MathHelper.TwoPi),
            "none"             => 0f,
            _                  => aim,
        };
        rot -= MathHelper.ToRadians(def.ArtForward);
        float jitter = 1f + def.ScaleJitter * (float)(_rng.NextDouble() * 2.0 - 1.0);

        var color = ParseTint(def.Tint);
        if (tint is Color t)
            color = new Color(color.ToVector3() * t.ToVector3());
        color.A = (byte)MathF.Round(255f * (1f - Math.Clamp(def.Glow, 0f, 1f)));

        _live.Add(new Instance
        {
            Sheet = sheet, Sequence = seq, Def = def, Pos = pos, Rotation = rot,
            Scale = scale * jitter, MirrorY = def.RandomMirror && _rng.Next(2) == 0 ? -1f : 1f,
            Life = life, Color = color,
        });
    }

    public void Update(float dt)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var v = _live[i];
            v.Age += dt;
            if (v.Age >= v.Life) _live.RemoveAt(i);
            else _live[i] = v;
        }
    }

    public void Clear() => _live.Clear();

    // Call outside SpriteBatch, in the world-space effects pass.
    public void Draw(Matrix camera)
    {
        if (_live.Count == 0) return;
        _effect.World = camera;
        var vp = _device.Viewport;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1);
        _device.BlendState = BlendState.AlphaBlend;
        _device.DepthStencilState = DepthStencilState.None;
        _device.RasterizerState = RasterizerState.CullNone;
        _device.SamplerStates[0] = SamplerState.LinearClamp;

        Span<Vector2> corners = stackalloc Vector2[4];
        Span<Vector2> uvs = stackalloc Vector2[4];
        foreach (var v in _live)
        {
            int frame = v.Sheet.Data.FrameAtProgress(v.Sequence, v.Age / v.Life);
            v.Sheet.Quad(frame, v.Def.PixelSize * v.Scale, corners, uvs);
            var xf = Matrix.CreateScale(1f, v.MirrorY, 1f) * Matrix.CreateRotationZ(v.Rotation)
                   * Matrix.CreateTranslation(v.Pos.X, v.Pos.Y, 0);
            for (int i = 0; i < 4; i++)
                _quad[i] = new VertexPositionColorTexture(
                    new Vector3(Vector2.Transform(corners[i], xf), 0), v.Color, uvs[i]);
            _effect.Texture = v.Sheet.Texture;
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, _quad, 0, 4, Indices, 0, 2);
            }
        }
    }

    public static Color ParseTint(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Color.White;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            return Color.White;
        return new Color((int)(rgb >> 16) & 255, (int)(rgb >> 8) & 255, (int)rgb & 255);
    }

    // A catalog entry drawn immediately into an open SpriteBatch (world pass) — for art
    // whose placement the caller recomputes every frame, like a telegraph's weapon.
    // False when the entry or its sheet is unavailable, so the caller can fall back.
    public bool DrawInBatch(SpriteBatch batch, string name, string tag, float progress, Vector2 at,
                            float rotation, bool mirror, Color tint)
    {
        if (name == null || !_catalog.TryGetValue(name, out var def)) return false;
        var sheet = _sheets.Get(def.Sheet);
        if (sheet == null) return false;
        var seq = sheet.Data.Sequence(tag ?? def.Tag);
        int frame = sheet.Data.FrameAtProgress(seq, progress);
        var color = new Color(ParseTint(def.Tint).ToVector3() * tint.ToVector3() * (tint.A / 255f));
        color.A = (byte)MathF.Round(tint.A * (1f - Math.Clamp(def.Glow, 0f, 1f)));
        SheetBatch.Draw(batch, sheet, frame, at, rotation - MathHelper.ToRadians(def.ArtForward),
                        def.PixelSize / sheet.Data.Scale, mirror, color);
        return true;
    }

    public void Dispose()
    {
        _effect.Dispose();
        _live.Clear();
    }
}
