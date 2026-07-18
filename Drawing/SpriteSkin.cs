using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Draws a bound PNG (SpriteBindings/<name>.json) deformed over the live skeleton pose:
// a coarse triangle mesh over the image, moved per frame by rigid MLS (MlsDeformer)
// from the binding's bind pose to the current pose. The solve runs in rig-local space
// (canonical facing); the caller's root Affine2 then places, scales, and mirrors the
// result — so facing flips never reach the MLS solve (rigid MLS can't represent a
// reflection). Built on DrawUserIndexedPrimitives + BasicEffect, same portable surface
// as PrimitiveBatch (DesktopGL + KNI). Render-only; never touches the sim.
//
// LAYERS: when the binding declares a color-coded Mask + Layers, the sprite splits into
// independent regions drawn back-to-front. Each layer gets its own texture (non-layer
// pixels zeroed), its own mesh (so an arm and the torso never share triangles), and its
// own bone-filtered handle set (so only the arm's bones ever move arm pixels). Without
// layers the whole image is one region deformed by every bone.
public sealed class SpriteSkin : IDisposable
{
    // One independently-deformed region of the sprite.
    private sealed class Layer
    {
        public string    Name;
        public Texture2D Texture;
        public bool      OwnsTexture;
        public VertexPositionColorTexture[] Verts;
        public short[]           Indices;
        public Vector2[]         RigLocal;      // deformed vertex positions, rig-local
        public SkinHandleLayout  Handles;       // bone-filtered
        public Vector2[]         PosedHandles;
        public MlsDeformer       Mls;
    }

    private readonly GraphicsDevice _gd;
    private readonly BasicEffect    _effect;
    private readonly Texture2D      _baseTexture;
    private readonly bool           _ownsBaseTexture;
    private readonly Skeleton       _skeleton;
    private readonly List<Layer>    _layers = new();
    private readonly Affine2[]      _world;    // identity-root forward pass (private —
                                               // never clobbers the pose's cached world)

    public int VertexCount   { get; private set; }
    public int TriangleCount { get; private set; }

    // Load a binding + its PNG (and mask, if layered). Null (and silent) when the .json
    // is absent; loud on a present-but-broken binding. `skel` must be the rig the
    // binding was authored on.
    public static SpriteSkin TryLoad(GraphicsDevice gd, string bindingPath, Skeleton skel)
    {
        SpriteBindingDocument doc;
        try
        {
            doc = SpriteBindingDocument.Load(bindingPath);
            if (doc == null) return null;
        }
        catch { return null; }   // no filesystem (WASM) → no skin

        try
        {
            using var fs = File.OpenRead(doc.ImagePath);
            var tex = Texture2D.FromStream(gd, fs);
            return new SpriteSkin(gd, doc, skel, tex, ownsTexture: true);
        }
        catch (Exception e)
        {
            Console.WriteLine($"SpriteSkin: failed to load '{bindingPath}': {e.Message}");
            return null;
        }
    }

