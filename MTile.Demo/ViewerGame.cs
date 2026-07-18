using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MTile;

namespace MTileDemo;

// Offline take viewer (Plans/ANIM_TAKE_VIEWER_PLAN.md):
//     dotnet run --project MTile.Demo -- --load Takes/<name>.take.json
//
// Loads a gameplay take recorded in-game (Ctrl+R … Ctrl+S) and RE-RUNS a CharacterAnimator
// over its sample stream — the take stores the animator's inputs, not poses, so every
// solver internal is live here. One "pre-solve pass" on load caches each frame's pose,
// root, and an AnimFrameDebug snapshot; scrubbing then indexes the cache in any order.
// Press R after editing anim_solver_config.json to re-solve the same take under new
// weights — deterministic A/B of solver tuning on real gameplay.
//
// Transport (mirrors the in-game scrubber):  Space play/pause   J/K/L rev/pause/fwd
//   ←/→ step (Shift ×10)   Home/End   click the timeline to seek
// Overlays:  C contacts (marker × weight)   P pins + no-pen surfaces   D solver readout
//   V velocity   +/- zoom   F1/H help
public sealed class ViewerGame : Game
{
    private const int TileSize = Chunk.TileSize;

    private readonly string _takePath;
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _sb;
    private Texture2D   _pixel;
    private SpriteFont  _font;
    private DrawContext _draw;

    private AnimTake          _take;
    private Skeleton          _skel;
    private List<AnimationDocument> _clips;
    private CharacterAnimator _anim;       // fresh instance per pre-solve pass
    private string            _solverCfg;  // repo-root anim_solver_config.json (R reloads)

    // One frame of the pre-solve pass: everything Draw needs, order-free.
    private struct Cached
    {
        public BoneTransform[] Pose;
        public Vector2         Root;
        public int             Facing;
        public CharacterAnimator.AnimFrameDebug Dbg;
    }
    private Cached[] _cache;
    private double   _presolveMs;

    private int  _cursor;
    private int  _playDir;                 // 0 paused, ±1
    private bool _showContacts = true, _showPins = true, _showReadout = true;
    private bool _showVelocity = true, _showHelp, _showJoints;
    private float _zoom = 5f;
    private readonly Camera _camera = new();
    private KeyboardState _prevKb;
    private MouseState    _prevMouse;

