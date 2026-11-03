using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Terrain as a projectile: the block eruption, played the way a human plays it.
//
// There is no "eruption button". The eruption is a release-time upgrade of a plain RMB
// stroke (BlockPaintAction — read its header): a hold whose cursor starts INSIDE solid
// banks eruption charge (BuildMeters.StepCharging), and if RMB comes up while the
// critically-damped paint ball is moving faster than EruptReleaseSpeed (220 px/s) with
// at least EruptMinToFire charge banked, Exit detaches the ball as a free-flying MassBall
// that keeps its release velocity and leaks build mass into TileMassField as it coasts.
// So the input plan is three phases:
//
//   Charge — RMB held, cursor buried in the floor a tile in front of the bot.
//   Sweep  — RMB still held, cursor dragged up out of the floor to the first air row,
//            then whipped along the surface toward the target. The ball lags the cursor
//            (SmoothTime 0.12 s), so the sweep has to last long enough for the BALL to
//            get up to speed, not just the cursor.
//   Release— RMB up mid-sweep. The ball flies on along the floor at the target.
//
// Why skim the surface instead of aiming the cursor at the target's chest: the ball
// ignores tiles and gravity, and the mass it leaks only commits where it has support
// to sprout from. A ball flung through open air outruns its own scaffolding and its
// mass dies (EruptionPillarTests documents this). Dragged along the first air row, every
// cell it crosses sits on the floor, so it raises a ridge that runs out under the
// target — the growing sprouts are what actually hit them (shove/lift and the crush
// channel), not the ball, which publishes no hitbox at all.
//
// The meters are the real limiter: Charge pays out of the Build reservoir, so Score
// refuses when Build can't fund a firing charge, and a charge that still hasn't reached
// CanFireEruption after the max hold is released IN the floor, which by design banks /
// retires the charge instead of erupting (no wasted ball, no eruption from inside rock).
public sealed class EruptionManeuver : BotManeuver
{
    // Engagement band (horizontal gap, px). Inside MinRange the slash is the better
    // tool and the ridge would sprout under our own feet; past MaxRange the ball has
    // leaked most of its mass before it gets there (it spends itself in ~1 s).
    public float MinRange      = 4f * Chunk.TileSize;
    public float MaxRange      = 11f * Chunk.TileSize;
    // The ridge runs along OUR floor line, so the target must be standing roughly on it.
    public float VerticalReach = 2.5f * Chunk.TileSize;

    // Charge hold (frames). ChargeDelay (6 f) is dead time, then the ramp banks 2 units/f,
    // so ~26 f is the bare EruptMinToFire; more charge is more mass = a longer ridge.
    public int ChargeMin    = 45;
    public int ChargeMax    = 70;
    // Extra frames we'll keep holding past the plan if the meter is still short.
    public int ChargeGrace  = 30;
    // Cursor speed during the sweep (px/frame). 20 px/f is the human gesture
    // EruptionPillarTests drives; the damped ball reaches ~400 px/s after 6 frames.
    public float SweepSpeed  = 22f;
    public int   SkimFrames  = 7;

    public float Utility       = 1.3f;
    public float ConfinedBonus = 0.5f;
    public int   CooldownFrames = 150;
    public override int  Cooldown       => CooldownFrames;
    // Standing still is part of the gesture: a walking bot drags its charge point and
    // can walk off the floor it's biting into.
    public override bool OwnsLocomotion => true;

