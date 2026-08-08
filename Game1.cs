using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTile;

// Render + input shell around the deterministic Simulation. Game1 gathers hardware
// input into a PlayerInput, hands it to Simulation.Step, then renders the resulting
// world and runs cosmetic-only systems (particles, cursor trail, camera, sprite
// animation) that must never feed back into the sim.
public class Game1 : Game
{
    private GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;
    private Texture2D _pixel;
    private SpriteFont _debugFont;

    private Simulation _sim;
    private GameConfig _config;

    // Stage-1 rollback bring-up: when the stage spawns a second player, a local bot
    // spoofs its input (stand-in for a future network peer — see GGPO_PLAN §G). Null
    // in solo play, in which case Step runs the primary-only path.
    private MTile.Net.BotInputSource _botInput;

    // Networked match (GGPO_PLAN stage 5). When _net is supplied, Game1 drives a
    // RollbackSession over the transport instead of stepping the sim directly: the
    // local player's input feeds the session, remote packets arrive via _net, and the
    // session predicts/rolls back to stay in sync. _localInput is the sample the
    // session pulls each step (set at the top of Update). Null ⇒ offline play.
    private readonly NetSetup _net;
    private MTile.Net.RollbackSession _session;
    private PlayerInput _localInput;

    public Game1(NetSetup net, string configPath = null) : this(configPath)
    {
        _net = net;
    }

    private readonly Camera _camera = new();
    private ParallaxBackground _background;

