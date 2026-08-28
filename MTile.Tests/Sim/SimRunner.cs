using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Tests.Sim;

public record SimFrame(
    int    Frame,
    float  T,
    float  X,
    float  Y,
    float  Vx,
    float  Vy,
    float  Fx,     // AppliedForce from the movement state (before physics step)
    float  Fy,
    string State,
    bool   Transition  // true when state changed from previous frame
);

public class SimConfig
{
    public ChunkMap     Terrain       { get; init; } = new ChunkMap();
    public Vector2      StartPosition { get; init; } = Vector2.Zero;
    public Vector2      StartVelocity { get; init; } = Vector2.Zero;
    public InputScript  Script        { get; init; } = InputScript.Always(default);
    public int          Frames        { get; init; } = 120;
    public float        Dt            { get; init; } = 1f / 30f;
    public Vector2      Gravity       { get; init; } = new Vector2(0f, 600f);
}

// Per-player config for the multi-player runner (SimRunner.RunMulti). Each player
// brings its own start pose + script; all share the terrain, gravity, dt, and the
// HitboxWorld/HurtboxWorld so attacks between players resolve like in real play.
public class SimPlayer
{
    public Vector2     StartPosition { get; init; } = Vector2.Zero;
    public Vector2     StartVelocity { get; init; } = Vector2.Zero;
    public InputScript Script        { get; init; } = InputScript.Always(default);
    // CombatSystem skips intersection when attacker.Faction == target.Faction.
    // Solo play uses Player1 for both; multi-player tests can give each player its
    // own faction (Player1/Player2) so cross-player hits register, or tag opponents
    // Neutral/Enemy.
    public Faction     Faction       { get; init; } = MTile.Faction.Player1;
}

public class SimConfigMulti
{
    public ChunkMap         Terrain { get; init; } = new ChunkMap();
    public IList<SimPlayer> Players { get; init; } = new List<SimPlayer>();
    public int              Frames  { get; init; } = 120;
    public float            Dt      { get; init; } = 1f / 30f;
    public Vector2          Gravity { get; init; } = new Vector2(0f, 600f);
}

// The multi-player runner's entity world: a spawner + chunk provider so actions that
// own an entity (BlockGrabAction's pulling point, every projectile throw) work
// headlessly. Ids are minted from firstIndex upward (players take 1..n); Resolve
// matches the full id like Simulation's World does. Dead entities are swept at the end
// of each frame, mirroring Simulation.Step — so Resolve returns a dying entity within
// the frame it died and null from the next frame on.
public sealed class HeadlessEntityWorld : IEntitySpawner, IChunkProvider
{
    private readonly List<Entity> _entities = new();
    private int _nextIndex;

    public ChunkMap        Chunks { get; }
    public HitIdAllocator  HitIds { get; }
    public IReadOnlyList<Entity> Entities => _entities;

    public HeadlessEntityWorld(ChunkMap chunks, HitIdAllocator hitIds, int firstIndex)
    {
        Chunks     = chunks;
        HitIds     = hitIds;
        _nextIndex = firstIndex;
    }

    public void SpawnEntity(Entity e)
    {
        e.Id = new EntityId(_nextIndex++);
        _entities.Add(e);
    }

    public Entity Resolve(EntityId id)
    {
        foreach (var e in _entities) if (e.Id == id) return e;
        return null;
    }

    public void SweepDead() => _entities.RemoveAll(e => e.IsDead);
}

public static class SimRunner
{
    public static SimFrame[] Run(SimConfig cfg)
    {
        var player  = new PlayerCharacter(cfg.StartPosition);
        player.Body.Velocity = cfg.StartVelocity;
        var bodies  = new List<PhysicsBody> { player.Body };
        var ctrl    = new Controller();
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var frames  = new List<SimFrame>(cfg.Frames);
        SimFrame? prev = null;
        string lastState = "";

        for (int f = 0; f < cfg.Frames; f++)
        {
            var input = cfg.Script.Get(f, prev);
            ctrl.InjectInput(input);

            cfg.Terrain.TickSprouts(cfg.Dt);

            player.Update(ctrl, cfg.Terrain, hitboxes, hurtboxes, cfg.Dt);

            // Capture AppliedForce HERE — PhysicsWorld resets it to zero during StepSwept.
            float fx = player.Body.AppliedForce.X;
            float fy = player.Body.AppliedForce.Y;

            PhysicsWorld.StepSwept(bodies, cfg.Terrain, cfg.Dt, cfg.Gravity);

            string state = player.CurrentStateName;
            bool transition = state != lastState;
            lastState = state;

            prev = new SimFrame(f, f * cfg.Dt,
                player.Body.Position.X, player.Body.Position.Y,
                player.Body.Velocity.X, player.Body.Velocity.Y,
                fx, fy, state, transition);
            frames.Add(prev);
        }

        return frames.ToArray();
    }

