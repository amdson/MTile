using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Offense by avalanche: erupt a shallow wave from under our own feet, ride its front at
// the target, and jump out of it with the carried momentum into an air slash.
//
// What it buys, measured honestly (BotManeuverAvalancheChargeTests): the launch costs
// ~40 frames before the bot moves (EruptMinToFire needs 6 f of ChargeDelay + 20 f of
// ramp, then ~12 f of pull before the ball is fast enough to release), after which the
// wave is a ~150–200 px/s conveyor for ~10 tiles. Against a STANDING target that never
// pays: walking in (83 px/s) breaks even only around 16 tiles, and the dash chord
// (DashManeuver) hits from 9–16 tiles in 13–49 frames versus the ride's 84–108. So it
// does not score against a standing target by default (StaticUtility = 0).
//
// Its niche is a target RUNNING AWAY on open ground: walking matches their speed and
// never closes (a fleeing P1 is only caught when they hit the arena wall), while the
// wave outruns them and the bot leaves it airborne, already swinging. The dash closes
// that too and faster, so this scores just under DashManeuver: it's what the bot does
// while the dash is cooling down (90 f) instead of trudging after them.
//
// Input plan:
//   Launch — AvalancheLauncher through our feet cell at a shallow 25°: steeper waves
//            lift the rider 3–4 tiles over a flat floor, and he then sails over the
//            target's head. Short charge (reach barely depends on mass). A faster pull
//            (10 px/f) gets the ball to release speed ~3 frames sooner.
//   Catch  — stand still until TerrainCarriedState picks us up.
//   Ride   — hold toward the target.
//   Strike — inside HopDist (or when the wave gives out): Space + toward, and the first
//            frame the target is inside slash reach a 1-frame LMB tap (SlashLikeAction
//            picks AirSlash/DownAirSlash from the state). HopDist 3 tiles measured best:
//            jumping out from 5–7 tiles overshoots and whiffs (the carried speed is
//            more than a hop needs); at 3 the jump and the tap land on the same frame
//            and connect every time 9–16 tiles out.
//
// Scored only with a clear run: floor under every column to the target and nothing at
// body height (a wall would turn the wave into AvalancheRide's elevator).
public sealed class AvalancheChargeManeuver : BotManeuver
{
    public float MinRange    = 6f * Chunk.TileSize;
    public float MaxRange    = 16f * Chunk.TileSize;
    public float VerticalReach = 3f * Chunk.TileSize;
    // Target speed away from us (px/s) that counts as fleeing. Walk speed is ~83.
    public float FleeSpeed   = 40f;
    public float AngleDeg    = 25f;
    public int   ChargeFrames = 30;   // just over EruptMinToFire (6 f delay + 20 f ramp)
    public float MinBuild    = 30f;

    public float HopDist     = 3f * Chunk.TileSize;
    public int   HopFrames   = 14;
    public float SlashReach  = 42f;   // SlashManeuver.Reach
    public float SlashVertical = 30f;

    public int   CatchBudget = 40;
    public int   RideBudget  = 150;
    public int   AirBudget   = 70;

    public float ChaseUtility  = 1.1f;  // under Dash's 1.2: the dash wins when it's ready
    public float StaticUtility = 0f;    // see header: slower than walking/dash; tests raise it
    public override int Cooldown => _failed ? 180 : 150;
    public override bool OwnsLocomotion => true;

    private enum Phase { Launch, Catch, Ride, Air }
    private Phase _phase;
    private int   _t, _dir;
    private bool  _failed, _swung;
    private readonly AvalancheLauncher _launch = new() { PullSpeed = 10f };
    public float PullSpeed { get => _launch.PullSpeed; set => _launch.PullSpeed = value; }

    // Test visibility.
    public bool Fired, Carried, Jumped, Swung;
    public int  FiredFrame = -1, JumpFrame = -1, SwingFrame = -1;

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        float adx = MathF.Abs(v.Dx);
        if (adx < MinRange || adx > MaxRange || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        if (!v.Target.IsGrounded || !v.LineOfSight()) return 0f;
        bool fleeing = v.TargetVel.X * v.Toward >= FleeSpeed;
        float u = fleeing ? ChaseUtility : StaticUtility;
        if (u <= 0f) return 0f;
        if (v.Self.Abilities.Meters.Build < MinBuild) return 0f;
        if (!ClearRun(v)) return 0f;
        if (!AvalancheLauncher.FindChargePoint(v, AvalancheRideManeuver.FeetOrigin(v),
                AvalancheRideManeuver.WaveDir(v.Toward, AngleDeg), _launch.RunUp, out _)) return 0f;
        return u;
    }

    public override void Begin(BotView v)
    {
        _dir = v.Toward;
        _t = 0; _failed = _swung = false;
        Fired = Carried = Jumped = Swung = false;
        FiredFrame = JumpFrame = SwingFrame = -1;
        _phase = Phase.Launch;
        if (!_launch.Plan(v, AvalancheRideManeuver.FeetOrigin(v),
                          AvalancheRideManeuver.WaveDir(_dir, AngleDeg), ChargeFrames)) _failed = true;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        if (_failed) return false;
        _t++;
        switch (_phase)
        {
            case Phase.Launch:
            {
                var s = _launch.Step(v, ref input);
                if (s == AvalancheLauncher.Status.Failed) return Fail();
                if (s == AvalancheLauncher.Status.Fired) { Fired = true; FiredFrame = v.Frame; Enter(Phase.Catch); }
                return true;
            }
            case Phase.Catch:
                if (Riding(v)) { Carried = true; Enter(Phase.Ride); return true; }
                return _t <= CatchBudget || Fail();

            case Phase.Ride:
            {
                // The target got behind us / walked off: the charge is a miss, stop here
                // rather than ride on past them.
                if (v.Toward != _dir && MathF.Abs(v.Dx) > SlashReach) return Fail();
                BotInput.Move(ref input, _dir);
                if (!Riding(v) || MathF.Abs(v.Dx) <= HopDist || _t > RideBudget)
                { Jumped = true; JumpFrame = v.Frame; Enter(Phase.Air); goto case Phase.Air; }
                return true;
            }
            case Phase.Air:
            default:
            {
                BotInput.Move(ref input, v.Toward);
                if (_t <= HopFrames) input.Space = true;
                if (!_swung && MathF.Abs(v.Dx) <= SlashReach && MathF.Abs(v.Dy) <= SlashVertical)
                {
                    input.LeftClick = true;   // 1-frame tap: a Click → AirSlash
                    _swung = Swung = true; SwingFrame = v.Frame;
                }
                if (_t > 6 && v.Grounded) return false;
                return _t <= AirBudget;
            }
        }
    }

    private void Enter(Phase p) { _phase = p; _t = 0; }
    private bool Fail() { _failed = true; return false; }
    private static bool Riding(BotView v) => v.Self.CurrentStateName == nameof(TerrainCarriedState);

    // Floor under every column to the target and nothing at body height in the way:
    // the wave runs along OUR floor line, and a wall would turn the charge into the
    // elevator AvalancheRide uses.
    private static bool ClearRun(BotView v)
    {
        var me = BotView.Cell(AvalancheRideManeuver.FeetPoint(v));
        int tx = BotView.Cell(v.TargetPos).X, dir = v.Toward;
        for (int x = me.X + dir; (tx - x) * dir > 0; x += dir)
        {
            if (v.Solid(x, me.Y) || v.Solid(x, me.Y - 1)) return false;
            bool floor = false;
            for (int y = me.Y + 1; y < me.Y + 4 && !floor; y++) floor = v.Solid(x, y);
            if (!floor) return false;
        }
        return true;
    }
}
