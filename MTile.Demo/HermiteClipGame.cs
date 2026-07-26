using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MTile;

namespace MTileDemo;

// Hermite reference-clip editor (BALLISTIC_CORRECTOR_PLAN §1): author the 2D parametric
// arc p(t) a maneuver hands the ballistic corrector as its feel reference. Everything is
// in the NORMALIZED frame — entry pinned at (0,0), gate pinned at (1,-1), y-down — drawn
// over a unit-ledge silhouette so the arc reads against the obstacle it will be
// retargeted onto. Parametric, so vertical phases author fine.
//
//   dotnet run --project MTile.Demo -- --ref vault
//
// Loads/saves ReferenceClips/<name>.json at the repo root (created if missing).
//
//   • Drag a key        — move it (endpoint positions are pinned; their tangents edit).
//   • Drag a handle tip — set the key's tangent vector (direction AND magnitude).
//   • A                 — add a key at the nearest curve point (no shape pop).
//   • X / Delete        — delete the hovered interior key.
//   • Wheel / right-drag / Home — zoom at cursor / pan / fit.
//   • R                 — revert to the file on disk.  Ctrl-S — save.  H — help.
public sealed class HermiteClipGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;
    private Texture2D   _pixel;
    private SpriteFont  _font;
    private DrawContext _draw;

    private readonly string _clipName;
    private string _jsonPath;
    private HermiteClipDocument _doc;

    // View: screen = norm * _zoom + _off (zoom = pixels per normalized unit; y-down in both).
    private float   _zoom = 400f;
    private Vector2 _off;
    private bool    _panning;

    private int  _dragKey = -1, _hoverKey = -1;
    private int  _dragHandle = -1, _hoverHandle = -1;   // key index; side in _handleSide
    private int  _handleSide = 1;                        // +1 = outgoing tip, -1 = incoming tip
    private bool _dirty, _showHelp;

    private MouseState    _prevMs;
    private KeyboardState _prevKb;

    private const float PickR = 12f;
    private const float HandleK = 0.25f;   // normalized units drawn per unit of tangent
    private const float TanMin = 0.05f, TanMax = 8f;

    private string _shotPath;
    private int    _shotFrame;

    private int W => GraphicsDevice.Viewport.Width;
    private int H => GraphicsDevice.Viewport.Height;

    public HermiteClipGame(string clipName)
    {
        _clipName = clipName;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = 1200,
            PreferredBackBufferHeight = 760,
        };
        IsMouseVisible = true;
        Content.RootDirectory = "Content";
        Window.AllowUserResizing = true;
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _font = Content.Load<SpriteFont>("DebugFont");
        _draw = new DrawContext(_spriteBatch, _pixel);

        string root = FindRepoRoot();
        _jsonPath = Path.Combine(root, "ReferenceClips", _clipName + ".json");

        _doc = HermiteClipDocument.Load(_jsonPath);
        if (_doc != null)
        {
            Console.WriteLine($"Hermite clip editor - loaded {_jsonPath}");
        }
        else
        {
            _doc = HermiteClipDocument.NewDefault(_clipName);
            _dirty = true;
            Console.WriteLine($"Hermite clip editor - NEW clip '{_clipName}' (Ctrl-S writes {_jsonPath})");
        }

        FitView();

        // Dev screenshot mode (same contract as DemoGame/BindGame): MTILE_SHOT=path
        // captures one frame and exits.
        _shotPath = Environment.GetEnvironmentVariable("MTILE_SHOT");
    }

    // === coordinate frames ====================================================

    private Vector2 ToScreen(Vector2 n) => n * _zoom + _off;
    private Vector2 ToNorm(Vector2 s) => (s - _off) / _zoom;

    // Fit the design box (arc frame + silhouette + margins) into the window.
    private void FitView()
    {
        Vector2 min = new(-0.6f, -1.8f), max = new(1.9f, 0.7f);
        _zoom = MathF.Min((W - 80f) / (max.X - min.X), (H - 140f) / (max.Y - min.Y));
        Vector2 c = (min + max) * 0.5f;
        _off = new Vector2(W * 0.5f, (H + 60f) * 0.5f) - c * _zoom;
    }

    protected override void Update(GameTime gameTime)
    {
        var ms = Mouse.GetState();
        var kb = Keyboard.GetState();
        var mp = new Vector2(ms.X, ms.Y);
        bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);

        if (Pressed(kb, Keys.Escape)) Exit();
        if (Pressed(kb, Keys.H))    _showHelp = !_showHelp;
        if (Pressed(kb, Keys.Home)) FitView();
        if (ctrl && Pressed(kb, Keys.S)) Save();
        if (Pressed(kb, Keys.R)) Revert();
        if (Pressed(kb, Keys.A)) AddKeyNear(ToNorm(mp));
        if ((Pressed(kb, Keys.X) || Pressed(kb, Keys.Delete)) && _hoverKey > 0 && _hoverKey < _doc.Keys.Count - 1)
        {
            _doc.Keys.RemoveAt(_hoverKey);
            _doc.RederiveT();
            _hoverKey = -1; _dirty = true;
        }

        int wheel = ms.ScrollWheelValue - _prevMs.ScrollWheelValue;
        if (wheel != 0)
        {
            float nz = MathHelper.Clamp(_zoom * MathF.Pow(1.0015f, wheel), 40f, 8000f);
            _off = mp + (_off - mp) * (nz / _zoom);   // keep the cursor's point fixed
            _zoom = nz;
        }

        bool leftDown    = ms.LeftButton == ButtonState.Pressed;
        bool leftPressed = leftDown && _prevMs.LeftButton == ButtonState.Released;
        bool leftUp      = !leftDown && _prevMs.LeftButton == ButtonState.Pressed;
        bool rightDown   = ms.RightButton == ButtonState.Pressed || ms.MiddleButton == ButtonState.Pressed;

        if (rightDown) { if (_panning) _off += mp - new Vector2(_prevMs.X, _prevMs.Y); _panning = true; }
        else _panning = false;

        if (_dragKey < 0 && _dragHandle < 0) Pick(mp);

        if (leftPressed)
        {
            if (_hoverKey >= 0) _dragKey = _hoverKey;
            else if (_hoverHandle >= 0) _dragHandle = _hoverHandle;
        }

        if (leftDown && _dragKey >= 0) DragKey(_dragKey, ToNorm(mp));
        else if (leftDown && _dragHandle >= 0) DragHandle(_dragHandle, ToNorm(mp));
        if (leftUp) { _dragKey = -1; _dragHandle = -1; }

        _prevMs = ms; _prevKb = kb;
        base.Update(gameTime);
    }

    // Keys win over handle tips; handle side remembered so the incoming tip drags naturally.
    private void Pick(Vector2 mp)
    {
        _hoverKey = -1; _hoverHandle = -1;
        float bestD = PickR * PickR;
        for (int i = 0; i < _doc.Keys.Count; i++)
        {
            float d = Vector2.DistanceSquared(ToScreen(_doc.Keys[i].Pos), mp);
            if (d < bestD) { bestD = d; _hoverKey = i; }
        }
        if (_hoverKey >= 0) return;

        bestD = PickR * PickR;
        for (int i = 0; i < _doc.Keys.Count; i++)
        {
            for (int s = -1; s <= 1; s += 2)
            {
                float d = Vector2.DistanceSquared(HandleTip(i, s), mp);
                if (d < bestD) { bestD = d; _hoverHandle = i; _handleSide = s; }
            }
        }
    }

    private Vector2 HandleTip(int i, int side)
    {
        var tan = _doc.Keys[i].Tan;
        if (tan.LengthSquared() < 1e-6f) tan = Vector2.UnitX;
        return ToScreen(_doc.Keys[i].Pos + tan * (HandleK * side));
    }

    // Endpoint positions are the frame convention (entry (0,0), gate (1,-1)) — pinned.
    private void DragKey(int i, Vector2 n)
    {
        if (i == 0 || i == _doc.Keys.Count - 1) return;
        _doc.Keys[i].Pos = n;
        _doc.RederiveT();
        _dirty = true;
    }

    private void DragHandle(int i, Vector2 n)
    {
        Vector2 tan = (n - _doc.Keys[i].Pos) * _handleSide / HandleK;
        float len = tan.Length();
        if (len < 1e-4f) return;
        tan *= MathHelper.Clamp(len, TanMin, TanMax) / len;
        _doc.Keys[i].Tan = tan;
        _dirty = true;
    }

    // New key sits ON the current curve at the nearest point (position + tangent
    // sampled), so adding never pops the shape.
    private void AddKeyNear(Vector2 n)
    {
        const int Samples = 256;
        float bestT = -1f, bestD = float.MaxValue;
        for (int i = 0; i <= Samples; i++)
        {
            float t = i / (float)Samples;
            float d = Vector2.DistanceSquared(_doc.Eval(t), n);
            if (d < bestD) { bestD = d; bestT = t; }
        }
        // Refuse right on top of an existing key.
        foreach (var k in _doc.Keys)
            if (Vector2.DistanceSquared(_doc.Eval(bestT), k.Pos) < 1e-4f) return;

        int insert = 1;
        while (insert < _doc.Keys.Count - 1 && _doc.Keys[insert].T < bestT) insert++;
        _doc.Keys.Insert(insert, new HermiteClipKey
        {
            Pos = _doc.Eval(bestT),
            Tan = _doc.EvalTangent(bestT),
        });
        _doc.RederiveT();
        _dirty = true;
    }

    private void Save()
    {
        _doc.Save(_jsonPath);
        _dirty = false;
        Console.WriteLine($"saved {_jsonPath}");
    }

    private void Revert()
    {
        var fresh = HermiteClipDocument.Load(_jsonPath);
        if (fresh == null) return;
        _doc = fresh;
        _dragKey = _dragHandle = _hoverKey = _hoverHandle = -1;
        _dirty = false;
        Console.WriteLine($"reverted to {_jsonPath}");
    }

    // === draw =================================================================

    protected override void Draw(GameTime gameTime)
    {
        _shotFrame++;
        bool capturing = _shotPath != null && _shotFrame >= 10;
        RenderTarget2D rt = null;
        if (capturing)
        {
            var pp = GraphicsDevice.PresentationParameters;
            rt = new RenderTarget2D(GraphicsDevice, pp.BackBufferWidth, pp.BackBufferHeight);
            GraphicsDevice.SetRenderTarget(rt);
        }

        GraphicsDevice.Clear(new Color(22, 24, 30));
        _spriteBatch.Begin();

        DrawSilhouette();
        DrawGrid();
        DrawCurve();
        DrawKeys();
        DrawHeader();
        DrawHelpOverlay();

        _spriteBatch.End();

        if (capturing)
        {
            GraphicsDevice.SetRenderTarget(null);
            try { using var fs = File.Create(_shotPath); rt.SaveAsPng(fs, rt.Width, rt.Height); } catch { }
            rt.Dispose();
            Exit();
        }
        base.Draw(gameTime);
    }

    // Unit-ledge backdrop: approach floor at y=0 (x<1), ledge top at y=-1 (x>=1) —
    // the generic obstacle the normalized frame is defined against.
    private void DrawSilhouette()
    {
        var solid = new Color(38, 42, 52);
        RectNorm(new Vector2(-0.6f, 0f), new Vector2(1f, 0.7f), solid);
        RectNorm(new Vector2(1f, -1f), new Vector2(1.9f, 0.7f), solid);
        _spriteBatch.DrawString(_font, "entry (0,0)", ToScreen(new Vector2(0f, 0.08f)), new Color(120, 130, 150));
        _spriteBatch.DrawString(_font, "gate (1,-1)", ToScreen(new Vector2(1.02f, -1.12f)), new Color(120, 130, 150));
    }

    private void RectNorm(Vector2 min, Vector2 max, Color c)
    {
        Vector2 a = ToScreen(min), b = ToScreen(max);
        _spriteBatch.Draw(_pixel, new Rectangle((int)a.X, (int)a.Y, (int)(b.X - a.X), (int)(b.Y - a.Y)), c);
    }

    private void DrawGrid()
    {
        var minor = new Color(40, 44, 56);
        var major = new Color(62, 68, 84);
        for (float v = -2f; v <= 2.5f; v += 0.25f)
        {
            bool onUnit = MathF.Abs(v - MathF.Round(v)) < 1e-3f;
            var c = onUnit ? major : minor;
            _draw.Line(ToScreen(new Vector2(-0.6f, v)), ToScreen(new Vector2(1.9f, v)), c);
            if (v >= -0.6f && v <= 1.9f)
                _draw.Line(ToScreen(new Vector2(v, -2f)), ToScreen(new Vector2(v, 2.5f)), c);
        }
        // Frame anchors.
        _draw.Ring(ToScreen(Vector2.Zero), 4f, new Color(120, 130, 150), 12, 1.5f);
        _draw.Ring(ToScreen(new Vector2(1f, -1f)), 4f, new Color(120, 130, 150), 12, 1.5f);
    }

    private void DrawCurve()
    {
        const int Samples = 200;
        const float Pad = 0.12f;
        Vector2 prev = ToScreen(_doc.Eval(-Pad));
        for (int i = 1; i <= Samples; i++)
        {
            float t = MathHelper.Lerp(-Pad, 1f + Pad, i / (float)Samples);
            Vector2 p = ToScreen(_doc.Eval(t));
            bool inside = t >= 0f && t <= 1f;
            _draw.Line(prev, p, inside ? new Color(110, 210, 255) : new Color(60, 100, 120), inside ? 2f : 1f);
            prev = p;
        }
        // Parameter ticks every 0.1 t — read local speed as tick spacing.
        for (int i = 1; i < 10; i++)
        {
            float t = i / 10f;
            Vector2 tan = _doc.EvalTangent(t);
            if (tan.LengthSquared() < 1e-6f) continue;
            Vector2 nrm = Vector2.Normalize(new Vector2(-tan.Y, tan.X)) * 3f;
            Vector2 p = ToScreen(_doc.Eval(t));
            _draw.Line(p - nrm, p + nrm, new Color(80, 150, 180));
        }
    }

    private void DrawKeys()
    {
        for (int i = 0; i < _doc.Keys.Count; i++)
        {
            Vector2 p = ToScreen(_doc.Keys[i].Pos);
            Vector2 tipOut = HandleTip(i, +1), tipIn = HandleTip(i, -1);
            var handleC = (i == _hoverHandle || i == _dragHandle) ? Color.LightYellow : new Color(150, 150, 160);
            _draw.Line(tipIn, tipOut, handleC);
            _draw.Disc(tipOut, 3.5f, handleC);
            _draw.Disc(tipIn, 3.5f, handleC);

            bool endpoint = i == 0 || i == _doc.Keys.Count - 1;
            if (i == _dragKey)       _draw.Disc(p, 6f, Color.White);
            else if (i == _hoverKey) _draw.Disc(p, 6f, Color.LightYellow);
            else _draw.Disc(p, 5f, endpoint ? Color.Yellow : Color.OrangeRed);
        }
    }

    private void DrawHeader()
    {
        var mp = ToNorm(new Vector2(_prevMs.X, _prevMs.Y));
        int shown = _hoverKey >= 0 ? _hoverKey : (_hoverHandle >= 0 ? _hoverHandle : -1);
        string keyInfo = shown >= 0
            ? $"key {shown}: pos ({_doc.Keys[shown].X:0.00}, {_doc.Keys[shown].Y:0.00})  tan ({_doc.Keys[shown].TX:0.00}, {_doc.Keys[shown].TY:0.00})  t {_doc.Keys[shown].T:0.00}"
            : $"cursor ({mp.X:0.00}, {mp.Y:0.00})";
        _spriteBatch.DrawString(_font,
            $"REF CLIP {_clipName}{(_dirty ? "  *unsaved*" : "")}",
            new Vector2(16, 10), _dirty ? Color.Orange : Color.White);
        _spriteBatch.DrawString(_font,
            $"{_doc.Keys.Count} keys   |   {keyInfo}",
            new Vector2(16, 28), new Color(160, 170, 185));
        _spriteBatch.DrawString(_font,
            "A add key   X delete   Ctrl-S save   R revert   H help",
            new Vector2(16, 46), new Color(130, 140, 155));
    }

    private static readonly (string Group, string Keys)[] HelpRows =
    {
        ("Edit", "drag key = move (endpoint positions pinned at (0,0)/(1,-1))    drag handle tip = tangent (dir + magnitude)"),
        ("Keys", "A = add key at nearest curve point    X / Del = delete hovered interior key"),
        ("View", "wheel = zoom at cursor    right/middle-drag = pan    Home = fit"),
        ("File", "Ctrl-S save json    R revert to disk    Esc quit"),
    };

    private void DrawHelpOverlay()
    {
        if (!_showHelp) return;
        int x = 30, y = 110;
        var panel = new Rectangle(x - 14, y - 30, W - 60, HelpRows.Length * 28 + 50);
        _spriteBatch.Draw(_pixel, panel, new Color(16, 18, 26, 240));
        _spriteBatch.DrawString(_font, "REF CLIP CONTROLS", new Vector2(x, y - 22), new Color(150, 200, 255));
        for (int i = 0; i < HelpRows.Length; i++)
        {
            int ry = y + 8 + i * 28;
            _spriteBatch.DrawString(_font, HelpRows[i].Group, new Vector2(x, ry), new Color(255, 200, 120));
            _spriteBatch.DrawString(_font, HelpRows[i].Keys,  new Vector2(x + 90, ry), new Color(205, 210, 220));
        }
    }

    private bool Pressed(KeyboardState kb, Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "MTile.sln"))) return d.FullName;
            d = d.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
