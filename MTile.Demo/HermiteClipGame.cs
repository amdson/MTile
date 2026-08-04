using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MTile;

namespace MTileDemo;

// Hermite reference-clip editor (BALLISTIC_CORRECTOR_PLAN §1): author the 2D parametric
// arc p(t) a maneuver hands the ballistic corrector as its feel reference.
//
// Everything is in GAME PIXELS (y-down), authored against a reference obstacle sized by
// the clip's two ANCHORS: the Entry anchor binds to the body's pose when the maneuver
// starts, the Gate anchor to the measured gate. The runtime normalizes by the anchor
// span and rescales onto the real obstacle, so the box size is a readability choice —
// but keys are free, including before the entry or past the gate.
//
//   dotnet run --project MTile.Demo -- --ref parkour
//
// Loads/saves ReferenceClips/<name>.json at the repo root (created if missing).
//
//   • Drag a key        — move it (all keys, endpoints included).
//   • Drag an anchor    — resize/reframe the reference obstacle.
//   • Drag a handle tip — set the key's tangent vector (direction AND magnitude).
//   • Shift             — snap the drag to whole pixels.
//   • A                 — add a key at the nearest curve point (no shape pop).
//   • X / Delete        — delete the hovered interior key.
//   • [ / ]             — arc duration (seconds end to end; what animation clips pace against).
//   • U                 — convert a legacy normalized clip to a pixel box.
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

    // View: screen = px * _zoom + _off (zoom = screen pixels per game pixel; y-down in both).
    private float   _zoom = 12f;
    private Vector2 _off;
    private bool    _panning;

    private int  _dragKey = -1, _hoverKey = -1;
    private int  _dragHandle = -1, _hoverHandle = -1;   // key index; side in _handleSide
    private int  _dragAnchor = -1, _hoverAnchor = -1;   // 0 = entry, 1 = gate
    private int  _handleSide = 1;                        // +1 = outgoing tip, -1 = incoming tip
    private bool _dirty, _showHelp;

    private MouseState    _prevMs;
    private KeyboardState _prevKb;

    private const float PickR = 12f;
    private const float HandleK = 0.25f;   // fraction of the tangent vector drawn as a handle
    // Tangent magnitude bounds, as multiples of the anchor span length — the same
    // shape limits the old normalized editor had, now scale-free.
    private const float TanMinFrac = 0.05f, TanMaxFrac = 8f;
    // Conversion box for a legacy normalized clip (U). Any size maps identically.
    private const float LegacyW = 26f, LegacyH = 40f;

    private MouseState    _ms;
    private KeyboardState _kb;

    private string _shotPath;
    private int    _shotFrame;

    private int W => GraphicsDevice.Viewport.Width;
    private int H => GraphicsDevice.Viewport.Height;

    private float SpanLen => MathF.Max(_doc.Span.Length(), 1e-3f);

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
            if (_doc.IsLegacyNormalized)
                Console.WriteLine("  legacy normalized clip (entry (0,0), gate (1,-1)) - press U to convert to pixels");
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

    private Vector2 ToScreen(Vector2 p) => p * _zoom + _off;
    private Vector2 ToClip(Vector2 s) => (s - _off) / _zoom;

    // Design box = the anchor rect plus the keys, with a margin of obstacle around it.
    private void FitView()
    {
        Vector2 min = Vector2.Min(_doc.Entry, _doc.Gate);
        Vector2 max = Vector2.Max(_doc.Entry, _doc.Gate);
        foreach (var k in _doc.Keys) { min = Vector2.Min(min, k.Pos); max = Vector2.Max(max, k.Pos); }
        Vector2 pad = new(MathF.Max((max.X - min.X) * 0.45f, 8f), MathF.Max((max.Y - min.Y) * 0.45f, 8f));
        min -= pad; max += pad;
        _zoom = MathHelper.Clamp(MathF.Min((W - 80f) / (max.X - min.X), (H - 140f) / (max.Y - min.Y)), 1f, 400f);
        Vector2 c = (min + max) * 0.5f;
        _off = new Vector2(W * 0.5f, (H + 60f) * 0.5f) - c * _zoom;
    }

    protected override void Update(GameTime gameTime)
    {
        _ms = Mouse.GetState();
        _kb = Keyboard.GetState();
        var mp = new Vector2(_ms.X, _ms.Y);
        bool ctrl = _kb.IsKeyDown(Keys.LeftControl) || _kb.IsKeyDown(Keys.RightControl);

        if (Pressed(Keys.Escape)) Exit();
        if (Pressed(Keys.H))    _showHelp = !_showHelp;
        if (Pressed(Keys.Home)) FitView();
        if (ctrl && Pressed(Keys.S)) Save();
        if (Pressed(Keys.R)) Revert();
        if (Pressed(Keys.U)) ConvertLegacy();
        // Duration in 0.05s steps, same convention as the animation editor's [ ].
        if (Pressed(Keys.OemOpenBrackets))  SetDuration(_doc.Duration - 0.05f);
        if (Pressed(Keys.OemCloseBrackets)) SetDuration(_doc.Duration + 0.05f);
        if (Pressed(Keys.A)) AddKeyNear(ToClip(mp));
        if ((Pressed(Keys.X) || Pressed(Keys.Delete)) && _hoverKey > 0 && _hoverKey < _doc.Keys.Count - 1)
        {
            _doc.Keys.RemoveAt(_hoverKey);
            _doc.RederiveT();
            _hoverKey = -1; _dirty = true;
        }

        int wheel = _ms.ScrollWheelValue - _prevMs.ScrollWheelValue;
        if (wheel != 0)
        {
            float nz = MathHelper.Clamp(_zoom * MathF.Pow(1.0015f, wheel), 0.5f, 400f);
            _off = mp + (_off - mp) * (nz / _zoom);   // keep the cursor's point fixed
            _zoom = nz;
        }

        bool leftDown    = _ms.LeftButton == ButtonState.Pressed;
        bool leftPressed = leftDown && _prevMs.LeftButton == ButtonState.Released;
        bool leftUp      = !leftDown && _prevMs.LeftButton == ButtonState.Pressed;
        bool rightDown   = _ms.RightButton == ButtonState.Pressed || _ms.MiddleButton == ButtonState.Pressed;

        if (rightDown) { if (_panning) _off += mp - new Vector2(_prevMs.X, _prevMs.Y); _panning = true; }
        else _panning = false;

        if (_dragKey < 0 && _dragHandle < 0 && _dragAnchor < 0) Pick(mp);

        if (leftPressed)
        {
            if (_hoverKey >= 0)         _dragKey = _hoverKey;
            else if (_hoverAnchor >= 0) _dragAnchor = _hoverAnchor;
            else if (_hoverHandle >= 0) _dragHandle = _hoverHandle;
        }

        if (leftDown && _dragKey >= 0)         DragKey(_dragKey, Snap(ToClip(mp)));
        else if (leftDown && _dragAnchor >= 0) DragAnchor(_dragAnchor, Snap(ToClip(mp)));
        else if (leftDown && _dragHandle >= 0) DragHandle(_dragHandle, ToClip(mp));
        if (leftUp) { _dragKey = -1; _dragHandle = -1; _dragAnchor = -1; }

        _prevMs = _ms; _prevKb = _kb;
        base.Update(gameTime);
    }

    private Vector2 Snap(Vector2 p)
        => (_kb.IsKeyDown(Keys.LeftShift) || _kb.IsKeyDown(Keys.RightShift))
            ? new Vector2(MathF.Round(p.X), MathF.Round(p.Y)) : p;

    // Keys win over anchors, anchors over handle tips; the handle side is remembered so
    // the incoming tip drags naturally.
    private void Pick(Vector2 mp)
    {
        _hoverKey = -1; _hoverHandle = -1; _hoverAnchor = -1;
        float bestD = PickR * PickR;
        for (int i = 0; i < _doc.Keys.Count; i++)
        {
            float d = Vector2.DistanceSquared(ToScreen(_doc.Keys[i].Pos), mp);
            if (d < bestD) { bestD = d; _hoverKey = i; }
        }
        if (_hoverKey >= 0) return;

        bestD = PickR * PickR;
        if (Vector2.DistanceSquared(ToScreen(_doc.Entry), mp) < bestD) _hoverAnchor = 0;
        if (Vector2.DistanceSquared(ToScreen(_doc.Gate),  mp) < bestD) _hoverAnchor = 1;
        if (_hoverAnchor >= 0) return;

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
        if (tan.LengthSquared() < 1e-6f) tan = Vector2.UnitX * SpanLen;
        return ToScreen(_doc.Keys[i].Pos + tan * (HandleK * side));
    }

    // Every key moves freely now — the retarget anchors are separate points, so an arc
    // may start behind the entry or overshoot past the gate.
    private void DragKey(int i, Vector2 p)
    {
        _doc.Keys[i].Pos = p;
        _doc.RederiveT();
        _dirty = true;
    }

    // Moving an anchor re-frames the obstacle: it changes what the arc's pixels MEAN
    // (the retarget normalizes by the anchor span), not the drawn curve.
    private void DragAnchor(int which, Vector2 p)
    {
        if (which == 0) _doc.Entry = p; else _doc.Gate = p;
        _dirty = true;
    }

    private void DragHandle(int i, Vector2 p)
    {
        Vector2 tan = (p - _doc.Keys[i].Pos) * _handleSide / HandleK;
        float len = tan.Length();
        if (len < 1e-4f) return;
        tan *= MathHelper.Clamp(len, TanMinFrac * SpanLen, TanMaxFrac * SpanLen) / len;
        _doc.Keys[i].Tan = tan;
        _dirty = true;
    }

    // New key sits ON the current curve at the nearest point (position + tangent
    // sampled), so adding never pops the shape.
    private void AddKeyNear(Vector2 p)
    {
        const int Samples = 256;
        float bestT = -1f, bestD = float.MaxValue;
        for (int i = 0; i <= Samples; i++)
        {
            float t = i / (float)Samples;
            float d = Vector2.DistanceSquared(_doc.Eval(t), p);
            if (d < bestD) { bestD = d; bestT = t; }
        }
        // Refuse right on top of an existing key.
        float eps = 1e-3f * SpanLen;
        foreach (var k in _doc.Keys)
            if (Vector2.DistanceSquared(_doc.Eval(bestT), k.Pos) < eps * eps) return;

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

    // Legacy normalized clip → pixel box. Pure rescale about the entry anchor, and the
    // key T values stay put, so the retargeted world arc is untouched.
    private void ConvertLegacy()
    {
        if (!_doc.IsLegacyNormalized) return;
        _doc.RescaleClipSpace(new Vector2(LegacyW, LegacyH));
        FitView();
        _dirty = true;
        Console.WriteLine($"converted to a {LegacyW}x{LegacyH}px box (arc unchanged) - Ctrl-S to keep");
    }

    private void SetDuration(float d)
    {
        _doc.Duration = MathF.Round(MathHelper.Clamp(d, 0.05f, 5f), 2);
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
        _dragKey = _dragHandle = _dragAnchor = _hoverKey = _hoverHandle = _hoverAnchor = -1;
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
        DrawAnchors();
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

    // Visible clip-space rect, so the grid and silhouette fill the window at any zoom.
    private (Vector2 Min, Vector2 Max) VisibleRect()
    {
        Vector2 a = ToClip(Vector2.Zero), b = ToClip(new Vector2(W, H));
        return (Vector2.Min(a, b), Vector2.Max(a, b));
    }

    // Reference obstacle: approach surface at the entry's height on the entry's side,
    // landing surface at the gate's height beyond it. Reads as a step up for a pull-up
    // and a step down for a drop, straight from where the anchors sit.
    private void DrawSilhouette()
    {
        var (vmin, vmax) = VisibleRect();
        var solid = new Color(38, 42, 52);
        Vector2 e = _doc.Entry, g = _doc.Gate;
        float xl = MathF.Min(e.X, g.X), xr = MathF.Max(e.X, g.X);
        // Which surface belongs on which side follows the direction of travel.
        float leftY  = e.X <= g.X ? e.Y : g.Y;
        float rightY = e.X <= g.X ? g.Y : e.Y;
        float split  = (xl + xr) * 0.5f;

        RectClip(new Vector2(vmin.X, leftY),  new Vector2(split,  vmax.Y), solid);
        RectClip(new Vector2(split,  rightY), new Vector2(vmax.X, vmax.Y), solid);
    }

    private void RectClip(Vector2 min, Vector2 max, Color c)
    {
        Vector2 a = ToScreen(min), b = ToScreen(max);
        if (b.X <= a.X || b.Y <= a.Y) return;
        _spriteBatch.Draw(_pixel, new Rectangle((int)a.X, (int)a.Y, (int)(b.X - a.X), (int)(b.Y - a.Y)), c);
    }

    // Pixel grid: minor lines every 4px, major on the tile lattice, so authored sizes
    // read against the units the game actually moves in.
    private void DrawGrid()
    {
        var (vmin, vmax) = VisibleRect();
        var minor = new Color(40, 44, 56);
        var major = new Color(62, 68, 84);
        float step = 4f;
        while (step * _zoom < 6f) step *= 4f;         // thin out when zoomed far out
        float tile = Chunk.TileSize;

        for (float x = MathF.Ceiling(vmin.X / step) * step; x <= vmax.X; x += step)
        {
            bool onTile = MathF.Abs(x / tile - MathF.Round(x / tile)) < 1e-3f;
            _draw.Line(ToScreen(new Vector2(x, vmin.Y)), ToScreen(new Vector2(x, vmax.Y)), onTile ? major : minor);
        }
        for (float y = MathF.Ceiling(vmin.Y / step) * step; y <= vmax.Y; y += step)
        {
            bool onTile = MathF.Abs(y / tile - MathF.Round(y / tile)) < 1e-3f;
            _draw.Line(ToScreen(new Vector2(vmin.X, y)), ToScreen(new Vector2(vmax.X, y)), onTile ? major : minor);
        }
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

    // The retarget anchors, drawn as rings so they never read as curve keys.
    private void DrawAnchors()
    {
        var c = new Color(120, 220, 160);
        Vector2 e = ToScreen(_doc.Entry), g = ToScreen(_doc.Gate);
        _draw.Line(e, g, new Color(60, 110, 90));
        _draw.Ring(e, _hoverAnchor == 0 || _dragAnchor == 0 ? 9f : 7f, c, 16, 2f);
        _draw.Ring(g, _hoverAnchor == 1 || _dragAnchor == 1 ? 9f : 7f, c, 16, 2f);
        Vector2 span = _doc.Gate - _doc.Entry;
        _spriteBatch.DrawString(_font, $"entry ({_doc.EntryX:0.#}, {_doc.EntryY:0.#})", e + new Vector2(10, -20), c);
        _spriteBatch.DrawString(_font, $"gate ({_doc.GateX:0.#}, {_doc.GateY:0.#})  span {span.X:0.#} x {span.Y:0.#} px", g + new Vector2(10, 6), c);
    }

    private void DrawHeader()
    {
        var mp = ToClip(new Vector2(_prevMs.X, _prevMs.Y));
        int shown = _hoverKey >= 0 ? _hoverKey : (_hoverHandle >= 0 ? _hoverHandle : -1);
        string keyInfo = shown >= 0
            ? $"key {shown}: pos ({_doc.Keys[shown].X:0.0}, {_doc.Keys[shown].Y:0.0}) px  tan ({_doc.Keys[shown].TX:0.0}, {_doc.Keys[shown].TY:0.0})  t {_doc.Keys[shown].T:0.00}"
            : $"cursor ({mp.X:0.0}, {mp.Y:0.0}) px";
        _spriteBatch.DrawString(_font,
            $"REF CLIP {_clipName}{(_dirty ? "  *unsaved*" : "")}",
            new Vector2(16, 10), _dirty ? Color.Orange : Color.White);
        _spriteBatch.DrawString(_font,
            $"{_doc.Keys.Count} keys   |   dur {_doc.Duration:0.00}s ({_doc.Duration * 60f:0} frames)   |   {keyInfo}",
            new Vector2(16, 28), new Color(160, 170, 185));
        _spriteBatch.DrawString(_font,
            _doc.IsLegacyNormalized
                ? "LEGACY normalized units - press U to convert to a pixel box (arc unchanged)"
                : "A add key   X delete   Shift snap to px   [ ] duration   Ctrl-S save   R revert   H help",
            new Vector2(16, 46), _doc.IsLegacyNormalized ? Color.Orange : new Color(130, 140, 155));
    }

    private static readonly (string Group, string Keys)[] HelpRows =
    {
        ("Edit", "drag key = move (all keys free)    drag handle tip = tangent (dir + magnitude)    Shift = snap to whole px"),
        ("Frame", "drag the green rings = entry / gate anchors; the runtime rescales the arc from their span onto the real obstacle"),
        ("Keys", "A = add key at nearest curve point    X / Del = delete hovered interior key"),
        ("Time", "[ / ] = arc duration in 0.05s steps - an animation riding this arc advances at (its duration / this one)"),
        ("View", "wheel = zoom at cursor    right/middle-drag = pan    Home = fit"),
        ("File", "Ctrl-S save json    R revert to disk    U convert legacy normalized clip    Esc quit"),
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

    private bool Pressed(Keys k) => _kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);

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
