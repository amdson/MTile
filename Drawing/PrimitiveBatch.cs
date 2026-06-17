using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Vertex-colored triangle/line renderer built on GraphicsDevice.DrawUserPrimitives +
// BasicEffect. This is the portable (DesktopGL + KNI/WebGL) primitive layer that
// SpriteBatch can't do: gradients (per-vertex color), filled polygons, tessellated
// curves and parametric surfaces. No custom shaders, no content pipeline.
//
// Begin/End mirrors SpriteBatch: pass the SAME world->screen camera matrix SpriteBatch
// gets (Camera.GetTransform). BasicEffect maps that to clip space via an off-center
// ortho projection built from the current viewport (Y-down, top-left origin — matches
// SpriteBatch). Render-only; never touches the sim.
public sealed class PrimitiveBatch
{
    private readonly GraphicsDevice _gd;
    private readonly BasicEffect _effect;
    private VertexPositionColor[] _verts;
    private int _count;
    private PrimitiveType _type;
    private bool _active;

    public PrimitiveBatch(GraphicsDevice gd, int capacity = 4096)
    {
        _gd = gd;
        _effect = new BasicEffect(gd)
        {
            VertexColorEnabled = true,
            TextureEnabled     = false,
            LightingEnabled    = false,
            World              = Matrix.Identity,
            View               = Matrix.Identity,
        };
        _verts = new VertexPositionColor[Math.Max(capacity, 6)];
    }

    // `transform` is the world->screen-pixel matrix (Camera.GetTransform). Projection
    // is rebuilt each Begin from the live viewport so window resizes Just Work.
    public void Begin(Matrix transform, PrimitiveType type = PrimitiveType.TriangleList,
                      BlendState blend = null)
    {
        if (_active) throw new InvalidOperationException("PrimitiveBatch.Begin called twice without End.");
        var vp = _gd.Viewport;
        _effect.World      = transform;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1);

        _gd.BlendState        = blend ?? BlendState.AlphaBlend;
        _gd.DepthStencilState = DepthStencilState.None;
        _gd.RasterizerState   = RasterizerState.CullNone;

        _type   = type;
        _count  = 0;
        _active = true;
    }

    public void Vertex(Vector2 pos, Color color)
    {
        if (!_active) throw new InvalidOperationException("PrimitiveBatch.Vertex outside Begin/End.");
        // Auto-flush on a primitive boundary so a triangle/line never straddles a flush.
        int stride = _type == PrimitiveType.TriangleList ? 3 : 2;
        if (_count + 1 > _verts.Length && _count % stride == 0)
            Flush();
        if (_count >= _verts.Length)
            Array.Resize(ref _verts, _verts.Length * 2);
        _verts[_count++] = new VertexPositionColor(new Vector3(pos, 0f), color);
    }

    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color ca, Color cb, Color cc)
    {
        Vertex(a, ca); Vertex(b, cb); Vertex(c, cc);
    }

    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color color)
        => Triangle(a, b, c, color, color, color);

    // Two triangles (a,b,c)+(a,c,d). Corners wind consistently; CullNone means winding
    // doesn't matter for visibility anyway.
    public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d,
                     Color ca, Color cb, Color cc, Color cd)
    {
        Triangle(a, b, c, ca, cb, cc);
        Triangle(a, c, d, ca, cc, cd);
    }

    // A ribbon between two equal-length polylines (left[i] <-> right[i]), each vertex
    // carrying its own color. The building block for stroked curves and surface rows.
    public void Strip(ReadOnlySpan<Vector2> left, ReadOnlySpan<Vector2> right,
                      ReadOnlySpan<Color> leftColor, ReadOnlySpan<Color> rightColor)
    {
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i + 1 < n; i++)
            Quad(left[i], right[i], right[i + 1], left[i + 1],
                 leftColor[i], rightColor[i], rightColor[i + 1], leftColor[i + 1]);
    }

    private void Flush()
    {
        int stride = _type == PrimitiveType.TriangleList ? 3 : 2;
        int prims  = _count / stride;
        if (prims > 0)
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _gd.DrawUserPrimitives(_type, _verts, 0, prims);
            }
        _count = 0;
    }

    public void End()
    {
        if (!_active) throw new InvalidOperationException("PrimitiveBatch.End without Begin.");
        Flush();
        _active = false;
    }
}
