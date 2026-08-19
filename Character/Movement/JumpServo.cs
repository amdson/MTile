using System;

namespace MTile;

// Saturated velocity servo for jump launches: drives vy toward a target velocity
// relative to the jump surface and holds it there while the jump is powered
// (Space held, within MaxHoldTime). Two-sided by design — it pushes a slow body
// toward the target and *brakes* a fast one down to it, so a jump can never exit
// faster than its target no matter what velocity the body entered with (e.g. the
// surplus vy of a ledge pull). Same saturated-force idiom as LedgePullState's
// crest brake; no velocity writes.
//
// The gravityCancel term keeps the servo authoritative over vertical dynamics
// while powered: without it the steady state sits gravity·dt short of the target.
public static class JumpServo
{
    public static float Force(float vy, float sourceVy, float targetVy, float maxAccel, float gravityCancel, float dt)
    {
        if (dt <= 0f) return 0f;
        float needed = (sourceVy + targetVy - vy) / dt;
        return Math.Clamp(needed, 0f, maxAccel) - gravityCancel;
    }
}
