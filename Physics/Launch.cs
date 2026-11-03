using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// How much of a body's velocity a hit or throw put there, and how that share decays.
//
// The velocity itself stays in Body.Velocity — physics sees one velocity and never reads
// this. A launch is bookkeeping on top of it: when a hit lands its Δv goes into the
// velocity ONCE and is recorded here as Vel; every step after, LaunchSystem takes
// Decay·dt of it back out, along the launch's own direction, so the hit's contribution
// bleeds off on a schedule while anything else the body is doing (a jump, a pogo, drift)
// is left alone. Fast hits can then start fast without flying off the map.
//
// Invariant the reconciler keeps: |Vel| never exceeds the body's real speed along Vel's
// direction. Whatever physics or the player removed from that motion, the launch gives up
// too — otherwise decay would later take away motion the hit no longer accounts for.
// Specific cases get explicit handlers first (a bounce reflects the launch); that clamp
// is the fallback for every case without one.
public struct Launch
{
    public Vector2 Vel;              // the hit's remaining share of Body.Velocity, px/s
    public float   Decay;            // px/s², linear
    public float   ImpactMass;       // authored Impact.Mass multiplier at full launch (1 = none)
    public float   InitialSpeed;     // |Vel| when it began — ImpactMass fades against this

    public readonly bool Active => Vel.LengthSquared() > 0f;
}

public static class LaunchSystem
{
    // A launch below this is over: the leftover is ordinary velocity.
    private const float EndSpeed = 1f;

    // A hit lands: `dv` goes into `velocity` and becomes the body's launch, replacing any
    // launch already running (the old one's remaining share is taken back out first, as
    // Smash does — so a follow-up hit's trajectory depends on that hit alone, never on
    // how much of the previous launch happened to be left). `velocity` is the body's
    // real velocity — Body.Velocity, or the owner's hitstop stash while it is held.
    public static void Hit(PhysicsBody body, ref Vector2 velocity, Vector2 dv,
                           float decay, float impactMass)
    {
        velocity -= body.Launch.Vel;
        velocity += dv;
        Begin(body, dv, decay, impactMass);
    }

    // Claim velocity the body already has as a launch, without adding any (a throw: the
    // grab's force field has already brought the victim up to speed).
    public static void Claim(PhysicsBody body, Vector2 launchVel, float decay, float impactMass)
        => Begin(body, launchVel, decay, impactMass);

    private static void Begin(PhysicsBody body, Vector2 vel, float decay, float impactMass)
    {
        float speed = vel.Length();
        if (decay <= 0f || speed < EndSpeed) { body.Launch = default; return; }
        body.Launch = new Launch
        {
            Vel          = vel,
            Decay        = decay,
            ImpactMass   = MathF.Max(1f, impactMass),
            InitialSpeed = speed,
        };
    }

    // The sweep with launches around it. PhysicsWorld.StepSwept stays launch-agnostic;
    // every sim loop calls this instead so the three phases can't drift apart.
    public static void StepSwept(IReadOnlyList<PhysicsBody> bodies, ChunkMap chunks,
                                 float dt, Vector2 gravity)
    {
        for (int i = 0; i < bodies.Count; i++) PreStep(bodies[i], dt);
        PhysicsWorld.StepSwept(bodies, chunks, dt, gravity);
        for (int i = 0; i < bodies.Count; i++) PostStep(bodies[i]);
    }

    // Before the sweep: set the impact-mass input, then decay the launch by taking its
    // decrement back out of the real velocity.
    public static void PreStep(PhysicsBody body, float dt)
    {
        ref var l = ref body.Launch;
        if (!l.Active) { body.ImpactMassScale = 1f; return; }

        // Fades with the launch: hardest straight off the hit, normal by the time the
        // launch is spent — a wall met early is smashed, one met late just stops you.
        float speed = l.Vel.Length();
        body.ImpactMassScale = 1f + (l.ImpactMass - 1f) * MathF.Min(1f, speed / l.InitialSpeed);

        if (body.Frozen) return;   // hitstop hold: Velocity is parked, the stash is real

        float next = MathF.Max(0f, speed - l.Decay * dt);
        var nextVel = l.Vel * (next / speed);
        body.Velocity -= l.Vel - nextVel;
        l.Vel = next < EndSpeed ? Vector2.Zero : nextVel;
    }

    // After the sweep: explicit handlers for what physics reported, then the continuity
    // clamp for everything else.
    public static void PostStep(PhysicsBody body)
    {
        ref var l = ref body.Launch;
        if (!l.Active || body.Frozen) return;

        // Bounce handler: physics reflected the real velocity; reflect the launch the
        // same way so it keeps riding the rebound (same e ⇒ the two stay aligned and the
        // clamp below is a no-op). A stop or break-through needs no handler — the clamp
        // trims the launch to whatever velocity survived.
        var events = body.ContactEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (ev.Outcome != ContactOutcome.Bounce) continue;
            float vn = Vector2.Dot(l.Vel, ev.Normal);
            if (vn < 0f) l.Vel -= (1f + ev.Restitution) * vn * ev.Normal;
        }

        // Continuity: the launch can't exceed the real speed along its own direction.
        float speed = l.Vel.Length();
        var dir = l.Vel / speed;
        float real = Vector2.Dot(body.Velocity, dir);
        if (real < speed) speed = MathF.Max(0f, real);
        l.Vel = speed < EndSpeed ? Vector2.Zero : dir * speed;
        if (!l.Active) l = default;
    }
}
