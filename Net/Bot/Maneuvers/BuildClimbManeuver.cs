using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Climb to a target that jumping can't reach by building an elevator column under
// your own feet.
//
// Why a column under the feet rather than a staircase: a sprout requested in the empty
// cell directly beneath a standing body grows UP into it at TileSize / SproutLifetime
// (~110 px/s) and carries the body with it (SproutLiftJumpTests pins that ride as
// smooth). So "stand still, keep the cursor on the cell under your feet, hold
// Shift+RMB" is a one-tile-per-~6-frames lift that needs no jumping, no aim at a
// moving body, and no air control. Each new cell is supported by the one just placed
// (or chains off it as a Pending ghost while it is still growing), so the support rule
// in ChunkMap.TryRequestTile is satisfied for free, and the cell is always ~1 tile from
// the body centre — far inside BlockPaintAction.BuildReach. A staircase would need the
// bot to place blocks it then jumps onto, i.e. a jump per step and a cursor out in
// front of the body that the reach and support rules both fight.
//
// Why Shift+RMB (BlockPlaceAction) and not plain RMB painting: the painter's ball lags
// the cursor and spills mass to neighbours, so the column grows fat and where it lands
// is a cascade's decision. BlockPlace puts exactly one tile in exactly the cursor cell
// the moment the cursor enters it, paid in full from BuildMeters — and the cell under
// the feet changes row every time the lift completes, which is precisely "entering a
// new cell", so one continuous hold lays the whole column.
//
// Phases:
//   Pick     — if the active material is foam (decays under you) or spikes (overlay,
//              can't place in air), press Num2 for dirt for one frame. Dirt is the
//              cheapest permanent material (0.77 units/tile; the working pool alone is
//              ~60 tiles before the refill rate matters).
//   Approach — walk at the target until pressed against its wall, or until under it.
//              The column is built flush against the cliff so stepping off lands on it.
//   Raise    — hold Shift+RMB with the cursor on the cell under the feet until the
//              body is level with the target.
//   StepOff  — release, walk toward the target with a jump to clear the lip, and hand
//              back to default locomotion once standing at the target's height.
//
// Scored only when the target has sat well above the bot for a while (default
// locomotion — which jumps at targets above — has had its chance and failed).
public sealed class BuildClimbManeuver : BotManeuver
{
    // Target must be at least this far above (px) to count as "not jumpable". Below it,
    // the chase's own jumps (and its wall-slide/mantle fallbacks) get the job; the
    // Patience window lets them try before we commit to building.
    public float MinRise        = 4.5f * Chunk.TileSize;
    // Don't start a build for a target miles away horizontally — walk closer first.
    public float MaxHorizontal  = 14f * Chunk.TileSize;
    // Frames the target must stay out of reach above before building is worth it.
    public int   Patience       = 40;
    // Frame budgets for each phase; exceeding one aborts (and the long cooldown keeps
    // the bot from immediately repeating a plan that just failed).
    public int   ApproachBudget = 240;
    public int   RaiseBudget    = 360;
    public int   StepOffBudget  = 60;
    // Raise aborts if the body hasn't gained this much height in StallFrames.
    public float StallGain      = 3f;
    public int   StallFrames    = 45;
    // How close (px) the body centre must stay to the build column's centre.
    public float CenterTolerance = 2.5f;

    private enum Phase { Pick, Approach, Raise, StepOff }

    private Phase _phase;
    private int   _phaseFrames;
    private int   _unreachable;    // Score's patience counter (observation only)
    private int   _settle;         // Approach: frames stopped on the build column
    private int   _col;            // Raise: the column being built (cell x)
    private float _bestY;          // Raise: highest (smallest y) body position so far
    private int   _sinceGain;
    private int   _broke;          // frames the meter couldn't afford a tile
    private int   _dir;            // StepOff walk direction, fixed when it begins
    private int   _stuckOff;       // StepOff: frames pushing a lip without moving
    private bool  _failed;

    public override int  Cooldown       => _failed ? 180 : 60;
    public override bool OwnsLocomotion => true;

