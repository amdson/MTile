using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Purified baseline locomotion feedforwards (Plans/BALLISTIC_CORRECTOR_PLAN.md §2).
// Pure functions of (body scalars, input, config, modifiers) — no EnvironmentContext,
// no world queries, no statics — so BallisticPredictor can integrate the SAME drive
// the live tick applies. Until the live states are switched over at the corrector's
// integration steps (build steps 4+), these are verbatim MIRRORS of the state code:
//
//   Air    mirrors AirControl.Apply (FallingState's horizontal drive)
//   Ground mirrors StandingState.Update's spring + anti-pop + walk block
//
// The mirror contract is enforced by BallisticPredictorTests' sample-for-sample
// parity against a real SimRunner rollout — a change to either side that isn't
// made to both breaks those tests loudly. Do not "improve" one copy in place.
public static class BaselineFeedforward
{
    // Horizontal air drive. Mirror of AirControl.Apply with the context reads
    // (Input, Body.Velocity.X, Dt, Modifiers.PreserveExternalVelocity) lifted
    // into parameters. accel/maxSpeed/drag arrive pre-multiplied by modifiers.
    public static float Air(float inputX, float vx, float accel, float maxSpeed, float drag,
                            bool preserveExternalVelocity, float dt)
    {
        if (inputX != 0f)
        {
            if (dt <= 0f) return inputX * accel;

            float vInInputDir = inputX * vx;

            if (vInInputDir < maxSpeed)
            {
                float headroom = maxSpeed - vInInputDir;
                float maxForceToLandOnCap = headroom / dt;
                return inputX * MathF.Min(accel, maxForceToLandOnCap);
            }
            else
            {
                if (preserveExternalVelocity) return 0f;
                float excess = vInInputDir - maxSpeed;
                return -inputX * excess / dt;
            }
        }

        if (dt > 0f)
            return Math.Clamp(-vx / dt, -drag, drag);
        return 0f;
    }

    // Grounded standing force: hover spring + walk drive. Mirror of
    // StandingState.Update's force block against its refreshed ground FSD.
    // walkAccel/maxWalkSpeed arrive pre-multiplied by modifiers.
    // TEMP EXPERIMENT (throwaway): the anti-pop rise clamp (and its
    // ambientLiftActive exemption gate) is removed here and in the live
    // Standing/Crouched mirrors — corrector corrections must not be fought by
    // hidden counter-forces while testing.
    public static Vector2 Ground(
        Vector2 pos, Vector2 vel,
        Vector2 groundPos, Vector2 groundNormal, Vector2 groundSurfaceVel, float minDistance,
        float inputX, float walkAccel, float maxWalkSpeed,
        bool preserveExternalVelocity, bool ambientLiftActive,
        float springK, float springDamping, float springMaxRiseSpeed,
        float dt)
    {
        var force = Vector2.Zero;

        float dist           = Vector2.Dot(pos - groundPos, groundNormal);
        float gap            = minDistance - dist;
        float velAlongNormal = Vector2.Dot(vel - groundSurfaceVel, groundNormal);
        if (gap > 0f)
            force += groundNormal * (gap * springK - velAlongNormal * springDamping);

        if (inputX != 0f)
        {
            force.X += inputX * walkAccel;
            float excess = MathF.Abs(vel.X) - maxWalkSpeed;
            if (excess > 0f && MathF.Sign(vel.X) == MathF.Sign(inputX) && dt > 0f
                && !preserveExternalVelocity)
                force.X -= MathF.Sign(vel.X) * excess / dt;
            if (MathF.Sign(vel.X) == MathF.Sign(inputX) && MathF.Abs(force.X) < 2f)
                force.X = inputX * 2f;
        }

        return force;
    }
}
