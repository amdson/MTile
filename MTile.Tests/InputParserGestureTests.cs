using System;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// InputParser measures gestures in player-relative space: the camera follows the
// player, so a cursor sitting still on screen drifts through world space whenever
// the player moves. These pin that a moving player with a still cursor is NOT a
// stab, while an actual swipe (in player-relative terms) IS one — and that the
// stab direction is the hand's motion, not the player's.
public class InputParserGestureTests
{
    private const float Dt = Simulation.FixedDt;

    // Drives one frame: cursor is at a fixed SCREEN offset from the player, so its
    // world position is playerPos + screenOffset — exactly what a follow-camera yields.
    private static void Frame(InputParser parser, Controller ctl, IntentBuffer buf,
                              ref int frame, Vector2 playerPos, Vector2 cursorOffsetFromPlayer, bool lmb,
                              bool shift = false)
    {
        ctl.InjectInput(new PlayerInput
        {
            LeftClick          = lmb,
            Shift              = shift,
            MouseWorldPosition = playerPos + cursorOffsetFromPlayer,
        });
        parser.Detect(ctl, buf, frame, Dt, playerPos);
        frame++;
    }

    private static int LongHoldFrames => SimFrames.FromSeconds(InputParser.ClickMaxHoldSeconds, Dt) + 6;

