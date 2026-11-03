using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// The spiked avalanche (todo.txt #7, World/CrystalWave.cs), played the way a human plays
// it. It is the eruption gesture with spikes selected, so the input plan is
// EruptionManeuver's skim, plus picking spikes first:
//
//   Pick   — Num5 (spikes) for one frame, unless already selected.
//   Charge — RMB held with the cursor buried in the floor a tile in front of us. With
//            spikes selected the hold first sits Undecided for SpikeChargeDelay (the
//            cursor must not leave the press cell, or it becomes a spike stroke), then
//            banks eruption charge like the painter's.
//   Rise   — cursor straight up to the first air row.
//   Skim   — whipped along the floor toward the target; RMB up mid-skim, so the ball is
//            moving fast and the cursor is in open air: BlockPaintAction's spike mode
//            launches a crystal MassBall that skims the floor line.
//   Restore— the original picker key, then done.
//
// Why the skim rather than AvalancheLauncher's straight line: a crystal ball, like a build
// ball, only commits where its mass reaches support, and a rising line (the launcher
// needs one to find a buried charge point) carries it off the floor within a few tiles —
// measured: a 10° launch laid 10 crystals; the skim lays the full run.
//
// When (Score): grounded, the target at mid range on roughly our floor line with line of
// sight, the wave off cooldown (BuildMeters.CrystalWaveReady), and enough reservoir for a
// firing charge. Mid range because the warning is long: up close the slash is faster and
// the target simply steps over the seeds; past ~18 tiles the wave's ~25-cell run gives
// out before it gets there.
//
// And only against a target that is keeping its distance — moving away, or standing off
// charging a ranged attack / guarding. Against a target that stands or approaches,
// walking in is faster than the wave: measured in BotManeuverBrainTests'
// AFK duel, opening with the wave from 18 tiles lands the first hit at frame 242 versus
// 175 walking in (the 1 s warning plus the ~0.4 s run). So StaticUtility defaults to 0,
// like AvalancheChargeManeuver's; tests raise it.
public sealed class RampartCrystalWave : BotManeuver
{
    private readonly RampartSense _s;
    public RampartCrystalWave(RampartSense s) => _s = s;

    public float MinRange      = 6f * Chunk.TileSize;
    public float MaxRange      = 18f * Chunk.TileSize;
    public float VerticalReach = 2.5f * Chunk.TileSize;
    public int   ChargeFrames  = 60;   // 9 f undecided + ~50 f of ramp ≈ 100 units ≈ 50 crystals
    public int   ChargeGrace   = 30;
    public float SweepSpeed    = 22f;  // px/frame, as EruptionManeuver
    public int   SkimFrames    = 7;
    public float ZoningUtility = 1.15f;  // target retreating / charging a ranged shot / guarding
    public float StaticUtility = 0f;     // anything else (see header)
    public float RetreatSpeed  = 25f;    // px/s away from us that counts as keeping distance

    private static readonly System.Collections.Generic.HashSet<string> StandOff =
        new() { "BeamAction", "LobbedAreaAction", "GuardAction" };
    public override int  Cooldown       => 60;   // the real gate is the 8 s wave cooldown
    public override bool OwnsLocomotion => true;

    private enum Phase { Pick, Charge, Rise, Skim, Done }
    private Phase    _phase;
    private int      _t, _dir;
    private Vector2  _cursor;
    private float    _skimY;
    private TileType _origType;
    private bool     _origSpikes;

    // Test visibility.
    public bool Fired;
    public int  FiredFrame = -1;

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        float adx = MathF.Abs(v.Dx);
        if (adx < MinRange || adx > MaxRange || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        if (!v.LineOfSight()) return 0f;
        var m = v.Self.Abilities.Meters;
        if (!m.CrystalWaveReady || m.Build < BuildMeters.EruptMinToFire) return 0f;
        bool zoning = v.TargetVel.X * v.Toward >= RetreatSpeed || StandOff.Contains(v.TargetAction);
        float u = zoning ? ZoningUtility : StaticUtility;
        if (u <= 0f) return 0f;
        if (!FindFloor(v, v.Toward, out _, out _)) return 0f;
        return u;
    }

    public override void Begin(BotView v)
    {
        _dir        = v.Toward;
        _t          = 0;
        _origType   = v.Self.ActiveBlockType;
        _origSpikes = v.Self.SpikesSelected;
        Fired = false; FiredFrame = -1;
        _phase = FindFloor(v, _dir, out float surfaceY, out _cursor) ? Phase.Pick : Phase.Done;
        _skimY = surfaceY - Chunk.TileSize * 0.5f;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        switch (_phase)
        {
            case Phase.Pick:
                _phase = Phase.Charge; _t = 0;
                if (!v.Self.SpikesSelected) { input.Num5 = true; return true; }
                goto case Phase.Charge;

            case Phase.Charge:
            {
                if (!v.Solid(_cursor)) return Restore(ref input);   // charge point carved away
                input.RightClick = true;
                input.MouseWorldPosition = _cursor;
                if (_t >= ChargeFrames)
                {
                    var m = v.Self.Abilities.Meters;
                    if (m.CanFireEruption) { _phase = Phase.Rise; _t = 0; }
                    // Short meter: let go IN the floor (banks/retires, no wave).
                    else if (_t >= ChargeFrames + ChargeGrace) return Restore(ref input);
                }
                return true;
            }
            case Phase.Rise:
                _cursor.Y = MathF.Max(_skimY, _cursor.Y - SweepSpeed);
                if (_cursor.Y <= _skimY) { _phase = Phase.Skim; _t = 0; }
                input.RightClick = true;
                input.MouseWorldPosition = _cursor;
                return true;

            case Phase.Skim:
                _cursor.X += _dir * SweepSpeed;
                input.MouseWorldPosition = _cursor;
                if (_t >= SkimFrames)
                {
                    input.RightClick = false;   // RMB up while moving: launch
                    Fired = true; FiredFrame = v.Frame;
                    _phase = Phase.Done;
                    return true;
                }
                input.RightClick = true;
                return true;

            default:
                return Restore(ref input);
        }
    }

    public override void Abort(BotView v, ref PlayerInput input) => Restore(ref input);

    // RMB up, picker back to what we had (spikes is a persistent per-player setting).
    private bool Restore(ref PlayerInput input)
    {
        input.RightClick = false;
        if (_origSpikes) input.Num5 = true;
        else             BarrierPlanner.PressPicker(ref input, _origType);
        return false;
    }

    // EruptionManeuver.FindFloor: the floor one tile toward `dir`, its top edge, and a
    // charge point buried a row into it; the skim row above must be open.
    private static bool FindFloor(BotView v, int dir, out float surfaceY, out Vector2 chargePoint)
    {
        var feet = BotView.Cell(v.Me + new Vector2(dir * Chunk.TileSize, PlayerCharacter.Radius));
        for (int dy = 0; dy < 4; dy++)
        {
            var c = new Point(feet.X, feet.Y + dy);
            if (!v.Solid(c)) continue;
            var deep = new Point(c.X, c.Y + 1);
            surfaceY    = c.Y * Chunk.TileSize;
            chargePoint = BotView.CellCenter(v.Solid(deep) ? deep : c);
            return !v.Solid(c.X, c.Y - 1);
        }
        surfaceY = 0f; chargePoint = default;
        return false;
    }
}
