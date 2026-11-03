# todo.txt items 2–8 — campaign plan

Branch `todo-campaign` (off `bot-ai`). Source of truth for the items: `todo.txt` (numbers
there). Order: 3 → 2a/2b/5 → 6 → 8 → 4 (+ non-rider crush) → 7 (+ bot hookup).
After the campaign the user wants to discuss a shape/size dial for avalanches.

Baseline: `scripts/test-group.py combat terrain bot` has 40 pre-existing reds at campaign
start (BACKLOG §5 known-failing + §5.0 clip-stub `SlashComboPresentationTests`). A red
not in that set is ours. List: scratchpad `baseline_fail.txt`; class names below.
`ActionOverlay CombatHitstun CorridorProbe DirectDamage FighterBlockAction Grab HitEviction
LatticePathPlanner LedgeGrabFlicker LedgePullExit LedgeRegrabDrift PlayerImpactByVelocity
RecoveryTransition SlashComboPresentation SproutCrush SproutGraph SproutLiftJump`.
Hard rule from the user: do NOT touch `TerrainCarriedState` / avalanche riding.

## Items

- [x] **3** Burst scales with charging / fires out of a held charge — user's work, committed @a09468e.
- [x] **2a** Faster sprouting + continuous front: completed-cell leftover progress carries
  into the next cell (an overshoot carry already exists in `ChunkMap.TickSprouts` ~548 —
  verify it holds at the new speeds, incl. the schedule-gate due-scan path).
- [x] **2b** Per-source sprout lifetime dials (MovementConfig, hot-reloadable):
  avalanche 0.04 s, normal paint 0.07 s, Shift placement 0.10 s (user-chosen 2026-11-02).
- [x] **5** Build reach effectively infinite when chaining from a sprouting cell
  (`ChainReachMul` in BlockPaintAction + BlockPlaceAction).
- [x] **6** @a07d9df (merged) More distinct sprout visuals (render-only; screenshot for the user's review).
- [x] **9** (added 2026-11-03) Slow movement 25%: horizontal x0.75, gravity untouched,
  jump HEIGHT x0.75 (launch + hold x0.875), dash + stab lunge + knockback-into-players
  x0.75. Measured in sim: jump rise 44.7→33.1 px (74%), walk 81.9→60.5 px/s (74%).
  User: let speed-calibrated tests go red for now.
- [x] **8** @f729a46 (merged) Spike placement crystal lifecycle: a crystal seed appears on top of the block,
  waits ~1 s, flashes, grows into the existing (1-cell overlay) spike. Phases are sim
  state (snapshotted); visuals render-only. Shares the phase machine with 7.
- [x] **4** @2f47da3 (merged) MassBall stops on entering solid it didn't create (pre-existing or anyone's
  barricade), dumping remaining mass at the face; ball remembers its own wave's cells.
  Plus: crush damage for NON-riders pinned between a wave and a wall should be
  significant. Watch `BotManeuverAvalancheRideTests` (rides gain height off cliffs).
- [x] **7** @ab13cca (merged) Spiked avalanche (crystal wave) — see decisions below and todo.txt #7.
  Then bot hookup: WallUpManeuver threat check + a launch maneuver.

- [x] **G** @f87568f (merged) (added 2026-11-03, side request) Telegraphed grab: separate biped clips for
  grab start / hold / throw / whiff (drafted with the sprite-anim skill, then wired into
  GrabAction) + a debug grab-box overlay when DebugDrawHitboxes is on. Opus worktree agent.

