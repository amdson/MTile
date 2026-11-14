using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MTile;

namespace MTileDemo;

// One-shot batch host: uses the actual demo/game renderers, then saves and exits.
// A graphics context is required (DesktopGL); no keyboard interaction is required.
public sealed class SpriteStripGame : Game
{
    private readonly string _output, _clipName, _takePath, _bindingPath, _rigName, _scenario;
    private readonly int _frames = 12, _columns = 4, _size = 240, _facing = 1;
    private readonly int _stairs = 10;
    private readonly float _start = 0, _end = -1;
    private readonly bool _world, _overlay, _trails, _contacts, _path;
    // Live walk scenarios besides "stairs": flat ground with one obstacle at column 12.
    private static readonly string[] Obstacles =
        { "bump", "bump3", "bumps", "bumps2", "step", "stepdown", "wall2", "pillar2", "ceiling", "ceilbumps" };
    // Column profile of an obstacle scenario: floor rise in tiles, and ceiling clearance
    // above the base ground in tiles (0 = open sky).
    private (int Rise, int Clear) Column(int x) => _scenario switch
    {
        "bump"      => (x == 12 ? 1 : 0, 0),                       // single block
        "bump3"     => (x is >= 12 and <= 14 ? 1 : 0, 0),          // 3-wide, 1-high platform
        "bumps"     => (x is 12 or 14 ? 1 : 0, 0),                 // two blocks, 1-tile gap
        "bumps2"    => (x is 12 or 15 ? 1 : 0, 0),                 // two blocks, 2-tile gap
        "step"      => (x >= 12 ? 1 : 0, 0),                       // 1-tile step up
        "stepdown"  => (x < 12 ? 1 : 0, 0),                        // 1-tile step down
        "wall2"     => (x >= 12 ? 2 : 0, 0),                       // 2-tile step: the walk refuses it
        "pillar2"   => (x == 12 ? 2 : 0, 0),                       // 2-tile column
        "ceiling"   => (0, x is >= 12 and <= 20 ? 2 : 0),          // 2-high corridor
        "ceilbumps" => (0, x is 12 or 14 or 17 ? 2 : 0),           // ceiling bumps: 1- and 2-tile gaps
        _ => (0, 0),
    };
    private readonly List<Frame> _panels = new();
    private SpriteBatch _batch;
    private Texture2D _pixel;
    private SpriteFont _font;
    private DrawContext _draw;
    private SkeletonPose _pose, _skinPose;
    private SpriteSkin _skin;
    private SpriteAttachmentRenderer _attachments;
    private readonly List<AttachmentSample> _attachmentSamples = new();
    private AnimTake _take;
    private Vector2 _min, _max;
    private bool _exported;

    private sealed record Frame(BoneTransform[] Pose, Vector2 Root, Vector2 Camera,
        float Scale, int Facing, string Label, int Terrain = -1,
        AnimationDocument Clip = null, float Tau = 0,
        AttachmentSample[] Effects = null, float Seconds = 0, FootDebug[] Feet = null,
        TelegraphShape[] Debug = null, Vector2[] Bodies = null, Vector2[] Path = null,
        string[] Front = null, float Turn = 1f);

    private sealed record FootDebug(string Name, FootPlan Plan, Vector2 Actual);

