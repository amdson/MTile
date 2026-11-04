using System.Collections.Generic;

namespace MTile.Net;

// Ranger: Bulldog's melee plus path-planned routing (Plans/BOT_ROUTING_PLAN.md) — the
// bot that follows you up a column, across a pit, or down and around. Built on Bulldog
// (like Harrier) so an A/B against plain Bulldog isolates the router. Bulldog's own
// WallClimbManeuver is switched off: the router plans climbs itself, with the same
// controller, as one edge type among the rest.
public static class RangerBot
{
    // Default: with multiplicative-weights move selection (ManeuverWeights), rewarded on
    // damage dealt only — benchmarked best (ZzzRangerEval, seeds 1-10 vs the field: 200.5
    // /300 vs 182 for dealt − taken and 177.5 without learning). NoLearn is the plain
    // highest-score brain, for A/B.
    public static readonly DuelBot Create  = (self, seed) => Build(seed, self, out _);
    public static readonly DuelBot NoLearn = (self, seed) => Build(seed, self, out _, learn: false);

    // With the learned tactics (RangerTactics.cs): approach / after-swing / defend choices.
    // Opt-in, not the default: against the field (ZzzRangerEval, seeds 1-10) it scored
    // 176.5/300 vs 200.5 for plain Ranger — the learner kept choosing "do what Bulldog
    // does" (its weight grew 5-10x the alternatives), so exploring hold / feint / step-out
    // / counter was pure cost against bots that don't adapt.
    // Each decision's option 0 defers to Bulldog's own behaviour, so Bulldog's dash and
    // guards stay on.
    public static readonly DuelBot Tactics = (self, seed) => Build(seed, self, out _, tactics: true);

    // Without the anti-air moves, for A/B.
    public static readonly DuelBot NoAntiAir = (self, seed) => Build(seed, self, out _, antiAir: false);

    // antiAir: three answers to an opponent overhead (the jump-over-and-pogo a human beats
    // Bulldog with) — hop up and air-slash, step out from under and punish the landing, or
    // stab straight up. They overlap on purpose: the move-level learner (ManeuverWeights)
    // picks whichever is paying off against this opponent.
    public static BulldogBot Build(int seed, int self, out RouteManeuver route, bool learn = true, float takenWeight = 0f,
                                   bool tactics = false, bool antiAir = true)
    {
        route = new RouteManeuver();
        var extra = new List<BotManeuver> { route };
        if (antiAir)
        {
            extra.Add(new AntiAirJumpSlashManeuver());
            extra.Add(new AntiAirSidestepManeuver());
            extra.Add(new AntiAirStabManeuver());
        }
        TacticBandit bandit = null;
        if (tactics)
        {
            bandit = new TacticBandit();
            extra.Add(new ApproachTactic(bandit));
            extra.Add(new AfterSwingTactic(bandit));
            extra.Add(new DefendTactic(bandit));
        }
        var b = new BulldogBot(seed, self, extra);
        if (tactics)
        {
            b.PerFrame = bandit.Tick;
        }
        b.Disabled.Add(nameof(WallClimbManeuver));
        if (learn) b.Learn = new ManeuverWeights { TakenWeight = takenWeight };
        return b;
    }
}
