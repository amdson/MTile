using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Basic plant play for the Bulldog family (Plans/PLANT_SEEDS_PLAN.md §8b): plant a spitter
// when there is time, and swing at an opposing plant in reach when nobody is pressing us.
// Both are low-priority Actions: small bets, gated on "the opponent is not a threat", so
// whenever a fighting maneuver applies it outbids them, and a reflex can preempt them.
// The planter's sticky state (the seed bar) is backstopped by SeedBarReturnConstraint.

// Plant one spitter beside or beneath the body, through the raw gesture a human uses: F
// (to the seed bar), Num1 (spitter), hold RMB on the cell — the right-click crouches the
// player itself (the seed crouch) and the channel runs PlantTuning.SeedChannelFrames — F
// (back).
//
// When: grounded and neutral, the opponent at least MinRange away horizontally and not
// swinging, not hit recently, no live spitter of ours, and SeedPlanting.LegalFromCrouch says
// yes for a floor cell (that covers reach from a crouch, root, occupancy, cap and cost) —
// the same answer the seed-crouch gate and the channel use.
//
// Script, one phase per frame at most (sim state is read back each frame, so every step
// waits for the previous press to land): ToSeedBar -> Select -> Press -> Channel -> Back.
// The opponent closing in or swinging before the plant sends it straight to Back (RMB
// released: the channel is cancelled, the crouch ends, nothing spent). Back keeps pressing F
// on alternate frames until the bar reads "block" again; if it times out, or the controller
// aborts us (hit, preempted), SeedBarReturnConstraint finishes the job on the following
// frames. So whatever happens the bot ends on the block bar.
public sealed class PlantSpitterManeuver : BotManeuver
{
    public float MinRange      = 12f * Chunk.TileSize;   // opponent horizontal gap to start
    public float AbortRange    = 8f * Chunk.TileSize;    // opponent this close mid-script: give up
    public int   QuietFrames   = 60;                     // frames since we were last hit
    public int   TimeoutFrames = 30;                     // ToSeedBar..Press must finish by then
    public int   ChannelSlack  = 20;                     // Channel may overrun the channel by this much
    public int   BackFrames    = 20;                     // then Back gets this long before the constraint takes over
    public int   PreferDx      = 1;                      // preferred site: this many cells toward the opponent

    public override int       Cooldown => 180;
    // Plant from a planted body: no movement keys at all (the RMB itself crouches us; a
    // stray Down from elsewhere could slip us off a ledge).
    public override MoveClaim Move     => MoveClaim.Still;

    private enum Phase { ToSeedBar, Select, Press, Channel, Back }
    private Phase _phase;
    private int   _t, _channelT;
    private Point _cell;
    private int   _spittersBefore;

    // The frame Step last ran on. SeedBarReturnConstraint leaves the seed bar alone only
    // while this equals the current frame (the planter is mid-script this very frame — every
    // frame of the channel's RMB hold included).
    internal int SteppedFrame = int.MinValue;

    // Test visibility.
    public bool  Planted;
    public int   PlantedFrame = -1;
    public Point PlantedCell;

    public override float Score(BotView v) => Applicable(v) && FindSite(v, out _) ? 0.6f : 0f;

    // Low: worth more than idling, well under every ranged/melee attack bet (block throw
    // 0.6/0.07, crystal wave 0.72/0.2, slash ~0.5/0.1).
    public override Bet Bet(BotView v) => Score(v) > 0f ? new Bet(0.3f, 0.05f, 120) : Net.Bet.Pass;

    private bool Applicable(BotView v)
    {
        if (!v.Grounded || MathF.Abs(v.Dx) < MinRange) return false;
        if (Situation.IsAttack(v.TargetAction) || v.FramesSinceHit <= QuietFrames) return false;
        var c = v.Self.Combat;
        if (c.HitstunActive || c.StunActive || c.GrabbedActive) return false;
        string a = v.Self.CurrentActionName;
        if (a != "NullAction" && a != "RecoveryAction") return false;
        return PlantRules.LiveCount(v.Sim.Entities, v.Self.Id, SeedKind.Spitter) == 0;
    }

    private bool Threat(BotView v)
        => v.Dist < AbortRange || Situation.IsAttack(v.TargetAction);