    public ViewerGame(string takePath)
    {
        _takePath = takePath;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = 1200,
            PreferredBackBufferHeight = 800,
        };
        IsMouseVisible = true;
        Content.RootDirectory = "Content";
        Window.Title = "MTile take viewer — " + Path.GetFileName(takePath);
    }

    protected override void LoadContent()
    {
        _sb    = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _font  = Content.Load<SpriteFont>("DebugFont");
        _draw  = new DrawContext(_sb, _pixel);

        _shotPath = Environment.GetEnvironmentVariable("MTILE_SHOT");
        if (Environment.GetEnvironmentVariable("MTILE_SHOT_HELP") != null) _showHelp = true;
        if (int.TryParse(Environment.GetEnvironmentVariable("MTILE_SHOT_FRAME"), out int sf)) _shotCursor = sf;

        _take = AnimTake.Load(_takePath);
        if (_take.Frames.Count == 0) throw new InvalidDataException("Take has no frames: " + _takePath);

        // Same content resolution as the game: authored rig + all clips, solver config
        // from the repo root so edits there are what R picks up.
        string repo = RepoRoot();
        _skel  = SkeletonExamples.Biped();
        _clips = AnimationStore.LoadAll(Path.Combine(repo, "SkeletonStates"));
        _solverCfg = Path.Combine(repo, "anim_solver_config.json");
        AnimSolverConfig.Load(_solverCfg);

        Presolve();
        if (_shotCursor >= 0) _cursor = Math.Min(_shotCursor, _cache.Length - 1);
        _camera.Zoom = _zoom;
        SnapCamera();
    }
    private int _shotCursor = -1;

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MTile.sln"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    // ── the pre-solve pass ──────────────────────────────────────────────────────
    // Replay the whole sample stream through a FRESH animator, caching per-frame results.
    // The animator is a deterministic function of the stream, so this reproduces exactly
    // what the game rendered (same clips, same solver config) — and re-running it after a
    // config edit shows the same take under new weights.
    private void Presolve()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _anim  = new CharacterAnimator(_skel, _take.SkeletonScale, _clips);
        _cache = new Cached[_take.Frames.Count];
        for (int i = 0; i < _take.Frames.Count; i++)
        {
            var s = _take.Frames[i].ToSample();
            _anim.Update(s);
            _cache[i] = new Cached
            {
                Pose   = _anim.Pose.CloneLocal(),
                Root   = AttackGlowSystem.RigRoot(s.Position, s.Facing, _anim, _take.SkeletonScale),
                Facing = s.Facing,
                Dbg    = _anim.CaptureDebug(),
            };
        }
        _presolveMs = sw.Elapsed.TotalMilliseconds;
    }

    // ── update / transport ──────────────────────────────────────────────────────
    protected override void Update(GameTime gameTime)
    {
        var kb = Keyboard.GetState();
        var ms = Mouse.GetState();
        if (kb.IsKeyDown(Keys.Escape)) Exit();
        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
        int step = shift ? 10 : 1;

        if (Pressed(kb, Keys.Right)) { _cursor += step; _playDir = 0; }
        if (Pressed(kb, Keys.Left))  { _cursor -= step; _playDir = 0; }
        if (Pressed(kb, Keys.Home))  { _cursor = 0;                 _playDir = 0; }
        if (Pressed(kb, Keys.End))   { _cursor = _cache.Length - 1; _playDir = 0; }
        if (Pressed(kb, Keys.L))     _playDir = +1;
        if (Pressed(kb, Keys.J))     _playDir = -1;
        if (Pressed(kb, Keys.K))     _playDir = 0;
        if (Pressed(kb, Keys.Space)) _playDir = _playDir == 0 ? +1 : 0;

        if (Pressed(kb, Keys.C))  _showContacts = !_showContacts;
        if (Pressed(kb, Keys.P))  _showPins     = !_showPins;
        if (Pressed(kb, Keys.D))  _showReadout  = !_showReadout;
        if (Pressed(kb, Keys.V))  _showVelocity = !_showVelocity;
        if (Pressed(kb, Keys.N))  _showJoints   = !_showJoints;
        if (Pressed(kb, Keys.F1) || Pressed(kb, Keys.H)) _showHelp = !_showHelp;
        if (Pressed(kb, Keys.OemPlus)  || Pressed(kb, Keys.Add))      _zoom *= 1.25f;
        if (Pressed(kb, Keys.OemMinus) || Pressed(kb, Keys.Subtract)) _zoom /= 1.25f;
        _zoom = MathHelper.Clamp(_zoom, 1f, 24f);

        // Re-solve under the (possibly edited) solver config — the offline tuning loop.
        if (Pressed(kb, Keys.R))
        {
            int keep = _cursor;
            AnimSolverConfig.Load(_solverCfg);
            Presolve();
            _cursor = keep;
        }

        if (_playDir != 0) _cursor += _playDir;
        // Wrap during play (loop the take), clamp on manual stepping.
        if (_cursor < 0)              _cursor = _playDir != 0 ? _cache.Length - 1 : 0;
        if (_cursor >= _cache.Length) _cursor = _playDir != 0 ? 0 : _cache.Length - 1;

        // Timeline click-to-seek.
        var bar = TimelineRect();
        if (ms.LeftButton == ButtonState.Pressed && bar.Contains(ms.X, ms.Y))
        {
            float t = MathHelper.Clamp((ms.X - bar.X) / (float)bar.Width, 0f, 1f);
            _cursor  = (int)MathF.Round(t * (_cache.Length - 1));
            _playDir = 0;
        }

        _camera.Zoom = _zoom;
        SnapCamera();

        _prevKb = kb;
        _prevMouse = ms;
        base.Update(gameTime);
    }

    private void SnapCamera()
    {
        var f = _take.Frames[_cursor];
        _camera.Position = new Vector2(f.Px, f.Py);
    }

    private bool Pressed(KeyboardState kb, Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);

    // ── draw ────────────────────────────────────────────────────────────────────
    private string _shotPath;
    private int    _shotFrame;

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

        GraphicsDevice.Clear(new Color(18, 20, 26));

        var screenCenter = new Vector2(GraphicsDevice.Viewport.Width / 2f,
                                       GraphicsDevice.Viewport.Height / 2f);
        var cam = _camera.GetTransform(screenCenter);
        var frame  = _take.Frames[_cursor];
        var cached = _cache[_cursor];

        // — world pass —
        _sb.Begin(transformMatrix: cam, samplerState: SamplerState.PointClamp);
        DrawTerrain(frame, screenCenter);
        DrawBody(frame);
        // Load the cached pose into the animator and draw with the game's own renderer.
        _anim.Pose.LoadLocal(cached.Pose);
        _anim.Draw(_draw, cached.Root, cached.Facing, drawJoints: _showJoints, highlightPlantFoot: false);
        DrawSolverOverlays(cached.Dbg);
        _sb.End();

        // — screen pass (text, timeline, readout) —
        _sb.Begin();
        DrawWorldLabels(cached.Dbg, cam);
        DrawTimeline();
        if (_showReadout) DrawReadout(frame, cached.Dbg);
        if (_showHelp)    DrawHelp();
        _sb.End();

        if (capturing)
        {
            GraphicsDevice.SetRenderTarget(null);
            _sb.Begin();
            _sb.Draw(rt, GraphicsDevice.Viewport.Bounds, Color.White);
            _sb.End();
            try { using var fs = File.Create(_shotPath); rt.SaveAsPng(fs, rt.Width, rt.Height); } catch { }
            rt.Dispose();
            Exit();
        }
        base.Draw(gameTime);
    }

    private static readonly Color[] _typeColors =
    {
        new Color(110, 112, 120),   // Stone
        new Color(124,  92,  60),   // Dirt
        new Color(194, 174, 110),   // Sand
        new Color(150, 200, 210),   // Foam
    };

    private void DrawTerrain(AnimTake.SampleDto frame, Vector2 screenCenter)
    {
        // Visible world bounds for culling.
        float halfW = screenCenter.X / _camera.Zoom, halfH = screenCenter.Y / _camera.Zoom;
        float x0 = _camera.Position.X - halfW - TileSize, x1 = _camera.Position.X + halfW + TileSize;
        float y0 = _camera.Position.Y - halfH - TileSize, y1 = _camera.Position.Y + halfH + TileSize;

        var tiles = _take.TerrainStates[frame.Terrain];
        foreach (var t in tiles)
        {
            float cx = t.X * TileSize + TileSize / 2f, cy = t.Y * TileSize + TileSize / 2f;
            if (cx < x0 || cx > x1 || cy < y0 || cy > y1) continue;
            var c = _typeColors[Math.Min(t.T, (byte)(_typeColors.Length - 1))];
            if ((TileState)t.S == TileState.Sprouting) c *= 0.45f;
            _draw.Rect(new Vector2(cx, cy), new Vector2(TileSize - 1, TileSize - 1), c);
        }
    }

    private void DrawBody(AnimTake.SampleDto f)
    {
        var pos = new Vector2(f.Px, f.Py);
        _draw.Ring(pos, _take.PlayerRadius, (f.Grounded ? Color.LimeGreen : Color.Orange) * 0.6f,
                   segments: 24, thickness: 0.4f);
        if (_showVelocity)
            _draw.Line(pos, pos + new Vector2(f.Vx, f.Vy) * 0.12f, Color.Yellow * 0.8f, 0.5f);
    }

    private void DrawSolverOverlays(CharacterAnimator.AnimFrameDebug d)
    {
        if (_showContacts && d.Contacts != null)
            foreach (var c in d.Contacts)
            {
                // Marker scales & brightens with the frozen solve weight: full plant = solid,
                // feathering/releasing = shrinking + fading.
                float w = MathHelper.Clamp(c.Weight, 0f, 1f);
                _draw.Ring(c.Target, 1.2f + 1.8f * w, Color.Lime * (0.35f + 0.65f * w),
                           segments: 12, thickness: 0.35f);
                _draw.Disc(c.Target, 0.5f, Color.Lime * (0.35f + 0.65f * w));
            }

        if (_showPins && d.Pins != null)
            foreach (var p in d.Pins)
            {
                _draw.Line(p.Target - new Vector2(2, 2), p.Target + new Vector2(2, 2), Color.OrangeRed, 0.5f);
                _draw.Line(p.Target - new Vector2(2, -2), p.Target + new Vector2(2, -2), Color.OrangeRed, 0.5f);
            }

        if (_showPins && d.Surfaces != null)
            foreach (var s in d.Surfaces)
            {
                var tangent = new Vector2(-s.Normal.Y, s.Normal.X);
                _draw.Line(s.Point - tangent * 24f, s.Point + tangent * 24f, Color.DeepSkyBlue * 0.7f, 0.6f);
                _draw.Line(s.Point, s.Point + s.Normal * 6f, Color.DeepSkyBlue, 0.6f);
                // The margin line the solver actually holds limbs outside of.
                var m = s.Point + s.Normal * s.Margin;
                _draw.Line(m - tangent * 24f, m + tangent * 24f, Color.DeepSkyBlue * 0.3f, 0.4f);
            }

        if (d.AimActive)
        {
            // Aim direction from the body (unit dir — draw a fixed-length ray).
            var f = _take.Frames[_cursor];
            var pos = new Vector2(f.Px, f.Py);
            _draw.Line(pos, pos + d.AimTarget * 20f, Color.Violet * 0.8f, 0.5f);
        }
    }

    // Weight numbers / bone names next to their world markers — drawn in the SCREEN pass
    // (world-pass text would be unreadably scaled), projected through the camera matrix.
    private void DrawWorldLabels(CharacterAnimator.AnimFrameDebug d, Matrix cam)
    {
        if (_showContacts && d.Contacts != null)
            foreach (var c in d.Contacts)
                Label(Vector2.Transform(c.Target, cam) + new Vector2(8, -8),
                      $"{c.Bone} w={c.Weight:0.00}", Color.Lime);
        if (_showPins && d.Pins != null)
            foreach (var p in d.Pins)
                Label(Vector2.Transform(p.Target, cam) + new Vector2(8, -8),
                      $"pin {p.Bone}", Color.OrangeRed);
        if (d.AimActive)
            Label(Vector2.Transform(_camera.Position, cam) + new Vector2(8, -24),
                  $"aim err {d.AimErrDeg:0.0}°", Color.Violet);
    }

    private void Label(Vector2 screen, string text, Color c)
    {
        _sb.DrawString(_font, text, screen + new Vector2(1, 1), Color.Black, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
        _sb.DrawString(_font, text, screen, c, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
    }

    private Rectangle TimelineRect()
    {
        var vp = GraphicsDevice.Viewport;
        return new Rectangle(16, vp.Height - 34, vp.Width - 32, 18);
    }

    private void DrawTimeline()
    {
        var bar = TimelineRect();
        _sb.Draw(_pixel, bar, new Color(40, 44, 54));

        // Terrain-change ticks: frames where the terrain state index advances (a block
        // broke/appeared) — handy anchors when scrubbing for the interesting moment.
        int prev = _take.Frames[0].Terrain;
        for (int i = 1; i < _take.Frames.Count; i++)
        {
            if (_take.Frames[i].Terrain == prev) continue;
            prev = _take.Frames[i].Terrain;
            int tx = bar.X + (int)(i / (float)(_cache.Length - 1) * bar.Width);
            _sb.Draw(_pixel, new Rectangle(tx, bar.Y, 1, bar.Height), Color.Orange * 0.7f);
        }

        int px = bar.X + (int)(_cursor / (float)Math.Max(1, _cache.Length - 1) * bar.Width);
        _sb.Draw(_pixel, new Rectangle(px - 1, bar.Y - 3, 3, bar.Height + 6), Color.Aqua);

        string mode = _playDir > 0 ? ">>" : _playDir < 0 ? "<<" : "||";
        Label(new Vector2(bar.X, bar.Y - 18),
              $"[{mode}] frame {_cursor + 1}/{_cache.Length}   presolve {_presolveMs:0} ms   (F1 help)", Color.Aqua);
    }

    private void DrawReadout(AnimTake.SampleDto f, CharacterAnimator.AnimFrameDebug d)
    {
        var lines = new List<string>
        {
            $"state  {f.State}  [{(AnimTag)f.Tag}]{(f.Grounded ? "  grounded" : "  airborne")}",
            $"action {(string.IsNullOrEmpty(f.Action) ? "-" : f.Action)}  t={f.ActionTime:0.00}/{f.ActionDuration:0.00}  prog={f.MoveProgress:0.00}",
            $"pos ({f.Px:0.0}, {f.Py:0.0})  vel ({f.Vx:0.0}, {f.Vy:0.0})  dt={f.Dt * 1000f:0.0}ms",
        };
        if (d.Solved)
        {
            lines.Add($"clip {d.Clip ?? "-"}  phi={d.Phase:0.000}  dPhi={d.DPhi:+0.000;-0.000}");
            lines.Add($"delta={d.Dy:+0.00;-0.00}px  dx={d.Dx:+0.00;-0.00}px  |dTheta|max={d.MaxDTheta:0.000} ({d.MaxDThetaBone ?? "-"})");
        }
        else lines.Add("(no solve this frame)");
        if (d.Contacts is { Length: > 0 })
        {
            var sb = new System.Text.StringBuilder("contacts ");
            foreach (var c in d.Contacts) sb.Append($" {c.Bone}={c.Weight:0.00}");
            lines.Add(sb.ToString());
        }
        if (d.Pins is { Length: > 0 })     lines.Add($"pins {d.Pins.Length}");
        if (d.Surfaces is { Length: > 0 }) lines.Add($"surfaces {d.Surfaces.Length}");
        if (d.AimActive)                   lines.Add($"aim err {d.AimErrDeg:0.0} deg");

        var pos = new Vector2(12, 10);
        foreach (var l in lines)
        {
            Label(pos, l, Color.White);
            pos.Y += 16;
        }
    }

    private void DrawHelp()
    {
        string[] lines =
        {
            "Space play/pause     J / K / L  reverse / pause / forward",
            "Left/Right step (Shift x10)     Home/End first/last     click timeline to seek",
            "C contacts   P pins+surfaces   D readout   V velocity   N joint nodes   +/- zoom",
            "R  reload anim_solver_config.json and RE-SOLVE the whole take",
            "Esc quit",
        };
        var vp = GraphicsDevice.Viewport;
        var pos = new Vector2(vp.Width - 560, 10);
        _sb.Draw(_pixel, new Rectangle((int)pos.X - 8, (int)pos.Y - 6, 556, lines.Length * 16 + 12),
                 new Color(0, 0, 0, 180));
        foreach (var l in lines)
        {
            Label(pos, l, Color.LightGray);
            pos.Y += 16;
        }
    }
}
