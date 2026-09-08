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
    private readonly float _start = 0, _end = -1;
    private readonly bool _world, _overlay, _trails;
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
        AttachmentSample[] Effects = null, float Seconds = 0);

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
                case "--columns": _columns = Number(); columnsSpecified = true; break;
                case "--size": _size = Number(); break;
                case "--facing": _facing = Number(); break;
                case "--start": _start = Real(); break;
                case "--end": _end = Real(); break;
                case "--world": _world = true; break;
                case "--overlay": _overlay = true; break;
                case "--trails": _trails = true; break;
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
        if (_scenario != null && _scenario is not ("stairs" or "slash-combo" or "slash1"))
            throw new ArgumentException("Unknown scenario: " + _scenario);
        if (!columnsSpecified) _columns = Math.Min(_columns, _frames);
        if (_frames < 2 || _frames > 64 || _columns < 1 || _columns > _frames || _size < 128 || _size > 1024
            || _columns * _size > 8192 || ((_frames + _columns - 1) / _columns) * _size > 8192)
            throw new ArgumentException("Use 2–64 frames, 1–frames columns, 128–1024 panel size, and dimensions <=8192.");
        if (_facing is not (-1 or 1) || !float.IsFinite(_start) || !float.IsFinite(_end)
            || _start < 0 || (_end != -1 && _end <= _start))
            throw new ArgumentException("Use facing -1 or 1 and a finite increasing start/end range.");
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
        if (_scenario is "slash-combo" or "slash1") CaptureSlashCombo(rig, clips);
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
                    AttackGlowSystem.RigRoot(sample.Position, sample.Facing, animator, _take.SkeletonScale),
                    _world ? Vector2.Zero : sample.Position, _take.SkeletonScale,
                    sample.Facing == 0 ? 1 : sample.Facing,
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
            anim.Update(new CharacterAnimSample(Vector2.Zero, Vector2.Zero, _facing, true,
                "StandingState", action, dt, actionProgress: progress, recoveryFramesLeft: recovery));
            if (!capture) return;
            anim.SampleAttachments(effects);
            sequence.Add(new Frame(anim.Pose.CloneLocal(),
                AttackGlowSystem.RigRoot(Vector2.Zero, _facing, anim, Game1.SkeletonScale),
                Vector2.Zero, Game1.SkeletonScale, _facing,
                $"f{sequence.Count} {action} {progress:0.00}", Effects: effects.ToArray(), Seconds: sequence.Count * dt));
        }
        for (int i = 0; i < 45; i++) Tick("ReadyAction", -1, 0, false);
        ActionState[] actions = _scenario == "slash1"
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

    private void CaptureStairs(Skeleton rig, List<AnimationDocument> clips)
    {
        int first = (int)_start, last = _end < 0 ? 149 : (int)_end;
        if (first != _start || (_end >= 0 && last != _end) || first >= last || last > 3599)
            throw new ArgumentException("Scenario start/end are integer frames, 0 <= start < end <= 3599.");
        MovementConfig.Load(Path.Combine(RepoRoot(), "configs", "movement_config.json"));
        AnimSolverConfig.Load(Path.Combine(RepoRoot(), "configs", "anim_solver_config.json"));
        var terrain = new ChunkMap();
        // Flat approach, ten one-high/one-wide risers, then a long flat landing.
        // Build directly as level loading does; all subsequent motion uses Simulation.Step.
        for (int x = 0; x < 80; x++)
        for (int y = 15 - Math.Clamp(x - 7, 0, 10); y < 20; y++)
        {
            int tx = _facing == 1 ? x : 79 - x;
            var cp = new Point(tx / Chunk.Size, y / Chunk.Size);
            if (!terrain.TryGet(cp, out var chunk)) terrain[cp] = chunk = new Chunk { ChunkPos = cp };
            chunk.Tiles[tx % Chunk.Size, y % Chunk.Size].IsSolid = true;
        }
        float startX = (_facing == 1 ? 1.5f : 78.5f) * Chunk.TileSize;
        var sim = new Simulation(terrain, new Vector2(startX, 15 * Chunk.TileSize - PlayerCharacter.Radius));
        var animator = new CharacterAnimator(rig, Game1.SkeletonScale, clips);
        _pose = animator.Skeleton.CreatePose();
        _take = new AnimTake { SkeletonScale = Game1.SkeletonScale, PlayerRadius = PlayerCharacter.Radius };
        var surfaces = new SolverSurface[32];
        var indices = Enumerable.Range(0, _frames)
            .Select(i => first + (int)Math.Round(i * (last - first) / (double)(_frames - 1))).ToArray();
        int panel = 0;
        for (int f = 0; f <= last; f++)
        {
            sim.Step(new PlayerInput { Right = _facing == 1, Left = _facing == -1 });
            // Same ordering and APIs as CosmeticUpdateSystem: previous-pose surfaces,
            // live character sample, full solver, then the game's CoM-anchored rig root.
            int count = TerrainSurfaces.Extract(terrain, animator, sim.Player.Body.Position,
                sim.Player.Facing, Game1.SkeletonScale, surfaces, out bool near);
            var sample = CharacterAnimSample.From(sim.Player, Simulation.FixedDt, surfaces, count, near, terrain);
            animator.Update(sample);
            while (panel < indices.Length && indices[panel] == f)
            {
                _take.AddFrame(sample, terrain.CaptureDense());
                _panels.Add(new Frame(animator.Pose.CloneLocal(),
                    AttackGlowSystem.RigRoot(sample.Position, sample.Facing, animator, Game1.SkeletonScale),
                    _world ? Vector2.Zero : sample.Position, Game1.SkeletonScale, sample.Facing,
                    $"f{f} {(f + 1) * Simulation.FixedDt:0.00}s {animator.State.Clip}", _take.Frames[^1].Terrain));
                Console.WriteLine($"frame {f}: {sample.MovementState} / {animator.State.Clip}, position {sample.Position}, phase {animator.State.Phase:0.000}");
                panel++;
            }
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_exported) return;
        _exported = true;
        int rows = (_frames + _columns - 1) / _columns;
        using var strip = new Texture2D(GraphicsDevice, _columns * _size, rows * _size);
        using var panel = new RenderTarget2D(GraphicsDevice, _size, _size);
        // Render each panel separately so sprites and terrain cannot bleed into its neighbors.
        var pixels = new Color[strip.Width * strip.Height];
        Array.Fill(pixels, new Color(22, 26, 34));
        var panelPixels = new Color[_size * _size];
        float zoom = MathF.Min((_size - 24) / (_max.X - _min.X), (_size - 52) / (_max.Y - _min.Y));
        var center = (_min + _max) / 2;
        for (int i = 0; i < _panels.Count; i++)
        {
            var frame = _panels[i];
            GraphicsDevice.SetRenderTarget(panel);
            GraphicsDevice.Clear(new Color(22, 26, 34));
            var offset = new Vector2(_size / 2f, (_size + 28) / 2f) - center * zoom;
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
            _batch.End();
            _pose.LoadLocal(frame.Pose);
            var root = Affine2.FromTRS(Screen(frame.Root), 0, new Vector2(frame.Facing * frame.Scale * zoom, frame.Scale * zoom));
            if (_skin != null)
            {
                SyncSkin();
                _skin.Draw(Matrix.Identity, _skinPose, root);
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
            if (_skin == null || _overlay) SkeletonRenderer.Draw(_draw, _pose, root);
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
        string source = _scenario is "slash-combo" or "slash1" ? "animator rehearsal " + _scenario
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
