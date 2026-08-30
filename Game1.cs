using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTile;

// Render + input shell around the deterministic Simulation. Game1 gathers hardware
// input into a PlayerInput, hands it to Simulation.Step, then renders the resulting
// world and runs cosmetic-only systems (particles, cursor trail, camera, sprite
// animation) that must never feed back into the sim.
public class Game1 : Game
{
    // Boot timing — one console line per startup stage. On the interpreted WASM
    // runtime the whole boot runs synchronously inside the first render tick and
    // the tab freezes until it finishes, so this breakdown is the only visibility
    // into where that time goes.
    private static readonly System.Diagnostics.Stopwatch _bootSw = System.Diagnostics.Stopwatch.StartNew();
    private static void BootMark(string label)
        => Console.WriteLine($"[boot] {label} @ {_bootSw.ElapsedMilliseconds} ms");

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
    private IBackdrop _background;

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
    // Edge-triggered sim events on their way to the render shell, deduped by
    // (sim frame, id) so a rollback replay doesn't re-present them. Particles and audio
    // are both consumers — see Plans/AUDIO_PLAN.md.
    private readonly PresentationEventLog _events = new();
    // Audio. Render-only, like the particle system beside it — see Plans/AUDIO_PLAN.md.
    // Level-triggered sounds are re-derived each frame from sim state; edge-triggered
    // ones arrive through _events, already deduped against rollback replay.
    private readonly GameAudio _audio = new();
    // Screen shake + directional/contact hit cosmetics (Plans/HIT_FEEL_PLAN.md phases
    // 4-6). Render-only, like _particles/_audio beside it — reads sim state each frame,
    // writes nothing back. Declared after _particles/_camera so this inline initializer
    // can reference them (C# runs field initializers in declaration order).
    private readonly HitFeelSystem _hitFeel;
    private readonly HitFlashSystem _hitFlash = new();
    private int _devToneSeq;
    // Cursor trail — a fading ribbon trailing the world-space mouse position.
    private readonly Trail _cursorTrail = new(capacity: 24, lifetime: 0.22f);
    // World-space overlay shapes declared by the players' actions and ITelegraphSource
    // entities this frame; cleared and refilled in Draw, rendered by TelegraphRenderer.
    private readonly TelegraphList _telegraphs = new();
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
    private float _simAccum;   // real time -> whole Simulation.Step calls (and TimeScale slow-mo)
    // Longest wall interval one frame may hand the sim, in fixed steps. Past this, time is
    // DROPPED rather than banked: a hitch (stage load, alt-tab, breakpoint) would otherwise
    // queue a backlog whose catch-up costs more than the hitch did. Dropping shows up as the
    // world skipping slightly; banking shows up as a freeze followed by a fast-forward.
    private const float MaxCatchUpSteps = 4f;
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

    // Live pause + frame-step (offline dev tool). F6/Pause freezes the sim; F7/'.' advances
    // exactly one Simulation.Step (Shift for a 10-frame burst, hold to repeat). Unlike the
    // recorder's Ctrl+P scrubbing this keeps the sim LIVE, so input is polled on each
    // stepped frame and you can walk a movement/animation transition forward one frame at
    // a time. Skipped in netplay — see FrameStepper.
    private readonly FrameStepper _stepper = new();

    // Stage save/reload dev tool (desktop, offline only). Ctrl+M captures the player
    // position + nearby chunks into Levels/saved/ as a new stage (StageSaver); F5
    // rebuilds the sim from _activeStage for a pristine re-entry of the scenario.
    // _activeStage starts as the config stage and switches to each new save.
    private string _activeStage;
    private string _configPath;
    private KeyboardState _prevKeys;
    private string _toast;
    private float  _toastTtl;

    // Frame-time probe (GameConfig.DebugFrameTimings): per-pass mean+worst over a
    // 60-frame window. This is the primary perf instrument for the WEB build, where no
    // external profiler exists — F11 dumps the last window to the console (devtools on
    // WASM). Slots are resolved once in Initialize; the per-frame path indexes ints.
    private readonly FrameProfiler _prof = new();
    private int _sSim, _sAnim, _sCosmetic, _sBackdrop, _sChunks, _sEntities, _sSkins,
                _sSkeleton, _sParticles, _sOverlays, _sGlow, _sHud, _sPresent;
    private bool _probeSlow, _shownSlow;
    private int  _probeFrames;

