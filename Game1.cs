using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTile;

public class Game1 : Game, IEntitySpawner
{
    private GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;
    private Texture2D _pixel;
    private SpriteFont _debugFont;

    private readonly List<PhysicsBody> _bodies = new();
    private PlayerCharacter _player;
    private Vector2 _playerSpawn;

    private static readonly Vector2 Gravity = new(0f, 600f);
    private readonly ChunkMap _chunks = new();
    private readonly HitboxWorld  _hitboxes  = new();   // offensive — populated by attackers during update
    private readonly HurtboxWorld _hurtboxes = new();   // defensive — populated by IHittables before update
    private readonly List<IHittable> _hittables = new();
    private readonly List<Entity>    _entities  = new();
    private readonly Camera _camera = new();
    private readonly Controller _controller = new();

    // Stage-owned scene props. The active Stage's Populate fills these; Update
    // ticks every callback in _stageTickers, Draw renders every entry in
    // _platforms. Cleared between stage loads (currently load-once at startup).
    private readonly List<Action<float>>            _stageTickers = new();
    private readonly List<(MovingRectangle Rect, Color Color)> _platforms = new();
    private GameConfig _config;

    private DrawContext _draw;
    private readonly ParticleSystem _particles = new(capacity: 2048);
    // Tracked frame-to-frame so the landing-puff fires exactly once on the
    // air→ground transition (rather than every frame the player is grounded).
    private bool _wasGroundedLastFrame;

    // Edge-detect 'P' so a held key doesn't flip the planner every frame.
    private bool _wasPDown;

    private FileSystemWatcher _movementConfigWatcher;


