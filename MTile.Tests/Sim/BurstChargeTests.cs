using System;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// BurstAction out of the LMB wind-up: it must fire at any point of a held charge (it used
// to key on the PressEdge intent, whose 20-frame lookback made long charges unburstable),
// and the wind-up's charge fraction scales its knockback and reach.
public class BurstChargeTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    // Grounded attacker, neutral victim standing `gap` px to the right. LMB press on
    // frame 1, held; RMB press `chargeFrames` frames later.
    private (bool burst, float victimVx) Run(int chargeFrames, float gap = 40f)
    {
        var terrain = SimTerrain.FromAscii(
            string.Join("\n", Enumerable.Repeat(new string('O', 40), 12).Append(new string('X', 40))),
            originTileX: 0, originTileY: 0);
        float standY = 12 * Chunk.TileSize - PlayerCharacter.Radius - MovementConfig.Current.FoldHoverOffset;
        var attacker = new Vector2(120f, standY);
        var victim   = new Vector2(120f + gap, standY);
        var mouse    = attacker + new Vector2(200f, 0f);
        var script = new InputScript()
            .For(1,            new PlayerInput { MouseWorldPosition = mouse })
            .For(chargeFrames, new PlayerInput { LeftClick = true, MouseWorldPosition = mouse })
            .Forever(          new PlayerInput { LeftClick = true, RightClick = true, MouseWorldPosition = mouse });

        var cfg = new SimConfigMulti
        {
            Terrain = terrain, Frames = chargeFrames + 25, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = attacker, Script = script },
                new SimPlayer { StartPosition = victim, Faction = Faction.Neutral, Script = InputScript.Always(default) },
            },
        };
        bool burst = false;
        float vx = 0f;
        SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            if (ps[0].CurrentActionName == "BurstAction") burst = true;
            vx = MathF.Max(vx, ps[1].Body.Velocity.X);
        });
        output.WriteLine($"charge {chargeFrames}f: burst = {burst}, victim max Vx = {vx}");
        return (burst, vx);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(40)]
    [InlineData(66)]    // the sweet spot (ramp 1.0 s + 0.1 s window)
    [InlineData(150)]   // overheld, past the PressEdge intent's 2 s buffer life
    public void Burst_FiresAnywhereInTheCharge(int chargeFrames)
        => Assert.True(Run(chargeFrames).burst, $"RMB after a {chargeFrames}-frame wind-up should burst");

    [Fact]
    public void ChargedBurst_ThrowsHarderThanATap()
    {
        float tap  = Run(3).victimVx;
        float full = Run(62).victimVx;   // in the sweet spot, full charge
        Assert.True(tap > 50f, $"a tap burst should still connect; Vx = {tap}");
        Assert.True(full > tap * 1.4f, $"a full-charge burst should throw well past a tap: tap {tap}, full {full}");
    }

    // Point-blank: the fan starts past a touching body, so only the near box (the first
    // stage, right in front of the caster) can catch it.
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.3f)]
    [InlineData(0.6f)]
    [InlineData(1.0f)]
    public void Burst_HitsABodyRightInFront(float radii)
        => Assert.True(Run(3, PlayerCharacter.Radius * radii).victimVx > 50f,
                       $"a burst should shove a body {radii}R away");

    // Reach scales too: a victim just past the uncharged rim is caught only when charged.
    [Fact]
    public void ChargedBurst_ReachesFurther()
    {
        float gap = PlayerCharacter.Radius * 5f;
        Assert.True(Run(3, gap).victimVx < 1f, "a tap burst shouldn't reach this far");
        Assert.True(Run(62, gap).victimVx > 50f, "a full-charge burst should");
    }
}
