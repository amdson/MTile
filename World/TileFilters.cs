namespace MTile;

// Predicates that drive TileQueryChain.Where (and its Edge / Corner siblings).
// Carrying ChunkMap as the first argument lets each filter read adjacent cells
// (TopExposed, BodyFacingNeighborEmpty, OpenCorner, …) without callers having to
// thread the map through their chain. Delegate types are explicit so static
// fields in TileFilters / EdgeFilters / CornerFilters bind to them by name.
public delegate bool TilePredicate  (ChunkMap chunks, TileRef   tile);
public delegate bool EdgePredicate  (ChunkMap chunks, EdgeRef   edge);
public delegate bool CornerPredicate(ChunkMap chunks, CornerRef corner);

// Each filter is a named, individually testable rule. The motivating bug
// (ParkourState anchoring on an inset block) was a missing rule — "the tile's
// body-facing horizontal neighbor must be empty" — that lived nowhere in the
// codebase; lifting rules out of inline foreach bodies into named delegates
// makes that mistake harder to repeat.
public static class TileFilters
{
    // Same-cell solidity. Useful when Tiles(…) is widened to include all tiles
    // rather than only solids; today the builder yields solids already so this
    // is mostly belt-and-braces for chains built from non-default sources.
    public static readonly TilePredicate Solid =
        (chunks, t) => TileQuery.IsSolidAt(chunks, t.WorldCenterX, t.WorldCenterY);

    // No solid tile directly above (cardinal neighbor probe at half-tile offset).
    public static readonly TilePredicate TopExposed =
        (chunks, t) => !TileQuery.IsSolidAt(chunks, t.WorldCenterX, t.WorldTop - Chunk.TileSize * 0.5f);

    // No solid tile directly below.
    public static readonly TilePredicate BottomExposed =
        (chunks, t) => !TileQuery.IsSolidAt(chunks, t.WorldCenterX, t.WorldBottom + Chunk.TileSize * 0.5f);

    // No solid tile directly to the left.
    public static readonly TilePredicate LeftExposed =
        (chunks, t) => !TileQuery.IsSolidAt(chunks, t.WorldLeft - Chunk.TileSize * 0.5f, t.WorldCenterY);

    // No solid tile directly to the right.
    public static readonly TilePredicate RightExposed =
        (chunks, t) => !TileQuery.IsSolidAt(chunks, t.WorldRight + Chunk.TileSize * 0.5f, t.WorldCenterY);

    // Tile sits on the body-far side of the body's facing face. For wallDir = +1
    // (body moving right) this keeps tiles whose left edge is at or past the
    // body's right face; for wallDir = -1 it keeps tiles whose right edge is at
    // or before the body's left face. Equivalent to the
    // `if (tile.WorldLeft < bodyFace) continue;` filters the side probes used inline.
    public static TilePredicate OutsideBodyFace(float bodyFace, int wallDir) =>
        wallDir == +1
            ? (TilePredicate)((_, t) => t.WorldLeft  >= bodyFace)
            : (TilePredicate)((_, t) => t.WorldRight <= bodyFace);

    // The body-facing horizontal neighbor at the same row is empty. This is the
    // EDGE-vs-INTERIOR test the corner checkers were missing: without it, every
    // top-exposed tile of a wide platform qualifies, and iteration order picks
    // the wrong one. With it, only the outermost tile of a slab passes.
    public static TilePredicate BodyFacingNeighborEmpty(int wallDir) =>
        (chunks, t) => !TileQuery.IsSolidAt(
            chunks,
            t.WorldCenterX - wallDir * Chunk.TileSize,
            t.WorldCenterY);

    // The tile diagonally above-and-toward-the-body is empty. Rejects inverted
    // notches: a top-exposed tile tucked under a wall extending up-and-out has
    // a solid above-inward neighbor, and isn't a real outer corner.
    public static TilePredicate UpperDiagonalClear(int wallDir) =>
        (chunks, t) => !TileQuery.IsSolidAt(
            chunks,
            t.WorldCenterX - wallDir * Chunk.TileSize,
            t.WorldTop - Chunk.TileSize * 0.5f);

