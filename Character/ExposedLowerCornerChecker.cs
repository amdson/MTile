using Microsoft.Xna.Framework;

namespace MTile;

public readonly struct ExposedLowerCorner
{
    public readonly Vector2 InnerEdge; // (tileLeft, tileBottom) for wallDir=1; (tileRight, tileBottom) for -1
    public ExposedLowerCorner(Vector2 innerEdge) => InnerEdge = innerEdge;
}

public static class ExposedLowerCornerChecker
{
    private const float ProbeSlack = 15f;

    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        int wallDir,
        out ExposedLowerCorner corner)
    {
        corner = default;
        var bounds = body.Bounds;
        // Probe a side slab from the body's head down to its center — corners whose bottom is in
        // this y window are at "head height" relative to the body.
        var probe = bounds.StripBeside(wallDir, ProbeSlack).WithVerticalRange(bounds.Top, bounds.CenterY);
        // Use the body's CENTER X (not its facing face) as the "outside-body" reference.
        // Anchoring on the face causes a sub-pixel forward overlap to flip the lower-corner
        // detection off, which in turn flickers ParkourState between engaging and exiting
        // against chest-height walls (reported user bug: pressing Up next to a low ledge).
        // The center-X reference tolerates the body clipping into the wall by up to halfW
        // before the corner drops, which is wider than any per-frame position step here.
        float bodyFace = body.Position.X;

        // Pick the lowest-hanging slab — largest WorldBottom — among tiles that
        // (a) sit outside the body's facing face, (b) have their bottom strictly
        // below the body's head (so the body actually fits under), (c) have an
        // empty cell directly below, (d) have an empty body-facing neighbor at
        // the same row (edge-vs-interior rule, mirror of the upper-corner fix),
        // and (e) have an empty diagonal lower-inward neighbor.
        var best = TileQuery.Tiles(chunks, probe)
            .Where(TileFilters.OutsideBodyFace(bodyFace, wallDir))
            .Where(TileFilters.BottomBelow(bounds.Top))
            .Where(TileFilters.BottomExposed)
            .Where(TileFilters.BodyFacingNeighborEmpty(wallDir))
            .Where(TileFilters.LowerDiagonalClear(wallDir))
            .MaxBy(t => t.WorldBottom);

        if (best is not { } b) return false;
        float bestX = wallDir == 1 ? b.WorldLeft : b.WorldRight;
        corner = new ExposedLowerCorner(new Vector2(bestX, b.WorldBottom));
        return true;
    }
}
