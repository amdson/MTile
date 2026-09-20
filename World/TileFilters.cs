namespace MTile;

// Predicates that drive TileQueryChain.Where (and its Edge / Corner siblings).
// Carrying ChunkMap as the first argument lets each filter read adjacent cells
// (TopExposed, BodyFacingNeighborEmpty, OpenCorner, …) without callers having to
// thread the map through their chain. Delegate types are explicit so static
// fields in TileFilters / EdgeFilters / CornerFilters bind to them by name.
public delegate bool TilePredicate  (ChunkMap chunks, TileRef   tile);

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

    // Nothing solid stands between the body's facing face and this tile, across
    // the body's own height. BodyFacingNeighborEmpty is the same idea at ONE
    // tile's range: it establishes that a tile is the outer edge of its own
    // slab, but says nothing about a SEPARATE slab standing in front of that
    // one. A side probe reaches ~18 px — 1.6 tiles at TileSize 11, where it was
    // 1.1 at 16 — so it comfortably looks over a knee-high block to the wall
    // behind it, and reports that far wall's corner as the ledge in front of
    // the body. The body cannot get there: the near block is in the way.
    //
    // The window is the body's own vertical extent, which is the honest
    // question — "could this body pass through the space between?" — and NOT
    // the corner's height alone, since a reach that is clear at the hands can
    // still be blocked at the knees.
    public static TilePredicate NothingBetween(BoundingBox body, int wallDir) =>
        (chunks, t) =>
        {
            const float Skin = 0.5f;   // keeps the body's own column and the tile itself out
            float face  = body.Side(wallDir);
            float inner = wallDir == +1 ? t.WorldLeft : t.WorldRight;
            if (wallDir * (inner - face) <= Skin) return true;   // corner is AT the face
            var between = wallDir == +1
                ? new BoundingBox(face  + Skin, body.Top, inner - Skin, body.Bottom)
                : new BoundingBox(inner + Skin, body.Top, face  - Skin, body.Bottom);
            return !TileQuery.Tiles(chunks, between).Any();
        };

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
