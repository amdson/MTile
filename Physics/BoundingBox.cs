namespace MTile;

// Axis-aligned float-precision rectangle. Doubles as a polygon's bounding box (see PhysicsBody.Bounds
// / Polygon.GetBoundingBox) and as a "region" type for probe queries — the StripXxx builders return
// another BoundingBox for the slab of space immediately adjacent to one face, with the face itself
// as the slab's inner edge:
//
//                     ┌─────────────┐  ← Top    (smaller y)
//                     │             │
//                Left ┤             ├ Right     CenterX = (Left + Right) / 2
//                     │             │           CenterY = (Top  + Bottom) / 2
//                     └─────────────┘  ← Bottom (larger y)
//                         StripBelow(t)
//                     ┌─────────────┐
//                     │             │
//                     └─────────────┘  ← Top + t
public readonly struct BoundingBox
{
    public readonly float Left;
    public readonly float Top;
    public readonly float Right;
    public readonly float Bottom;

    public BoundingBox(float left, float top, float right, float bottom)
    {
        Left = left; Top = top; Right = right; Bottom = bottom;
    }

    public float Width   => Right - Left;
    public float Height  => Bottom - Top;
    public float CenterX => (Left + Right) * 0.5f;
    public float CenterY => (Top + Bottom) * 0.5f;

    // The face on a side: dir = +1 → Right; dir = -1 → Left.
    public float Side(int dir) => dir == 1 ? Right : Left;

    // Slabs of space immediately outside one face — the face is the slab's inner edge.
    public BoundingBox StripAbove(float thickness) => new(Left,             Top - thickness,    Right,             Top);
    public BoundingBox StripBelow(float thickness) => new(Left,             Bottom,             Right,             Bottom + thickness);
    public BoundingBox StripRight(float thickness) => new(Right,            Top,                Right + thickness, Bottom);
    public BoundingBox StripLeft (float thickness) => new(Left - thickness, Top,                Left,              Bottom);
    public BoundingBox StripBeside(int dir, float thickness)
        => dir == 1 ? StripRight(thickness) : StripLeft(thickness);

    // Pull the top and bottom faces inward by `amount`. Used by side probes that should ignore the
    // body's upper/lower corners (e.g. WallChecker, which doesn't want a "wall" reported from a floor
    // tile that the body's bottom vertex barely grazes).
    public BoundingBox InsetVertical(float amount) => new(Left, Top + amount, Right, Bottom - amount);

    // Same x-extent as this box, but the given vertical range. Useful when a checker has body-relative
    // x bounds (a side strip) but a specific y window (e.g. "from the body's center down to its feet").
    public BoundingBox WithVerticalRange(float top, float bottom) => new(Left, top, Right, bottom);

    public override string ToString() => $"[{Left:F1},{Top:F1} → {Right:F1},{Bottom:F1}]";
}
