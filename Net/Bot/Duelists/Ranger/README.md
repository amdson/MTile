# Ranger

Bulldog's melee plus path-planned routing (`Net/Bot/Routing`, `Plans/BOT_ROUTING_PLAN.md`):
the duelist that follows you when you run — up the column you stacked, across a pit, down
and around a shelf, onto a floating platform.

- **Brain:** `BulldogBot` with one extra maneuver, `RouteManeuver`, and Bulldog's own
  `WallClimbManeuver` switched off (the router plans climbs itself, with the same
  controller). Everything else — strings, punishes, grabs, guard, breach — is Bulldog's.
- **Router:** A* over stand nodes. Edges are *measured*, not modelled: a jump / drop /
  walk-off table flown headlessly (`JumpTable`, `ZzzRouteCalibration`), wall climbs (any
  face height, 1-wide pillars included), and build-ups (stack a column under your feet,
  `BuildClimbManeuver`'s elevator). `RouteFollower` replays each edge's calibrated
  gesture closed-loop and replans on failure.
- **When it routes:** only when the route needs a climb or a build. Otherwise it is
  plain Bulldog.
- **Learning:** move choice is a weighted draw, P ∝ (score × weight)^3, with
  multiplicative-weights updates per move (`Net/Bot/ManeuverWeights.cs`): w ← w·e^(0.7·r),
  r = HP dealt in the 1.5 s after the move starts, w clamped to [0.1, 10]. Against the
  field (seeds 1–10, all arenas, both sides): 200.5/300 with dealt-only reward, 182 with
  dealt − taken, 177.5 with no learning. It learns to lean on Bulldog's strings.
- **Perception:** the opponent through Bulldog's 6-frame delay line; terrain live.

Play it: `"BotOpponent": "Ranger"` in `configs/game_config.json`.

Known limits: against a camper who keeps stacking, climbing and stacking run at the same
~6 frames per cell, so it is a race the camper wins until their build budget or height
cap stops them, and the camper wins the exchange at the lip. No dig edges (Bulldog's
reactive breach covers sealed targets).