    public Game1()
    {
        // Load game config before the GraphicsDeviceManager finalizes so window
        // prefs (size, fullscreen) take effect on the first frame instead of
        // resizing once gameplay starts.
        // Title-relative path: resolved via TitleContent → TitleContainer (works on
        // both DesktopGL and the planned Blazor/WASM build).
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
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 30.0);
    }

    protected override void Initialize()
    {
        var stage = Stages.Get(_config.Stage);

        // Title-relative; chunk files referenced by the config resolve next to it.
        TerrainLoader.Load($"Levels/{stage.TerrainConfig}", _chunks);

        // On desktop, point at the absolute repo-source path so FileSystemWatcher
        // can hot-reload tuning edits. On web (Blazor WASM), the filesystem APIs
        // throw PlatformNotSupported — load from a title-relative path instead and
        // skip the watcher entirely.
        if (OperatingSystem.IsBrowser())
        {
            MovementConfig.Load("movement_config.json");
        }
        else
        {
            string movementCfgPath = Path.GetFullPath("movement_config.json");
            MovementConfig.Load(movementCfgPath);

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

        _playerSpawn = stage.PlayerSpawn;
        _player = new PlayerCharacter(_playerSpawn);
        _bodies.Add(_player.Body);
        _hittables.Add(_player);

        // Hand control to the stage. It populates platforms, tickers, and entities;
        // see Stages.PopulateStart / PopulateArena for the per-stage scripts.
        stage.Populate(this);

        // Wire tile-break debris. ChunkMap doesn't know about particles; it just
        // raises the event with the cell's center and material, and Effects.TileBreak
        // tints the burst by the broken tile's type color.
        _chunks.OnTileBroken += (pos, type) =>
            Effects.TileBreak(_particles, pos, GetTileBaseColor(type));

        base.Initialize();
    }

    // Stage-facing API. Stages call these from Populate to register entities,
    // dynamic platforms, and per-frame tickers without touching Game1's private state.
    public void SpawnEntity(Entity e)
    {
        _entities.Add(e);
        _hittables.Add(e);
        _bodies.Add(e.Body);
    }

    public void AddPlatform(MovingRectangle rect, Color color)
    {
        _platforms.Add((rect, color));
        _chunks.Providers.Add(rect);
    }

    public void AddTicker(Action<float> tick) => _stageTickers.Add(tick);

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _debugFont = Content.Load<SpriteFont>("DebugFont");
        _draw = new DrawContext(_spriteBatch, _pixel);
    }

    protected override void Update(GameTime gameTime)
    {
        var keyboardState = Keyboard.GetState();

        if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed ||
            keyboardState.IsKeyDown(Keys.Escape))
            Exit();

        // 'P' toggles between block-eruption planners (priority-field ↔ mass-ball).
        // Edge-detect so a held key doesn't flip every frame.
        bool pDown = keyboardState.IsKeyDown(Keys.P);
        if (pDown && !_wasPDown)
            EruptionPlanner.CurrentMode = EruptionPlanner.CurrentMode == EruptionPlannerMode.PriorityField
                ? EruptionPlannerMode.MassBall
                : EruptionPlannerMode.PriorityField;
        _wasPDown = pDown;

        var mouseState = Mouse.GetState();
        var viewport = GraphicsDevice.Viewport;
        var screenCenter = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        var mouseWorldPos = _camera.ScreenToWorld(mouseState.Position.ToVector2(), screenCenter);

        _controller.Update(mouseWorldPos);

        // Mirror config toggles into the action-side static so BlockEruptionAction.Draw
        // can consult it without taking GameConfig as a dependency.
        EruptionPlanner.DebugDrawMassBall = _config.DebugDrawMassBall;

        // Cosmetic cursor trail — one tiny world-space dot per frame at the
        // cursor position. The dots decay in place, so the trail is the residue
        // of the cursor's recent motion. Particles are drawn within the world
        // transform below, so the trail tracks the cursor naturally.
        if (_config.MouseTrail) Effects.MouseTrailTick(_particles, mouseWorldPos);

        HandleBuildInput();

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Tick dynamic surfaces BEFORE the body sweep (per roadmap §2D update ordering).
        // Each ticker was registered by the active stage's Populate (e.g. the
        // sinusoidal bobber and ferris wheel on the start stage).
        foreach (var tick in _stageTickers) tick(dt);

        _chunks.TickSprouts(dt);
        // Decay per-cell impact accumulator so stray small impulses (walking,
        // tiny bumps) bleed off instead of accreting toward a fake landing.
        _chunks.Impact.Tick(dt);

        // Combat frame phases:
        //   1. Clear both registries.
        //   2. Each IHittable publishes its defensive hurtboxes (start-of-frame snapshot).
        //   3. Player update runs; SlashAction publishes offensive hitboxes during it.
        //      (Future: entity AI / contact-damage attacks publish here too.)
        //   4. CombatSystem.Apply: tile damage + per-(HitId,Target) OnHit dispatch.
        //   5. Physics integrates afterward.
        _hitboxes.Clear();
        _hurtboxes.Clear();
        foreach (var h in _hittables) h.PublishHurtboxes(_hurtboxes);
        _player.Update(_controller, _chunks, _hitboxes, _hurtboxes, dt);
        // Entity AI tick. Slotted between hurtbox publication and CombatSystem
        // so any hitboxes published here (Stalker lunge) resolve this frame.
        // Pass _player so AI can target it; null-target enemies would just skip.
        // Snapshot count so newly-spawned entities (turret bullets, etc.) skip
        // their first Update and start being ticked next frame.
        int entityCount = _entities.Count;
        for (int i = 0; i < entityCount; i++) _entities[i].Update(dt, _player, _hitboxes, this);
        CombatSystem.Apply(_chunks, _hitboxes, _hurtboxes);

        // Player respawn on death. Reset position, velocity, health — the FSMs
        // re-evaluate from the new pose on the next frame (Falling → Standing
        // once the body lands on the floor).
        if (!_player.IsAlive)
        {
            _player.Respawn(_playerSpawn);
            Effects.Puff(_particles, _player.Body.Position, Color.LimeGreen);
        }

        // Entity gravity-scale opt-out — applied as a counter-force right before
        // PhysicsWorld integrates gravity uniformly. Balloons (GravityScale=0) end
        // up weightless; balls (1) get full gravity; partial values would float.
        foreach (var e in _entities) e.PreStep(Gravity);

        PhysicsWorld.StepSwept(_bodies, _chunks, dt, Gravity);

        // Sweep up entities that died this frame. Order: remove from physics body
        // list first (otherwise StepSwept next frame still integrates the corpse),
        // then from the IHittable list, then the entity list.
        for (int i = _entities.Count - 1; i >= 0; i--)
        {
            if (!_entities[i].IsDead) continue;
            _bodies.Remove(_entities[i].Body);
            _hittables.Remove(_entities[i]);
            _entities.RemoveAt(i);
        }

        // Sync sprites to their physics bodies + advance animations + particles.
        // Done after StepSwept so positions reflect this frame's resolved motion.
        if (_player.Sprite != null)
        {
            _player.Sprite.Position = _player.Body.Position;
            _player.Sprite.Update(dt);
        }
        foreach (var e in _entities)
        {
            if (e.Sprite == null) continue;
            e.SyncSprite();
            e.Sprite.Update(dt);
        }

        // Air→ground transition: small dust puff at the player's feet.
        bool grounded = _player.IsGrounded;
        if (grounded && !_wasGroundedLastFrame)
            Effects.Puff(_particles, _player.Body.Position + new Vector2(0f, PlayerCharacter.Radius * 0.8f),
                new Color(180, 160, 120));
        _wasGroundedLastFrame = grounded;

        _particles.Update(dt);

        _camera.TrackTarget(_player.Body.Position, screenCenter, dt);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);

        var viewport = GraphicsDevice.Viewport;
        var screenCenter = new Vector2(viewport.Width / 2f, viewport.Height / 2f);

        _spriteBatch.Begin(transformMatrix: _camera.GetTransform(screenCenter));

        foreach (var chunk in _chunks)
            DrawChunk(chunk);

        foreach (var (r, color) in _platforms)
            _spriteBatch.Draw(_pixel, new Rectangle(
                (int)r.Left, (int)r.Top,
                (int)(r.Right - r.Left), (int)(r.Bottom - r.Top)),
                color);

        foreach (var s in _chunks.ActiveSprouts)
        {
            var c = s.Center;
            const float half = Chunk.TileSize * 0.5f;
            _spriteBatch.Draw(_pixel, new Rectangle(
                (int)(c.X - half), (int)(c.Y - half),
                Chunk.TileSize - 1, Chunk.TileSize - 1),
                Color.LightSkyBlue);
        }

        // Entities (balloons, balls) before the player so the player overlays them
        // when they overlap. Sprite when present (current default); polygon outline
        // fallback for entities created without one.
        foreach (var e in _entities)
        {
            if (e.Sprite != null) e.Sprite.Draw(_draw);
            else DrawPolygon(e.Body.Polygon, e.Body.Position, e.Color);
        }

        if (_player.Sprite != null) _player.Sprite.Draw(_draw);
        if (_config.DebugDrawBodies)
        {
            DrawPolygon(_player.Body.Polygon, _player.Body.Position,
                (_player.IsGrounded ? Color.LimeGreen : Color.Orange) * 0.5f);
            foreach (var e in _entities)
                DrawPolygon(e.Body.Polygon, e.Body.Position, Color.White * 0.3f);
        }

        // Particles drawn over gameplay but under debug overlays — they're feedback,
        // not diagnostic info.
        _particles.Draw(_draw);

        // Action overlay (slash arc, etc.) in world space, on top of the body.
        _player.CurrentAction.Draw(_spriteBatch, _pixel, _player.Body);

        if (_config.DebugDrawHitboxes)
            foreach (var hb in _hitboxes.All)
                DrawHitbox(hb);

        if (_config.DebugDrawHurtboxes)
            foreach (var hb in _hurtboxes.All)
                DrawHurtbox(hb);

        if (_config.DebugDrawPlayerOrientation)
        {
            var bodyPos = _player.Body.Position;
            // Facing intent — short magenta arrow on the side the player is oriented toward.
            DrawConstraintArrow(bodyPos, new Vector2(_player.Facing, 0f), Color.Magenta);
            // Mouse ray — thin translucent yellow line from body center to cursor.
            DrawLine(bodyPos, _controller.Current.MouseWorldPosition, Color.Yellow * 0.6f, 1);
        }

        if (_config.DebugDrawConstraints)
            foreach (var body in _bodies)
                foreach (var c in body.Constraints)
                    if (c is SurfaceContact sc)
                        DrawConstraintArrow(sc.Position, sc.Normal,
                            c is FloatingSurfaceDistance ? Color.Cyan : Color.Yellow);

        if (_config.DebugDrawGuidedPath
            && _player.CurrentState is GuidedState gs && gs.ActivePath != null)
            DrawGuidedPath(gs.ActivePath, gs.CurrentProgressT);

        // Enemy health bars in world space, drawn just above each wounded body.
        // Skip undamaged ones so passive entities (intact balls, balloons) don't
        // clutter the screen with full-green bars.
        foreach (var e in _entities)
            if (_config.DebugDrawHealthBars && e.Health < e.MaxHealth) DrawEntityHealthBar(e);

        _spriteBatch.End();

        _spriteBatch.Begin();
        var mousePos = _controller.Current.MousePosition;
        _spriteBatch.Draw(_pixel, new Rectangle(mousePos.X - 2, mousePos.Y - 2, 5, 5), Color.Red);
        if (_config.DebugDrawHealthBars) DrawPlayerHealthBar();
        _spriteBatch.DrawString(_debugFont, _player.CurrentStateName,  new Vector2(8,  8), Color.White);
        _spriteBatch.DrawString(_debugFont, _player.CurrentActionName, new Vector2(8, 24), Color.White);
        _spriteBatch.DrawString(_debugFont,
            $"Planner: {EruptionPlanner.CurrentMode}  (P to toggle)",
            new Vector2(8, 40),
            EruptionPlanner.CurrentMode == EruptionPlannerMode.MassBall ? Color.LightCoral : Color.LightSkyBlue);

        // Diagnostic overlay for active GuidedPath: shows planner's actual
        // start/end positions and the body's position so we can compare.
        if (_config.DebugDrawGuidedPath
            && _player.CurrentState is GuidedState gs2 && gs2.ActivePath != null)
        {
            var p   = gs2.ActivePath;
            var sp  = p.Sample(0f);
            var ep  = p.Sample(1f);
            var bp  = _player.Body.Position;
            string info =
                $"path start ({sp.X:F0},{sp.Y:F0})\n" +
                $"path end   ({ep.X:F0},{ep.Y:F0})\n" +
                $"body       ({bp.X:F0},{bp.Y:F0})\n" +
                $"progressT  {gs2.CurrentProgressT:F2}";
            _spriteBatch.DrawString(_debugFont, info, new Vector2(8, 30), Color.Yellow);
        }

        _spriteBatch.End();

        base.Draw(gameTime);
    }

    // Drag-to-build: while right-click is held, every cell the cursor sweeps
    // through is fed to TryRequestTile in path order. Cells touching a Solid
    // neighbour start growing immediately; the rest pile into the graph as
    // Pending and wake up when their first parent finalizes. Reach is measured
    // from the player center to each candidate cell so a fast drag arc that
    // strays out of range silently drops the out-of-range cells but resumes
    // requests as it swings back in.
    private const float BuildReach = 64f;
    // Multiplier on BuildReach when the candidate cell has a sprout neighbour
    // (Pending or Growing) — i.e. it's chaining off an existing placement.
    // Lets the player extend a build outward beyond their normal reach without
    // having to re-anchor near the player.
    private const float ChainBuildReachMul = 2f;
    private void HandleBuildInput()
    {
        var input = _controller.Current;
        if (!input.RightClick) return;
        // RMB is committed to the block-eruption gesture while either of its
        // action states is current — suppress the drag-to-build path so the
        // eruption can claim the input cleanly.
        if (_player.CurrentAction is BlockReadyAction or BlockEruptionAction) return;

        var prev = _controller.GetPrevious(1);
        var segStart = prev.RightClick ? prev.MouseWorldPosition : input.MouseWorldPosition;
        var segEnd   = input.MouseWorldPosition;

        foreach (var (gtx, gty) in MouseSweep.Cells(segStart, segEnd))
        {
            var cellCenter = new Vector2(
                gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
                gty * Chunk.TileSize + Chunk.TileSize * 0.5f);
            // Chain-extended reach when any 4-neighbour is already a sprout
            // (Pending or Growing). TryRequestTile registers each placed cell
            // in the graph immediately, so a single drag's later cells see
            // earlier ones as parents and inherit the extended reach.
            float maxReach = HasSproutNeighbour(gtx, gty) ? BuildReach * ChainBuildReachMul : BuildReach;
            if (Vector2.DistanceSquared(_player.Body.Position, cellCenter) > maxReach * maxReach)
                continue;
            _chunks.TryRequestTile(gtx, gty);
        }
    }

    private bool HasSproutNeighbour(int gtx, int gty) =>
        _chunks.Graph.TryGet(gtx,     gty + 1, out _) ||
        _chunks.Graph.TryGet(gtx - 1, gty,     out _) ||
        _chunks.Graph.TryGet(gtx + 1, gty,     out _) ||
        _chunks.Graph.TryGet(gtx,     gty - 1, out _);

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
                // Damage fraction is scaled by the cell's *type-specific* max HP so a
                // mostly-broken sand tile reads as visibly damaged even though its
                // absolute HP is lower than a fresh stone tile.
                float dmgFrac = MathF.Min(_chunks.Damage.Get(gtx, gty) / TileDamage.MaxHPFor(type), 1f);
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

    // Screen-space HP indicator in the upper-left, beneath the state labels.
    // 120 px wide, color shifts green→red as HP drops; flashes when invuln so
    // the player can read "I just got hit, I'm safe for a moment."
    private void DrawPlayerHealthBar()
    {
        const int X = 8, Y = 56, BarW = 120, BarH = 8;
        float frac = MathHelper.Clamp(_player.Health / _player.MaxHealth, 0f, 1f);
        _spriteBatch.Draw(_pixel, new Rectangle(X, Y, BarW, BarH), new Color(40, 40, 40));
        _spriteBatch.Draw(_pixel, new Rectangle(X, Y, (int)(BarW * frac), BarH),
            Color.Lerp(Color.Red, Color.LimeGreen, frac));
        _spriteBatch.DrawString(_debugFont,
            $"HP {_player.Health:F1}/{_player.MaxHealth:F1}",
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

    // Translucent fill + crisp outline. A slash hitbox is only live for a handful
    // of frames; the outline keeps the AABB extent legible even on a single-frame
    // flash, while the fill makes overlap with tiles/entities visually obvious.
    // Color is owned by the publisher (Hitbox.DebugColor) so different actions
    // (ground slash red, air slash blue, future enemy attacks etc.) read at a glance.
    private void DrawHitbox(Hitbox hb)
    {
        var color = hb.DebugColor;
        var rect = new Rectangle(
            (int)hb.Region.Left, (int)hb.Region.Top,
            (int)(hb.Region.Right - hb.Region.Left),
            (int)(hb.Region.Bottom - hb.Region.Top));

        if (hb.Shape != null)
        {
            // Diagonal/rotated hitbox: outline the actual polygon (matches narrow-phase
            // intersection) and show the broad-phase AABB faintly so it's still visible
            // when the rotation is nearly axis-aligned.
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

    // Defensive region outline. Distinct color from hitboxes (cyan) so the two
    // overlays are readable side-by-side. Hurtboxes are always axis-aligned AABBs,
    // so no polygon path is needed.
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

    // Visualize the active GuidedPath: yellow polyline for the future portion,
    // dimmed for the already-traversed portion, plus colored dots at the start
    // (green), end (red), and the body's current projected position on the path
    // (magenta). Path velocity at the endpoint is drawn as a short red line so
    // you can see the planned exit direction.
    private void DrawGuidedPath(GuidedPath path, float progressT)
    {
        const int Samples = 48;
        var prev = path.Sample(0f);
        for (int i = 1; i <= Samples; i++)
        {
            float t = i / (float)Samples;
            var pt = path.Sample(t);
            Color c = t <= progressT ? new Color(80, 80, 30) : Color.Yellow;
            DrawLine(prev, pt, c, 1);
            prev = pt;
        }

        var startPos = path.Sample(0f);
        var endPos   = path.Sample(1f);
        var endVel   = path.SampleVelocity(1f);

        _spriteBatch.Draw(_pixel,
            new Rectangle((int)startPos.X - 3, (int)startPos.Y - 3, 7, 7),
            Color.LimeGreen);
        _spriteBatch.Draw(_pixel,
            new Rectangle((int)endPos.X - 3, (int)endPos.Y - 3, 7, 7),
            Color.Red);

        // End velocity tangent (scaled to ~0.1s of motion at goal speed)
        if (endVel.LengthSquared() > 1f)
            DrawLine(endPos, endPos + endVel * 0.1f, Color.Red, 1);

        var here = path.Sample(progressT);
        _spriteBatch.Draw(_pixel,
            new Rectangle((int)here.X - 2, (int)here.Y - 2, 5, 5),
            Color.Magenta);
    }
}