    // Multi-player headless sim. Each player has its own Controller (fed by its
    // own InputScript) and a per-frame trace. All players share the HitboxWorld /
    // HurtboxWorld so cross-player attacks resolve through CombatSystem.Apply —
    // letting tests script e.g. "player 0 spams slash combo while player 1 holds
    // a movement direction" and assert that player 1 doesn't escape the cone.
    //
    // Returns a SimFrame[][] indexed [playerIdx][frame]. Optional callbacks:
    //   onFrame    — fires AFTER each frame's full update (combat + physics applied);
    //                use to sample non-frame state (Combat.HitstunActive, Health, …).
    //   outPlayers — fires once at the end with the final PlayerCharacter[].
    //   onFrameEntities — like onFrame, plus the live entity list (spawned projectiles,
    //                the block-grab pulling point) after that frame's dead-sweep.
    public static SimFrame[][] RunMulti(SimConfigMulti cfg,
                                        Action<int, PlayerCharacter[]>? onFrame = null,
                                        Action<PlayerCharacter[]>? outPlayers = null,
                                        Action<int, PlayerCharacter[], IReadOnlyList<Entity>>? onFrameEntities = null)
    {
        int n = cfg.Players.Count;
        if (n == 0) return Array.Empty<SimFrame[]>();

        var players = new PlayerCharacter[n];
        var ctrls   = new Controller[n];
        var bodies  = new List<PhysicsBody>(n);
        var traces  = new List<SimFrame>[n];
        var lastStates = new string[n];
        var prevs   = new SimFrame?[n];
        // Shared HitId source across all players so cross-player attack ids are unique.
        var hitIds = new HitIdAllocator();

        for (int i = 0; i < n; i++)
        {
            // Distinct EntityId per player so the combat dedupe table (keyed on
            // EntityId) doesn't conflate them. Ids start at 1; None (0) is reserved.
            players[i] = new PlayerCharacter(cfg.Players[i].StartPosition) { HitIds = hitIds, Id = new EntityId(i + 1) };
            players[i].Body.Velocity = cfg.Players[i].StartVelocity;
            players[i].Faction       = cfg.Players[i].Faction;
            ctrls[i] = new Controller();
            bodies.Add(players[i].Body);
            traces[i] = new List<SimFrame>(cfg.Frames);
            lastStates[i] = "";
        }

        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var fields    = new ForceFieldWorld();
        var combat    = new CombatSystem();
        // Recoil tally lives on CombatSystem and is read by actions in
        // ApplyActionForces. Wire it through to each player so attack recoil
        // (stab pogo, etc.) actually fires in multi-player sim tests.
        for (int i = 0; i < n; i++) players[i].CombatSystem = combat;

        // Entities (projectiles, the block-grab pulling point) — ids after the players'.
        var world = new HeadlessEntityWorld(cfg.Terrain, hitIds, firstIndex: n + 1);
        var entityScratch = new List<Entity>();
        var bodyScratch   = new List<PhysicsBody>();

        IHittable ResolveHittable(EntityId id)
        {
            foreach (var p in players) if (p.Id == id) return p;
            return world.Resolve(id);
        }

        for (int f = 0; f < cfg.Frames; f++)
        {
            cfg.Terrain.TickSprouts(cfg.Dt);

            // Mirror Simulation.Step phase order: clear combat registries → publish
            // hurtboxes → run each player's Update (which publishes hitboxes +
            // force fields) → entity Update → CombatSystem.Apply → field forces →
            // physics step → dead-entity sweep.
            hitboxes.Clear();
            hurtboxes.Clear();
            fields.Clear();
            for (int i = 0; i < n; i++) players[i].PublishHurtboxes(hurtboxes);
            foreach (var e in world.Entities) e.PublishHurtboxes(hurtboxes);

            // Inject input + update each player. Capture AppliedForce per-player
            // before StepSwept zeroes them.
            var fx = new float[n];
            var fy = new float[n];
            for (int i = 0; i < n; i++)
            {
                var input = cfg.Players[i].Script.Get(f, prevs[i]);
                ctrls[i].InjectInput(input);
                players[i].Update(ctrls[i], cfg.Terrain, hitboxes, hurtboxes, cfg.Dt, spawner: world, forceFields: fields);
                fx[i] = players[i].Body.AppliedForce.X;
                fy[i] = players[i].Body.AppliedForce.Y;
            }

            // Entities spawned by a sibling's Update skip their first frame, as in
            // Simulation.Step; ones spawned by a player's action run this frame.
            entityScratch.Clear();
            entityScratch.AddRange(world.Entities);
            foreach (var e in entityScratch) e.Update(cfg.Dt, players[0], hitboxes, world);

            combat.Apply(cfg.Terrain, hitboxes, hurtboxes, ResolveHittable);

            PhysicsBody ResolveBody(EntityId id)
            {
                foreach (var p in players) if (p.Id == id) return p.Body;
                return world.Resolve(id)?.Body;
            }
            ForceFieldSystem.Apply(fields, hurtboxes, ResolveBody, cfg.Dt,
                onGrabHeld: id => { foreach (var p in players) if (p.Id == id) p.Combat.MarkGrabbed(p.Frame); },
                onThrown:   id => { foreach (var p in players) if (p.Id == id) p.Combat.RegisterThrown(p.Frame, cfg.Dt); });

            // Entity gravity-scale opt-out, then every body (players + live entities,
            // spawn order) through the solver.
            foreach (var e in world.Entities) e.PreStep(cfg.Gravity);
            bodyScratch.Clear();
            bodyScratch.AddRange(bodies);
            foreach (var e in world.Entities) bodyScratch.Add(e.Body);
            PhysicsWorld.StepSwept(bodyScratch, cfg.Terrain, cfg.Dt, cfg.Gravity);
            world.SweepDead();

            for (int i = 0; i < n; i++)
            {
                string state = players[i].CurrentStateName;
                bool transition = state != lastStates[i];
                lastStates[i] = state;

                var sf = new SimFrame(f, f * cfg.Dt,
                    players[i].Body.Position.X, players[i].Body.Position.Y,
                    players[i].Body.Velocity.X, players[i].Body.Velocity.Y,
                    fx[i], fy[i], state, transition);
                traces[i].Add(sf);
                prevs[i] = sf;
            }

            onFrame?.Invoke(f, players);
            onFrameEntities?.Invoke(f, players, world.Entities);
        }

        outPlayers?.Invoke(players);
        var result = new SimFrame[n][];
        for (int i = 0; i < n; i++) result[i] = traces[i].ToArray();
        return result;
    }
}