    // Legal from a crouch, standing on a floor (the spitter faces up).
    private static bool Legal(BotView v, Point cell)
    {
        var s   = v.Self;
        var req = SeedPlanting.CrouchRequest(SeedKind.Spitter, s.Id, s.Faction, s.Team, s.Body, v.Sim.Chunks,
                                             cell.X, cell.Y, Vector2.Zero);
        return PlantRules.Evaluate(in req, v.Sim.Chunks, v.Sim.Entities, s.Abilities.Meters, out var site)
               == PlantVerdict.Ok && site.Root == PlantRoot.Below;
    }

    // A legal floor-standing cell, nearest to PreferDx cells toward the opponent (in front,
    // where its shots go), then the shallowest. Pure query.
    private bool FindSite(BotView v, out Point best)
    {
        best = default;
        var me = BotView.Cell(v.Me);
        int bestCost = int.MaxValue;
        for (int dx = -2; dx <= 2; dx++)
        for (int dy = -1; dy <= 3; dy++)
        {
            int cost = Math.Abs(dx - PreferDx * v.Toward) * 4 + Math.Abs(dy);
            if (cost >= bestCost) continue;
            var c = new Point(me.X + dx, me.Y + dy);
            if (!Legal(v, c)) continue;
            best = c; bestCost = cost;
        }
        return bestCost != int.MaxValue;
    }

    // The hold still owes its planting (armed, waiting for the crouch or counting).
    private static bool Armed(BotView v)
        => v.Self.CurrentAction is BlockPlaceAction && v.Self.CurrentActionVars.SeedMode
           && v.Self.CurrentActionVars.SeedArmed;

    public override void Begin(BotView v)
    {
        _t = 0; _channelT = 0;
        Planted = false; PlantedFrame = -1;
        _spittersBefore = PlantRules.LiveCount(v.Sim.Entities, v.Self.Id, SeedKind.Spitter);
        _phase = FindSite(v, out _cell) ? Phase.ToSeedBar : Phase.Back;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        SteppedFrame = v.Frame;
        _t++;
        input.RightClick = false;   // we hold Act: only our own press
        if (_phase < Phase.Back && Threat(v)) _phase = Phase.Back;
        if (_phase < Phase.Channel && _t > TimeoutFrames) _phase = Phase.Back;

        switch (_phase)
        {
            case Phase.ToSeedBar:
                if (!v.Self.SeedBarActive)
                {
                    input.F = !v.Previous.F;   // a press edge: F up on the frame after a press
                    return true;
                }
                _phase = Phase.Select;
                goto case Phase.Select;

            case Phase.Select:
                if (v.Self.SelectedSeed != SeedKind.Spitter) { input.Num1 = true; return true; }
                _phase = Phase.Press;
                goto case Phase.Press;

            case Phase.Press:
                if (v.Previous.RightClick) return true;               // need a press edge
                if (!Legal(v, _cell)) { _phase = Phase.Back; goto case Phase.Back; }
                input.RightClick = true;                              // crouches us and arms the channel
                input.MouseWorldPosition = BotView.CellCenter(_cell);
                _phase = Phase.Channel;
                return true;

            case Phase.Channel:
                _channelT++;
                if (PlantRules.LiveCount(v.Sim.Entities, v.Self.Id, SeedKind.Spitter) > _spittersBefore)
                {
                    Planted = true; PlantedFrame = v.Frame; PlantedCell = _cell;
                    _phase = Phase.Back; goto case Phase.Back;
                }
                // Cancelled (nothing owed any more) or overdue: let go and go home.
                if (!Armed(v) || _channelT > PlantTuning.SeedChannelFrames + ChannelSlack)
                { _phase = Phase.Back; goto case Phase.Back; }
                input.RightClick = true;
                input.MouseWorldPosition = BotView.CellCenter(_cell);
                return true;

            case Phase.Back:
            default:
                _phase = Phase.Back;
                input.RightClick = false;
                if (!v.Self.SeedBarActive) return false;
                input.F = !v.Previous.F;
                return _t <= TimeoutFrames + PlantTuning.SeedChannelFrames + ChannelSlack + BackFrames;
        }
    }

    // Hit or preempted mid-script: RMB up and, if we are on the seed bar, start back now.
    // (If F was down last frame this frame can't make an edge; the constraint presses it next.)
    public override void Abort(BotView v, ref PlayerInput input)
    {
        input.RightClick = false;
        if (v.Self.SeedBarActive && !v.Previous.F) input.F = true;
    }
}