    // Distance from a standing body's centre down to the floor it stands on
    // (PlayerCharacter.StandingHeight is ground-to-head; the head is Radius above centre).
    private static float FootDrop => PlayerCharacter.StandingHeight - PlayerCharacter.Radius;

    public override float Score(BotView v)
    {
        // A target that's in the air is coming down on its own; one that's merely a
        // step up is the chase's job.
        bool outOfReach = v.Dy < -MinRise
                       && MathF.Abs(v.Dx) < MaxHorizontal
                       && MathF.Abs(v.TargetVel.Y) < 40f
                       && v.TargetGrounded;
        _unreachable = outOfReach ? _unreachable + 1 : 0;
        if (_unreachable < Patience || !v.Grounded) return 0f;
        // No point building into a ceiling right over our head.
        if (CeilingAbove(v)) return 0f;
        return 0.9f;
    }

    public override void Begin(BotView v)
    {
        _phase  = NeedsPick(v) ? Phase.Pick : Phase.Approach;
        _phaseFrames = 0;
        _settle = _sinceGain = _broke = _stuckOff = 0;
        _failed = false;
        _unreachable = 0;
    }

    public override void Abort(BotView v, ref PlayerInput input) { _unreachable = 0; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _phaseFrames++;
        switch (_phase)
        {
            case Phase.Pick:
                // Level-triggered picker key: one frame is enough.
                input.Num2 = true;
                Enter(Phase.Approach);
                return true;

            case Phase.Approach: return Approach(v, ref input);
            case Phase.Raise:    return Raise(v, ref input);
            default:             return StepOff(v, ref input);
        }
    }

    private bool Approach(BotView v, ref PlayerInput input)
    {
        if (_phaseFrames > ApproachBudget) return Fail();
        // Target came down / moved off: nothing left to climb to.
        if (v.Dy > -2f * Chunk.TileSize) return false;

        // Pick the column to build in. Against a cliff: one cell SHORT of the face,
        // never flush. A requested cell grows out of every solid neighbour at once
        // (ChunkMap.TryRequestTile), so a cell touching the wall grows sideways out of
        // it too and shoves the body off its own column (the first cut of this maneuver
        // did exactly that and rode the sprout ~30px away from the wall). With an empty
        // column between, each block's only support is the one below it and it grows
        // straight up. The one-cell gap is narrower than a stride, so the step-off
        // walks across it. No cliff in sight but standing under the target (an
        // overhang edge / floating platform): build where we stand.
        int? wall = WallColumnAhead(v, v.Toward);
        int col;
        if (wall.HasValue)                                col = wall.Value - 2 * v.Toward;
        else if (MathF.Abs(v.Dx) < 1.0f * Chunk.TileSize) col = BotView.Cell(v.Me).X;
        else { BotInput.Move(ref input, v.Toward); _settle = 0; return true; }

        // Walk onto that column's centre and stop; a moving body would slide off the
        // block growing under it.
        float err = ColumnX(col) - v.Me.X;
        if (MathF.Abs(err) > CenterTolerance)
        {
            BotInput.Move(ref input, err >= 0f ? 1 : -1);
            _settle = 0;
            return true;
        }
        if (!v.Grounded || MathF.Abs(v.MyVel.X) > 25f || ++_settle < 2) return true;

        if (CeilingAbove(v)) return Fail();
        _col   = col;
        _bestY = v.Me.Y;
        Enter(Phase.Raise);
        return Raise(v, ref input);
    }

    private static float ColumnX(int col) => col * Chunk.TileSize + Chunk.TileSize * 0.5f;

