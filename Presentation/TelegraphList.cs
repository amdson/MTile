using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The world-space overlay shapes a sim object wants shown this frame — player action
// telegraphs (slash rings, charge dots), enemy wind-up tells, the block-grab tether
// tint. Sim-side code (ActionState, EnemyState, ITelegraphSource entities) APPENDS
// shapes here; Drawing/TelegraphRenderer draws them. That is the whole contract:
//
//   * No graphics types. This file compiles without Microsoft.Xna.Framework.Graphics,
//     so the action/enemy FSMs never see a SpriteBatch — they declare visuals, the
//     renderer decides how (and in what pass) to draw them. Same split Unity's
//     components / Godot's CanvasItem draw calls impose.
//   * Render-only. Game1 clears and refills the list once per rendered frame; nothing
//     in Simulation.Step reads it, so its contents can never feed back into the sim.
//   * Headless-testable: a test can drive an action's Telegraph() and assert on the
//     shapes without a GraphicsDevice.
//
// Shapes are pixel-agnostic floats; the renderer snaps axis-aligned boxes to ints the
// way DrawContext.Rect does. Storage is a growable array — grows only when a frame
// emits more shapes than any previous one, so steady state is allocation-free.
public enum TelegraphKind : byte
{
    Line,          // A → B, `Thickness` wide, centred on the segment
    Box,           // axis-aligned filled rect, A = top-left, B = size
    RotatedRect,   // filled rect centred on A, size B, rotated by `Rotation`
    Ring,          // N-gon outline centred on A, radius B.X, `Segments` sides
}

public struct TelegraphShape
{
    public TelegraphKind Kind;
    public Vector2 A;
    public Vector2 B;
    public float   Rotation;
    public float   Thickness;
    public int     Segments;
    public Color   Color;
}

public sealed class TelegraphList
{
    private TelegraphShape[] _shapes;
    private int _count;

    public TelegraphList(int initialCapacity = 256)
    {
        _shapes = new TelegraphShape[Math.Max(initialCapacity, 16)];
    }

    public int Count => _count;
    public ref readonly TelegraphShape this[int i] => ref _shapes[i];

    public void Clear() => _count = 0;

    // ── Primitives ─────────────────────────────────────────────────────────────

    // Segment from `a` to `b`; the thickness straddles the segment.
    public void Line(Vector2 a, Vector2 b, Color color, float thickness = 1f)
    {
        ref var s = ref Next();
        s.Kind = TelegraphKind.Line; s.A = a; s.B = b;
        s.Thickness = thickness; s.Color = color;
    }

    // Segment from `start` along `angle` (radians) for `length`. The old
    // EnemyTelegraph.Line — sight lines, lash arcs.
    public void Ray(Vector2 start, float angle, float length, float thickness, Color color)
    {
        var end = start + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * length;
        Line(start, end, color, thickness);
    }

    // Axis-aligned filled rect from its top-left corner. Use this when the geometry is
    // already cell/pixel-addressed (tile outlines, bars hanging off a fixed edge).
    public void Box(float x, float y, float w, float h, Color color)
    {
        ref var s = ref Next();
        s.Kind = TelegraphKind.Box; s.A = new Vector2(x, y); s.B = new Vector2(w, h);
        s.Color = color;
    }

    public void Box(Vector2 topLeft, Vector2 size, Color color)
        => Box(topLeft.X, topLeft.Y, size.X, size.Y, color);

    // Axis-aligned filled rect centred on `center`.
    public void Rect(Vector2 center, Vector2 size, Color color)
        => Box(center.X - size.X * 0.5f, center.Y - size.Y * 0.5f, size.X, size.Y, color);

    // Centred square — the ubiquitous "dot" / "marker" telegraph.
    public void Rect(Vector2 center, float size, Color color)
        => Rect(center, new Vector2(size, size), color);

    // Filled rect centred on `center`, rotated about it.
    public void RotatedRect(Vector2 center, Vector2 size, float rotation, Color color)
    {
        ref var s = ref Next();
        s.Kind = TelegraphKind.RotatedRect; s.A = center; s.B = size;
        s.Rotation = rotation; s.Color = color;
    }

    // N-gon outline. Matches DrawContext.Ring.
    public void Ring(Vector2 center, float radius, Color color, int segments = 16, float thickness = 1f)
    {
        ref var s = ref Next();
        s.Kind = TelegraphKind.Ring; s.A = center; s.B = new Vector2(radius, 0f);
        s.Segments = segments < 3 ? 3 : segments; s.Thickness = thickness; s.Color = color;
    }

    // "Disc" — a rotated square of the same diameter, as DrawContext.Disc.
    public void Disc(Vector2 center, float radius, Color color)
        => RotatedRect(center, new Vector2(radius * 2f, radius * 2f), 0f, color);

    // A slice of a Ring: the outline from `centerAngle - halfAngle` to
    // `centerAngle + halfAngle` (radians, y-down like everything else). Composite, not
    // a new kind — it emits `segments` Lines, the way Disc is really a RotatedRect —
    // so TelegraphRenderer and DrawContext both stay as they are. The caller pays a
    // handful of extra shapes for that; at the cone sizes this draws (a dozen segments
    // over 120°) it is not worth a primitive of its own.
    public void Arc(Vector2 center, float radius, float centerAngle, float halfAngle,
                    Color color, int segments = 12, float thickness = 1f)
    {
        if (segments < 1) segments = 1;
        float start = centerAngle - halfAngle;
        float step  = (halfAngle * 2f) / segments;
        var prev = center + new Vector2(MathF.Cos(start), MathF.Sin(start)) * radius;
        for (int i = 1; i <= segments; i++)
        {
            float a = start + i * step;
            var next = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            Line(prev, next, color, thickness);
            prev = next;
        }
    }

    // ── Storage ────────────────────────────────────────────────────────────────

    private ref TelegraphShape Next()
    {
        if (_count == _shapes.Length)
            Array.Resize(ref _shapes, _shapes.Length * 2);
        ref var s = ref _shapes[_count++];
        s = default;
        return ref s;
    }
}

// An entity that emits overlay shapes beyond its Sprite (enemy telegraphs, the
// block-grab tether tint). Game1 collects these after the players' action telegraphs,
// into the same list, so they draw in the same world-space pass. Reads sim state,
// writes none.
public interface ITelegraphSource
{
    void Telegraph(TelegraphList t);
}
