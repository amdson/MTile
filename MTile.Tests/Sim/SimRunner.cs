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
}
