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
//   dotnet run --project MTile.Probe -- addcom [clip] [--dry]    stamp grounded COM anchors (all clips, or one)
//   dotnet run --project MTile.Probe -- new <name> <type> [--dur s] [--from clip[@t]] [--noloop]
//   dotnet run --project MTile.Probe -- addkey <clip> <t> [--from clip[@t]]   pose = own-clip sample at t (shape-preserving) or a copy
//   dotnet run --project MTile.Probe -- contact <clip> <t> <node|none>        set the keyframe's planted contact
//   dotnet run --project MTile.Probe -- rot <clip> <t> <bone> <value> [--deg] escape hatch: set one bone's rotation
//   dotnet run --project MTile.Probe -- retime <clip> <t> <newT> | delkey <clip> <t> | dur <clip> <seconds>
//   dotnet run --project MTile.Probe -- ik <clip> <keyTime> <tip> <dx,dy> [--to] [--chain a,b] [--write]
//       nudge a tip (toe/hand) BY dx,dy rig units (+y DOWN) at the keyframe nearest keyTime and
//       solve the limb's angles to reach it; --to makes dx,dy an absolute root-local target.
//       Prints solved angles + miss; unreachable targets report the closest reachable point.
//       Dry-run by default — --write saves the solved angles back into the clip.

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
                case "addcom": return AddCom(FirstNonFlag(args, 1), HasFlag(args, "--dry"));
                case "ik":     return Ik(args);
                case "new":    return NewClip(args);
                case "addkey": return AddKey(args);
                case "contact": return Contact(args);
                case "rot":    return Rot(args);
                case "retime": return Retime(args);
                case "delkey": return DelKey(args);
                case "dur":    return Dur(args);
                default:
                    Console.Error.WriteLine($"unknown command '{cmd}'. try: list | digest | diff | report | anim | addcom | ik"
                                          + " | new | addkey | contact | rot | retime | delkey | dur");
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
        var anim = new CharacterAnimator(_rig, scale: 1f, animations: _all);
        var overlay = action != null ? _all.Find(c => string.Equals(c.Type, action, StringComparison.OrdinalIgnoreCase)) : null;
        float actDur = overlay?.Duration ?? 0.2f;
        string actType = overlay?.Type;   // exact Type string the animator keys on
        if (action != null && overlay == null)
            Console.WriteLine($"# (no clip with Type='{action}' — overlay inactive)");

        var (vel, grounded, moveTag) = BaseSample(baseSel);
        var pos = Vector2.Zero;
        const float dt = 1f / 30f;
        int frames = 30;
        Console.WriteLine($"# anim: base='{baseSel}' overlay='{actType ?? "none"}'  {frames} frames @ {dt:0.000}s\n");

        for (int f = 0; f <= frames; f++)
        {
            float t = f * dt;
            var s = new CharacterAnimSample(
                position: pos, velocity: vel, facing: 1, grounded: grounded,
                movementState: moveTag.ToString(), action: actType ?? "None", dt: dt,
                actionTime: t, actionDuration: actType != null ? actDur : 0f,
                movementProgress: MathHelper.Clamp(t / 0.6f, 0f, 1f), tag: moveTag);
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

    // Stamp a "com" Point addition onto every keyframe of each clip, computed so the
    // clip's standing feet rest on the ground line when the host anchors com onto the
    // body centroid (AttackGlowSystem.RigRoot). Replaces any existing "com" (e.g. run's
    // stale -23 that buried the feet). Other additions on a keyframe are preserved.
    //
    //   ground line (world)   = Body.Y + 2*Radius          (bottom of the player hexagon)
    //   foot world Y          = Body.Y + (soleLocal - com.Y) * scale
    //   feet on ground  =>     com.Y = soleLocal - 2*Radius / scale
    // soleLocal = the lower foot's toe (local +y is DOWN) at the first keyframe; com.X = 0
    // keeps the hip over the body center, matching the legacy placement.
    static int AddCom(string only, bool dryRun)
    {
        const float scale = Game1.SkeletonScale;
        const float worldGround = 2f * PlayerCharacter.Radius;   // centroid → hexagon bottom
        int n = 0;
        foreach (var clip in _all)
        {
            if (only != null && !string.Equals(clip.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
            float toeL = MotionProbe.Sample(clip, _rig, "foot_l", 0f).Tip.Y;
            float toeR = MotionProbe.Sample(clip, _rig, "foot_r", 0f).Tip.Y;
            float sole = MathF.Max(toeL, toeR);                  // lower foot (Y-down → larger Y)
            float comY = sole - worldGround / scale;
            foreach (var kf in clip.Keyframes)
            {
                kf.Additions ??= new List<AnimAddition>();
                kf.Additions.RemoveAll(a => string.Equals(a.Name, "com", StringComparison.Ordinal));
                kf.Additions.Add(new AnimAddition { Name = "com", Kind = AnimAdditionKind.Point, Px = 0f, Py = comY });
            }
            Console.WriteLine($"{clip.Name,-18} sole={sole,6:0.0}  com=(0.0,{comY,8:0.00})  {(dryRun ? "(dry)" : "written")}");
            if (!dryRun) AnimationStore.Save(clip, _statesDir);
            n++;
        }
        if (only != null && n == 0) { Console.Error.WriteLine($"no clip '{only}'. run `list` to see clips."); return 2; }
        Console.WriteLine($"# {(dryRun ? "would update" : "updated")} {n} clip(s)");
        return 0;
    }

    // Solve a limb's angles so a tip reaches a target at one keyframe (PoseIk on top of
    // the LM solver). Delta mode by default — "move the toe BY (dx,dy) from where this
    // keyframe puts it" — because that's the authoring loop's real question after reading
    // a digest; --to gives an absolute root-local target. Dry-run unless --write.
    static int Ik(string[] args)
    {
        var clip = Find(Arg(args, 1));
        float keyTime = ParseF(Arg(args, 2));
        string tipName = Arg(args, 3);
        // The vector may start with '-' (a negative delta), so only "--" tokens are
        // flags here; skip --chain's value too.
        string vecTok = null;
        for (int i = 4; i < args.Length && vecTok == null; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (string.Equals(args[i], "--chain", StringComparison.OrdinalIgnoreCase)) i++;
                continue;
            }
            vecTok = args[i];
        }
        if (vecTok == null) throw new ArgumentException("missing target 'dx,dy' (or 'x,y' with --to)");
        Vector2 vec = ParseVec(vecTok);
        bool absolute = HasFlag(args, "--to");
        bool write = HasFlag(args, "--write");

        int tip = _rig.IndexOf(tipName);
        if (tip < 0) throw new ArgumentException($"no bone '{tipName}' in rig '{_rig.Name}'");

        // Nearest keyframe to the requested time (clips carry editor-noise times like
        // 0.3197183, so snapping beats demanding an exact match).
        AnimationKeyframe kf = null; int kfIdx = -1; float best = float.MaxValue;
        for (int i = 0; i < clip.Keyframes.Count; i++)
        {
            float d = MathF.Abs(clip.Keyframes[i].Time - keyTime);
            if (d < best) { best = d; kf = clip.Keyframes[i]; kfIdx = i; }
        }
        if (kf == null) throw new ArgumentException($"clip '{clip.Name}' has no keyframes");

        // Chain: explicit --chain a,b,c or the tip's own limb.
        int[] chain;
        string chainArg = FlagValue(args, "--chain");
        if (chainArg != null)
        {
            var names = chainArg.Split(',');
            chain = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                chain[i] = _rig.IndexOf(names[i].Trim());
                if (chain[i] < 0) throw new ArgumentException($"no bone '{names[i]}' in rig");
            }
        }
        else chain = PoseIk.DefaultChain(_rig, tip);
        if (chain.Length == 0) throw new ArgumentException($"empty chain for '{tipName}' — pass --chain");

        var pose = _rig.CreatePose();
        PoseData.Apply(kf.Bones, pose);
        var root = Affine2.FromTRS(Vector2.Zero, 0f, Vector2.One);
        Vector2 current = pose.ComputeWorld(root)[tip].Translation;
        Vector2 target = absolute ? vec : current + vec;

        var seed = new float[chain.Length];
        for (int i = 0; i < chain.Length; i++) seed[i] = pose.Local[chain[i]].Rotation;

        var result = PoseIk.Solve(_rig, pose, tip, target, chain);

        Console.WriteLine($"# ik {clip.Name} @ key[{kfIdx}] t={kf.Time:0.000}"
                        + (best > 0.005f ? $" (nearest to requested {keyTime:0.000})" : "")
                        + $"  tip={tipName}");
        Console.WriteLine($"  current=({current.X,6:0.0},{current.Y,6:0.0})  target=({target.X,6:0.0},{target.Y,6:0.0})"
                        + (absolute ? "" : $"  (delta {vec.X:+0.0;-0.0},{vec.Y:+0.0;-0.0})"));
        Console.WriteLine($"  reached=({result.Achieved.X,6:0.0},{result.Achieved.Y,6:0.0})  miss={result.Miss:0.00}"
                        + (result.Miss > 2f
                            ? $"  <-- short by ({target.X - result.Achieved.X:+0.0;-0.0},{target.Y - result.Achieved.Y:+0.0;-0.0}); bring the target closer"
                            : ""));
        for (int i = 0; i < chain.Length; i++)
        {
            float now = pose.Local[chain[i]].Rotation;
            Console.WriteLine($"  {_rig.Bones[chain[i]].Name,-14} {seed[i],7:0.000} -> {now,7:0.000}  ({now - seed[i]:+0.000;-0.000})");
        }

        // Post-solve sanity: knee direction for any leg the chain touched, and how far
        // the solved angles sit from the SAME bone in the adjacent keyframes (a locally
        // perfect solve can still author a steep interval / fold flip across keys).
        var w = pose.ComputeWorld(root);
        foreach (var s in new[] { "l", "r" })
        {
            bool touches = false;
            foreach (int b in chain) touches |= _rig.Bones[b].Name.Contains($"_{s}");
            if (!touches || _rig.IndexOf($"leg_{s}_upper") < 0) continue;
            Vector2 hip = w[_rig.IndexOf("hip")].Translation;
            Vector2 knee = w[_rig.IndexOf($"leg_{s}_upper")].Translation;
            Vector2 ankle = w[_rig.IndexOf($"leg_{s}_lower")].Translation;
            Vector2 d = ankle - hip; float L = d.Length();
            float side = L > 1e-4f ? ((knee.X - hip.X) * d.Y - (knee.Y - hip.Y) * d.X) / L : 0f;
            if (side < -0.2f)
                Console.WriteLine($"  WARN leg_{s}: knee bends BACKWARD (recurvatum, side={side:0.0}) — revise the target");
        }
        for (int i = 0; i < chain.Length; i++)
        {
            string bone = _rig.Bones[chain[i]].Name;
            float now = pose.Local[chain[i]].Rotation;
            foreach (int j in new[] { kfIdx - 1, kfIdx + 1 })
            {
                if (j < 0 || j >= clip.Keyframes.Count) continue;
                var e = clip.Keyframes[j].Bones?.Find(b => b.Bone == bone);
                if (e == null) continue;
                float jump = MathF.Abs(now - e.Rotation);
                if (jump > 1.2f)
                    Console.WriteLine($"  WARN {bone}: {jump:0.00} rad from key t={clip.Keyframes[j].Time:0.000} — steep interval, consider moving neighbors too");
            }
        }

        if (write)
        {
            for (int i = 0; i < chain.Length; i++)
            {
                string bone = _rig.Bones[chain[i]].Name;
                var e = kf.Bones.Find(b => b.Bone == bone);
                if (e == null) kf.Bones.Add(e = new PoseBoneEntry { Bone = bone });
                e.Rotation = pose.Local[chain[i]].Rotation;
            }
            AnimationStore.Save(clip, _statesDir);
            Console.WriteLine($"  written to {clip.FilePath}");
        }
        else Console.WriteLine("  (dry run — pass --write to save)");
        return 0;
    }

    // --- clip authoring commands (edit clips programmatically; each prints the digest
    //     as the checked feedback, so authored geometry is always read back as numbers) ---

    // new <name> <type> [--dur s] [--from clip[@t]] [--noloop]
    // Create a clip with one keyframe at t=0: rest pose, or a pose sampled from an
    // existing clip (carrying its com/additions), ready for addkey/ik/contact shaping.
    static int NewClip(string[] args)
    {
        string name = Arg(args, 1), type = Arg(args, 2);
        if (_all.Exists(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"clip '{name}' already exists");
        var doc = new AnimationDocument
        {
            Name = name, Type = type, Skeleton = _rig.Name,
            Loop = !HasFlag(args, "--noloop"),
        };
        string dur = FlagValue(args, "--dur");
        if (dur != null) doc.Duration = ParseF(dur);

        var pose = _rig.CreatePose();
        List<AnimAddition> adds = null;
        string from = FlagValue(args, "--from");
        if (from != null)
        {
            var (src, t) = ParseClipAt(from);
            SamplePoseAt(src, t, pose);
            adds = NearestKey(src, t).kf.Additions;
        }
        else pose.SetToDefault();

        doc.Keyframes.Add(new AnimationKeyframe
        {
            Time = 0f, Bones = PoseData.Capture(pose), Additions = CloneAdds(adds),
        });
        AnimationStore.Save(doc, _statesDir);
        _all.Add(doc);
        Console.WriteLine($"created {doc.FilePath}");
        Console.Write(MotionProbe.Digest(doc, _rig));
        return 0;
    }

    // addkey <clip> <t> [--from clip[@t]]
    // Insert a keyframe at t. Default pose = the clip's OWN C1 sample at t (inserting a
    // key never changes the assembled motion — you then reshape it with ik/rot); --from
    // copies another clip's sampled pose instead. Additions (com) copy from the nearest
    // existing key so the anchor stays continuous.
    static int AddKey(string[] args)
    {
        var clip = Find(Arg(args, 1));
        float t = ParseF(Arg(args, 2));
        foreach (var k in clip.Keyframes)
            if (MathF.Abs(k.Time - t) < 1e-3f)
                throw new ArgumentException($"key already exists at t={k.Time:0.000} (zero-width intervals are forbidden)");

        var pose = _rig.CreatePose();
        string from = FlagValue(args, "--from");
        if (from != null) { var (src, ts) = ParseClipAt(from); SamplePoseAt(src, ts, pose); }
        else SamplePoseAt(clip, t, pose);

        clip.Keyframes.Add(new AnimationKeyframe
        {
            Time = t, Bones = PoseData.Capture(pose),
            Additions = CloneAdds(NearestKey(clip, t).kf.Additions),
        });
        clip.SortKeyframes();
        AnimationStore.Save(clip, _statesDir);
        Console.Write(MotionProbe.Digest(clip, _rig));
        return 0;
    }

    // contact <clip> <t> <node|none> — set the keyframe's planted contact (SelfPlant).
    static int Contact(string[] args)
    {
        var clip = Find(Arg(args, 1));
        var (kf, _) = NearestKey(clip, ParseF(Arg(args, 2)));
        string node = Arg(args, 3);
        if (string.Equals(node, "none", StringComparison.OrdinalIgnoreCase))
            kf.Contacts = new List<ContactLabel>();
        else
        {
            if (_rig.IndexOf(node) < 0) throw new ArgumentException($"no bone '{node}' in rig");
            kf.Contacts = new List<ContactLabel> { new() { Node = node, Weight = 1f } };
        }
        AnimationStore.Save(clip, _statesDir);
        Console.WriteLine($"key t={kf.Time:0.000} contacts = {(kf.Contacts.Count == 0 ? "none" : node)}");
        return 0;
    }

    // rot <clip> <t> <bone> <value> [--deg] — the raw-angle escape hatch (torso lean,
    // head tilt). Prefer ik for limbs; always read the digest it prints after.
    static int Rot(string[] args)
    {
        var clip = Find(Arg(args, 1));
        var (kf, _) = NearestKey(clip, ParseF(Arg(args, 2)));
        string bone = Arg(args, 3);
        if (_rig.IndexOf(bone) < 0) throw new ArgumentException($"no bone '{bone}' in rig");
        float v = ParseF(Arg(args, 4));
        if (HasFlag(args, "--deg")) v = MathHelper.ToRadians(v);
        var e = kf.Bones.Find(b => b.Bone == bone);
        if (e == null) kf.Bones.Add(e = new PoseBoneEntry { Bone = bone });
        e.Rotation = v;
        AnimationStore.Save(clip, _statesDir);
        Console.Write(MotionProbe.Digest(clip, _rig));
        return 0;
    }

    // retime <clip> <t> <newT> — move a keyframe on the timeline.
    static int Retime(string[] args)
    {
        var clip = Find(Arg(args, 1));
        var (kf, _) = NearestKey(clip, ParseF(Arg(args, 2)));
        float newT = ParseF(Arg(args, 3));
        foreach (var k in clip.Keyframes)
            if (k != kf && MathF.Abs(k.Time - newT) < 1e-3f)
                throw new ArgumentException($"another key already sits at t={k.Time:0.000}");
        Console.WriteLine($"key t={kf.Time:0.000} -> {newT:0.000}");
        kf.Time = newT;
        clip.SortKeyframes();
        AnimationStore.Save(clip, _statesDir);
        Console.Write(MotionProbe.Digest(clip, _rig));
        return 0;
    }

    // delkey <clip> <t> — remove the keyframe nearest t.
    static int DelKey(string[] args)
    {
        var clip = Find(Arg(args, 1));
        if (clip.Keyframes.Count <= 1) throw new ArgumentException("refusing to delete the last keyframe");
        var (kf, idx) = NearestKey(clip, ParseF(Arg(args, 2)));
        clip.Keyframes.RemoveAt(idx);
        AnimationStore.Save(clip, _statesDir);
        Console.WriteLine($"deleted key t={kf.Time:0.000}");
        Console.Write(MotionProbe.Digest(clip, _rig));
        return 0;
    }

    // dur <clip> <seconds> — set the clip's real-time duration for its [0,1] timeline.
    static int Dur(string[] args)
    {
        var clip = Find(Arg(args, 1));
        clip.Duration = ParseF(Arg(args, 2));
        AnimationStore.Save(clip, _statesDir);
        Console.WriteLine($"{clip.Name} duration = {clip.Duration:0.###}s");
        return 0;
    }

    // --- authoring helpers ---------------------------------------------------

    static (AnimationKeyframe kf, int idx) NearestKey(AnimationDocument clip, float t)
    {
        AnimationKeyframe kf = null; int idx = -1; float best = float.MaxValue;
        for (int i = 0; i < clip.Keyframes.Count; i++)
        {
            float d = MathF.Abs(clip.Keyframes[i].Time - t);
            if (d < best) { best = d; kf = clip.Keyframes[i]; idx = i; }
        }
        if (kf == null) throw new ArgumentException($"clip '{clip.Name}' has no keyframes");
        return (kf, idx);
    }

    // "clip" or "clip@t" → (document, sample time)
    static (AnimationDocument doc, float t) ParseClipAt(string spec)
    {
        int at = spec.IndexOf('@');
        return at < 0 ? (Find(spec), 0f) : (Find(spec[..at]), ParseF(spec[(at + 1)..]));
    }

    static void SamplePoseAt(AnimationDocument clip, float t, SkeletonPose dst)
    {
        var a = _rig.CreatePose(); var b = _rig.CreatePose();
        var c = _rig.CreatePose(); var d = _rig.CreatePose();
        AnimationSampler.SampleSmooth(clip, t - MathF.Floor(t), a, b, c, d, dst);
    }

    static List<AnimAddition> CloneAdds(List<AnimAddition> src)
    {
        if (src == null || src.Count == 0) return null;
        var list = new List<AnimAddition>(src.Count);
        foreach (var x in src) list.Add(x.Clone());
        return list;
    }

    // base selector → a sample that makes SelectClip pick that locomotion clip.
    static (Vector2 vel, bool grounded, AnimTag tag) BaseSample(string sel) => sel.ToLowerInvariant() switch
    {
        "walk"     => (new Vector2(20f, 0f), true,  AnimTag.None),
        "run"      => (new Vector2(60f, 0f), true,  AnimTag.None),
        "walkback" => (new Vector2(-20f, 0f), true, AnimTag.None),   // moving against facing=+1
        "jump"     => (new Vector2(0f, -120f), false, AnimTag.None),
        "fall"     => (new Vector2(0f, 120f), false, AnimTag.None),
        "crouch"   => (Vector2.Zero, true, AnimTag.Crouch),
        "crouchwalk" => (new Vector2(30f, 0f), true, AnimTag.Crouch),
        "vault"    => (new Vector2(40f, -40f), false, AnimTag.Parkour),
        _          => (Vector2.Zero, true, AnimTag.None),            // idle
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

    // First non-flag token at or after index `from` (flags start with '-'); null if none.
    static string FirstNonFlag(string[] a, int from)
    {
        for (int i = from; i < a.Length; i++)
            if (!a[i].StartsWith("-", StringComparison.Ordinal)) return a[i];
        return null;
    }

    static float ParseF(string s)
        => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    static Vector2 ParseVec(string s)
    {
        var parts = s.Split(',');
        if (parts.Length != 2) throw new ArgumentException($"expected 'x,y', got '{s}'");
        return new Vector2(ParseF(parts[0]), ParseF(parts[1]));
    }

    // Value following a flag token, e.g. "--chain leg_l_upper,foot_l"; null if absent.
    static string FlagValue(string[] a, string flag)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (string.Equals(a[i], flag, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
        return null;
    }

    static bool HasFlag(string[] a, string flag)
    {
        foreach (var s in a) if (string.Equals(s, flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

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
