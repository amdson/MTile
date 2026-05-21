using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The deterministic game world. Owns every piece of state the simulation reads or
// writes — players, entities, chunks, combat registries, dynamic platforms — and
// advances them by one fixed timestep via Step(input). Knows nothing about
// rendering, hardware input, or wall-clock time: those live in Game1, strictly
// downstream of (and feeding into) this object.
//
// This is the rollback-netcode core. SimRunner is the headless test analogue and
// mirrors the same phase ordering. Goal 4 will make Step's state snapshot/restorable;
// for now the job is just to isolate it from the render shell and run it on a fixed dt.
public sealed class Simulation : IEntitySpawner, IChunkProvider
{
    // Fixed simulation timestep. Every Step advances the world by exactly this much,
    // regardless of wall-clock frame time — a hard requirement for deterministic
    // replay. Game1 still runs at this rate (IsFixedTimeStep), but the sim no longer
    // trusts the GameTime value; it uses this constant.
    public const float FixedDt = 1f / 30f;

    public static readonly Vector2 Gravity = new(0f, 600f);
    // Exposed so action FSMs can compute ballistic launch velocities against the
    // same gravity vector the physics world integrates with.
    public static float WorldGravityY => Gravity.Y;

    private readonly List<PhysicsBody> _bodies = new();
    private readonly ChunkMap _chunks;
    private readonly HitboxWorld  _hitboxes  = new();
    private readonly HurtboxWorld _hurtboxes = new();
    private readonly List<IHittable> _hittables = new();
    private readonly List<Entity>    _entities  = new();
    // Per-frame intersection pass; owns the cross-frame (HitId,Target) dedupe table.
    private readonly CombatSystem _combat = new();
    // Single deterministic HitId source shared by all players + entities.
    private readonly HitIdAllocator _hitIds = new();

    private PlayerCharacter _player;
    private readonly Controller _controller = new();
    private Vector2 _playerSpawn;

    // Secondary PlayerCharacters: full hittables whose Controllers are never fed
    // hardware input (real input drives _player only). See Game1.AddSecondaryPlayer
    // history / SimRunner.RunMulti for the rationale.
    private readonly List<(PlayerCharacter Player, Controller Ctrl)> _secondaryPlayers = new();

    // Stage-owned scene props: per-frame tickers (moving platforms) and the platform
    // shapes themselves. Populate fills these; Step ticks every callback; the chunk
    // map already holds each platform as a solid-shape provider.
    private readonly List<Action<float>>                       _stageTickers = new();
    private readonly List<(MovingRectangle Rect, Color Color)> _platforms    = new();

    // Most recent frame on which HandleBuildInput placed a tile. Plumbed into the
    // player so BlockReadyAction can cancel its charge mid-build.
    private int _lastTilePlacedFrame = int.MinValue / 2;

    // Deterministic id source for snapshot identity (roadmap goal 4 §G/§H). Players
    // and entities draw from the same monotonic sequence in construction/spawn order,
    // so a given object keeps its id across a snapshot/restore round-trip and the
    // combat dedupe table can be snapshotted by id. Restored alongside the rest of
    // the sim so post-restore spawns mint the same ids as the original run.
    private int _nextId;

    // Absolute elapsed sim time (seconds), advanced by one FixedDt per Step. Drives
    // moving-platform tickers as a pure function of time so platforms snapshot/restore
    // with no hidden accumulator state.
    private float _elapsed;

    private int NextId() => ++_nextId;

    // IChunkProvider — lets entities (LobbedAreaProjectile) mutate the chunk map.
    public ChunkMap Chunks => _chunks;
    // IEntitySpawner — shared HitId source for AI / projectile-spawned hitboxes.
    public HitIdAllocator HitIds => _hitIds;

    // Fired when the player dies and respawns. Game1 hooks this for the cosmetic puff;
    // the respawn itself happens inside Step so it stays deterministic.
    public event Action<Vector2> OnPlayerRespawn;

    // ── Read-only views for the render shell ────────────────────────────────────
    public PlayerCharacter Player => _player;
    public IReadOnlyList<(PlayerCharacter Player, Controller Ctrl)> SecondaryPlayers => _secondaryPlayers;
    public IReadOnlyList<Entity> Entities => _entities;
    public IReadOnlyList<(MovingRectangle Rect, Color Color)> Platforms => _platforms;
    public IReadOnlyList<PhysicsBody> Bodies => _bodies;
    public HitboxWorld  Hitboxes  => _hitboxes;
    public HurtboxWorld Hurtboxes => _hurtboxes;
    public PlayerInput CurrentInput => _controller.Current;
    // Primary player's current selections — surfaced for the HUD.
    public TileType            ActiveBlockType => _player.ActiveBlockType;
    public EruptionPlannerMode EruptionMode    => _player.EruptionMode;

