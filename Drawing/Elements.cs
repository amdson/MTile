using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Elements: add-ons hung off a named point of the rig — the knife's mechanism. A clip
// declares them over a window (AnimationDocument.Attachments, drawn by
// SpriteAttachmentRenderer); a CHARACTER declares them always-on (SpriteBindingDocument
// .Elements, drawn here), same AnimAttachment schema. Each Effect name resolves to an
// asset in Assets/AnimationEffects whose "Type" says what manages it:
//   Curtain — a cloth sheet (cloak, cape, banner, ears as a 1-column sheet) whose own
//             physics keeps it trailing the point with inertia. State is per character,
//             so it survives clip changes.
// Render-only: everything here runs after the sim stepped and the animator posed.

// A Curtain asset (<name>.json, "Type": "Curtain"). The image is the element hanging at
// rest, drawn as worn by a character facing RIGHT; a Columns × Rows grid over its opaque
// bounds becomes the cloth, top row pinned to the point.
public sealed class CurtainDefinition
{
    public string Type { get; set; }
    public string Image { get; set; }
    public float PixelSize { get; set; } = 0.06f;   // world px per art px
    public int Columns { get; set; } = 4;
    public int Rows { get; set; } = 8;
    // Where the point sits on the image (px); default: top center of the opaque bounds.
    public float? AnchorX { get; set; }
    public float? AnchorY { get; set; }
    // Pull back toward the drawn rest shape, per step: Stiffness at the pinned top,
    // easing to StiffnessTip at the hem. A HINT, not a spring: in steady motion only
    // Damping (air drag) against Gravity sets how far the cloth streams back, and a
    // strong shape pull cancels that. Raise for a stiff collar, keep low for cloth.
    public float Stiffness { get; set; } = 0.012f;
    public float StiffnessTip { get; set; } = 0f;
    // Velocity lost per step (0..1) — world-frame, so it is air drag: with Gravity it
    // sets the streaming angle at a given speed (≈ atan(Damping·60·v / Gravity)).
    public float Damping { get; set; } = 0.05f;
    public float Gravity { get; set; } = 600f;      // px/s², world's own by default
    public int Iterations { get; set; } = 6;
    public bool Front { get; set; }                 // over the body (default: behind it)
    // Pin the BOTTOM row instead of the top: things that stand up from their base (ears,
    // a plume) rather than hang from it. Stiffness then eases from the base upward.
    public bool PinBottom { get; set; }
    // Turn the rest shape with the point's bone (relative to the skin's bind pose), so
    // ears tilt with the head. Off: the rest shape stays upright (a cape from the neck).
    public bool FollowBone { get; set; }
    // Resistance to FOLDING (0..1): soft links that skip a row along each strand. Plain
    // links stop stretching but not creasing — without these a thin ear accordions on a
    // hard landing. They allow a smooth curve while fighting a sharp fold.
    public float Bend { get; set; } = 0.3f;
}

// The cloth, headless: a grid of Verlet points in world px. Row 0 is pinned to the
// anchor each step; the rest keep their own momentum (inertia — a run streams the hem
// back, a stop swings it forward), are held to their neighbours' rest distances, and
// are pulled toward the rest shape with a strength that eases down the sheet.
public sealed class CurtainSim
{
    public const float Step = 1f / 60f;
    private const int MaxSteps = 4;            // after a hitch, don't spiral
    private const float TeleportDistance = 80f;

    public readonly int Columns, Rows;
    public readonly Vector2[] Points, Previous;
    private readonly Vector2[] _rest;          // offsets from the anchor, facing right
    private readonly CurtainDefinition _def;
    // Optional terrain test (world px → solid?). A point that sinks into a solid tile is
    // lifted to that tile's top and stops — the hem settles on the floor instead of
    // passing through it when the body crouches or lands.
    public Func<Vector2, bool> IsSolid;
    // Draw-space px per game px: 1 in the game; an editor that zooms the rig sets its zoom
    // so the cloth keeps its proportions and its feel (gravity scales with it).
    public float Scale = 1f;

    private float _accum;
    private Vector2 _anchor;
    private int _facing;
    private bool _started;

