using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Traversal by avalanche: erupt a diagonal wave from under your own feet, ride its
// front (TerrainCarriedState) across a pit, and hop off onto the far side.
//
// What it beats: BuildClimbManeuver lifts straight up on a column it builds against the
// cliff it WALKED to, so a pit in the way swallows it (and every jump in the default
// chase) before it ever starts. A wave builds its own bridge as it goes — each cell it
// commits is supported by the one behind it — and carries the rider over it.
//
// What a bot-launched wave actually does (measured, BotManeuverAvalancheRideTests):
// whatever the release angle in 35°–65°, the carried run rises ~2–3 tiles in its first
// ~20 frames and then flattens into a ~130 px/s horizontal conveyor, ~11–12 tiles of
// reach in all. Charge barely changes the reach (a 40 f and a 110 f hold reach the same
// wall) — the leak is 10%/frame, so extra mass mostly thickens the mound near the
// launch. Where the front runs into a cliff the pile banks up the face and lifts the
// rider like an elevator; whether that clears the lip depends on how much mass is left
// when it arrives. So the plan is geometric, not ballistic:
//
//   Approach — walk to the launch column: ~LaunchReach tiles short of the far side, but
//              never past the pit edge. Too far back and the wave dies before the cliff
//              (bot 18 tiles out stalls 5 tiles under the lip); launching close keeps
//              the most mass for the wall-backed rise.
//   Launch   — AvalancheLauncher: charge in the floor, pull the cursor out along the
//              wave line through our own feet cell, release at speed as the ball
//              crosses it. The AvalancheRideTests sweep launches from the rider's own
//              cell and catches at every angle, and so does this.
//   Catch    — no input: the front grows into us; TerrainCarriedState takes over.
//   Ride     — hold toward the target (steering is an offset on the ride servo).
//   Hop      — jump toward the target: when the ride ends, when we're level and close,
//              or when a cliff lip within hop reach is coming up. That last one matters:
//              pinned against the face below the lip, Space is a WallJump AWAY from it.
//              The carried state hands its velocity into the jump (JumpCarrySource).
//   Land     — keep holding toward the target until grounded, then hand back.
//
// Scored only when the target has been across a pit for Patience frames and the far
// side is within the wave's reach. The real meters gate it (the charge pays out of the
// Build reservoir; the eruption needs EruptMinToFire banked).
public sealed class AvalancheRideManeuver : BotManeuver
{
    // Short on purpose: the default chase's answer to a pit is to walk into it.
    public int   Patience   = 8;
    // Target may be at most this far below us (it's across, not down in the pit).
    public float MaxDrop    = 2f * Chunk.TileSize;
    public float MaxHorizontal = 30f * Chunk.TileSize;
    // Launch this many tiles short of the far side's first column (clamped to the pit
    // edge). Swept 9–14 over nine pit/ledge layouts: 12 and 14 were the robust values.
    public int   LaunchReach = 12;
    // Widest pit (tiles) the wave is trusted to bridge (the conveyor reaches ~12–13).
    public int   MaxGap      = 13;
    // Release angle band (degrees above horizontal). 35° arrives at the cliff early and
    // banks too little; 65° wastes reach; 45–50 rode 11/11 layouts, 55 10/11.
    public float MinAngle = 45f, MaxAngle = 50f;
    // Charge hold (frames). ~0.75 s ≈ 80 units ≈ 100 dirt tiles: plenty (see above).
    public int   ChargeFrames = 50;
    // Reservoir the charge must be able to draw on (units, 2:1 conversion).
    public float MinBuild = 50f;

    public int   ApproachBudget = 240;
    public int   CatchBudget = 40;    // frames after release for the front to pick us up
    public int   RideBudget  = 240;
    public int   HopFrames   = 16;    // jump hold: near-full hop
    public int   LandBudget  = 90;
    // Hop reach off a moving ride: a held jump clears ~3.5 tiles of rise, and the
    // carried run speed covers several tiles before the apex.
    public float HopRise  = 3.5f * Chunk.TileSize;
    public float HopAhead = 5f * Chunk.TileSize;

    public float Utility = 1.15f;     // above BuildClimb's 0.9: it can't cross a pit
    public override int Cooldown => _failed ? 240 : 120;
    public override bool OwnsLocomotion => true;

    public float? AngleOverride;      // tests/tuning: force the launch angle

