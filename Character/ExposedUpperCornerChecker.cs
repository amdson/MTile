using Microsoft.Xna.Framework;

namespace MTile;

public readonly struct ExposedCorner
{
    public readonly Vector2 InnerEdge; // (tileLeft, tileTop) for wallDir=1; (tileRight, tileTop) for -1
    public ExposedCorner(Vector2 innerEdge) => InnerEdge = innerEdge;
}

public static class ExposedUpperCornerChecker
{
    private const float ProbeSlack = 18f;

    // ParkourState vault precondition band, in tile heights above the body's
    // "standing base" — the Y where the body's feet would rest if grounded at
    // its current (X, Y):
    //   StandingBaseY = body.Position.Y + 2 * PlayerCharacter.Radius
    //                 = body center + (halfH + floatHeight)   (apex-to-floor in standing pose)
    // A corner whose WorldTop sits inside
    //   [StandingBaseY - MaxVaultHeightTiles*TS, StandingBaseY - MinVaultHeightTiles*TS]
    // is "vault-able": shallow enough to crest from the current altitude, tall
    // enough to actually be a step rather than the floor or a flush wall corner.
    //
    // 0.5..1.2 brackets the canonical 1-block vault (TS) with small play margin.
    // It excludes:
    //   - 0-block (flush) corners — the body would just walk over.
    //   - 2-block+ corners — the wall above the step is in the way (TallWall).
    //   - corners the body has already crested (above the upper bound of the band).
    private const float MinVaultHeightTiles = 0.5f;
    private const float MaxVaultHeightTiles = 1.2f;

    // ParkourState precondition: is there a vault-able upper corner on `wallDir`?
    // Picks the shallowest qualifying corner — largest WorldTop inside the band.
    public static bool TryFind(PhysicsBody body, ChunkMap chunks, int wallDir, out ExposedCorner corner)
    {
        corner = default;
        var bounds = body.Bounds;
        float standingBaseY = body.Position.Y + 2f * PlayerCharacter.Radius;
        float bandTop    = standingBaseY - MaxVaultHeightTiles * Chunk.TileSize;
        float bandBottom = standingBaseY - MinVaultHeightTiles * Chunk.TileSize;

        var probe = bounds.StripBeside(wallDir, ProbeSlack).WithVerticalRange(bandTop, bandBottom);
        float bodyFace = bounds.Side(wallDir);

        //   - OutsideBodyFace          tile sits on the body-far side of the facing face.
        //   - TopExposed               no solid directly above (true outer face).
        //   - BodyFacingNeighborEmpty  edge-vs-interior rule (fixes inset-corner bug).
        //   - UpperDiagonalClear       rejects notch corners under an overhang.
        //   - WorldTopInRange          pins tile.WorldTop strictly within the band;
        //                              a tile spans a full TS so it can overlap the
        //                              probe rect without its top being in the band.
        var best = TileQuery.Tiles(chunks, probe)
            .Where(TileFilters.OutsideBodyFace(bodyFace, wallDir))
            .Where(TileFilters.TopExposed)
            .Where(TileFilters.BodyFacingNeighborEmpty(wallDir))
            .Where(TileFilters.UpperDiagonalClear(wallDir))
            .Where(TileFilters.WorldTopInRange(bandTop, bandBottom))
            .MaxBy(t => t.WorldTop);

        if (best is not { } b) return false;
        float bestX = wallDir == 1 ? b.WorldLeft : b.WorldRight;
        corner = new ExposedCorner(new Vector2(bestX, b.WorldTop));
        return true;
    }

    // Ledge-grab probe (above-head, single-Y line). Targets a different reach
    // than TryFind: the body's head height, where the corner of a graspable
    // ledge would sit. Kept separate so the vault band doesn't apply here.
    public static bool TryFindAboveHead(PhysicsBody body, ChunkMap chunks, int wallDir, out ExposedCorner corner)
    {
        corner = default;
        var bounds = body.Bounds;
        float headY = bounds.Top;
        var probe = bounds.StripBeside(wallDir, ProbeSlack).WithVerticalRange(headY, headY);
        float bodyFace = bounds.Side(wallDir);

        var best = TileQuery.Tiles(chunks, probe)
            .Where(TileFilters.OutsideBodyFace(bodyFace, wallDir))
            .Where(TileFilters.TopExposed)
            .Where(TileFilters.BodyFacingNeighborEmpty(wallDir))
            .Where(TileFilters.UpperDiagonalClear(wallDir))
            .MaxBy(t => t.WorldTop);

        if (best is not { } b) return false;
        float bestX = wallDir == 1 ? b.WorldLeft : b.WorldRight;
        corner = new ExposedCorner(new Vector2(bestX, b.WorldTop));
        return true;
    }
}
