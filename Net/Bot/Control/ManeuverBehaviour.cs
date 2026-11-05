using System;

namespace MTile.Net;

// Migration adapter (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, "Adapter"): runs an existing
// BotManeuver under BotController unchanged. Category and Move claim come from
// the maneuver's own Category/Move overrides (default: Action; Exclusive if OwnsLocomotion
// else None); the bet is the
// maneuver's Bet (default 0.5 * Score HP over 90 frames).
// Name is the wrapped maneuver's Name, so diagnostics keep today's names.
public sealed class ManeuverBehaviour : BotBehaviour
{
    public const float ScoreToHp  = 0.5f;
    public const int   BetHorizon = 90;

    public readonly BotManeuver Maneuver;
    private readonly Category  _category;
    private readonly MoveClaim _move;

    public ManeuverBehaviour(BotManeuver m)
    {
        Maneuver = m ?? throw new ArgumentNullException(nameof(m));
        _category = m.Category;
        _move     = m.Move;
    }

    public override string    Name     => Maneuver.Name;
    public override Category  Category => _category;
    public override MoveClaim Move     => _move;
    public override int       Cooldown => Maneuver.Cooldown;

    public override Bet Bet(BotView v) => Maneuver.Bet(v);

    public override void Begin(BotView v) => Maneuver.Begin(v);
    public override bool Step(BotView v, ref PlayerInput input) => Maneuver.Step(v, ref input);
    public override void Aborted(BotView v, ref PlayerInput input) => Maneuver.Abort(v, ref input);
}
