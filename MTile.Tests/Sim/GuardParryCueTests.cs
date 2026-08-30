using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// The render-side guard-block cue (sound + sparks) is derived, not evented: it reads
// the stamps CombatState.TryParry writes (LastParryFrame / LastParryDir /
// LastParryCharged) exactly the way GameAudio.HitConnect and HitFeelSystem read
// LastHitFrame. These tests pin that contract — if the stamps stop being written, or
// stop surviving a snapshot restore, the cue silently stops firing (or double-fires
// after a rollback) with nothing else in the suite noticing.
public class GuardParryCueTests
{
    private const float Dt = 1f / 60f;
    private const int   Frame = 42;

    // Weak enough to arm GuardRetaliate (GuardChargeMaxDamage is 1.0).
    private const float WeakDamage   = 0.5f;
    private const float StrongDamage = 3.0f;

    private static CombatState Guarding()
    {
        var c = new CombatState();
        c.GuardActive = true;
        return c;
    }

    // Attacker on the right, player facing right: the impulse pushes the player LEFT,
    // so the direction back toward the attacker is +X.
    private static Vector2 ImpulseFromTheRight => new Vector2(-200f, 0f);

    [Fact]
    public void ParryStampsFrameAndIncomingDirection()
    {
        var c = Guarding();

        Assert.True(c.TryParry(ImpulseFromTheRight, WeakDamage, facing: 1, Frame, Dt));

        Assert.Equal(Frame, c.LastParryFrame);
        Assert.True(c.LastParryDir.X > 0.99f);          // unit vector toward the attacker
        Assert.Equal(1f, c.LastParryDir.Length(), 3);
    }

    [Fact]
    public void ChargedFlagTracksWhetherRetaliateWasArmed()
    {
        var weak = Guarding();
        weak.TryParry(ImpulseFromTheRight, WeakDamage, facing: 1, Frame, Dt);
        Assert.True(weak.GuardCharged);
        Assert.True(weak.LastParryCharged);

        var strong = Guarding();
        strong.TryParry(ImpulseFromTheRight, StrongDamage, facing: 1, Frame, Dt);
        Assert.False(strong.GuardCharged);
        Assert.False(strong.LastParryCharged);   // still a parry, just no counter cue
        Assert.Equal(Frame, strong.LastParryFrame);
    }

    // Out-of-cone hits are NOT parried, so they must leave no stamp behind — otherwise
    // a hit that actually landed would also play the block clang.
    [Fact]
    public void HitFromBehindLeavesNoStamp()
    {
        var c = Guarding();

        Assert.False(c.TryParry(ImpulseFromTheRight, WeakDamage, facing: -1, Frame, Dt));

        Assert.Equal(0, c.LastParryFrame);
    }

    [Fact]
    public void UnguardedHitLeavesNoStamp()
    {
        var c = new CombatState();   // GuardActive false

        Assert.False(c.TryParry(ImpulseFromTheRight, WeakDamage, facing: 1, Frame, Dt));

        Assert.Equal(0, c.LastParryFrame);
    }

    // The cue dedupes on the frame stamp, so a rollback restore has to bring the stamp
    // back with it — a dropped field would re-fire the block sound on every re-sim.
    [Fact]
    public void StampsSurviveSnapshotRestore()
    {
        var c = Guarding();
        c.TryParry(ImpulseFromTheRight, WeakDamage, facing: 1, Frame, Dt);

        var saved = c.Clone();
        c.LastParryFrame = 0; c.LastParryDir = Vector2.Zero; c.LastParryCharged = false;
        c.CopyFrom(saved);

        Assert.Equal(Frame, c.LastParryFrame);
        Assert.True(c.LastParryDir.X > 0.99f);
        Assert.True(c.LastParryCharged);
    }
}
