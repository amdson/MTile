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
}
