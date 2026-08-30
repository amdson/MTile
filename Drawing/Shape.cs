using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// A Sprite's transform passed into Shape.Draw. Local-space coordinates inside
// the Pose are transformed by (Position, Rotation, Scale) to world space.
public readonly struct SpriteTransform
{
    public readonly Vector2 Position;
    public readonly float   Rotation;
    public readonly float   Scale;
    public SpriteTransform(Vector2 p, float r, float s) { Position = p; Rotation = r; Scale = s; }
}

// One drawing primitive in local sprite-space. Subclasses are tiny data carriers —
// no per-frame allocation once a Pose is built up.
public abstract class Shape
{
    // `flash` lerps the final colour toward white (0 = untouched). It rides alongside
    // `tint` rather than folding into it because tint MULTIPLIES: it can darken a shape
    // but never brighten one, so a hit flash can't be expressed as a tint.
    public abstract void Draw(DrawContext ctx, in SpriteTransform t, Color tint, float flash = 0f);

    protected static Vector2 ToWorld(Vector2 local, in SpriteTransform t)
    {
        float c = MathF.Cos(t.Rotation), s = MathF.Sin(t.Rotation);
        var sc = local * t.Scale;
        return t.Position + new Vector2(sc.X * c - sc.Y * s, sc.X * s + sc.Y * c);
    }

    protected static Color Multiply(Color a, Color b)
        => new(a.R * b.R / 255, a.G * b.G / 255, a.B * b.B / 255, a.A * b.A / 255);

    // Tint first (multiply), then whiten — the whole point of the flash is that it
    // survives whatever the tint did.
    protected static Color Shade(Color c, Color tint, float flash)
        => HitFlashTracker.Whiten(Multiply(c, tint), flash);
}

public sealed class LineShape : Shape
{
    public Vector2 A, B;
    public Color   Color = Color.White;
    public float   Thickness = 1f;
    public override void Draw(DrawContext ctx, in SpriteTransform t, Color tint, float flash = 0f)
        => ctx.Line(ToWorld(A, t), ToWorld(B, t), Shade(Color, tint, flash), Thickness * t.Scale);
}

public sealed class RingShape : Shape
{
    public Vector2 Center;
    public float   Radius;
    public Color   Color = Color.White;
    public int     Segments = 16;
    public float   Thickness = 1f;
    public override void Draw(DrawContext ctx, in SpriteTransform t, Color tint, float flash = 0f)
        => ctx.Ring(ToWorld(Center, t), Radius * t.Scale, Shade(Color, tint, flash), Segments, Thickness * t.Scale);
}

public sealed class DiscShape : Shape
{
    public Vector2 Center;
    public float   Radius;
    public Color   Color = Color.White;
    public override void Draw(DrawContext ctx, in SpriteTransform t, Color tint, float flash = 0f)
        => ctx.Disc(ToWorld(Center, t), Radius * t.Scale, Shade(Color, tint, flash));
}

// Filled rotated rect. LocalRotation stacks with the sprite's own rotation.
public sealed class BoxShape : Shape
{
    public Vector2 Center;
    public Vector2 Size;
    public float   LocalRotation;
    public Color   Color = Color.White;
    public override void Draw(DrawContext ctx, in SpriteTransform t, Color tint, float flash = 0f)
        => ctx.RotatedRect(ToWorld(Center, t), Size * t.Scale, LocalRotation + t.Rotation, Shade(Color, tint, flash));
}

// A single "frame" of art: an ordered list of Shape primitives in local space.
// Built via the fluent Line/Ring/Disc/Box helpers; drawn through Sprite/AnimatedSprite.
public sealed class Pose
{
    public readonly List<Shape> Shapes = new();

    public Pose Add(Shape s) { Shapes.Add(s); return this; }

    public Pose Line(Vector2 a, Vector2 b, Color color, float thickness = 1f)
        => Add(new LineShape { A = a, B = b, Color = color, Thickness = thickness });

    public Pose Ring(Vector2 center, float radius, Color color, int segments = 16, float thickness = 1f)
        => Add(new RingShape { Center = center, Radius = radius, Color = color, Segments = segments, Thickness = thickness });

    public Pose Disc(Vector2 center, float radius, Color color)
        => Add(new DiscShape { Center = center, Radius = radius, Color = color });

    public Pose Box(Vector2 center, Vector2 size, float rotation, Color color)
        => Add(new BoxShape { Center = center, Size = size, LocalRotation = rotation, Color = color });

    public void Draw(DrawContext ctx, in SpriteTransform t, Color tint, float flash = 0f)
    {
        foreach (var s in Shapes) s.Draw(ctx, t, tint, flash);
    }
}
