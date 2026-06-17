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
    private PrimitiveBatch _prims;
    private DensityField _density;
    private SkeletonMetaballRenderer _metaballs;
    private GlowRenderer _glow;
    private readonly ParticleSystem _particles = new(capacity: 2048);
    // Cursor trail — a fading ribbon trailing the world-space mouse position.
    private readonly Trail _cursorTrail = new(capacity: 24, lifetime: 0.22f);
    // Tracked frame-to-frame so the landing-puff fires exactly once on the
    // air→ground transition.
    private bool _wasGroundedLastFrame;

    private FileSystemWatcher _movementConfigWatcher;

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
    private const float SkeletonScale = 0.6f;
    // Render-only trail of the primary player's animated "knife" bone, fed while a slash
    // overlay is playing so the glow triangle sweeps with the hand (not the hitbox dot).
    private Trail _knifeTrail;
    private const string KnifeBone = "knife";
    private float _simAccum;   // fixed-step accumulator for GameConfig.TimeScale (slow-mo)

    // Screenshot capture (desktop dev tool). F12 grabs a timestamped PNG next to the
    // binary. If the MTILE_SCREENSHOT env var is set, auto-capture to that path after
    // _autoShotFrame frames have rendered, then exit — lets a headless run produce a
    // frame for review. Captures through a RenderTarget so it's immune to window focus.
    private string _autoShotPath;
    private int _frameCount;
    private const int _autoShotFrame = 20;
    private bool _shotPending;
    private bool _exitAfterShot;
    private Keys _prevShotKey;   // F12 edge-detect

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
        // Auto-screenshot for headless review: MTILE_SCREENSHOT=path captures one frame
        // then exits. Desktop only (browser has no filesystem).
        if (!OperatingSystem.IsBrowser())
        {
            _autoShotPath = Environment.GetEnvironmentVariable("MTILE_SCREENSHOT");
            if (!string.IsNullOrEmpty(_autoShotPath)) { _shotPending = true; _exitAfterShot = true; }
        }

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
            Effects.TileBreak(_particles, pos, GetTileBaseColor(type));

        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _debugFont = Content.Load<SpriteFont>("DebugFont");
        _draw = new DrawContext(_spriteBatch, _pixel);
        _prims = new PrimitiveBatch(GraphicsDevice);
        // Half-res field (downscale 2) — cheaper and softer; 8-bit Color until banding
        // proves we need HalfVector4 (RENDERING_UPGRADE_PLAN spike #0).
        _density = new DensityField(GraphicsDevice, kernelSize: 128, downscale: 2);
        var splatFx     = Content.Load<Effect>("CapsuleSplat");
        var compositeFx = Content.Load<Effect>("MetaballComposite");
        _metaballs = new SkeletonMetaballRenderer(GraphicsDevice, splatFx, compositeFx, downscale: 2);
        _glow = new GlowRenderer(GraphicsDevice);
        // Load authored skeleton animations (copied next to the binary). Empty on
        // platforms without a readable filesystem (e.g. WASM) → procedural fallback.
        _skeletonAnims = AnimationStore.LoadAll(Path.Combine(AppContext.BaseDirectory, "SkeletonStates"));
        _animator = new CharacterAnimator(SkeletonExamples.Biped(), SkeletonScale, _skeletonAnims);
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

        // F12: manual screenshot to a timestamped PNG next to the binary (desktop only).
        if (!OperatingSystem.IsBrowser())
        {
            bool f12 = keyboardState.IsKeyDown(Keys.F12);
            if (f12 && _prevShotKey != Keys.F12)
            {
                _autoShotPath = Path.Combine(AppContext.BaseDirectory,
                    $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                _shotPending = true;
            }
            _prevShotKey = f12 ? Keys.F12 : Keys.None;
        }

        var mouseState = Mouse.GetState();
        var viewport = GraphicsDevice.Viewport;
        var screenCenter = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        var mouseWorldPos = _camera.ScreenToWorld(mouseState.Position.ToVector2(), screenCenter);

        // Mirror config toggle into the action-side static so BlockEruptionAction.Draw
        // can consult it without taking GameConfig as a dependency. Debug-draw only.
        EruptionPlanner.DebugDrawMassBall = _config.DebugDrawMassBall;

        // Gather this frame's input and advance the simulation by one fixed step.
        var input = Controller.Poll(mouseWorldPos);
        if (_session != null)
        {
            // Networked: feed the local sample to the session, drain remote arrivals,
            // then advance one rollback step (which may predict, roll back, or stall).
            _localInput = input;
            while (_net.TryReceive(out var bytes))
                if (MTile.Net.InputCodec.TryDecode(bytes, out var pkt))
                    _session.Receive(in pkt);
            _session.TryStep();
        }
        else
        {
            // Slow-/fast-motion (offline only): accumulate TimeScale and run that many
            // fixed steps this frame — <1 skips frames (slow-mo), >1 runs extra, 0 pauses.
            _simAccum += MathF.Max(0f, _config.TimeScale);
            while (_simAccum >= 1f)
            {
                _simAccum -= 1f;
                // With a second player present offline, the bot spoofs its input (P2).
                if (_botInput != null) _sim.Step(input, _botInput.Poll(_sim, _sim.Player.Frame));
                else                   _sim.Step(input);
            }
        }

        // ── Cosmetic-only systems below; they read sim state but never write it ──
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Cursor ribbon in world-space from the residue of the cursor's recent motion.
        if (_config.MouseTrail)
        {
            _cursorTrail.Tick(dt);
            _cursorTrail.Push(mouseWorldPos);
        }
        else _cursorTrail.Clear();

        // Sync sprites to their physics bodies + advance animations.
        var player = _sim.Player;
        if (player.Sprite != null)
        {
            player.Sprite.Position = player.Body.Position;
            player.Sprite.Update(dt);
        }

        // Procedural skeleton: pull a read-only sample of the player and advance the
        // animator every frame. One-way — the sim is unaware this happens. The rig is
        // drawn only under DebugDrawSkeleton, but the pose runs always so render effects
        // (the knife-anchored slash glow below) can read animated bone positions.
        // Scale the animator's dt too so easing/idle slow with the sim under TimeScale.
        _animator.Update(CharacterAnimSample.From(player, dt * _config.TimeScale));
        // Secondary players (training dummy, P2) get their own animators so
        // each rig tracks its own body, facing, and action timing.
        while (_secondaryAnimators.Count < _sim.SecondaryPlayers.Count)
            _secondaryAnimators.Add(new CharacterAnimator(SkeletonExamples.Biped(), SkeletonScale, _skeletonAnims));
        for (int i = 0; i < _sim.SecondaryPlayers.Count; i++)
            _secondaryAnimators[i].Update(
                CharacterAnimSample.From(_sim.SecondaryPlayers[i].Player, dt * _config.TimeScale));
        UpdateKnifeTrail(player, dt);
        foreach (var (p, _) in _sim.SecondaryPlayers)
        {
            if (p.Sprite == null) continue;
            p.Sprite.Position = p.Body.Position;
            p.Sprite.Update(dt);
        }
        foreach (var e in _sim.Entities)
        {
            if (e.Sprite == null) continue;
            e.SyncSprite();
            e.Sprite.Update(dt);
        }

        // Camera + landing puff follow the LOCAL player (the joiner controls P2).
        var localPlayer = LocalPlayer;

        // Air→ground transition: small dust puff at the player's feet.
        bool grounded = localPlayer.IsGrounded;
        if (grounded && !_wasGroundedLastFrame)
            Effects.Puff(_particles, localPlayer.Body.Position + new Vector2(0f, PlayerCharacter.Radius * 0.8f),
                new Color(180, 160, 120));
        _wasGroundedLastFrame = grounded;

        _particles.Update(dt);

        _camera.TrackTarget(localPlayer.Body.Position, screenCenter, dt);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        _frameCount++;
        // Capture this frame? Manual (F12) fires immediately; auto (env var) waits a few
        // frames so the world settles. Render through an offscreen target, save it, then
        // blit to the backbuffer — immune to window focus/occlusion.
        bool capturing = _shotPending && !OperatingSystem.IsBrowser()
                         && (!_exitAfterShot || _frameCount >= _autoShotFrame);
        RenderTarget2D shotTarget = null;
        if (capturing)
        {
            var pp = GraphicsDevice.PresentationParameters;
            // PreserveContents: the glow pass rebinds this target mid-frame, so it must
            // not discard the scene drawn before the glow.
            shotTarget = new RenderTarget2D(GraphicsDevice, pp.BackBufferWidth, pp.BackBufferHeight,
                false, pp.BackBufferFormat, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            GraphicsDevice.SetRenderTarget(shotTarget);
        }

        GraphicsDevice.Clear(Color.Black);

        var viewport = GraphicsDevice.Viewport;
        var screenCenter = new Vector2(viewport.Width / 2f, viewport.Height / 2f);

        _spriteBatch.Begin(transformMatrix: _camera.GetTransform(screenCenter));

        var chunks = _sim.Chunks;
        var player = _sim.Player;

        foreach (var chunk in chunks)
            DrawChunk(chunk);

        foreach (var (r, color) in _sim.Platforms)
            _spriteBatch.Draw(_pixel, new Rectangle(
                (int)r.Left, (int)r.Top,
                (int)(r.Right - r.Left), (int)(r.Bottom - r.Top)),
                color);

        foreach (var s in chunks.ActiveSprouts)
        {
            var c = s.Center;
            const float half = Chunk.TileSize * 0.5f;
            _spriteBatch.Draw(_pixel, new Rectangle(
                (int)(c.X - half), (int)(c.Y - half),
                Chunk.TileSize - 1, Chunk.TileSize - 1),
                Color.LightSkyBlue);
        }

        // Ghost outlines of queued (Pending) sprouts so players can preview the
        // build they're generating before each block starts growing. Pending nodes
        // have no live Center, so draw at the target cell (gtx*16+8, gty*16+8).
        foreach (var s in chunks.PendingSprouts)
        {
            const float half = Chunk.TileSize * 0.5f;
            float cx = s.Gtx * Chunk.TileSize + half;
            float cy = s.Gty * Chunk.TileSize + half;
            int left = (int)(cx - half);
            int top  = (int)(cy - half);
            int size = Chunk.TileSize - 1;
            var ghost = Color.LightSkyBlue * 0.4f;
            _spriteBatch.Draw(_pixel, new Rectangle(left,            top,            size, 1),    ghost);
            _spriteBatch.Draw(_pixel, new Rectangle(left,            top + size - 1, size, 1),    ghost);
            _spriteBatch.Draw(_pixel, new Rectangle(left,            top,            1,    size), ghost);
            _spriteBatch.Draw(_pixel, new Rectangle(left + size - 1, top,            1,    size), ghost);
        }

        // Entities before the player so the player overlays them when they overlap.
        foreach (var e in _sim.Entities)
        {
            if (e.Sprite != null) e.Sprite.Draw(_draw);
            else DrawPolygon(e.Body.Polygon, e.Body.Position, e.Color);
        }

        if (_config.DrawPlayerSprites)
        {
            if (player.Sprite != null) player.Sprite.Draw(_draw);
            foreach (var (p, _) in _sim.SecondaryPlayers)
                if (p.Sprite != null) p.Sprite.Draw(_draw);
        }
        if (_config.DebugDrawSkeleton)
        {
            // Drop the rig so its bind-pose soles rest on the ground line. A grounded
            // body floats 2·Radius above the floor (GroundChecker rest distance), so
            // the ground sits that far below the body center.
            float groundY    = player.Body.Position.Y + 2f * PlayerCharacter.Radius;
            // Per-frame sole: drop the rig so the lowest point of the *current* pose
            // rests on the ground, so swinging/arcing feet don't punch through.
            float rootY       = groundY - _animator.CurrentSoleY() * SkeletonScale;
            _animator.Draw(_draw, new Vector2(player.Body.Position.X, rootY), player.Facing,
                           _config.DebugDrawSkeletonJoints, _config.DebugHighlightPlantFoot);

            // Secondary players' rigs — same ground-drop math, each from its own animator.
            for (int i = 0; i < _sim.SecondaryPlayers.Count && i < _secondaryAnimators.Count; i++)
            {
                var p = _sim.SecondaryPlayers[i].Player;
                var anim = _secondaryAnimators[i];
                float pGroundY = p.Body.Position.Y + 2f * PlayerCharacter.Radius;
                float pRootY   = pGroundY - anim.CurrentSoleY() * SkeletonScale;
                anim.Draw(_draw, new Vector2(p.Body.Position.X, pRootY), p.Facing,
                          _config.DebugDrawSkeletonJoints, _config.DebugHighlightPlantFoot);
            }
        }
        if (_config.DebugDrawBodies)
        {
            DrawPolygon(player.Body.Polygon, player.Body.Position,
                (player.IsGrounded ? Color.LimeGreen : Color.Orange) * 0.5f);
            foreach (var (p, _) in _sim.SecondaryPlayers)
                DrawPolygon(p.Body.Polygon, p.Body.Position,
                    (p.IsGrounded ? Color.LimeGreen : Color.Orange) * 0.5f);
            foreach (var e in _sim.Entities)
                DrawPolygon(e.Body.Polygon, e.Body.Position, Color.White * 0.3f);
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
                DrawHitbox(hb);

        if (_config.DebugDrawForceFields)
            foreach (var f in _sim.ForceFields.All)
                DrawForceField(f);

        if (_config.DebugDrawHurtboxes)
            foreach (var hb in _sim.Hurtboxes.All)
                DrawHurtbox(hb);

        if (_config.DebugDrawPlayerOrientation)
        {
            var bodyPos = player.Body.Position;
            DrawConstraintArrow(bodyPos, new Vector2(player.Facing, 0f), Color.Magenta);
            DrawLine(bodyPos, _sim.CurrentInput.MouseWorldPosition, Color.Yellow * 0.6f, 1);
        }

        if (_config.DebugDrawConstraints)
            foreach (var body in _sim.Bodies)
                foreach (var c in body.Constraints)
                    if (c is SurfaceContact sc)
                        DrawConstraintArrow(sc.Position, sc.Normal,
                            c is FloatingSurfaceDistance ? Color.Cyan : Color.Yellow);

        if (_config.DebugDrawSteeringRamps)
            foreach (var body in _sim.Bodies)
                foreach (var c in body.Constraints)
                    if (c is SteeringRamp ramp) DrawSteeringRamp(ramp);

        // Enemy health bars in world space, drawn just above each wounded body.
        foreach (var e in _sim.Entities)
            if (_config.DebugDrawHealthBars && e.Health < e.MaxHealth) DrawEntityHealthBar(e);

        _spriteBatch.End();

        // PrimitiveBatch layer (gradients / curves / surfaces) draws in world space on
        // top of the SpriteBatch pass. Demo card for now; real users (metaballs) land later.
        if (_config.DebugDrawPrimitiveDemo)
            DrawPrimitiveDemo(_camera.GetTransform(screenCenter),
                              new Vector2(player.Body.Position.X, player.Body.Position.Y - 80f));

        if (_config.DebugDrawDensityDemo)
            DrawDensityDemo(_camera.GetTransform(screenCenter),
                            new Vector2(player.Body.Position.X, player.Body.Position.Y - 80f));

        if (_config.DebugDrawMetaballDemo)
            DrawMetaballDemo(_camera.GetTransform(screenCenter),
                             new Vector2(player.Body.Position.X, player.Body.Position.Y - 70f));

        // Glowing-shape pass (world space): the slash apex renders as a glowing triangle +
        // trail here, since GlowRenderer needs its own pass outside the SpriteBatch block.
        var camTransform = _camera.GetTransform(screenCenter);
        RenderActionGlow(camTransform, player.CurrentAction, player.CurrentActionVars, _knifeTrail);
        foreach (var (p, _) in _sim.SecondaryPlayers)
            RenderActionGlow(camTransform, p.CurrentAction, p.CurrentActionVars);

        if (_config.DebugDrawGlowDemo)
            DrawGlowDemo(camTransform,
                         new Vector2(player.Body.Position.X, player.Body.Position.Y - 70f));

        _spriteBatch.Begin();
        var mousePos = _sim.CurrentInput.MousePosition;
        _spriteBatch.Draw(_pixel, new Rectangle(mousePos.X - 2, mousePos.Y - 2, 5, 5), Color.Red);
        if (_config.DebugDrawHealthBars) DrawPlayerHealthBar();
        DrawPercentHud();   // always on — the percent meter is a core gameplay readout, not debug
        _spriteBatch.DrawString(_debugFont, player.CurrentStateName,  new Vector2(8,  8), Color.White);
        _spriteBatch.DrawString(_debugFont, player.CurrentActionName, new Vector2(8, 24), Color.White);
        _spriteBatch.DrawString(_debugFont, $"Anim: {_animator.State.Clip}", new Vector2(8, 40), Color.Aqua);
        _spriteBatch.DrawString(_debugFont,
            $"Planner: {_sim.EruptionMode}  (P to toggle)",
            new Vector2(8, 56),
            _sim.EruptionMode == EruptionPlannerMode.MassBall ? Color.LightCoral : Color.LightSkyBlue);

        DrawBlockPickerHud();

        _spriteBatch.End();

        if (capturing)
        {
            GraphicsDevice.SetRenderTarget(null);
            GraphicsDevice.Clear(Color.Black);
            _spriteBatch.Begin();
            _spriteBatch.Draw(shotTarget, GraphicsDevice.Viewport.Bounds, Color.White);
            _spriteBatch.End();
            try
            {
                using var fs = File.Create(_autoShotPath);
                shotTarget.SaveAsPng(fs, shotTarget.Width, shotTarget.Height);
            }
            catch { /* dev tool — never crash the game over a failed save */ }
            shotTarget.Dispose();
            _shotPending = false;
            if (_exitAfterShot) Exit();
        }

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

    // Dev preview of the PrimitiveBatch layer: a gradient quad, a stroked cubic Bezier
    // with a width+color taper, and a parametric surface (wavy grid colored by uv). All
    // anchored at `anchor` in world space. Toggled by GameConfig.DebugDrawPrimitiveDemo.
    private void DrawPrimitiveDemo(Matrix transform, Vector2 anchor)
    {
        _prims.Begin(transform);

        // Gradient quad — four corner colors interpolate across the fill.
        var q = anchor + new Vector2(-90f, 0f);
        _prims.Quad(q, q + new Vector2(60f, 0f), q + new Vector2(60f, 50f), q + new Vector2(0f, 50f),
                    Color.Red, Color.Yellow, Color.Lime, Color.Cyan);

        // Stroked cubic Bezier, tapering white->magenta along its length.
        Primitives.StrokeBezier(_prims,
            anchor + new Vector2(-10f, 50f), anchor + new Vector2(20f, -40f),
            anchor + new Vector2(60f,  60f), anchor + new Vector2(95f,  0f),
            width: 6f, Color.White, Color.Magenta);

        // Parametric surface: a sine-warped grid, hue ramped across u and v.
        Vector2 sbase = anchor + new Vector2(20f, 0f);
        Primitives.Surface(_prims,
            (u, v) => sbase + new Vector2(u * 70f, v * 50f + MathF.Sin(u * MathF.PI * 2f) * 8f),
            (u, v) => new Color(u, v, 1f - u * v), ucount: 20, vcount: 14);

        _prims.End();
    }

    // Dev preview of the DensityField glow layer: a cluster of overlapping colored
    // kernels splatted into the field RT and composited additively, so the additive sum
    // (the "sum of kernels around particles") reads as merging soft glow. Toggled by
    // GameConfig.DebugDrawDensityDemo.
    private void DrawDensityDemo(Matrix transform, Vector2 anchor)
    {
        _density.Begin(transform);
        // A ring of colored blobs plus a bright core — overlaps merge additively.
        _density.Splat(anchor,                              34f, new Color(40, 120, 255));
        _density.Splat(anchor + new Vector2( 26f,  4f),     30f, new Color(255, 60, 160));
        _density.Splat(anchor + new Vector2(-24f,  8f),     28f, new Color(80, 255, 140));
        _density.Splat(anchor + new Vector2(  6f, -22f),    26f, new Color(255, 200, 40));
        _density.Splat(anchor + new Vector2( 40f, -16f),    18f, new Color(180, 120, 255));
        _density.End();
        _density.Composite();
    }

    // Dev preview of the segment-metaball shaders: a synthetic stick figure (torso, two
    // arms, two legs) built as bone segments and rendered as one merged gooey blob. Tests
    // the CapsuleSplat + MetaballComposite path before it's wired to the real skeleton.
    private readonly List<MetaballBone> _metaballDemoBones = new();
    private void DrawMetaballDemo(Matrix transform, Vector2 anchor)
    {
        var blob = new Color(120, 200, 255);
        Vector2 hip = anchor, neck = anchor + new Vector2(0f, -34f), head = neck + new Vector2(0f, -10f);
        _metaballDemoBones.Clear();
        _metaballDemoBones.Add(new MetaballBone(hip, neck, blob));                              // torso
        _metaballDemoBones.Add(new MetaballBone(neck, head, blob));                             // neck->head
        _metaballDemoBones.Add(new MetaballBone(neck, neck + new Vector2(-22f, 18f), blob));    // left arm
        _metaballDemoBones.Add(new MetaballBone(neck, neck + new Vector2( 22f, 18f), blob));    // right arm
        _metaballDemoBones.Add(new MetaballBone(hip,  hip  + new Vector2(-14f, 34f), blob));    // left leg
        _metaballDemoBones.Add(new MetaballBone(hip,  hip  + new Vector2( 14f, 34f), blob));    // right leg

        var style = MetaballStyle.Default;
        style.Radius = 18f;
        style.Iso    = 0.35f;
        style.Edge   = 0.05f;
        style.Inner  = new Color(120, 230, 255);
        style.Rim    = new Color(20, 70, 200);
        _metaballs.Render(transform, _metaballDemoBones, style);
    }

    // Feed the primary player's knife-anchored slash trail. While a slash overlay is
    // actually eased in (its clip is playing), push the animated knife bone's world
    // position so the glow triangle sweeps with the hand; otherwise let the trail age
    // out. Slashes without an authored clip never raise ActionWeight, so they fall back
    // to the hitbox-driven SlashTrail in RenderActionGlow. Render-only.
    private void UpdateKnifeTrail(PlayerCharacter player, float dt)
    {
        _knifeTrail ??= new Trail(24, 0.16f);
        _knifeTrail.Tick(dt);
        if (player.CurrentAction is SlashLikeAction && _animator.OverlayActive)
        {
            // Same root the rig Draw uses: drop the rig so the current pose's sole rests
            // on the ground line under the body center. Read the knife from the LIVE
            // pose so the glow stays welded to the rendered hand. The upper body now
            // stiffens during the overlay (CharacterAnimator §4), so the live rig keeps
            // most of the authored sweep instead of low-passing it away.
            float groundY = player.Body.Position.Y + 2f * PlayerCharacter.Radius;
            float rootY   = groundY - _animator.CurrentSoleY() * SkeletonScale;
            if (_animator.TryBoneOrigin(KnifeBone, new Vector2(player.Body.Position.X, rootY),
                                        player.Facing, out var knife, fromOverlay: false))
                _knifeTrail.Push(knife);
        }
    }

    // Render the glowing-shape effect for an action if it's a slash: the swept apex
    // becomes a glowing triangle trailing a colored aura streak. Replaces the old ribbon.
    // `knifeTrail` (primary player only) anchors the sweep to the animated knife bone;
    // when it's empty (e.g. a slash with no authored clip) it falls back to the action's
    // own hitbox-driven trail.
    private void RenderActionGlow(Matrix cam, ActionState action, in ActionVars vars, Trail knifeTrail = null)
    {
        if (action is SlashLikeAction slash)
        {
            Trail t = (knifeTrail != null && knifeTrail.Count >= 1) ? knifeTrail : slash.SlashTrail;
            if (t.Count >= 1)
                _glow.DrawTrailGlow(cam, t, slash.SlashGlowColor,
                                    auraRadius: 13f, coreSize: 5f, intensity: 0.8f);
        }
        else if (action is StabAction stab && stab.TipTrail.Count >= 1)
            _glow.DrawTrailGlow(cam, stab.TipTrail, stab.StabColorFor(vars.IsGrounded),
                                auraRadius: 14f, coreSize: 6f, intensity: 0.8f, core: GlowCore.Sphere);
    }

    // Dev preview of the glow effect: a glowing triangle riding a curved trail, built from
    // a synthetic Trail so the streak is visible without a live slash. GameConfig.DebugDrawGlowDemo.
    private Trail _glowDemoTrail;
    private float _glowDemoT;
    private void DrawGlowDemo(Matrix cam, Vector2 anchor)
    {
        _glowDemoTrail ??= new Trail(16, 0.4f);
        // Sweep a point along a lissajous curve, pushing one sample per frame.
        _glowDemoT += 0.08f;
        var p = anchor + new Vector2(MathF.Sin(_glowDemoT) * 70f, MathF.Cos(_glowDemoT * 1.7f) * 30f);
        _glowDemoTrail.Tick(1f / 30f);
        _glowDemoTrail.Push(p);
        _glow.DrawTrailGlow(cam, _glowDemoTrail, new Color(120, 200, 255), auraRadius: 20f, coreSize: 9f);
    }

    private void DrawChunk(Chunk chunk)
    {
        var viewport = GraphicsDevice.Viewport;
        float halfW = viewport.Width / (2f * _camera.Zoom);
        float halfH = viewport.Height / (2f * _camera.Zoom);
        int chunkPixelSize = Chunk.Size * Chunk.TileSize;
        var origin = chunk.WorldPosition;

        if (origin.X > _camera.Position.X + halfW || origin.X + chunkPixelSize < _camera.Position.X - halfW ||
            origin.Y > _camera.Position.Y + halfH || origin.Y + chunkPixelSize < _camera.Position.Y - halfH)
            return;

        for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++)
            {
                if (!chunk.Tiles[tx, ty].IsSolid) continue;
                int gtx = chunk.ChunkPos.X * Chunk.Size + tx;
                int gty = chunk.ChunkPos.Y * Chunk.Size + ty;
                var type = chunk.Tiles[tx, ty].Type;
                float dmgFrac = MathF.Min(_sim.Chunks.Damage.Get(gtx, gty) / TileDamage.MaxHPFor(type), 1f);
                var baseColor = GetTileBaseColor(type);
                var color = dmgFrac > 0f
                    ? Color.Lerp(baseColor, Color.Black, dmgFrac * 0.7f)
                    : baseColor;
                _spriteBatch.Draw(_pixel, new Rectangle(
                    (int)(origin.X + tx * Chunk.TileSize),
                    (int)(origin.Y + ty * Chunk.TileSize),
                    Chunk.TileSize - 1, Chunk.TileSize - 1), color);
            }
    }

    private static Color GetTileBaseColor(TileType type) => type switch
    {
        TileType.Sand  => new Color(220, 200, 150),  // warm light sandy
        TileType.Dirt  => new Color(110,  75,  45),  // earthy mid-brown
        TileType.Stone => Color.Gray,                 // existing
        TileType.Foam  => new Color(235, 240, 250),  // near-white, faint blue tint
        _              => Color.Gray,
    };

    private void DrawPolygon(Polygon polygon, Vector2 position, Color color)
    {
        var verts = polygon.GetVertices(position);
        for (int i = 0; i < verts.Length; i++)
            DrawLine(verts[i], verts[(i + 1) % verts.Length], color);
    }

    // Compact 16×2 bar floating above the entity. Background dark gray, fill
    // green→red as HP drops, both in world space so the bar tracks the body.
    private void DrawEntityHealthBar(Entity e)
    {
        const int BarWidth   = 18;
        const int BarHeight  = 2;
        var bounds = e.Body.Bounds;
        int x = (int)(bounds.CenterX - BarWidth * 0.5f);
        int y = (int)(bounds.Top - 6);
        float frac = MathHelper.Clamp(e.Health / e.MaxHealth, 0f, 1f);
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, BarWidth, BarHeight), new Color(40, 40, 40));
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, (int)(BarWidth * frac), BarHeight),
            Color.Lerp(Color.Red, Color.LimeGreen, frac));
    }

    // Top-right block-picker indicator: four 24x24 swatches in a row, one per
    // pickable TileType, the selected one brightened and outlined, with 1-4 labels.
    private void DrawBlockPickerHud()
    {
        var types = new[] { TileType.Stone, TileType.Dirt, TileType.Sand, TileType.Foam };

        const int SwatchSize    = 24;
        const int SwatchGap     = 6;
        const int RightPadding  = 12;
        const int TopPadding    = 8;
        const int LabelOffset   = SwatchSize + 4;

        int viewportW = GraphicsDevice.Viewport.Width;
        int totalW    = types.Length * SwatchSize + (types.Length - 1) * SwatchGap;
        int x0        = viewportW - RightPadding - totalW;
        int y0        = TopPadding;

        var activeBlockType = _sim.ActiveBlockType;
        for (int i = 0; i < types.Length; i++)
        {
            int x = x0 + i * (SwatchSize + SwatchGap);
            bool selected = types[i] == activeBlockType;

            var col = GetTileBaseColor(types[i]);
            var fill = selected ? col : new Color((int)(col.R * 0.4f), (int)(col.G * 0.4f), (int)(col.B * 0.4f));
            _spriteBatch.Draw(_pixel, new Rectangle(x, y0, SwatchSize, SwatchSize), fill);

            var border = selected ? Color.White : new Color(80, 80, 80);
            _spriteBatch.Draw(_pixel, new Rectangle(x,                  y0,                  SwatchSize, 1), border);
            _spriteBatch.Draw(_pixel, new Rectangle(x,                  y0 + SwatchSize - 1, SwatchSize, 1), border);
            _spriteBatch.Draw(_pixel, new Rectangle(x,                  y0,                  1, SwatchSize), border);
            _spriteBatch.Draw(_pixel, new Rectangle(x + SwatchSize - 1, y0,                  1, SwatchSize), border);

            string keyLabel = (i + 1).ToString();
            _spriteBatch.DrawString(_debugFont, keyLabel,
                new Vector2(x + SwatchSize / 2f - 4, y0 + LabelOffset),
                selected ? Color.White : new Color(160, 160, 160));
        }

        _spriteBatch.DrawString(_debugFont, activeBlockType.ToString(),
            new Vector2(x0, y0 + LabelOffset + 16), Color.White);
    }

    private void DrawPlayerHealthBar()
    {
        const int X = 8, Y = 56, BarW = 120, BarH = 8;
        var player = _sim.Player;
        float frac = MathHelper.Clamp(player.Health / player.MaxHealth, 0f, 1f);
        _spriteBatch.Draw(_pixel, new Rectangle(X, Y, BarW, BarH), new Color(40, 40, 40));
        _spriteBatch.Draw(_pixel, new Rectangle(X, Y, (int)(BarW * frac), BarH),
            Color.Lerp(Color.Red, Color.LimeGreen, frac));
        _spriteBatch.DrawString(_debugFont,
            $"HP {player.Health:F1}/{player.MaxHealth:F1}",
            new Vector2(X + BarW + 8, Y - 4), Color.White);
    }

    // Escalation percent (COMBAT_FEEL_PLAN Phase 5) — the monotonic meter that scales
    // incoming knockback. A core gameplay readout, so it's always on (independent of
    // DebugDrawHealthBars), pinned to the lower-left and tinted hotter as it climbs.
    private void DrawPercentHud()
    {
        var vp = GraphicsDevice.Viewport;
        float pct = _sim.Player.Combat.DamagePercent;
        var color = Color.Lerp(Color.White, Color.OrangeRed, MathHelper.Clamp(pct / 200f, 0f, 1f));
        _spriteBatch.DrawString(_debugFont, $"{pct:F0}%",
            new Vector2(12f, vp.Height - 34f), color,
            0f, Vector2.Zero, 1.6f, SpriteEffects.None, 0f);
    }

    // Force-field overlay (hold / grab / throw) — faint region fill + outline + a
    // focus marker, colored by the field's DebugColor. Mirrors DrawHitbox; world-space.
    private void DrawForceField(ForceField f)
    {
        var color = f.DebugColor;
        var r = f.Region;
        var rect = new Rectangle((int)r.Left, (int)r.Top,
            (int)(r.Right - r.Left), (int)(r.Bottom - r.Top));
        _spriteBatch.Draw(_pixel, rect, color * 0.12f);
        var tl = new Vector2(r.Left,  r.Top);
        var tr = new Vector2(r.Right, r.Top);
        var br = new Vector2(r.Right, r.Bottom);
        var bl = new Vector2(r.Left,  r.Bottom);
        DrawLine(tl, tr, color, 1);
        DrawLine(tr, br, color, 1);
        DrawLine(br, bl, color, 1);
        DrawLine(bl, tl, color, 1);
        // Focus marker — the point the servo pulls/flings toward.
        _spriteBatch.Draw(_pixel, new Rectangle((int)f.Focus.X - 2, (int)f.Focus.Y - 2, 4, 4), color);
    }

    private void DrawConstraintArrow(Vector2 position, Vector2 normal, Color color)
    {
        const float shaftLength = 20f;
        const float headLength = 8f;
        var tip = position + normal * shaftLength;
        var perp = new Vector2(-normal.Y, normal.X);
        DrawLine(position, tip, color);
        DrawLine(tip, tip + (-normal + perp) * headLength * 0.707f, color);
        DrawLine(tip, tip + (-normal - perp) * headLength * 0.707f, color);
    }

    // Visualize one SteeringRamp at its corner: the surface tangent (the "ramp" the
    // body skims along), the banned direction (into the solid), and the corner dot.
    // Color = Sense (Over: lime / Under: orange); opacity fades with Weight so inert
    // ramps appear ghosted.
    private void DrawSteeringRamp(SteeringRamp ramp)
    {
        var baseColor = ramp.Sense == SteeringSense.Over ? Color.LimeGreen : Color.Orange;
        float alpha = 0.25f + 0.75f * MathHelper.Clamp(ramp.Weight, 0f, 1f);
        var color   = baseColor * alpha;
        var banned  = baseColor * (alpha * 0.45f);

        // Tangent line through the corner (the implicit ramp surface, on both sides).
        const float Tangent = 28f;
        var tan = ramp.SurfaceDir * Tangent;
        DrawLine(ramp.Corner - tan, ramp.Corner + tan, color, 2);

        // Arrowhead at the leading tip so the travel direction reads.
        var lead = ramp.Corner + tan;
        var perp = new Vector2(-ramp.SurfaceDir.Y, ramp.SurfaceDir.X) * 6f;
        DrawLine(lead, lead - tan * 0.25f + perp, color, 1);
        DrawLine(lead, lead - tan * 0.25f - perp, color, 1);

        // Banned direction (into the solid) — a short ghosted stub from the corner.
        DrawLine(ramp.Corner, ramp.Corner + ramp.BannedDir * 14f, banned, 1);

        // Corner marker.
        _draw.Disc(ramp.Corner, 3f, color);
        _draw.Ring(ramp.Corner, 5f, color, 12, 1f);
    }

    // Translucent fill + crisp outline. Color owned by the publisher (Hitbox.DebugColor).
    private void DrawHitbox(Hitbox hb)
    {
        var color = hb.DebugColor;
        var rect = new Rectangle(
            (int)hb.Region.Left, (int)hb.Region.Top,
            (int)(hb.Region.Right - hb.Region.Left),
            (int)(hb.Region.Bottom - hb.Region.Top));

        if (hb.Shape != null)
        {
            _spriteBatch.Draw(_pixel, rect, color * 0.12f);
            var verts = hb.Shape.GetVertices(hb.ShapePos, hb.ShapeRotation);
            for (int i = 0; i < verts.Length; i++)
                DrawLine(verts[i], verts[(i + 1) % verts.Length], color, 1);
        }
        else
        {
            _spriteBatch.Draw(_pixel, rect, color * 0.35f);
            var tl = new Vector2(hb.Region.Left,  hb.Region.Top);
            var tr = new Vector2(hb.Region.Right, hb.Region.Top);
            var br = new Vector2(hb.Region.Right, hb.Region.Bottom);
            var bl = new Vector2(hb.Region.Left,  hb.Region.Bottom);
            DrawLine(tl, tr, color, 1);
            DrawLine(tr, br, color, 1);
            DrawLine(br, bl, color, 1);
            DrawLine(bl, tl, color, 1);
        }
    }

    // Defensive region outline (cyan), always an axis-aligned AABB.
    private void DrawHurtbox(Hurtbox hb)
    {
        var color = Color.Cyan;
        var rect = new Rectangle(
            (int)hb.Region.Left, (int)hb.Region.Top,
            (int)(hb.Region.Right - hb.Region.Left),
            (int)(hb.Region.Bottom - hb.Region.Top));
        _spriteBatch.Draw(_pixel, rect, color * 0.18f);
        var tl = new Vector2(hb.Region.Left,  hb.Region.Top);
        var tr = new Vector2(hb.Region.Right, hb.Region.Top);
        var br = new Vector2(hb.Region.Right, hb.Region.Bottom);
        var bl = new Vector2(hb.Region.Left,  hb.Region.Bottom);
        DrawLine(tl, tr, color, 1);
        DrawLine(tr, br, color, 1);
        DrawLine(br, bl, color, 1);
        DrawLine(bl, tl, color, 1);
    }

    private void DrawLine(Vector2 start, Vector2 end, Color color, int thickness = 2)
    {
        var edge = end - start;
        float angle = MathF.Atan2(edge.Y, edge.X);
        _spriteBatch.Draw(_pixel, start, null, color, angle, Vector2.Zero,
            new Vector2(edge.Length(), thickness), SpriteEffects.None, 0f);
    }
}