- [~] **D** (added 2026-11-03, before the user went to bed) Duelist league: three Opus
  agents each build a P2 bot with a distinct playstyle — Rushdown, Terrain zoner /
  fortress, Counter-fighter / footsies — under `Net/Bot/Duelists/<Name>/`, exposing a
  `DuelBot Create`. Harness: `Net/Bot/BotDuel.cs` @82e726f (arenas hall_close / hall_far /
  plain_close on the shipped duel stages; 60 s bouts; kills, then ≥ 0.5 HP damage margin;
  round robin plays both sides). Supervisor's job when all three land: audit each for the
  rules in their brief (raw input, ≤ ~12 presses/s, opponent info only via BotView's
  ≥ 6-frame-delayed view or the HUD's HP, no shared/game-code edits, no degenerate
  stalling, determinism), patch or disqualify violations, merge, run the round robin over
  all arenas × ≥ 6 seeds (+ the Brawler as a reference entrant), and write the results
  to `Plans/DUELIST_LEAGUE.md`.

## Decisions (user, 2026-11-02)

- Spiked wave input: same gesture as eruption. With spikes selected, RMB held inside
  solid charges like the painter, and a fast release launches the crystal wave; a
  tap/slow stroke still spikes cells (with #8's seed lifecycle). Today spikes-selected
  RMB bypasses BlockPaintAction entirely (`ActionStates.cs:2874`) — that has to change.
- Crystal damage: per crystal, with a per-wave cap per victim. A crystal BREAKS when it
  hurts a player. While forming, crystals push bodies out (no damage while growing).
- Wave stops at barricades (no chipping); grown hitbox terrain-occluded from its base;
  path = the normal avalanche path, built by adapting the avalanche generation code;
  small per-crystal cost so waves can be big; high cooldown + long first-seed telegraph.
- Crush applies to the rider too eventually, but riding is off-limits this campaign.

## Decisions needed

- **Faster sprouts vs. two sprout-ride tests (from 2a/2b).** At your chosen speeds
  (paint 0.07 s, avalanche 0.04 s) two `SproutLiftJumpTests` go red, both
  riding-calibration: `JumpOffRisingSprout_InheritsFloorVelocity` (a jump off a rising
  floor keeps ~48 of the floor's 157 px/s; it asserts ≥ 60 %) and
  `DiagonalGrowth_CarriesPlayerUpAndRight` (a synthetic 4-cell diagonal push no longer
  enters TerrainCarriedState within 12 frames — each cell now pushes for ~4 frames). Real
  avalanche riding still passes (all `BotManeuverAvalancheRide` layouts). Left red rather
  than retuning riding (off-limits). Either retune riding later, or dial
  `SproutLifetime` back toward 0.1 in movement_config.json (hot-reloadable) if it feels off.
- **Crush after #4.** Wall pockets crush again (5/5) and Eruption.LowCeiling is green.
  `AvalancheCrushTests.FreeRoof` (free-standing roof) is 1 of 2 red: the target slides out
  from under the roof; the maneuver's angle/lead needs a rework.
- **#4 calls made by the agent (defaults in force):** pin crush = flat 1.5 HP per pin with
  a 0.5 s re-bill cooldown, NON-riders only (standing victims usually get scooped into
  TerrainCarried, so today the pin lands mainly on stunned/tumbling victims; extending to
  riders is a one-line gate in PlayerCharacter when riding is back on the table). Wave
  mass that would spill into foreign solid is DROPPED (wave shapes shrink 0–8%), not
  redistributed into a bigger mound at the face. Enemies (entities) take no crush, as
  before.

- **Bot retune after the slowdown (#9).** 4 bot tests went red on distances/timings tuned
  at the old speeds: `Dash_ClosesGap_AndDashSlashes`, `Burst_CloseInBand…`,
  `AirWhipKick_CrossUp…`, `BotManeuverBackoff_StillHits…`. Left red per "don't worry about
  tests"; a bot retune pass (ranges, hop timing) is a follow-up once speeds settle by feel.

## For the user to review

- **G grab animations:** `Plans/img/grab_strip_{start,hold,throw,whiff}.png`,
  `grab_ingame_{throw,whiff}.png`. Startup 12f → 20f (coil-then-lunge wind-up); hold
  loops; throw/whiff play over the 18f end-lag via a new `SettleClip` successor; phase
  clips bind as `GrabAction.Hold/.Throw/.Whiff` through a new `AnimationVariant` hook.
  Debug grab box (exact latch region; carry box while holding; BlockGrab reach ring) via
  `ActionState.DebugQueryShapes`. To judge by eye: legs mostly come from locomotion
  (0.35 weight); the throw heaves overhead (artefact of fixing GPT's backward elbows) and
  lands ~4 frames after the victim leaves; held victim sits at the carry point, not in
  the hands; a struggle-free escape still plays the throw clip; the live latch box is big
  (~4.8 body radii) because that IS the real geometry. 4 of 16 image calls used.

- **#7 crystal wave:** `Plans/img/crystal_wave_{warning,front,midflight}.png`. Numbers are
  code constants in `World/CrystalWave.cs`: root warning 0.8 s seed + 0.2 s flash; front
  3 frames/cell (~220 px/s); grown 0.35 s; 0.5 HP per crystal, cap 1.5 HP/wave/victim;
  knockback 260 + 0.15 s hitstun; forming push 140 px/s; shard 1.8 tiles; 2 meter
  units/crystal; 8 s cooldown; ~25-cell run. Agent's calls (defaults in force):
  - **Shape:** crystals root only on solid ground below or on a same-wave crystal, so the
    wave hugs the floor (1–2 rows) instead of climbing walls / hanging from ceilings like
    a normal avalanche; a crystal ball hitting a wall shatters (no spill). This is the
    main thing to weigh in the shape-dial discussion.
  - Input: spikes-selected plain RMB on solid is decided after 0.15 s — tap spikes the
    press cell on release, a stroke spikes cells as it goes, a 0.15 s stay on the press
    cell charges (fast release = wave). Shift+RMB spiking is unchanged and immediate.
  - Pending crystals are invisible (ball + root seeds are the telegraph); followers flash
    only 2 frames; the forming push only affects opposing bodies; building over a
    pending crystal evicts it (lets a wall built in the path cut the wave).
  - Bot: WallUp scores 1.4 against visible opposing crystals; CrystalWaveManeuver fires
    only at a target keeping its distance (fleeing / charging / guarding) — against a
    standing target walking in was faster.
  - Shard art is pale lavender-ice; reads a bit faint on the bright backdrop.

- **#8 spike crystals:** `Plans/img/spike_crystal_phases.png` (seed → flash → grown, 3x).
  Open calls from the agent, defaults in force: no refund when the block under a seed
  breaks; seeds don't toughen their block (breaking it during the 1.25 s warning is a
  counter); the shard is only ~3–5 px wide at TileSize 11 (seed vs flash read mostly by
  colour); timings are code constants (`SpikeSeedSeconds` 1.0, `SpikeFlashSeconds` 0.25 in
  `World/CrystalLifecycle.cs`), not config dials, because they're sim-affecting.

- **#6 sprout look** (taste item): `Plans/img/sprout_visuals_before.png` / `_after.png`.
  Owner-coloured corner brackets mark the target slot; growing blocks are a flat,
  lightened material with an owner-coloured rim and a bright leading-edge bar; pending
  ghosts shimmer at 1.6 Hz (needs a live look — a still can't show it). Knobs are the
  constants atop the sprout section of `Drawing/ChunkRenderer.cs`. Unowned builds use a
  pale blue-white close to P1 cyan.

## Campaign log

- a09468e — #3 committed (user's burst work); BurstChargeTests 10/10; combat/terrain/bot reds = 40 baseline.
- c86fc15 — #2a/#2b/#5: per-source sprout dials (SproutSource on nodes, snapshotted; LifetimeFor), per-parent overshoot carry (front continuous: 12 painted cells in 51 frames vs 60 with rounding), unlimited reach chaining off your OWN sprout (others' keep 2x). SproutSpeedTests 7/7. New reds: 2 SproutLiftJump + 5 crush (see Decisions needed); movement-group reds otherwise pre-existing (no sprout refs).
- a07d9df (merge) — #6 sprout visuals, render-only (Drawing/ChunkRenderer.cs, Game1 passes render time). MTile.sln builds.
- (this commit) — #9 slowdown. ConfigLayout only the pre-existing ParkourEnabled mismatch. Bot group 75/9/1: 4 new reds from #9, 5 crush reds from #2b (pending #4).
- f729a46 (merge) — #8 spike seed→flash→grown via reusable `CrystalLifecycle` (World/CrystalLifecycle.cs) + `TileSpikeSeeds` (seed frame is the clock; snapshotted; checksum salt 0x09). SpikeOverlay*/BotManeuverBarrier/SnapshotRoundTrip 47/47 after merging onto the #9 slowdown.
- 2f47da3 (merge) — #4: MassBall stops at solid its wave didn't request (cell→wave table in AvalancheWaves, snapshotted; arming after release; swept path so fast balls can't skip 1-tile walls), lump deposit at the face, foreign-solid spill dropped; wave pin crush 1.5 HP for non-riders (PhysicsWorld pin detection → PhysicsBody.WavePinned). Merge fixups: break path clears wave tag + spike seed; checksum now folds wave cells (salt 0x0A) and sprout Source. AvalancheWall 12/12; bot reds = 4 slowdown + 1 FreeRoof.
- ab13cca (merge) — #7 crystal wave: CrystalWaveField (ChunkMap.Crystals; snapshotted; salts 0x0B–0x0D), CrystalContactSystem, MassBall crystal mode, BlockPaintAction spike-mode arbitration, BuildMeters.CrystalCooldown; bot CrystalWaveManeuver + WallUp crystal threat. SpikeOverlayCrystalWave 12/12, BotManeuverCrystalWave 4/4; targeted sweep 228 pass / 5 known reds.
- 0ffceae — end-of-campaign `full` sweep: 1693 pass / 128 fail / 15 skip. Reds concentrate in movement/corrector classes (LedgePullExit 12, LedgeRegrabDrift 7, JumpExitSweep 5, CorrectorOperations 5, CorrectorHoleJump 4, DeliberateClimb 3, …) — consistent with the #9 slowdown (user: let speed-calibrated tests go red) on top of the BACKLOG §5 / §5.0 baseline; not individually attributed (no pre-campaign full baseline). Remaining: side task G (grab animations) in flight.
- (merge) — G grab animations + debug grab box @f87568f. ClipBinding / ActionOverlay / Grab / BotManeuverGrab / CrystalWave / Duel / SnapshotRoundTrip: 81 pass, 2 known reds (ActionEnd_FadesOut, StruggleSlash).
- 0e5824a — league fairness patch: shared maneuvers read the opponent only via BotView's delayed TargetGrounded/TargetGrabbed/TargetHelpless/TargetAction/TargetPos; GrabManeuver checks its own latch; Guard retaliate ≤ 12/s. Cost: AvalancheCrush WallPocket(roofRow 3) red.
- (merge) — duelist Riposte (counter-fighter) @308b5bd merged after audit (delayed 7-frame record; HP via HUD only). vs Brawler 24-0-0; still green after the fairness patch.
- (merge) — duelist Rampart (terrain zoner) @867b9e2 merged after audit (8-frame delay line; HP via HUD only; taps ≤ 12/s). vs Brawler 23-0-1. Ruling: the telegraphed spike carpet around its fort is reasonable play (visible seed→flash warning, still fights: waves at range, advance, 5 s push).
- (merge) — duelist Bulldog (rushdown) @835b594 merged after audit (6-frame delay ring; HP via HUD for hit-confirm; taps ≤ 12/s). vs Brawler 23-0-1. League running (Release, held-out seeds 11-16, 3 arenas, both sides; Brawler as reference).
