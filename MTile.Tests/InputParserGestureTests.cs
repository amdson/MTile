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
                              ref int frame, Vector2 playerPos, Vector2 cursorOffsetFromPlayer, bool lmb)
    {
        ctl.InjectInput(new PlayerInput
        {
            LeftClick          = lmb,
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
}
