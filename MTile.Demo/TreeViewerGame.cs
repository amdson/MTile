using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MTile;

namespace MTileDemo;

// Fast tree-backdrop viewer:   dotnet run --project MTile.Demo -- --trees
//
// Boots straight to the TreeParallaxBackground — no sim, no clips, no stage —
// so a tuning iteration is just the backdrop bake (~0.3 s of the game's ~2 s boot).
//
// Live C# tuning loop (hot reload):
//     dotnet watch run --project MTile.Demo --non-interactive -- --trees
// Edit Drawing/TreeParallaxBackground.cs (layer tables, fog, rays, mist...), save,
// then press R in the window: hot reload patches the class in place, and R builds a
// FRESH TreeParallaxBackground so the edited field initializers (_layers, _mists,
// knob defaults) actually re-run and the strips re-bake. Edits that change type
// layout (new struct fields etc.) are "rude" — dotnet watch restarts instead, which
// still lands in a couple of seconds here.
//
// Controls:  WASD / arrows pan (Shift = 4x)   R rebuild   Home recenter   Esc quit
// MTILE_SHOT=<path.png> captures frame 20 headlessly and exits (same idea as the
// game's MTILE_SCREENSHOT).
public sealed class TreeViewerGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _sb;
    private Texture2D   _pixel;
    private TreeParallaxBackground _bg;
    private readonly Camera _camera = new() { Zoom = 1f };
    private KeyboardState _prevKb;
    private string _shotPath;
    private int _frame;

    public TreeViewerGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = 1000,
            PreferredBackBufferHeight = 900,
        };
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "MTile tree backdrop viewer";
    }

    protected override void LoadContent()
    {
        _sb    = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _shotPath = Environment.GetEnvironmentVariable("MTILE_SHOT");
        // Vertical parallax tracks camY * Zoom (see TreeParallaxBackground.ParallaxY),
        // so previewing with the game's configured zoom keeps the motion honest.
        _camera.Zoom = GameConfig.Load(Path.Combine(RepoRoot(), "game_config.json")).CameraZoom;
        BuildBackground();
    }

    // Same art set + order as Game1's tree loader (TreeType indices must match).
    private void BuildBackground()
    {
        _bg?.Dispose();
        string root = RepoRoot();
        var treeNames = new[] { "tree.png", "redwood_tree.png", "deadwood_tree.png", "trees_all_layers.png" };
        var texs = new List<Texture2D>();
        foreach (var name in treeNames)
        {
            string path = Path.Combine(root, "Assets", "trees_small", name);
            if (!File.Exists(path) && name == "tree.png")
                path = Path.Combine(root, "Assets", "tree.png");
            if (!File.Exists(path)) continue;
            using var fs = File.OpenRead(path);
            texs.Add(Texture2D.FromStream(GraphicsDevice, fs));
        }
        if (texs.Count == 0)
            throw new FileNotFoundException("No tree art found under " + Path.Combine(root, "Assets"));
        _bg = new TreeParallaxBackground(GraphicsDevice, texs.ToArray(), _pixel);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MTile.sln"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    protected override void Update(GameTime gameTime)
    {
        var kb = Keyboard.GetState();
        if (kb.IsKeyDown(Keys.Escape)) Exit();

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        float speed = 600f * (kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift) ? 4f : 1f);
        if (kb.IsKeyDown(Keys.A) || kb.IsKeyDown(Keys.Left))  _camera.Position.X -= speed * dt;
        if (kb.IsKeyDown(Keys.D) || kb.IsKeyDown(Keys.Right)) _camera.Position.X += speed * dt;
        if (kb.IsKeyDown(Keys.W) || kb.IsKeyDown(Keys.Up))    _camera.Position.Y -= speed * dt;
        if (kb.IsKeyDown(Keys.S) || kb.IsKeyDown(Keys.Down))  _camera.Position.Y += speed * dt;
        if (kb.IsKeyDown(Keys.Home)) _camera.Position = Vector2.Zero;

        if (kb.IsKeyDown(Keys.R) && _prevKb.IsKeyUp(Keys.R))
            BuildBackground();

        Window.Title = $"MTile tree backdrop viewer — cam ({_camera.Position.X:F0}, {_camera.Position.Y:F0})   R rebuild";
        _prevKb = kb;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        var pp = GraphicsDevice.PresentationParameters;
        _bg.Draw(_sb, _camera, new Vector2(pp.BackBufferWidth / 2f, pp.BackBufferHeight / 2f));
        base.Draw(gameTime);

        if (_shotPath != null && ++_frame == 20)
        {
            int w = pp.BackBufferWidth, h = pp.BackBufferHeight;
            var data = new Color[w * h];
            GraphicsDevice.GetBackBufferData(data);
            using (var tex = new Texture2D(GraphicsDevice, w, h))
            {
                tex.SetData(data);
                using var fs = File.Create(_shotPath);
                tex.SaveAsPng(fs, w, h);
            }
            Exit();
        }
    }

    protected override void UnloadContent()
    {
        _bg?.Dispose();
        _pixel?.Dispose();
        base.UnloadContent();
    }
}