    // Headless/test constructor: terrain is supplied directly (no file load) and the
    // scene is populated by an optional delegate (spawn entities / platforms via the
    // public stage-facing API). Lets determinism tests build a self-contained sim
    // without TitleContent file plumbing. No GameConfig — defaults stand.
    public Simulation(ChunkMap chunks, Vector2 playerSpawn, Action<Simulation> populate = null)
    {
        _chunks      = chunks;
        _playerSpawn = playerSpawn;
        _player = new PlayerCharacter(_playerSpawn) { HitIds = _hitIds, Id = NextId() };
        _bodies.Add(_player.Body);
        _hittables.Add(_player);
        populate?.Invoke(this);
    }

    public Simulation(GameConfig config, Stage stage)
    {
        _chunks = new ChunkMap();
        // Title-relative; chunk files referenced by the config resolve next to it.
        TerrainLoader.Load($"Levels/{stage.TerrainConfig}", _chunks);

        _playerSpawn = stage.PlayerSpawn;
        _player = new PlayerCharacter(_playerSpawn) { HitIds = _hitIds, Id = NextId() };
        _bodies.Add(_player.Body);
        _hittables.Add(_player);

        // Seed the block picker from config. Falls back to Dirt on any unknown string.
        if (Enum.TryParse<TileType>(config.StartingBlockType, ignoreCase: true, out var startType))
            _player.ActiveBlockType = startType;

        // Optional second player for manual combat testing.
        if (config.SpawnSecondPlayer)
        {
            var offset = new Vector2(config.SecondPlayerOffsetX, config.SecondPlayerOffsetY);
            AddSecondaryPlayer(_playerSpawn + offset);
        }

        // Hand control to the stage: it populates platforms, tickers, and entities.
        stage.Populate(this);
    }

    // ── Stage-facing API (called from Stage.Populate) ───────────────────────────
    public void SpawnEntity(Entity e)
    {
        e.Id = NextId();
        _entities.Add(e);
        _hittables.Add(e);
        _bodies.Add(e.Body);
    }

    public void AddPlatform(MovingRectangle rect, Color color)
    {
        _platforms.Add((rect, color));
        _chunks.Providers.Add(rect);
    }

    public void AddTicker(Action<float> tick) => _stageTickers.Add(tick);

    // Spawns an additional PlayerCharacter alongside _player. The returned Controller
    // is the input channel for this body (tests / scripted scenarios call InjectInput);
    // hardware input never touches it.
    public (PlayerCharacter Player, Controller Ctrl) AddSecondaryPlayer(Vector2 spawn)
    {
        var ctrl = new Controller();
        // Distinct faction per player so attacks resolve between players (and stay
        // self-immune). Primary is Player1; the Nth secondary is player index N.
        var player = new PlayerCharacter(spawn)
        {
            HitIds  = _hitIds,
            Id      = NextId(),
            Faction = Factions.ForPlayerIndex(_secondaryPlayers.Count + 1),
        };
        _bodies.Add(player.Body);
        _hittables.Add(player);
        _secondaryPlayers.Add((player, ctrl));
        return (player, ctrl);
    }

    // Two-player step (rollback stage 1, GGPO_PLAN §I.1). `p0` drives the primary
    // player, `p1` the first secondary player — the analogue of the reference's
    // update(gamestate, [p0, p1]). Any further secondary players (rare) keep whatever
    // their own controller holds. The single-arg Step is solo play / the primary-only
    // path. Inject the secondary input BEFORE the shared update so the secondary's
    // Update reads p1 as its current frame.
    public void Step(PlayerInput p0, PlayerInput p1)
    {
        if (_secondaryPlayers.Count > 0)
            _secondaryPlayers[0].Ctrl.InjectInput(p1);
        Step(p0);
    }

