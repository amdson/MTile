using System.Text;
using Microsoft.Xna.Framework;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Diagnostic experiments, not acceptance gates: success rates and per-frame traces
// are the output. Keep production tuning unchanged; all config edits are restored.
public class TwoHighCorridorEntryTests(ITestOutputHelper output)
{
    private const int Ts = Chunk.TileSize, FloorRow = 12, MouthCol = 24;
    private const float Mouth = MouthCol * Ts;
    private static readonly string Results = Path.Combine(Path.GetTempPath(), "mtile-corridor-entry");

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

    [Fact]
    public void Measure_GeometricClearance_AndPlannerReach()
    {
        var cfg = MovementConfig.Current;
        int prevCells = cfg.LatticeCellsPerTile;
        var body = PlayerCharacter.CreateBodyPolygon();
        var bounds = body.GetBoundingBox(Vector2.Zero);
        float floor = FloorRow * Ts, ceiling = floor - 2 * Ts;
        float minY = ceiling - bounds.Top, maxY = floor - bounds.Bottom;
        output.WriteLine($"tile={Ts} bodyHeight={bounds.Height:F3} physical center band=[{minY:F3},{maxY:F3}] width={maxY-minY:F3}");
        output.WriteLine($"QP margin={cfg.CorrectorMargin:F3}; remaining vertical slack={maxY-minY-2*cfg.CorrectorMargin:F3}");
        try
        {
            foreach (int cells in new[] { 3, 4, 5, 6, 7, 8 })
            {
                cfg.LatticeCellsPerTile = cells;
                var planner = new LatticePathPlanner();
                var path = new CoastSample[LatticePathPlanner.MaxPath];
                var seed = new Vector2(Mouth + 3 * Ts, (minY + maxY) / 2);
                foreach (bool hover in new[] { true, false })
                {
                    int n = planner.Solve(Terrain(0), body, seed, new Vector2(100, 0), Vector2.UnitX,
                        hover, cfg.FoldHoverOffset, cfg.FoldRiseCost, path, out _, out bool bonk);
                    float advance = n > 0 ? path[n-1].Pos.X - seed.X : 0;
                    output.WriteLine($"cells={cells} hover={hover} totalMargin={(float)Ts/cells:F3} n={n} advance={advance:F2} bonk={bonk}");
                }
            }
        }
        finally { cfg.LatticeCellsPerTile = prevCells; }
        Assert.True(maxY > minY, "Fixture must physically fit the current body.");
    }

