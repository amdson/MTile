using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Executes a NavGrid path edge by edge, replaying exactly the gesture the JumpTable was
// calibrated with (ZzzRouteCalibration): get to the takeoff spot, press (or for Walk,
// just cross the edge), hold Space HoldFrames, hold the travel direction until the body
// centre passes the middle of the destination's first column, then release it.
//
// Closed loop: an edge succeeds when the body is standing on its destination node (or
// one column past it, the table's overshoot); landing anywhere else, or running out of
// time, is a failure and the owner replans from wherever the body is.
public sealed class RouteFollower
{
    public enum Status { Running, Done, Failed }

    // Stand takeoff window: body centre this many cells short of the takeoff cell's far
    // edge (the generator flew 0.6 and 0.9), at rest.
    public float StandBackMin = 0.55f, StandBackMax = 0.95f;
    public float RestSpeed    = 8f;
    public int   ApproachBudget = 150;    // frames to reach the takeoff
    public int   FlightBudget   = 150;    // frames from press to landing

    private enum Phase { Approach, Flight }

    private List<NavGrid.Edge> _path;
    private int   _i;
    private Phase _phase;
    private int   _t, _pressAt;
    private bool  _backingUp;

    public IReadOnlyList<NavGrid.Edge> Path => _path;
    public string PhaseName => $"{_phase} t{_t} back{_backingUp}";
    public int Index => _i;
    public NavGrid.Edge? Current => _path != null && _i < _path.Count ? _path[_i] : null;
    // Safe to swap the path: grounded and not yet committed to the current edge's takeoff.
    // A running takeoff is committed from the start: its back-up walks the body onto an
    // earlier node, and a replan from there would throw the run-up away.
    public bool Interruptible => _path == null || _i >= _path.Count
        || (_path[_i].Kind == NavGrid.EdgeKind.Build && !_raising)
        || (_path[_i].Kind == NavGrid.EdgeKind.Jump && _phase == Phase.Approach && _path[_i].Jump.Takeoff < JumpTable.Takeoff.Edge);

    public void Follow(List<NavGrid.Edge> path)
    {
        // Same first edge as the one under way: keep its progress (a running takeoff's
        // back-up and run-in outlasts a replan period).
        bool same = Current is NavGrid.Edge cur && path.Count > 0
                    && cur.From.Equals(path[0].From) && cur.To.Equals(path[0].To) && cur.Jump.Takeoff == path[0].Jump.Takeoff;
        _path = path; _i = 0;
        if (!same) BeginEdge();
    }

    private void BeginEdge() { _phase = Phase.Approach; _t = 0; _pressAt = -1; _backingUp = false; _rest = 0; }
    private int _rest;
    public int RestFrames = 12;     // the calibration started from a settled stand (20 frames)

    private static float T => Chunk.TileSize;

