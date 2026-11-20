using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Net;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Scratch diagnostic: P2 walks at P1 mashing slash; P1 lands one S1 and stops.
public class ZzzWalkInTrace(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private int _pre = System.Environment.GetEnvironmentVariable("WALKIN_PRE") is string q ? int.Parse(q) : 2;
    private float _gap = System.Environment.GetEnvironmentVariable("WALKIN_GAP") is string g ? float.Parse(g) : 90f;

    [Fact]
    public void Sweep()
    {
        foreach (float g in new[] { 70f, 80f, 90f, 100f, 110f, 120f, 130f }) { _gap = g; for (int t = 4; t <= 80; t += 2) WalkIn(t, true, false); }
    }

    [Theory]
    [InlineData(0, true)]
    public void One(int p1Tap, bool p2Mashes) => WalkIn(System.Environment.GetEnvironmentVariable("WALKIN_TAP") is string e ? int.Parse(e) : p1Tap, p2Mashes, true);

    private void WalkIn(int p1Tap, bool p2Mashes, bool verbose)
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        foreach (var ch in terrain) for (int i = 0; i < Chunk.Size; i++) for (int j = 0; j < Chunk.Size; j++) ch.Tiles[i, j].Type = TileType.Hardened;
        float floorTopY = 3 * Chunk.TileSize;
        var aStart = new Vector2(100f, floorTopY - PlayerCharacter.Radius);
        var vStart = new Vector2(100f + _gap, floorTopY - PlayerCharacter.Radius);
        var mouseAhead = new Vector2(380f, 20f);
        var mouseBack  = new Vector2(-100f, 20f);

        var idleA = new PlayerInput { MouseWorldPosition = mouseAhead };
        var attacker = new InputScript().For(p1Tap, idleA)
            .For(1, new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAhead }).Forever(idleA);

        var walkV = new PlayerInput { Left = true, MouseWorldPosition = mouseBack };
        var tapV  = new PlayerInput { Left = true, LeftClick = true, MouseWorldPosition = mouseBack };
        var victim = new InputScript();
        if (p2Mashes) { for (int i = 0; i < 60; i++) victim = victim.For(1, tapV).For(3, walkV); }
        victim = victim.Forever(walkV);

        var cfg = new SimConfigMulti
        {
            Terrain = terrain, Frames = 110, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = aStart, Script = attacker },
                new SimPlayer { StartPosition = vStart, Script = victim, Faction = Faction.Neutral },
            },
        };
        output.WriteLine($"--- gap={_gap} p1Tap={p1Tap} p2Mashes={p2Mashes}");
        float hpA = 5, hpV = 5; string first = "none";
        SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            var a = ps[0]; var v = ps[1];
            if (first == "none" && v.Health < hpV) first = $"P1 hit P2 at f{f} gap={v.Body.Position.X - a.Body.Position.X:F1} P2 was {v.CurrentActionName}";
            if (first == "none" && a.Health < hpA) first = $"P2 hit P1 at f{f}";
            if (!verbose || f < p1Tap - _pre || f > p1Tap + 60) return;
            int vStun = v.Combat.HitstunActive ? v.Combat.HitstunExpireFrame - f : 0;
            int aStun = a.Combat.HitstunActive ? a.Combat.HitstunExpireFrame - f : 0;
            output.WriteLine($"f{f,3}: A={a.CurrentActionName,-14} {a.CurrentStateName,-14} hp={a.Health:F2} hs={aStun,2} x={a.Body.Position.X,6:F1} vx={a.Body.Velocity.X,6:F1} | V={v.CurrentActionName,-14} {v.CurrentStateName,-14} hp={v.Health:F2} hs={vStun,2} x={v.Body.Position.X,6:F1} vx={v.Body.Velocity.X,6:F1} y={v.Body.Position.Y,5:F1} vy={v.Body.Velocity.Y,6:F1} gap={v.Body.Position.X - a.Body.Position.X:F1}");
        });
        output.WriteLine("    first: " + first);
    }
}