    private DrawContext _draw;
    private DebugOverlayRenderer _debugOverlay;
    // Render-side scratch for the DebugDrawCoast overlay's predictor rollout + rows.
    private readonly CoastSample[] _coastScratch = new CoastSample[BallisticPredictor.MaxHorizon];
    private readonly ClearanceRow[] _rowScratch  = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];
    private HudRenderer _hud;
    private PrimitiveBatch _prims;
    private DensityField _density;
    private SkeletonMetaballRenderer _metaballs;
    private GlowRenderer _glow;
    private DevDemoRenderer _devDemos;
    private ChunkRenderer _chunkRenderer;
    private GlowTrailField _glowField;
    private AttackGlowSystem _attackGlow;
    private readonly ParticleSystem _particles = new(capacity: 2048);
    // Cursor trail — a fading ribbon trailing the world-space mouse position.
    private readonly Trail _cursorTrail = new(capacity: 24, lifetime: 0.22f);
    private CosmeticUpdateSystem _cosmetics;

    private FileSystemWatcher _movementConfigWatcher;
    private FileSystemWatcher _animConfigWatcher;
    private FileSystemWatcher _refClipWatcher;

    // Procedural skeleton animation for the primary player. Render-only, pull-model
    // (reads sim state via CharacterAnimSample; never writes back). Scale fits the
    // ~62px-tall rig to the player's ~19px body.
    private CharacterAnimator _animator;
    // MLS-deformed artwork drawn over each player's skeleton, resolved per player via
    // GameConfig.PlayerSpriteBindings (fallback: PlayerSpriteBinding). Lazily loaded and
    // cached by binding name; a name whose binding fails to load caches null — the game
    // falls back to the stick figure for that player.
    private readonly Dictionary<string, SpriteSkin> _spriteSkins = new();
    // One animator per secondary player (training dummy, second local player, …),
    // same pull-model. Grown lazily in Update to match the sim's secondary count;
    // index i animates SecondaryPlayers[i].
    private readonly List<CharacterAnimator> _secondaryAnimators = new();
    // The authored clip set, kept so secondary animators built after LoadContent
    // bind the same animations as the primary's.
    private List<AnimationDocument> _skeletonAnims = new();
    // Rig→world scale. Public so offline tooling (MTile.Probe `addcom`) bakes COM
    // anchors against the same value the renderer places the rig with.
    public const float SkeletonScale = 0.6f;
    private float _simAccum;   // fixed-step accumulator for GameConfig.TimeScale (slow-mo)
    // Viewport center, recomputed once at the top of Update and reused in Draw — the
    // camera transform's pivot. Consistent within a frame.
    private Vector2 _screenCenter;

    // Screenshot capture (desktop dev tool) — F12 PNG grab + MTILE_SCREENSHOT headless
    // auto-capture, threaded through Initialize/Update/Draw. See ScreenshotSystem.
    private readonly ScreenshotSystem _screenshots = new();

    // In-game animation recorder (offline dev tool). Ctrl+R record, Ctrl+P scrub. Captures
    // the sim + the exact drawn pose per frame so a take can be reviewed frame-by-frame.
    private readonly GameRecorder _recorder = new();

    // Per-frame animator trace logging during manual play (offline dev tool). Ctrl+L
    // toggles; stop writes .probe/traces/manual_*.csv (TraceExportTests schema — the
    // anim_traces notebook plots it directly).
    private readonly AnimTraceLogger _animTrace = new();

    // Stage save/reload dev tool (desktop, offline only). Ctrl+M captures the player
    // position + nearby chunks into Levels/saved/ as a new stage (StageSaver); F5
    // rebuilds the sim from _activeStage for a pristine re-entry of the scenario.
    // _activeStage starts as the config stage and switches to each new save.
    private string _activeStage;
    private string _configPath;
    private KeyboardState _prevKeys;
    private string _toast;
    private float  _toastTtl;

    // Frame-time probe (GameConfig.DebugFrameTimings). Tracks the WORST cost of each
    // frame section over a 60-frame window (hitches hide in averages), shown for the
    // last completed window. Single stopwatch reused: Update and Draw run sequentially.
    private readonly System.Diagnostics.Stopwatch _probeSw = new();
    private double _probeSimMs, _probeCosMs, _probeDrawMs;
    private double _shownSimMs, _shownCosMs, _shownDrawMs;
    private bool _probeSlow, _shownSlow;
    private int _probeFrames;

    public Game1(string configPath = null)
    {
        // Load game config before the GraphicsDeviceManager finalizes so window
        // prefs take effect on the first frame. The path may be a CLI-selected
        // scenario config (e.g. Testing/freeze.json) — a repo-root CWD copy is
        // preferred so edit → F5 iterates; otherwise resolved title-relative via
        // TitleContent → TitleContainer (works on DesktopGL and the Blazor/WASM build).
        _configPath = configPath ?? "game_config.json";
        _config = GameConfig.Load(File.Exists(_configPath)
            ? Path.GetFullPath(_configPath) : _configPath);
        _camera.Zoom = _config.CameraZoom;

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = _config.WindowWidth,
            PreferredBackBufferHeight = _config.WindowHeight,
            IsFullScreen              = _config.Fullscreen,
        };
        if (_config.Fullscreen)
        {
            // Borderless fullscreen at the desktop resolution. The default (hardware
            // mode switch) tries to change the MONITOR to the backbuffer size; arbitrary
            // window sizes aren't real display modes, so the driver falls back to the
            // nearest legacy one (1024x768) — stretched, blurry, and it rearranges the
            // desktop on exit. Borderless keeps native pixels and alt-tabs instantly.
            var dm = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            _graphics.PreferredBackBufferWidth  = dm.Width;
            _graphics.PreferredBackBufferHeight = dm.Height;
            _graphics.HardwareModeSwitch        = false;
        }
        // The glow/metaball passes bind an offscreen RenderTarget mid-frame and then
        // rebind the backbuffer. RenderTargets default to DiscardContents, which would
        // wipe the already-drawn scene on rebind — preserve it so the scene survives.
        _graphics.PreparingDeviceSettings += (s, e) =>
            e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage =
                RenderTargetUsage.PreserveContents;
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(Simulation.FixedDt);
    }

    protected override void Initialize()
    {
        _screenshots.Initialize();

        // Stages captured in-game (Ctrl+M → Levels/saved/) register before the config
        // lookup so "Stage": "saved_NNN" works across restarts. Desktop only: the
        // registration walks the real filesystem.
        if (!OperatingSystem.IsBrowser())
            StageSaver.RegisterSavedStages(Path.GetFullPath(Path.Combine("Levels", "saved")));

        var stage = Stages.Get(_config.Stage);

        // Movement tuning. On desktop, point at the absolute repo-source path so
        // FileSystemWatcher can hot-reload tuning edits. On web (Blazor WASM), the
        // filesystem APIs throw PlatformNotSupported — load from a title-relative
        // path and skip the watcher.
        // NOTE (rollback): the hot-reload watcher mutates a sim-affecting static at
        // an arbitrary wall-clock moment. It's fine for solo tuning but must be
        // disabled for multiplayer (roadmap §3) so both peers share fixed config.
        if (OperatingSystem.IsBrowser())
        {
            MovementConfig.Load("movement_config.json");
            AnimSolverConfig.Load("anim_solver_config.json");
            ReferenceClipRegistry.LoadOverrides();
        }
        else
        {
            string movementCfgPath = Path.GetFullPath("movement_config.json");
            MovementConfig.Load(movementCfgPath);

            // Hot-reload is a desktop dev convenience only. It mutates a sim-affecting
            // static at an arbitrary moment, so it's gated off for multiplayer.
            if (_config.HotReloadMovementConfig)
            {
                _movementConfigWatcher = new FileSystemWatcher(Path.GetDirectoryName(movementCfgPath))
                {
                    Filter = Path.GetFileName(movementCfgPath),
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                _movementConfigWatcher.Changed += (s, e) =>
                {
                    System.Threading.Thread.Sleep(50);
                    MovementConfig.Load(movementCfgPath);
                };
            }

            // Reference clips (LedgePull/Dropdown): baked defaults overridden by
            // ReferenceClips/*.json. Prefer the repo-source copies (same CWD
            // convention as movement_config.json above) so editor saves are what
            // the game loads; fall back to the title-relative bin copies.
            string refClipDir = Path.GetFullPath("ReferenceClips");
            ReferenceClipRegistry.LoadOverrides(Directory.Exists(refClipDir) ? refClipDir : "ReferenceClips");
            if (_config.HotReloadMovementConfig && Directory.Exists(refClipDir))
            {
                _refClipWatcher = new FileSystemWatcher(refClipDir)
                {
                    Filter = "*.json",
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                _refClipWatcher.Changed += (s, e) =>
                {
                    System.Threading.Thread.Sleep(50);
                    ReferenceClipRegistry.LoadOverrides(refClipDir);
                };
            }

            // The animation solver weights (anim_solver_config.json). The solver is RENDER-ONLY
            // (never feeds the sim), so hot-reloading it is always safe — no multiplayer gate.
            string animCfgPath = Path.GetFullPath("anim_solver_config.json");
            AnimSolverConfig.Load(animCfgPath);
            _animConfigWatcher = new FileSystemWatcher(Path.GetDirectoryName(animCfgPath))
            {
                Filter = Path.GetFileName(animCfgPath),
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _animConfigWatcher.Changed += (s, e) =>
            {
                System.Threading.Thread.Sleep(50);
                AnimSolverConfig.Load(animCfgPath);
            };
        }

        // One-shot config loads for impact tuning. No hot-reload (unlike
        // movement_config.json) — these are sim-affecting per-body parameters
        // and the rollback peers would desync if one side picked up an edit
        // mid-match. Title-relative paths work on both DesktopGL (resolved
        // via TitleContainer) and Blazor WASM (HTTP fetch from wwwroot).
        ImpactProfiles.Load("impact_profiles.json");
        MaterialStrengths.Load("material_strengths.json");

        // A networked match always has two real players (local + remote), so force the
        // second player on regardless of config.
        if (_net != null) _config.SpawnSecondPlayer = true;

        LoadStage(stage);

        if (_net != null)
        {
            // Drive a RollbackSession over the transport. pollLocal returns the input
            // sampled at the top of Update; send encodes packets onto the wire.
            _session = new MTile.Net.RollbackSession(
                _sim, _net.LocalPlayerIndex,
                _ => _localInput,
                pkt => _net.Send(MTile.Net.InputCodec.Encode(in pkt)));
        }

        base.Initialize();
    }

    // Transient HUD line for the stage save/reload hotkeys (drawn in Draw, gold).
    private void Toast(string msg)
    {
        _toast = msg;
        _toastTtl = 4f;
        Console.WriteLine("[stage] " + msg);
    }

    // (Re)build the deterministic sim from a stage. Called at startup and by the F5
    // stage-reload hotkey (offline only — a RollbackSession holds a sim reference,
    // so reload/save hotkeys are gated off when _session exists).
    private void LoadStage(Stage stage)
    {
        _sim = new Simulation(_config, stage);
        _activeStage = stage.Name;
        _simAccum = 0f;

        // Offline only: if the stage spawned a second player, spoof its input with a bot.
        _botInput = _net == null && _sim.SecondaryPlayers.Count > 0
            ? new MTile.Net.BotInputSource(seed: 1234)
            : null;

        // Cosmetic feedback hooks. The sim raises these during Step; Game1 turns
        // them into particles. ChunkMap tints the tile-break burst by material.
        _sim.OnPlayerRespawn += pos => Effects.Puff(_particles, pos, Color.LimeGreen);
        _sim.Chunks.OnTileBroken += (pos, type) =>
            Effects.TileBreak(_particles, pos, TilePalette.BaseColor(type));

        if (_config.FreezeFrame) ApplyFreezeFrame();
    }

    // TEMP EXPERIMENT: single-frame corrector inspector (GameConfig.FreezeFrame,
    // set by scenario configs under Testing/).
    private readonly CoastSample[] _lmTrajectory = new CoastSample[BallisticPredictor.MaxHorizon];
    private readonly TrajectoryLm _lmProbe = new();
    private int _lmCount;
    // PROTOTYPE: lattice-search oracle (LatticePlanner), drawn orange next to
    // the LM probe's lime — freeze mode only.
    private readonly CoastSample[] _latticeTrajectory = new CoastSample[BallisticPredictor.MaxHorizon];
    private int _latticeCount;

    private void ApplyFreezeFrame()
    {
        var body = _sim.Player.Body;
        body.Position = new Vector2(
            _config.FreezeFramePosX ?? body.Position.X,
            _config.FreezeFramePosY ?? body.Position.Y);
        body.Velocity = new Vector2(_config.FreezeFrameVelX, _config.FreezeFrameVelY);

        // TEMP EXPERIMENT: nonlinear trajectory probe from the same pre-step
        // state the corrector captures use. Render-only; drawn lime. (With
        // FoldEngine "lm" the sim's own solved capture is the same solve —
        // this probe stays useful for A/B against the QP engine.)
        var mc = MovementConfig.Current;
        _lmCount = _lmProbe.Solve(
            _sim.Chunks, body.Polygon, body.Position, body.Velocity,
            _config.FreezeFrameInputX, mc.MaxWalkSpeed, trackProgress: true,
            mc.FoldHoverOffset, mc.FoldClimbReachUp,
            new Vector2(0f, Simulation.WorldGravityY), Simulation.FixedDt,
            mc.AmbientHorizon, iterations: 30, _lmTrajectory, out float lmCost);

        // PROTOTYPE: lattice-search oracle from the same pre-step state. A
        // longer horizon than the ambient reflex (24 ticks) so the discrete
        // route choices are visible in the overlay.
        _latticeCount = LatticePlanner.Solve(
            _sim.Chunks, body.Polygon, body.Position, body.Velocity,
            _config.FreezeFrameInputX, mc.MaxWalkSpeed, mc.FoldHoverOffset,
            new Vector2(0f, Simulation.WorldGravityY), Simulation.FixedDt,
            24, _latticeTrajectory, out float latticeCost);

        _sim.Player.CorrectorDebug.CaptureTrajectories = true;
        _sim.Step(new PlayerInput
        {
            Left  = _config.FreezeFrameInputX < 0,
            Right = _config.FreezeFrameInputX > 0,
            Down  = _config.FreezeFrameDown,
        });
        _config.TimeScale = 0f;
        var cd = _sim.Player.CorrectorDebug;
        Toast($"frozen: pos ({body.Position.X:F1},{body.Position.Y:F1}) " +
              $"vel ({body.Velocity.X:F1},{body.Velocity.Y:F1}) " +
              $"ref={cd.ReferenceCount} bal={cd.BallisticCount} sol={cd.SolvedCount} " +
              $"lmCost={lmCost:F1} - F5 reloads config");
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _debugFont = Content.Load<SpriteFont>("DebugFont");
        // Never let a HUD string crash the game: glyphs missing from the font atlas
        // render as '?' instead of throwing from DrawString mid-Draw.
        _debugFont.DefaultCharacter ??= '?';
        _draw = new DrawContext(_spriteBatch, _pixel);
        _debugOverlay = new DebugOverlayRenderer(_draw);
        _hud = new HudRenderer(_spriteBatch, _pixel, _debugFont, GraphicsDevice);
        _prims = new PrimitiveBatch(GraphicsDevice);
        // Half-res field (downscale 2) — cheaper and softer; 8-bit Color until banding
        // proves we need HalfVector4 (RENDERING_UPGRADE_PLAN spike #0).
        _density = new DensityField(GraphicsDevice, kernelSize: 128, downscale: 2);
        var splatFx     = Content.Load<Effect>("CapsuleSplat");
        var compositeFx = Content.Load<Effect>("MetaballComposite");
        _metaballs = new SkeletonMetaballRenderer(GraphicsDevice, splatFx, compositeFx, downscale: 2);
        _glow = new GlowRenderer(GraphicsDevice);
        _devDemos = new DevDemoRenderer(_prims, _density, _metaballs, _glow);
        _chunkRenderer = new ChunkRenderer(_spriteBatch, _pixel, _camera, GraphicsDevice);
        // Rock grain for tile fills — same raw-PNG loading rules as the backdrop below.
        try
        {
            string r1 = Path.Combine(AppContext.BaseDirectory, "Assets", "rock1.png");
            string r2 = Path.Combine(AppContext.BaseDirectory, "Assets", "rock2.png");
            if (File.Exists(r1) && File.Exists(r2))
            {
                using var fs1 = File.OpenRead(r1);
                using var fs2 = File.OpenRead(r2);
                _chunkRenderer.Atlas = TileTextureAtlas.Build(GraphicsDevice,
                    Texture2D.FromStream(GraphicsDevice, fs1),
                    Texture2D.FromStream(GraphicsDevice, fs2));
            }
        }
        catch (Exception) { /* cosmetic only — flat fills without it */ }
        _glowField = new GlowTrailField(GraphicsDevice, downscale: 2)
        {
            Lambda           = 6f,    // ~0.8s visible streak
            HaloWorld        = 6f,    // soft halo ≈ 6 world px
            SourceFill       = 0.95f,
            StampRadiusWorld = 5f,    // blade half-thickness
            StampSpacingWorld = 3f,
        };
        // Load authored skeleton animations (copied next to the binary). Clips live in
        // one dir per base rig — SkeletonStates/<rigName>/ — so the pool matches the
        // animator's skeleton (GameConfig.AnimationRig). There is no procedural
        // fallback: content is authored-only, so an empty pool is fatal. On WASM the
        // directory can't be enumerated, so the clip list comes from a build-generated
        // index.json manifest fetched over HTTP instead.
        var animRig = SkeletonExamples.Load(
            string.IsNullOrEmpty(_config.AnimationRig) ? SkeletonExamples.BipedName : _config.AnimationRig);
        if (OperatingSystem.IsBrowser())
        {
            string manifestDir = "SkeletonStates/" + animRig.Name;
            _skeletonAnims = AnimationStore.LoadAllFromManifest(manifestDir);
            if (_skeletonAnims.Count == 0)
                throw new InvalidOperationException(
                    $"No animation clips loaded for rig '{animRig.Name}' from wwwroot/{manifestDir}/index.json. " +
                    "That manifest is written by MTile.Web's GenerateClipManifests target from the repo-root " +
                    "SkeletonStates/ tree — rebuild MTile.Web to regenerate it.");
        }
        else
        {
            _skeletonAnims = AnimationStore.LoadAll(
                Path.Combine(AppContext.BaseDirectory, "SkeletonStates", animRig.Name));
        }
        _animator = new CharacterAnimator(animRig, SkeletonScale, _skeletonAnims);
        // Sprite skins load lazily per player in SkinForPlayer (secondary players may
        // not exist yet here).
        _spriteSkins.Clear();
        // Parallax backdrop — loaded straight from PNG (not the content pipeline) so it
        // works without an .mgcb rebuild; silently absent on hosts without the file.
        if (_config.DrawBackground)
        {
            var bgPath = Path.Combine(AppContext.BaseDirectory, "Assets", "mountain_background.png");
            try
            {
                if (File.Exists(bgPath))
                    using (var fs = File.OpenRead(bgPath))
                        _background = new ParallaxBackground(Texture2D.FromStream(GraphicsDevice, fs), _pixel);
            }
            catch (Exception) { _background = null; }
        }
        _attackGlow = new AttackGlowSystem(_animator, _glow, _glowField, SkeletonScale);
        _cosmetics = new CosmeticUpdateSystem(_animator, _secondaryAnimators, _skeletonAnims, SkeletonScale,
                                              _camera, _particles, _cursorTrail, _attackGlow);
    }

    // The player this client controls + the camera follows. Host (index 0) = primary;
    // joiner (index 1) = the secondary player. Offline = primary.
    private PlayerCharacter LocalPlayer =>
        _net != null && _net.LocalPlayerIndex == 1 && _sim.SecondaryPlayers.Count > 0
            ? _sim.SecondaryPlayers[0].Player
            : _sim.Player;

    protected override void Update(GameTime gameTime)
    {
        var keyboardState = Keyboard.GetState();

        if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed ||
            keyboardState.IsKeyDown(Keys.Escape))
            Exit();

        _screenshots.Update(keyboardState);

        // Stage save/reload (desktop dev tool, offline only — see LoadStage).
        // Ctrl+M: capture player pos + nearby chunks to Levels/saved/ as a new stage
        // and make it F5's target. F5: rebuild the sim from the active stage.
        if (_session == null && !OperatingSystem.IsBrowser())
        {
            bool ctrl = keyboardState.IsKeyDown(Keys.LeftControl) || keyboardState.IsKeyDown(Keys.RightControl);
            if (ctrl && keyboardState.IsKeyDown(Keys.M) && !_prevKeys.IsKeyDown(Keys.M))
            {
                try
                {
                    string dir = Path.GetFullPath(Path.Combine("Levels", "saved"));
                    string name = StageSaver.Save(_sim, _config.StageSaveChunkRadius, dir);
                    _activeStage = name;
                    Toast($"saved stage '{name}' - F5 re-enters it; persist via game_config \"Stage\": \"{name}\"");
                }
                catch (Exception ex) { Toast($"stage save FAILED: {ex.Message}"); }
            }
            if (keyboardState.IsKeyDown(Keys.F5) && !_prevKeys.IsKeyDown(Keys.F5))
            {
                // TEMP EXPERIMENT: re-read the active config's freeze fields
                // (+ TimeScale, which a freeze forces to 0; + Stage while
                // frozen) so edit → F5 iterates on a scenario.
                var fresh = GameConfig.Load(File.Exists(_configPath)
                    ? Path.GetFullPath(_configPath) : _configPath);
                _config.FreezeFrame       = fresh.FreezeFrame;
                _config.FreezeFramePosX   = fresh.FreezeFramePosX;
                _config.FreezeFramePosY   = fresh.FreezeFramePosY;
                _config.FreezeFrameVelX   = fresh.FreezeFrameVelX;
                _config.FreezeFrameVelY   = fresh.FreezeFrameVelY;
                _config.FreezeFrameInputX = fresh.FreezeFrameInputX;
                _config.FreezeFrameDown   = fresh.FreezeFrameDown;
                _config.TimeScale         = fresh.TimeScale;
                if (_config.FreezeFrame) _activeStage = fresh.Stage;
                else _lmCount = 0;
                LoadStage(Stages.Get(_activeStage));
                if (!_config.FreezeFrame) Toast($"reloaded stage '{_activeStage}'");
            }
        }
        _prevKeys = keyboardState;

        var mouseState = Mouse.GetState();
        var viewport = GraphicsDevice.Viewport;
        _screenCenter = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        var mouseWorldPos = _camera.ScreenToWorld(mouseState.Position.ToVector2(), _screenCenter);

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        if (_session != null)
        {
            // Networked: feed the local sample to the session, drain remote arrivals,
            // then advance one rollback step (which may predict, roll back, or stall).
            // The recorder is an offline-only dev tool and stays out of the netcode path.
            var input = Controller.Poll(mouseWorldPos);
            _localInput = input;
            while (_net.TryReceive(out var bytes))
                if (MTile.Net.InputCodec.TryDecode(bytes, out var pkt))
                    _session.Receive(in pkt);
            bool stepped = _session.TryStep();
            _cosmetics.Update(_sim, _config, dt, stepped ? Simulation.FixedDt : 0f,
                              mouseWorldPos, _screenCenter, LocalPlayer);
        }
        else
        {
            // Dev recorder: handle Ctrl+R / Ctrl+P and any playback scrubbing. Returns
            // false while in playback (sim frozen) — we skip the normal step + cosmetics.
            bool live = _recorder.HandleInput(keyboardState, _sim, _camera, _animator,
                                              _secondaryAnimators, SkeletonScale);
            if (live)
            {
                // Corrector trajectory capture follows the draw flags (write-only
                // diagnostics inside the sim; the buffers are read in Draw below).
                _sim.Player.CorrectorDebug.CaptureTrajectories =
                    _config.DebugDrawCorrectorReference
                    || _config.DebugDrawCorrectorBallistic
                    || _config.DebugDrawCorrectorSolved
                    || _config.DebugDrawCorrectorContacts;

                // Gather this frame's input and advance the simulation by fixed steps.
                var input = Controller.Poll(mouseWorldPos);
                // Slow-/fast-motion: accumulate TimeScale and run that many fixed steps
                // this frame — <1 skips frames (slow-mo), >1 runs extra, 0 pauses.
                _simAccum += MathF.Max(0f, _config.TimeScale);
                int stepsRun = 0;
                _probeSw.Restart();
                while (_simAccum >= 1f)
                {
                    _simAccum -= 1f;
                    // With a second player present offline, the bot spoofs its input (P2).
                    if (_botInput != null) _sim.Step(input, _botInput.Poll(_sim, _sim.Player.Frame));
                    else                   _sim.Step(input);
                    stepsRun++;
                }
                _probeSimMs = Math.Max(_probeSimMs, _probeSw.Elapsed.TotalMilliseconds);
                float simDt = stepsRun * Simulation.FixedDt;

                // Cosmetic-only pass: sprite sync, skeleton animators, knife trail,
                // particles, landing puff, camera tracking — reads sim state, never writes.
                // Animators tick on simDt (lockstep with physics, incl. slow-mo).
                _probeSw.Restart();
                _cosmetics.Update(_sim, _config, dt, simDt, mouseWorldPos, _screenCenter, LocalPlayer);
                _probeCosMs = Math.Max(_probeCosMs, _probeSw.Elapsed.TotalMilliseconds);

                // Record AFTER cosmetics so the captured pose + RigRoot reflect this frame.
                _recorder.CaptureFrame(_sim, _camera, _animator, _secondaryAnimators, SkeletonScale, dt);

                // Trace log AFTER cosmetics for the same reason (this frame's solved pose);
                // rows only on frames the animator ticked, timestamped in sim time.
                _animTrace.Capture(_animator, _sim.Player, simDt);
            }
            _animTrace.HandleInput(keyboardState);
        }
        _probeSlow |= gameTime.IsRunningSlowly;
        _toastTtl -= dt;

        base.Update(gameTime);
    }

    // Player index → binding name (PlayerSpriteBindings[i], else the fallback) → skin.
    private SpriteSkin SkinForPlayer(int index)
    {
        string name = _config.PlayerSpriteBindings != null && index < _config.PlayerSpriteBindings.Count
            ? _config.PlayerSpriteBindings[index] : null;
        return SkinByName(name) ?? SkinByName(_config.PlayerSpriteBinding);
    }

    // Cached by name; a failed load caches null (TryLoad logs once) → stick figure.
    private SpriteSkin SkinByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!_spriteSkins.TryGetValue(name, out var skin))
            _spriteSkins[name] = skin = SpriteSkin.TryLoad(GraphicsDevice,
                Path.Combine(AppContext.BaseDirectory, "SpriteBindings", name + ".json"),
                _animator.Skeleton);
        return skin;
    }

    protected override void Draw(GameTime gameTime)
    {
        _probeSw.Restart();
        // Capture this frame to an offscreen target (if a screenshot is pending), so the
        // save is immune to window focus/occlusion. Null when nothing is being captured.
        RenderTarget2D shotTarget = _screenshots.BeginCapture(GraphicsDevice);

        GraphicsDevice.Clear(Color.Black);

        _background?.Draw(_spriteBatch, _camera, _screenCenter);

        _spriteBatch.Begin(transformMatrix: _camera.GetTransform(_screenCenter));

        var player = _sim.Player;

        _chunkRenderer.Draw(_sim);

        // Entities before the player so the player overlays them when they overlap.
        foreach (var e in _sim.Entities)
        {
            if (e.Sprite != null) e.Sprite.Draw(_draw);
            else _debugOverlay.DrawPolygon(e.Body.Polygon, e.Body.Position, e.Color);
        }

        if (_config.DrawPlayerSprites)
        {
            if (player.Sprite != null) player.Sprite.Draw(_draw);
            foreach (var (p, _) in _sim.SecondaryPlayers)
                if (p.Sprite != null) p.Sprite.Draw(_draw);
        }
        // Sprite skins: each player's bound artwork deformed over its rig's live pose,
        // resolved per player (P1/P2 can wear different bindings). Drawn between the
        // placeholder sprites and the debug skeleton, outside the SpriteBatch pass
        // (each skin issues its own device draw) — split the batch around them.
        if (_config.DrawPlayerSpriteSkin)
        {
            var cam = _camera.GetTransform(_screenCenter);
            bool split = false;
            var skin0 = SkinForPlayer(0);
            if (skin0 != null)
            {
                _spriteBatch.End(); split = true;
                Vector2 root; int facing;
                if (_recorder.TryPlaybackPrimary(out var pc)) { root = pc.RootWorldPos; facing = pc.Facing; }
                else { root = AttackGlowSystem.RigRoot(player, _animator, SkeletonScale); facing = player.Facing; }
                int dir = facing == 0 ? 1 : facing;
                skin0.Draw(cam, _animator.Pose,
                    Affine2.FromTRS(root, 0f, new Vector2(dir * SkeletonScale, SkeletonScale)));
            }
            for (int i = 0; i < _sim.SecondaryPlayers.Count && i < _secondaryAnimators.Count; i++)
            {
                var skin = SkinForPlayer(i + 1);
                if (skin == null) continue;
                if (!split) { _spriteBatch.End(); split = true; }
                var p = _sim.SecondaryPlayers[i].Player;
                var anim = _secondaryAnimators[i];
                Vector2 root; int facing;
                if (_recorder.TryPlaybackSecondary(i, out var sc)) { root = sc.RootWorldPos; facing = sc.Facing; }
                else { root = AttackGlowSystem.RigRoot(p, anim, SkeletonScale); facing = p.Facing; }
                int dir = facing == 0 ? 1 : facing;
                skin.Draw(cam, anim.Pose,
                    Affine2.FromTRS(root, 0f, new Vector2(dir * SkeletonScale, SkeletonScale)));
            }
            if (split) _spriteBatch.Begin(transformMatrix: cam);
        }
        if (_config.DebugDrawSkeleton)
        {
            // In playback the rig is drawn from the recorder's captured pose (already
            // loaded into the animator) at the captured root — RigRoot reads animator
            // clip/δ state the snapshot doesn't restore, so the live recompute would drift.
            Vector2 primRoot; int primFacing;
            if (_recorder.TryPlaybackPrimary(out var pc)) { primRoot = pc.RootWorldPos; primFacing = pc.Facing; }
            else { primRoot = AttackGlowSystem.RigRoot(player, _animator, SkeletonScale); primFacing = player.Facing; }
            _animator.Draw(_draw, primRoot, primFacing,
                           _config.DebugDrawSkeletonJoints, _config.DebugHighlightPlantFoot);

            // Secondary players' rigs — same anchoring, each from its own animator.
            for (int i = 0; i < _sim.SecondaryPlayers.Count && i < _secondaryAnimators.Count; i++)
            {
                var p = _sim.SecondaryPlayers[i].Player;
                var anim = _secondaryAnimators[i];
                Vector2 root; int facing;
                if (_recorder.TryPlaybackSecondary(i, out var sc)) { root = sc.RootWorldPos; facing = sc.Facing; }
                else { root = AttackGlowSystem.RigRoot(p, anim, SkeletonScale); facing = p.Facing; }
                anim.Draw(_draw, root, facing,
                          _config.DebugDrawSkeletonJoints, _config.DebugHighlightPlantFoot);
            }
        }
        if (_config.DebugDrawBodies)
        {
            _debugOverlay.DrawPolygon(player.Body.Polygon, player.Body.Position,
                (player.IsGrounded ? Color.LimeGreen : Color.Orange) * 0.5f);
            foreach (var (p, _) in _sim.SecondaryPlayers)
                _debugOverlay.DrawPolygon(p.Body.Polygon, p.Body.Position,
                    (p.IsGrounded ? Color.LimeGreen : Color.Orange) * 0.5f);
            foreach (var e in _sim.Entities)
                _debugOverlay.DrawPolygon(e.Body.Polygon, e.Body.Position, Color.White * 0.3f);
        }

        // Particles over gameplay but under debug overlays.
        _particles.Draw(_draw);
        _cursorTrail.Draw(_spriteBatch, _pixel,
            new Color(255, 240, 180, 220), new Color(255, 100, 40, 0),
            3f);

        // Action overlay (slash arc, etc.) in world space, on top of the body.
        // Secondary players too — the training dummy's slash arcs / stab ribbons
        // are its attack telegraphs. Each PlayerCharacter owns its action-state
        // instances, so the per-action trails don't cross-contaminate.
        player.CurrentAction.Draw(_spriteBatch, _pixel, player.Body, player.CurrentActionVars);
        foreach (var (p, _) in _sim.SecondaryPlayers)
            p.CurrentAction.Draw(_spriteBatch, _pixel, p.Body, p.CurrentActionVars);

        // Enemy FSM telegraph/strike overlays — same world-space layer as the
        // player's action draw, so windup tells read alongside the player's
        // slash arcs.
        foreach (var e in _sim.Entities)
            if (e is EnemyEntity en) en.DrawOverlay(_spriteBatch, _pixel);

        if (_config.DebugDrawHitboxes)
            foreach (var hb in _sim.Hitboxes.All)
                _debugOverlay.DrawHitbox(hb);

        if (_config.DebugDrawForceFields)
            foreach (var f in _sim.ForceFields.All)
                _debugOverlay.DrawForceField(f);

        if (_config.DebugDrawHurtboxes)
            foreach (var hb in _sim.Hurtboxes.All)
                _debugOverlay.DrawHurtbox(hb);

        if (_config.DebugDrawPlayerOrientation)
        {
            var bodyPos = player.Body.Position;
            _debugOverlay.DrawConstraintArrow(bodyPos, new Vector2(player.Facing, 0f), Color.Magenta);
            _debugOverlay.DrawLine(bodyPos, _sim.CurrentInput.MouseWorldPosition, Color.Yellow * 0.6f, 1);
        }

        if (_config.DebugDrawConstraints)
            foreach (var body in _sim.Bodies)
                foreach (var c in body.Constraints)
                    if (c is SurfaceContact sc)
                        _debugOverlay.DrawConstraintArrow(sc.Position, sc.Normal,
                            c is FloatingSurfaceDistance ? Color.Cyan : Color.Yellow);

        // Corridor probe ahead of the primary player, in their facing direction. The scan is
        // pure and render-local — this call never feeds back into the sim.
        if (_config.DebugDrawCorridor)
            _debugOverlay.DrawCorridor(CorridorProbe.Scan(player.Body, _sim.Chunks, player.Facing));

        // C-space obstacle boundary near the player — what the row builder plans against.
        if (_config.DebugDrawCObstacles)
            _debugOverlay.DrawCObstacles(_sim.Chunks, player.Body.Polygon, player.Body.Position, 160f);

        // Predicted coast (BallisticPredictor) from the player's current state under the
        // currently-held input. Pure render-local rollout — identity modifiers (action
        // modifiers aren't reproduced here; the overlay approximates the baseline coast).
        if (_config.DebugDrawCoast)
        {
            var inp = _sim.CurrentInput;
            int dirX = (inp.Right ? 1 : 0) - (inp.Left ? 1 : 0);
            int n = BallisticPredictor.Predict(
                player.Body, _sim.Chunks, dirX, inp.Down, player.IsGrounded,
                MovementModifiers.Identity, player.Gravity,
                Simulation.FixedDt, 20, _coastScratch);
            _debugOverlay.DrawCoast(player.Body.Position, _coastScratch, n);

            // Clearance rows the coast implies (constraint builder, step 2) —
            // the violations the corrector will exist to resolve.
            int nRows = ClearanceConstraintBuilder.Build(
                _sim.Chunks, player.Body.Polygon, _coastScratch, n,
                margin: 2f, ClearanceConstraintBuilder.DefaultDeepViolation,
                _rowScratch, out _);
            _debugOverlay.DrawClearanceRows(_coastScratch, _rowScratch, nRows);
        }

        // Corrector maneuver trajectories, exactly as the active state computed
        // them this timestep (captured in CorrectorScratch): reference (gold,
        // frozen at entry), ballistic coast (aqua), solved/corrected (magenta).
        {
            var cd = player.CorrectorDebug;
            if (_config.DebugDrawCorrectorReference && cd.ReferenceCount > 0)
                _debugOverlay.DrawTrajectory(cd.ReferenceTrajectory, cd.ReferenceCount, Color.Gold);
            if (_config.DebugDrawCorrectorBallistic && cd.BallisticCount > 0)
                _debugOverlay.DrawTrajectory(cd.BallisticTrajectory, cd.BallisticCount, Color.Aqua);
            if (_config.DebugDrawCorrectorSolved && cd.SolvedCount > 0)
                _debugOverlay.DrawTrajectory(cd.SolvedTrajectory, cd.SolvedCount, Color.Magenta);
            if (_config.DebugDrawCorrectorContacts && cd.ContactCount > 0)
                _debugOverlay.DrawContactForces(cd.ContactPos, cd.ContactDv, cd.ContactCount);
            // TEMP EXPERIMENT: nonlinear trajectory probe (TrajectoryLm, freeze mode only).
            if (_lmCount > 0)
                _debugOverlay.DrawTrajectory(_lmTrajectory, _lmCount, Color.Lime);
            // PROTOTYPE: lattice-search oracle path (LatticePlanner).
            if (_latticeCount > 0)
                _debugOverlay.DrawTrajectory(_latticeTrajectory, _latticeCount, Color.Orange);
        }

        // Enemy health bars in world space, drawn just above each wounded body.
        foreach (var e in _sim.Entities)
            if (_config.DebugDrawHealthBars && e.Health < e.MaxHealth) _debugOverlay.DrawEntityHealthBar(e);

        _spriteBatch.End();

        // PrimitiveBatch layer (gradients / curves / surfaces) draws in world space on
        // top of the SpriteBatch pass. Demo card for now; real users (metaballs) land later.
        if (_config.DebugDrawPrimitiveDemo)
            _devDemos.DrawPrimitiveDemo(_camera.GetTransform(_screenCenter),
                              new Vector2(player.Body.Position.X, player.Body.Position.Y - 80f));

        if (_config.DebugDrawDensityDemo)
            _devDemos.DrawDensityDemo(_camera.GetTransform(_screenCenter),
                            new Vector2(player.Body.Position.X, player.Body.Position.Y - 80f));

        if (_config.DebugDrawMetaballDemo)
            _devDemos.DrawMetaballDemo(_camera.GetTransform(_screenCenter),
                             new Vector2(player.Body.Position.X, player.Body.Position.Y - 70f));

        // Glowing-shape pass (world space): the slash apex renders as a glowing triangle +
        // trail here, since the glow renderers need their own pass outside the SpriteBatch.
        var camTransform = _camera.GetTransform(_screenCenter);
        // Frozen during playback: the glow advances its own trail state from dt and reads
        // a live RigRoot, neither of which is valid while scrubbing a recorded take.
        if (!_recorder.IsPlayback)
            _attackGlow.Draw(camTransform, _sim, _config, (float)gameTime.ElapsedGameTime.TotalSeconds);

        if (_config.DebugDrawGlowDemo)
            _devDemos.DrawGlowDemo(camTransform,
                         new Vector2(player.Body.Position.X, player.Body.Position.Y - 70f));

        _hud.Draw(_sim, _animator, _config);
        _recorder.DrawHud(_spriteBatch, _debugFont);
        if (_animTrace.Active)
        {
            var tracePos = new Vector2(8, 76);   // below the recorder's HUD line
            _spriteBatch.Begin();
            _spriteBatch.DrawString(_debugFont, "[TRACE]  Ctrl+L stop+save",
                                    tracePos + new Vector2(1, 1), Color.Black);
            _spriteBatch.DrawString(_debugFont, "[TRACE]  Ctrl+L stop+save",
                                    tracePos, new Color(255, 90, 70));
            _spriteBatch.End();
        }

        if (_config.DebugFrameTimings)
        {
            _probeDrawMs = Math.Max(_probeDrawMs, _probeSw.Elapsed.TotalMilliseconds);
            if (++_probeFrames >= 60)
            {
                _shownSimMs = _probeSimMs; _shownCosMs = _probeCosMs; _shownDrawMs = _probeDrawMs;
                _shownSlow  = _probeSlow;
                _probeSimMs = _probeCosMs = _probeDrawMs = 0; _probeSlow = false; _probeFrames = 0;
            }
            string probe = $"worst/60f  sim {_shownSimMs:F2}ms  cosmetics {_shownCosMs:F2}ms  draw {_shownDrawMs:F2}ms" +
                           $"  particles {_particles.Count}  entities {_sim.Entities.Count}" +
                           (_shownSlow ? "  CATCH-UP" : "");
            var probePos = new Vector2(8, 96);
            _spriteBatch.Begin();
            _spriteBatch.DrawString(_debugFont, probe, probePos + new Vector2(1, 1), Color.Black);
            _spriteBatch.DrawString(_debugFont, probe, probePos, _shownSlow ? new Color(255, 90, 70) : Color.LimeGreen);
            _spriteBatch.End();
        }

        // Stage save/reload feedback line (Ctrl+M / F5), below the frame-time probe.
        if (_toastTtl > 0f && _toast != null)
        {
            var toastPos = new Vector2(8, 116);
            _spriteBatch.Begin();
            _spriteBatch.DrawString(_debugFont, _toast, toastPos + new Vector2(1, 1), Color.Black);
            _spriteBatch.DrawString(_debugFont, _toast, toastPos, Color.Gold);
            _spriteBatch.End();
        }

        if (shotTarget != null && _screenshots.EndCapture(shotTarget, _spriteBatch, GraphicsDevice))
            Exit();

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _animTrace.Stop();   // flush a trace left running at exit
        foreach (var skin in _spriteSkins.Values) skin?.Dispose();
        _spriteSkins.Clear();
        _pixel?.Dispose();
        _density?.Dispose();
        _metaballs?.Dispose();
        _movementConfigWatcher?.Dispose();
        base.UnloadContent();
    }
}