    [Fact]
    public void Measure_StepAndJumpEntrySensitivity()
    {
        Directory.CreateDirectory(Results);
        var cfg = MovementConfig.Current;
        string previous = cfg.FoldEngine;
        cfg.FoldEngine = "lattice";
        var summary = new List<string> { "kind,step,distance,speed,hold,phase,entered,entryFrames,minVx,stallFrames,peakImpulse,latticeFrames,noPathFrames,newObstacleFrames,opposingRowFrames,wallResidual,endX,endY,states" };
        try
        {
            foreach (int step in new[] { 0, 1 })
            foreach (float speed in new[] { 50f, 100f, 150f })
            foreach (float phase in new[] { 0f, 0.9f, 1.8f, 2.7f })
                summary.Add(Run(step, 24, speed, 0, phase));
            foreach (int step in new[] { 0, 1, 2 })
            foreach (float distance in new[] { 12f, 24f, 36f, 48f, 60f, 72f })
            foreach (int hold in new[] { 6, 12 })
            foreach (float phase in new[] { 0f, 1.8f })
                summary.Add(Run(step, distance, 100, hold, phase));
        }
        finally { cfg.FoldEngine = previous; }
        File.WriteAllLines(Path.Combine(Results, "summary.csv"), summary);
        File.WriteAllText(Path.Combine(Results, "movement-config.json"),
            System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        foreach (string line in summary) output.WriteLine(line);
        output.WriteLine($"Traces: {Results}");
        Assert.Equal(97, summary.Count);
    }

    [Fact]
    public void Measure_MarginAblations_AndCrouchControl()
    {
        Directory.CreateDirectory(Results);
        var cfg = MovementConfig.Current;
        var previous = (cfg.FoldEngine, cfg.LatticeCellsPerTile, cfg.CorrectorMargin);
        var summary = new List<string>();
        try
        {
            cfg.FoldEngine = "lattice";
            foreach (string mode in new[] { "baseline", "qp-margin", "dense-grid", "both" })
            {
                cfg.LatticeCellsPerTile = mode is "dense-grid" or "both" ? 8 : previous.LatticeCellsPerTile;
                cfg.CorrectorMargin = mode is "qp-margin" or "both" ? 0.5f : previous.CorrectorMargin;
                foreach (int step in new[] { 0, 1, 2 })
                foreach (int hold in new[] { 0, 12 })
                foreach (float phase in new[] { 0f, 1.8f })
                    summary.Add(mode + "," + Run(step, 24, 100, hold, phase, mode));
            }
            cfg.LatticeCellsPerTile = previous.LatticeCellsPerTile;
            cfg.CorrectorMargin = previous.CorrectorMargin;
            summary.Add("crouch," + Run(0, 24, 100, 0, 0, "crouch", true));
        }
        finally { (cfg.FoldEngine, cfg.LatticeCellsPerTile, cfg.CorrectorMargin) = previous; }
        File.WriteAllLines(Path.Combine(Results, "ablations.csv"), summary);
        foreach (string row in summary) output.WriteLine(row);
        Assert.Equal(49, summary.Count);
    }

    private static string Run(int step, float distance, float speed, int hold, float phase, string prefix = "", bool down = false)
    {
        var cfg = MovementConfig.Current;
        var chunks = Terrain(step);
        var shape = PlayerCharacter.CreateBodyPolygon();
        var bb = shape.GetBoundingBox(Vector2.Zero);
        var sim = new Simulation(chunks, new Vector2(Mouth - 100, FloorRow * Ts - bb.Bottom - cfg.FoldHoverOffset));
        for (int f = 0; f < 60; f++) sim.Step(default);
        sim.Player.Body.Position.X = Mouth - distance - phase;
        sim.Player.Body.Velocity = new Vector2(speed, 0);
        var scratch = sim.Player.CorrectorDebug;
        scratch.CaptureTrajectories = true;
        string kind = hold == 0 ? "step" : "jump";
        string name = prefix + FormattableString.Invariant($"{kind}_s{step}_d{distance}_v{speed}_h{hold}_p{phase}");
        var trace = new List<string> { "frame,x,y,vx,vy,state,impulse,pathCount,bonk,lattice,newObstacle,opposed,wallResidual" };
        bool capture = (prefix == "" && distance == 24 && speed == 100 &&
            ((hold == 0 && phase is 0f or 1.8f) || (step == 2 && hold == 12))) || prefix == "crouch";
        var pictures = new List<object>();
        var states = new HashSet<string>();
        int entry = -1, stable = 0, stalls = 0, lattice = 0, noPath = 0, newObstacle = 0, opposed = 0;
        float minVx = float.PositiveInfinity, peak = 0, residual = 0;
        float floor = (FloorRow - step) * Ts, ceiling = floor - 2 * Ts;
        for (int f = 0; f < 180; f++)
        {
            var before = sim.Player.Body.Position;
            sim.Step(new PlayerInput { Right = true, Space = f < hold, Down = down });
            var b = sim.Player.Body;
            states.Add(sim.Player.CurrentStateName);
            bool near = b.Position.X > Mouth - 30 && b.Position.X < Mouth + 4 * Ts;
            bool isLattice = scratch.BallisticCount == 5 && scratch.Problem.H == 5 && scratch.Ledger.ChannelCount > 0;
            bool missed = false, against = false;
            float frameResidual = 0;
            if (near)
            {
                minVx = MathF.Min(minVx, b.Velocity.X);
                if (b.Velocity.X < 10) stalls++;
                peak = MathF.Max(peak, b.LastImpulseMagnitude);
                if (isLattice)
                {
                    lattice++;
                    if (scratch.SolvedCount < 2) noPath++;
                    var tangent = scratch.SolvedCount >= 2
                        ? Vector2.Normalize(scratch.LatticePath[1].Pos - scratch.LatticePath[0].Pos) : Vector2.Zero;
                    for (int k = 0; k < 5; k++)
                    {
                        Vector2 delta = Vector2.Zero;
                        for (int j = 0; j <= k; j++) delta += scratch.TickDv[j] * ((k-j+1) * Simulation.FixedDt);
                        var free = scratch.DeliverySamples[k].Pos;
                        var corrected = free + delta;
                        foreach (var tile in Overlaps(chunks, shape, corrected))
                            if (!OverlapsTile(shape, free, tile.X, tile.Y) && !HasRow(scratch, tile.X, tile.Y)) missed = true;
                        for (int r = 0; r < scratch.Problem.RowCount; r++)
                        {
                            var row = scratch.Rows[r];
                            if (!row.HasCell || row.Tick != k) continue;
                            frameResidual = MathF.Max(frameResidual, row.Depth - Vector2.Dot(delta, row.Normal));
                            if (Vector2.Dot(row.Normal, tangent) < -0.1f && scratch.RowPush[r].LengthSquared() > 1e-6f) against = true;
                        }
                    }
                }
            }
            if (missed) newObstacle++;
            if (against) opposed++;
            residual = MathF.Max(residual, frameResidual);
            trace.Add(FormattableString.Invariant($"{f},{b.Position.X:F4},{b.Position.Y:F4},{b.Velocity.X:F4},{b.Velocity.Y:F4},{sim.Player.CurrentStateName},{b.LastImpulseMagnitude:F4},{scratch.SolvedCount},{scratch.Lattice.LastBonk},{isLattice},{missed},{against},{frameResidual:F4}"));
            if (capture)
            {
                var free = new List<float[]>();
                var corrected = new List<float[]>();
                var path = new List<float[]>();
                if (isLattice)
                {
                    for (int k = 0; k < 5; k++)
                    {
                        Vector2 delta = Vector2.Zero;
                        for (int j = 0; j <= k; j++) delta += scratch.TickDv[j] * ((k-j+1) * Simulation.FixedDt);
                        var p = scratch.DeliverySamples[k].Pos;
                        free.Add(new[] {p.X, p.Y});
                        corrected.Add(new[] {p.X + delta.X, p.Y + delta.Y});
                    }
                    for (int k = 0; k < scratch.SolvedCount; k++)
                        path.Add(new[] {scratch.LatticePath[k].Pos.X, scratch.LatticePath[k].Pos.Y});
                }
                pictures.Add(new { frame = f, x = b.Position.X, y = b.Position.Y, vx = b.Velocity.X, vy = b.Velocity.Y,
                    state = sim.Player.CurrentStateName, impulse = b.LastImpulseMagnitude,
                    lattice = isLattice, bonk = scratch.Lattice.LastBonk, before = new[] { before.X, before.Y },
                    free, corrected, path, missed, against, residual = frameResidual });
            }
            bool inside = b.Bounds.Left >= Mouth + 2 * Ts && b.Bounds.Top >= ceiling - 0.05f && b.Bounds.Bottom <= floor + 0.05f;
            stable = inside ? stable + 1 : 0;
            if (stable >= 6) { entry = f - 5; break; }
        }
        File.WriteAllLines(Path.Combine(Results, name + ".csv"), trace);
        if (capture)
            File.WriteAllText(Path.Combine(Results, name + ".json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                name, step, mouth = Mouth, floor, ceiling, tileSize = Ts,
                body = shape.GetVertices(Vector2.Zero).Select(p => new[] {p.X,p.Y}).ToArray(), frames = pictures
            }));
        var end = sim.Player.Body.Position;
        return FormattableString.Invariant($"{kind},{step},{distance},{speed},{hold},{phase},{entry>=0},{entry},{minVx:F2},{stalls},{peak:F2},{lattice},{noPath},{newObstacle},{opposed},{residual:F3},{end.X:F2},{end.Y:F2},{string.Join('|', states)}");
    }