    public Game1(string configPath = null)
    {
        // Load game config before the GraphicsDeviceManager finalizes so window
        // prefs take effect on the first frame. The path may be a CLI-selected
        // scenario config (e.g. Testing/freeze.json) — a repo-root CWD copy under
        // configs/ is
        // preferred so edit → F5 iterates; otherwise resolved title-relative via
        // TitleContent → TitleContainer (works on DesktopGL and the Blazor/WASM build).
        _configPath = configPath ?? "configs/game_config.json";
        _config = GameConfig.Load(File.Exists(_configPath)
            ? Path.GetFullPath(_configPath) : _configPath);
        _camera.Zoom = _config.CameraZoom;
        _hitFeel = new HitFeelSystem(_particles, _camera);

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
        // Render frames and sim steps are DECOUPLED: vsync paces presentation, and the sim
        // advances in whole Simulation.FixedDt steps off a wall-clock accumulator (see
        // Update). Sim rate is a property of the game, not of the monitor — under
        // IsFixedTimeStep the two were the same knob, so display refresh and frame overruns
        // both leaked into how fast the world actually ran.
        //
        // This matters most for netplay: peers must agree on sim rate, and with the
        // accumulator they run 60 steps per wall second whatever their refresh rates are,
        // rather than agreeing only because both happened to be pinned to MonoGame's clock.
        // Rollback itself is untouched — RollbackSession is already sim-frame-native
        // (snapshot ring, input rings and checksums are all keyed by sim frame, and TryStep
        // advances exactly one). The only frame-coupled thing was calling it once per
        // Update, which is what the accumulator replaces.
        IsFixedTimeStep = false;
    }

    protected override void Initialize()
    {
        BootMark("Initialize start");
        _screenshots.Initialize();

        // Profiler slots, resolved once. Order here is the order they print in.
        _sSim       = _prof.Slot("sim");        // Simulation.Step (all fixed steps this frame)
        _sAnim      = _prof.Slot("anim");       // animation solver (inside cosmetics)
        _sCosmetic  = _prof.Slot("cosmetic");   // rest of the cosmetic pass
        _sBackdrop  = _prof.Slot("backdrop");
        _sChunks    = _prof.Slot("chunks");     // terrain + platforms + sprouts
        _sEntities  = _prof.Slot("entities");
        _sSkins     = _prof.Slot("skins");      // MLS sprite-skin deform + upload
        _sSkeleton  = _prof.Slot("skeleton");   // debug stick figure
        _sParticles = _prof.Slot("fx");         // particles + trails + action overlays
        _sOverlays  = _prof.Slot("debugdraw");  // the DebugDraw* world overlays
        _sGlow      = _prof.Slot("glow");       // GlowTrailField / GlowRenderer passes
        _sHud       = _prof.Slot("hud");
        _sPresent   = _prof.Slot("present");    // SpriteBatch.End flush of the world pass

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
            MovementConfig.Load("configs/movement_config.json");
            AnimSolverConfig.Load("configs/anim_solver_config.json");
            ReferenceClipRegistry.LoadOverrides();
        }
        else
        {
            string movementCfgPath = Path.GetFullPath("configs/movement_config.json");
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
            // convention as configs/movement_config.json above) so editor saves are what
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

            // The animation solver weights (configs/anim_solver_config.json). The solver is RENDER-ONLY
            // (never feeds the sim), so hot-reloading it is always safe — no multiplayer gate.
            string animCfgPath = Path.GetFullPath("configs/anim_solver_config.json");
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
        // configs/movement_config.json) — these are sim-affecting per-body parameters
        // and the rollback peers would desync if one side picked up an edit
        // mid-match. Title-relative paths work on both DesktopGL (resolved
        // via TitleContainer) and Blazor WASM (HTTP fetch from wwwroot).
        BootMark("configs loaded");
        ImpactProfiles.Load("configs/impact_profiles.json");
        MaterialStrengths.Load("configs/material_strengths.json");
        BootMark("impact/material configs loaded");

        // A networked match always has two real players (local + remote), so force the
        // second player on regardless of config.
        if (_net != null) _config.SpawnSecondPlayer = true;

        LoadStage(stage);
        BootMark("stage/sim built");

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

        // Cosmetic feedback hooks. The sim raises these during Step; they land in the
        // presentation log rather than spawning particles on the spot, because Step runs
        // again for every rolled-back frame and used to spray a duplicate burst each
        // time. The log drains once per rendered frame, below.
        _events.Reset();   // fresh sim ⇒ frame counter restarts ⇒ old keys are stale
        _audio.StopAll();  // and no voice should survive into a different world
        _sim.OnPlayerRespawn += pos =>
            _events.Emit(_sim.Frame, new PresentationId(PresentationKind.PlayerRespawn), pos);
        _sim.Chunks.OnTileBroken += (pos, type) =>
            _events.Emit(_sim.Frame,
                         new PresentationId(PresentationKind.TileBreak,
                                            (int)MathF.Floor(pos.X / Chunk.TileSize),
                                            (int)MathF.Floor(pos.Y / Chunk.TileSize)),
                         pos, (int)type);
        _sim.Chunks.OnTilePlaced += (pos, type) =>
            _events.Emit(_sim.Frame,
                         new PresentationId(PresentationKind.TilePlace,
                                            (int)MathF.Floor(pos.X / Chunk.TileSize),
                                            (int)MathF.Floor(pos.Y / Chunk.TileSize)),
                         pos, (int)type);
        // Entity ids are World-minted and restored with it, so (frame, id) is the same
        // key on a rollback replay — the dedupe holds.
        _sim.OnMassLanded += (id, pos, type, blocks) =>
            _events.Emit(_sim.Frame,
                         new PresentationId(PresentationKind.MassLand, id.Index, id.Generation),
                         pos, (int)type | (Math.Min(blocks, 255) << 8));

        if (_config.FreezeFrame) ApplyFreezeFrame();
    }