    // Advance the world by one fixed timestep. `input` is the primary player's input
    // for this frame (gathered from hardware, or — eventually — from the netcode
    // input queue). Phase ordering mirrors the legacy Game1.Update exactly.
    public void Step(PlayerInput input)
    {
        _controller.InjectInput(input);
        const float dt = FixedDt;
        _elapsed += dt;

        // Block-picker (1-4) and planner-mode toggle (P) are now interpreted per-player
        // inside PlayerCharacter.Update from its own input — no global planner statics.

        HandleBuildInput();

        // Tick dynamic surfaces BEFORE the body sweep (roadmap §2D update ordering).
        // Tickers receive absolute elapsed time so platform motion is a pure function
        // of the clock (snapshot-safe — see _elapsed).
        foreach (var tick in _stageTickers) tick(_elapsed);

        _chunks.TickSprouts(dt);
        // Decay per-cell impact accumulator so stray small impulses bleed off.
        _chunks.Impact.Tick(dt);

        // Combat frame phases: clear registries → publish hurtboxes → player update
        // (publishes hitboxes) → entity AI → CombatSystem.Apply → physics.
        _hitboxes.Clear();
        _hurtboxes.Clear();
        foreach (var h in _hittables) h.PublishHurtboxes(_hurtboxes);
        _player.LastTilePlacedFrame = _lastTilePlacedFrame;
        _player.Update(_controller, _chunks, _hitboxes, _hurtboxes, dt, this);
        foreach (var (p, c) in _secondaryPlayers)
            p.Update(c, _chunks, _hitboxes, _hurtboxes, dt);
        // Snapshot count so newly-spawned entities skip their first Update.
        int entityCount = _entities.Count;
        for (int i = 0; i < entityCount; i++) _entities[i].Update(dt, _player, _hitboxes, this);
        _combat.Apply(_chunks, _hitboxes, _hurtboxes);

        // Player respawn on death.
        if (!_player.IsAlive)
        {
            _player.Respawn(_playerSpawn);
            OnPlayerRespawn?.Invoke(_player.Body.Position);
        }

        // Entity gravity-scale opt-out, applied right before uniform gravity integrates.
        foreach (var e in _entities) e.PreStep(Gravity);

        PhysicsWorld.StepSwept(_bodies, _chunks, dt, Gravity);

        // Sweep up entities that died this frame: remove from physics first, then
        // the IHittable list, then the entity list.
        for (int i = _entities.Count - 1; i >= 0; i--)
        {
            if (!_entities[i].IsDead) continue;
            _bodies.Remove(_entities[i].Body);
            _hittables.Remove(_entities[i]);
            _entities.RemoveAt(i);
        }
    }

    // Drag-to-build: while right-click is held, every cell the cursor sweeps through
    // is fed to TryRequestTile in path order. Reach is measured from the player center;
    // a sprout neighbour extends reach so a build can chain outward.
    private const float BuildReach = 64f;
    private const float ChainBuildReachMul = 2f;
    private void HandleBuildInput()
    {
        var input = _controller.Current;
        if (!input.RightClick) return;
        // Once an eruption is armed, RMB is committed to the gesture.
        if (_player.CurrentAction is BlockEruptionAction) return;

        var prev = _controller.GetPrevious(1);
        var segStart = prev.RightClick ? prev.MouseWorldPosition : input.MouseWorldPosition;
        var segEnd   = input.MouseWorldPosition;

        foreach (var (gtx, gty) in MouseSweep.Cells(segStart, segEnd))
        {
            var cellCenter = new Vector2(
                gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
                gty * Chunk.TileSize + Chunk.TileSize * 0.5f);
            float maxReach = HasSproutNeighbour(gtx, gty) ? BuildReach * ChainBuildReachMul : BuildReach;
            if (Vector2.DistanceSquared(_player.Body.Position, cellCenter) > maxReach * maxReach)
                continue;
            var node = _chunks.TryRequestTile(gtx, gty, _player.ActiveBlockType);
            if (node != null) _lastTilePlacedFrame = _player.Frame;
        }
    }

    private bool HasSproutNeighbour(int gtx, int gty) =>
        _chunks.Graph.TryGet(gtx,     gty + 1, out _) ||
        _chunks.Graph.TryGet(gtx - 1, gty,     out _) ||
        _chunks.Graph.TryGet(gtx + 1, gty,     out _) ||
        _chunks.Graph.TryGet(gtx,     gty - 1, out _);

    // Cheap, pure, order-stable hash of the gameplay-significant sim state (GGPO_PLAN
    // §F/§I.2). Two peers running the same confirmed inputs must produce the same
    // checksum at the same frame; a mismatch is a hard desync (the float-determinism
    // risk biting). FNV-1a over both players' pose/velocity, the entity set, and the
    // id/hit-id counters — exact float bits, no formatting tolerance. Not a snapshot:
    // it's a fingerprint for the desync guard, not enough to restore from.
    public ulong Checksum()
    {
        ulong h = 1469598103934665603UL;        // FNV offset basis
        void Mix(uint v)
        {
            h ^= v;
            h *= 1099511628211UL;                // FNV prime
        }
        void MixF(float f) => Mix((uint)BitConverter.SingleToInt32Bits(f));
        void MixBody(PhysicsBody b)
        {
            MixF(b.Position.X); MixF(b.Position.Y);
            MixF(b.Velocity.X); MixF(b.Velocity.Y);
        }

        MixBody(_player.Body);
        MixF(_player.Health);
        foreach (var (p, _) in _secondaryPlayers) { MixBody(p.Body); MixF(p.Health); }

        Mix((uint)_entities.Count);
        foreach (var e in _entities)
        {
            Mix((uint)e.Id);
            MixBody(e.Body);
            MixF(e.Health);
        }

        Mix((uint)_hitIds.Value);
        Mix((uint)_nextId);
        return h;
    }