    private static bool HasRow(CorrectorScratch s, int x, int y)
    {
        for (int r = 0; r < s.Problem.RowCount; r++)
            if (s.Rows[r].HasCell && s.Rows[r].CellX == x && s.Rows[r].CellY == y) return true;
        return false;
    }

    private static IEnumerable<Point> Overlaps(ChunkMap chunks, Polygon body, Vector2 pos)
    {
        float reach = CObstacleTemplate.For(body).Reach;
        for (int x = (int)MathF.Floor((pos.X-reach)/Ts); x <= (int)MathF.Floor((pos.X+reach)/Ts); x++)
        for (int y = (int)MathF.Floor((pos.Y-reach)/Ts); y <= (int)MathF.Floor((pos.Y+reach)/Ts); y++)
            if (TileQuery.IsSolidAt(chunks, (x+0.5f)*Ts, (y+0.5f)*Ts) && OverlapsTile(body, pos, x, y)) yield return new Point(x,y);
    }

    private static bool OverlapsTile(Polygon body, Vector2 pos, int x, int y)
    {
        var rel = pos - new Vector2((x+0.5f)*Ts, (y+0.5f)*Ts);
        foreach (var facet in CObstacleTemplate.For(body).Facets)
            if (Vector2.Dot(rel, facet.Normal) >= facet.Offset - 0.01f) return false;
        return true;
    }
}
