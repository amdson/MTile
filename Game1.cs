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

    public Game1(NetSetup net = null) : this()
    {
        _net = net;
    }

    private readonly Camera _camera = new();

    private DrawContext _draw;
    private DebugOverlayRenderer _debugOverlay;
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

    // Procedural skeleton animation for the primary player. Render-only, pull-model
    // (reads sim state via CharacterAnimSample; never writes back). Scale fits the
    // ~62px-tall rig to the player's ~19px body.
    private CharacterAnimator _animator;
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

    public Game1()
    {
        // Load game config before the GraphicsDeviceManager finalizes so window
        // prefs take effect on the first frame. Title-relative path resolved via
        // TitleContent → TitleContainer (works on DesktopGL and the Blazor/WASM build).
        _config = GameConfig.Load("game_config.json");

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = _config.WindowWidth,
            PreferredBackBufferHeight = _config.WindowHeight,
            IsFullScreen              = _config.Fullscreen,
        };
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

        _sim = new Simulation(_config, stage);

        if (_net != null)
        {
            // Drive a RollbackSession over the transport. pollLocal returns the input
            // sampled at the top of Update; send encodes packets onto the wire.
            _session = new MTile.Net.RollbackSession(
                _sim, _net.LocalPlayerIndex,
                _ => _localInput,
                pkt => _net.Send(MTile.Net.InputCodec.Encode(in pkt)));
        }
        // Offline only: if the stage spawned a second player, spoof its input with a bot.
        else if (_sim.SecondaryPlayers.Count > 0)
        {
            _botInput = new MTile.Net.BotInputSource(seed: 1234);
        }

        // Cosmetic feedback hooks. The sim raises these during Step; Game1 turns
        // them into particles. ChunkMap tints the tile-break burst by material.
        _sim.OnPlayerRespawn += pos => Effects.Puff(_particles, pos, Color.LimeGreen);
        _sim.Chunks.OnTileBroken += (pos, type) =>
            Effects.TileBreak(_particles, pos, TilePalette.BaseColor(type));

        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _debugFont = Content.Load<SpriteFont>("DebugFont");
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
        _glowField = new GlowTrailField(GraphicsDevice, downscale: 2)
        {
            Lambda           = 6f,    // ~0.8s visible streak
            HaloWorld        = 6f,    // soft halo ≈ 6 world px
            SourceFill       = 0.95f,
            StampRadiusWorld = 5f,    // blade half-thickness
            StampSpacingWorld = 3f,
        };
        // Load authored skeleton animations (copied next to the binary). Empty on
        // platforms without a readable filesystem (e.g. WASM) → procedural fallback.
        _skeletonAnims = AnimationStore.LoadAll(Path.Combine(AppContext.BaseDirectory, "SkeletonStates"));
        _animator = new CharacterAnimator(SkeletonExamples.Biped(), SkeletonScale, _skeletonAnims);
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

        var mouseState = Mouse.GetState();
        var viewport = GraphicsDevice.Viewport;
        _screenCenter = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        var mouseWorldPos = _camera.ScreenToWorld(mouseState.Position.ToVector2(), _screenCenter);

        // Mirror config toggle into the action-side static so BlockEruptionAction.Draw
        // can consult it without taking GameConfig as a dependency. Debug-draw only.
        EruptionPlanner.DebugDrawMassBall = _config.DebugDrawMassBall;

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
            _session.TryStep();
            _cosmetics.Update(_sim, _config, dt, mouseWorldPos, _screenCenter, LocalPlayer);
        }
        else
        {
            // Dev recorder: handle Ctrl+R / Ctrl+P and any playback scrubbing. Returns
            // false while in playback (sim frozen) — we skip the normal step + cosmetics.
            bool live = _recorder.HandleInput(keyboardState, _sim, _camera, _animator,
                                              _secondaryAnimators, SkeletonScale);
            if (live)
            {
                // Gather this frame's input and advance the simulation by fixed steps.
                var input = Controller.Poll(mouseWorldPos);
                // Slow-/fast-motion: accumulate TimeScale and run that many fixed steps
                // this frame — <1 skips frames (slow-mo), >1 runs extra, 0 pauses.
                _simAccum += MathF.Max(0f, _config.TimeScale);
                while (_simAccum >= 1f)
                {
                    _simAccum -= 1f;
                    // With a second player present offline, the bot spoofs its input (P2).
                    if (_botInput != null) _sim.Step(input, _botInput.Poll(_sim, _sim.Player.Frame));
                    else                   _sim.Step(input);
                }

                // Cosmetic-only pass: sprite sync, skeleton animators, knife trail,
                // particles, landing puff, camera tracking — reads sim state, never writes.
                _cosmetics.Update(_sim, _config, dt, mouseWorldPos, _screenCenter, LocalPlayer);

                // Record AFTER cosmetics so the captured pose + RigRoot reflect this frame.
                _recorder.CaptureFrame(_sim, _camera, _animator, _secondaryAnimators, SkeletonScale);
            }
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        // Capture this frame to an offscreen target (if a screenshot is pending), so the
        // save is immune to window focus/occlusion. Null when nothing is being captured.
        RenderTarget2D shotTarget = _screenshots.BeginCapture(GraphicsDevice);

        GraphicsDevice.Clear(Color.Black);

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

        if (_config.DebugDrawSteeringRamps)
            foreach (var body in _sim.Bodies)
                foreach (var c in body.Constraints)
                    if (c is SteeringRamp ramp) _debugOverlay.DrawSteeringRamp(ramp);

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

        if (shotTarget != null && _screenshots.EndCapture(shotTarget, _spriteBatch, GraphicsDevice))
            Exit();

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _pixel?.Dispose();
        _density?.Dispose();
        _metaballs?.Dispose();
        _movementConfigWatcher?.Dispose();
        base.UnloadContent();
    }
}
