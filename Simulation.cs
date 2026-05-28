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

    private readonly ChunkMap _chunks;
    private readonly HitboxWorld  _hitboxes  = new();
    private readonly HurtboxWorld _hurtboxes = new();
    // The ECS world: owns EntityId allocation and the live-only registry components
    // (PhysicsBodyComponent / EntityRef / PlayerRef). Single source of truth for the
    // body + entity sets; the lists it replaced (_bodies/_entities/_hittables) are gone.
    private readonly World _world = new();
    // Per-step projections of the World into plain lists. Rebuilt each Step in World
    // (spawn) order; never the source of truth, just a view the physics API + the
    // collect-then-iterate phases consume. Reused to keep allocations off the hot path.
    private readonly List<PhysicsBody> _bodyScratch   = new();
    private readonly List<Entity>      _entityScratch = new();
    // Ids of the entities to rebuild during Restore, collected from the restored
    // EntityData store before re-registering the live-only ref stores (so the
    // re-registration doesn't structurally modify a store mid-query).
    private readonly List<EntityId>    _restoreIdScratch = new();
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

    // Absolute elapsed sim time (seconds), advanced by one FixedDt per Step. Drives
    // moving-platform tickers as a pure function of time so platforms snapshot/restore
    // with no hidden accumulator state.
    private float _elapsed;

    // Configure the World's component stores once per construction, before any entity
    // is registered. Two flavors:
    //   • Live-only ref stores (PlayerRef/EntityRef/PhysicsBodyComponent) wrap class
    //     objects — skipped by the World snapshot, rebuilt from rehydrated entities on
    //     restore.
    //   • Snapshotted value stores (EntityData/PlayerData/BodyStateComp) hold the
    //     serializable entity + player state and ARE captured. BodyStateComp and
    //     PlayerData wrap reference state (a body's maintained-contact list; a player's
    //     history arrays / cloned abilities / gesture samples), so they register
    //     deep-clone hooks — a shallow array copy would alias that live state into the
    //     snapshot.
    private void MarkWorldStores()
    {
        _world.MarkLiveOnly<PlayerRef>();
        _world.MarkLiveOnly<EntityRef>();
        _world.MarkLiveOnly<PhysicsBodyComponent>();
        _world.SetCloner<BodyStateComp>(b => new BodyStateComp { State = b.State.DeepCopy() });
        _world.SetCloner<PlayerData>(d => d.DeepCopy());
    }

    // Register a player/entity in the World: mint its id, stamp it on the object, and
    // add the registry components. Order of registration across the sim's lifetime is
    // canonical (primary, secondaries, entities in spawn order) and reproduced on
    // restore, so World iteration matches the old list order.
    private void RegisterPlayer(PlayerCharacter p)
    {
        var id = _world.Create();
        p.Id = id;
        _world.Add<PlayerRef>(id).Obj = p;
        _world.Add<PhysicsBodyComponent>(id).Body = p.Body;
        // Snapshotted state mirrors — empty now, populated from the live player at each
        // Snapshot() via CaptureState; the World captures them as the rollback substrate.
        _world.Add<PlayerData>(id);
        _world.Add<BodyStateComp>(id);
    }

    // Resolve an EntityId to its live IHittable for CombatSystem dispatch.
    private IHittable ResolveHittable(EntityId id)
    {
        if (_world.Has<PlayerRef>(id)) return _world.Get<PlayerRef>(id).Obj;
        if (_world.Has<EntityRef>(id)) return _world.Get<EntityRef>(id).Obj;
        return null;
    }

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
    // Render/test views, projected from the World on access (spawn order). Not on the
    // sim hot path — Game1 reads them while drawing, tests while asserting.
    public IReadOnlyList<Entity> Entities
    {
        get { var l = new List<Entity>(); foreach (var r in _world.Query<EntityRef>()) l.Add(r.Component1.Obj); return l; }
    }
    public IReadOnlyList<(MovingRectangle Rect, Color Color)> Platforms => _platforms;
    public IReadOnlyList<PhysicsBody> Bodies
    {
        get { var l = new List<PhysicsBody>(); foreach (var r in _world.Query<PhysicsBodyComponent>()) l.Add(r.Component1.Body); return l; }
    }
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
        MarkWorldStores();
        _player = new PlayerCharacter(_playerSpawn) { HitIds = _hitIds, CombatSystem = _combat };
        RegisterPlayer(_player);
        populate?.Invoke(this);
    }

    public Simulation(GameConfig config, Stage stage)
    {
        _chunks = new ChunkMap();
        // Title-relative; chunk files referenced by the config resolve next to it.
        TerrainLoader.Load($"Levels/{stage.TerrainConfig}", _chunks);

        _playerSpawn = stage.PlayerSpawn;
        MarkWorldStores();
        _player = new PlayerCharacter(_playerSpawn) { HitIds = _hitIds, CombatSystem = _combat };
        RegisterPlayer(_player);

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
        var id = _world.Create();
        e.Id = id;
        _world.Add<EntityRef>(id).Obj = e;
        _world.Add<PhysicsBodyComponent>(id).Body = e.Body;
        // Snapshotted state mirrors — empty now, populated from the live object at each
        // Snapshot() via CaptureState; the World captures them as the rollback substrate.
        _world.Add<EntityData>(id);
        _world.Add<BodyStateComp>(id);
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
            Faction = Factions.ForPlayerIndex(_secondaryPlayers.Count + 1),
            CombatSystem = _combat,
        };
        RegisterPlayer(player);
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
        // Drag-to-build also lives per-player now, inside BlockReadyAction.

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
        // Hurtboxes in canonical order: primary, secondaries, then entities (spawn order).
        _player.PublishHurtboxes(_hurtboxes);
        foreach (var (p, _) in _secondaryPlayers) p.PublishHurtboxes(_hurtboxes);
        foreach (var r in _world.Query<EntityRef>()) r.Component1.Obj.PublishHurtboxes(_hurtboxes);

        _player.Update(_controller, _chunks, _hitboxes, _hurtboxes, dt, this);
        foreach (var (p, c) in _secondaryPlayers)
            p.Update(c, _chunks, _hitboxes, _hurtboxes, dt);

        // Collect the current entity set before updating so entities spawned during a
        // sibling's Update skip their first frame (matches the old count-snapshot).
        _entityScratch.Clear();
        foreach (var r in _world.Query<EntityRef>()) _entityScratch.Add(r.Component1.Obj);
        foreach (var e in _entityScratch) e.Update(dt, _player, _hitboxes, this);

        _combat.Apply(_chunks, _hitboxes, _hurtboxes, ResolveHittable);

        // Player respawn on death.
        if (!_player.IsAlive)
        {
            _player.Respawn(_playerSpawn);
            OnPlayerRespawn?.Invoke(_player.Body.Position);
        }

        // Entity gravity-scale opt-out, applied right before uniform gravity integrates.
        // Fresh query so entities spawned this frame are included (as before).
        foreach (var r in _world.Query<EntityRef>()) r.Component1.Obj.PreStep(Gravity);

        // Project the World's bodies into the scratch list (spawn order) for the solver.
        _bodyScratch.Clear();
        foreach (var r in _world.Query<PhysicsBodyComponent>()) _bodyScratch.Add(r.Component1.Body);
        PhysicsWorld.StepSwept(_bodyScratch, _chunks, dt, Gravity);

        // Sweep up entities that died this frame: collect dead ids, then destroy them in
        // the World (drops their EntityRef + PhysicsBodyComponent).
        _entityScratch.Clear();
        foreach (var r in _world.Query<EntityRef>())
            if (r.Component1.Obj.IsDead) _entityScratch.Add(r.Component1.Obj);
        foreach (var e in _entityScratch) _world.Destroy(e.Id);
    }

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

        // Entities in World (spawn) order — same fingerprint shape as the old list.
        int entityCount = 0;
        foreach (var _ in _world.Query<EntityRef>()) entityCount++;
        Mix((uint)entityCount);
        foreach (var r in _world.Query<EntityRef>())
        {
            var e = r.Component1.Obj;
            Mix((uint)e.Id.Index);
            MixBody(e.Body);
            MixF(e.Health);
        }

        Mix((uint)_hitIds.Value);
        Mix((uint)_world.SlotCount);   // analogue of the old monotonic id counter
        return h;
    }

    // ── Snapshot / restore (roadmap goal 4 §H) ──────────────────────────────────
    // Capture everything the sim reads or writes EXCEPT terrain: players (+ their
    // controller rings), entities, the combat dedupe table, moving-platform poses,
    // and the sim-level scalars (hit-id counter, id counter, tile-place frame, the
    // platform clock). Terrain (chunks/tiles) is out of scope here — goal 6.
    public SimSnapshot Snapshot()
    {
        var secCtrls = new ControllerState[_secondaryPlayers.Count];
        for (int i = 0; i < _secondaryPlayers.Count; i++)
            secCtrls[i] = _secondaryPlayers[i].Ctrl.Capture();

        // Sync every live player + entity's serializable state into its World value
        // components (PlayerData/EntityData + BodyStateComp), THEN capture the World —
        // those component stores ARE the snapshot now; there's no separate per-player or
        // per-entity struct array. Player capture must precede _world.Capture() below.
        _player.CaptureState(_world);
        foreach (var (p, _) in _secondaryPlayers) p.CaptureState(_world);
        foreach (var r in _world.Query<EntityRef>()) r.Component1.Obj.CaptureState(_world);

        var platforms = new PlatformState[_platforms.Count];
        for (int i = 0; i < _platforms.Count; i++)
            platforms[i] = new PlatformState { Position = _platforms[i].Rect.Position, Velocity = _platforms[i].Rect.Velocity };

        return new SimSnapshot
        {
            HitIdValue           = _hitIds.Value,
            World                = _world.Capture(),
            Elapsed              = _elapsed,
            PrimaryController    = _controller.Capture(),
            SecondaryControllers = secCtrls,
            Dedupe               = _combat.CaptureDedupe(),
            Platforms            = platforms,
            Terrain              = _chunks.CaptureTerrain(),
        };
    }

    public void Restore(SimSnapshot snap)
    {
        _hitIds.Value = snap.HitIdValue;
        _elapsed      = snap.Elapsed;

        // Snapshot the live entity set (by id) BEFORE the World restore clears the
        // registry stores — used to restore-in-place where an id still matches.
        var live = new Dictionary<EntityId, Entity>();
        foreach (var r in _world.Query<EntityRef>()) live[r.Component1.Obj.Id] = r.Component1.Obj;

        // Restore the World's id bookkeeping (slot generations + free list) and clear
        // the live-only ref stores. We re-register every player/entity below, in
        // canonical order, so World iteration order is reproduced exactly.
        _world.Restore(snap.World);

        // Terrain: rewind the dense-grid journal + restore the sparse structures.
        _chunks.RestoreTerrain(snap.Terrain);

        // Players (count is fixed across the sim's life). Re-register the live-only refs
        // at each player's stable id, then restore state FROM the World components the
        // World.Restore above just rehydrated (PlayerData + BodyStateComp). Controllers
        // live outside the World, so they restore from the snapshot's own arrays.
        _world.Add<PlayerRef>(_player.Id).Obj = _player;
        _world.Add<PhysicsBodyComponent>(_player.Id).Body = _player.Body;
        _player.RestoreState(_world);
        _controller.Restore(snap.PrimaryController);
        for (int i = 0; i < _secondaryPlayers.Count; i++)
        {
            var p = _secondaryPlayers[i].Player;
            _world.Add<PlayerRef>(p.Id).Obj = p;
            _world.Add<PhysicsBodyComponent>(p.Id).Body = p.Body;
            p.RestoreState(_world);
            _secondaryPlayers[i].Ctrl.Restore(snap.SecondaryControllers[i]);
        }

        // Entities: the restored EntityData store (spawn order) is the authoritative set
        // to rebuild — its ids + Kinds came back with the World capture. Collect the ids
        // first (so re-registering the live-only ref stores doesn't modify a store mid-
        // query), then for each: restore into a still-live entity where the id matches,
        // else rehydrate a fresh one from its captured Kind. Live entities absent from
        // the snapshot (spawned after the snapshot frame) are never re-registered.
        _restoreIdScratch.Clear();
        foreach (var r in _world.Query<EntityData>()) _restoreIdScratch.Add(r.Entity);
        foreach (var id in _restoreIdScratch)
        {
            Entity e;
            if (live.TryGetValue(id, out var existing)) { e = existing; }
            else
            {
                var body = _world.Get<BodyStateComp>(id).State;
                e = EntityFactory.Rehydrate(in _world.Get<EntityData>(id), in body, _hitIds);
                e.Id = id;
            }
            _world.Add<EntityRef>(id).Obj = e;
            _world.Add<PhysicsBodyComponent>(id).Body = e.Body;
            e.RestoreState(_world);
        }

        // Platforms — restore pose; tickers re-derive motion from _elapsed next Step.
        for (int i = 0; i < _platforms.Count && i < snap.Platforms.Length; i++)
        {
            _platforms[i].Rect.Position = snap.Platforms[i].Position;
            _platforms[i].Rect.Velocity = snap.Platforms[i].Velocity;
        }

        // Combat dedupe — keyed on EntityId now, so it's a direct value restore.
        _combat.RestoreDedupe(snap.Dedupe);
    }
}