    // Turn this frame's fresh presentation events into cosmetics. The only consumer today
    // is the particle system; audio joins here (Plans/AUDIO_PLAN.md §8).
    private void PresentThisFrame(float dt)
    {
        _audio.BeginFrame();

        // Edge-triggered: each event is already unique per (sim frame, id).
        foreach (var e in _events.Pending)
        {
            switch (e.Id.Kind)
            {
                case PresentationKind.TileBreak:
                    Effects.TileBreak(_particles, e.Position, TilePalette.BaseColor((TileType)e.Payload));
                    // Debris decal (Plans/HIT_FEEL_PLAN.md phase 7) — same edge-triggered
                    // event, just a longer-lived burst so a break leaves something behind.
                    Effects.Decal(_particles, e.Position, TilePalette.BaseColor((TileType)e.Payload));
                    break;
                case PresentationKind.PlayerRespawn:
                    Effects.Puff(_particles, e.Position, Color.LimeGreen);
                    break;
                case PresentationKind.MassLand:
                    Effects.MassSplash(_particles, e.Position,
                        TilePalette.BaseColor((TileType)(e.Payload & 0xFF)), e.Payload >> 8);
                    break;
            }
            _audio.Present(in e);
        }
        _events.Clear();

        // Level-triggered: re-derived from final sim state, so a rollback needs nothing.
        _audio.CollectLevel(_sim);
        _audio.EndFrame(_camera.Position, dt);
        // Screen shake + hit cosmetics (Plans/HIT_FEEL_PLAN.md phases 4-6) — own
        // frame-stamp edge-detect, same reasoning as _audio.CollectLevel above.
        _hitFeel.Collect(_sim);
        // White flash on everything hit this frame — same stamp-edge reasoning, but it
        // persists and decays over the next few rendered frames, so it needs real dt.
        _hitFlash.Collect(_sim, dt);
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
    // PHASE 0/1 (Plans/LATTICE_PATH_PLANNER.md): the lattice PATH planner
    // oracle — drawn yellow (path) with red blocked-cell ticks, freeze only.
    private readonly LatticePathPlanner _latticePath = new();
    private readonly CoastSample[] _latticePathBuf = new CoastSample[256];
    private int _latticePathCount;

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

        // PHASE 0/1 lattice path planner oracle (Plans/LATTICE_PATH_PLANNER.md):
        // spatial DP from the same pre-step state. u = held direction; hover on
        // at the standing offset (the fold-state configuration).
        _latticePathCount = _config.FreezeFrameInputX == 0 ? 0 : _latticePath.Solve(
            _sim.Chunks, body.Polygon, body.Position, body.Velocity,
            new Vector2(Math.Sign(_config.FreezeFrameInputX), 0f),
            hover: true, mc.FoldHoverOffset, mc.FoldRiseCost,
            _latticePathBuf, out _, out _);

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
        BootMark("LoadContent start");
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _debugFont = Content.Load<SpriteFont>("DebugFont");
        BootMark("DebugFont loaded");
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
        // Optional, like the rock atlas below: the macOS content build ships without the
        // .fx shaders (mgfxc needs Wine — see Content.Mac.mgcb), and their only consumer
        // is the DebugDrawMetaballDemo preview. Missing .xnb → no metaball renderer.
        try
        {
            var splatFx     = Content.Load<Effect>("CapsuleSplat");
            var compositeFx = Content.Load<Effect>("MetaballComposite");
            _metaballs = new SkeletonMetaballRenderer(GraphicsDevice, splatFx, compositeFx, downscale: 2);
        }
        catch (ContentLoadException) { _metaballs = null; }
        BootMark("effects loaded");
        _glow = new GlowRenderer(GraphicsDevice);
        _devDemos = new DevDemoRenderer(_prims, _density, _metaballs, _glow);
        _chunkRenderer = new ChunkRenderer(_spriteBatch, _pixel, _camera, GraphicsDevice);
        // Rock grain for tile fills — same raw-PNG loading rules as the backdrop below.
        try
        {
            using var fs1 = OpenAsset("rock1.png");
            using var fs2 = OpenAsset("rock2.png");
            if (fs1 != null && fs2 != null)
            {
                _chunkRenderer.Atlas = TileTextureAtlas.Build(GraphicsDevice,
                    Texture2D.FromStream(GraphicsDevice, fs1),
                    Texture2D.FromStream(GraphicsDevice, fs2));
                // The thrown clod's disc, cut from the same grain per material.
                MassOrbTextures.Build(GraphicsDevice, _chunkRenderer.Atlas);
            }
        }
        catch (Exception) { /* cosmetic only — flat fills without it */ }
        BootMark("rock atlas built");
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
        BootMark("glow field ready");
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
        BootMark($"clips loaded ({_skeletonAnims.Count})");
        _animator = new CharacterAnimator(animRig, SkeletonScale, _skeletonAnims);
        BootMark("animator built");
        // Sprite skins load lazily per player in SkinForPlayer (secondary players may
        // not exist yet here).
        _spriteSkins.Clear();
        // Parallax backdrop — loaded straight from PNG (not the content pipeline) so it
        // works without an .mgcb rebuild; silently absent on hosts without the file.
        //
        // Browser gets the quarter-scale *_web variants: Texture2D.FromStream decodes
        // PNGs in managed code, ~5.6 s per megapixel on the interpreted WASM runtime —
        // the full 9-Mpx art was 51 s of frozen main thread (and then failed). The tree
        // layers are baked once into strips and drawn minified 10-40x, so the smaller
        // source doesn't read any different on screen.
        if (_config.DrawBackground)
        {
            bool web = OperatingSystem.IsBrowser();
            try
            {
                if (_config.BackgroundStyle == "trees")
                {
                    // Tree art variants, order matching Layer.Tree indices in
                    // TreeParallaxBackground (0 pine, 1 broadleaf, 2 snag, 3 conifer).
                    // The downsampled set (1024px tall) is preferred on every host;
                    // the pine falls back to the legacy full-res/web copies.
                    var treeNames = new[] { "tree.png", "redwood_tree.png", "deadwood_tree.png", "trees_all_layers.png" };
                    var treeTexs = new List<Texture2D>();
                    foreach (var name in treeNames)
                    {
                        var stream = OpenAsset("trees_small/" + name);
                        if (stream == null && name == "tree.png")
                            stream = OpenAsset(web ? "tree_web.png" : "tree.png");
                        if (stream == null) continue;
                        using (stream) treeTexs.Add(Texture2D.FromStream(GraphicsDevice, stream));
                    }
                    if (treeTexs.Count > 0)
                        _background = new TreeParallaxBackground(GraphicsDevice, treeTexs.ToArray(), _pixel);
                    BootMark("tree backdrop built");
                }
                if (_background == null)
                {
                    using var fs = OpenAsset(web ? "mountain_background_web.png" : "mountain_background.png");
                    if (fs != null)
                        _background = new ParallaxBackground(Texture2D.FromStream(GraphicsDevice, fs), _pixel);
                    BootMark("mountain backdrop built");
                }
            }
            catch (Exception) { _background = null; }
        }
        _attackGlow = new AttackGlowSystem(_animator, _glow, _glowField, SkeletonScale);
        _cosmetics = new CosmeticUpdateSystem(_animator, _secondaryAnimators, _skeletonAnims, SkeletonScale,
                                              _camera, _particles, _cursorTrail, _attackGlow)
        {
            Profiler = _prof, AnimSlot = _sAnim,
        };

        // Audio. The bank is optional: with no clips built every kind resolves to null
        // and the mixer no-ops, so the game runs exactly as it does today.
        _audio.Load(Content);
        Console.WriteLine("[boot] " + _audio.Summary);
        BootMark("LoadContent done");
    }

