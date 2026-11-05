using MTile.Net;
using Xunit;

namespace MTile.Tests.Sim;

// Pure unit tests for BetLedger (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, "Learning: bets").
public class BotManeuverBetLedgerTests
{
    private const string A = "Slash";
    private const int B = 3;

    // Opens one record at `frame`, ticks through its horizon with the opponent losing
    // `dealt` HP on the first frame after open. Returns the next free frame.
    private static int Episode(BetLedger l, Bet bet, int frame, float dealt, ref float theirHp)
    {
        l.Tick(frame, 5f, theirHp);
        l.Open(A, B, bet, frame);
        for (int f = frame + 1; f <= frame + bet.Horizon; f++)
        {
            if (f == frame + 1) theirHp -= dealt;
            l.Tick(f, 5f, theirHp);
        }
        return frame + bet.Horizon + 1;
    }

    [Fact]
    public void OverconfidentBet_CorrectsDownMonotonically()
    {
        var l = new BetLedger { Eta = 0.3f, Hierarchical = false };
        var bet = new Bet(0.5f, 0f, 10);
        float hp = 5f; int frame = 0;
        float prev = l.Corrected(A, B, bet).Dealt;
        Assert.Equal(0.5f, prev);
        for (int i = 0; i < 40; i++)
        {
            frame = Episode(l, bet, frame, 0f, ref hp);
            float now = l.Corrected(A, B, bet).Dealt;
            Assert.True(now < prev, $"settle {i}: {now} !< {prev}");
            Assert.True(now >= 0f, $"overshot below 0: {now}");
            prev = now;
        }
        Assert.True(prev < 0.05f, $"corrected dealt {prev} not near 0");
    }

    [Fact]
    public void UnderconfidentBet_CorrectsUpTowardRealised()
    {
        var l = new BetLedger { Eta = 0.3f, Hierarchical = false };
        var bet = new Bet(0f, 0f, 10);
        float hp = 1000f; int frame = 0;
        float prev = 0f;
        for (int i = 0; i < 40; i++)
        {
            frame = Episode(l, bet, frame, 0.5f, ref hp);
            float now = l.Corrected(A, B, bet).Dealt;
            Assert.True(now > prev, $"settle {i}: {now} !> {prev}");
            Assert.True(now <= 0.5f);
            prev = now;
        }
        // Fixed-share decay keeps it slightly under 0.5 at equilibrium.
        Assert.InRange(prev, 0.4f, 0.5f);
    }

    [Fact]
    public void HpRise_CountsZeroNotNegative()
    {
        var l = new BetLedger { Eta = 0.3f, Hierarchical = false, Share = 0f };
        var bet = new Bet(0f, 0f, 5);
        l.Tick(0, 1f, 1f);
        l.Open(A, B, bet, 0);
        l.Tick(1, 5f, 5f); // both respawn
        for (int f = 2; f <= 5; f++) l.Tick(f, 5f, 5f);
        Assert.Equal(1, l.Settled);
        var c = l.Corrected(A, B, bet);
        Assert.Equal(0f, c.Dealt);
        Assert.Equal(0f, c.Taken);
    }

    [Fact]
    public void Record_SettlesExactlyAtFramePlusHorizon()
    {
        var l = new BetLedger { Eta = 0.3f, Hierarchical = false };
        var bet = new Bet(0.5f, 0f, 7);
        l.Tick(10, 5f, 5f);
        l.Open(A, B, bet, 10);
        for (int f = 11; f < 17; f++)
        {
            l.Tick(f, 5f, 5f);
            Assert.Equal(0, l.Settled);
            Assert.Equal(1, l.OpenCount);
            Assert.Equal(0.5f, l.Corrected(A, B, bet).Dealt);
        }
        l.Tick(17, 5f, 5f);
        Assert.Equal(1, l.Settled);
        Assert.Equal(0, l.OpenCount);
        Assert.True(l.Corrected(A, B, bet).Dealt < 0.5f);
    }

