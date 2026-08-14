using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// The presentation seam's one job: survive replay. A rollback re-runs Step over frames
// it has already run, so the cosmetic events fire again with the same (frame, id) — and
// must not be presented twice. See Plans/AUDIO_PLAN.md §4.
public class PresentationEventLogTests
{
    private static PresentationId Tile(int x, int y)
        => new(PresentationKind.TileBreak, x, y);

    [Fact]
    public void ReplayedFrameIsNotPresentedTwice()
    {
        var log = new PresentationEventLog();
        log.Emit(10, Tile(3, 4), Vector2.Zero);
        Assert.Single(log.Pending);
        log.Clear();

        // Rollback to 10 and re-step it — the same break, the same frame.
        log.Emit(10, Tile(3, 4), Vector2.Zero);
        Assert.Empty(log.Pending);
    }

    [Fact]
    public void RepeatedRollbacksOverTheSameFrameStayDeduped()
    {
        var log = new PresentationEventLog();
        for (int i = 0; i < 5; i++) log.Emit(10, Tile(3, 4), Vector2.Zero);
        Assert.Single(log.Pending);
    }

    [Fact]
    public void DistinctTilesAndFramesArePresentedSeparately()
    {
        var log = new PresentationEventLog();
        log.Emit(10, Tile(3, 4), Vector2.Zero);
        log.Emit(10, Tile(3, 5), Vector2.Zero);   // different tile, same frame
        log.Emit(11, Tile(3, 4), Vector2.Zero);   // same tile, later frame
        Assert.Equal(3, log.Pending.Count);
    }

    [Fact]
    public void ResetForgetsTheOldTimeline()
    {
        var log = new PresentationEventLog();
        log.Emit(10, Tile(3, 4), Vector2.Zero);
        log.Clear();

        log.Reset();                              // stage reload: frame counter restarts
        log.Emit(10, Tile(3, 4), Vector2.Zero);
        Assert.Single(log.Pending);
    }
}