    // Bakes everything up front: layer assignment from the mask, per-layer meshes from
    // the (masked) image alpha, MLS weights from the bind pose. `premultiply` converts
    // the texture's alpha in place (SpriteBatch/AlphaBlend convention — raw
    // Texture2D.FromStream data is straight-alpha); pass false when the caller already
    // premultiplied it (e.g. the bind editor rebaking a preview).
    public SpriteSkin(GraphicsDevice gd, SpriteBindingDocument doc, Skeleton skel,
                      Texture2D texture, bool ownsTexture, bool premultiply = true)
    {
        _gd = gd; _baseTexture = texture; _ownsBaseTexture = ownsTexture; _skeleton = skel;
        if (!string.Equals(doc.Skeleton, skel.Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Binding is for rig '{doc.Skeleton}', got '{skel.Name}'.");

        int w = texture.Width, h = texture.Height;
        var pixels = new Color[w * h];
        texture.GetData(pixels);
        if (premultiply)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.FromNonPremultiplied(pixels[i].R, pixels[i].G, pixels[i].B, pixels[i].A);
            texture.SetData(pixels);
        }

        // --- shared bind-pose world (bind handles are sampled per layer from this) ----
        _world = new Affine2[skel.Count];
        var bindPose = doc.CreateBindPose(skel);
        ComputeWorldLocal(bindPose);

        int  step   = Math.Max(2, doc.MeshStep);
        byte thresh = (byte)Math.Clamp(doc.AlphaThreshold, 1, 255);
        bool layered = doc.Layers != null && doc.Layers.Count > 0 && doc.Mask != null;

        if (!layered)
        {
            var all = new SpriteSkinLayer { Name = "all" };   // no Bones → every bone
            BuildLayer(doc, all, texture, ownsLayerTexture: false, pixels, w, h, step, thresh);
        }
        else
        {
            int[] assign = AssignPixels(gd, doc, pixels, w, h);
            var masked = new Color[w * h];
            for (int li = 0; li < doc.Layers.Count; li++)
            {
                Array.Clear(masked, 0, masked.Length);
                int count = 0;
                for (int i = 0; i < pixels.Length; i++)
                    if (assign[i] == li) { masked[i] = pixels[i]; count++; }
                if (count == 0)
                {
                    Console.WriteLine($"SpriteSkin: layer '{doc.Layers[li].Name}' has no pixels " +
                                      "(mask color unused?) — skipped.");
                    continue;
                }
                var layerTex = new Texture2D(gd, w, h);
                layerTex.SetData(masked);
                BuildLayer(doc, doc.Layers[li], layerTex, ownsLayerTexture: true, masked, w, h, step, thresh);
            }
            if (_layers.Count == 0)
                throw new InvalidOperationException("SpriteSkin: no layer produced any mesh.");
        }

        _effect = new BasicEffect(gd)
        {
            VertexColorEnabled = true,
            TextureEnabled     = true,
            LightingEnabled    = false,
            World              = Matrix.Identity,
            View               = Matrix.Identity,
        };
    }

    // Per-pixel layer index from the color-coded mask: each opaque sprite pixel goes to
    // the layer whose Color is nearest (RGB) at that mask pixel; mask-uncovered pixels
    // go to the catch-all (a layer with no Color), else to layer 0.
    private static int[] AssignPixels(GraphicsDevice gd, SpriteBindingDocument doc,
                                      Color[] sprite, int w, int h)
    {
        Color[] mask;
        using (var fs = File.OpenRead(doc.MaskPath))
        using (var mtex = Texture2D.FromStream(gd, fs))
        {
            if (mtex.Width != w || mtex.Height != h)
                throw new InvalidOperationException(
                    $"Mask '{doc.Mask}' is {mtex.Width}x{mtex.Height}, image is {w}x{h} — must match.");
            mask = new Color[w * h];
            mtex.GetData(mask);
        }

        var colors = new (int r, int g, int b, bool has)[doc.Layers.Count];
        int catchAll = -1;
        for (int li = 0; li < doc.Layers.Count; li++)
        {
            string c = doc.Layers[li].Color;
            if (string.IsNullOrEmpty(c)) { catchAll = li; continue; }
            var (r, g, b) = ParseColor(c);
            colors[li] = (r, g, b, true);
        }
        if (catchAll < 0) catchAll = 0;

        // A layer color claims a pixel only within MaskTolerance — mask pixels matching
        // nothing (unpainted areas, or the artwork itself when the mask was painted on a
        // copy of the sprite) fall to the catch-all.
        int tol = Math.Clamp(doc.MaskTolerance, 1, 255);
        int tolD = 3 * tol * tol;
        int unassigned = 0;
        var assign = new int[w * h];
        for (int i = 0; i < assign.Length; i++)
        {
            assign[i] = -1;
            if (sprite[i].A == 0) continue;
            var m = mask[i];
            if (m.A < 128) { assign[i] = catchAll; unassigned++; continue; }
            int best = catchAll, bestD = tolD;
            for (int li = 0; li < colors.Length; li++)
            {
                if (!colors[li].has) continue;
                int dr = m.R - colors[li].r, dg = m.G - colors[li].g, db = m.B - colors[li].b;
                int d = dr * dr + dg * dg + db * db;
                if (d <= bestD) { bestD = d; best = li; }
            }
            if (best == catchAll && bestD >= tolD) unassigned++;
            assign[i] = best;
        }
        if (unassigned > 0 && doc.Layers[catchAll].Color != null)
            Console.WriteLine($"SpriteSkin: {unassigned} opaque pixels uncovered by the mask " +
                              $"fell back to layer '{doc.Layers[catchAll].Name}'.");
        return assign;
    }

    private static (int r, int g, int b) ParseColor(string s)
    {
        s = s.TrimStart('#');
        if (s.Length != 6) throw new FormatException($"Layer color '{s}' — expected #RRGGBB.");
        return (Convert.ToInt32(s.Substring(0, 2), 16),
                Convert.ToInt32(s.Substring(2, 2), 16),
                Convert.ToInt32(s.Substring(4, 2), 16));
    }

    // Mesh one layer over its (masked) pixels and bake its MLS deformer against the
    // layer's bone subset. Requires the bind-pose _world to be resolved already.
    private void BuildLayer(SpriteBindingDocument doc, SpriteSkinLayer spec,
                            Texture2D layerTex, bool ownsLayerTexture,
                            Color[] px, int w, int h, int step, byte thresh)
    {
        int cols = (w + step - 1) / step, rows = (h + step - 1) / step;
        var cornerIndex = new int[(cols + 1) * (rows + 1)];
        Array.Fill(cornerIndex, -1);
        var positions = new List<Vector2>();   // image px, corners deduped within the layer
        var indices   = new List<short>();

        int Corner(int cx, int cy)
        {
            int key = cy * (cols + 1) + cx;
            if (cornerIndex[key] >= 0) return cornerIndex[key];
            positions.Add(new Vector2(Math.Min(cx * step, w), Math.Min(cy * step, h)));
            cornerIndex[key] = positions.Count - 1;
            return cornerIndex[key];
        }

        for (int cy = 0; cy < rows; cy++)
        for (int cx = 0; cx < cols; cx++)
        {
            int x1 = Math.Min((cx + 1) * step, w), y1 = Math.Min((cy + 1) * step, h);
            bool keep = false;
            for (int y = cy * step; y < y1 && !keep; y++)
            for (int x = cx * step; x < x1; x++)
                if (px[y * w + x].A >= thresh) { keep = true; break; }
            if (!keep) continue;

            int a = Corner(cx, cy),         b = Corner(cx + 1, cy);
            int c = Corner(cx + 1, cy + 1), d = Corner(cx, cy + 1);
            if (positions.Count > short.MaxValue)
                throw new InvalidOperationException(
                    $"SpriteSkin layer '{spec.Name}' mesh too dense ({positions.Count} verts) — raise MeshStep.");
            indices.Add((short)a); indices.Add((short)b); indices.Add((short)c);
            indices.Add((short)a); indices.Add((short)c); indices.Add((short)d);
        }
        if (positions.Count == 0)
            throw new InvalidOperationException(
                $"SpriteSkin layer '{spec.Name}': no pixels over AlphaThreshold.");

        var layer = new Layer
        {
            Name = spec.Name, Texture = layerTex, OwnsTexture = ownsLayerTexture,
            Indices = indices.ToArray(),
            Verts = new VertexPositionColorTexture[positions.Count],
            RigLocal = new Vector2[positions.Count],
        };
        var restRig = new Vector2[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            layer.Verts[i].Color = Color.White;
            layer.Verts[i].TextureCoordinate = new Vector2(positions[i].X / w, positions[i].Y / h);
            restRig[i] = doc.ImageToRig(positions[i]);
        }

        layer.Handles = SkinHandleLayout.Create(_skeleton, step: doc.HandleStep, includeBone: spec.IncludesBone);
        if (layer.Handles.Count < 2)
            throw new InvalidOperationException(
                $"SpriteSkin layer '{spec.Name}': bone patterns match too few handles " +
                "(need ≥ 2 — check the Bones list against the rig).");
        var restHandles = new Vector2[layer.Handles.Count];
        layer.Handles.Sample(_world, restHandles);   // _world = bind pose here
        layer.Mls = MlsDeformer.Bake(restRig, restHandles,
                                     Math.Max(3, doc.MaxHandles), MathF.Max(0.5f, doc.WeightAlpha));
        layer.PosedHandles = new Vector2[layer.Handles.Count];

        _layers.Add(layer);
        VertexCount   += positions.Count;
        TriangleCount += layer.Indices.Length / 3;
    }

    // Identity-root forward pass into the private world buffer. Same math as
    // SkeletonPose.ComputeWorld, but does not touch the pose's cached world (the
    // animator and glow systems may still be reading theirs, resolved under the
    // real root).
    private void ComputeWorldLocal(SkeletonPose pose)
    {
        for (int i = 0; i < pose.Count; i++)
        {
            var local  = pose.Local[i].ToAffine();
            int parent = _skeleton.Bones[i].Parent;
            _world[i]  = parent < 0 ? local : _world[parent] * local;
        }
    }

    // Deform to `pose` and draw all layers back-to-front. `root` is the same whole-rig
    // placement the skeleton renderer gets (position, SkeletonScale, facing flip);
    // `cameraTransform` the same world→screen matrix SpriteBatch gets. Must be called
    // OUTSIDE SpriteBatch Begin/End (it issues its own device draws, PrimitiveBatch-
    // style). fill=false deforms without rendering — for a wireframe-only overlay.
    public void Draw(Matrix cameraTransform, SkeletonPose pose, in Affine2 root, bool fill = true)
    {
        if (pose.Count != _skeleton.Count)
            throw new ArgumentException("Pose rig doesn't match the binding's rig.");

        ComputeWorldLocal(pose);
        foreach (var layer in _layers)
        {
            layer.Handles.Sample(_world, layer.PosedHandles);
            layer.Mls.Deform(layer.PosedHandles, layer.RigLocal);
            for (int i = 0; i < layer.Verts.Length; i++)
            {
                Vector2 p = root.TransformPoint(layer.RigLocal[i]);
                layer.Verts[i].Position = new Vector3(p, 0f);
            }
        }
        if (!fill) return;

        var vp = _gd.Viewport;
        _effect.World      = cameraTransform;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1);

        _gd.BlendState        = BlendState.AlphaBlend;
        _gd.DepthStencilState = DepthStencilState.None;
        _gd.RasterizerState   = RasterizerState.CullNone;
        _gd.SamplerStates[0]  = SamplerState.LinearClamp;

        foreach (var layer in _layers)
        {
            _effect.Texture = layer.Texture;
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList,
                    layer.Verts, 0, layer.Verts.Length, layer.Indices, 0, layer.Indices.Length / 3);
            }
        }
    }

    // Overlay every layer's triangle edges as SpriteBatch lines (editor debug view —
    // shows exactly which triangles twist, and that layers are truly disconnected).
    // Positions come from the LAST Draw call this frame, so call Draw first (fill:false
    // for wireframe-only). Interior edges draw twice; irrelevant at editor scale.
    public void DrawWireframe(DrawContext ctx, Color color, float thickness = 1f)
    {
        foreach (var layer in _layers)
            for (int t = 0; t < layer.Indices.Length; t += 3)
            {
                var pa = layer.Verts[layer.Indices[t]].Position;
                var pb = layer.Verts[layer.Indices[t + 1]].Position;
                var pc = layer.Verts[layer.Indices[t + 2]].Position;
                Vector2 a = new(pa.X, pa.Y), b = new(pb.X, pb.Y), c = new(pc.X, pc.Y);
                ctx.Line(a, b, color, thickness);
                ctx.Line(b, c, color, thickness);
                ctx.Line(c, a, color, thickness);
            }
    }

    public void Dispose()
    {
        _effect.Dispose();
        foreach (var layer in _layers)
            if (layer.OwnsTexture) layer.Texture.Dispose();
        if (_ownsBaseTexture) _baseTexture.Dispose();
    }
}
