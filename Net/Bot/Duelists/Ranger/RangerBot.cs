using System.Collections.Generic;

namespace MTile.Net;

// Ranger: Bulldog's melee plus path-planned routing (Plans/BOT_ROUTING_PLAN.md) — the
// bot that follows you up a column, across a pit, or down and around. Built on Bulldog
// (like Harrier) so an A/B against plain Bulldog isolates the router. The RouteManeuver
// sits behind the controller's MoveExecutor as its planner (RoutePlanner), not as a
// maneuver. Bulldog's own WallClimbManeuver is switched off: the router plans climbs
// itself, with the same controller, as one edge type among the rest.
public static class RangerBot
{
    // Default: the controller's bet ledger (BetLedger) with softmax selection, valued on net
    // HP (Lambda at the controller default). NoLearn is the plain highest-bet brain, for A/B.
    // Earlier learner results: see git history.
    public static readonly DuelBot Create  = (self, seed) => Build(seed, self, out _);
    public static readonly DuelBot NoLearn = (self, seed) => Build(seed, self, out _, learn: false);

    // Diagnostics for the learner A/B (ZzzLearningEffect): learned argmax without softmax
    // noise; softmax noise without a ledger; and a ledger carried across bouts.
    public static readonly DuelBot LearnArgmax    = (self, seed) => { var b = Build(seed, self, out _); b.ExploreOverride = false; return b; };
    public static readonly DuelBot NoLearnExplore = (self, seed) => { var b = Build(seed, self, out _, learn: false); b.ExploreOverride = true; return b; };
    public static DuelBot Carry(BetLedger shared) => (self, seed) =>
    {
        var b = Build(seed, self, out _, learn: false);
        shared.NewBout();
        b.Ledger = shared;
        return b;
    };

    // Without the anti-air moves, for A/B.
    public static readonly DuelBot NoAntiAir = (self, seed) => Build(seed, self, out _, antiAir: false);

    // antiAir: three answers to an opponent overhead (the jump-over-and-pogo a human beats
    // Bulldog with) — hop up and air-slash, step out from under and punish the landing, or
    // stab straight up. They overlap on purpose: the bet ledger corrects whichever is
    // paying off against this opponent.
    public static BulldogBot Build(int seed, int self, out RouteManeuver route, bool learn = true,
                                   bool antiAir = true)
    {
        route = new RouteManeuver();
        var extra = new List<BotManeuver>();
        extra.Add(new WhiffPunishManeuver());        // Harrier's: dash in on an opponent's missed swing
        extra.Add(new RangerBlockThrowManeuver());   // ranged pressure: an aimed clod at 60-280 px
        extra.Add(new RangerCrystalWaveManeuver());  // ranged pressure: spiked wave at 6-14 tiles
        extra.Add(new CornerEscapeManeuver());       // back to a wall: dash past the opponent
        if (antiAir)
        {
            extra.Add(new AntiAirJumpSlashManeuver());
            extra.Add(new AntiAirSidestepManeuver());
            extra.Add(new AntiAirStabManeuver());
        }
        var b = new BulldogBot(seed, self, extra) { Planner = new RoutePlanner(route) };
        b.ExtraStances.Add(new HoldRangeStance());   // a second Move choice for the learner
        b.Disabled.Add(nameof(WallClimbManeuver));
        if (learn) b.Ledger = new BetLedger();
        return b;
    }
}
