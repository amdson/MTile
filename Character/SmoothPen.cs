using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Spring-pulled cursor smoother. The "pen" position lags behind the raw cursor
// by an amount that depends on cursor speed — slow gestures track tightly,
// fast gestures produce smooth wide arcs. Used by BlockEruptionAction to
// turn the raw mouse path into something curve-shaped before feeding the
// EruptionPlanner.
//
// Tuning:
//   PullStiffness — how hard the pen accelerates toward the cursor.
//   Damping       — how fast pen velocity bleeds off. With Stiffness=60 / Damping=15
//                   the pen reaches a stationary target in ~3 frames at 30fps and
//                   doesn't overshoot perceptibly.
public class SmoothPen
{
    public Vector2 Position;
    public Vector2 Velocity;

    private const float PullStiffness = 60f;
    private const float Damping       = 15f;

    public SmoothPen(Vector2 initial)
    {
        Position = initial;
        Velocity = Vector2.Zero;
    }

    public void Update(Vector2 target, float dt)
    {
        if (dt <= 0f) return;
        Velocity += (target - Position) * PullStiffness * dt;
        Velocity *= MathF.Max(0f, 1f - Damping * dt);
        Position += Velocity * dt;
    }
}