// The "always end on the block bar" guarantee. Every frame, last: if we are on the seed
// bar and the planter did not step this frame, nothing else may act on that bar — RMB
// would plant, Num keys would pick seeds — so RMB and the number row are masked and F is
// pressed (on alternate frames, so each press is an edge) until the bar reads "block".
// Keyed on the planter's SteppedFrame rather than on abort callbacks, so it holds however
// the script ended.
public sealed class SeedBarReturnConstraint : Constraint
{
    private readonly PlantSpitterManeuver _planter;   // may be null: no planter, always return
    public SeedBarReturnConstraint(PlantSpitterManeuver planter) { _planter = planter; }

    public override string Name => "SeedBarReturn";

    public override bool Mask(BotView v, ref PlayerInput input)
    {
        if (!v.Self.SeedBarActive) return false;
        if (_planter != null && _planter.SteppedFrame == v.Frame) return false;
        input.RightClick = false;
        input.Num1 = input.Num2 = input.Num3 = input.Num4 = input.Num5 = false;
        input.F = !v.Previous.F;
        return true;
    }
}

// Spend a few ordinary swings on an opposing plant (BotView.OpposingPlants) close by, while
// the opponent is far enough not to punish it. Walks the last step in, faces the plant and
// taps LMB with the cursor on it — the same 1-frame tap as SlashManeuver, aimed at the
// plant instead of the opponent (the existing attack maneuvers all aim at v.TargetPos, so
// this one carries its own tap). Up to Swings taps SwingGap frames apart; quits as soon
// as the plant is dead or the opponent comes close or swings.
public sealed class PlantStrikeManeuver : BotManeuver
{
    public float SeekRange     = 4f * Chunk.TileSize;   // plant this close horizontally to start
    public float VerticalReach = 2.5f * Chunk.TileSize;
    public float Reach         = 26f;                   // horizontal gap we swing from
    public float SafeRange     = 7f * Chunk.TileSize;   // opponent at least this far
    public int   Swings        = 3;
    public int   SwingGap      = 18;
    public int   MaxFrames     = 120;

    public override int       Cooldown => 30;
    public override MoveClaim Move     => MoveClaim.Exclusive;   // walks the last step in

    private PlantEntity _plant;
    private int _t, _swings, _lastSwing;

    // Test visibility.
    public int SwingsThrown;

    public override float Score(BotView v)
    {
        if (!v.Grounded || Threat(v)) return 0f;
        var c = v.Self.Combat;
        if (c.HitstunActive || c.StunActive || c.GrabbedActive) return 0f;
        return Nearest(v) != null ? 0.6f : 0f;
    }

    // Low, like the planter: a plant is worth a few swings, never more than the opponent.
    public override Bet Bet(BotView v) => Score(v) > 0f ? new Bet(0.3f, 0.1f, 60) : Net.Bet.Pass;

    private bool Threat(BotView v) => v.Dist < SafeRange || Situation.IsAttack(v.TargetAction);

    private PlantEntity Nearest(BotView v)
    {
        PlantEntity best = null;
        float bestD = float.MaxValue;
        foreach (var p in v.OpposingPlants)
        {
            var d = p.Body.Position - v.Me;
            if (MathF.Abs(d.X) > SeekRange || MathF.Abs(d.Y) > VerticalReach) continue;
            float dd = d.LengthSquared();
            if (dd < bestD) { best = p; bestD = dd; }
        }
        return best;
    }

    public override void Begin(BotView v)
    {
        _plant = Nearest(v);
        _t = 0; _swings = 0; _lastSwing = -SwingGap;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        BotInput.Move(ref input, 0);
        if (_plant == null || _plant.IsDead || Threat(v) || _t > MaxFrames) return false;
        if (_swings >= Swings) return _t - _lastSwing < SwingGap;   // let the last swing play out

        var pos = _plant.Body.Position;
        float dx = pos.X - v.Me.X;
        int dir = dx >= 0f ? 1 : -1;
        if (MathF.Abs(dx) > Reach) { BotInput.Move(ref input, dir); return true; }

        if (_t - _lastSwing >= SwingGap && !v.Previous.LeftClick)
        {
            input.LeftClick = true;
            input.MouseWorldPosition = pos;
            if (v.Facing != dir) BotInput.Move(ref input, dir);   // a swing behind the body clamps to vertical
            _swings++; SwingsThrown++;
            _lastSwing = _t;
        }
        return true;
    }
}
