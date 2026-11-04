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

    public static BulldogBot Build(int seed, int self, out RouteManeuver route, bool learn = true, float takenWeight = 0f)
    {
        route = new RouteManeuver();
        var b = new BulldogBot(seed, self, new BotManeuver[] { route });
        b.Disabled.Add(nameof(WallClimbManeuver));
        if (learn) b.Learn = new ManeuverWeights { TakenWeight = takenWeight };
        return b;
    }
}
