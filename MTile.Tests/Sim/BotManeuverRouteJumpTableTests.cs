using System.Linq;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Guards the baked routing jump table (Net/Bot/Routing/JumpTable.g.cs) against movement
// drift: re-flies the envelope's boundary — every entry two or more cells up, and the
// longest jump at each height — with the same nudged starts the generator required. A
// failure means a retune moved what the bot can reach: re-run
// ZzzRouteCalibration.GenerateJumpTable, then this.
public class BotManeuverRouteJumpTableTests(ITestOutputHelper output)
{
    public static TheoryData<int, int> Boundary()
    {
        var d = new TheoryData<int, int>();
        foreach (var g in JumpTable.All.GroupBy(e => e.Dy))
        {
            if (g.Key >= 2) foreach (var e in g) d.Add(e.Dx, e.Dy);
            else { var far = g.OrderByDescending(e => e.Dx).First(); d.Add(far.Dx, far.Dy); }
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(Boundary))]
    public void Entry_StillLands(int dx, int dy)
    {
        Assert.True(JumpTable.TryGet(dx, dy, out var e));
        bool ok = ZzzRouteCalibration.Robust(dx, dy, (int)e.Takeoff, out int frames, out _, out int lmin, out int lmax, out _);
        output.WriteLine($"({dx},{dy}) {e.Takeoff}: ok {ok}, frames {frames}, land {lmin}..{lmax} (table {e.LandMin}..{e.LandMax})");
        Assert.True(ok, $"jump ({dx},{dy}) via {e.Takeoff} no longer lands");
        Assert.InRange(lmin, e.LandMin - 1, e.LandMax + 1);
        Assert.InRange(lmax, e.LandMin - 1, e.LandMax + 1);
    }
}
