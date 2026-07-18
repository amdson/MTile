using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// Standing tool: export per-frame animation traces as CSV for the plotting notebook
// (notebooks/anim_traces.ipynb). One row per render frame; columns cover everything the
// solver produces — phase, clip, root offsets (δ, d.x), every bone's FINAL local rotation
// (rot_*) and every bone's solved angle correction (corr_*). Regenerate with:
//   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~TraceExport"
// Output: .probe/traces/<scenario>.csv (repo root).
public class TraceExportTests
{
    private const float Dt = Simulation.FixedDt;   // animator dt must match the sim's step
    private const float Scale = 0.6f;   // Game1.SkeletonScale

    // The real thing: hold right on flat ground through the actual sim (idle → spin-up →
    // steady run), animator fed exactly like the game (CharacterAnimSample.From).
    [Fact]
    public void Export_SimDriven_HoldRightFlat()
    {
        var floor = new StringBuilder();
        for (int r = 0; r < 3; r++) floor.AppendLine(new string('X', 150));
        var terrain = SimTerrain.FromAscii(floor.ToString(), originTileY: 10);

        var anim = NewAnimator(out var skel);
        var csv = NewCsv(skel);

        var cfg = new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 360,
            Players = { new SimPlayer
            {
                StartPosition = new Vector2(40f, 140f),
                Script        = InputScript.Always(new PlayerInput { Right = true }),
            } },
        };
        SimRunner.RunMulti(cfg, onFrame: (f, players) =>
        {
            var p = players[0];
            anim.Update(CharacterAnimSample.From(p, Dt));
            AppendRow(csv, skel, anim, f, p.Body.Position, p.Body.Velocity,
                      p.IsGrounded, p.CurrentStateName);
        });

        Save("holdright_flat.csv", csv);
    }

    // Solver-isolated controls: constant velocity on a virtual ground, no sim noise.
    [Theory]
    [InlineData("run",  90f)]
    [InlineData("walk", 25f)]
    public void Export_ConstantVelocity(string label, float vx)
    {
        var anim = NewAnimator(out var skel);
        var csv = NewCsv(skel);
        float x = 0f;
        for (int f = 0; f < 300; f++)
        {
            x += vx * Dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), +1, true, "RunningState", "", Dt));
            AppendRow(csv, skel, anim, f, new Vector2(x, 0f), new Vector2(vx, 0f),
                      grounded: true, state: "const");
        }
        Save($"constant_{label}_{(int)vx}.csv", csv);
    }

    // --- shared plumbing ------------------------------------------------------

    private static CharacterAnimator NewAnimator(out Skeleton skel)
    {
        skel = SkeletonExamples.Biped();
        var clips = AnimationStore.LoadAll(Path.Combine(FindUp("MTile.sln"), "SkeletonStates"));
        return new CharacterAnimator(skel, Scale, clips);
    }

    private static StringBuilder NewCsv(Skeleton skel)
    {
        var sb = new StringBuilder();
        sb.Append("frame,t,phase,clip,state,grounded,x,y,vx,vy,vert_offset,horiz_offset");
        for (int b = 0; b < skel.Count; b++) sb.Append(",rot_").Append(skel.Bones[b].Name);
        for (int b = 0; b < skel.Count; b++) sb.Append(",corr_").Append(skel.Bones[b].Name);
        sb.AppendLine();
        return sb;
    }

    private static void AppendRow(StringBuilder sb, Skeleton skel, CharacterAnimator anim,
                                  int f, Vector2 pos, Vector2 vel, bool grounded, string state)
    {
        var ci = CultureInfo.InvariantCulture;
        sb.Append(f).Append(',').Append((f * Dt).ToString("0.####", ci)).Append(',')
          .Append(anim.State.Phase.ToString("0.#####", ci)).Append(',')
          .Append(anim.State.Clip).Append(',').Append(state).Append(',')
          .Append(grounded ? 1 : 0).Append(',')
          .Append(pos.X.ToString("0.###", ci)).Append(',').Append(pos.Y.ToString("0.###", ci)).Append(',')
          .Append(vel.X.ToString("0.###", ci)).Append(',').Append(vel.Y.ToString("0.###", ci)).Append(',')
          .Append(anim.VerticalOffset.ToString("0.####", ci)).Append(',')
          .Append(anim.HorizontalOffset.ToString("0.####", ci));
        for (int b = 0; b < skel.Count; b++)
            sb.Append(',').Append(anim.Pose.Local[b].Rotation.ToString("0.#####", ci));
        for (int b = 0; b < skel.Count; b++)
            sb.Append(',').Append(anim.AngleCorrection(b).ToString("0.#####", ci));
        sb.AppendLine();
    }

    private static void Save(string file, StringBuilder csv)
    {
        string dir = Path.Combine(FindUp("MTile.sln"), ".probe", "traces");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), csv.ToString());
    }

    private static string FindUp(string marker)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null) { if (File.Exists(Path.Combine(d.FullName, marker))) return d.FullName; d = d.Parent; }
        throw new DirectoryNotFoundException(marker);
    }
}