    public CurtainSim(CurtainDefinition def, Vector2[] rest, int columns, int rows)
    {
        _def = def; _rest = rest; Columns = columns; Rows = rows;
        Points = new Vector2[rest.Length];
        Previous = new Vector2[rest.Length];
    }

    // Radians the rest shape is turned by (FollowBone), in world space after mirroring.
    public float Rotation;

    public Vector2 RestWorld(int i)
    {
        var o = new Vector2(_rest[i].X * _facing, _rest[i].Y) * Scale;
        if (Rotation != 0f)
        {
            float c = MathF.Cos(Rotation), s = MathF.Sin(Rotation);
            o = new Vector2(o.X * c - o.Y * s, o.X * s + o.Y * c);
        }
        return _anchor + o;
    }

    // The pinned row: the top one, or the bottom one for PinBottom.
    public bool IsPinned(int i) => _def.PinBottom ? i >= (Rows - 1) * Columns : i < Columns;

    // Advance to this frame's anchor. `facing` ±1 mirrors the rest shape.
    public void Update(Vector2 anchor, int facing, float dt)
    {
        facing = facing < 0 ? -1 : 1;
        if (!_started || Vector2.Distance(anchor, _anchor) > TeleportDistance * Scale)
        {
            Reset(anchor, facing);
            return;
        }
        if (facing != _facing)
        {
            // Turning mirrors the sheet about the anchor: it stays BEHIND the body
            // instead of swinging through it — and keeps its motion, mirrored.
            for (int i = 0; i < Points.Length; i++)
            {
                Points[i].X = 2f * anchor.X - Points[i].X;
                Previous[i].X = 2f * anchor.X - Previous[i].X;
            }
            _facing = facing;
        }
        _accum = Math.Min(_accum + Math.Max(dt, 0f), MaxSteps * Step);
        // The anchor is interpolated across the substeps of one frame, so a fast move
        // reads as motion rather than a jump the cloth has to absorb in one step.
        Vector2 from = _anchor;
        int steps = (int)(_accum / Step);
        for (int k = 1; k <= steps; k++)
        {
            _anchor = Vector2.Lerp(from, anchor, k / (float)steps);
            Simulate();
        }
        _accum -= steps * Step;
        _anchor = anchor;
    }

    public void Reset(Vector2 anchor, int facing)
    {
        _anchor = anchor; _facing = facing < 0 ? -1 : 1; _started = true; _accum = 0f;
        for (int i = 0; i < Points.Length; i++) Points[i] = Previous[i] = RestWorld(i);
    }

    private void Simulate()
    {
        float keep = 1f - Math.Clamp(_def.Damping, 0f, 1f);
        var g = new Vector2(0f, _def.Gravity * Scale * Step * Step);
        for (int i = 0; i < Points.Length; i++)
        {
            if (IsPinned(i)) { Previous[i] = Points[i] = RestWorld(i); continue; }
            var p = Points[i];
            Points[i] = p + (p - Previous[i]) * keep + g;
            Previous[i] = p;
        }
        for (int it = 0; it < Math.Max(1, _def.Iterations); it++)
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                {
                    int i = r * Columns + c;
                    // Strands down, links across — no diagonals: with the whole top edge
                    // pinned, shear links would make the sheet a rigid frame that cannot
                    // swing. Without them it shears like cloth seen edge-on.
                    if (c + 1 < Columns) Link(i, i + 1, 1f);
                    if (r + 1 < Rows) Link(i, i + Columns, 1f);
                    if (r + 2 < Rows) Link(i, i + 2 * Columns, _def.Bend);
                }
        }
        if (IsSolid != null)
            for (int i = 0; i < Points.Length; i++)
            {
                if (IsPinned(i) || !IsSolid(Points[i])) continue;
                const float ts = Chunk.TileSize;
                float top = MathF.Floor(Points[i].Y / ts) * ts - 0.05f;
                if (IsSolid(new Vector2(Points[i].X, top - ts * 0.5f))) continue;   // buried deeper: leave it
                Points[i].Y = top;
                Previous[i] = Points[i];   // contact kills its momentum (no bounce)
            }
        for (int r = 0; r < Rows; r++)
        {
            int fromPin = _def.PinBottom ? Rows - 1 - r : r;   // rows away from the pinned edge
            if (fromPin == 0) continue;
            float t = (fromPin - 1) / (float)Math.Max(1, Rows - 2);
            float s = MathHelper.Lerp(_def.Stiffness, _def.StiffnessTip, t);
            for (int c = 0; c < Columns; c++)
            {
                int i = r * Columns + c;
                Points[i] += (RestWorld(i) - Points[i]) * s;
            }
        }
    }

    // Pull two points `k` (0..1) of the way back to their rest separation; a pinned
    // point doesn't move.
    private void Link(int a, int b, float k)
    {
        if (k <= 0f) return;
        float rest = Vector2.Distance(_rest[a], _rest[b]) * Scale;
        var d = Points[b] - Points[a];
        float len = d.Length();
        if (len < 1e-5f) return;
        var fix = d * ((len - rest) / len * Math.Min(k, 1f));
        bool pa = IsPinned(a), pb = IsPinned(b);
        if (pa && pb) return;
        if (pa) Points[b] -= fix;
        else if (pb) Points[a] += fix;
        else { Points[a] += fix * 0.5f; Points[b] -= fix * 0.5f; }
    }
}

