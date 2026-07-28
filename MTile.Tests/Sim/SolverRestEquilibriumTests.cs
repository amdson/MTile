using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Isolated solve of the rest-on-floor state: 9 soft hover up-rows (depth ~10)
// + LegServo/Traction channels. Why does z0 converge to ~4 instead of a real
// supporting force? Diagnostic.
public class SolverRestEquilibriumTests(ITestOutputHelper output)
{
    [Fact]
    public void RestOnFloor_SoftHoverRows_LegServoResponse()
    {
        const int n = 10;
        float dt = 1f / 60f;

        var rows = new ClearanceRow[32];
        int rc = 0;
        for (int k = 0; k < 9; k++)
            rows[rc++] = new ClearanceRow { Tick = k, Normal = new Vector2(0, -1), Depth = 10f, HingeScale = 0.02f };

        var mask = new bool[n];
        var cap = new float[n];
        for (int k = 0; k < n; k++) { mask[k] = true; cap[k] = 6000f; }

        var coastVel = new Vector2[n];
        var p = new CorrectionProblem
        {
            H = n, Dt = dt, CoastVel = coastVel,
            Rows = rows, RowCount = rc,
            Channels = new ChannelDef[2],
            PrevApplied = new Vector2[2],
            DeltaWeight = 5f, HingeWeight = 1e6f, InnerIterations = 16,
        };
        p.Channels[0] = new ChannelDef {
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0, -1), CapPerTick = cap, ActiveMask = mask };
        p.Channels[1] = new ChannelDef {
            Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true,
            Axis = new Vector2(1, 0), Cap = 3000f, ActiveMask = mask };
        p.ChannelCount = 2;

        var z = new Vector2[2 * n];
        var zs = new Vector2[2 * n];
        float residual = CorrectionSolver.Solve(p, z, zs);

        var sb = new StringBuilder();
        for (int k = 0; k < n; k++) sb.Append($" {z[k].Y:F0}");
        output.WriteLine($"LegServo z.Y per tick:{sb}");
        output.WriteLine($"residual={residual:F2}");

        // What does the plan achieve at each row?
        for (int j = 0; j < rc; j++)
            output.WriteLine($"row T={rows[j].Tick} depth={rows[j].Depth:F1} slack={CorrectionSolver.RowSlack(p, z, j):F2}");
    }
}
