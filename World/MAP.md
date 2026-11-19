# World map
Verified against: 4e34142 (2026-11-14)
Covers: World/*.cs, Levels/ (format summary), Stages/*.cs

## What this area is
The sim-side tile world and the hit/hurt registries. `ChunkMap` owns the dense tile grid plus every sparse per-cell table (HP, charge, foam, mass, sprouts, ownership, crystals, spikes) and is the only writer of tiles. `CombatSystem` resolves published hitboxes against tiles and hurtboxes. All of it is sim state (rollback-relevant) except the presentation events noted below. Boundaries: `Physics/` reads tiles through `WorldQuery`/`TileQuery` and calls `DamageCell`/`BreakCell`; `Character/` and `Entities/` publish hitboxes and request tiles; `Drawing/` and `Game1.cs` subscribe to events; `Simulation.cs` drives the per-frame order. `Stage.cs` (repo root) picks a level or terrain delegate; `Stages/` holds the code-built stages.

## Files
- `Chunk.cs`: `Chunk.Size`=16, `Chunk.TileSize`=11, `Tile[16,16]` per chunk. `Tile.cs`: `Tile`, `TileState` (Empty/Sprouting/Solid/Spike), `TileType` (Stone, Dirt, Sand, Foam, Hardened, Spike), `TileTypes` (`IsPlaceable`, `IsGrabbable`, `Count`), `TileRef` (global cell to world px getters).
- `ChunkMap.cs` (1079 lines, the hub), index:
  - 10-215 fields: `_dict`, `Providers`, `Graph`, `Damage`, `Charge`, `SpikeSeeds`, `Crystals`, `Impact`, `Mass`, `Waves`, `Ownership`, `Foam`, `_journal`, `Generator`, `TerrainHash`, events
  - 98-129 `GetOrCreateChunk`, `WriteTile`; 143-174 fingerprint; 240-287 streaming (`EnsureChunk`, `StreamAround`, `DropChunk`)
  - 292-340 `ISolidShapeProvider` impl; 343-388 coordinate helpers and `GetCell*`
  - 391-590 build requests (`TryRequestTile`, `TryRequestCrystal`, `TryRequestSpike`, `ForceSprout`); 595-609 `LifetimeFor`, `StampPlaced`
  - 621-728 `TickSprouts`; 746-818 `DropSupportFor`, `PruneOrphanedGhosts`
  - 823-944 `DamageCell`, `BreakCell`, `EditorSetCell`, `HarvestCell`
  - 950-1011 `CaptureTerrain`/`RestoreTerrain`/`RevertTile`; 1018-1078 `CaptureDense`/`RestoreDense`
- `TerrainJournal.cs`: inverse-delta log (TileWrite, ChunkCreated). `TerrainSnapshot.cs`: `TerrainSnapshot`, `SproutGraphData`, `SproutNodeData`. `DenseTerrainCapture.cs`: full-grid copy for the recorder.
- Sparse tables (each has Capture/Restore): `TileDamage.cs` (HP), `TileCharge.cs`, `FoamDecay.cs`, `TileSpikeSeeds.cs`, `TileImpactAccumulator.cs`, `TileMassField.cs`, `AvalancheWaves.cs`, `BlockOwnership.cs`, `BlockEaterInfection.cs` (block-eater plant: infection table, ticked in `TickSprouts`, chews via `DamageCell`; art in `Drawing/BlockEaterRenderer.cs`), `CrystalWaveField.cs` (+ `CrystalWave.cs` dials, `CrystalLifecycle.cs` phase math).
- Sprouts: `TileSproutGraph.cs` (Pending/Growing lists, dedupe by cell), `TileSproutNode.cs` (faces, volume geometry, `FaceOrder`).
- `MaterialStrengths.cs`: per-type MaxHP, Restitution, BuildCost, loaded from `configs/material_strengths.json`.
- Queries: `TileQuery.cs`, `TileQueryChain.cs`, `TileFilters.cs` (solid-only fluent queries for probes), `WorldQuery.cs` (tiles + providers), `SolidShapeProvider.cs` (`SolidShapeRef`, `ISolidShapeProvider`), `MovingRectangle.cs` (platform provider), `TileReach.cs` (terrain line-of-sight and area damage).
- Combat: `Hitbox.cs` (struct, `Faction`, `HitTargets`, `ClankKind`), `Hurtbox.cs`, `HitboxWorld.cs`, `HurtboxWorld.cs`, `CombatSystem.cs` (708; `Sense` = sensor hitboxes), `SensorContact.cs` (`SensorContact`, `IContactSensor` — swept first-contact reports for `Hitbox.Sensor`), `HitIdAllocator.cs`, `ForceField.cs` (`ForceField`, `ForceFieldWorld`, `ForceFieldSystem`), `SpikeContactSystem.cs`, `CrystalContactSystem.cs` (post-sweep contact damage).
- Loading and generation: `TerrainLoader.cs` (level json + ascii), `AsciiTerrain.cs` (test/tool builder), `WorldGenerator.cs` (`IChunkGenerator`, endless terrain), `PerlinNoise1D.cs`, `ValueNoise2D.cs`.
- `Levels/`: `*.json` = `TerrainConfig` (`Extents`, `ChunkFiles` "cx,cy" to txt, `Rules` like `"y >= 6"`, optional `Perlin`, optional `WorldGen`, `PlayerSpawn`); `*.txt` = 16 rows x 16 chars per chunk (X/x Stone, D Dirt, S Sand, F Foam, H Hardened, else empty); `Levels/saved/` = StageSaver output.
- `Stages/`: `DuelStages.cs` (duel_hall, duel_plain, `DuelChunks`), `WallDummyStage.cs`, `DepthsStage.cs` (381, `DepthsPlan` table, ticker-driven room/wave script, state in `g.StageVars`), `DepthsLayout.cs` (900), index:
  - 20-92 `DepthsShape`, `DepthsRoom`, `DepthsMap` (tile-unit rectangles, world-px Vector2s)
  - 118-244 `Build`/`BuildCore` (seeded); 245-267 `MakeChunks` (Grid to ChunkMap via the unjournaled indexer setter)
  - 284 `ToAscii`; 304 `Reach`; 351-455 `Local`, `MakeRoom` (385: re-rolls a room up to 16 times until the `Reach` jump-route check passes), `Finish`
  - 456-531 decoration (`Spike`, `Decorate`, `Zigzag`)
  - 534-820 shapes: `Hall` 534, `Shaft` 612, `Gallery` 642, `Terraces` 665, `Chasm` 697, `Dome` 728, `Massive` 764
  - 823-900 spawn/anchor spot computation (`ClearBox`, `Spots`)

## Flow
(a) Tile edit, request to committed:
1. Writers: player actions (`Character/Action/ActionStates.cs:3278` `TryRequestSpike`/`TryRequestTile` for Shift+place; paint through `ctx.Chunks.Mass.Deposit` at ActionStates.cs:3179), `MassBall` (`Entities/MassBall.cs:129,144`), `LobbedAreaProjectile.cs:311`, enemy builders (`EnemyBlockActions.cs:153,239`, `ForceSprout` users), `PullPointEntity.cs:136,379` (`HarvestCell`), `CreativeMode.cs:140` (`EditorSetCell`), `Physics/PhysicsWorld.cs:210` and `Physics/ImpactSpringField.cs:370` (`BreakCell`/`DamageCell`), `CombatSystem.cs:208` (`DamageCell`), `TileReach.cs:139`.
2. The meter charges, not `ChunkMap`: callers check `BuildMeters.CanAfford`/`SpendForTiles` (`Character/Action/BuildMeters.cs:265-279`), cost = `MaterialStrengths.BuildCostFor(type)` (MaterialStrengths.cs:69). Spikes cost `SpikeCostInStone` x Stone (MaterialStrengths.cs:77-78).
3. `TileMassField.Deposit` (TileMassField.cs:83) pools mass per cell; each whole unit (`Threshold` 1) calls `TryRequestTile`/`TryRequestSpike`/`TryRequestCrystal` or spills 25% to N/E/S/W (depth limit 8, order fixed at lines 170-173).
4. `TryRequestTile` (ChunkMap.cs:429): rejects `TileType.Spike`, non-open or already-requested cells. Solid neighbour: `Graph.AddGrowing`, `WriteTile(Sprouting)`, `StampPlaced`, fires `OnTilePlaced`. Only sprout neighbour: `AddPending` ghost, no tile write. Neither: null.
5. `Simulation.Step` calls `TickSprouts(dt)` each frame (Simulation.cs:540): advances clocks and `TerrainFrame`, ticks waves/ownership prune/spike seeds/crystals/foam/mass, prunes orphan ghosts, ages Growing sprouts, finalizes complete ones to `Solid` (pass 1), promotes adjacent ghosts (pass 2), then the avalanche due-scan (pass 3).
6. Every cell change goes through `WriteTile` (ChunkMap.cs:111): records `(prevState, prevType, prevTeam)` in `_journal`, XOR-updates `TerrainHash`, writes the cell. `GetOrCreateChunk` records chunk creation.
7. Destruction: `DamageCell` (823) accumulates in `TileDamage.ApplyDamage` against `MaterialStrengths.MaxHPFor`; crossing it calls `BreakCell` (848): `WriteTile(Empty)`, clears Damage/Charge/Ownership/Waves/Foam entries, queues charged breaks (`ChargedBreaks`, drained at Simulation.cs:643-650), clears orphan spikes, `DropSupportFor`, then fires `OnTileBroken(center, type)`.
8. Event subscribers: `Game1.cs:544-556` (both events, cosmetic only). `OnTileBroken` is not fired by `EditorSetCell`, `ClearSpike`, `DropSupportFor`'s sprout cancellation, or rollback `RevertTile`; it IS fired when `BreakCell` cancels a Sprouting cell.
9. Snapshot: `Simulation.Snapshot` stores `_chunks.CaptureTerrain()` (Simulation.cs:814); restore calls `RestoreTerrain` (838): `_journal.RewindTo(mark)` undoes tile writes newest-first via `RevertTile`/`DropChunk`, value-restores every sparse table, re-links `Tile.Sprout` from `Graph.Growing`, sets `_ghostsDirty`.

(b) Hit, registration to resolution (order in `Simulation.Step`, Simulation.cs:546-572):
1. Clear `_hitboxes`, `_hurtboxes`, `_fields`; every `IHittable.PublishHurtboxes` (primary, secondaries, entities).
2. `PlayerCharacter.Update` / secondaries / entity `Update` publish `Hitbox`es (e.g. ActionStates.cs:876,1778,2048) with ids from `HitIdAllocator.Next()` (`Simulation.HitIds`, Simulation.cs:170).
3. `CombatSystem.Apply` (CombatSystem.cs:113): reset the 1-frame inboxes, `ResolveClanks`, then per hitbox: tile pass (skipped for `EntitiesOnly`) then entity pass (skipped for `TilesOnly` or a clanked HitId).
4. Tile pass: gather Solid cells in `Region` (optionally polygon SAT, `TileMaxMaterialHP` filter), sort nearest-first and filter by `TileReach.IsCellReachable` if `Occluded`, call `chunks.DamageCell(gtx, gty, hit.Damage)` per cell (line 208). Recoil goes to `_recoilByHitId` (`AccumulateRecoil`, line 500; tile pogo latched once per attack via `EntityId.None` in the dedupe set, 243-246).
5. Entity pass: for each hurtbox that passes faction/team/coop/dedupe/overlap/occlusion tests, `resolve(hb.Target).OnHit(hit, hb)` (line 275). The victim consumes `hit.BodyDamage` (`PlayerCharacter.cs:258`, `Entity.cs:116`) and calls `HitResolver.Resolve` (`PlayerCharacter.cs:262`, `Entity.cs:117`). The returned impulse feeds recoil and hitstop inboxes (279-286).
6. Prune dedupe entries for HitIds not broadcast this frame unless `HoldHitId`'d (291-298).
7. Attackers read inboxes next frame via `PeekRecoil`/`PeekHits`/`PeekHitstop` (e.g. ActionStates.cs:857,951).
8. `ForceFieldSystem.Apply` (Simulation.cs:576) then adds `AppliedForce` before `LaunchSystem.StepSwept`; spike and crystal contact systems run after the sweep (Simulation.cs:616-629) and deliver hits through `OnHit` with synthesized `Hitbox`es.

## Invariants and traps
- Units: `Chunk.TileSize` is 11 px (Chunk.cs:8), not 16; some comments (`AsciiTerrain.cs:10`) and docs still say 16. Chunk = 16x16 tiles = 176 px. Y is down.
- Coordinates: global cell `(gtx, gty)` is the key of every sparse table. World to cell: `floor(x / TileSize)` (ChunkMap.cs:343). Cell to chunk-local: floor-division, then `tx = gtx - cx*16` (348-355); negative cells need floor, not truncation (also `TileRef.ChunkX`, Tile.cs:27). Cell centre: `(g + 0.5) * TileSize` (`CellCenter`, ChunkMap.cs:357). `Chunk.Tiles` is indexed `[tx, ty]`, x first. `ChunkPos` is a `Point` in chunk units; `Chunk.WorldPosition` is px.
- `Tile.State == Solid` is the only blocking state. `Sprouting` blocks via `Graph.Growing` face volumes, not tiles; `Spike` is open air (`IsOpen`, Tile.cs:56) and `TileQuery.SolidTilesInRect` uses `IsSolid` (Solid only). `GetCellType` returns Stone for unloaded chunks (ChunkMap.cs:386); `Tile.Type` is meaningful only for Solid/Sprouting. Crystals are not tiles: a crystal cell is `Empty` in the grid and lives only in `CrystalWaveField` (CrystalWaveField.cs header); `WriteTile` evicts it when real terrain claims the cell (ChunkMap.cs:117-118).
- `WriteTile` and `GetOrCreateChunk` are the only journaled mutators (ChunkMap.cs:98,111). Direct cell writes (and the `this[Point]` indexer setter, ChunkMap.cs:219-223) bypass the journal and `TerrainHash`: `TerrainLoader`, `AsciiTerrain`, `DepthsLayout.MakeChunks`, `DuelChunks`, `WorldGenerator.Generate`. They are safe only before `Simulation` calls `RecomputeTerrainHash` (Simulation.cs:380,443), or, for generated chunks, because `EnsureChunk` XORs `ChunkHash` in (ChunkMap.cs:245) and `DropChunk` XORs it out (285).
- `Generator.Generate` must be a pure function of chunk position (WorldGenerator.cs:10-14). `StreamAround` is called with player positions via `StreamTerrain` (Simulation.cs:452-459) at the top of `Step` (529) and in both ctors; a rewind drops streamed chunks through the journal.
- Journal marks are instance-relative: restoring a `TerrainSnapshot` onto a different `ChunkMap` is invalid once tiles were journaled (TerrainSnapshot.cs header). `TerrainJournal` has no trim method and nothing in `Simulation.cs`/`Net/` bounds it; `_entries` only grows except in `RewindTo` (TerrainJournal.cs:57-67). Its header comment (line 21, "netcode trims it") is stale.
- `RestoreDense` writes cells directly and does not update `TerrainHash` or the journal (ChunkMap.cs:1045-1078); it is for the recorder only and must be called after sparse restore.
- Sprout refs (`Tile.Sprout`) are not journaled; `RevertTile` nulls them and `RestoreTerrain` step 3 re-links from `Graph.Growing` (ChunkMap.cs:1009, 991-993). Anything new stored on a `Tile` needs the journal entry, `CellHash` and `RevertTile` updated together.
- Any new sparse per-cell table must be added to `CaptureTerrain`, `RestoreTerrain`, `TerrainSnapshot`, and `Simulation.Checksum` (Simulation.cs:710-768 folds the tables in), and cleared in `BreakCell`/`EditorSetCell` where a cell empties. Today `Charge`, Pending ghosts, wave records, `SproutClock` and `TerrainFrame` are snapshotted but NOT checksummed.
- `TickSprouts` pass order is load-bearing: finalize all, then promote (ChunkMap.cs:654-699), so shells grow symmetrically. Foam expiry runs before sprout aging (638). Wave-tagged requests park as Pending even when supported, when `AvalancheSurfaceLag > 0` (444-458) and promote via the due-scan (708-727).
- `BreakCell` also cancels a Sprouting cell (857-871); `DamageCell` does nothing to Sprouting cells (833) and knocks off a Spike on any positive damage (828-831).
- `HarvestCell` lifts the charge before `BreakCell` so no blast queues, and restores it if nothing broke (930-939).
- `Hitbox.Damage` is what tiles take (`CombatSystem.cs:208`); `BodyDamage` (defaults to `Damage` when the ctor arg is < 0, Hitbox.cs:234) is what bodies take. Do not read `Damage` for HP.
- `CombatSystem` dedupe is per `(HitId, Target)` for entities only; tiles take damage every frame the hitbox overlaps (CombatSystem.cs:12-18). `EntityId(-1)` is the clank-cancel mark and `EntityId.None` the tile-pogo latch inside the dedupe sets (lines 89, 243).
- Snapshotted combat state: `_hitDedupe`, `_entityHitsByHitId`, `_recoilByHitId`, `_hitstopByHitId` (CombatSystem.cs:433-485). Not snapshotted by design: registries, `_heldHitIds`, `_liveHitIds`, `ChargedBreaks`, `Coop`, force fields.
- `HitIdAllocator.Next()` pre-increments (first id 1); id 0 is the None latch (HitIdAllocator.cs:22, CombatSystem.cs:87). Its single int is snapshotted as `HitIdValue`. Never mint ids from statics.
- Faction gate: same `Owner` faction never hits (CombatSystem.cs:256) unless both are `Faction.Enemy` on different `Team`s (`AcrossTeams`, 418-424); `Coop` blocks player-on-player (260). Use `Factions.IsPlayer`, not equality (Hitbox.cs:16).
- `TileImpactAccumulator` is ticked (Simulation.cs:542), snapshotted and checksummed (722), but `AccrueAndConsume` has no caller anywhere (not even tests); the table is always empty in practice.
- `OnTileBroken`/`OnTilePlaced` are presentation events; tests reassign them wholesale (ChunkMap.cs:188-196). Sim reactions must not hang off them; the sim-side channel is `ChargedBreaks`.
- `MaterialStrengths.Load` is sim-affecting, a process-wide static, loaded once at boot (Game1.cs:383); a missing file keeps the current table (`Defaults()` at startup) and only logs (MaterialStrengths.cs:88-93). The loader merges per field. `Hardened` has `BuildCost` 1e6 and is blocked by `IsPlaceable`/`IsGrabbable` (Tile.cs:97,103).
- `TileTypes.Count` (6) must track the enum (Tile.cs:92).
- `ForceField` is not snapshotted; it regenerates from FSM state each step. It runs before physics, hitboxes resolve before it, in the same frame (Simulation.cs:571-585).
- `BlockOwnership` fades by frames: `HalfLifeFrames` 3600, pruned every 60 `TerrainFrame`s (BlockOwnership.cs:30-33; ChunkMap.cs:631). `TerrainFrame` and `SproutClock` advance only in `TickSprouts`.
- `DepthsLayout`/`DuelStages` build deterministically from a seed/constants so both peers match; Depths progress lives in `g.StageVars` (DepthsStage.cs header).

## Where to change X
- Tile HP, restitution, build cost: `configs/material_strengths.json`, defaults in `MaterialStrengths.Defaults()` (MaterialStrengths.cs:46); enum changes also touch `TileTypes`, `TerrainLoader` chars, `StageSaver.TileChar`, `Drawing/TilePalette.cs`.
- Add a new cell state or per-tile field: `Tile`, `WriteTile`, `TerrainJournal.Entry`, `CellHash`, `RevertTile`, `DenseTerrainCapture`/`CaptureDense`/`RestoreDense`.
- Add a sparse per-cell table: new class modelled on `TileCharge`, field on `ChunkMap`, `TerrainSnapshot`, `CaptureTerrain`/`RestoreTerrain`, `Simulation.Checksum`, clear in `BreakCell`.
- Sprout growth speed/shape: `ChunkMap.LifetimeFor` (595), `TileSproutNode.VolumeCenter`, `TickSprouts` (621).
- Build mass spill behaviour: `TileMassField.DepositAt`/`Spill`; build gating by support: `TryRequestTile` (429).
- Add a Hitbox field: `Hitbox.cs` struct + ctor (258 lines), then consume in `CombatSystem.Apply` or the victim's `OnHit`.
- Hit-vs-hit (clank) rules: `CombatSystem.ResolveClanks` (310), `ClankDamageBand` (84).
- Terrain occlusion or area damage: `TileReach` (`IsCellReachable`, `DamageDisc`).
- New level: add `Levels/<name>.json` (+ chunk txt) and register in `Stage.cs`; procedural/endless: `WorldGenConfig` in `WorldGenerator.cs`; code-built stage: `Stages/DuelStages.cs` pattern (`DuelChunks`).
- Contact-damage hazards (spikes, crystals): `SpikeContactSystem.cs`, `CrystalContactSystem.cs`, tuning in `CrystalWave.cs`.
- Pull/hold effects on bodies: `ForceFieldSystem.Apply` (ForceField.cs:101).

## Tests
Under `MTile.Tests/` (`scripts/test-group.py` filters `FullyQualifiedName~term`, so a term matches anywhere in namespace.class.method; in practice class names):
- `terrain`: `TerrainRollbackTests`, `SproutGraphTests`, `SproutCrushTests`/`SproutPushTests`/`SproutSpeedTests`/`SproutLiftJumpTests`, `TileMassFieldTests`, `FoamDecayTests`, `InfiniteTerrainTests`, `LevelWriterTerrainTests`, `DenseTerrainScrubTests`, `BlockOwnershipTests`, `SpikeOverlay*Tests`, `DepthsLayoutTests`, `Avalanche*Tests`.
- `combat`: `CombatClankTests`, `CombatClankSimTests`, `HitboxOcclusionTests`, `CombatFeelTests`, `CombatHitstunTests`, `BlockOwnershipTests`, `SpikeOverlay*Tests`.
- `simcore`: `SnapshotRoundTripTests`, `DepthsStageTests`, `DepthsDeterminismTests`, `DepthsLayoutTests`, `DuelStageTests`, `WallDummyStageTests`, `StageSaverTests`.
- `bot`: `BotManeuver*CrystalWave*Tests`, `BotManeuverSpikeGuardTests`. No test class is named for `TerrainJournal`, `MaterialStrengths` or `ForceFieldSystem`. The journal is exercised only indirectly through `CaptureTerrain`/`RestoreTerrain` and sim snapshots (`TerrainRollbackTests`, `InfiniteTerrainTests`, `BlockChargeTests`, `SnapshotRoundTripTests`); `MaterialStrengths` is referenced by `ConfigLayoutTests`, `FighterBlockActionTests`, `SpikeOverlayTests`; `ForceFieldSystem` runs in `SimRunner` (so every Sim scenario), and `GrabTests`/`CombatFeelTests` mention force fields. `TileImpactAccumulator` has no test reference.
Headless scenario harness: `MTile.Tests/Sim/SimTerrain.cs`, `SpikeTiles.cs`.
