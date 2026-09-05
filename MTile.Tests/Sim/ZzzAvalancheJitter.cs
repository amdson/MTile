using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Scratch diagnostic (Zzz = excluded from groups and plain `full`): per-frame
// Δv vs live moving contacts during a 45° avalanche ride, to localize the
// "shoved by tiles" jitter. Not a regression test.
public class ZzzAvalancheJitter(ITestOutputHelper output)
{
    private static float Ts => Chunk.TileSize;
    private const int FloorRow = 60, PlayerCol = 60, Cols = 140, Rows = 90;

    private void RunAndDump(Func<int, PlayerInput> input, string label)
    {
        var terrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
        var player = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
        float rad = 45f * MathF.PI / 180f;
        var origin = new Vector2(PlayerCol * Ts + Ts * 0.5f, (FloorRow - 1) * Ts + Ts * 0.5f);
        var wave = new ScriptedWave(origin, rad, 25f * Ts, 60f);

        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        Vector2 prevVel = default;

        output.WriteLine($"── {label} ──");
        for (int f = 0; f < 130; f++)
        {
            ctrl.InjectInput(input?.Invoke(f) ?? new PlayerInput());
            terrain.TickSprouts(AvalancheHarness.Dt);
            terrain.Impact.Tick(AvalancheHarness.Dt);
            player.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), AvalancheHarness.Dt);
            var force = player.Body.AppliedForce;
            if (!wave.Done) wave.Step(terrain, AvalancheHarness.Dt);
            PhysicsWorld.StepSwept(bodies, terrain, AvalancheHarness.Dt, AvalancheHarness.Gravity);

            var dv = player.Body.Velocity - prevVel;
            prevVel = player.Body.Velocity;
            if (f < 15) continue;

            // Full path: one row per frame. The ASCII lane marks x relative to
            // the wave's ideal 45° track through the origin, so lateral wobble
            // reads as the '*' walking off the '|' center line (2px per column).
            float idealX = origin.X + (origin.Y - player.Body.Position.Y);   // 45°, y-down
            float off = player.Body.Position.X - idealX;
            int lane = Math.Clamp((int)MathF.Round(off / 2f) + 20, 0, 40);
            var laneStr = new char[41];
            for (int i = 0; i < 41; i++) laneStr[i] = i == 20 ? '|' : ' ';
            laneStr[lane] = '*';
            output.WriteLine($"f{f,3} p=({player.Body.Position.X,7:F1},{player.Body.Position.Y,7:F1}) " +
                             $"v=({player.Body.Velocity.X,6:F1},{player.Body.Velocity.Y,7:F1}) dvx={dv.X,6:F1} " +
                             $"{new string(laneStr)} {player.CurrentStateName}");
        }
    }

    [Fact]
    public void Dump45DegreeRideJitter()
    {
        RunAndDump(null, "45deg neutral");
        RunAndDump(f => new PlayerInput { Right = true }, "45deg hold-right (into the sweep)");
        RunAndDump(f => new PlayerInput { Left = true }, "45deg hold-left (against the sweep)");
    }
}
