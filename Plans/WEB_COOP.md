# Web co-op: depths in the browser, with a level selector

Goal: two browser players pick a level in the lobby (host picks, joiner sees it) and play
it over the existing WebRTC rollback session. First target: the `depths` co-op gauntlet.

## Already true (2026-10-17)

- Peers exchange no settings: both load the same `configs/game_config.json`, which already
  says `"Stage": "depths"`. The networked path builds `Simulation(config, stage)`, forces a
  second player, calls `Stage.Populate`, and honours `Stage.Coop` (no friendly fire,
  checkpoint respawn for both).
- Wire traffic is inputs only (`Net/InputPacket.cs`); the checksum covers terrain.
- No round/stock/win layer in the core loop to fight a co-op stage.
- `EnemyEntity` picks targets through the team target source; Stalker and Turret predate it.

## Steps

1. **Stage override.** `Game1` takes an optional stage name that wins over `_config.Stage`
   (today `Game1.cs` reads only the config). Solo gets a dropdown.
2. **Settings handshake.** Host chooses. Stage name + build id ride the offer (Firestore
   room doc and the manual blob) so the joiner knows the level before building Game1; after
   the data channel opens, a resend-until-ack `hello {stage, buildId}` gates ticking on both
   sides. Mismatched build ids show "different versions" instead of desyncing.
3. **Lobby selector.** Curated list (Depths co-op, PvP arena, Gauntlet, Training) with a
   co-op/versus tag; co-op wording instead of "opponent".
4. **Depths co-op polish.** Explicit second spawn per stage (P2 is a blind +96 px today);
   Stalker/Turret target the nearest living player; HUD with room name / wave / door open /
   victory; build the depths map once at boot instead of three times.
5. **Determinism under rollback.** Headless two-sim depths run with scripted inputs and
   forced rollback+replay, checksums compared, through nest and boss rooms.
6. **Browser perf.** Bench the worst depths room headless (`MTile.Bench`), then in the AOT
   build; cap nest k in netplay or trim late waves if replay blows the budget.
7. **Deploy.** `pwsh scripts/publish-web.ps1`, extend Playwright smoke for the handshake,
   one two-machine run.

Also in this pass: a mid-match **disconnect overlay** (INTERNET_READY_PLAN Phase 4).

**Status (2026-10-17, branch web-coop-lobby):** steps 1-3 and the disconnect overlay are
in. `Game1.StageOverride`; `LobbyStages.cs` (curated list, guarded by
`LobbyStageCatalogTests`); `LobbyHello.cs` (0xFE-tagged hello/ack, build id = MVID of
the game assembly); the offer blob carries `mtile:{stage,build}` so the Firestore schema
and rules are unchanged. `smoke/pvp_move.py` covers stage pick, version refusal and the
disconnect overlay.

## Pre-release bugs

- **Slow fall beside a stalactite.** Rarely, a player holding right while falling below or
  beside a stalactite falls much slower than in open air — likely a holding/cling channel
  (wall slide, ledge hang, ceiling contact, corrector hover) activating on the spike's
  tapered faces when it shouldn't.

## Inherited, not in this pass

TURN (STUN-only today), desync recovery, background-tab throttling. See
`Plans/INTERNET_READY_PLAN.md`.
