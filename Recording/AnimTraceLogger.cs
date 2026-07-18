using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace MTile;

// Dev tool: per-frame animator trace logging during MANUAL play. Ctrl+L toggles; each
// stop writes .probe/traces/manual_<HHmmss>.csv at the repo root — the SAME schema as
// MTile.Tests TraceExportTests, so notebooks/anim_traces.ipynb plots it directly
// (set SCENARIO to the file's stem). Render-only observer: reads the animator/player,
// never writes. Rows buffer in memory (a few MB/hour) and flush on stop/exit, so the
// per-frame cost is string formatting only. No-ops silently where the filesystem
// isn't writable (WASM).
public sealed class AnimTraceLogger
{
    private readonly StringBuilder _rows = new();
    private KeyboardState _prevKeys;
    private bool _active;
    private int _frame;
    private float _t;
    private const int MaxFrames = 30 * 60 * 30;   // 30 min hard cap

    public bool Active => _active;

    // Call once per Update with the live keyboard; toggles on Ctrl+L.
    public void HandleInput(KeyboardState keys)
    {
        bool ctrl = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        if (ctrl && keys.IsKeyDown(Keys.L) && !_prevKeys.IsKeyDown(Keys.L))
        {
            if (_active) Stop();
            else Start();
        }
        _prevKeys = keys;
    }

    private void Start()
    {
        _rows.Clear();
        _frame = 0; _t = 0f;
        _active = true;
        Console.WriteLine("[animtrace] recording (Ctrl+L to stop + save)");
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        try
        {
            string dir = Path.Combine(RepoRoot(), ".probe", "traces");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"manual_{DateTime.Now:HHmmss}.csv");
            var sb = new StringBuilder();
            WriteHeader(sb, _skel);
            sb.Append(_rows);
            File.WriteAllText(file, sb.ToString());
            Console.WriteLine($"[animtrace] {_frame} frames -> {file}");
        }
        catch (Exception e) { Console.WriteLine($"[animtrace] save failed: {e.Message}"); }
        _rows.Clear();
    }

    private Skeleton _skel;

    // Call once per frame AFTER the cosmetic pass (animator holds this frame's solve).
    // dt = SIM time this frame; 0 (sim didn't step → animator didn't tick) logs nothing,
    // so rows correspond 1:1 to animator updates and t is the sim-time axis.
    public void Capture(CharacterAnimator anim, PlayerCharacter player, float dt)
    {
        if (!_active || anim == null || player == null || dt <= 0f) return;
        if (_frame >= MaxFrames) { Stop(); return; }
        _skel = anim.Skeleton;
        var ci = CultureInfo.InvariantCulture;
        var sb = _rows;
        sb.Append(_frame).Append(',').Append(_t.ToString("0.####", ci)).Append(',')
          .Append(anim.State.Phase.ToString("0.#####", ci)).Append(',')
          .Append(anim.State.Clip).Append(',').Append(player.CurrentStateName).Append(',')
          .Append(player.IsGrounded ? 1 : 0).Append(',')
          .Append(player.Body.Position.X.ToString("0.###", ci)).Append(',')
          .Append(player.Body.Position.Y.ToString("0.###", ci)).Append(',')
          .Append(player.Body.Velocity.X.ToString("0.###", ci)).Append(',')
          .Append(player.Body.Velocity.Y.ToString("0.###", ci)).Append(',')
          .Append(anim.VerticalOffset.ToString("0.####", ci)).Append(',')
          .Append(anim.HorizontalOffset.ToString("0.####", ci));
        int n = _skel.Count;
        for (int b = 0; b < n; b++)
            sb.Append(',').Append(anim.Pose.Local[b].Rotation.ToString("0.#####", ci));
        for (int b = 0; b < n; b++)
            sb.Append(',').Append(anim.AngleCorrection(b).ToString("0.#####", ci));
        sb.AppendLine();
        _frame++; _t += dt;
    }

    private static void WriteHeader(StringBuilder sb, Skeleton skel)
    {
        sb.Append("frame,t,phase,clip,state,grounded,x,y,vx,vy,vert_offset,horiz_offset");
        if (skel != null)
        {
            for (int b = 0; b < skel.Count; b++) sb.Append(",rot_").Append(skel.Bones[b].Name);
            for (int b = 0; b < skel.Count; b++) sb.Append(",corr_").Append(skel.Bones[b].Name);
        }
        sb.AppendLine();
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "MTile.sln"))) return d.FullName;
            d = d.Parent;
        }
        return AppContext.BaseDirectory;   // installed build: land next to the exe
    }
}
