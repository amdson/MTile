using System;
using System.IO;
using System.Text;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public static class SimReport
{
    // Prints a concise transition log + frame table to xUnit test output.
    public static void Print(SimFrame[] frames, ITestOutputHelper output, bool fullTable = true)
    {
        // Transition log
        output.WriteLine("── State transitions ──────────────────────────────────────────────────");
        output.WriteLine($"{"Frame",6} {"T(s)",7} {"X",8} {"Y",8} {"Vx",8} {"Vy",8}  State");
        output.WriteLine(new string('─', 72));
        foreach (var f in frames)
        {
            if (!f.Transition) continue;
            output.WriteLine($"{f.Frame,6} {f.T,7:F3} {f.X,8:F1} {f.Y,8:F1} {f.Vx,8:F1} {f.Vy,8:F1}  {f.State}");
        }
        output.WriteLine(string.Empty);

        if (!fullTable) return;

        // Full frame table
        output.WriteLine("── Per-frame data ─────────────────────────────────────────────────────");
        output.WriteLine($"{"Frame",6} {"T(s)",6} {"X",8} {"Y",8} {"Vx",7} {"Vy",7} {"Fx",8} {"Fy",8}  State");
        output.WriteLine(new string('─', 80));
        foreach (var f in frames)
        {
            string marker = f.Transition ? "→" : " ";
            output.WriteLine($"{f.Frame,6} {f.T,6:F3} {f.X,8:F1} {f.Y,8:F1} {f.Vx,7:F1} {f.Vy,7:F1} {f.Fx,8:F0} {f.Fy,8:F0} {marker}{f.State}");
        }
    }

    // Writes all frame data to a CSV file. Pass outputDir = null to use working directory.
    public static string WriteCsv(SimFrame[] frames, string name, string? outputDir = null)
    {
        string dir  = outputDir ?? Directory.GetCurrentDirectory();
        string path = Path.Combine(dir, $"{name}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Frame,T,X,Y,Vx,Vy,Fx,Fy,State,Transition");
        foreach (var f in frames)
            sb.AppendLine($"{f.Frame},{f.T:F4},{f.X:F3},{f.Y:F3},{f.Vx:F3},{f.Vy:F3},{f.Fx:F1},{f.Fy:F1},{f.State},{f.Transition}");

        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
