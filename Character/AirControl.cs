using System;

namespace MTile;

public static class AirControl
{
    public static float Apply(EnvironmentContext ctx, float accel, float maxSpeed, float drag)
    {
        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        float vx     = ctx.Body.Velocity.X;

        if (inputX != 0f)
        {
            float fx = inputX * accel;
            float excess = MathF.Abs(vx) - maxSpeed;
            if (excess > 0f && MathF.Sign(vx) == MathF.Sign(inputX) && ctx.Dt > 0f)
                fx -= MathF.Sign(vx) * excess / ctx.Dt;
            return fx;
        }

        if (ctx.Dt > 0f)
            return Math.Clamp(-vx / ctx.Dt, -drag, drag);
        return 0f;
    }

    public static float SoftClampVelocity(float v, float target, float maxAccel, float dt)
    {
        if (dt <= 0f) return 0f;
        float needed = (target - v) / dt;
        return Math.Clamp(needed, -maxAccel, maxAccel);
    }
}
