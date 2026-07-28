using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// movement_todo #2: falling with a direction held toward a cave mouth in a
// wall, the corrector trims the arc slightly DOWN so the body ducks under
// the upper lip and enters seamlessly. Channel story (user-confirmed): far
// out, AirVertical's tiny down side integrates over the approach; near the
// mouth the tunnel floor comes within LegReach and the near mask flips
// while airborne — Redirect's plant-and-deflect case. The decision comes
// from hard clearance rows against the wall face above the mouth.
//
// The assist smooths NEAR-MISSES; it does not steer into caves. A fall
// aimed well above the mouth bonks the wall honestly — the tiny air
// authority must not (and cannot) save it.
public class CaveMouthTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    // The natural game shape: a ledge, a gap, and a cliff wall with a 2-high
    // tunnel at its base. Run off the ledge toward the wall → fall arc
    // arrives at the face around lip height.
    //   cols 0..ledgeCols  solid ledge (top at ledgeTopRow·16)
    //   cols 12+           wall rows 0..3 (face at x=192), tunnel rows 4..5
    //                      (opening y 64..96), floor continues at row 6.
    private static ChunkMap CaveTerrain(int ledgeCols = 8, int ledgeTopRow = 3)
    {
        var rows = new string[8];
        for (int r = 0; r < 8; r++)
        {
            var sb = new System.Text.StringBuilder(30);
            for (int c = 0; c < 30; c++)
            {
                bool wall = c >= 12;
                bool ledge = c <= ledgeCols && r >= ledgeTopRow;
                sb.Append(r switch
                {
                    >= 6 => 'X',                        // ground / tunnel floor
                    4 or 5 when wall => 'O',            // the mouth + tunnel bore
                    _ when wall => 'X',                 // wall above the mouth
                    _ when ledge => 'X',
                    _ => 'O',
                });
            }
            rows[r] = sb.ToString();
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    // Runs with the corner-plant feature ON (default-off in config: the bumpy
    // corridor is a chain of cave mouths and micro-plants cost it ~6% speed —
    // a live design trade; this suite pins the enabled behavior either way).
    private SimFrame[] Drop(Vector2 start, Vector2 vel, int frames = 240,
                            int ledgeCols = 8, int ledgeTopRow = 3)
    {
        bool prev = MovementConfig.Current.FoldCornerPlantEnabled;
        MovementConfig.Current.FoldCornerPlantEnabled = true;
        try
        {
            return SimRunner.Run(new SimConfig
            {
                Terrain = CaveTerrain(ledgeCols, ledgeTopRow),
                StartPosition = start,
                StartVelocity = vel,
                Script = InputScript.Always(new PlayerInput { Right = true }),
                Frames = frames, Dt = Dt, Gravity = Gravity,
            });
        }
        finally { MovementConfig.Current.FoldCornerPlantEnabled = prev; }
    }

    // A face smack: x pinned at the wall (vx collapsed) while the body
    // center is still above the mouth's top edge.
    private static int FaceStallFrames(SimFrame[] frames) =>
        frames.Count(f => f.X > 170f && f.X < 194f && MathF.Abs(f.Vx) < 5f && f.Y < 66f);

    [Fact]
    public void NearMiss_DucksUnderTheLip_AndEntersClean()
    {
        // The genuinely trim-sized shape (steep falls are UNSALVAGEABLE by
        // tiny authority, by design — see the bonk test): a low step just
        // outside the mouth, its top inside the opening's vertical band.
        // Walking off it, hover height puts the head a few px above the lip
        // with small vy — gravity alone loses the race to the face by a
        // hair, and the down-trim (Tuck outmuscles gravity; the tunnel floor
        // is within LegReach the whole way, so the near channels are live)
        // ducks it under. The airborne-arrival assert keeps this honest: a
        // fixture that face-plants high and wall-slides in proves nothing.
        var frames = Drop(new Vector2(100f, 59f), new Vector2(100f, 0f),
                          ledgeCols: 9, ledgeTopRow: 5);

        foreach (var f in frames.Where(f => f.X is > 150f and < 205f).Take(25))
            output.WriteLine($"  f{f.Frame}: x {f.X,6:F1} y {f.Y,6:F1} vy {f.Vy,6:F1} " +
                             $"fy {f.Fy,7:F0} {f.State}");

        var atFace = frames.FirstOrDefault(f => f.X > 184f);
        var inside = frames.FirstOrDefault(f => f.X > 240f);
        int stalls = FaceStallFrames(frames);
        float minVxInApproach = frames.Where(f => f.X is > 176f and < 200f)
                                      .Select(f => f.Vx).DefaultIfEmpty(0f).Min();
        bool trimmed = frames.Any(f => f.X is > 140f and < 190f && f.Y < 80f && f.Fy > 50f);
        output.WriteLine($"at face y={atFace?.Y ?? -1:F1}, entered frame {inside?.Frame ?? -1}, " +
                         $"stalls {stalls}, min vx {minVxInApproach:F1}, down-trim {trimmed}");

        // Airborne arrival INSIDE the mouth band: genuinely threaded, not a
        // runway walk-in (runway walkers arrive standing at y≈84).
        Assert.NotNull(atFace);
        Assert.InRange(atFace.Y, 64f, 82f);
        Assert.NotNull(inside);
        Assert.True(inside.Frame < 115, $"entry too slow: frame {inside.Frame}");
        Assert.True(stalls == 0, $"{stalls} face-smack frames ABOVE the mouth — the trim didn't duck");
        // Current entry shape: duck most of the way, then a brief hand-catch
        // ON the lip (a few frames of shed vx at mouth level, sliding down-in)
        // before walking on. The catch is bounded here; a fully seamless
        // no-catch entry needs a longer AmbientHorizon — logged as a design
        // decision (the horizon is the authored reflex/autopilot boundary).
        int slowFrames = frames.Count(f => f.X is > 170f and < 200f && MathF.Abs(f.Vx) < 30f);
        output.WriteLine($"slow frames at the mouth: {slowFrames}");
        Assert.True(slowFrames < 10, $"{slowFrames} near-stalled frames — a bonk, not a hand-catch");
        Assert.True(trimmed, "no authored down-trim in the approach — fixture too easy to prove assist");
    }

    [Fact]
    public void AimedWellAboveTheMouth_BonksHonestly()
    {
        // Crossing the face plane two blocks above the mouth: no tiny trim
        // can (or should) make this — the wall stops the body.
        var frames = Drop(new Vector2(120f, 10f), new Vector2(110f, 0f));

        int stalls = FaceStallFrames(frames);
        output.WriteLine($"face stalls {stalls}");
        Assert.True(stalls > 0,
            "no face contact above the mouth — the assist steered a bad fall into the cave");
    }
}