    // Mirror of UpperDiagonalClear for ExposedLowerCornerChecker.
    public static TilePredicate LowerDiagonalClear(int wallDir) =>
        (chunks, t) => !TileQuery.IsSolidAt(
            chunks,
            t.WorldCenterX - wallDir * Chunk.TileSize,
            t.WorldBottom + Chunk.TileSize * 0.5f);

    // Tile's bottom sits below the playerHead probe Y (i.e. the overhang's
    // bottom face is below the head, so the head has clearance under it).
    // Used by ExposedLowerCornerChecker as `if (playerHead >= tile.WorldBottom) continue`.
    public static TilePredicate BottomBelow(float playerHeadY) =>
        (_, t) => playerHeadY < t.WorldBottom;

    // tile.WorldTop ∈ [minY, maxY]. The seed enumerator includes any tile whose
    // Y range OVERLAPS a probe rect — a tile spans a full TileSize so its top
    // may fall outside the rect while the tile itself overlaps. Use this to pin
    // the tile's top edge into an explicit band (e.g. the ParkourState vault
    // precondition: corner top must sit 0.5..1.2 tiles above the standing base).
    public static TilePredicate WorldTopInRange(float minY, float maxY) =>
        (_, t) => t.WorldTop >= minY && t.WorldTop <= maxY;

    // Mirror: tile.WorldBottom ∈ [minY, maxY]. For lower-corner band checks.
    public static TilePredicate WorldBottomInRange(float minY, float maxY) =>
        (_, t) => t.WorldBottom >= minY && t.WorldBottom <= maxY;

}

// Edge filters. Edges enumerate from all four sides of every solid tile in the
// seed region; IsOpen narrows to faces where the neighbor across the edge is
// empty — i.e. an actual exposed wall / floor / ceiling face the body can
// interact with.
public static class EdgeFilters
{
    public static readonly EdgePredicate IsOpen = (chunks, e) =>
    {
        // Sample one half-tile beyond the edge in its outward normal direction.
        // Top edge → up (Y-down: negative Y), Bottom → down, Left → left, Right → right.
        float cx = e.Tile.WorldCenterX;
        float cy = e.Tile.WorldCenterY;
        const float h = Chunk.TileSize * 0.5f;
        (float px, float py) = e.Type switch
        {
            EdgeType.Top    => (cx,     cy - Chunk.TileSize),
            EdgeType.Bottom => (cx,     cy + Chunk.TileSize),
            EdgeType.Left   => (cx - Chunk.TileSize, cy),
            EdgeType.Right  => (cx + Chunk.TileSize, cy),
            _ => (cx, cy)
        };
        // `h` keeps the float compiler-happy and documents intent; the offsets
        // above are already a full tile so the probe hits the neighbor's center.
        _ = h;
        return !TileQuery.IsSolidAt(chunks, px, py);
    };

    public static EdgePredicate Type(EdgeType type) => (_, e) => e.Type == type;
}

// Corner filters. Corners enumerate from all four corners of every solid tile;
// IsOpen narrows to outward (convex) corners — both adjacent cardinal neighbors
// AND the diagonal neighbor are empty, so the corner is a true exposed vertex
// of the solid geometry. That's the precondition for vault / overcrop ramps.
public static class CornerFilters
{
    public static readonly CornerPredicate IsOpen = (chunks, c) =>
    {
        int dx = c.Type switch
        {
            CornerType.TopLeft  or CornerType.BottomLeft  => -1,
            CornerType.TopRight or CornerType.BottomRight => +1,
            _ => 0
        };
        int dy = c.Type switch
        {
            CornerType.TopLeft    or CornerType.TopRight    => -1,
            CornerType.BottomLeft or CornerType.BottomRight => +1,
            _ => 0
        };
        float cx = c.Tile.WorldCenterX;
        float cy = c.Tile.WorldCenterY;
        const int ts = Chunk.TileSize;
        // Both cardinal neighbors + the diagonal must be empty.
        if (TileQuery.IsSolidAt(chunks, cx + dx * ts, cy           )) return false;
        if (TileQuery.IsSolidAt(chunks, cx,           cy + dy * ts)) return false;
        if (TileQuery.IsSolidAt(chunks, cx + dx * ts, cy + dy * ts)) return false;
        return true;
    };

    public static CornerPredicate Type(CornerType type) => (_, c) => c.Type == type;
}
