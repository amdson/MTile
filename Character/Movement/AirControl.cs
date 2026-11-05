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
            if (ctx.Dt <= 0f) return inputX * accel;

            // Signed velocity component along the input direction. Positive = moving
            // with the input; negative = moving against it.
            float vInInputDir = inputX * vx;

            if (vInInputDir < maxSpeed)
            {
                // Below cap in input direction. Apply accel, but cap the force so a
                // single frame's velocity update doesn't overshoot maxSpeed. The
                // previous formulation here added full accel and subtracted only
                // *excess*-velocity-over-cap as the brake — which leaves an
                // equilibrium of maxSpeed + accel·dt rather than maxSpeed. With low
                // modifier values that overshoot dominated, so the cap was barely
                // visible.
                float headroom = maxSpeed - vInInputDir;
                float maxForceToLandOnCap = headroom / ctx.Dt;
                return inputX * MathF.Min(accel, maxForceToLandOnCap);
            }
            else
            {
                // At or above the cap in the input direction: no accel, and NO brake
                // (2026-11-05). The cap limits what air control can ADD; speed the body
                // brought into the air - a running jump's takeoff at walk speed, a
                // launch, a dash - is kept while the player holds its direction, so a
                // jump starts with the ground's lateral speed and MaxAirSpeed (0.9x walk)
                // only governs what you can build up from a standing jump. Releasing the
                // direction hands the speed to the drag branch below, as before. (The old
                // one-frame brake to the cap also erased knockback whenever the victim
                // held a direction, which PreserveExternalVelocity used to special-case.)
                return 0f;
            }
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