    // A raw PNG under Assets/ — beside the binary on desktop, fetched from wwwroot over
    // HTTP in the browser (relative + forward-slashed for TitleContainer). Null when the
    // file is absent; every caller treats missing art as cosmetic.
    private static Stream OpenAsset(string file)
        => TitleContent.TryOpenRead(OperatingSystem.IsBrowser()
            ? "Assets/" + file
            : Path.Combine(AppContext.BaseDirectory, "Assets", file));

    // The player this client controls + the camera follows. Host (index 0) = primary;
    // joiner (index 1) = the secondary player. Offline = primary.
    private PlayerCharacter LocalPlayer =>
        _net != null && _net.LocalPlayerIndex == 1 && _sim.SecondaryPlayers.Count > 0
            ? _sim.SecondaryPlayers[0].Player
            : _sim.Player;

    protected override void Update(GameTime gameTime)
    {
        _prof.BeginFrame();
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

        // Audio dev hotkeys: F9 plays the committed dev tone at the player (proves the
        // whole chain — pipeline, bank, mixer, backend — without needing real clips),
        // F10 mutes. Both offline-only, like the stage hotkeys above.
        if (keyboardState.IsKeyDown(Keys.F9) && !_prevKeys.IsKeyDown(Keys.F9))
        {
            // Unique id per press so the dedup table cannot swallow a repeat while paused.
            _audio.Mixer.Fire(new SoundId(SoundKind.DevTone, ++_devToneSeq), _sim.Frame,
                              gain: 1f, pitch: 0f, pos: _sim.Player.Body.Position);
            Toast(_audio.Summary);
        }
        // F11: dump the last complete profiler window, to BOTH the console and a file —
        // neither one alone is readable everywhere:
        //   web     — there is no filesystem, and Console.WriteLine lands in devtools.
        //   desktop — the console is swamped by PlayerCharacter's [move]/[action] tracing,
        //             which scrolls a dump away in well under a second, and the on-screen
        //             line runs off the edge of a small window.
        // APPENDS rather than overwrites: the workflow this exists for is an A/B (toggle a
        // setting, press F11 on each side, compare), which needs both blocks to survive.
        if (keyboardState.IsKeyDown(Keys.F11) && !_prevKeys.IsKeyDown(Keys.F11))
        {
            string dump = _prof.Dump();
            Console.WriteLine("[perf]\n" + dump);
            if (!OperatingSystem.IsBrowser())
            {
                try
                {
                    string path = Path.Combine(AppContext.BaseDirectory, "perf_log.txt");
                    // Stamp each block with what produced it. A dump with no record of the
                    // settings behind it can't be compared against anything measured later,
                    // which defeats the point of appending.
                    File.AppendAllText(path,
                        $"--- {DateTime.Now:HH:mm:ss}"
                        + $"  bg={(_config.DrawBackground ? _config.BackgroundStyle : "off")}"
                        + $"  {GraphicsDevice.Viewport.Width}x{GraphicsDevice.Viewport.Height}"
                        + $"  zoom={_config.CameraZoom}"
                        + $"  stage={_activeStage} ---\n{dump}\n");
                    Toast($"perf -> {path}");
                }
                catch (Exception ex) { Toast($"perf log FAILED: {ex.Message}"); }
            }
        }

        // F8: run the corrector-QP microbenchmark and print to the console. Same code, same
        // captured subproblem, on desktop and in the browser — so the wasm penalty on the
        // sim's hot solver is measured rather than extrapolated. It blocks for ~1.5 s by
        // design (batched under one clock pair; wasm clock resolution is too coarse to time
        // a single ~100 µs solve).
        if (keyboardState.IsKeyDown(Keys.F8) && !_prevKeys.IsKeyDown(Keys.F8))
        {
            Toast("running qp bench...");
            Console.WriteLine("[perf] " + QpBench.Run());
        }
        // ...and the same bench armed by ?qpbench=1 on the web host, which is how the
        // headless driver triggers it (Chrome keeps the function keys for itself).
        if (QpBench.PollStartup(out string qpReport)) Console.WriteLine("[perf] " + qpReport);
        if (keyboardState.IsKeyDown(Keys.F10) && !_prevKeys.IsKeyDown(Keys.F10))
        {
            _audio.Mixer.Muted = !_audio.Mixer.Muted;
            if (_audio.Mixer.Muted) _audio.StopAll();
            Toast(_audio.Mixer.Muted ? "audio muted" : "audio on");
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
            // Advance by real time rather than one step per rendered frame, so both peers
            // run 60 sim steps per wall second whatever their refresh rates are. TimeScale
            // is deliberately NOT applied here: slow-mo is an offline dev knob, and scaling
            // it on one side would desync the pair outright.
            _simAccum += MathF.Min(dt / Simulation.FixedDt, MaxCatchUpSteps);
            int netSteps = 0;
            long tNetSim = _prof.Begin();
            while (_simAccum >= 1f)
            {
                // A false return is the stall cap: we are further ahead of the peer than
                // InputFrameDelay + StallSlack, so no frame ran. Running slower than real
                // time is the CORRECT response there — it is how the peers resynchronise —
                // so drop the banked time instead of queueing it. Queued, the stall would
                // end in a burst of catch-up steps: a visible speed spike, and a way to
                // make the next rollback deeper than it needed to be.
                if (!_session.TryStep()) { _simAccum = 0f; break; }
                _simAccum -= 1f;
                netSteps++;
            }
            _prof.End(_sSim, tNetSim);   // includes any rollback resim this frame
            long tNetCos = _prof.Begin();
            _cosmetics.Update(_sim, _config, dt, netSteps * Simulation.FixedDt,
                              mouseWorldPos, _screenCenter, LocalPlayer);
            _prof.End(_sCosmetic, tNetCos);
        }
        else
        {
            // Dev recorder: handle Ctrl+R / Ctrl+P and any playback scrubbing. Returns
            // false while in playback (sim frozen) — we skip the normal step + cosmetics.
            bool live = _recorder.HandleInput(keyboardState, _sim, _camera, _animator,
                                              _secondaryAnimators, SkeletonScale);
            // Pause/step keys. Fed the REAL delta so hold-to-repeat keeps ticking while the
            // sim is frozen, and only while `live` — during playback the sim is already
            // frozen and the recorder owns the same arrow/space scrubbing keys.
            if (live)
            {
                _stepper.HandleInput(keyboardState, dt);

                // Corrector trajectory capture follows the draw flags (write-only
                // diagnostics inside the sim; the buffers are read in Draw below).
                _sim.Player.CorrectorDebug.CaptureTrajectories =
                    _config.DebugDrawCorrectorReference
                    || _config.DebugDrawCorrectorBallistic
                    || _config.DebugDrawCorrectorSolved
                    || _config.DebugDrawCorrectorContacts;

                // Gather this frame's input and advance the simulation by fixed steps.
                var input = Controller.Poll(mouseWorldPos);
                int stepsRun = 0;
                long tSim = _prof.Begin();
                if (_stepper.Paused)
                {
                    // Paused: the wall clock no longer drives the sim, only the step key
                    // does. Zero the accumulator rather than letting it bank — banked time
                    // would fast-forward the moment you resume, which is exactly the frame
                    // you stopped to look at. Input is still polled above, so holding a
                    // direction and tapping step advances one frame of that input.
                    _simAccum = 0f;
                    while (_stepper.TryConsumeStep())
                    {
                        if (_botInput != null) _sim.Step(input, _botInput.Poll(_sim, _sim.Player.Frame));
                        else                   _sim.Step(input);
                        stepsRun++;
                    }
                }
                else
                {
                    // Slow-/fast-motion: accumulate TimeScale and run that many fixed steps
                    // this frame — <1 skips frames (slow-mo), >1 runs extra, 0 pauses.
                    // Accumulate REAL TIME (in units of fixed steps) rather than one step per
                    // frame, so the sim advances at 60 Hz whatever the display refresh is.
                    // TimeScale still multiplies it, so slow-/fast-motion is unchanged.
                    _simAccum += MathF.Min(dt / Simulation.FixedDt, MaxCatchUpSteps)
                                 * MathF.Max(0f, _config.TimeScale);
                    while (_simAccum >= 1f)
                    {
                        _simAccum -= 1f;
                        // With a second player present offline, the bot spoofs its input (P2).
                        if (_botInput != null) _sim.Step(input, _botInput.Poll(_sim, _sim.Player.Frame));
                        else                   _sim.Step(input);
                        stepsRun++;
                    }
                }
                _prof.End(_sSim, tSim);
                float simDt = stepsRun * Simulation.FixedDt;

                // Cosmetic-only pass: sprite sync, skeleton animators, knife trail,
                // particles, landing puff, camera tracking — reads sim state, never writes.
                // Animators tick on simDt (lockstep with physics, incl. slow-mo).
                long tCos = _prof.Begin();
                _cosmetics.Update(_sim, _config, dt, simDt, mouseWorldPos, _screenCenter, LocalPlayer);
                _prof.End(_sCosmetic, tCos);

                // Record AFTER cosmetics so the captured pose + RigRoot reflect this frame.
                _recorder.CaptureFrame(_sim, _camera, _animator, _secondaryAnimators, SkeletonScale, dt);

                // Trace log AFTER cosmetics for the same reason (this frame's solved pose);
                // rows only on frames the animator ticked, timestamped in sim time.
                _animTrace.Capture(_animator, _sim.Player, simDt);
            }
            _animTrace.HandleInput(keyboardState);
        }

        // Drain the presentation log once per RENDERED frame — not once per Step, since a
        // rollback runs many Steps inside one frame. Everything here is already deduped.
        PresentThisFrame(dt);

        _probeSlow |= gameTime.IsRunningSlowly;
        _toastTtl -= dt;

        base.Update(gameTime);
    }

