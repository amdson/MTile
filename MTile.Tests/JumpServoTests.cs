using Xunit;

namespace MTile.Tests;

// JumpServo's two-sided contract (y-down: up is negative). Every jump test only
// asserted a ceiling ("never faster than the target"), so for a long time the
// servo could only BRAKE — its clamp was [0, maxAccel], which zeroes every upward
// push — and LedgeJump from a dead hang barely moved. Pin both halves.
public class JumpServoTests
{
    private const float Dt = 1f / 60f, Target = -210f, Accel = 3000f, Cancel = 600f;

    [Fact]
    public void FromRest_PushesUpAtFullAuthority()
    {
        float f = JumpServo.Force(vy: 0f, sourceVy: 0f, Target, Accel, Cancel, Dt);
        Assert.Equal(-Accel - Cancel, f, 3);   // saturated push up, plus gravity cancel
    }

    [Fact]
    public void BelowTarget_PushesUp_ButNeverPastIt()
    {
        float vy = -200f;   // 10 px/s short of the target
        float f = JumpServo.Force(vy, 0f, Target, Accel, Cancel, Dt);
        float next = vy + (f + Cancel) * Dt;   // gravity is what Cancel cancels
        Assert.True(f < -Cancel, "no push toward an upward target");
        Assert.Equal(Target, next, 3);
    }

    [Fact]
    public void AboveTarget_Brakes()
    {
        float f = JumpServo.Force(vy: -400f, 0f, Target, Accel, Cancel, Dt);
        Assert.True(f > -Cancel, "a body faster than the target was not braked");
    }

    [Fact]
    public void AtTarget_HoldsIt()
        => Assert.Equal(-Cancel, JumpServo.Force(Target, 0f, Target, Accel, Cancel, Dt), 3);

    [Fact]
    public void MovingSource_TargetIsRelative()
    {
        // A source rising at 50 px/s: the target is 50 faster in the world frame.
        float f = JumpServo.Force(vy: -210f, sourceVy: -50f, Target, Accel, Cancel, Dt);
        Assert.True(f < -Cancel, "relative target ignored the source's own rise");
    }
}