    public Status Step(BotView v, ref PlayerInput input)
    {
        if (_path == null || _i >= _path.Count) return Status.Done;
        var e = _path[_i];
        int dir = e.Dir;
        float x = v.Me.X;
        // The takeoff cell's far edge in the travel direction, and the brake line.
        float edge  = dir > 0 ? (e.From.X + 1) * T : e.From.X * T;
        float brake = (e.To.X + 0.5f) * T;
        float ahead(float target) => dir * (x - target);   // > 0: past it
        input.Left = input.Right = false;
        input.Space = false;
        _t++;

        if (e.Kind == NavGrid.EdgeKind.Climb) return Climb(v, e, ref input);
        if (e.Kind == NavGrid.EdgeKind.Build) return Build(v, e, ref input);

        if (_phase == Phase.Approach)
        {
            if (_t > ApproachBudget) return Status.Failed;
            // The body must be at (or next to) the edge's start; anything else is a stale
            // path, and walking its gestures from here goes somewhere arbitrary.
            if (_t == 1 && v.Grounded && NavGrid.NodeUnder(v.Solid, v.Me) is NavGrid.Node at
                && (at.Y != e.From.Y || Math.Abs(at.X - e.From.X) > 1)) return Status.Failed;
            switch (e.Jump.Takeoff)
            {
                case JumpTable.Takeoff.Walk:
                    // A flat one-cell walk is just "keep walking"; anything else crosses an
                    // edge and becomes a flight the moment the centre passes it.
                    if (e.Jump.Dy == 0 && e.Jump.Dx == 1)
                    {
                        BotInput.Move(ref input, dir);
                        if (ahead(brake) >= -0.4f * T) return Advance();
                        return Status.Running;
                    }
                    BotInput.Move(ref input, dir);
                    if (ahead(edge) >= 0f) Press(false);
                    return Status.Running;

                case JumpTable.Takeoff.Stand:
                {
                    float back = -ahead(edge) / T;          // cells short of the edge
                    if (back > StandBackMax) { BotInput.Move(ref input, dir); _rest = 0; }
                    else if (back < StandBackMin) { BotInput.Move(ref input, -dir); _rest = 0; }
                    else if (MathF.Abs(v.MyVel.X) < RestSpeed && v.Grounded && ++_rest >= RestFrames)
                    {
                        Press(true);
                        BotInput.Move(ref input, dir);
                        input.Space = true;
                    }
                    return Status.Running;
                }

                default:
                {
                    // Running takeoff: from at least ~3 cells back, run in, press at the lead.
                    float back = -ahead(edge) / T;
                    if (!_backingUp && back < 3f && _t < 3) _backingUp = true;
                    if (_backingUp)
                    {
                        BotInput.Move(ref input, -dir);
                        if (back >= 3.5f) _backingUp = false;
                        return back > 6f ? Status.Failed : Status.Running;
                    }
                    BotInput.Move(ref input, dir);
                    if (v.Grounded && ahead(edge - dir * JumpTable.Lead(e.Jump.Takeoff)) >= 0f)
                    {
                        Press(true);
                        input.Space = true;
                    }
                    return Status.Running;
                }
            }
        }

        // Flight: hold Space for HoldFrames (jumps only), the direction until the brake line.
        if (_t - _pressAt > FlightBudget) return Status.Failed;
        if (_jumping && _t - _pressAt < JumpTable.HoldFrames) input.Space = true;
        if (ahead(brake) < 0f) BotInput.Move(ref input, dir);
        if (_t - _pressAt > 4 && v.Grounded)
        {
            var at = NavGrid.NodeUnder(v.Solid, v.Me);
            if (at is NavGrid.Node n && n.Y == e.To.Y && dir * (n.X - e.To.X) >= 0 && dir * (n.X - e.To.X) <= 1)
                return Advance();
            // Grounded somewhere else (fell short, bounced back): let the owner replan —
            // unless still on the takeoff and the jump never left (a sliding stand).
            if (at is NavGrid.Node m && !(m.Equals(e.From) && _t - _pressAt < 12)) return Status.Failed;
        }
        return Status.Running;
    }

