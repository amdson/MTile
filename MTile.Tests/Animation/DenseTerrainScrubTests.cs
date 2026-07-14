using System.Linq;
using System.Text;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

public class DenseTerrainScrubTests
{
    // The in-game recorder restores terrain frames in ARBITRARY order (scrub back, then
    // forward again). SimSnapshot's journal-based terrain can't do that — RewindTo
    // TRUNCATES history past the restored mark, so once you jump back you can't roll
    // forward again. That's why the recorder uses ChunkMap.CaptureDense/RestoreDense.
    // This pins the property the recorder relies on: restoring captures in any order
    // reproduces the exact dense grid every time.
    [Fact]
    public void RestoreDense_IsOrderIndependent()
    {
        var map = SimTerrain.FromAscii(@"
            XXXXXXXX
            X......X
            XXXXXXXX");

        var capA = map.CaptureDense();

        // Mutate through the real (journaled) break path so the grids genuinely differ.
        map.BreakCell(0, 0);
        map.BreakCell(7, 0);
        map.BreakCell(3, 2);
        var capB = map.CaptureDense();

        Assert.NotEqual(Sig(capA), Sig(capB));   // the mutation actually changed the grid

        // Restore A, B, A, B — the back-then-forward case the journal can't handle.
        map.RestoreDense(capA); Assert.Equal(Sig(capA), Sig(map.CaptureDense()));
        map.RestoreDense(capB); Assert.Equal(Sig(capB), Sig(map.CaptureDense()));
        map.RestoreDense(capA); Assert.Equal(Sig(capA), Sig(map.CaptureDense()));   // forward after backward
        map.RestoreDense(capB); Assert.Equal(Sig(capB), Sig(map.CaptureDense()));
    }

    // Stable signature of a dense capture: chunks sorted, then each cell's state+type.
    private static string Sig(DenseTerrainCapture cap)
    {
        var sb = new StringBuilder();
        foreach (var c in cap.Chunks.OrderBy(c => c.Pos.X).ThenBy(c => c.Pos.Y))
        {
            sb.Append(c.Pos.X).Append(',').Append(c.Pos.Y).Append(':');
            for (int i = 0; i < c.State.Length; i++)
                sb.Append((int)c.State[i]).Append('/').Append((int)c.Type[i]).Append(',');
            sb.Append(';');
        }
        return sb.ToString();
    }
}
