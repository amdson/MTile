namespace MTile.Net;

// Harrier: Bulldog with one extra maneuver, WhiffPunishManeuver — the hit-and-run
// counter experiment (Plans/PLAYER_AGENTS.md §6). Built on Bulldog so the A/B against
// plain Bulldog isolates exactly that maneuver.
public static class HarrierBot
{
    public static readonly DuelBot Create = (self, seed) => Build(seed, self, out _);

    public static BulldogBot Build(int seed, int self, out WhiffPunishManeuver punish)
    {
        punish = new WhiffPunishManeuver();
        return new BulldogBot(seed, self, new BotManeuver[] { punish });
    }
}