    // ── Climb: WallClimbManeuver's reactive controller with the side fixed to the edge's
    // direction. Press into the face; Space on a fresh edge when grounded beside it or
    // wall-sliding (a WallJump kicks off and re-attaches higher); keep Space while
    // rising; hold Up once the lip is within LipReach (LedgeGrab -> LedgePull) and
    // through the ledge states. The face top is read live: a camper stacking higher just
    // makes the climb longer.
    public float LipReach = 3.0f * Chunk.TileSize;
    private Status Climb(BotView v, NavGrid.Edge e, ref PlayerInput input)
    {
        int h = e.From.Y - e.To.Y;
        if (_t > 90 + 12 * h) return Status.Failed;
        int fx = e.To.X;
        string st = v.Self.CurrentStateName;
        if (_t > 5 && v.Grounded && NavGrid.NodeUnder(v.Solid, v.Me) is NavGrid.Node n)
        {
            // On top — or higher (it grew), or a column or two further onto a wide top.
            if (n.Y <= e.To.Y && e.Dir * (n.X - fx) >= 0 && e.Dir * (n.X - fx) <= 2) return Advance();
            if (n.Y < e.From.Y - 1 && n.X != fx) return Status.Failed;        // landed on something else
        }
        BotInput.Move(ref input, e.Dir);
        int y = (int)MathF.Floor(v.Me.Y / T);
        while (y > -1000 && v.Solid(fx, y - 1)) y--;
        float top = y * T;
        bool ledge = st is "LedgeGrabState" or "LedgePullState";
        bool nearLip = v.Solid(fx, (int)MathF.Floor(v.Me.Y / T)) && v.Me.Y - top < LipReach;
        if (ledge || nearLip) input.Up = true;
        bool rising = st is "JumpingState" or "RunningJumpState" or "WallJumpingState" or "DoubleJumpingState" or "LedgeJumpState";
        if (ledge) { }
        else if (rising && v.MyVel.Y < 0f) input.Space = true;
        else if (!v.Previous.Space && st == "WallSlidingState") input.Space = true;
        else if (!v.Previous.Space && v.Grounded) input.Space = true;
        return Status.Running;
    }

    // ── Build: BuildClimbManeuver's elevator. Pick dirt if the hand holds foam (decays)
    // or spikes (an overlay, can't be placed in air); centre on the column and come to
    // rest; then hold Shift+RMB with the cursor on the first empty cell of the column under
    // the feet — each new cell grows up into the body and lifts it, and while one is still
    // growing the cursor is already a row higher, so the lift never stalls.
    public int BuildStallFrames = 45;
    private float _bestY;
    private int   _sinceGain, _settle;
    private bool  _raising;
    private Status Build(BotView v, NavGrid.Edge e, ref PlayerInput input)
    {
        int k = e.From.Y - e.To.Y, col = e.From.X;
        if (_t > 90 + 10 * k) return Status.Failed;
        if (_t == 1)
        {
            _raising = false; _settle = 0; _sinceGain = 0; _bestY = v.Me.Y;
            if (v.Self.SpikesSelected || v.Self.ActiveBlockType == TileType.Foam) { input.Num2 = true; return Status.Running; }
        }
        if (NavGrid.NodeUnder(v.Solid, v.Me) is NavGrid.Node n && n.X == col && n.Y <= e.To.Y && v.Grounded)
            return Advance();

        float err = (col + 0.5f) * T - v.Me.X;
        if (MathF.Abs(err) > 2.5f) BotInput.Move(ref input, err > 0f ? 1 : -1);
        if (!_raising)
        {
            if (MathF.Abs(err) > 2.5f || !v.Grounded || MathF.Abs(v.MyVel.X) > 25f) { _settle = 0; return Status.Running; }
            if (++_settle < 2) return Status.Running;
            _raising = true; _bestY = v.Me.Y; _sinceGain = 0;
        }
        if (v.Me.Y < _bestY - 3f) { _bestY = v.Me.Y; _sinceGain = 0; }
        else if (++_sinceGain > BuildStallFrames) return Status.Failed;

        // First empty cell of the column above whatever is under the body.
        int top = (int)MathF.Floor(v.Me.Y / T) + 1;
        int maxRow = (int)MathF.Floor((v.Me.Y + PlayerCharacter.StandingHeight - PlayerCharacter.Radius + T) / T);
        for (int y = top; y <= maxRow; y++)
        {
            if (v.Sim.Chunks.GetCellState(col, y).IsOpen()) continue;
            input.Shift = true; input.RightClick = true;
            input.MouseWorldPosition = BotView.CellCenter(new Point(col, y - 1));
            break;
        }
        return Status.Running;
    }

    private bool _jumping;
    private void Press(bool jump) { _phase = Phase.Flight; _pressAt = _t; _jumping = jump; }

    private Status Advance()
    {
        _i++;
        if (_i >= _path.Count) return Status.Done;
        BeginEdge();
        return Status.Running;
    }
}
