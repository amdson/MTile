using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTile;

public class Game1 : Game
{
    private GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;
    private Texture2D _pixel;

    private readonly List<PhysicsBody> _bodies = new();
    private PlayerCharacter _player;

    private static readonly Vector2 Gravity = new(0f, 300f);
    private readonly ChunkMap _chunks = new();
    private readonly Camera _camera = new();
    private readonly Controller _controller = new();

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
        for (int cx = -2; cx <= 2; cx++)
            for (int cy = -1; cy <= 1; cy++)
            {
                var chunk = new Chunk { ChunkPos = new Point(cx, cy) };
                for (int tx = 0; tx < Chunk.Size; tx++)
                    for (int ty = 0; ty < Chunk.Size; ty++) {
                        chunk.Tiles[tx, ty].IsSolid = (cy * Chunk.Size + ty) >= 0;
                        chunk.Tiles[tx, ty].IsSolid = chunk.Tiles[tx, ty].IsSolid || ((cx == -1) && (tx < Chunk.Size / 2) && (cy * Chunk.Size + ty) <= -10);
                        chunk.Tiles[tx, ty].IsSolid = chunk.Tiles[tx, ty].IsSolid || (cx < -1);
                    }
                _chunks[new Point(cx, cy)] = chunk;
            }

        _player = new PlayerCharacter(new Vector2(0f, -200f));
        _bodies.Add(_player.Body);

        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
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

        _player.Update(_controller.Current, _chunks, dt);

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

        _spriteBatch.End();

        _spriteBatch.Begin();
        var mousePos = _controller.Current.MousePosition;
        _spriteBatch.Draw(_pixel, new Rectangle(mousePos.X - 2, mousePos.Y - 2, 5, 5), Color.Red);
        _spriteBatch.End();

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _pixel?.Dispose();
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
}
