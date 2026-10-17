using System;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// User report: holding a direction while falling just below a stalactite (the
// depths stage's tapered Spike) fell much slower than open air. Cause: the
// lattice tracker enables the Redirect disc at any convex corner in hand reach
// (CorrectorChannels.MarkCornerPlants), and a descending tick accepted corners
// ABOVE the body's center — the spike's tip. The disc then served the level
// fall plan by cancelling the fall (vy 85 -> 9 over 12 frames, ~-900 px/s^2 of
// lift) for as long as the tip stayed within CornerPlantReach. A falling body
// is moving away from a corner above it; there is nothing there to push off.
public class StalactiteFallTests
{
    private const int T = Chunk.TileSize;
    private const int W = 70, H = 60, C = 35, RootRow = 3;

    // Ceiling slab (rows 0..2), floor far below, and optionally a DepthsLayout
    // Spike hanging from the slab at column C — same taper formula.
    private static ChunkMap Map(int len, int rootW, bool spike)
    {
        var g = new char[H, W];
        for (int r = 0; r < H; r++) for (int c = 0; c < W; c++)
            g[r, c] = (r < RootRow || r >= H - 2) ? 'X' : 'O';
        if (spike)
            for (int k = 0; k < len; k++)
            {
                int ww = Math.Max(1, (int)MathF.Round(rootW * (1f - k / (float)len)));
                int l = C - (ww - 1) / 2;
                for (int j = 0; j < ww; j++) g[RootRow + k, l + j] = 'X';
            }
        var sb = new StringBuilder();
        for (int r = 0; r < H; r++) { for (int c = 0; c < W; c++) sb.Append(g[r, c]); if (r < H - 1) sb.Append('\n'); }
        return SimTerrain.FromAscii(sb.ToString());
    }

    // Frames until the body has fallen 200 px below its start.
    private static int FallFrames(ChunkMap map, Vector2 start, Vector2 vel, PlayerInput input)
    {
        var frames = SimRunner.Run(new SimConfig
        {
            Terrain = map, StartPosition = start, StartVelocity = vel,
            Script = InputScript.Always(input), Frames = 150, Dt = 1f / 60f,
        });
        int i = Array.FindIndex(frames, f => f.Y > start.Y + 200f);
        return i < 0 ? int.MaxValue : i;
    }

    // dx: start x relative to the spike column's center; the body starts 14 px
    // below the tip (its head just clear of it) drifting down and toward
    // (and under) the spike, the held direction carrying it past.
    [Theory]
    [InlineData(2, 3, -24, 60f, 100f)]
    [InlineData(2, 3, -24, 0f, 0f)]
    [InlineData(4, 1, -24, 60f, 100f)]
    [InlineData(6, 3, -16, 60f, 100f)]
    public void FallingUnderTip_DirectionHeld_FallsLikeOpenAir(int len, int rootW, int dx, float vx, float vy)
    {
        foreach (int dir in new[] { 1, -1 })
        {
            float tipY = (RootRow + len) * T;
            var start = new Vector2(C * T + T / 2f + dx * dir, tipY + 14f);
            var vel = new Vector2(vx * dir, vy);
            var input = dir == 1 ? new PlayerInput { Right = true } : new PlayerInput { Left = true };

            int withSpike = FallFrames(Map(len, rootW, spike: true), start, vel, input);
            int open      = FallFrames(Map(len, rootW, spike: false), start, vel, input);
            Assert.True(withSpike - open <= 2,
                $"dir={dir} len={len} w={rootW} dx={dx}: fell 200 px in {withSpike} frames under the spike vs {open} in open air");
        }
    }
}