    [Fact]
    public void SimultaneousRecords_BothSeeFullOutcome()
    {
        var l = new BetLedger { Share = 0f, Eta = 1f, Hierarchical = false };
        var bet = new Bet(0f, 0f, 4);
        l.Tick(0, 5f, 5f);
        l.Open("Stance", B, bet, 0);
        l.Open("Slash", B, bet, 0);
        l.Tick(1, 4.5f, 4f);
        for (int f = 2; f <= 4; f++) l.Tick(f, 4.5f, 4f);
        Assert.Equal(2, l.Settled);
        foreach (var name in new[] { "Stance", "Slash" })
        {
            var c = l.Corrected(name, B, bet);
            Assert.Equal(1f, c.Dealt, 4);
            Assert.Equal(0.5f, c.Taken, 4);
        }
    }

    [Fact]
    public void Offset_StableWithoutSettles_DecaysUnderShareAtCorrectedValue()
    {
        var l = new BetLedger { Eta = 0.3f, Hierarchical = false, Share = 0f };
        var bet = new Bet(0f, 0f, 3);
        float hp = 100f; int frame = 0;
        frame = Episode(l, bet, frame, 1f, ref hp);
        float off = l.Corrected(A, B, bet).Dealt;
        Assert.True(off > 0f);

        // No further settles: ticking with HP changes and no open records leaves it alone.
        for (int i = 0; i < 50; i++) { hp -= 0.1f; l.Tick(frame++, 5f, hp); }
        Assert.Equal(off, l.Corrected(A, B, bet).Dealt);

        // With Share > 0, settles whose realised equals the corrected bet still decay it.
        l.Share = 0.1f;
        float prev = off;
        for (int i = 0; i < 10; i++)
        {
            float realised = l.Corrected(A, B, bet).Dealt;
            l.Tick(frame, 5f, hp);
            l.Open(A, B, bet, frame);
            hp -= realised;
            for (int f = frame + 1; f <= frame + bet.Horizon; f++) l.Tick(f, 5f, hp);
            frame += bet.Horizon + 1;
            float now = l.Corrected(A, B, bet).Dealt;
            Assert.True(now < prev && now > 0f, $"settle {i}: {now} vs {prev}");
            Assert.Equal(prev * 0.9f, now, 3);
            prev = now;
        }
    }

    [Fact]
    public void Offset_ClampsAtMaxOffset()
    {
        var l = new BetLedger { Share = 0f, Eta = 1f, MaxOffset = 2f, Hierarchical = false };
        var bet = new Bet(0f, 0f, 2);
        float hp = 100f; int frame = 0;
        for (int i = 0; i < 5; i++) frame = Episode(l, bet, frame, 10f, ref hp);
        Assert.Equal(2f, l.Corrected(A, B, bet).Dealt, 5);

        var l2 = new BetLedger { Share = 0f, Eta = 1f, MaxOffset = 2f, Hierarchical = false };
        var big = new Bet(10f, 0f, 2);
        hp = 100f; frame = 0;
        for (int i = 0; i < 5; i++) frame = Episode(l2, big, frame, 0f, ref hp);
        Assert.Equal(8f, l2.Corrected(A, B, big).Dealt, 5);
    }

    [Fact]
    public void Corrected_UnknownKey_ReturnsInput()
    {
        var l = new BetLedger { Eta = 0.3f, Hierarchical = false };
        var bet = new Bet(0.3f, 0.1f, 20);
        var c = l.Corrected("Nobody", 7, bet);
        Assert.Equal(bet.Dealt, c.Dealt);
        Assert.Equal(bet.Taken, c.Taken);
        Assert.Equal(bet.Horizon, c.Horizon);
        Assert.Empty(l.Offsets);

        // A known name in a different bucket is still unknown.
        l.Tick(0, 5f, 5f);
        l.Open("Nobody", 1, new Bet(0f, 0f, 1), 0);
        l.Tick(1, 5f, 4f);
        Assert.Equal(bet.Dealt, l.Corrected("Nobody", 7, bet).Dealt);
        Assert.Contains("Nobody", l.Report());
    }
}