    private enum Phase { Charge, Rise, Skim }
    private Phase   _phase;
    private int     _t;           // frames in the current phase
    private int     _chargeFrames;
    private int     _dir;         // ±1, toward the target, fixed at Begin
    private Vector2 _cursor;
    private float   _surfaceY;    // world y of the floor's top edge under the charge point
    private float   _skimY;       // centre of the first air row above it

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        float adx = MathF.Abs(v.Dx);
        if (adx < MinRange || adx > MaxRange || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        if (!v.LineOfSight()) return 0f;
        var m = v.Self.Abilities.Meters;
        // Charge buys 2 units per reservoir unit; ~half the hold's charge must be funded.
        if (m.Build < BuildMeters.EruptMinToFire) return 0f;
        if (!FindFloor(v, v.Toward, out _, out _)) return 0f;
        // Ramps with range: the farther out, the more this beats walking in to slash.
        float u = Utility * (0.75f + 0.25f * (adx - MinRange) / (MaxRange - MinRange));
        // In the open the ridge only lifts/shoves the target; under a low ceiling it
        // wedges them and the sprouts crush (0.5 HP each, see BotTerrainSense).
        return BotTerrainSense.Confined(v, v.TargetPos) ? u + ConfinedBonus : u;
    }

    public override void Begin(BotView v)
    {
        _dir   = v.Toward;
        _phase = Phase.Charge;
        _t     = 0;
        _chargeFrames = v.Rng.Next(ChargeMin, ChargeMax + 1);
        FindFloor(v, _dir, out _surfaceY, out _cursor);
        _skimY = _surfaceY - Chunk.TileSize * 0.5f;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        switch (_phase)
        {
            case Phase.Charge:
            {
                var m = v.Self.Abilities.Meters;
                // The floor we're biting into got carved out from under the cursor: the
                // hold has silently become a paint stroke. Nothing to salvage.
                if (!v.Solid(_cursor)) return Release(ref input);
                input.RightClick = true;
                input.MouseWorldPosition = _cursor;
                if (_t >= _chargeFrames)
                {
                    if (m.CanFireEruption) { _phase = Phase.Rise; _t = 0; }
                    // Meter short (reservoir drained): give it a little longer, then
                    // release IN the floor — Exit banks the charge, no eruption.
                    else if (_t >= _chargeFrames + ChargeGrace) return Release(ref input);
                }
                return true;
            }
            case Phase.Rise:
            {
                // Straight up to the skim row; one frame at most can be in the air early.
                _cursor.Y = MathF.Max(_skimY, _cursor.Y - SweepSpeed);
                if (_cursor.Y <= _skimY) { _phase = Phase.Skim; _t = 0; }
                input.RightClick = true;
                input.MouseWorldPosition = _cursor;
                return true;
            }
            default: // Skim
            {
                _cursor.X += _dir * SweepSpeed;
                input.MouseWorldPosition = _cursor;
                if (_t >= SkimFrames) return Release(ref input);   // RMB up while moving
                input.RightClick = true;
                return true;
            }
        }
    }

    // RMB up this frame (the cursor keeps whatever it was set to — for the skim that's
    // the next point along the line, so Exit's in-solid test sees open air).
    private bool Release(ref PlayerInput input)
    {
        input.RightClick = false;
        return false;
    }

    // Find the floor one tile toward `dir`: the first solid cell scanning down from the
    // bot's feet. Returns its top edge and a charge point two rows into it (deep enough
    // that the cursor stays buried if the surface cell gets chipped).
    private static bool FindFloor(BotView v, int dir, out float surfaceY, out Vector2 chargePoint)
    {
        var feet = BotView.Cell(v.Me + new Vector2(dir * Chunk.TileSize, PlayerCharacter.Radius));
        for (int dy = 0; dy < 4; dy++)
        {
            var c = new Point(feet.X, feet.Y + dy);
            if (!v.Solid(c)) continue;
            // Need the cell below too, so the charge point is genuinely buried.
            var deep = new Point(c.X, c.Y + 1);
            surfaceY    = c.Y * Chunk.TileSize;
            chargePoint = BotView.CellCenter(v.Solid(deep) ? deep : c);
            // The skim row must be open air or the sweep never leaves the solid.
            return !v.Solid(c.X, c.Y - 1);
        }
        surfaceY = 0f; chargePoint = default;
        return false;
    }
}
