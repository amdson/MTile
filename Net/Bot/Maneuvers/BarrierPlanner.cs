using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Shared cell planning + placement driver for the barrier maneuvers (EncloseManeuver,
// WallUpManeuver). Two halves:
//
//   * BarrierPlanner (static, pure) answers geometry questions — which cells a body
//     occupies, where the ground is under a column, which cells of a wall are still
//     empty, whether a cell is inside build reach. No input is written here.
//   * BarrierLayer (one per maneuver instance) turns an ordered cell list into raw
//     input, one cell per frame, the way a human drags Shift+RMB across cells.
//
// The placement rules it designs around (all enforced by the game, not here):
//   - BlockPlaceAction places ONE tile each time the cursor ENTERS a new cell, so jumping
//     the cursor to a different planned cell every frame lays one tile per frame.
//   - ChunkMap.TryRequestTile needs support: a solid face, or a neighbouring sprout
//     (growing OR pending). A pending ghost costs the same and promotes as soon as its
//     support solidifies, so a column can be requested bottom-up on consecutive frames
//     without waiting for each tile to finish growing.
//   - Reach: BuildReach (64 px) body-centre → cell-centre, doubled when the cell already
//     has a sprout neighbour. We plan against the plain 64 so a plan never depends on
//     the chain bonus being live at the right moment.
//   - The placement does NOT check bodies. A sprout growing into a body pushes it and,
//     wedged, crushes itself (PhysicsWorld.CrushOverlappingSprouts) — so a wall planned
//     inside the target is wasted meter. Plans keep their cells outside body boxes.
//   - Spikes (picker key 5) only overlay an existing SOLID cell, never a sprout, so a
//     spike pass has to wait out SproutLifetime after the blocks it lines.
internal static class BarrierPlanner
{
    public static float Ts => Chunk.TileSize;

    // Player torso AABB half-extents (body-space), derived from the same constants
    // PlayerCharacter.CreateBodyPolygon uses: a regular hexagon (vertex up) squeezed
    // to BodyWidthScale wide, BodyHeightScale tall about its top vertex.
    public static readonly float HalfWidth =
        PlayerCharacter.Radius * MathF.Cos(MathF.PI / 6f) * PlayerCharacter.BodyWidthScale;
    public const float Top    = -PlayerCharacter.Radius;
    public static readonly float Bottom =
        PlayerCharacter.Radius * PlayerCharacter.BodyHeightScale - PlayerCharacter.Radius * (1f - PlayerCharacter.BodyHeightScale);

    // Inclusive cell box a body at `pos` touches, padded by `pad` px. Vertical extent
    // runs from the head down to the row just above the ground under the body (the
    // standing hover leaves a gap row between the polygon and the floor; a wall that
    // skipped it would leave a crawl hole). groundRow = -1 when no ground was found.
    // `padTop` overrides `pad` above the head: a standing head sits EXACTLY on a cell
    // boundary (body y 67 → head y 55 = 5 × 11), so any positive pad there pushes a lid
    // a whole empty row up — one more stone per wall column and a lid the head never
    // touches. A slightly negative padTop keeps the lid flush on the head.
    public static (int minX, int maxX, int minY, int maxY, int groundRow) BodyBox(
        BotView v, Vector2 pos, float pad, float? padTop = null)
    {
        int minX = (int)MathF.Floor((pos.X - HalfWidth - pad) / Ts);
        int maxX = (int)MathF.Floor((pos.X + HalfWidth + pad) / Ts);
        int minY = (int)MathF.Floor((pos.Y + Top - (padTop ?? pad)) / Ts);
        int feet = (int)MathF.Floor((pos.Y + Bottom) / Ts);
        // Ground = the shallowest solid row under ANY of the body's columns.
        int ground = -1;
        for (int x = minX; x <= maxX; x++)
        {
            int g = GroundRow(v, x, feet, 4);
            if (g >= 0 && (ground < 0 || g < ground)) ground = g;
        }
        int maxY = ground >= 0 ? ground - 1 : feet;
        return (minX, maxX, minY, maxY, ground);
    }

    // First solid row in column gx at or below `fromRow`, searching `depth` rows. -1 if none.
    public static int GroundRow(BotView v, int gx, int fromRow, int depth)
    {
        for (int y = fromRow; y <= fromRow + depth; y++)
            if (v.Solid(gx, y)) return y;
        return -1;
    }

    // True once the cell is anything but untouched air: solid, sprouting, or a pending
    // ghost in the sprout graph. That is exactly "TryRequestTile would refuse it", i.e.
    // nothing more to spend there.
    public static bool Requested(BotView v, Point c)
        => v.Sim.Chunks.GetCellState(c.X, c.Y) != TileState.Empty
        || v.Sim.Chunks.Graph.TryGet(c.X, c.Y, out _);

    public static bool InReach(Vector2 me, Point c, float margin = 2f)
    {
        float r = BlockPaintAction.BuildReach - margin;
        return Vector2.DistanceSquared(me, BotView.CellCenter(c)) <= r * r;
    }

    // Empty cells of column gx from just above its ground up to `topRow`, bottom-up —
    // the order support requires. Ground is searched from `fromRow` down `depth` rows;
    // a column with no ground nearby yields nothing (a floating wall has no support).
    public static void AddColumn(BotView v, List<Point> into, int gx, int fromRow, int topRow, int depth = 4)
    {
        int ground = GroundRow(v, gx, fromRow, depth);
        if (ground < 0) return;
        for (int y = ground - 1; y >= topRow; y--)
        {
            var c = new Point(gx, y);
            if (!Requested(v, c)) into.Add(c);
        }
    }

    // Number key that selects a material on the block picker (PlayerCharacter.Update).
    public static void PressPicker(ref PlayerInput input, TileType type)
    {
        switch (type)
        {
            case TileType.Stone: input.Num1 = true; break;
            case TileType.Dirt:  input.Num2 = true; break;
            case TileType.Sand:  input.Num3 = true; break;
            case TileType.Foam:  input.Num4 = true; break;
        }
    }
}

// Feeds an ordered cell list to the game as Shift+RMB placements (or Shift+RMB spike
// overlays when `spikes` is set), one cell per frame. The owner keeps calling Step until
// it reports Done; it reports Stalled when the plan has stopped making progress so the
// maneuver can bail out instead of holding RMB forever.
internal sealed class BarrierLayer
{
    public enum Status { Placing, Done, OutOfReach, Stalled }

    public readonly List<Point> Cells = new();
    public bool   Spikes;
    public TileType Material = TileType.Stone;

    // A cell the game keeps refusing (unsupported, crushed by a body, …) is skipped
    // after this many tries so one bad cell can't stall the whole wall.
    public int MaxTriesPerCell = 8;
    // Frames with nothing new landing (meter dry, everything out of reach) ⇒ Stalled.
    public int StallFrames = DefaultStallFrames;
    public const int DefaultStallFrames = 24;

    public int Placed;      // cells that went from empty to requested/spiked while we aimed at them
    public int Skipped;

    private int _cursor;    // index of the cell being aimed at
    private int _tries;
    private int _idle;

    public void Reset(bool spikes, TileType material)
    {
        Cells.Clear();
        Spikes = spikes; Material = material;
        StallFrames = DefaultStallFrames;
        Placed = Skipped = _cursor = _tries = _idle = 0;
    }

    public int Remaining => Cells.Count - _cursor;

    // Done-ness of one cell for this layer's mode.
    private bool Finished(BotView v, Point c)
        => Spikes ? (v.Sim.Chunks.Spikes.IsSpiked(c.X, c.Y) || v.Sim.Chunks.SpikeSeeds.Has(c.X, c.Y))
                 : BarrierPlanner.Requested(v, c);   // a seeded cell is placed: it grows on its own

    public Status Step(BotView v, ref PlayerInput input)
    {
        // Advance past whatever landed last frame (or was already there).
        while (_cursor < Cells.Count && Finished(v, Cells[_cursor]))
        {
            if (_tries > 0) { Placed++; _idle = 0; }
            _cursor++; _tries = 0;
        }
        if (_cursor >= Cells.Count) return Status.Done;

        // A cell refused too many times: skip it. For spikes the usual cause is the
        // block under it still sprouting, so it gets a longer leash (lifetime ≈ 6 frames).
        int limit = Spikes ? MaxTriesPerCell * 2 : MaxTriesPerCell;
        if (_tries >= limit) { Skipped++; _cursor++; _tries = 0; return Remaining > 0 ? Status.Placing : Status.Done; }

        if (++_idle > StallFrames) return Status.Stalled;

        var cell = Cells[_cursor];
        if (!BarrierPlanner.InReach(v.Me, cell)) return Status.OutOfReach;

        var meters = v.Self.Abilities.Meters;
        float cost = Spikes ? MaterialStrengths.SpikeBuildCost : MaterialStrengths.BuildCostFor(Material);
        // The picker key is level-triggered, so holding it every frame of the pass is
        // harmless and makes the mode immune to anything that changed it in between.
        if (Spikes) input.Num5 = true;
        else        BarrierPlanner.PressPicker(ref input, Material);
        input.MouseWorldPosition = BotView.CellCenter(cell);

        // Not affordable yet: let go of RMB and wait for the working pool to refill.
        // Holding it would leave the brain's default cursor (ON the target) live under
        // Shift+RMB — and the hover-gap cell under a standing body is supported floor,
        // so that is a block placed under the target's feet.
        if (meters.BuildMove + meters.EruptMove < cost) return Status.Placing;

        input.RightClick = true;
        // Shift for spikes too: Shift+RMB is the deliberate one-overlay-per-cell path.
        // Plain RMB with spikes selected is BlockPaintAction's spike mode, which defers a
        // press until it knows the hold isn't a crystal-wave charge (todo.txt #7) — a
        // bot re-aiming one cell for several frames would read as a charge there.
        input.Shift = true;
        _tries++;
        return Status.Placing;
    }
}
