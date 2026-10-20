using System;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// AirWhipKick — the airborne backward swipe (it replaced AirSpinStab). Guards:
//
//   1. Routing. A backward air swipe fires the kick (not the stab) and flips Facing;
//      a forward air swipe still stabs.
//   2. Limited aim. A steep backward swipe is clamped into the ±25° cone around the
//      new facing's horizontal.
public class AirWhipKickTests
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 AirStart = new(200f, 40f);

    private static ChunkMap Shaft()
    {
        string open = new string('O', 28), solid = new string('X', 28);
        int rows = (int)MathF.Ceiling(600f / Chunk.TileSize);
        return SimTerrain.FromAscii(string.Join("\n", Enumerable.Repeat(open, rows).Append(solid)),
                                    originTileX: 0, originTileY: 0);
    }

    // Long-hold swipe (the stab gesture), as AirHammerTests.Swipe. The player spawns
    // facing +X, so a swipe toward -X is the backward one.
    private static InputScript Swipe(Vector2 press, Vector2 release) => new InputScript()
        .For(2,  new PlayerInput { MouseWorldPosition = press })
        .For(1,  new PlayerInput { LeftClick = true, MouseWorldPosition = press })
        .For(16, new PlayerInput { LeftClick = true, MouseWorldPosition = release })
        .Forever(new PlayerInput { MouseWorldPosition = release });

    private record Outcome(bool Kick, bool Stab, int FacingBefore, int FacingInKick, Vector2 KickDir);

    private static Outcome Run(Vector2 press, Vector2 release)
    {
        bool kick = false, stab = false;
        int before = 0, during = 0;
        Vector2 dir = Vector2.Zero;
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = Shaft(), Frames = 40, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[] { new SimPlayer { StartPosition = AirStart, Script = Swipe(press, release) } },
        }, onFrame: (f, ps) =>
        {
            var p = ps[0];
            if (p.CurrentActionName == nameof(AirWhipKick))
            {
                if (!kick) { during = p.Facing; dir = p.CurrentActionVars.StabDir; }
                kick = true;
            }
            else if (!kick) before = p.Facing;
            if (p.CurrentActionName == nameof(StabAction)) stab = true;
        });
        return new Outcome(kick, stab, before, during, dir);
    }

    [Fact]
    public void BackwardAirSwipe_FiresKick_AndFlipsFacing()
    {
        var o = Run(AirStart + new Vector2(-40f, 0f), AirStart + new Vector2(-300f, 40f));
        Assert.True(o.Kick, "A backward air swipe should fire AirWhipKick.");
        Assert.False(o.Stab, "The kick replaces the backward air stab.");
        Assert.Equal(-o.FacingBefore, o.FacingInKick);
        Assert.True(o.KickDir.X * o.FacingInKick > 0f, "The kick points along the new facing.");
    }

    [Fact]
    public void ForwardAirSwipe_StillStabs()
    {
        var o = Run(AirStart + new Vector2(40f, 0f), AirStart + new Vector2(300f, 40f));
        Assert.False(o.Kick);
        Assert.True(o.Stab);
    }

    // ─── sweet spot ───────────────────────────────────────────────────────────────────
    // A level backward swipe; the kick fires toward -X (the player spawns facing +X).
    private static InputScript LevelKick() =>
        Swipe(AirStart + new Vector2(-40f, 0f), AirStart + new Vector2(-300f, 0f));

    private const int Frames = 40;
    private const int StrikeLag = 7;   // frames from entering the kick to mid-active

    // The stab gesture's hold changes the attacker's fall, so a free-falling victim drifts
    // relative to it. Measure that drift on a dummy out of reach, then pre-compensate.
    private static Vector2 DriftAtStrike()
    {
        var dummyStart = AirStart + new Vector2(300f, 0f);
        int entered = -1;
        Vector2 drift = Vector2.Zero;
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = Shaft(), Frames = Frames, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = AirStart, Script = LevelKick() },
                new SimPlayer { StartPosition = dummyStart, Faction = Faction.Neutral,
                                Script = InputScript.Always(default) },
            },
        }, onFrame: (f, ps) =>
        {
            if (entered < 0 && ps[0].CurrentActionName == nameof(AirWhipKick)) entered = f;
            if (entered >= 0 && f == entered + StrikeLag)
                drift = (ps[0].Body.Position - AirStart) - (ps[1].Body.Position - dummyStart);
        });
        Assert.True(entered >= 0, "the level backward swipe never entered the kick");
        return drift;
    }

    private static (float hpLost, float launch) StrikeVictimAt(Vector2 offset, Vector2 drift)
    {
        float hp0 = float.NaN, hpMin = float.MaxValue, launch = 0f;
        Vector2 prevV = Vector2.Zero;
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = Shaft(), Frames = Frames, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = AirStart, Script = LevelKick() },
                new SimPlayer { StartPosition = AirStart + offset + drift, Faction = Faction.Neutral,
                                Script = InputScript.Always(default) },
            },
        }, onFrame: (f, ps) =>
        {
            if (float.IsNaN(hp0)) hp0 = ps[1].Health;
            hpMin  = MathF.Min(hpMin, ps[1].Health);
            launch = MathF.Max(launch, (ps[1].Body.Velocity - prevV).Length());
            prevV  = ps[1].Body.Velocity;
        });
        return (hp0 - hpMin, launch);
    }

    [Fact]
    public void TipSweetSpot_DoublesDamage_WithoutStacking_AndLaunchesHarder()
    {
        // The line runs 0..5.2R from the body, the sweet spot its last 1.3R (3.9R..5.2R).
        // A victim's hurtbox is ≈0.49R in half-width: centered at 4.6R it sits in the tip,
        // at 2.2R it is clear of it (1.7R..2.7R) but on the line.
        const float R = PlayerCharacter.Radius;
        var drift = DriftAtStrike();
        var tip   = StrikeVictimAt(new Vector2(-4.6f * R, 0f), drift);
        var line  = StrikeVictimAt(new Vector2(-2.2f * R, 0f), drift);

        Assert.Equal(0.5f, line.hpLost, 3);
        Assert.Equal(1.0f, tip.hpLost, 3);
        Assert.True(tip.launch > 2f * line.launch,
            $"sweet-spot launch {tip.launch} should far exceed the line's {line.launch}");
    }

    [Fact]
    public void SteepBackwardSwipe_IsClampedToTheAimCone()
    {
        // ~60° above horizontal, backward.
        var o = Run(AirStart + new Vector2(-20f, 0f), AirStart + new Vector2(-150f, -260f));
        Assert.True(o.Kick);
        float angle = MathF.Atan2(-o.KickDir.Y, MathF.Abs(o.KickDir.X));
        Assert.InRange(angle, 0.3f, 0.45f);
    }
}
