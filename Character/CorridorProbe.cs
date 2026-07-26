using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Why the scan stopped where it did. Horizon = ran out of columns (the edge of sight is OPEN —
// consumers must not decelerate toward it); the other three are walls in the plan's sense:
// in-window geometry the body cannot traverse.
public enum CorridorTruncation
{
    Horizon,   // scanned all columns without hitting infeasible geometry
    Pinch,     // floor-to-ceiling gap below CorridorMinGap (includes fully-solid columns)
    TallRise,  // floor rises more than CorridorMaxRise in one column — beyond the maneuver envelope
    NoFloor,   // no supporting solid within CorridorWindowDown — a drop deeper than the window
}

// A discrete surface change between two adjacent corridor columns.
// Floor corners: Delta > 0 = rise (Pos = near boundary x, NEW floor top);
//                Delta < 0 = drop (Pos = near boundary x, OLD floor top — the exposed lip).
// Ceiling corners: recorded when the ceiling hangs lower ahead (an entry lip / overcrop);
//                Pos = (near boundary x, new ceiling bottom), Delta = how much lower vs the
//                previous effective ceiling (clamped to the window top when the previous
//                column was open sky).
public struct CorridorCorner
{
    public Vector2 Pos;
    public float   Delta;
    public int     Column;   // index of the column the corner leads into
}

// The feasible corridor prefix ahead of a body: per-column floor/ceiling in world px, the
// surface-change corners between columns, and why/where the scan stopped. Pure data — no
// behavior, no body mutation. Recomputed from scratch every query (see CorridorProbe.Scan);
// nothing here is snapshot state.
public sealed class Corridor
{
    public const int MaxColumns = 6;

    public readonly int Dir;              // ±1: scan direction
    public int   FirstColumn;             // global tile-x of column index 0 (the column containing the leading face)
    public int   ColumnCount;             // feasible columns recorded — always a prefix [0, ColumnCount)
    public CorridorTruncation Truncation;
    public int   TruncationColumn;        // index of the first infeasible column; == ColumnCount when Horizon
    public float StandingBaseY;           // body.Position.Y + 2·Radius at scan time — the feet line
    public float WindowTop, WindowBottom; // vertical search bounds used for this scan

    public readonly float[] FloorY = new float[MaxColumns];  // top of supporting solid
    public readonly float[] CeilY  = new float[MaxColumns];  // bottom of solid above the gap; -∞ = open within window

    public readonly CorridorCorner[] FloorCorners = new CorridorCorner[MaxColumns];
    public int FloorCornerCount;
    public readonly CorridorCorner[] CeilCorners = new CorridorCorner[MaxColumns];
    public int CeilCornerCount;

    public Corridor(int dir) => Dir = dir;

    // Near boundary x of column i — for Dir=+1 its left edge, for Dir=-1 its right edge.
    // This is where inter-column corners sit.
    public float ColumnEdgeX(int i)
    {
        int col = FirstColumn + Dir * i;
        return Dir == 1 ? col * (float)Chunk.TileSize : (col + 1) * (float)Chunk.TileSize;
    }

    // Column index of a global tile-x (may be outside [0, ColumnCount)).
    public int ColumnIndexOf(int gtx) => (gtx - FirstColumn) * Dir;

    public bool TryFirstRise(out CorridorCorner corner)
    {
        for (int i = 0; i < FloorCornerCount; i++)
            if (FloorCorners[i].Delta > 0f) { corner = FloorCorners[i]; return true; }
        corner = default; return false;
    }

    public bool TryFirstDrop(out CorridorCorner corner)
    {
        for (int i = 0; i < FloorCornerCount; i++)
            if (FloorCorners[i].Delta < 0f) { corner = FloorCorners[i]; return true; }
        corner = default; return false;
    }

    public bool TryFirstCeilingLip(out CorridorCorner corner)
    {
        if (CeilCornerCount > 0) { corner = CeilCorners[0]; return true; }
        corner = default; return false;
    }

    // Smallest floor-to-ceiling gap over the recorded columns (+∞ if every column is open sky).
    public float MinGap()
    {
        float min = float.PositiveInfinity;
        for (int i = 0; i < ColumnCount; i++)
        {
            if (float.IsNegativeInfinity(CeilY[i])) continue;
            float gap = FloorY[i] - CeilY[i];
            if (gap < min) min = gap;
        }
        return min;
    }

    // Total floor height change over the recorded prefix (+ = net climb).
    public float TotalRise() => ColumnCount == 0 ? 0f : StandingBaseY - FloorY[ColumnCount - 1];

    // Landing gate for a climb onto this column's floor: standing float height (2·Radius
    // above the floor top). The single definition of the gate — the reflex crest cap and
    // both climb states size their delivery against it.
    public float LandingGateY(int column) => FloorY[column] - 2f * PlayerCharacter.Radius;

    // The gate, raised (= body arrives lower/crouched) if the landing's ceiling demands it.
    public float ClimbTargetY(int column)
    {
        float targetY = LandingGateY(column);
        if (!float.IsNegativeInfinity(CeilY[column]))
            targetY = MathF.Max(targetY, CeilY[column] + PlayerCharacter.Radius + 2f);
        return targetY;
    }
}

// Column-scan sensing for the maneuver layer (Plans/CORRIDOR_MANEUVER_PLAN.md piece 1).
// Pure function of (body, chunks, dir) + MovementConfig thresholds: walks HorizonColumns tile
// columns ahead of the body's leading face; per column finds the free vertical run CONNECTED
// to the previous column's run (max overlap — this is what rejects disconnected passages above
// a roof or below a floor), derives floor/ceiling, and stops at the first infeasible column
// with a reason. Corners between columns come out as CorridorCorner records.
//
// Determinism: plain loops in fixed order, no statics, no caching — safe to call from sim code.
// Ties in run selection resolve to the LOWER run (closer to the ground path).
public static class CorridorProbe
{
    private const float Eps         = 0.01f;   // nudge into the leading-face column
    private const float RiseEpsilon = 0.5f;    // floor deltas below this are noise, not corners

    private struct RunPick
    {
        public bool  Found;       // a reachable free run exists
        public bool  AnyRiseRun;  // some run's floor is above prevFloor (TallRise-vs-Pinch hint)
        public float Floor, Ceil; // the selected run's surfaces (Ceil = -∞ when open above)
    }

    // One column's free-run scan + selection. Reachability: the run must vertically overlap
    // the previous column's open interval [prevTop, prevBot]. Selection among reachable runs:
    // floor closest to prevFloor (the corridor follows the ground the body is on); ties go to
    // the later (lower) run.
    private static RunPick PickRun(ChunkMap chunks, float colX, int rowTop, int rowBot,
        float windowTop, float windowBottom, float prevTop, float prevBot, float prevFloor)
    {
        var pick = new RunPick();
        float bestDist = float.PositiveInfinity;
        bool  haveBest = false;
        const int NoRun = int.MinValue;   // row indices can be negative — -1 is a real row
        int runStart = NoRun;
        for (int gty = rowTop; gty <= rowBot + 1; gty++)
        {
            bool solid = gty > rowBot   // sentinel row closes the final run; its floor is probed below
                || TileQuery.IsSolidAt(chunks, colX, gty * Chunk.TileSize + Chunk.TileSize * 0.5f);
            if (!solid) { if (runStart == NoRun) runStart = gty; continue; }
            if (runStart == NoRun) continue;

            // Close the run [runStart, gty-1] and resolve its bounding surfaces.
            float ceilY = runStart == rowTop
                ? (TileQuery.IsSolidAt(chunks, colX, (rowTop - 1) * Chunk.TileSize + Chunk.TileSize * 0.5f)
                    ? rowTop * (float)Chunk.TileSize : float.NegativeInfinity)
                : runStart * (float)Chunk.TileSize;
            float floorY = gty > rowBot
                ? (TileQuery.IsSolidAt(chunks, colX, (rowBot + 1) * Chunk.TileSize + Chunk.TileSize * 0.5f)
                    ? (rowBot + 1) * (float)Chunk.TileSize : float.PositiveInfinity)
                : gty * (float)Chunk.TileSize;
            runStart = NoRun;

            if (floorY < prevFloor) pick.AnyRiseRun = true;

            float top = float.IsNegativeInfinity(ceilY)  ? windowTop    : ceilY;
            float bot = float.IsPositiveInfinity(floorY) ? windowBottom : floorY;
            if (MathF.Min(bot, prevBot) - MathF.Max(top, prevTop) <= 0f) continue;   // unreachable

            pick.Found = true;
            float dist = float.IsPositiveInfinity(floorY)
                ? float.PositiveInfinity              // bottomless: any run with real ground wins
                : MathF.Abs(floorY - prevFloor);
            // <= so a tie goes to the later (lower) run.
            if (!haveBest || dist <= bestDist)
            {
                bestDist = dist; pick.Floor = floorY; pick.Ceil = ceilY; haveBest = true;
            }
        }
        return pick;
    }

    public static Corridor Scan(PhysicsBody body, ChunkMap chunks, int dir)
        => ScanInto(new Corridor(dir), body, chunks);

    // Fill-in-place variant so a long-lived Corridor (e.g. PlayerCharacter's per-direction
    // scratch) can be re-scanned every frame without allocating. Every scalar and count is
    // rewritten below; array slots beyond the counts are stale but never read.
    public static Corridor ScanInto(Corridor c, PhysicsBody body, ChunkMap chunks)
    {
        var cfg = MovementConfig.Current;
        int dir = c.Dir;
        int columns = Math.Clamp(cfg.CorridorHorizonColumns, 1, Corridor.MaxColumns);
        c.ColumnCount = 0;
        c.FloorCornerCount = 0;
        c.CeilCornerCount = 0;

        var bounds = body.Bounds;
        float face = bounds.Side(dir);
        c.StandingBaseY = body.Position.Y + 2f * PlayerCharacter.Radius;
        c.WindowTop     = c.StandingBaseY - cfg.CorridorWindowUp;
        c.WindowBottom  = c.StandingBaseY + cfg.CorridorWindowDown;
        c.FirstColumn   = (int)MathF.Floor((face + dir * Eps) / Chunk.TileSize);

        int rowTop = (int)MathF.Floor(c.WindowTop / Chunk.TileSize);
        int rowBot = (int)MathF.Floor((c.WindowBottom - Eps) / Chunk.TileSize);

        // Connectivity: a run is REACHABLE from the previous column iff it vertically overlaps
        // that column's open interval [prevTop, prevBot] — the body cannot pass through the
        // previous floor (rejects voids under the ground) or its ceiling (rejects sky above a
        // roof the body is under). Among reachable runs, SELECTION picks the one whose floor
        // deviates least from the previous floor — the corridor follows the ground the body is
        // on (a drop into a tunnel mouth beats climbing over its roof); ties go to the lower
        // run. Seeded with the body's own occupied span.
        float prevTop   = bounds.Top;
        float prevBot   = c.StandingBaseY;
        float prevFloor = c.StandingBaseY;
        float prevCeil  = float.NegativeInfinity;

        c.Truncation = CorridorTruncation.Horizon;
        c.TruncationColumn = columns;

        // Seed prevFloor from the ground actually under the body's own column, not from
        // StandingBaseY: a body displaced below rest (duck bob, spring compression, being
        // shoved by a ramp) would otherwise read the real flat floor as a phantom "rise"
        // and trip maneuvers on level ground. StandingBaseY stays the fallback when no
        // floor resolves in the body's column (airborne over a pit).
        {
            float bodyColX = MathF.Floor(body.Position.X / Chunk.TileSize) * Chunk.TileSize
                           + Chunk.TileSize * 0.5f;
            var seed = PickRun(chunks, bodyColX, rowTop, rowBot,
                c.WindowTop, c.WindowBottom, prevTop, prevBot, prevFloor);
            if (seed.Found && !float.IsPositiveInfinity(seed.Floor))
                prevFloor = seed.Floor;
        }

        for (int i = 0; i < columns; i++)
        {
            int col = c.FirstColumn + dir * i;
            float colX = col * Chunk.TileSize + Chunk.TileSize * 0.5f;

            var pick = PickRun(chunks, colX, rowTop, rowBot,
                c.WindowTop, c.WindowBottom, prevTop, prevBot, prevFloor);
            bool found = pick.Found, anyRiseRun = pick.AnyRiseRun;
            float bestFloor = pick.Floor, bestCeil = pick.Ceil;

            if (!found)
            {
                // No free run connects to the previous column. If free space exists above the
                // current floor line it's a too-tall step (a wall in maneuver terms); otherwise
                // the column is sealed/squeezed shut.
                c.Truncation = anyRiseRun ? CorridorTruncation.TallRise : CorridorTruncation.Pinch;
                c.TruncationColumn = i;
                return c;
            }
            if (float.IsPositiveInfinity(bestFloor))
            {
                c.Truncation = CorridorTruncation.NoFloor;
                c.TruncationColumn = i;
                return c;
            }
            float rise = prevFloor - bestFloor;   // y-down: positive = up
            if (rise > cfg.CorridorMaxRise)
            {
                c.Truncation = CorridorTruncation.TallRise;
                c.TruncationColumn = i;
                return c;
            }
            if (!float.IsNegativeInfinity(bestCeil) && bestFloor - bestCeil < cfg.CorridorMinGap)
            {
                c.Truncation = CorridorTruncation.Pinch;
                c.TruncationColumn = i;
                return c;
            }

            // Column is feasible — record it and any corners on its near boundary.
            c.FloorY[i] = bestFloor;
            c.CeilY[i]  = bestCeil;
            c.ColumnCount = i + 1;

            float edgeX = c.ColumnEdgeX(i);
            if (MathF.Abs(rise) > RiseEpsilon)
            {
                c.FloorCorners[c.FloorCornerCount++] = new CorridorCorner
                {
                    // Rise: the corner is the new floor's exposed lip. Drop: it's the old floor's.
                    Pos    = new Vector2(edgeX, rise > 0f ? bestFloor : prevFloor),
                    Delta  = rise,
                    Column = i,
                };
            }
            if (!float.IsNegativeInfinity(bestCeil) && bestCeil > prevCeil)
            {
                c.CeilCorners[c.CeilCornerCount++] = new CorridorCorner
                {
                    Pos    = new Vector2(edgeX, bestCeil),
                    Delta  = bestCeil - MathF.Max(prevCeil, c.WindowTop),
                    Column = i,
                };
            }

            prevFloor = bestFloor;
            prevCeil  = bestCeil;
            prevTop   = float.IsNegativeInfinity(bestCeil) ? c.WindowTop : bestCeil;
            prevBot   = bestFloor;
        }

        return c;   // Horizon truncation, set before the loop
    }
}