    private enum Phase { Approach, Launch, Catch, Ride, Hop, Land, Climb }
    private Phase _phase;
    private int   _t;
    private int   _dir;
    private int   _launchCol;
    private int   _settle;
    private int   _unreachable;
    private bool  _failed;
    private readonly AvalancheLauncher _launch = new();
    // Borrowed whole for the last few tiles: once the ride has pinned us against the far
    // cliff, the reactive wall-slide / wall-kick / LedgeGrab controller is exactly the
    // right tool, and it's a public maneuver — so we drive it rather than copy it.
    private readonly WallClimbManeuver _climb = new();
    private float _bestY;
    private int   _sinceRise;

    // Test visibility.
    public float LastAngle;
    public bool  Fired, Carried;

    public override float Score(BotView v)
    {
        bool hard = MathF.Abs(v.Dx) < MaxHorizontal && v.Dy < MaxDrop
                 && v.TargetGrounded && MathF.Abs(v.TargetVel.Y) < 40f
                 && v.Grounded && PlanCrossing(v, v.Toward, out _);
        _unreachable = hard ? _unreachable + 1 : 0;
        if (_unreachable < Patience) return 0f;
        if (v.Self.Abilities.Meters.Build < MinBuild) return 0f;
        return Utility;
    }

    public override void Begin(BotView v)
    {
        _dir = v.Toward;
        _t = 0; _settle = 0; _failed = false; Fired = Carried = false;
        _unreachable = 0;
        if (!PlanCrossing(v, _dir, out _launchCol)) _failed = true;
        _phase = Phase.Approach;
    }

    public override void Abort(BotView v, ref PlayerInput input) { _unreachable = 0; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        if (_failed) return false;
        _t++;
        switch (_phase)
        {
            case Phase.Approach:
            {
                if (_t > ApproachBudget || !v.Grounded && _t > 10) return Fail();
                float err = BotView.CellCenter(new Point(_launchCol, 0)).X - v.Me.X;
                if (MathF.Abs(err) > 3f) { BotInput.Move(ref input, err > 0f ? 1 : -1); _settle = 0; return true; }
                // Stop dead before charging: the launch line is anchored on our feet cell.
                if (MathF.Abs(v.MyVel.X) > 20f || ++_settle < 3) return true;
                LastAngle = Angle(v);
                if (!_launch.Plan(v, FeetOrigin(v), WaveDir(_dir, LastAngle), ChargeFrames)) return Fail();
                Enter(Phase.Launch);
                return true;
            }
            case Phase.Launch:
            {
                var s = _launch.Step(v, ref input);
                if (s == AvalancheLauncher.Status.Failed) return Fail();
                if (s == AvalancheLauncher.Status.Fired) { Fired = true; Enter(Phase.Catch); }
                return true;
            }
            case Phase.Catch:
                // Stand still and let the front reach us; walking off the launch cell is
                // how you miss your own wave.
                if (Riding(v)) { Carried = true; _bestY = v.Me.Y; _sinceRise = 0; Enter(Phase.Ride); return true; }
                if (_t > CatchBudget) return Fail();   // the wave fizzled / missed us
                return true;

            case Phase.Ride:
            {
                bool riding = Riding(v);
                if (v.Toward == _dir) BotInput.Move(ref input, _dir);
                bool level = v.Me.Y <= v.TargetPos.Y + Chunk.TileSize;
                bool close = MathF.Abs(v.Dx) < 5f * Chunk.TileSize;
                if (v.Me.Y < _bestY - 1f) { _bestY = v.Me.Y; _sinceRise = 0; } else _sinceRise++;
                // Pinned against the far cliff: the pile banks up the face and lifts us
                // while it lasts. Once it stops lifting (or the lip is already in grab
                // reach) hand over to the wall-climb controller — a plain hop here would
                // be a WallJump away from the very ledge we came for.
                if (AgainstFace(v) && (!riding || _sinceRise > 8 || LipAbove(v) < _climb.LipReach))
                { _climb.Begin(v); Enter(Phase.Climb); goto case Phase.Climb; }
                // Jump out while the carry is live — the handoff is freshest then.
                if (!riding || (level && close) || LipInHopReach(v) || _t > RideBudget)
                { Enter(Phase.Hop); goto case Phase.Hop; }
                return true;
            }
            case Phase.Climb:
                // WallClimb ends itself once we stand near the target's level.
                return _climb.Step(v, ref input) && _t <= LandBudget * 3;
            case Phase.Hop:
                BotInput.Move(ref input, v.Toward);
                if (_t <= HopFrames) input.Space = true;
                else Enter(Phase.Land);
                return true;

            default: // Land
                BotInput.Move(ref input, v.Toward);
                if (v.Grounded && _t > 3) return false;
                return _t <= LandBudget;
        }
    }

    private void Enter(Phase p) { _phase = p; _t = 0; }
    private bool Fail() { _failed = true; return false; }

