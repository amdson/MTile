using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Rampart's crystal wave (todo.txt #7, World/CrystalWave.cs): spikes selected, RMB held in
// the floor to charge, then a fast release in open air launches a crystal MassBall that
// lays a wave of crystals along whatever floor it skims.
//
// Differs from the stock CrystalWaveManeuver in the launch, which matters a lot: the
// stock skim releases the ball while it is still climbing out of the floor (it lags the
// cursor by the paint ball's 0.12 s smoothing), so the ball flies off upward, leaves its
// support after a few cells and lays ~6 crystals. Here the cursor is lifted to the first
// air row and HELD until the (perceived — it is our own action's ball) paint ball has
// settled there, then pulled straight along the row; RMB comes up once the ball is moving
// fast and level. A level ball skims the floor for its whole leak life (~20 tiles).
//
// Input plan: Pick (Num5) → Charge (RMB in the floor a tile ahead; cursor fixed on the
// press cell, so the spike-mode hold resolves to a charge) → Lift → Skim → release →
// Restore (picker back).
public sealed class RampartCrystalWave : BotManeuver
{
    private readonly RampartSense _s;
    public RampartCrystalWave(RampartSense s) => _s = s;

    public float MinRange      = 9f * Chunk.TileSize;
    public float AbortRange    = 4.5f * Chunk.TileSize;   // they closed in mid-charge: bank it, fight
    public float MaxRange      = 14f * Chunk.TileSize;   // a ~37-crystal wave runs out a little past this
    public float VerticalReach = 2.5f * Chunk.TileSize;
    public int   ChargeFrames  = 50;
    public int   ChargeGrace   = 30;
    public float SkimSpeed     = 7f;     // cursor px/frame along the row
    public float ReleaseSpeed  = 330f;   // ball px/s
    public int   LiftBudget    = 24, SkimBudget = 30;
    public float Utility       = 1.25f;
    public override int  Cooldown       => 45;   // the real gate is the 8 s wave cooldown
    public override bool OwnsLocomotion => true;

    private enum Phase { Pick, Charge, Lift, Skim, Done }
    private Phase    _phase;
    private int      _t, _dir;
    private Vector2  _cursor, _charge;
    private float    _skimY;
    private TileType _origType;
    private bool     _origSpikes;

    public bool Fired;
    public int  FiredFrame = -1;

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        float adx = MathF.Abs(v.Dx);
        if (adx < MinRange || adx > MaxRange || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        if (!v.LineOfSight()) return 0f;
        var m = v.Self.Abilities.Meters;
        if (!m.CrystalWaveReady || m.Build < BuildMeters.EruptMinToFire + 20f) return 0f;
        if (!FindFloor(v, v.Toward, out _, out _)) return 0f;
        if (!FloorRunClear(v, v.Toward, adx)) return 0f;
        return Utility;
    }

    // The skim row must be open (and floored) most of the way to the target, or the
    // ball stops at the first foreign cell / leaves its support.
    private static bool FloorRunClear(BotView v, int dir, float adx)
    {
        var feet = BotView.Cell(v.Me + new Vector2(0f, PlayerCharacter.Radius));
        int g = BarrierPlanner.GroundRow(v, feet.X + dir, feet.Y, 3);
        if (g < 0) return false;
        int n = (int)(adx / Chunk.TileSize);
        for (int k = 2; k <= n; k++)
        {
            int x = feet.X + dir * k;
            if (v.Solid(x, g - 1)) return false;
        }
        return true;
    }

    public override void Begin(BotView v)
    {
        _dir = v.Toward; _t = 0;
        _origType = v.Self.ActiveBlockType;
        _origSpikes = v.Self.SpikesSelected;
        Fired = false; FiredFrame = -1;
        _phase = FindFloor(v, _dir, out float surfaceY, out _charge) ? Phase.Pick : Phase.Done;
        _skimY = surfaceY - Chunk.TileSize * 0.5f;
        _cursor = _charge;
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
                if (!v.Solid(_charge)) return Restore(ref input);
                // Rushed mid-charge: RMB up IN the floor banks it (no wave), melee takes over.
                if (MathF.Abs(v.Dx) < AbortRange) { input.MouseWorldPosition = _charge; return Restore(ref input); }
                input.RightClick = true;
                input.MouseWorldPosition = _charge;
                if (_t >= ChargeFrames)
                {
                    var m = v.Self.Abilities.Meters;
                    if (m.CanFireEruption && v.Self.CurrentActionName == nameof(BlockPaintAction))
                    { _phase = Phase.Lift; _t = 0; _cursor = new Vector2(_charge.X, _skimY); }
                    else if (_t >= ChargeFrames + ChargeGrace) return Restore(ref input);
                }
                return true;
            }
            case Phase.Lift:
            {
                input.RightClick = true;
                input.MouseWorldPosition = _cursor;
                if (v.Self.CurrentActionName != nameof(BlockPaintAction)) return Restore(ref input);
                var av = v.Self.CurrentActionVars;
                bool settled = MathF.Abs(av.BallPos.Y - _skimY) < 1.5f && av.BallVel.Length() < 40f;
                if (settled || _t > LiftBudget) { _phase = Phase.Skim; _t = 0; }
                return true;
            }
            case Phase.Skim:
            {
                _cursor.X += _dir * SkimSpeed;
                input.MouseWorldPosition = _cursor;
                if (v.Self.CurrentActionName != nameof(BlockPaintAction)) return Restore(ref input);
                var av = v.Self.CurrentActionVars;
                bool level = MathF.Abs(av.BallVel.Y) < 12f && MathF.Abs(av.BallPos.Y - _skimY) < 2.5f;
                if (av.BallVel.Length() >= ReleaseSpeed && level && !v.Solid(_cursor) && !v.Solid(av.BallPos))
                {
                    input.RightClick = false;   // RMB up while moving fast: launch
                    Fired = true; FiredFrame = v.Frame;
                    _phase = Phase.Done;
                    return true;
                }
                if (_t > SkimBudget || v.Solid(_cursor)) { input.MouseWorldPosition = _charge; return Restore(ref input, keepCursor: true); }
                input.RightClick = true;
                return true;
            }
            default:
                return Restore(ref input);
        }
    }

    public override void Abort(BotView v, ref PlayerInput input) => Restore(ref input);

    private bool Restore(ref PlayerInput input, bool keepCursor = false)
    {
        input.RightClick = false;
        if (_origSpikes) input.Num5 = true;
        else             BarrierPlanner.PressPicker(ref input, _origType);
        return false;
    }

    // The floor one tile toward `dir`, its top edge, and a charge point buried a row into it.
    internal static bool FindFloor(BotView v, int dir, out float surfaceY, out Vector2 chargePoint)
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