    public SpriteStripGame(string[] args)
    {
        string rig = null, binding = null;
        bool columnsSpecified = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
            int Number() => int.Parse(Value(), CultureInfo.InvariantCulture);
            float Real() => float.Parse(Value(), CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case "--strip": _output = Value(); break;
                case "--load": _takePath = Value(); break;
                case "--scenario": _scenario = Value(); break;
                case "--rig": rig = Value(); break;
                case "--usebind": binding = Value(); break;
                case "--frames": _frames = Number(); break;
                case "--stairs": _stairs = Number(); break;
                case "--columns": _columns = Number(); columnsSpecified = true; break;
                case "--size": _size = Number(); break;
                case "--facing": _facing = Number(); break;
                case "--start": _start = Real(); break;
                case "--end": _end = Real(); break;
                case "--world": _world = true; break;
                case "--overlay": _overlay = true; break;
                case "--trails": _trails = true; break;
                case "--contacts": _contacts = true; break;
                case "--path": _path = true; break;
                default:
                    if (args[i].StartsWith("--") || _clipName != null)
                        throw new ArgumentException("Unknown argument: " + args[i]);
                    _clipName = args[i]; break;
            }
        }
        if (string.IsNullOrWhiteSpace(_output) || Path.GetExtension(_output).ToLowerInvariant() != ".png")
            throw new ArgumentException("--strip requires an output .png path.");
        if (new[] { _clipName, _takePath, _scenario }.Count(s => s != null) != 1)
            throw new ArgumentException("Supply a clip name, --load <take.json>, OR --scenario stairs.");
        if (_scenario != null && !Obstacles.Contains(_scenario) && !_scenario.StartsWith("stab") && !_scenario.StartsWith("guard") && !_scenario.StartsWith("grab") && !_scenario.StartsWith("drop") && !_scenario.StartsWith("run-") && _scenario is not ("stairs" or "slash-combo" or "slash1" or "whip-kick" or "grab" or "grab-whiff" or "charge" or "charge-run" or "charge-air" or "jump" or "jump-run"))
            throw new ArgumentException("Unknown scenario: " + _scenario);
        if (!columnsSpecified) _columns = Math.Min(_columns, _frames);
        if (_frames < 2 || _frames > 64 || _columns < 1 || _columns > _frames || _size < 128 || _size > 1024
            || _columns * _size > 8192 || ((_frames + _columns - 1) / _columns) * _size > 8192)
            throw new ArgumentException("Use 2–64 frames, 1–frames columns, 128–1024 panel size, and dimensions <=8192.");
        if (_facing is not (-1 or 1) || !float.IsFinite(_start) || !float.IsFinite(_end)
            || _start < 0 || (_end != -1 && _end <= _start))
            throw new ArgumentException("Use facing -1 or 1 and a finite increasing start/end range.");
        if (_stairs < 1 || _stairs > 300)
            throw new ArgumentException("Use 1–300 stairs.");
        if (binding != null)
        {
            _bindingPath = File.Exists(binding) ? Path.GetFullPath(binding)
                : Path.Combine(RepoRoot(), "SpriteBindings", Path.ChangeExtension(binding, ".json"));
            var doc = SpriteBindingDocument.Load(_bindingPath)
                ?? throw new FileNotFoundException("Binding not found", _bindingPath);
            rig ??= doc.Skeleton;
            if (rig != doc.Skeleton) throw new ArgumentException("--rig must match the sprite binding's Skeleton.");
        }
        _rigName = rig ?? (_takePath != null ? SkeletonExamples.BipedName : SkeletonExamples.RabbitName);
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 320, PreferredBackBufferHeight = 240 };
        Content.RootDirectory = "Content";
        Window.Title = "MTile sprite-strip export";
        IsFixedTimeStep = false;
    }

    // The exporter needs a GL context, so SDL makes a window — but nobody needs to see
    // it. QuietWindow (call BEFORE constructing the game) tells SDL on macOS to run as a
    // background app: no dock icon, no focus steal. HideWindow then takes the window off
    // screen on the first frame. Both are best-effort: if SDL can't be reached the export
    // still works, with the window visible as before.
    private static IntPtr _sdl;
    private static IntPtr Sdl()
    {
        if (_sdl != IntPtr.Zero) return _sdl;
        string dir = AppContext.BaseDirectory;
        foreach (var name in new[]
        {
            Path.Combine(dir, "runtimes", "osx", "native", "libSDL2-2.0.0.dylib"), Path.Combine(dir, "libSDL2-2.0.0.dylib"),
            Path.Combine(dir, "runtimes", "linux-x64", "native", "libSDL2-2.0.so.0"), Path.Combine(dir, "runtimes", "win-x64", "native", "SDL2.dll"),
            "SDL2",
        })
            if (System.Runtime.InteropServices.NativeLibrary.TryLoad(name, out _sdl)) return _sdl;
        return IntPtr.Zero;
    }
    public static unsafe void QuietWindow()
    {
        try
        {
            if (Sdl() == IntPtr.Zero || !System.Runtime.InteropServices.NativeLibrary.TryGetExport(_sdl, "SDL_SetHint", out var fn)) return;
            var name = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("SDL_MAC_BACKGROUND_APP");
            var value = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("1");
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)fn)(name, value);
        }
        catch { }
    }
    private unsafe void HideWindow()
    {
        try
        {
            if (Sdl() != IntPtr.Zero && System.Runtime.InteropServices.NativeLibrary.TryGetExport(_sdl, "SDL_HideWindow", out var fn))
                ((delegate* unmanaged[Cdecl]<IntPtr, void>)fn)(Window.Handle);
        }
        catch { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MTile.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot locate MTile.sln above exporter.");
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _attachments = new SpriteAttachmentRenderer(GraphicsDevice, Path.Combine(RepoRoot(), "Assets", "AnimationEffects"));
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _draw = new DrawContext(_batch, _pixel);
        _font = Content.Load<SpriteFont>("DebugFont");
        var rig = SkeletonExamples.Load(_rigName);
        var clips = AnimationStore.LoadAll(Path.Combine(RepoRoot(), "SkeletonStates", _rigName));
        if (_scenario is "slash-combo" or "slash1" or "whip-kick") CaptureSlashCombo(rig, clips);
        else if (_scenario != null && (_scenario.StartsWith("stab") || _scenario.StartsWith("guard") || _scenario.StartsWith("grab") || _scenario.StartsWith("drop") || _scenario.StartsWith("run-") || _scenario is "grab" or "grab-whiff" or "charge" or "charge-run" or "charge-air" or "jump" or "jump-run")) CaptureGrab(rig, clips, !_scenario.StartsWith("grab") || _scenario == "grab-whiff");
        else if (_scenario != null) CaptureStairs(rig, clips);
        else if (_takePath == null) SampleClip(rig, clips);
        else ReplayTake(rig, clips);
        if (_bindingPath != null)
        {
            _skin = SpriteSkin.TryLoad(GraphicsDevice, _bindingPath, rig)
                ?? throw new InvalidDataException("Could not load sprite skin: " + _bindingPath);
            _skinPose = rig.CreatePose();
        }
        // One camera extent/zoom for the entire strip: per-panel auto-fit hides body bob.
        _min = new Vector2(float.MaxValue); _max = new Vector2(float.MinValue);
        foreach (var frame in _panels)
        {
            _pose.LoadLocal(frame.Pose);
            var root = Affine2.FromTRS(frame.Root - frame.Camera, 0,
                new Vector2(frame.Facing * frame.Scale, frame.Scale));
            foreach (var bone in _pose.ComputeWorld(root))
            {
                _min = Vector2.Min(_min, bone.Translation);
                _max = Vector2.Max(_max, bone.Translation);
            }
            if (frame.Feet != null)
                foreach (var foot in frame.Feet)
                    if (foot.Plan.HasSupport || foot.Plan.Reject == StepReject.Unreachable)
                    {
                        var target = foot.Plan.Target - frame.Camera;
                        _min = Vector2.Min(_min, target); _max = Vector2.Max(_max, target);
                        if (foot.Plan.HasSupport && foot.Plan.State == FootPlanState.Swing)
                        {
                            var landing = foot.Plan.Landing - frame.Camera;
                            _min = Vector2.Min(_min, landing); _max = Vector2.Max(_max, landing);
                        }
                    }
            if (frame.Debug != null)
                foreach (var d in frame.Debug)
                {
                    var (lo, hi) = d.Kind switch
                    {
                        TelegraphKind.Box  => (d.A, d.A + d.B),
                        TelegraphKind.Line => (Vector2.Min(d.A, d.B), Vector2.Max(d.A, d.B)),
                        TelegraphKind.Ring => (d.A - new Vector2(d.B.X), d.A + new Vector2(d.B.X)),
                        _ => (d.A, d.A),
                    };
                    // Framing only: a big carry box must not shrink the rig to a speck.
                    lo = Vector2.Max(lo - frame.Camera, new Vector2(-40)); hi = Vector2.Min(hi - frame.Camera, new Vector2(40));
                    _min = Vector2.Min(_min, lo); _max = Vector2.Max(_max, hi);
                }
            if (frame.Path != null)
                foreach (var p in frame.Path)
                {
                    _min = Vector2.Min(_min, p - frame.Camera); _max = Vector2.Max(_max, p - frame.Camera);
                }
            if (frame.Bodies != null)
                foreach (var b in frame.Bodies)
                {
                    var r = new Vector2(PlayerCharacter.Radius);
                    if (Vector2.Distance(b, frame.Camera) > 50f) continue;   // a thrown body flies off-frame
                    _min = Vector2.Min(_min, b - r - frame.Camera); _max = Vector2.Max(_max, b + r - frame.Camera);
                }
            if (_skin != null)
            {
                SyncSkin();
                _skin.Draw(Matrix.Identity, _skinPose, root, fill: false);
                _skin.GetDrawBounds(out var skinMin, out var skinMax);
                _min = Vector2.Min(_min, skinMin);
                _max = Vector2.Max(_max, skinMax);
            }
        }
        float margin = _panels.Max(f => f.Scale) * (_take == null ? 6 : 18);
        _min -= new Vector2(margin); _max += new Vector2(margin);
    }

    private void SampleClip(Skeleton rig, List<AnimationDocument> clips)
    {
        var clip = clips.FirstOrDefault(c => string.Equals(c.Name, _clipName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unknown clip: " + _clipName);
        float end = _end < 0 ? 1 : _end;
        if (_start >= 1 || end > 1) throw new ArgumentException("Clip start/end are normalized phases in [0,1].");
        _pose = SkeletonComposition.Compose(rig, clip.ExtraBones).CreatePose();
        var scratch = Enumerable.Range(0, 4).Select(_ => _pose.Skeleton.CreatePose()).ToArray();
        for (int i = 0; i < _frames; i++)
        {
            // Full loops exclude duplicate phase 1; one-shots and explicit ranges include the endpoint.
            float t = MathHelper.Lerp(_start, end, i / (float)(clip.Loop && _end < 0 ? _frames : _frames - 1));
            AnimationSampler.SampleSmooth(clip, t, scratch[0], scratch[1], scratch[2], scratch[3], _pose);
            _panels.Add(new Frame(_pose.CloneLocal(), Vector2.Zero, Vector2.Zero, 1, _facing,
                $"{clip.Name}  t={t:0.000}", Clip: clip, Tau: t));
        }
    }

    private void ReplayTake(Skeleton rig, List<AnimationDocument> clips)
    {
        _take = AnimTake.Load(_takePath);
        if (_take.Frames.Count < 2 || !float.IsFinite(_take.SkeletonScale) || _take.SkeletonScale <= 0)
            throw new InvalidDataException("Take needs at least two frames and a positive SkeletonScale.");
        int first = (int)_start, last = _end < 0 ? _take.Frames.Count - 1 : (int)_end;
        if (first != _start || (_end >= 0 && last != _end) || first >= last || last >= _take.Frames.Count)
            throw new ArgumentException("Take start/end must be valid inclusive integer frame indices.");
        var indices = Enumerable.Range(0, _frames)
            .Select(i => first + (int)Math.Round(i * (last - first) / (double)(_frames - 1))).ToArray();
        AnimSolverConfig.Load(Path.Combine(RepoRoot(), "configs", "anim_solver_config.json"));
        var animator = new CharacterAnimator(rig, _take.SkeletonScale, clips);
        _pose = animator.Skeleton.CreatePose();
        int panel = 0;
        double seconds = 0;
        // Always replay from frame zero, even for a late export range: contact/cadence memory matters.
        for (int i = 0; i <= last; i++)
        {
            var sample = _take.Frames[i].ToSample();
            animator.Update(sample);
            seconds += sample.Dt;
            while (panel < indices.Length && indices[panel] == i)
            {
                _panels.Add(new Frame(animator.Pose.CloneLocal(),
                    AttackGlowSystem.RigRoot(sample.Position, animator.DisplayFacing, animator, _take.SkeletonScale),
                    _world ? Vector2.Zero : sample.Position, _take.SkeletonScale,
                    animator.DisplayFacing,
                    $"f{i} {seconds:0.00}s {animator.State.Clip}", _take.Frames[i].Terrain));
                panel++;
            }
        }
    }

    // Animator rehearsal at 60 Hz: actual action progress clocks, immediate combo
    // transitions and recovery. No combat simulation or hitstop is synthesized.
    private void CaptureSlashCombo(Skeleton rig, List<AnimationDocument> clips)
    {
        const float dt = 1f / 60;
        AnimSolverConfig.Load(Path.Combine(RepoRoot(), "configs", "anim_solver_config.json"));
        var anim = new CharacterAnimator(rig, Game1.SkeletonScale, clips);
        _pose = anim.Skeleton.CreatePose();
        var sequence = new List<Frame>();
        var effects = new List<AttachmentSample>();
        void Tick(string action, float progress, int recovery, bool capture)
        {
            // whip-kick: airborne, and the sim has ALREADY turned (AirWhipKick.Enter flips
            // Facing at once) — the animator holds the old facing until the clip's FlipAt.
            bool kick = _scenario == "whip-kick";
            int simFacing = kick && action != "ReadyAction" ? -_facing : _facing;
            anim.Update(new CharacterAnimSample(Vector2.Zero, Vector2.Zero, simFacing, !kick,
                kick ? "FallingState" : "StandingState", action, dt, actionProgress: progress, recoveryFramesLeft: recovery));
            if (!capture) return;
            anim.SampleAttachments(effects);
            string[] front = new string[anim.FrontBones.Count];
            for (int fi = 0; fi < front.Length; fi++) front[fi] = anim.FrontBones[fi];
            sequence.Add(new Frame(anim.Pose.CloneLocal(),
                AttackGlowSystem.RigRoot(Vector2.Zero, anim.DisplayFacing, anim, Game1.SkeletonScale),
                Vector2.Zero, Game1.SkeletonScale, anim.DisplayFacing,
                $"f{sequence.Count} {action} {progress:0.00}", Effects: effects.ToArray(), Seconds: sequence.Count * dt,
                Front: front, Turn: anim.TurnScale));
        }
        for (int i = 0; i < 45; i++) Tick("ReadyAction", -1, 0, false);
        ActionState[] actions = _scenario == "whip-kick" ? new ActionState[] { new AirWhipKick() }
            : _scenario == "slash1"
            ? new ActionState[] { new GroundSlash1() }
            : new ActionState[] { new GroundSlash1(), new GroundSlash2(), new GroundSlash3() };
        foreach (var action in actions)
            for (int f = 0; ; f++)
            {
                var vars = new ActionVars { TimeInState = f * dt };
                float progress = action.AnimationProgress(in vars);
                if (progress >= 1) break;
                Tick(action.GetType().Name, progress, 0, true);
            }
        int recoveryFrames = _scenario == "slash1" ? 10 : 18;
        for (int f = recoveryFrames; f > 0; f--) Tick("RecoveryAction", -1, f, true);
        for (int f = 0; f < 6; f++) Tick("ReadyAction", -1, 0, true);
        for (int i = 0; i < _frames; i++)
            _panels.Add(sequence[(int)Math.Round(i * (sequence.Count - 1) / (double)(_frames - 1))]);
    }

    // Live grab at 60 Hz: flat ground, an AFK second player in reach (none for the whiff),
    // P1 presses Shift+LMB at open air in front, holds, swipes and releases. Same sample →
    // animator path as CosmeticUpdateSystem, so the panels show the runtime phase clips
    // (start → hold → throw, or start → whiff) — plus, per panel, the grab's
    // DebugQueryShapes (what DebugDrawHitboxes shows in game) and the second body's box.
    private void CaptureGrab(Skeleton rig, List<AnimationDocument> clips, bool whiff)
    {
        int first = (int)_start, last = _end < 0 ? 95 : (int)_end;
        if (first != _start || (_end >= 0 && last != _end) || first >= last || last > 3599)
            throw new ArgumentException("Scenario start/end are integer frames, 0 <= start < end <= 3599.");
        MovementConfig.Load(Path.Combine(RepoRoot(), "configs", "movement_config.json"));
        AnimSolverConfig.Load(Path.Combine(RepoRoot(), "configs", "anim_solver_config.json"));
        var terrain = new ChunkMap();
        const int ground = 12, width = 60;
        for (int x = 0; x < width; x++)
        for (int y = ground; y < ground + 4; y++)
        {
            var cp = new Point(x / Chunk.Size, y / Chunk.Size);
            if (!terrain.TryGet(cp, out var chunk)) terrain[cp] = chunk = new Chunk { ChunkPos = cp };
            chunk.Tiles[x % Chunk.Size, y % Chunk.Size].IsSolid = true;
        }
        // charge / charge-run / charge-air: the same stage, but a plain held LMB (the stab
        // wind-up) standing, running, or dropped from the air.
        // stab[-run|-air][-up|-down]: the same hold, released at frame 50 with a swipe — forward,
        // or 50° up / down — so the charge → stab hand-off and the aim re-target are both on film.
        bool stab = _scenario.StartsWith("stab");
        bool run = _scenario is "charge-run" or "jump-run" || _scenario.Contains("-run");
        var swipeDir = _scenario.EndsWith("-up") ? new Vector2(_facing * 0.64f, -0.77f)
                     : _scenario.EndsWith("-down") ? new Vector2(_facing * 0.64f, 0.77f) : new Vector2(_facing, 0f);
        // guard[-up|-down|-sweep]: Shift held from frame 10 with the cursor forward, 50° up / down,
        // or sweeping from straight down to straight up over frames 20-80 — the shield arc is drawn.
        bool guard = _scenario.StartsWith("guard");
        bool charge = _scenario.StartsWith("charge") || stab;
        // jump / jump-run: Space held from frame 10 (a full-height jump), standing or at a run.
        // drop / drop-run: no buttons at all — spawned 180 px up, so the touchdown is a heavy one.
        bool drop = _scenario.StartsWith("drop");
        // run-stop: run from frame 40, let go at 90. run-turn: run from 40, reverse at 90 for 50 frames.
        bool runScript = _scenario.StartsWith("run-");
        bool jump = _scenario.StartsWith("jump") || drop || runScript;
        var start = new Vector2(20 * Chunk.TileSize, ground * Chunk.TileSize - PlayerCharacter.Radius);
        if (_scenario == "charge-air" || _scenario.Contains("-air") || drop) start.Y -= drop ? 180f : 220f;   // 180: a heavy landing that still sticks (from ~200 px the float spring throws the body back up)
        var sim = new Simulation(terrain, start);
        PlayerCharacter victim = whiff ? null : sim.AddSecondaryPlayer(start + new Vector2(_facing * 28f, 0f)).Item1;
        var animator = new CharacterAnimator(rig, Game1.SkeletonScale, clips);
        _pose = animator.Skeleton.CreatePose();
        _take = new AnimTake { SkeletonScale = Game1.SkeletonScale, PlayerRadius = PlayerCharacter.Radius };
        var surfaces = new SolverSurface[SolveProblem.MaxSurfaces];
        var shapes = new TelegraphList(32);
        var indices = Enumerable.Range(0, _frames)
            .Select(i => first + (int)Math.Round(i * (last - first) / (double)(_frames - 1))).ToArray();
        // A jump presses late: the spawned body needs ~30 frames to settle at its standing height.
        int press = jump ? 40 : 10, release = jump ? 70 : guard ? 100 : stab ? 50 : charge ? 90 : whiff ? 40 : 60;
        int panel = 0;
        for (int f = 0; f <= last; f++)
        {
            var p = sim.Player.Body.Position;
            var mouse = p + new Vector2(_facing * 30f, -4f);
            // The last few held frames swipe the cursor up and forward: that swipe is the throw.
            // grab-up / grab-down carry the victim 50° up / down instead of straight ahead.
            if (_scenario.StartsWith("grab") && swipeDir.Y != 0f) mouse = p + swipeDir * 40f;
            if (guard)
            {
                float a = _scenario.EndsWith("-sweep") ? MathHelper.Lerp(1.45f, -1.45f, MathHelper.Clamp((f - 20) / 60f, 0f, 1f)) : 0f;
                var d = _scenario.EndsWith("-sweep") ? new Vector2(_facing * MathF.Cos(a), MathF.Sin(a)) : swipeDir;
                mouse = p + d * 40f;
            }
            if (stab && f >= release - 4) mouse += swipeDir * 12f * Math.Min(f - (release - 5), 5);   // held past the release: the parser reads the swipe on the release frame
            if (!charge && f >= release - 4 && f < release) mouse += new Vector2(_facing * 14f, -10f) * (f - (release - 5));
            var input = new PlayerInput { MouseWorldPosition = mouse,
                Shift = !charge && !jump && f >= press && f < release, LeftClick = !jump && !guard && f >= press && f < release,
                Space = jump && !drop && !runScript && f >= press && f < release,
                Right = (run && _facing == 1) || (runScript && (f >= 40 && f < 90 ? _facing == 1 : _scenario == "run-turn" && f >= 90 && f < 140 && _facing == -1)),
                Left = (run && _facing == -1) || (runScript && (f >= 40 && f < 90 ? _facing == -1 : _scenario == "run-turn" && f >= 90 && f < 140 && _facing == 1)) };
            if (victim != null) sim.Step(input, default); else sim.Step(input);
            int count = TerrainSurfaces.Extract(terrain, animator, sim.Player.Body.Position,
                animator.DisplayFacing, Game1.SkeletonScale, surfaces, out bool near);
            var sample = CharacterAnimSample.From(sim.Player, Simulation.FixedDt, surfaces, count, near, terrain);
            animator.Update(sample);
            while (panel < indices.Length && indices[panel] == f)
            {
                _take.AddFrame(sample, terrain.CaptureDense());
                shapes.Clear();
                sim.Player.CurrentAction.DebugQueryShapes(shapes, sim.Player.Body, sim.Player.CurrentActionVars);
                if (guard) sim.Player.CurrentAction.Telegraph(shapes, sim.Player.Body, sim.Player.CurrentActionVars);
                var dbg = new TelegraphShape[shapes.Count];
                for (int k = 0; k < shapes.Count; k++) dbg[k] = shapes[k];
                string act = sample.Action.Replace("Action", "");
                if (sample.ActionVariant != null) act += "." + sample.ActionVariant;
                if (sample.RecoveryFramesLeft > 0) act += $" r{sample.RecoveryFramesLeft}";
                _panels.Add(new Frame(animator.Pose.CloneLocal(),
                    AttackGlowSystem.RigRoot(sample.Position, animator.DisplayFacing, animator, Game1.SkeletonScale),
                    _world ? Vector2.Zero : sample.Position, Game1.SkeletonScale, animator.DisplayFacing,
                    $"f{f} {act}", _take.Frames[^1].Terrain,
                    Debug: dbg, Bodies: victim != null ? new[] { victim.Body.Position } : null));
                panel++;
            }
        }
    }

    private void CaptureStairs(Skeleton rig, List<AnimationDocument> clips)
    {
        int first = (int)_start, last = _end < 0 ? 149 : (int)_end;
        if (first != _start || (_end >= 0 && last != _end) || first >= last || last > 3599)
            throw new ArgumentException("Scenario start/end are integer frames, 0 <= start < end <= 3599.");
        MovementConfig.Load(Path.Combine(RepoRoot(), "configs", "movement_config.json"));
        AnimSolverConfig.Load(Path.Combine(RepoRoot(), "configs", "anim_solver_config.json"));
        var terrain = new ChunkMap();
        // Flat approach, configurable one-high/one-wide risers, then a long flat landing.
        // The obstacle scenarios (Obstacles / Column) replace the flight with one feature at column 12.
        // Build directly as level loading does; all subsequent motion uses Simulation.Step.
        int ground = _stairs + 5, width = _stairs + 70;
        bool obstacle = Obstacles.Contains(_scenario);
        void Fill(int x, int y)
        {
            int tx = _facing == 1 ? x : width - 1 - x;
            var cp = new Point(tx / Chunk.Size, y / Chunk.Size);
            if (!terrain.TryGet(cp, out var chunk)) terrain[cp] = chunk = new Chunk { ChunkPos = cp };
            chunk.Tiles[tx % Chunk.Size, y % Chunk.Size].IsSolid = true;
        }
        for (int x = 0; x < width; x++)
        {
            var (rise, clear) = obstacle ? Column(x) : (Math.Clamp(x - 7, 0, _stairs), 0);
            for (int y = ground - rise; y < ground + 5; y++) Fill(x, y);
            if (clear > 0)
                for (int y = ground - clear - 3; y < ground - clear; y++) Fill(x, y);
        }
        float startX = (_facing == 1 ? 1.5f : width - 1.5f) * Chunk.TileSize;
        int startRise = obstacle ? Column(1).Rise : 0;
        var sim = new Simulation(terrain, new Vector2(startX, (ground - startRise) * Chunk.TileSize - PlayerCharacter.Radius));
        var animator = new CharacterAnimator(rig, Game1.SkeletonScale, clips);
        _pose = animator.Skeleton.CreatePose();
        _take = new AnimTake { SkeletonScale = Game1.SkeletonScale, PlayerRadius = PlayerCharacter.Radius };
        var surfaces = new SolverSurface[SolveProblem.MaxSurfaces];
        var predictor = new LatticePathSampler();
        var indices = Enumerable.Range(0, _frames)
            .Select(i => first + (int)Math.Round(i * (last - first) / (double)(_frames - 1))).ToArray();
        int panel = 0;
        for (int f = 0; f <= last; f++)
        {
            sim.Step(new PlayerInput { Right = _facing == 1, Left = _facing == -1 });
            // Same ordering and APIs as CosmeticUpdateSystem: previous-pose surfaces,
            // live character sample, full solver, then the game's CoM-anchored rig root.
            int count = TerrainSurfaces.Extract(terrain, animator, sim.Player.Body.Position,
                animator.DisplayFacing, Game1.SkeletonScale, surfaces, out bool near);
            predictor.Bind(sim.Player);
            var sample = CharacterAnimSample.From(sim.Player, Simulation.FixedDt, surfaces, count, near, terrain, predictor.PredictAt);
            animator.Update(sample);
            while (panel < indices.Length && indices[panel] == f)
            {
                _take.AddFrame(sample, terrain.CaptureDense());
                var rigRoot = AttackGlowSystem.RigRoot(sample.Position, animator.DisplayFacing, animator, Game1.SkeletonScale);
                FootDebug[] feet = null;
                if (_contacts)
                {
                    var world = animator.Pose.ComputeWorld(Affine2.FromTRS(rigRoot, 0,
                        new Vector2(animator.DisplayFacing * Game1.SkeletonScale, Game1.SkeletonScale)));
                    feet = animator.Planner.Plans.Take(animator.Planner.FeetCount)
                        .Select(p => new FootDebug(animator.Skeleton.Bones[p.Bone].Name, p, world[p.Bone].Translation)).ToArray();
                }
                // --path: the walk planner's path from this tick (body-center positions,
                // seed → goal), as the corrector left it in its debug scratch.
                Vector2[] path = null;
                var scratch = sim.Player.CorrectorDebug;
                if (_path && scratch != null && scratch.LatticePathCount >= 2
                    && sim.Player.Frame - scratch.LatticePathFrame <= 1)
                    path = scratch.LatticePath.Take(scratch.LatticePathCount).Select(c => c.Pos).ToArray();
                _panels.Add(new Frame(animator.Pose.CloneLocal(),
                    rigRoot,
                    _world ? Vector2.Zero : sample.Position, Game1.SkeletonScale, animator.DisplayFacing,
                    $"f{f} p{animator.State.Phase:0.000} loop{animator.LoopBackJumps}", _take.Frames[^1].Terrain, Feet: feet, Path: path));
                string plants = string.Join(",", animator.Planner.Plans.Take(animator.Planner.FeetCount)
                    .Where(p => p.State == FootPlanState.Stance && p.HasSupport)
                    .Select(p => animator.Skeleton.Bones[p.Bone].Name));
                Console.WriteLine($"frame {f}: {sample.MovementState} / {animator.State.Clip}, position {sample.Position}, phase {animator.State.Phase:0.000}, loop {animator.LoopBackJumps}, plants [{plants}]");
                for (int fi = 0; fi < animator.Planner.FeetCount; fi++)
                {
                    var plan = animator.Planner.Plans[fi];
                    Console.WriteLine($"  {animator.Skeleton.Bones[plan.Bone].Name}: {plan.State}, reject={plan.Reject}, weight={plan.Weight:0.000}, target={plan.Target}, bodyDistance={Vector2.Distance(plan.Target, sample.Position):0.000}");
                }
                panel++;
            }
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_exported) return;
        _exported = true;
        HideWindow();
        int rows = (_frames + _columns - 1) / _columns;
        using var strip = new Texture2D(GraphicsDevice, _columns * _size, rows * _size);
        using var panel = new RenderTarget2D(GraphicsDevice, _size, _size);
        // Render each panel separately so sprites and terrain cannot bleed into its neighbors.
        var pixels = new Color[strip.Width * strip.Height];
        Array.Fill(pixels, new Color(22, 26, 34));
        var panelPixels = new Color[_size * _size];
        int footer = _contacts ? 58 : 0;
        float zoom = MathF.Min((_size - 24) / (_max.X - _min.X), (_size - 52 - footer) / (_max.Y - _min.Y));
        var center = (_min + _max) / 2;
        for (int i = 0; i < _panels.Count; i++)
        {
            var frame = _panels[i];
            GraphicsDevice.SetRenderTarget(panel);
            GraphicsDevice.Clear(new Color(22, 26, 34));
            var offset = new Vector2(_size / 2f, (_size + 28 - footer) / 2f) - center * zoom;
            Vector2 Screen(Vector2 p) => (p - frame.Camera) * zoom + offset;
            _batch.Begin(samplerState: SamplerState.PointClamp);
            if (_take != null && frame.Terrain >= 0 && frame.Terrain < _take.TerrainStates.Count)
                foreach (var tile in _take.TerrainStates[frame.Terrain])
                {
                    if (tile.S == (byte)TileState.Empty) continue;
                    var p = Screen(new Vector2(tile.X * Chunk.TileSize, tile.Y * Chunk.TileSize));
                    float size = Chunk.TileSize * zoom;
                    if (p.X + size < 0 || p.Y + size < 28 || p.X > _size || p.Y > _size) continue;
                    _draw.Box(p, new Vector2(MathF.Max(1, size - 1)), new Color(75, 82, 94));
                }
            if (frame.Bodies != null)
                foreach (var b in frame.Bodies)
                {
                    var tl = Screen(b - new Vector2(PlayerCharacter.Radius));
                    float side = 2 * PlayerCharacter.Radius * zoom;
                    _draw.Box(tl, new Vector2(side), Color.Cyan * 0.18f);
                    _draw.Ring(Screen(b), PlayerCharacter.Radius * zoom, Color.Cyan, segments: 20);
                }
            if (frame.Path != null)
            {
                var gold = new Color(255, 200, 60);
                for (int k = 1; k < frame.Path.Length; k++)
                    _draw.Line(Screen(frame.Path[k - 1]), Screen(frame.Path[k]), gold);
                foreach (var p in frame.Path)
                    _draw.Box(Screen(p) - new Vector2(1.5f), new Vector2(3f), gold);
                _draw.Ring(Screen(frame.Path[0]), 4f, Color.White, segments: 12);
            }
            if (frame.Debug != null)
                foreach (var d in frame.Debug)
                    switch (d.Kind)
                    {
                        case TelegraphKind.Box:  _draw.Box(Screen(d.A), d.B * zoom, d.Color); break;
                        case TelegraphKind.Line: _draw.Line(Screen(d.A), Screen(d.B), d.Color); break;
                        case TelegraphKind.Ring: _draw.Ring(Screen(d.A), d.B.X * zoom, d.Color, segments: d.Segments); break;
                    }
            _batch.End();
            _pose.LoadLocal(frame.Pose);
            var root = Affine2.FromTRS(Screen(frame.Root), 0, new Vector2(frame.Facing * frame.Scale * zoom, frame.Scale * zoom));
            if (_skin != null)
            {
                SyncSkin();
                var skinRoot = Affine2.FromTRS(Screen(frame.Root), 0,
                    new Vector2(frame.Facing * frame.Scale * zoom * frame.Turn, frame.Scale * zoom));
                _skin.Draw(Matrix.Identity, _skinPose, skinRoot, front: frame.Front);
            }
            _attachmentSamples.Clear();
            if (frame.Effects != null) _attachmentSamples.AddRange(frame.Effects);
            else AttachmentSampling.Append(frame.Clip, frame.Tau, 1, _attachmentSamples, _pose.Skeleton);
            float effectDt = i > 0 && frame.Clip != null
                ? (frame.Tau - _panels[i - 1].Tau) * frame.Clip.Duration
                : i > 0 ? frame.Seconds - _panels[i - 1].Seconds : 0;
            _attachments.Draw(Matrix.Identity, _pose, root, _attachmentSamples,
                _trails ? this : null, effectDt);
            _batch.Begin();
            if (_skin == null || _overlay)
            {
                // Side-coded joints (blue left / red right) so a strip shows which limb is which.
                var style = SkeletonDrawStyle.Default;
                style.LeftJointColor  = SkeletonDrawStyle.SideLeft;
                style.RightJointColor = SkeletonDrawStyle.SideRight;
                SkeletonRenderer.Draw(_draw, _pose, root, style);
            }
            if (frame.Feet != null)
            {
                _draw.Box(new Vector2(0, _size - footer), new Vector2(_size, footer), new Color(22, 26, 34));
                for (int fi = 0; fi < frame.Feet.Length; fi++)
                {
                    var foot = frame.Feet[fi]; var p = foot.Plan;
                    bool left = foot.Name.Contains("_l_");
                    var color = left ? Color.Cyan : Color.Magenta;
                    var actual = Screen(foot.Actual);
                    _draw.Ring(actual, 3, color);
                    bool hasTarget = p.HasSupport || p.Reject == StepReject.Unreachable;
                    if (hasTarget)
                    {
                        var target = Screen(p.Target);
                        _draw.Line(target - new Vector2(4, 4), target + new Vector2(4, 4), color);
                        _draw.Line(target + new Vector2(-4, 4), target + new Vector2(4, -4), color);
                        if (p.State == FootPlanState.Stance && p.HasSupport) _draw.Line(actual, target, color);
                        else _draw.Dashed(actual, target, color);
                        if (p.State == FootPlanState.Swing && p.HasSupport)
                            _draw.Ring(Screen(p.Landing), 6, color, segments: 4);
                    }
                    string error = hasTarget ? Vector2.Distance(foot.Actual, p.Target).ToString("0.0", CultureInfo.InvariantCulture) : "-";
                    string text = $"{(left ? "L" : "R")} {p.State} w{p.Weight:0.00} e{error} {p.Reject}";
                    float scale = MathF.Min(.75f, (_size - 8) / _font.MeasureString(text).X);
                    _batch.DrawString(_font, text, new Vector2(4, _size - footer + fi * 18), color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
                }
                const string legend = "O foot  X target  diamond landing";
                _batch.DrawString(_font, legend, new Vector2(4, _size - 18), Color.White, 0, Vector2.Zero,
                    MathF.Min(.65f, (_size - 8) / _font.MeasureString(legend).X), SpriteEffects.None, 0);
            }
            _draw.Box(Vector2.Zero, new Vector2(_size, 28), new Color(34, 42, 54));
            float textScale = MathF.Min(1, (_size - 12) / _font.MeasureString(frame.Label).X);
            _batch.DrawString(_font, frame.Label, new Vector2(6, 5), Color.White, 0, Vector2.Zero, textScale, SpriteEffects.None, 0);
            _draw.Box(new Vector2(_size - 1, 0), new Vector2(1, _size), Color.SlateGray);
            _draw.Box(new Vector2(0, _size - 1), new Vector2(_size, 1), Color.SlateGray);
            _batch.End();
            GraphicsDevice.SetRenderTarget(null);
            panel.GetData(panelPixels);
            for (int y = 0; y < _size; y++)
                Array.Copy(panelPixels, y * _size, pixels,
                    ((i / _columns * _size + y) * strip.Width) + i % _columns * _size, _size);
        }
        strip.SetData(pixels);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_output)));
        using (var file = File.Create(_output)) strip.SaveAsPng(file, strip.Width, strip.Height);
        string source = _scenario is "slash-combo" or "slash1" or "whip-kick" ? "animator rehearsal " + _scenario
            : _scenario != null ? "live " + _scenario : _take == null ? "raw clip" : "replayed take";
        Console.WriteLine($"Saved {Path.GetFullPath(_output)} ({strip.Width}x{strip.Height}, {_panels.Count} panels, {_rigName}, {source}).");
        Exit();
    }

    private void SyncSkin()
    {
        for (int b = 0; b < _skinPose.Count; b++)
            _skinPose.SetLocal(b, _pose.Local[_pose.Skeleton.IndexOf(_skinPose.Skeleton.Bones[b].Name)]);
    }

    protected override void UnloadContent()
    {
        _skin?.Dispose(); _batch?.Dispose(); _pixel?.Dispose();
        _attachments?.Dispose();
        base.UnloadContent();
    }
}
