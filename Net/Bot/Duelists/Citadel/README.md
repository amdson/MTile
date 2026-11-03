# Citadel

An opt-in P2 duelist combining Rampart's fort, spikes, level crystal waves,
wall defense and anti-stall advance with Bulldog's hit-confirmed strings,
low pokes, guard grabs, combo breaker and dash punish. Also climbs walls and
attacks downward from the air. Existing agents and gameplay are unchanged.
The independent brain is adapted from RampartBot; maneuver implementations
are reused, with separate per-agent instances and perception state.

Entry point: `CitadelBot.Create(0, seed)` or `new CitadelBot(seed, 0)` for P2;
use index -1 for P1. Implements `IRemoteInputSource`. To play against it offline,
set `"BotOpponent": "Citadel"` in `configs/game_config.json` and use a stage with
a non-idle second player (or enable `SpawnSecondPlayer`). Restart the game after
changing the config.

Opponent observations are delayed eight frames (initial frames use the oldest
available sample); health is HUD-visible. All actions are raw player inputs,
randomness is seeded, and a quiet fight triggers an advance after five seconds.

## Measured results

60-second bouts, seeds 1 and 2, all three BotDuel arenas, both sides:

| Opponent | Wins | Draws | Losses |
| --- | ---: | ---: | ---: |
| Brawler | 12 | 0 | 0 |
| Bulldog | 5 | 1 | 6 |
| Riposte | 8 | 0 | 4 |
| Rampart | 5 | 1 | 6 |

Total: 30–2–16. Competitive hybrid, not a universal improvement: Bulldog and
Rampart narrowly outpoint it. This is a small evaluation sample, not a held-out
league. Results reflect the working tree during concurrent terrain development.
Deterministic repeat-bout check passes. Benchmark tests report results and check
finite damage; they deliberately do not assert victories against every agent.

Run:

```sh
dotnet test MTile.Tests/MTile.Tests.csproj --filter FullyQualifiedName~BotManeuverDuelistCitadelTests --logger 'console;verbosity=detailed'
```
