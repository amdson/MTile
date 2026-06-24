using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;

// Headless probe/console for the animation system. No window, no test runner — just
// runs the key classes (Skeleton, AnimationDocument, MotionProbe, CharacterAnimator)
// and prints. See Plans/ANIMATION_CODE_STATE.md §10.
//
//   dotnet run --project MTile.Probe -- list
//   dotnet run --project MTile.Probe -- digest <clip>        (no clip → write .probe/ for all)
//   dotnet run --project MTile.Probe -- diff   <clip> <ref>
//   dotnet run --project MTile.Probe -- report <clip>
//   dotnet run --project MTile.Probe -- anim   <base> [Action]   base=idle|walk|run|walkback|jump|fall|crouch|vault

static class Probe
{
    static Skeleton _rig;
    static List<AnimationDocument> _all;
    static string _statesDir;

    static int Main(string[] args)
    {
        try
        {
            _rig = SkeletonExamples.Biped();
            _statesDir = FindUp("SkeletonStates");
            _all = AnimationStore.LoadAll(_statesDir);

            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            switch (cmd)
            {
                case "list":   return List();
                case "digest": return Digest(args.Length > 1 ? args[1] : null);
                case "diff":   return Diff(Arg(args, 1), Arg(args, 2, "idle"));
                case "report": return Report(Arg(args, 1));
                case "anim":   return Anim(Arg(args, 1, "idle"), args.Length > 2 ? args[2] : null);
                default:
                    Console.Error.WriteLine($"unknown command '{cmd}'. try: list | digest | diff | report | anim");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    // --- commands ------------------------------------------------------------

    static int List()
    {
        Console.WriteLine($"# {_all.Count} clips in {_statesDir}");
        Console.WriteLine($"{"clip",-18} {"Type",-16} {"Region",-10} {"kf",3}  {"dur",5}  flags");
        foreach (var c in _all)
        {
            bool flagged = MotionProbe.Digest(c, _rig).Contains("FLAGS:");
            Console.WriteLine($"{c.Name,-18} {c.Type,-16} {c.Region,-10} {c.Keyframes.Count,3}  "
                            + $"{c.Duration,5:0.00}  {(flagged ? "FLAGS" : "")}");
        }
        return 0;
    }

    static int Digest(string name)
    {
        if (name == null)   // dump every clip to .probe/, like MotionProbeTests
        {
            string outDir = Path.Combine(FindUp("MTile.sln", file: true), ".probe");
            Directory.CreateDirectory(outDir);
            foreach (var c in _all)
                File.WriteAllText(Path.Combine(outDir, Safe(c.Name) + ".digest.md"), MotionProbe.Digest(c, _rig));
            Console.WriteLine($"wrote {_all.Count} digests to {outDir}");
            return 0;
        }
        var clip = Find(name);
        Console.Write(MotionProbe.Digest(clip, _rig));
        return 0;
    }

    static int Diff(string name, string refName)
    {
        Console.Write(MotionProbe.Diff(Find(name), Find(refName), _rig));
        return 0;
    }

    static int Report(string name)
    {
        Console.Write(MotionProbe.Report(Find(name), _rig, samples: 16));
        return 0;
    }

    // Construct a real CharacterAnimator and tick it ~1s with a synthetic sample stream,
    // then print the live COMPOSED+EASED pose — the runtime path the editor/static probe
    // both bypass (masks, overlay stack, easing). `baseSel` picks the locomotion clip via
    // the sample; `action` (optional) names an overlay Type to composite on top.
    static int Anim(string baseSel, string action)
    {
        var anim = new CharacterAnimator(_rig, scale: 1f, animations: _all, useSolver: false);
        var overlay = action != null ? _all.Find(c => string.Equals(c.Type, action, StringComparison.OrdinalIgnoreCase)) : null;
        float actDur = overlay?.Duration ?? 0.2f;
        string actType = overlay?.Type;   // exact Type string the animator keys on
        if (action != null && overlay == null)
            Console.WriteLine($"# (no clip with Type='{action}' — overlay inactive)");

        var (vel, grounded, move) = BaseSample(baseSel);
        var pos = Vector2.Zero;
        const float dt = 1f / 30f;
        int frames = 30;
        Console.WriteLine($"# anim: base='{baseSel}' overlay='{actType ?? "none"}'  {frames} frames @ {dt:0.000}s\n");

        for (int f = 0; f <= frames; f++)
        {
            float t = f * dt;
            var s = new CharacterAnimSample(
                position: pos, velocity: vel, facing: 1, grounded: grounded,
                movementState: move, action: actType ?? "None", dt: dt,
                actionTime: t, actionDuration: actType != null ? actDur : 0f,
                movementProgress: MathHelper.Clamp(t / 0.6f, 0f, 1f));
            anim.Update(s);
            pos += vel * dt;
            if (f == 0 || f == frames / 2 || f == frames)
            {
                Console.WriteLine($"## frame {f,2}  t={t:0.00}s  ActionWeight={anim.State.ActionWeight:0.00}  overlayActive={anim.OverlayActive}");
                LivePose(anim);
                Console.WriteLine();
            }
        }
        return 0;
    }

    // base selector → a sample that makes SelectClip pick that locomotion clip.
    static (Vector2 vel, bool grounded, string move) BaseSample(string sel) => sel.ToLowerInvariant() switch
    {
        "walk"     => (new Vector2(20f, 0f), true,  null),
        "run"      => (new Vector2(60f, 0f), true,  null),
        "walkback" => (new Vector2(-20f, 0f), true, null),     // moving against facing=+1
        "jump"     => (new Vector2(0f, -120f), false, null),
        "fall"     => (new Vector2(0f, 120f), false, null),
        "crouch"   => (Vector2.Zero, true, "CrouchState"),
        "vault"    => (new Vector2(40f, -40f), false, "ParkourState"),
        _          => (Vector2.Zero, true, null),               // idle
    };

    // --- a compact live-pose digest (mirrors MotionProbe's per-pose block) ---

    static void LivePose(CharacterAnimator anim)
    {
        var w = anim.Pose.ComputeWorld(Affine2.FromTRS(Vector2.Zero, 0f, Vector2.One));
        var rig = anim.Skeleton;
        // world[i].Translation is bone i's far end under the R·T·S chain — an anatomical landmark
        // (leg_upper→KNEE, leg_lower→ANKLE, foot→TOE, chest→shoulders, arm_lower→HAND).
        Vector2 P(string n) { int i = rig.IndexOf(n); return i < 0 ? Vector2.Zero : w[i].Translation; }

        float ground = float.NegativeInfinity;
        for (int i = 0; i < rig.Count; i++)
            ground = MathF.Max(ground, w[i].Translation.Y);
        Vector2 hip = P("hip"), chestTop = P("chest");
        Console.WriteLine($"  hip=({hip.X,5:0.0},{hip.Y,5:0.0})  lean(chestTop.x-hip.x)={chestTop.X - hip.X,5:0.0}");
        foreach (var s in new[] { "l", "r" })
        {
            Vector2 knee = P($"leg_{s}_upper"), ankle = P($"leg_{s}_lower"), toe = P($"foot_{s}");
            Vector2 d = ankle - hip; float L = d.Length();
            float side = L > 1e-4f ? ((knee.X - hip.X) * d.Y - (knee.Y - hip.Y) * d.X) / L : 0f;
            string dir = side > 0.2f ? "knee-FWD" : side < -0.2f ? "knee-RECURV" : "knee-straight";
            bool planted = toe.Y >= ground - 1.0f;
            Console.WriteLine($"  leg_{s}: {dir,-12} toe=({toe.X,5:0.0},{toe.Y,5:0.0})  {(planted ? "PLANTED" : "swing")}"
                            + (side < -0.2f ? "  <-- RECURVATUM" : ""));
        }
        foreach (var s in new[] { "l", "r" })
        {
            Vector2 hand = P($"arm_{s}_lower");
            string fb = hand.X > hip.X + 1f ? "front" : hand.X < hip.X - 1f ? "back" : "center";
            Console.WriteLine($"  arm_{s}: hand=({hand.X,5:0.0},{hand.Y,5:0.0})  {fb}");
        }
    }

    // --- helpers -------------------------------------------------------------

    static AnimationDocument Find(string name)
    {
        var c = _all.Find(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (c == null) throw new ArgumentException($"no clip '{name}'. run `list` to see clips.");
        return c;
    }

    static string Arg(string[] a, int i, string fallback = null)
        => i < a.Length ? a[i] : (fallback ?? throw new ArgumentException($"missing argument #{i}"));

    static string Safe(string s)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }

    static string FindUp(string marker, bool file = false)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string p = Path.Combine(d.FullName, marker);
            if (file ? File.Exists(p) : Directory.Exists(p)) return file ? d.FullName : p;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException($"could not find {marker} above {AppContext.BaseDirectory}");
    }
}
