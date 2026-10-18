using Xunit;

namespace MTile.Tests;

// ConditionState.StampRecovery keeps the later expiry: a short-tailed move (a parry
// counter) can't cut a pending long tail short, but a longer stamp still extends it.
public class CommitmentRecoveryStampTests
{
    [Fact]
    public void ShorterStamp_KeepsThePendingTail()
    {
        var c = new ConditionState();
        c.StampRecovery(36, currentFrame: 100);
        c.StampRecovery(8, currentFrame: 110);
        Assert.True(c.RecoveryActive);
        Assert.Equal(136, c.RecoveryExpireFrame);
    }

    [Fact]
    public void LongerStamp_Extends()
    {
        var c = new ConditionState();
        c.StampRecovery(8, currentFrame: 100);
        c.StampRecovery(36, currentFrame: 104);
        Assert.Equal(140, c.RecoveryExpireFrame);
    }

    [Fact]
    public void ClearedRecovery_StampsFresh()
    {
        // A combo step clears RecoveryActive on Enter; its Exit then stamps from scratch.
        var c = new ConditionState();
        c.StampRecovery(36, currentFrame: 100);
        c.RecoveryActive = false;
        c.StampRecovery(10, currentFrame: 110);
        Assert.Equal(120, c.RecoveryExpireFrame);
    }
}
