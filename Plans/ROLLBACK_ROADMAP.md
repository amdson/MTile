# Rollback Netcode Roadmap

Goal: get MTile running in multiplayer with **rollback netcode** (GGPO-style).
Topology decision: **same-build P2P only** (desktop↔desktop or WASM↔WASM). Both
peers run the same binary, so `float`/`MathF` results and `Dictionary<Point,_>`
iteration are deterministic — no fixed-point math required. The only float caveat
is NaN/Inf hygiene (don't `Normalize` a zero vector, etc.).

Rollback needs three things from the simulation:
1. **Deterministic advance** from inputs alone at a fixed timestep.
2. **Snapshot** — capture the entire sim state cheaply.
3. **Restore** — roll back to a snapshot exactly.

Snapshot strategy decision: **refactor sim state into plain data** (heading toward
ECS). Behavior becomes stateless code over flat component structs; a snapshot is
then a struct-copy of the component data. Exception: **chunks/tiles** are too large
to copy each frame — they get an **update/revert journal** instead (deferred).

---

## Goals

- [x] **0. Audit** — inventory of state + determinism hazards. (done in conversation)
- [ ] **1. Extract `Simulation`** — one object owns players + entities + chunks +
      per-match config, with a single `Step(input)` method. `Game1` becomes a thin
      shell: gather input → `Step` → render. Cosmetic systems (particles, trail,
      camera, sprites) stay in `Game1`, strictly downstream of the sim.
- [ ] **2. Fixed `dt` + all input through `PlayerInput`** — sim runs on a constant
      `FixedDt`; block-picker (1-4), planner toggle (P), and build (RMB drag) get
      captured into `PlayerInput` and interpreted *inside* the sim, not by polling
      hardware mid-update.
- [x] **3. Kill sim-affecting statics** — done:
      - 9 `_nextHitId` counters → one sim-shared `HitIdAllocator` (threaded via
        `EnvironmentContext.HitIds` for actions, `IEntitySpawner.HitIds` for AI/
        projectiles). `BulletProjectile` holds the allocator (re-mints on deflect).
      - planner `CurrentMode`/`ActiveType` → per-player `PlayerCharacter.EruptionMode`/
        `ActiveBlockType` (driven by that player's own P/1-4 input), passed into
        `EruptionPlanner.Plan`/`MassBallPlanner.Plan` as args. `LobbedAreaProjectile`
        captures mode+type at launch.
      - `CombatSystem` static dedupe table → instance owned by `Simulation`
        (also bonus-scope, but it's cross-frame sim state).
      - `MovementConfig` hot-reload watcher gated behind `GameConfig.HotReloadMovementConfig`
        (default true; MP sets false). Remaining static: `EruptionPlanner.DebugDrawMassBall`
        — render-only, not sim-affecting, intentionally left.
- [ ] **4. Data-oriented refactor + Snapshot/Restore** — pull per-activation fields
      out of the flyweight FSM states/actions and `PlayerAbilityState` into per-player
      plain-data blobs; entities become components with stable IDs. Add
      `Snapshot()`/`Restore()` over players + entities (terrain excluded).
      **Detailed design + full state inventory: [STATE_SNAPSHOT_PLAN.md](STATE_SNAPSHOT_PLAN.md).**
- [ ] **5. Determinism test** — run N frames, snapshot at K, run to N, restore K,
      re-run to N, assert identical traces. Built on the existing `SimReport` CSV
      diffing. This is the gate that proves rollback-safety.
- [x] **6. Chunk journal/revert** — **done.** `TerrainJournal` logs the *dense* tile
      grid as inverse-recoverable deltas (tile writes + lazy chunk creation); every
      mutation funnels through `ChunkMap.WriteTile`/`GetOrCreateChunk` (used by
      `BreakCell`, `DamageCell`→break, `TryRequestTile`, sprout finalize/promotion,
      foam decay), so nothing mutates a chunk outside the journaled path. A snapshot
      records `_journal.Mark`; `RestoreTerrain` rewinds past it, applying inverses in
      reverse. The *sparse* side-structures tick every frame (sprout ages, foam/HP/
      impact timers), so they're value-snapshotted instead of journaled
      (`TileSproutGraph`/`TileDamage`/`FoamDecay`/`TileImpactAccumulator` `.Capture/
      .Restore`); sprout→tile refs are re-linked from the restored graph after the
      rewind. Wired into `SimSnapshot.Terrain` + `Simulation.Snapshot/Restore`.
      Validated by `SnapshotRoundTripTests` (a ball chipping the floor + a foam build
      straddling the snapshot both replay bit-for-bit). **Caveat:** journal marks are
      instance-relative — terrain restore is same-instance only (the rollback case);
      player/entity snapshots remain fully portable.
- [ ] **7. GGPO-style prediction/rollback** layered on top: predict remote input, run
      locally lag-free, roll back + re-simulate (`Restore` → replay) on misprediction;
      WebRTC datachannel transport + Firestore signaling.
      **Detailed design, ported from the user's JS prototype: [GGPO_PLAN.md](GGPO_PLAN.md).**

Milestone: goals 1–6 give a fully snapshot-restorable world **including terrain**,
verified by tests. Only GGPO-style prediction/transport (goal 7) remains.

---

## Notes / decisions

- `MathF` cross-platform determinism is a non-issue under same-build P2P; revisit
  only if cross-play (WASM↔desktop) is ever wanted (goal would need fixed-point or
  state-sync + checksums).
- Camera-derived `MouseWorldPosition` differs per client; for now it rides in
  `PlayerInput` computed from the local camera. Networking it correctly is a
  later concern (send screen-space + deterministic camera, or a shared rule).
- Particles / `_cursorTrail` / sprites / `Camera` are render-only and must never
  feed back into the sim.
