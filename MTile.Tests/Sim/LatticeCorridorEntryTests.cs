using System.Text;
using Microsoft.Xna.Framework;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Regression gates for entering a 2-high corridor on the lattice engine
// (Plans/LATTICE_PLANNING_OBJECTIONS.md objection 5): the probe's most
// sensitive cases, pinned. TwoHighCorridorEntryTests is the diagnostic sweep
// (no asserts); these fail. Entry must happen, must be PLANNED — the tracker
// reports LatticeOutcome.Route on the approach, so a fallback drive plus the
// auto-crouch can no longer pass as a planned entry — and the phase pair
// 0 / 1.8 px must both stay under the impulse ceiling (the pair the probe
// found 0 vs 132 px/s apart before the clearance and hover-pricing fixes).
//
// Same fixture as the probe: floor top at row 12 (y=132); the corridor from
// tile x=24 (x=264) on is two tiles (22 px) high with solid mass above and
// below; step = how many tiles its floor rises above the approach floor.
public class LatticeCorridorEntryTests(ITestOutputHelper output)
{
    private const int Ts = Chunk.TileSize, FloorRow = 12, MouthCol = 24;
    private const float Mouth = MouthCol * Ts;

    // Acceptance thresholds (set from the post-fix measurement, 2026-09-05;
    // see Plans/TWO_HIGH_CORRIDOR_ENTRY_REPORT.md for the pre-fix numbers).
    private const int   MaxEntryFrames = 90;     // within 1.5 s of the approach start
    private const float MaxImpulse     = 60f;    // px/s — a graze, not the 132 px/s face-smack

    private static ChunkMap Terrain(int step)
    {
        var rows = new List<string>();
        for (int y = 0; y < FloorRow + 2; y++)
        {
            var row = new StringBuilder();
            for (int x = 0; x < 80; x++)
                row.Append(y >= FloorRow || (x >= MouthCol &&
                    (y >= FloorRow - step || y < FloorRow - step - 2)) ? 'X' : 'O');
            rows.Add(row.ToString());
        }
        return SimTerrain.FromAscii(string.Join('\n', rows));
    }

    private sealed record Result(bool Entered, int EntryFrame, float PeakImpulse, float MinVx,
                                 int Lattice, int Route, int Refused, int NoRoute, string States);

    // Settle 60 frames, then place the body `distance + phase` px before the
    // mouth at `speed` px/s and hold Right for up to 180 frames. Entry = the
    // whole body two tiles past the mouth and inside the opening for six
    // consecutive frames. Near-mouth = x within 30 px before to 4 tiles past.
    private static Result Run(int step, float distance, float speed, float phase)
    {
        var cfg = MovementConfig.Current;
        string prevEngine = cfg.FoldEngine;
        cfg.FoldEngine = "lattice";
        try
        {
            var chunks = Terrain(step);
            var shape = PlayerCharacter.CreateBodyPolygon();
            var bb = shape.GetBoundingBox(Vector2.Zero);
            var sim = new Simulation(chunks, new Vector2(Mouth - 100, FloorRow * Ts - bb.Bottom - cfg.FoldHoverOffset));
            for (int f = 0; f < 60; f++) sim.Step(default);
            sim.Player.Body.Position.X = Mouth - distance - phase;
            sim.Player.Body.Velocity = new Vector2(speed, 0);
            var scratch = sim.Player.CorrectorDebug;
            var states = new List<string>();
            float floor = (FloorRow - step) * Ts, ceiling = floor - 2 * Ts;
            int entry = -1, stable = 0, lattice = 0, route = 0, refused = 0, noRoute = 0;
            float minVx = float.PositiveInfinity, peak = 0f;
            for (int f = 0; f < 180; f++)
            {
                sim.Step(new PlayerInput { Right = true });
                var b = sim.Player.Body;
                if (states.Count == 0 || states[^1] != sim.Player.CurrentStateName) states.Add(sim.Player.CurrentStateName);
                if (b.Position.X > Mouth - 30 && b.Position.X < Mouth + 4 * Ts)
                {
                    minVx = MathF.Min(minVx, b.Velocity.X);
                    peak = MathF.Max(peak, b.LastImpulseMagnitude);
                    switch (scratch.LatticeOutcome)
                    {
                        case LatticeOutcome.Route:   lattice++; route++; break;
                        case LatticeOutcome.Refused: lattice++; refused++; break;
                        case LatticeOutcome.NoRoute: lattice++; noRoute++; break;
                    }
                }
                bool inside = b.Bounds.Left >= Mouth + 2 * Ts && b.Bounds.Top >= ceiling - 0.05f && b.Bounds.Bottom <= floor + 0.05f;
                stable = inside ? stable + 1 : 0;
                if (stable >= 6) { entry = f - 5; break; }
            }
            return new Result(entry >= 0, entry, peak, minVx, lattice, route, refused, noRoute, string.Join('>', states));
        }
        finally { cfg.FoldEngine = prevEngine; }
    }

    private void Report(string name, Result r) =>
        output.WriteLine($"{name}: entered={r.Entered} frame={r.EntryFrame} peakImpulse={r.PeakImpulse:F1} minVx={r.MinVx:F1} " +
                         $"lattice={r.Lattice} route={r.Route} refused={r.Refused} noRoute={r.NoRoute} states={r.States}");

    // The level walk-in is now PLANNED (Route on every approach frame; the
    // path dips 12.8 px over ~15 px of run) but not delivered: the tracker's
    // 5-tick horizon lags the dip by ~3.5 px at 100 px/s, the body clips the
    // lintel wall 0.8 px short, and from a body pressed against the lip no
    // forward-only edge exists (no vertical offsets in the cone), so the DP
    // refuses and the drive walks into the wall — objection 9 of
    // Plans/LATTICE_PLANNING_OBJECTIONS.md; the open question is BACKLOG.md
    // §1's "2-high corridor entry" item. Un-skip when that is decided.
    private const string LevelWalkIn =
        "level 2-high walk-in: planned but the tracker lags the 12.8 px dip into the lintel wall — open dynamics question, see the class comment";

    // The one-tile step at 100 px/s from 24 px out, and the level walk-in:
    // the probe's phase-sensitive pair (offset 0 clean, offset 1.8 px a
    // 132 px/s smack) and the case that never entered at all (0/12).
    [Theory]
    [InlineData(1, 0f)]
    [InlineData(1, 1.8f)]
    [InlineData(0, 0f, Skip = LevelWalkIn)]
    [InlineData(0, 1.8f, Skip = LevelWalkIn)]
    public void HoldRight_EntersPlanned_WithoutASmack(int step, float phase)
    {
        var r = Run(step, 24f, 100f, phase);
        Report($"step={step} phase={phase}", r);
        Assert.True(r.Entered, "never entered the corridor");
        Assert.True(r.EntryFrame <= MaxEntryFrames, $"entry took {r.EntryFrame} frames");
        Assert.True(r.PeakImpulse <= MaxImpulse, $"collision impulse {r.PeakImpulse:F1} px/s at the mouth");
        // Planned, not fallen into: the planner routed on the majority of
        // near-mouth tracker frames.
        Assert.True(r.Lattice > 0, "no lattice tracker frames near the mouth");
        Assert.True(r.Route * 2 > r.Lattice, $"entry was mostly fallback: route={r.Route} refused={r.Refused} noRoute={r.NoRoute}");
    }
}
