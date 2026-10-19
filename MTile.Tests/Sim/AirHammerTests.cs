using System;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// AirHammerAction — the airborne down-swipe (stab gesture) that replaced the downward
// air stab. Guards:
//
//   1. Routing. An air swipe within 30° of straight down picks the hammer; a sideways
//      air swipe and a grounded down swipe still pick the stab.
//   2. The sweet spot. A victim at the core loses 2× the outer box's HP — NOT core +
//      outer stacked (both boxes share a HitId; the core publishes first) — and is
//      launched harder; a victim only inside the outer ring takes the plain hit.
public class AirHammerTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    // Tall open shaft over a floor, so a hammer started near the top resolves in the air.
    private static ChunkMap Shaft()
    {
        string open = new string('O', 28), solid = new string('X', 28);
        int rows = (int)MathF.Ceiling(600f / Chunk.TileSize);
        return SimTerrain.FromAscii(string.Join("\n", Enumerable.Repeat(open, rows).Append(solid)),
                                    originTileX: 0, originTileY: 0);
    }

    private static ChunkMap FlatGround()
    {
        string open = new string('O', 28), solid = new string('X', 28);
        int rows = (int)MathF.Ceiling(64f / Chunk.TileSize);
        return SimTerrain.FromAscii(string.Join("\n", Enumerable.Repeat(open, rows).Append(solid)),
                                    originTileX: 0, originTileY: 0);
    }

    private static readonly Vector2 AirStart = new(200f, 40f);

    // Long hold (> ClickMaxHoldSeconds) with a swipe from `press` to `release` — the
    // stab gesture. Swipes are player-relative, so the offsets are generous enough
    // that the attacker's own fall during the hold can't flip their sign.
    private static InputScript Swipe(Vector2 press, Vector2 release) => new InputScript()
        .For(2,  new PlayerInput { MouseWorldPosition = press })
        .For(1,  new PlayerInput { LeftClick = true, MouseWorldPosition = press })
        .For(16, new PlayerInput { LeftClick = true, MouseWorldPosition = release })
        .Forever(new PlayerInput { MouseWorldPosition = release });

    private static (bool hammer, bool stab) Route(ChunkMap terrain, Vector2 start, Vector2 press, Vector2 release)
    {
        bool hammer = false, stab = false;
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = terrain, Frames = 50, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[] { new SimPlayer { StartPosition = start, Script = Swipe(press, release) } },
        }, onFrame: (f, ps) =>
        {
            var a = ps[0].CurrentActionName;
            if (a == nameof(AirHammerAction)) hammer = true;
            if (a == nameof(StabAction) || a == nameof(AirSpinStab)) stab = true;
        });
        return (hammer, stab);
    }

    [Fact]
    public void AirDownSwipe_SelectsHammer_NotStab()
    {
        var (hammer, stab) = Route(Shaft(), AirStart, AirStart + new Vector2(0f, 40f), AirStart + new Vector2(0f, 400f));
        Assert.True(hammer, "An air swipe straight down should fire AirHammerAction.");
        Assert.False(stab, "The hammer replaces the downward air stab — no stab should fire.");
    }

    [Fact]
    public void AirSidewaysSwipe_StillStabs()
    {
        var (hammer, stab) = Route(Shaft(), AirStart, AirStart + new Vector2(40f, 0f), AirStart + new Vector2(300f, 40f));
        Assert.False(hammer, "A level air swipe is outside the bottom wedge.");
        Assert.True(stab, "A level air swipe should still stab.");
    }

    [Fact]
    public void GroundedDownSwipe_StillStabs()
    {
        float floorTop = (int)MathF.Ceiling(64f / Chunk.TileSize) * Chunk.TileSize;
        var start = new Vector2(200f, floorTop - PlayerCharacter.Radius - MovementConfig.Current.FoldHoverOffset);
        var (hammer, _) = Route(FlatGround(), start, start + new Vector2(0f, -40f), start + new Vector2(5f, 400f));
        Assert.False(hammer, "The hammer is air-only.");
    }

    private static InputScript DownSwipe() =>
        Swipe(AirStart + new Vector2(0f, 40f), AirStart + new Vector2(0f, 400f));

    // Frames 0..54 of the strike run. The hammer's boxes are live for 4 frames starting
    // 15 frames after it enters (HurtboxStartTime 0.25 s at 60 Hz); aim at the second.
    private const int Frames = 55;

    // Holding the stab gesture slows the attacker's fall (RecoveryAction's charge
    // modifiers), so a victim that starts level with them and falls freely drifts away
    // before the strike. Pass 1 measures that drift against a free-falling dummy parked
    // well out of reach; the victim then starts pre-compensated so that, on the strike
    // frame, it sits exactly at `offset` from the attacker.
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
                new SimPlayer { StartPosition = AirStart, Script = DownSwipe() },
                new SimPlayer { StartPosition = dummyStart, Faction = Faction.Neutral,
                                Script = InputScript.Always(default) },
            },
        }, onFrame: (f, ps) =>
        {
            if (entered < 0 && ps[0].CurrentActionName == nameof(AirHammerAction)) entered = f;
            if (entered >= 0 && f == entered + 16)
                drift = (ps[0].Body.Position - AirStart) - (ps[1].Body.Position - dummyStart);
        });
        Assert.True(entered >= 0, "the down swipe never entered the hammer");
        return drift;
    }

    // Returns (HP lost, largest one-frame velocity change — the hit's launch).
    private (float hpLost, float launch) StrikeVictimAt(Vector2 offset, Vector2 drift)
    {
        float hp0 = float.NaN, hpMin = float.MaxValue, launch = 0f;
        Vector2 prevV = Vector2.Zero;
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = Shaft(), Frames = Frames, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = AirStart, Script = DownSwipe() },
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
        output.WriteLine($"offset={offset}  hpLost={hp0 - hpMin}  launch={launch}");
        return (hp0 - hpMin, launch);
    }

    [Fact]
    public void SweetSpot_DoublesDamage_WithoutStacking_AndLaunchesHarder()
    {
        // Box centers sit (Facing·0.35R, 1.6R) from the attacker; Facing defaults right.
        // The victim's hurtbox is ~R tall but only BodyWidthScale·R (≈0.49R) in half-width,
        // so 2.3R down overlaps both boxes vertically, and a +1.15R sideways shift clears
        // the core (0.4R + 0.49R) while staying inside the outer ring (1R + 0.49R).
        const float R = PlayerCharacter.Radius;
        var drift = DriftAtStrike();
        var core  = StrikeVictimAt(new Vector2(0.35f * R,          2.3f * R), drift);
        var outer = StrikeVictimAt(new Vector2(0.35f * R + 1.15f * R, 2.3f * R), drift);

        Assert.Equal(1.0f, outer.hpLost, 3);
        Assert.Equal(2.0f, core.hpLost, 3);
        Assert.True(core.launch > 2f * outer.launch,
            $"sweet-spot launch {core.launch} should far exceed the outer box's {outer.launch}");
    }
}
