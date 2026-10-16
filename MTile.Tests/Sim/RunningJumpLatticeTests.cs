using System;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// RunningJumpState on the lattice engine is a HYBRID jump (PATH_TRACKER_UNIFICATION_PLAN
// phase 1): its velocity write + hold force is the launch, and the tracker follows the
// rising plan with the legs OFF (FoldProfile.ImpulseJump). With the legs permitted, the
// leg servo launched it a second time on top of the impulse — a held running jump rose
// 85.7 px against the 49.4 px its own tuning derives (RunJumpApexRise).
public class RunningJumpLatticeTests(ITestOutputHelper output)
{
    private static ChunkMap Flat() => SimTerrain.FromAscii(@"
            ........................................
            ........................................
            ........................................
            ........................................
            ........................................
            ........................................
            ........................................
            XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    [Theory]
    [InlineData(1f / 60f)]
    [InlineData(1f / 30f)]
    public void HeldRunningJump_RisesItsOwnHeight_NoSecondLaunch(float dt)
    {
        Assert.Equal("lattice", MovementConfig.Current.FoldEngine);
        int runIn = (int)(0.6f / dt);
        var trace = SimRunner.Run(new SimConfig
        {
            Terrain       = Flat(),
            StartPosition = new Vector2(30f, 7 * Chunk.TileSize - 22f),
            Script        = new InputScript()
                .For(runIn, new PlayerInput { Right = true })
                .For((int)(0.3f / dt), new PlayerInput { Right = true, Space = true })
                .Forever(new PlayerInput { Right = true }),
            Frames        = runIn + (int)(1.2f / dt),
            Dt            = dt,
            Gravity       = new Vector2(0f, 600f),
        });

        Assert.Contains(trace, f => f.State == "RunningJumpState");
        float ground = trace[runIn - 1].Y;
        float rise = ground - trace.Skip(runIn).Min(f => f.Y);
        float fastestUp = -trace.Skip(runIn).Min(f => f.Vy);
        output.WriteLine($"dt={dt:F4} rise={rise:F1}px (RunJumpApexRise {MovementConfig.Current.RunJumpApexRise:F1}) fastestUp={fastestUp:F0}");

        // Its own launch: the derived apex within a few px of discretization...
        Assert.InRange(rise, MovementConfig.Current.RunJumpApexRise - 4f, MovementConfig.Current.RunJumpApexRise + 6f);
        // ...and never faster than a deliberate jump can author (no leg double-launch).
        Assert.True(fastestUp <= MovementConfig.Current.MaxAssistRiseSpeed + 5f,
            $"rose at {fastestUp:F0} px/s, past the jump ceiling {MovementConfig.Current.MaxAssistRiseSpeed:F0}");
    }
}