    private static bool Riding(BotView v) => v.Self.CurrentStateName == nameof(TerrainCarriedState);

    // Aim a little above the target's offset, clamped to the band that rides well.
    private float Angle(BotView v)
    {
        if (AngleOverride.HasValue) return AngleOverride.Value;
        float deg = MathF.Atan2(-v.Dy + 2f * Chunk.TileSize, MathF.Abs(v.Dx)) * 180f / MathF.PI;
        return Math.Clamp(deg, MinAngle, MaxAngle);
    }

    internal static Vector2 WaveDir(int dir, float angleDeg)
    {
        float r = angleDeg * MathF.PI / 180f;
        return new Vector2(dir * MathF.Cos(r), -MathF.Sin(r));
    }

    // Centre of the air cell our feet stand in.
    internal static Vector2 FeetOrigin(BotView v) => BotView.CellCenter(BotView.Cell(FeetPoint(v)));
    internal static Vector2 FeetPoint(BotView v)
        => v.Me + new Vector2(0f, PlayerCharacter.StandingHeight - PlayerCharacter.Radius - 1f);

    // The crossing plan, scanning from our column toward the target along our floor row:
    //   pit     — first column with no floor for 6 rows below our floor line;
    //   far     — first column after it with anything solid from 12 rows above the
    //             floor line down to just below it (a cliff face or the far floor).
    // Launch column = LaunchReach short of `far`, but no further out than the last floor
    // column before the pit, and no further back than where we stand. False when there
    // is no pit before the target, the gap is wider than MaxGap, or the launch column has
    // no buried charge point.
    internal bool PlanCrossing(BotView v, int dir, out int launchCol)
    {
        launchCol = 0;
        var me = BotView.Cell(FeetPoint(v));
        int floorRow = me.Y + 1;
        int tx = BotView.Cell(v.TargetPos).X;
        int pit = int.MinValue, far = int.MinValue;
        for (int i = 1; i <= 40; i++)
        {
            int x = me.X + i * dir;
            if ((tx - x) * dir < 0) break;       // past the target
            if (pit == int.MinValue)
            {
                bool floor = false;
                for (int y = floorRow; y < floorRow + 6 && !floor; y++) floor = v.Solid(x, y);
                if (!floor) pit = i;
                else if (v.Solid(x, me.Y)) return false;   // a wall before any pit: not our job
                continue;
            }
            bool any = false;
            for (int y = floorRow - 12; y <= floorRow + 1 && !any; y++) any = v.Solid(x, y);
            if (any) { far = i; break; }
        }
        if (pit == int.MinValue || far == int.MinValue || far - pit > MaxGap) return false;
        int along = Math.Clamp(far - LaunchReach, 0, pit - 1);
        launchCol = me.X + along * dir;
        // The launch column's own floor must take a buried charge point.
        var origin = BotView.CellCenter(new Point(launchCol, me.Y));
        return AvalancheLauncher.FindChargePoint(v, origin, WaveDir(dir, MinAngle), _launch.RunUp, out _);
    }

    // A cliff face right in front of us (within a tile of the body's edge).
    private bool AgainstFace(BotView v)
        => v.Solid(v.Me + new Vector2(_dir * (PlayerCharacter.Radius + 0.6f * Chunk.TileSize), 0f));

    // Height (px) of the face's lip above our body centre, or +inf with no face.
    private float LipAbove(BotView v)
    {
        var c = BotView.Cell(v.Me + new Vector2(_dir * (PlayerCharacter.Radius + 0.6f * Chunk.TileSize), 0f));
        if (!v.Solid(c)) return float.PositiveInfinity;
        int y = c.Y;
        for (int i = 0; i < 40 && v.Solid(c.X, y - 1); i++) y--;
        return v.Me.Y - y * Chunk.TileSize;
    }

    // The first cliff face ahead within HopAhead: a column solid at our body's height.
    // True when its lip is no more than HopRise above our feet and we're moving at it.
    private bool LipInHopReach(BotView v)
    {
        if (v.MyVel.X * _dir < 40f) return false;
        float feetY = FeetPoint(v).Y;
        var body = BotView.Cell(v.Me);
        int reach = (int)MathF.Ceiling(HopAhead / Chunk.TileSize);
        for (int i = 1; i <= reach; i++)
        {
            int x = body.X + i * _dir;
            if (!v.Solid(x, body.Y)) continue;
            int top = body.Y;
            while (v.Solid(x, top - 1) && body.Y - top < 12) top--;
            float rise = feetY - top * Chunk.TileSize;
            return rise > 0f && rise <= HopRise;
        }
        return false;
    }
}