    // Player index → binding name (PlayerSpriteBindings[i], else the fallback) → skin.
    // Secondary players fall back to P1's binding last: a binding authored on another rig
    // than AnimationRig fails to load (SpriteSkin rejects the mismatch), and an unskinned
    // P2 reads as a bug rather than as a missing cosmetic.
    private SpriteSkin SkinForPlayer(int index)
    {
        string name = _config.PlayerSpriteBindings != null && index < _config.PlayerSpriteBindings.Count
            ? _config.PlayerSpriteBindings[index] : null;
        return SkinByName(name)
            ?? SkinByName(_config.PlayerSpriteBinding)
            ?? (index > 0 ? SkinForPlayer(0) : null);
    }

    // Cached by name; a failed load caches null (TryLoad logs once) → stick figure.
    // On WASM the binding (and the PNGs it names, resolved relative to it) is fetched from
    // wwwroot over HTTP, so the path has to stay relative and forward-slashed.
    private SpriteSkin SkinByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!_spriteSkins.TryGetValue(name, out var skin))
        {
            string path = OperatingSystem.IsBrowser()
                ? "SpriteBindings/" + name + ".json"
                : Path.Combine(AppContext.BaseDirectory, "SpriteBindings", name + ".json");
            _spriteSkins[name] = skin = SpriteSkin.TryLoad(GraphicsDevice, path, _animator.Skeleton);
        }
        return skin;
    }

    protected override void Draw(GameTime gameTime)
    {
        // Capture this frame to an offscreen target (if a screenshot is pending), so the
        // save is immune to window focus/occlusion. Null when nothing is being captured.
        RenderTarget2D shotTarget = _screenshots.BeginCapture(GraphicsDevice);

        GraphicsDevice.Clear(Color.Black);

        using (_prof.Measure(_sBackdrop))
            _background?.Draw(_spriteBatch, _camera, _screenCenter);

        _spriteBatch.Begin(transformMatrix: _camera.GetDrawTransform(_screenCenter));

        var player = _sim.Player;

        // NOTE: these Draw-side scopes measure the CPU cost of BUILDING the batch, not the
        // GPU work — SpriteBatch is deferred, so the actual submission lands in the
        // `present` scope at the closing End(). Read them as "managed cost per pass".
        using (_prof.Measure(_sChunks))
            _chunkRenderer.Draw(_sim);

        long tEnt = _prof.Begin();
        // Entities before the player so the player overlays them when they overlap.
        foreach (var e in _sim.Entities)
        {
            float flash = _hitFlash.Intensity(e.Id);
            if (e.Sprite != null) { e.Sprite.Flash = flash; e.Sprite.Draw(_draw); }
            else _debugOverlay.DrawPolygon(e.Body.Polygon, e.Body.Position,
                                           HitFlashTracker.Whiten(e.Color, flash));
        }
        _prof.End(_sEntities, tEnt);

        if (_config.DrawPlayerSprites)
        {
            if (player.Sprite != null)
            {
                player.Sprite.Flash = _hitFlash.Intensity(player.Id);
                player.Sprite.Draw(_draw);
            }
            foreach (var (p, _) in _sim.SecondaryPlayers)
                if (p.Sprite != null)
                {
                    p.Sprite.Flash = _hitFlash.Intensity(p.Id);
                    p.Sprite.Draw(_draw);
                }
        }
        // Sprite skins: each player's bound artwork deformed over its rig's live pose,
        // resolved per player (P1/P2 can wear different bindings). Drawn between the
        // placeholder sprites and the debug skeleton, outside the SpriteBatch pass
        // (each skin issues its own device draw) — split the batch around them.
        long tSkins = _prof.Begin();
        if (_config.DrawPlayerSpriteSkin)
        {
            var cam = _camera.GetDrawTransform(_screenCenter);
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
                    Affine2.FromTRS(root, 0f, new Vector2(dir * SkeletonScale, SkeletonScale)),
                    flash: _hitFlash.Intensity(player.Id));
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
                    Affine2.FromTRS(root, 0f, new Vector2(dir * SkeletonScale, SkeletonScale)),
                    flash: _hitFlash.Intensity(p.Id));
            }
            if (split) _spriteBatch.Begin(transformMatrix: cam);
        }
        _prof.End(_sSkins, tSkins);

        long tSkel = _prof.Begin();
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
        _prof.End(_sSkeleton, tSkel);

        long tFx = _prof.Begin();
        // Particles over gameplay but under debug overlays.
        _particles.Draw(_draw);
        _cursorTrail.Draw(_draw,
            new Color(255, 240, 180, 220), new Color(255, 100, 40, 0),
            3f);

        // Telegraph pass, in world space on top of the bodies. Sim objects DECLARE
        // shapes into one list — players' current actions (charge dots, pulse rings;
        // secondary players too, since the training dummy's tells are its attack
        // telegraphs), then ITelegraphSource entities (enemy wind-ups, the block-grab
        // tether tint) — and TelegraphRenderer draws them. Nothing sim-side touches
        // the SpriteBatch. Emission order is draw order: actions under entities.
        _telegraphs.Clear();
        player.CurrentAction.Telegraph(_telegraphs, player.Body, player.CurrentActionVars);
        foreach (var (p, _) in _sim.SecondaryPlayers)
            p.CurrentAction.Telegraph(_telegraphs, p.Body, p.CurrentActionVars);
        foreach (var e in _sim.Entities)
            if (e is ITelegraphSource ts) ts.Telegraph(_telegraphs);
        TelegraphRenderer.Draw(_draw, _telegraphs);
        _prof.End(_sParticles, tFx);

        long tDbg = _prof.Begin();
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
            // PHASE 0/1: lattice PATH planner (Plans/LATTICE_PATH_PLANNER.md) —
            // yellow path, red ticks on blocked lattice cells (the C-obstacle
            // bitmap: the phase-0 visual gate).
            if (_latticePathCount > 0)
            {
                _debugOverlay.DrawTrajectory(_latticePathBuf, _latticePathCount, Color.Yellow);
                float half = _latticePath.DebugCell * 0.5f;
                for (int cy = 0; cy < _latticePath.DebugHeight; cy++)
                for (int cx = 0; cx < _latticePath.DebugWidth; cx++)
                {
                    if (!_latticePath.DebugBlocked(cx, cy)) continue;
                    var c = _latticePath.DebugCellCenter(cx, cy);
                    _debugOverlay.DrawLine(c - new Vector2(half, half),
                                           c + new Vector2(half, half), Color.Red, 1);
                }
            }
        }

        // Enemy health bars in world space, drawn just above each wounded body.
        foreach (var e in _sim.Entities)
            if (_config.DebugDrawHealthBars && e.Health < e.MaxHealth) _debugOverlay.DrawEntityHealthBar(e);
        _prof.End(_sOverlays, tDbg);

        // The deferred world pass actually submits here — everything batched above pays
        // its GPU-upload/draw-call cost inside this End(), not at the Draw call sites.
        using (_prof.Measure(_sPresent))
            _spriteBatch.End();

        // PrimitiveBatch layer (gradients / curves / surfaces) draws in world space on
        // top of the SpriteBatch pass. Demo card for now; real users (metaballs) land later.
        if (_config.DebugDrawPrimitiveDemo)
            _devDemos.DrawPrimitiveDemo(_camera.GetDrawTransform(_screenCenter),
                              new Vector2(player.Body.Position.X, player.Body.Position.Y - 80f));

        if (_config.DebugDrawDensityDemo)
            _devDemos.DrawDensityDemo(_camera.GetDrawTransform(_screenCenter),
                            new Vector2(player.Body.Position.X, player.Body.Position.Y - 80f));

        if (_config.DebugDrawMetaballDemo && _metaballs != null)
            _devDemos.DrawMetaballDemo(_camera.GetDrawTransform(_screenCenter),
                             new Vector2(player.Body.Position.X, player.Body.Position.Y - 70f));

        // Glowing-shape pass (world space): the slash apex renders as a glowing triangle +
        // trail here, since the glow renderers need their own pass outside the SpriteBatch.
        var camTransform = _camera.GetDrawTransform(_screenCenter);
        long tGlow = _prof.Begin();
        // Frozen during playback: the glow advances its own trail state from dt and reads
        // a live RigRoot, neither of which is valid while scrubbing a recorded take.
        if (!_recorder.IsPlayback)
            _attackGlow.Draw(camTransform, _sim, _config, (float)gameTime.ElapsedGameTime.TotalSeconds);

        if (_config.DebugDrawGlowDemo)
            _devDemos.DrawGlowDemo(camTransform,
                         new Vector2(player.Body.Position.X, player.Body.Position.Y - 70f));

        _prof.End(_sGlow, tGlow);

        long tHud = _prof.Begin();
        _hud.Draw(_sim, _animator, _config);
        _recorder.DrawHud(_spriteBatch, _debugFont);
        string stepLine = _stepper.HudLine(_sim.Frame);
        if (stepLine != null)
        {
            var stepPos = new Vector2(8, 56);   // above the anim-trace indicator
            _spriteBatch.Begin();
            _spriteBatch.DrawString(_debugFont, stepLine, stepPos + new Vector2(1, 1), Color.Black);
            _spriteBatch.DrawString(_debugFont, stepLine, stepPos, Color.Cyan);
            _spriteBatch.End();
        }
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
        _prof.End(_sHud, tHud);

        if (_config.DebugFrameTimings)
        {
            // Two lines: the per-pass breakdown (mean ms over the window, worst appended
            // only for passes that spike), then the scene counters that explain it.
            string probe = _prof.Report + (_shownSlow ? "  CATCH-UP" : "");
            string counts = $"particles {_particles.Count}  entities {_sim.Entities.Count}  F11 dumps to console";
            var probePos = new Vector2(8, 96);
            _spriteBatch.Begin();
            _spriteBatch.DrawString(_debugFont, probe, probePos + new Vector2(1, 1), Color.Black);
            _spriteBatch.DrawString(_debugFont, probe, probePos, _shownSlow ? new Color(255, 90, 70) : Color.LimeGreen);
            _spriteBatch.DrawString(_debugFont, counts, probePos + new Vector2(1, 17), Color.Black);
            _spriteBatch.DrawString(_debugFont, counts, probePos + new Vector2(0, 16), Color.Gray);
            _spriteBatch.End();
        }

        // Stage save/reload feedback line (Ctrl+M / F5), below the frame-time probe.
        if (_toastTtl > 0f && _toast != null)
        {
            var toastPos = new Vector2(8, 136);
            _spriteBatch.Begin();
            _spriteBatch.DrawString(_debugFont, _toast, toastPos + new Vector2(1, 1), Color.Black);
            _spriteBatch.DrawString(_debugFont, _toast, toastPos, Color.Gold);
            _spriteBatch.End();
        }

        if (shotTarget != null && _screenshots.EndCapture(shotTarget, _spriteBatch, GraphicsDevice))
            Exit();

        // Close the profiling frame. IsRunningSlowly is latched across the window on the
        // same schedule as the profiler's, so the CATCH-UP flag and the numbers beside it
        // describe the same span.
        _prof.EndFrame();
        if (++_probeFrames >= _prof.WindowFrames)
        {
            _shownSlow = _probeSlow;
            _probeSlow = false;
            _probeFrames = 0;
        }

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
