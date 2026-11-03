using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// AvalancheForecast: the render-side look-ahead must (a) leave the live sim exactly as
// it was and (b) predict the cells the wave really claims when nothing else intervenes.
public class AvalancheForecastTests(ITestOutputHelper output)
{
    private static float Ts => Chunk.TileSize;
    private const int Cols = 50, Rows = 20, FloorRow = 12;

    private static ChunkMap Arena(int wallCol = -1)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
                sb.Append(y >= FloorRow || x < 2 || x >= Cols - 1 || x == wallCol ? 'X' : 'O');
            sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    private static Simulation Build(int wallCol, bool crystal, out MassBall ball)
    {
        var sim = new Simulation(Arena(wallCol), new Vector2(4 * Ts, FloorRow * Ts - 12f));
        sim.AddSecondaryPlayer(new Vector2(46 * Ts, FloorRow * Ts - 12f));
        for (int i = 0; i < 30; i++) sim.Step(default, default);   // settle
        ball = new MassBall(new Vector2(8 * Ts, (FloorRow - 1) * Ts + Ts * 0.5f), new Vector2(300f, 0f),
                            crystal ? 120f : 200f, TileType.Dirt, Faction.Player1, crystal);
        sim.SpawnEntity(ball);
        sim.Step(default, default);
        return sim;
    }

    private static Dictionary<EntityId, AvalancheForecast.Claim> Waves(MassBall b)
        => new() { [b.Id] = new AvalancheForecast.Claim(b.BlockType, b.Crystal) };

    [Theory]
    [InlineData(-1, false)]
    [InlineData(14, false)]   // a wall in the path: the forecast stops where the ball does
    [InlineData(-1, true)]    // crystal wave
    public void Forecast_MatchesReality_AndRewindsExactly(int wallCol, bool crystal)
    {
        var sim = Build(wallCol, crystal, out var ball);
        var twin = Build(wallCol, crystal, out _);

        var predicted = new Dictionary<(int, int), AvalancheForecast.Claim>();
        AvalancheForecast.Predict(sim, Waves(ball), default, default(PlayerInput), predicted);
        Assert.Equal(twin.Checksum(), sim.Checksum());   // the look-ahead left no trace
        Assert.NotEmpty(predicted);

        // Now let it really happen, collecting the same way.
        var actual = new HashSet<(int, int)>();
        for (int f = 0; f < AvalancheForecast.HorizonFrames; f++)
        {
            sim.Step(default, default);
            foreach (var (cell, w) in sim.Chunks.Waves.CellEntries) if (w == ball.Id) actual.Add(cell);
            foreach (var (cell, c) in sim.Chunks.Crystals.Entries) if (c.Wave == ball.Id) actual.Add(cell);
        }
        output.WriteLine($"predicted {predicted.Count}, actual {actual.Count}");
        Assert.Equal(actual.Count, predicted.Count);
        foreach (var cell in actual) Assert.True(predicted.ContainsKey(cell), $"missed {cell}");
        if (wallCol >= 0)
            foreach (var (x, _) in predicted.Keys) Assert.True(x < wallCol, $"forecast past the wall at x={x}");
    }

    [Fact]
    public void Update_MutesPresentationWhileForecasting()
    {
        var sim = Build(-1, false, out _);
        var log = new PresentationEventLog();
        sim.Chunks.OnTilePlaced += (pos, type) =>
            log.Emit(sim.Frame, new PresentationId(PresentationKind.TilePlace, (int)pos.X, (int)pos.Y), pos);
        var fc = new AvalancheForecast();
        fc.Update(sim, default, default(PlayerInput), log);
        Assert.NotEmpty(fc.Cells);
        Assert.Empty(log.Pending);
        Assert.False(log.Muted);
    }
}

public class ZzzAvalancheForecastCost(ITestOutputHelper output)
{
    [Fact]
    public void Cost_OnDuelStage()
    {
        var sim = MTile.Net.BotDuel.BuildArena("hall_close");
        for (int i = 0; i < 60; i++) sim.Step(default, default);
        var p = sim.Player.Body.Position;
        for (int run = 0; run < 4; run++)
        {
            var ball = new MassBall(p + new Vector2(20f, -10f), new Vector2(320f, 0f), 240f, TileType.Dirt, Faction.Player1);
            sim.SpawnEntity(ball);
            sim.Step(default, default);
            var into = new System.Collections.Generic.Dictionary<(int, int), AvalancheForecast.Claim>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AvalancheForecast.Predict(sim, new System.Collections.Generic.Dictionary<EntityId, AvalancheForecast.Claim>
                { [ball.Id] = new(ball.BlockType, false) }, default, default(PlayerInput), into);
            output.WriteLine($"run {run}: {sw.Elapsed.TotalMilliseconds:F1} ms, {into.Count} cells");
            var step = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 60; i++) sim.Step(default, default);
            output.WriteLine($"   60 live steps: {step.Elapsed.TotalMilliseconds:F1} ms");
        }
    }
}
