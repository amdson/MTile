using System;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The climb family (Parkour / Mantle / ArcJump) swept across step height × entry
// speed × approach gap, at the game's 60 Hz AND 30 Hz (every other climb test runs
// at one rate; the ledge pull's 60 Hz timeout hid behind 30 Hz suites). Since the
// climbs follow their authored clips on PathTracker (PATH_TRACKER_UNIFICATION_PLAN
// phase 3), every climb that fires must hand straight to Standing — no mid-air
// completion into Falling, no MaxLipManeuverTime timeout — and end on top.
public class MantleVaultArcSweepTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1f / 60f)]
    [InlineData(1f / 30f)]
    public void EveryClimbThatFires_DeliversOnTopStraightIntoStanding(float dt)
    {
        const float TS = Chunk.TileSize;
        int climbs = 0;
        var failures = new System.Collections.Generic.List<string>();
        foreach (int tiles in new[] { 1, 2 })
        foreach (float v0 in new[] { 0f, 50f, 100f, 150f })
        foreach (float gap in new[] { 2f, 10f, 25f })
        {
            var rows = new string[10];
            for (int r = 0; r < 10; r++)
            {
                var c = new char[30];
                for (int x = 0; x < 30; x++) c[x] = r == 9 ? 'X' : (x >= 12 && r >= 9 - tiles ? 'X' : '.');
                rows[r] = new string(c);
            }
            float floorTop = 9 * TS, stepTop = (9 - tiles) * TS, face = 12 * TS;
            var trace = SimRunner.Run(new SimConfig
            {
                Terrain       = SimTerrain.FromAscii(string.Join("\n", rows), 0, 0),
                StartPosition = new Vector2(face - gap - 5.2f, floorTop - 20.8f),
                StartVelocity = new Vector2(v0, 0f),
                Script        = InputScript.Always(new PlayerInput { Right = true, Up = tiles >= 2 }),
                Frames        = (int)(1.6f / dt),
                Dt            = dt,
                Gravity       = new Vector2(0f, 600f),
            });
            string climb = trace.Select(f => f.State)
                .FirstOrDefault(s => s is "ParkourState" or "MantleState" or "ArcJumpState");
            if (climb == null) continue;
            climbs++;
            int last = Array.FindLastIndex(trace, f => f.State == climb);
            float dur = (last - Array.FindIndex(trace, f => f.State == climb) + 1) * dt;
            string label = $"{tiles}-tile v0={v0} gap={gap}: {climb}";
            if (dur >= MovementConfig.Current.MaxLipManeuverTime - dt) failures.Add($"{label} timed out ({dur:F3}s)");
            if (last + 1 < trace.Length && trace[last + 1].State != "StandingState")
                failures.Add($"{label} handed to {trace[last + 1].State}, not Standing");
            var end = trace[^1];
            if (!(end.X > face && end.Y < stepTop - 15f)) failures.Add($"{label} ended at ({end.X:F1}, {end.Y:F1}), not on top");
        }
        output.WriteLine($"dt={dt:F4}: {climbs} climbs fired");
        Assert.True(climbs >= 20, $"only {climbs} climbs fired — the sweep stopped exercising the family");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