// A loaded Curtain: its definition, texture, rest grid (offsets from the anchor in world
// px) and the UVs of that grid.
public sealed class CurtainAsset
{
    public CurtainDefinition Def;
    public Texture2D Texture;
    public Vector2[] Rest, Uvs;
    public short[] Indices;

    // The grid over the opaque bounds of an RGBA image (premultiplied or not).
    public static (Vector2[] rest, Vector2[] uvs, short[] indices) BuildGrid(
        CurtainDefinition def, int width, int height, Func<int, int, byte> alpha)
    {
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (alpha(x, y) > 8) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < x0) throw new InvalidDataException("Image is fully transparent.");
        int cols = Math.Max(2, def.Columns), rows = Math.Max(2, def.Rows);
        float ax = def.AnchorX ?? (x0 + x1) * 0.5f, ay = def.AnchorY ?? y0;
        var rest = new Vector2[cols * rows];
        var uvs = new Vector2[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                float px = MathHelper.Lerp(x0, x1 + 1, c / (float)(cols - 1));
                float py = MathHelper.Lerp(y0, y1 + 1, r / (float)(rows - 1));
                rest[r * cols + c] = new Vector2(px - ax, py - ay) * def.PixelSize;
                uvs[r * cols + c] = new Vector2(px / width, py / height);
            }
        var idx = new List<short>();
        for (int r = 0; r + 1 < rows; r++)
            for (int c = 0; c + 1 < cols; c++)
            {
                short i = (short)(r * cols + c);
                idx.AddRange(new[] { i, (short)(i + 1), (short)(i + cols), (short)(i + 1), (short)(i + cols + 1), (short)(i + cols) });
            }
        return (rest, uvs, idx.ToArray());
    }
}

