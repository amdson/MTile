using System;
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
    private const float SkeletonScale = 0.6f;

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
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(Simulation.FixedDt);
    }

    protected override void Initialize()
    {
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
        // Load authored skeleton animations (copied next to the binary). Empty on
        // platforms without a readable filesystem (e.g. WASM) → procedural fallback.
        var anims = AnimationStore.LoadAll(Path.Combine(AppContext.BaseDirectory, "SkeletonStates"));
        _animator = new CharacterAnimator(SkeletonExamples.Biped(), anims);
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
        else if (_botInput != null)
        {
            // With a second player present offline, the bot spoofs its input (P2).
            _sim.Step(input, _botInput.Poll(_sim, _sim.Player.Frame));
        }
        else
        {
            _sim.Step(input);
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
        // animator. One-way — the sim is unaware this happens.
        if (_config.DebugDrawSkeleton)
            _animator.Update(CharacterAnimSample.From(player, dt));
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
            _animator.Draw(_draw, player.Body.Position, player.Facing, SkeletonScale);
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
        player.CurrentAction.Draw(_spriteBatch, _pixel, player.Body, player.CurrentActionVars);

        // Enemy FSM telegraph/strike overlays — same world-space layer as the
        // player's action draw, so windup tells read alongside the player's
        // slash arcs.
        foreach (var e in _sim.Entities)
            if (e is EnemyEntity en) en.DrawOverlay(_spriteBatch, _pixel);

        if (_config.DebugDrawHitboxes)
            foreach (var hb in _sim.Hitboxes.All)
                DrawHitbox(hb);

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

        // Enemy health bars in world space, drawn just above each wounded body.
        foreach (var e in _sim.Entities)
            if (_config.DebugDrawHealthBars && e.Health < e.MaxHealth) DrawEntityHealthBar(e);

        _spriteBatch.End();

        _spriteBatch.Begin();
        var mousePos = _sim.CurrentInput.MousePosition;
        _spriteBatch.Draw(_pixel, new Rectangle(mousePos.X - 2, mousePos.Y - 2, 5, 5), Color.Red);
        if (_config.DebugDrawHealthBars) DrawPlayerHealthBar();
        _spriteBatch.DrawString(_debugFont, player.CurrentStateName,  new Vector2(8,  8), Color.White);
        _spriteBatch.DrawString(_debugFont, player.CurrentActionName, new Vector2(8, 24), Color.White);
        _spriteBatch.DrawString(_debugFont,
            $"Planner: {_sim.EruptionMode}  (P to toggle)",
            new Vector2(8, 40),
            _sim.EruptionMode == EruptionPlannerMode.MassBall ? Color.LightCoral : Color.LightSkyBlue);

        DrawBlockPickerHud();

        _spriteBatch.End();

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _pixel?.Dispose();
        _movementConfigWatcher?.Dispose();
        base.UnloadContent();
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