    [Fact]
    public void StillCursorWhilePlayerRuns_IsNotAStab()
    {
        var parser = new InputParser();
        var ctl    = new Controller();
        var buf    = new IntentBuffer();
        int frame  = 0;
        var offset = new Vector2(40f, -10f);   // cursor parked ahead of the player on screen

        var pos = new Vector2(100f, 100f);
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false);
        // Press, then hold while the player runs 8 px/frame to the right — far more
        // than StabSwipeThreshold over the hold — with the cursor never moving on screen.
        for (int i = 0; i < LongHoldFrames; i++)
        {
            Frame(parser, ctl, buf, ref frame, pos, offset, lmb: true);
            pos.X += 8f;
        }
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false);   // release

        Assert.False(buf.Peek(IntentType.Stab, frame, out _), "player motion alone must not register as a swipe");
        Assert.False(buf.Peek(IntentType.Click, frame, out _), "long hold is not a click");
    }

    [Fact]
    public void SwipeWhilePlayerRuns_StabDirectionFollowsTheHandNotThePlayer()
    {
        var parser = new InputParser();
        var ctl    = new Controller();
        var buf    = new IntentBuffer();
        int frame  = 0;

        var pos    = new Vector2(100f, 100f);
        var offset = new Vector2(0f, 0f);
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false);
        // Player runs RIGHT while the hand swipes UP 5 px/frame on screen.
        for (int i = 0; i < LongHoldFrames; i++)
        {
            Frame(parser, ctl, buf, ref frame, pos, offset, lmb: true);
            pos.X    += 8f;
            offset.Y -= 5f;
        }
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false);

        Assert.True(buf.Peek(IntentType.Stab, frame, out var stab), "a real swipe should register as a stab");
        Assert.Equal(0f, stab.Direction.X, 3);
        Assert.Equal(-1f, stab.Direction.Y, 3);
    }

    [Fact]
    public void StationaryPlayerSwipe_StillStabs()
    {
        var parser = new InputParser();
        var ctl    = new Controller();
        var buf    = new IntentBuffer();
        int frame  = 0;

        var pos    = new Vector2(100f, 100f);
        var offset = new Vector2(0f, 0f);
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false);
        for (int i = 0; i < LongHoldFrames; i++)
        {
            Frame(parser, ctl, buf, ref frame, pos, offset, lmb: true);
            offset.X += 5f;
        }
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false);

        Assert.True(buf.Peek(IntentType.Stab, frame, out var stab));
        Assert.Equal(1f, stab.Direction.X, 3);
    }

    // ── Shift-owned presses don't emit the bare-LMB drag gestures ────────────────
    //
    // The reported bug: a Shift+LMB block throw almost always fired a stab right after
    // it. The throw is a long LMB hold with a drag, which is exactly the Stab shape, so
    // the parser emitted a Stab intent on the LMB release. StabAction's
    // `if (ctx.Input.Shift) return false` looked like it covered this, but it reads
    // Shift when the intent is CONSUMED, and IntentBuffer keeps intents alive for
    // MaxAgeFrames (120 ≈ 2 s) — so the stab simply waited for the player to let go of
    // Shift and then fired.
    //
    // Runs the actual throw shape: Shift down for the whole press, a drag well past
    // StabSwipeThreshold, LMB released first, Shift released after.
    [Fact]
    public void ShiftHeldDrag_EmitsNoStab()
    {
        var parser = new InputParser();
        var ctl    = new Controller();
        var buf    = new IntentBuffer();
        int frame  = 0;

        var pos    = new Vector2(100f, 100f);
        var offset = new Vector2(0f, 0f);
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false, shift: true);
        for (int i = 0; i < LongHoldFrames; i++)
        {
            Frame(parser, ctl, buf, ref frame, pos, offset, lmb: true, shift: true);
            offset.X += 5f;              // 5 px/frame — far past StabSwipeThreshold (12)
        }
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false, shift: true);   // LMB up
        Assert.False(buf.Peek(IntentType.Stab, frame, out _),
            "a Shift+LMB drag is a block throw / beam, not a stab");

        // The half the consumption-time guard got wrong: let go of Shift afterwards and
        // the suppressed intent must still not exist. Several frames, because the buffer
        // would have held it for ~2 seconds.
        for (int i = 0; i < 20; i++)
            Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false, shift: false);

        Assert.False(buf.Peek(IntentType.Stab, frame, out _),
            "releasing Shift after the throw must not resurrect a stab");
    }

    // Ownership is decided at the PRESS edge, so dropping Shift mid-drag doesn't hand
    // the gesture back to the stab family. This is the ordering a real block throw
    // usually produces — the hand relaxes Shift while the button is still down.
    [Fact]
    public void ShiftReleasedMidDrag_StillEmitsNoStab()
    {
        var parser = new InputParser();
        var ctl    = new Controller();
        var buf    = new IntentBuffer();
        int frame  = 0;

        var pos    = new Vector2(100f, 100f);
        var offset = new Vector2(0f, 0f);
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false, shift: true);
        for (int i = 0; i < LongHoldFrames; i++)
        {
            // Shift goes up halfway through, while LMB stays down.
            Frame(parser, ctl, buf, ref frame, pos, offset, lmb: true, shift: i < LongHoldFrames / 2);
            offset.X += 5f;
        }
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false, shift: false);

        Assert.False(buf.Peek(IntentType.Stab, frame, out _),
            "the press opened under Shift, so it stays a Shift gesture to the end");
    }

    // Same shape, but the drag closes a loop. PulseAction has no Shift guard of its own,
    // so before the emission-side fix a Shift+LMB drag that happened to circle fired a
    // pulse — the same bug wearing a different intent.
    [Fact]
    public void ShiftHeldCircle_EmitsNoCircle()
    {
        var parser = new InputParser();
        var ctl    = new Controller();
        var buf    = new IntentBuffer();
        int frame  = 0;

        var pos = new Vector2(100f, 100f);
        // The press edge must land at the CENTRE of the loop. The circle accumulator
        // measures angular sweep about the press point, so a press taken on the rim
        // makes the drag pass through its own centre, where the MinCircleRadius filter
        // discards the samples and the angle never accumulates — a lap authored that
        // way registers nothing at all and would make this test vacuous.
        Frame(parser, ctl, buf, ref frame, pos, Vector2.Zero, lmb: false, shift: true);
        Frame(parser, ctl, buf, ref frame, pos, Vector2.Zero, lmb: true,  shift: true);
        // A full lap at radius 30 (well past MinCircleRadius), 24 samples ⇒ 360°.
        for (int i = 0; i < 24; i++)
        {
            float a = i / 24f * MathF.PI * 2f;
            Frame(parser, ctl, buf, ref frame, pos,
                  new Vector2(MathF.Cos(a) * 30f, MathF.Sin(a) * 30f), lmb: true, shift: true);
        }
        Frame(parser, ctl, buf, ref frame, pos, new Vector2(30f, 0f), lmb: false, shift: true);

        Assert.False(buf.Peek(IntentType.Circle, frame, out _),
            "a Shift+LMB loop belongs to the Shift family, not to Pulse");
    }

    // The control that keeps this from being a regression: with no Shift, the identical
    // drag still stabs. Without this, "no stab ever" would pass the tests above.
    [Fact]
    public void BareDrag_StillStabs()
    {
        var parser = new InputParser();
        var ctl    = new Controller();
        var buf    = new IntentBuffer();
        int frame  = 0;

        var pos    = new Vector2(100f, 100f);
        var offset = new Vector2(0f, 0f);
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false);
        for (int i = 0; i < LongHoldFrames; i++)
        {
            Frame(parser, ctl, buf, ref frame, pos, offset, lmb: true);
            offset.X += 5f;
        }
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false);

        Assert.True(buf.Peek(IntentType.Stab, frame, out _),
            "an unmodified LMB drag is still a stab");
    }

    // Click is SHARED, not suppressed: EnergyBallAction is a Shift+LMB tap and consumes
    // IntentType.Click. Only the two drag gestures are bare-LMB only.
    [Fact]
    public void ShiftTap_StillEmitsClick()
    {
        var parser = new InputParser();
        var ctl    = new Controller();
        var buf    = new IntentBuffer();
        int frame  = 0;

        var pos    = new Vector2(100f, 100f);
        var offset = new Vector2(0f, 0f);
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false, shift: true);
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: true,  shift: true);
        Frame(parser, ctl, buf, ref frame, pos, offset, lmb: false, shift: true);

        Assert.True(buf.Peek(IntentType.Click, frame, out _),
            "Shift+LMB tap must still reach EnergyBallAction");
    }
}