    private bool Raise(BotView v, ref PlayerInput input)
    {
        if (_phaseFrames > RaiseBudget) return Fail();

        // Level with the target (feet at or above its floor): go across.
        if (v.Me.Y <= v.TargetPos.Y + 2f)
        {
            Enter(Phase.StepOff);
            _dir = v.Toward;
            return StepOff(v, ref input);
        }
        if (CeilingAbove(v)) return Fail();

        if (v.Me.Y < _bestY - StallGain) { _bestY = v.Me.Y; _sinceGain = 0; }
        else if (++_sinceGain > StallFrames) return Fail();

        var meters = v.Self.Abilities.Meters;
        if (meters.CanAfford(v.Self.ActiveBlockType)) _broke = 0;
        else if (++_broke > 90) return Fail();   // reservoir is dry, not just the working pool

        // Stay centred over the column: the lift's completion hop and spring settle can
        // nudge the body sideways, and a body on the column's edge gets only partial
        // lift — or none, once it's over the gap.
        float err = ColumnX(_col) - v.Me.X;
        if (MathF.Abs(err) > CenterTolerance) BotInput.Move(ref input, err >= 0f ? 1 : -1);

        // The cell to fill: the first empty cell of our column above whatever we stand
        // on. Scanning down from the body (rather than computing a fixed foot offset)
        // means that while the last block is still growing — Sprouting, so non-empty —
        // we already aim one row higher, and BlockPlace parks it as a Pending ghost that
        // promotes the moment the one below finalizes. The lift never stalls between
        // blocks.
        if (!FootCell(v, _col, out var cell)) return true;   // mid-hop over the column: wait
        input.Shift      = true;
        input.RightClick = true;
        input.MouseWorldPosition = BotView.CellCenter(cell);
        return true;
    }

    private bool StepOff(BotView v, ref PlayerInput input)
    {
        if (_phaseFrames > StepOffBudget) return false;   // default locomotion takes over
        // On the target's level and off our column: done.
        if (_phaseFrames > 8 && v.Grounded && MathF.Abs(v.Dy) < 1.5f * Chunk.TileSize)
            return false;
        BotInput.Move(ref input, _dir);
        // Walk, don't jump. A jump off the column launches RELATIVE to the rising
        // sprout (SproutLiftJumpTests) — the last ghost is usually still lifting us at
        // ~110 px/s — and the first cut of this phase sailed clean over a six-tile
        // pillar top. Only hop if a lip actually stops us (last block left us a few px
        // shy), and then only a short tap.
        if (v.Grounded && MathF.Abs(v.MyVel.X) < 8f) _stuckOff++; else _stuckOff = 0;
        if (_stuckOff >= 6) { input.Space = true; _stuckOff = -6; }   // −6: tap, then a release gap
        return true;
    }

    private void Enter(Phase p) { _phase = p; _phaseFrames = 0; }

    private bool Fail() { _failed = true; return false; }

    private static bool NeedsPick(BotView v)
        => v.Self.SpikesSelected || v.Self.ActiveBlockType == TileType.Foam;

    // The nearest solid column ahead (direction `dir`) within a few cells, at body
    // height — a face we'd walk into, not the floor.
    private static int? WallColumnAhead(BotView v, int dir)
    {
        var c = BotView.Cell(v.Me);
        for (int k = 1; k <= 4; k++)
        {
            int x = c.X + dir * k;
            if (v.Solid(x, c.Y) || v.Solid(x, c.Y + 1)) return x;
        }
        return null;
    }

    // Solid within the head-clearance band: a column would just crush us into it.
    private static bool CeilingAbove(BotView v)
    {
        var c = BotView.Cell(v.Me);
        return v.Solid(c.X, c.Y - 1) || v.Solid(c.X, c.Y - 2);
    }

    // First empty cell of column `col` above the first non-empty cell below the body
    // (searched down to a tile past the feet).
    private static bool FootCell(BotView v, int col, out Point cell)
    {
        int top    = BotView.Cell(v.Me).Y + 1;
        int maxRow = BotView.Cell(v.Me + new Vector2(0f, FootDrop + Chunk.TileSize)).Y;
        for (int y = top; y <= maxRow; y++)
        {
            if (v.Sim.Chunks.GetCellState(col, y).IsOpen()) continue;
            cell = new Point(col, y - 1);
            return true;
        }
        cell = default;
        return false;
    }
}