    // ── Snapshot / restore (roadmap goal 4 §H) ──────────────────────────────────
    // Capture everything the sim reads or writes EXCEPT terrain: players (+ their
    // controller rings), entities, the combat dedupe table, moving-platform poses,
    // and the sim-level scalars (hit-id counter, id counter, tile-place frame, the
    // platform clock). Terrain (chunks/tiles) is out of scope here — goal 6.
    public SimSnapshot Snapshot()
    {
        var secondaries = new PlayerSnapshot[_secondaryPlayers.Count];
        var secCtrls    = new ControllerState[_secondaryPlayers.Count];
        for (int i = 0; i < _secondaryPlayers.Count; i++)
        {
            secondaries[i] = _secondaryPlayers[i].Player.CaptureState();
            secCtrls[i]    = _secondaryPlayers[i].Ctrl.Capture();
        }

        var entities = new EntitySnapshot[_entities.Count];
        for (int i = 0; i < _entities.Count; i++) entities[i] = _entities[i].Capture();

        var platforms = new PlatformState[_platforms.Count];
        for (int i = 0; i < _platforms.Count; i++)
            platforms[i] = new PlatformState { Position = _platforms[i].Rect.Position, Velocity = _platforms[i].Rect.Velocity };

        return new SimSnapshot
        {
            HitIdValue           = _hitIds.Value,
            LastTilePlacedFrame  = _lastTilePlacedFrame,
            NextId               = _nextId,
            Elapsed              = _elapsed,
            Primary              = _player.CaptureState(),
            PrimaryController    = _controller.Capture(),
            Secondaries          = secondaries,
            SecondaryControllers = secCtrls,
            Entities             = entities,
            Dedupe               = _combat.CaptureDedupe(h => h.HittableId),
            Platforms            = platforms,
            Terrain              = _chunks.CaptureTerrain(),
        };
    }

    public void Restore(SimSnapshot snap)
    {
        // Sim scalars first (id counter must be set before any rehydrate that mints
        // — none currently do, but keep the ordering honest).
        _hitIds.Value       = snap.HitIdValue;
        _lastTilePlacedFrame = snap.LastTilePlacedFrame;
        _nextId             = snap.NextId;
        _elapsed            = snap.Elapsed;

        // Terrain: rewind the dense-grid journal + restore the sparse structures.
        _chunks.RestoreTerrain(snap.Terrain);

        // Players (count is fixed across the sim's life — restore in place).
        _player.RestoreState(snap.Primary);
        _controller.Restore(snap.PrimaryController);
        for (int i = 0; i < _secondaryPlayers.Count; i++)
        {
            _secondaryPlayers[i].Player.RestoreState(snap.Secondaries[i]);
            _secondaryPlayers[i].Ctrl.Restore(snap.SecondaryControllers[i]);
        }

        // Entities: rebuild the live set from the snapshot. Restore into a still-live
        // entity where its id matches; otherwise rehydrate a fresh one. Any live
        // entity absent from the snapshot (spawned after the snapshot frame) is
        // dropped. The new lists are built in snapshot (spawn) order.
        var live = new Dictionary<int, Entity>(_entities.Count);
        foreach (var e in _entities) live[e.Id] = e;

        _entities.Clear();
        foreach (var es in snap.Entities)
        {
            if (live.TryGetValue(es.Id, out var existing)) { existing.RestoreInto(in es); _entities.Add(existing); }
            else                                            _entities.Add(es.Rehydrate(_hitIds));
        }

        // Rebuild the body + hittable lists in canonical order: primary player,
        // secondary players, then entities in spawn order. PhysicsWorld.StepSwept and
        // the combat passes iterate these, so identical order ⇒ identical stepping.
        _bodies.Clear();
        _hittables.Clear();
        _bodies.Add(_player.Body);
        _hittables.Add(_player);
        foreach (var (p, _) in _secondaryPlayers) { _bodies.Add(p.Body); _hittables.Add(p); }
        foreach (var e in _entities)              { _bodies.Add(e.Body); _hittables.Add(e); }

        // Platforms — restore pose; tickers re-derive motion from _elapsed next Step.
        for (int i = 0; i < _platforms.Count && i < snap.Platforms.Length; i++)
        {
            _platforms[i].Rect.Position = snap.Platforms[i].Position;
            _platforms[i].Rect.Velocity = snap.Platforms[i].Velocity;
        }

        // Combat dedupe — resolve snapshotted HittableIds back to live objects.
        var byId = new Dictionary<int, IHittable>(_hittables.Count);
        foreach (var h in _hittables) byId[h.HittableId] = h;
        _combat.RestoreDedupe(snap.Dedupe, id => byId.TryGetValue(id, out var h) ? h : null);
    }
}