// The character-scoped elements of every skinned character: resolves each Effect once,
// keeps per-character physics state, draws behind or in front of the skin.
public sealed class CharacterElements : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly string _directory;
    private readonly BasicEffect _effect;
    private readonly Dictionary<string, CurtainAsset> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<(object, AnimAttachment), CurtainSim> _sims = new();
    private VertexPositionColorTexture[] _verts = new VertexPositionColorTexture[64];

    public CharacterElements(GraphicsDevice device, string directory = "Assets/AnimationEffects")
    {
        _device = device;
        _directory = directory;
        _effect = new BasicEffect(device) { VertexColorEnabled = true, TextureEnabled = true,
            LightingEnabled = false, View = Matrix.Identity };
    }

    // Advance every element of `owner` to this pose. Once per rendered frame, before Draw.
    public void Update(object owner, SpriteSkin skin, SkeletonPose pose,
                       in Affine2 root, float dt, Func<Vector2, bool> isSolid = null)
    {
        var elements = skin?.Elements;
        if (elements == null || elements.Count == 0) return;
        var world = pose.ComputeWorld(root);
        int facing = root.M11 * root.M22 - root.M12 * root.M21 < 0 ? -1 : 1;
        foreach (var a in elements)
        {
            var asset = Load(a?.Effect);
            if (asset == null || !EndpointResolver.TryResolvePoint(pose.Skeleton, null, a.Point, out var rp)) continue;
            var anchor = EndpointResolver.World(world, root, pose.Skeleton, rp);
            if (!_sims.TryGetValue((owner, a), out var sim))
                _sims[(owner, a)] = sim = new CurtainSim(asset.Def, asset.Rest,
                    Math.Max(2, asset.Def.Columns), Math.Max(2, asset.Def.Rows));
            sim.IsSolid = isSolid;
            if (asset.Def.FollowBone)
            {
                // The bone's turn since the bind pose the art was drawn in, measured in
                // world space (so a facing flip mirrors it along with everything else).
                var now  = world[rp.Bone].TransformVector(Vector2.UnitX);
                float ba = skin.BindAngle(rp.Bone);
                var bind = root.TransformVector(new Vector2(MathF.Cos(ba), MathF.Sin(ba)));
                sim.Rotation = MathHelper.WrapAngle(MathF.Atan2(now.Y, now.X) - MathF.Atan2(bind.Y, bind.X));
            }
            // The game draws rigs at Game1.SkeletonScale px per rig unit; anything else
            // (an editor's zoom) scales the cloth to match.
            sim.Scale = MathF.Sqrt(MathF.Abs(root.M11 * root.M22 - root.M12 * root.M21)) / Game1.SkeletonScale;
            sim.Update(anchor, facing, dt);
        }
    }

    // Call outside SpriteBatch: once with front = false before the skin, once with
    // front = true after it.
    public void Draw(Matrix camera, object owner, IReadOnlyList<AnimAttachment> elements, bool front)
    {
        if (elements == null || elements.Count == 0) return;
        bool prepared = false;
        foreach (var a in elements)
        {
            var asset = Load(a?.Effect);
            if (asset == null || asset.Def.Front != front || !_sims.TryGetValue((owner, a), out var sim)) continue;
            if (!prepared) { Prepare(camera); prepared = true; }
            if (_verts.Length < sim.Points.Length) _verts = new VertexPositionColorTexture[sim.Points.Length];
            for (int i = 0; i < sim.Points.Length; i++)
                _verts[i] = new VertexPositionColorTexture(new Vector3(sim.Points[i], 0), Color.White, asset.Uvs[i]);
            _effect.Texture = asset.Texture;
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, _verts, 0, sim.Points.Length,
                                                  asset.Indices, 0, asset.Indices.Length / 3);
            }
        }
    }

    // Forget physics state (a stage reload, a replay scrub): elements re-seed at rest.
    public void Clear() => _sims.Clear();

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

    private CurtainAsset Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_assets.TryGetValue(name, out var cached)) return cached;
        Texture2D texture = null;
        try
        {
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains(".."))
                throw new InvalidDataException("Effect must be a catalog name.");
            CurtainDefinition def;
            using (var s = TitleContent.TryOpenRead(Path.Combine(_directory, name + ".json")))
            {
                if (s == null) throw new FileNotFoundException("Element definition missing.");
                def = JsonSerializer.Deserialize<CurtainDefinition>(s);
            }
            if (!string.Equals(def?.Type, "Curtain", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Type '{def?.Type}' can't be worn always-on yet (only Curtain).");
            using var png = TitleContent.TryOpenRead(Path.Combine(_directory, def.Image));
            if (png == null) throw new FileNotFoundException(def.Image);
            texture = Texture2D.FromStream(_device, png);
            var px = new Color[texture.Width * texture.Height];
            texture.GetData(px);
            int w = texture.Width;
            var (rest, uvs, indices) = CurtainAsset.BuildGrid(def, w, texture.Height, (x, y) => px[y * w + x].A);
            for (int i = 0; i < px.Length; i++) px[i] = Color.FromNonPremultiplied(px[i].R, px[i].G, px[i].B, px[i].A);
            texture.SetData(px);
            return _assets[name] = new CurtainAsset { Def = def, Texture = texture, Rest = rest, Uvs = uvs, Indices = indices };
        }
        catch (Exception e)
        {
            texture?.Dispose();
            Console.WriteLine($"Element '{name}': {e.Message}");
            return _assets[name] = null;
        }
    }

    public void Dispose()
    {
        foreach (var a in _assets.Values) a?.Texture.Dispose();
        _effect.Dispose();
    }
}
