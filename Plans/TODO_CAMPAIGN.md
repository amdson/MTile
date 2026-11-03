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
- [ ] **6** More distinct sprout visuals (render-only; screenshot for the user's review).
- [ ] **8** Spike placement crystal lifecycle: a crystal seed appears on top of the block,
  waits ~1 s, flashes, grows into the existing (1-cell overlay) spike. Phases are sim
  state (snapshotted); visuals render-only. Shares the phase machine with 7.
- [ ] **4** MassBall stops on entering solid it didn't create (pre-existing or anyone's
  barricade), dumping remaining mass at the face; ball remembers its own wave's cells.
  Plus: crush damage for NON-riders pinned between a wave and a wall should be
  significant. Watch `BotManeuverAvalancheRideTests` (rides gain height off cliffs).
- [ ] **7** Spiked avalanche (crystal wave) — see decisions below and todo.txt #7.
  Then bot hookup: WallUpManeuver threat check + a launch maneuver.

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
- **Avalanche speed broke the bot's crush maneuvers.** At avalanche 0.04 s,
  `BotManeuverAvalancheCrushTests` (4 layouts) and `BotManeuverEruptionTests.LowCeiling`
  stop crushing (they pass at 0.1). Item 4 reworks wall crush anyway — revisited there.

## Campaign log

- a09468e — #3 committed (user's burst work); BurstChargeTests 10/10; combat/terrain/bot reds = 40 baseline.
- (this commit) — #2a/#2b/#5: per-source sprout dials (SproutSource on nodes, snapshotted; LifetimeFor), per-parent overshoot carry (front continuous: 12 painted cells in 51 frames vs 60 with rounding), unlimited reach chaining off your OWN sprout (others' keep 2x). SproutSpeedTests 7/7. New reds: 2 SproutLiftJump + 5 crush (see Decisions needed); movement-group reds otherwise pre-existing (no sprout refs).
