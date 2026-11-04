namespace MTile.Net;

// Ranger: Bulldog's melee plus path-planned routing (Plans/BOT_ROUTING_PLAN.md) — the
// bot that follows you up a column, across a pit, or down and around. Built on Bulldog
// (like Harrier) so an A/B against plain Bulldog isolates the router. Bulldog's own
// WallClimbManeuver is switched off: the router plans climbs itself, with the same
// controller, as one edge type among the rest.
public static class RangerBot
{
    public static readonly DuelBot Create = (self, seed) => Build(seed, self, out _);

    public static BulldogBot Build(int seed, int self, out RouteManeuver route)
    {
        route = new RouteManeuver();
        var b = new BulldogBot(seed, self, new BotManeuver[] { route });
        b.Disabled.Add(nameof(WallClimbManeuver));
        return b;
    }
}
