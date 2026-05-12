using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTile;

public class Game1 : Game
{
    private GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;
    private Texture2D _pixel;
    private SpriteFont _debugFont;

    private readonly List<PhysicsBody> _bodies = new();
    private PlayerCharacter _player;

    private static readonly Vector2 Gravity = new(0f, 600f);
    private readonly ChunkMap _chunks = new();
    private readonly Camera _camera = new();
    private readonly Controller _controller = new();
    
    private FileSystemWatcher _movementConfigWatcher;

    public bool DebugDrawConstraints = true;

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 30.0);
    }

    protected override void Initialize()
    {
        string terrainConfigPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Levels", "terrain.json");
        TerrainLoader.Load(terrainConfigPath, _chunks);

        string configPath = Path.GetFullPath("movement_config.json");
        MovementConfig.Load(configPath);
        
        _movementConfigWatcher = new FileSystemWatcher(Path.GetDirectoryName(configPath))
        {
            Filter = Path.GetFileName(configPath),
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _movementConfigWatcher.Changed += (s, e) =>
        {
            System.Threading.Thread.Sleep(50);
            MovementConfig.Load(configPath);
        };

        _player = new PlayerCharacter(new Vector2(0f, -200f));
        _bodies.Add(_player.Body);

        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _debugFont = Content.Load<SpriteFont>("DebugFont");
    }

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

        _controller.Update(mouseWorldPos);

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        _player.Update(_controller, _chunks, dt);

        PhysicsWorld.StepSwept(_bodies, _chunks, dt, Gravity);

        _camera.TrackTarget(_player.Body.Position, screenCenter);

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

        DrawPolygon(_player.Body.Polygon, _player.Body.Position,
            _player.IsGrounded ? Color.LimeGreen : Color.Orange);

        if (DebugDrawConstraints)
            foreach (var body in _bodies)
                foreach (var c in body.Constraints)
                    if (c is SurfaceContact sc)
                        DrawConstraintArrow(sc.Position, sc.Normal,
                            c is FloatingSurfaceDistance ? Color.Cyan : Color.Yellow);

        if (_player.CurrentState is GuidedState gs && gs.ActivePath != null)
            DrawGuidedPath(gs.ActivePath, gs.CurrentProgressT);

        _spriteBatch.End();

        _spriteBatch.Begin();
        var mousePos = _controller.Current.MousePosition;
        _spriteBatch.Draw(_pixel, new Rectangle(mousePos.X - 2, mousePos.Y - 2, 5, 5), Color.Red);
        _spriteBatch.DrawString(_debugFont, _player.CurrentStateName, new Vector2(8, 8), Color.White);

        // Diagnostic overlay for active GuidedPath: shows planner's actual
        // start/end positions and the body's position so we can compare.
        if (_player.CurrentState is GuidedState gs2 && gs2.ActivePath != null)
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
                _spriteBatch.Draw(_pixel, new Rectangle(
                    (int)(origin.X + tx * Chunk.TileSize),
                    (int)(origin.Y + ty * Chunk.TileSize),
                    Chunk.TileSize - 1, Chunk.TileSize - 1), Color.Gray);
            }
    }

    private void DrawPolygon(Polygon polygon, Vector2 position, Color color)
    {
        var verts = polygon.GetVertices(position);
        for (int i = 0; i < verts.Length; i++)
            DrawLine(verts[i], verts[(i + 1) % verts.Length], color);
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
