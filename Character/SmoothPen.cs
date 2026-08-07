using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Cursor position / velocity tracker
public class SmoothPen
{
    public Vector2 Position;
    public Vector2 Velocity;

    public SmoothPen(Vector2 initial)
    {
        Position = initial;
        Velocity = Vector2.Zero;
    }

    // Snap pen back to a known position with zero velocity. Used by
    // BlockReadyAction.Update when an in-flight charge cancels and the sample
    // buffer rewinds.
    public void Reset(Vector2 to)
    {
        Position = to;
        Velocity = Vector2.Zero;
    }

    public void Update(Vector2 target, float dt)
    {
        if (dt <= 0f) return;
        Velocity = (target - Position) / dt;
        Position = target;
    }

    // Critically-damped follow toward `target`: the point trails the target with real
    // inertia and settles without ever crossing it. Used by the RMB paint gesture, where
    // overshoot would deposit mass on the wrong side of the stroke.
    //
    // This is the closed-form critically-damped step (the SmoothDamp form) rather than a
    // spring integrated explicitly. An explicit-Euler spring at critical damping still
    // overshoots by a hair, and its stability depends on dt — bad news for something the
    // sim replays at a fixed step but that tests drive at 1/30. The rational approximation
    // to exp(-x) below is unconditionally stable, monotone, and uses no transcendentals.
    //
    // smoothTime is the lag time constant: roughly how long the point takes to close the
    // gap. State is (pos, vel), both plain Vector2, so callers can keep it in ActionVars
    // and get snapshotting for free.
    public static void CriticallyDampedStep(ref Vector2 pos, ref Vector2 vel,
                                            Vector2 target, float smoothTime, float dt)
    {
        if (dt <= 0f) return;
        smoothTime = MathF.Max(smoothTime, 1e-4f);

        float omega = 2f / smoothTime;
        float x     = omega * dt;
        float decay = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

        Vector2 change = pos - target;
        Vector2 temp   = (vel + change * omega) * dt;
        vel = (vel - temp * omega) * decay;
        pos = target + (change + temp) * decay;
    }
}
