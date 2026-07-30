using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MTile;

namespace MTileDemo;

// Sprite BINDING editor (Plans/SPRITE_SKIN_PLAN.md §7): drag the rig over a PNG to author
// the one-time skeleton↔artwork alignment that drives the runtime MLS sprite skin.
//
//   dotnet run --project MTile.Demo -- --bind hero.png
//
// The PNG is resolved against SpriteBindings/ at the repo root; the binding saves next to
// it as <name>.json. Edits touch ONLY the binding (pose rotations + binding lengths +
// image→rig placement) — never the shared rig or any clip.
//
//   • Drag a joint      — Rotate mode: aim the bone at the cursor (bind pose rotation).
//                         Resize mode: also stretch the BINDING's bone length to the cursor.
//   • Drag the root     — move the whole rig over the image.  Shift+wheel — scale it.
//   • Wheel             — zoom the view about the cursor.  Right-drag / arrows — pan.
//   • G                 — toggle the deformed-skin preview (rebakes from the current edit;
//                         with no clip playing it shows the bind pose, which should
//                         reproduce the artwork almost exactly — the alignment sanity check).
//   • Space , .         — play / pause a clip through the deformation; , / . cycle clips.
//                         This is the real acceptance test: judge the binding on a walk.
//   • [ ]               — cycle the selected layer (multi-layer bindings): other layers'
//                         backdrops dim, the selection's deformed mesh outlines in preview.
//   • B                 — toggle part styling (on by default): every part image draws with
//                         a black silhouette outline and reduced opacity, so overlapping
//                         limbs and their boundaries read while posing the rig.
//   • Tab / H / Ctrl-S  — edit mode / help / save.
//
// `--bind rabbit` resolves SpriteBindings/rabbit.json FIRST (multi-image bindings have
// no single PNG to name); a PNG argument keeps the legacy path, including creating a
// brand-new binding. Multi-image backdrops composite every layer at its canvas offset —
// the parts visibly reassembling IS the registration check (§10.5).
//
// The rig comes from the binding's Skeleton field (Skeletons/<name>.json; default
// biped). `--rig <name>` overrides it — for a new binding it just picks the rig; for
// an existing one it re-targets (bone-name matched, Ctrl-S persists the new name).
public sealed class BindGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;
    private Texture2D   _pixel;
    private SpriteFont  _font;
    private DrawContext _draw;

    private readonly string _imageArg;
    private readonly string _rigArg;      // --rig override; null = binding's Skeleton field, then biped
    private string _pngPath, _jsonPath;

    private Skeleton              _skeleton;
    private SkeletonPose          _pose;          // the bind pose being authored
    private SpriteBindingDocument _doc;
    private Texture2D             _texture;       // doc-level image, premultiplied; null when
                                                  // every layer brings its own PNG
    private SkinHandleLayout      _handles;

    // Backdrop composite: every image that participates in the binding, at its canvas
    // offset (the doc image at 0,0 plus each own-image layer). Spec is null for the doc
    // image. _canvasMin/Max is their union bbox in image px — the "canvas" all fits use.
    private readonly List<(SpriteSkinLayer Spec, Texture2D Tex, Texture2D Outline, Point Off)> _backdrops = new();
    private Vector2 _canvasMin, _canvasMax;
    private int     _selLayer = -1;               // index into _doc.Layers; -1 = all

    // Part styling (B): each backdrop draws over its black silhouette ring at reduced
    // opacity, so overlapping limbs and their boundaries stay readable while posing.
    private bool _partStyle = true;
    private const int   OutlinePx   = 3;      // ring thickness, image px (pre-zoom)
    private const float PartOpacity = 0.55f;

    // View (image px → screen): screen = img * _zoom + _viewOff.
    private float   _zoom = 1f;
    private Vector2 _viewOff;

    private enum EditMode { Rotate, Resize }
    private EditMode _editMode = EditMode.Rotate;
    private int  _dragBone = -1, _hoverBone = -1;
    private bool _dragRoot, _panning;
    private bool _dirty;
    private bool _showHelp;

    // Deformed preview.
    private SpriteSkin   _skin;
    private bool         _preview, _skinDirty = true;
    private string       _skinError;
    private SkeletonPose _previewPose, _scratchA, _scratchB;

    // Clip playback through the deformation.
    private List<AnimationDocument> _clips = new();
    private int   _clipIndex;
    private bool  _playing;
    private float _playTime;

    private MouseState    _prevMs;
    private KeyboardState _prevKb;

    private const float PickR = 12f;

    private int W => GraphicsDevice.Viewport.Width;
    private int H => GraphicsDevice.Viewport.Height;

    public BindGame(string imageArg, string rigArg = null)
    {
        _imageArg = imageArg;
        _rigArg   = rigArg;
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

        // Json-first: `--bind rabbit` opens SpriteBindings/rabbit.json when it exists
        // (multi-image bindings have no single PNG to name). Otherwise the legacy
        // png-first path, which is also how a brand-new binding is created.
        string jsonCandidate = ResolveJson(root, _imageArg);
        if (jsonCandidate != null)
        {
            _jsonPath = jsonCandidate;
            _doc = SpriteBindingDocument.Load(_jsonPath)
                ?? throw new InvalidOperationException($"Binding exists but failed to load: {_jsonPath}");
            _pngPath = _doc.ImagePath;
            if (_pngPath != null)
            {
                using var fs = File.OpenRead(_pngPath);
                _texture = Texture2D.FromStream(GraphicsDevice, fs);
                Premultiply(_texture);
            }
            ResolveSkeleton();
            _pose = _doc.CreateBindPose(_skeleton);
            Console.WriteLine($"Bind editor - loaded {_jsonPath} (rig {_skeleton.Name})");
        }
        else
        {
            _pngPath = ResolvePng(root, _imageArg);
            _jsonPath = Path.ChangeExtension(_pngPath, ".json");
            using (var fs = File.OpenRead(_pngPath))
                _texture = Texture2D.FromStream(GraphicsDevice, fs);
            Premultiply(_texture);

            _doc = SpriteBindingDocument.Load(_jsonPath);
            if (_doc != null)
            {
                ResolveSkeleton();
                _pose = _doc.CreateBindPose(_skeleton);
                Console.WriteLine($"Bind editor - loaded {_jsonPath} (rig {_skeleton.Name})");
            }
            else
            {
                _skeleton = SkeletonExamples.Load(_rigArg ?? SkeletonExamples.BipedName);
                _doc = new SpriteBindingDocument
                {
                    Skeleton = _skeleton.Name,
                    Image    = Path.GetFileName(_pngPath),
                    FilePath = _jsonPath,
                };
                _pose = _skeleton.CreatePose();
            }
        }

        LoadBackdrops();
        if (jsonCandidate == null && _doc.BindPose.Count == 0)
        {
            FitRigToImage();
            _dirty = true;
            Console.WriteLine($"Bind editor - NEW binding for {_pngPath} (Ctrl-S writes {_jsonPath})");
        }

        _handles = SkinHandleLayout.Create(_skeleton, step: _doc.HandleStep);

        _previewPose = _skeleton.CreatePose();
        _scratchA    = _skeleton.CreatePose();
        _scratchB    = _skeleton.CreatePose();

        string statesDir = Path.Combine(root, "SkeletonStates", _skeleton.Name);
        if (Directory.Exists(statesDir)) _clips = AnimationStore.LoadAll(statesDir);
        // Open on a locomotion clip if one exists — the binding is judged on a walk.
        _clipIndex = Math.Max(0, _clips.FindIndex(c =>
            string.Equals(c.Name, "walk", StringComparison.OrdinalIgnoreCase)));

        FitViewToImage();

        // Dev screenshot mode (same contract as DemoGame): MTILE_SHOT=path captures one
        // frame and exits; MTILE_SHOT_PREVIEW additionally turns the deformed preview on,
        // MTILE_SHOT_CLIP plays the named clip's first frame through the deformation.
        _shotPath = Environment.GetEnvironmentVariable("MTILE_SHOT");
        if (Environment.GetEnvironmentVariable("MTILE_SHOT_PREVIEW") != null) _preview = true;
        // MTILE_SHOT_LAYER=<name> selects a layer for the capture (as [ ] would).
        string shotLayer = Environment.GetEnvironmentVariable("MTILE_SHOT_LAYER");
        if (shotLayer != null && _doc.Layers != null)
            _selLayer = _doc.Layers.FindIndex(l =>
                string.Equals(l.Name, shotLayer, StringComparison.OrdinalIgnoreCase));
        string shotClip = Environment.GetEnvironmentVariable("MTILE_SHOT_CLIP");
        if (shotClip != null)
        {
            int idx = _clips.FindIndex(c => string.Equals(c.Name, shotClip, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _clipIndex = idx; _preview = true; _playing = true; _playTime = 0.25f;
                AnimationSampler.SampleAtTime(_clips[idx], _playTime, _scratchA, _scratchB, _previewPose);
            }
        }
    }

    private string _shotPath;
    private int    _shotFrame;

    // Rig resolution for an existing binding: --rig overrides, else the doc's Skeleton
    // field, else biped (Skeletons/<name>.json, authored-only — Load throws if missing).
    // Opening a binding under a DIFFERENT rig is a deliberate re-target: bind-pose
    // entries match by bone name (unknown ones are skipped, new bones start at rest),
    // and the doc's Skeleton field updates so Ctrl-S persists the switch.
    private void ResolveSkeleton()
    {
        string name = _rigArg
            ?? (string.IsNullOrWhiteSpace(_doc.Skeleton) ? SkeletonExamples.BipedName : _doc.Skeleton);
        _skeleton = SkeletonExamples.Load(name);
        if (!string.Equals(_doc.Skeleton, _skeleton.Name, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Bind editor - re-targeting rig '{_doc.Skeleton ?? "(none)"}' -> '{_skeleton.Name}' (Ctrl-S persists)");
            _doc.Skeleton = _skeleton.Name;
            _dirty = true;
        }
    }

    // The binding argument as a json: an explicit .json path, else
    // SpriteBindings/<name>.json at the repo root. Null when neither exists.
    private static string ResolveJson(string repoRoot, string arg)
    {
        if (arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return File.Exists(arg) ? Path.GetFullPath(arg) : null;
        string candidate = Path.Combine(repoRoot, "SpriteBindings",
            Path.GetFileNameWithoutExtension(arg) + ".json");
        return File.Exists(candidate) ? candidate : null;
    }

    // Collect every image participating in the binding — the doc image at (0,0) plus
    // each own-image layer at its offset — and their union bbox (the shared canvas).
    // The composited figure visibly reassembling is the §10.5 registration check.
    private void LoadBackdrops()
    {
        _backdrops.Clear();
        if (_texture != null)
            _backdrops.Add((null, _texture, BuildOutline(_texture), Point.Zero));
        if (_doc.Layers != null)
            foreach (var l in _doc.Layers)
            {
                if (string.IsNullOrEmpty(l.Image)) continue;
                Texture2D tex;
                using (var fs = File.OpenRead(_doc.LayerImagePath(l)))
                    tex = Texture2D.FromStream(GraphicsDevice, fs);
                Premultiply(tex);
                _backdrops.Add((l, tex, BuildOutline(tex), new Point(l.OffsetX, l.OffsetY)));
            }
        if (_backdrops.Count == 0)
            throw new InvalidOperationException("Binding has no images at all.");

        _canvasMin = new Vector2(float.MaxValue); _canvasMax = new Vector2(float.MinValue);
        foreach (var (_, tex, _, off) in _backdrops)
        {
            _canvasMin = Vector2.Min(_canvasMin, off.ToVector2());
            _canvasMax = Vector2.Max(_canvasMax, off.ToVector2() + new Vector2(tex.Width, tex.Height));
        }
    }

    // Black silhouette ring for a part image: alpha dilated by OutlinePx (separable
    // max filter) and cleared inside the solid silhouette, so it only marks the rim.
    // Drawn UNDER the translucent part, it keeps every limb's boundary readable even
    // where parts overlap. Premultiplied black, so tints leave it black.
    private Texture2D BuildOutline(Texture2D tex)
    {
        int w = tex.Width, h = tex.Height, r = OutlinePx;
        var px = new Color[w * h];
        tex.GetData(px);

        var a   = new byte[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = px[i].A;
        var tmp = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte m = 0;
                for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++)
                    m = Math.Max(m, a[y * w + k]);
                tmp[y * w + x] = m;
            }
        var dil = new byte[w * h];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                byte m = 0;
                for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++)
                    m = Math.Max(m, tmp[k * w + x]);
                dil[y * w + x] = m;
            }

        for (int i = 0; i < px.Length; i++)
        {
            byte ring = a[i] >= 160 ? (byte)0 : dil[i];   // keep it under AA edges for crispness
            px[i] = new Color((byte)0, (byte)0, (byte)0, ring);
        }
        var outline = new Texture2D(GraphicsDevice, w, h);
        outline.SetData(px);
        return outline;
    }

    // Default image→rig placement for a brand-new binding: the rig's default-pose
    // joint bounds scaled onto the canvas bbox height, centers aligned.
    private void FitRigToImage()
    {
        var world = _pose.ComputeWorld(Affine2.Identity);
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < world.Length; i++)
        {
            var p = world[i].Translation;
            min = Vector2.Min(min, p); max = Vector2.Max(max, p);
        }
        float rigH = MathF.Max(max.Y - min.Y, 1f);
        float s = rigH / MathF.Max(_canvasMax.Y - _canvasMin.Y, 1f);
        Vector2 rigC = (min + max) * 0.5f;
        Vector2 imgC = (_canvasMin + _canvasMax) * 0.5f;
        _doc.ImageToRigScale = s;
        _doc.ImageToRigTx = rigC.X - imgC.X * s;
        _doc.ImageToRigTy = rigC.Y - imgC.Y * s;
    }

    private void FitViewToImage()
    {
        Vector2 size = Vector2.Max(_canvasMax - _canvasMin, Vector2.One);
        _zoom = MathF.Min((W - 80f) / size.X, (H - 120f) / size.Y) * 0.95f;
        _zoom = MathF.Max(_zoom, 0.05f);
        _viewOff = new Vector2(W, H) * 0.5f - (_canvasMin + _canvasMax) * 0.5f * _zoom;
    }

    // === coordinate frames ====================================================
    // screen = img*Z + O;  rig = img*s + T  ⇒  screen = rig*(Z/s) + (O − T·Z/s)

    private Affine2 RigToScreen()
    {
        float k = _zoom / _doc.ImageToRigScale;
        Vector2 t = _viewOff - new Vector2(_doc.ImageToRigTx, _doc.ImageToRigTy) * k;
        return Affine2.FromTRS(t, 0f, new Vector2(k, k));
    }

    protected override void Update(GameTime gameTime)
    {
        var ms = Mouse.GetState();
        var kb = Keyboard.GetState();
        var mp = new Vector2(ms.X, ms.Y);
        bool ctrl  = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        bool shift = kb.IsKeyDown(Keys.LeftShift)   || kb.IsKeyDown(Keys.RightShift);

        if (Pressed(kb, Keys.Escape)) Exit();
        if (Pressed(kb, Keys.Tab)) _editMode = (EditMode)(((int)_editMode + 1) % 2);
        if (Pressed(kb, Keys.H))   _showHelp = !_showHelp;
        if (Pressed(kb, Keys.Home)) FitViewToImage();
        if (ctrl && Pressed(kb, Keys.S)) Save();
        if (Pressed(kb, Keys.G)) TogglePreview();
        if (Pressed(kb, Keys.B)) _partStyle = !_partStyle;
        if (Pressed(kb, Keys.Space)) TogglePlay();
        if (_clips.Count > 0 && Pressed(kb, Keys.OemComma))  CycleClip(-1);
        if (_clips.Count > 0 && Pressed(kb, Keys.OemPeriod)) CycleClip(+1);
        if (_doc.Layers != null && _doc.Layers.Count > 0)
        {
            if (Pressed(kb, Keys.OemOpenBrackets))  CycleLayer(-1);
            if (Pressed(kb, Keys.OemCloseBrackets)) CycleLayer(+1);
        }

        float nudge = shift ? 8f : 2f;
        if (kb.IsKeyDown(Keys.Left))  _viewOff.X += nudge;
        if (kb.IsKeyDown(Keys.Right)) _viewOff.X -= nudge;
        if (kb.IsKeyDown(Keys.Up))    _viewOff.Y += nudge;
        if (kb.IsKeyDown(Keys.Down))  _viewOff.Y -= nudge;

        // Wheel: view zoom about the cursor; Shift+wheel: RIG scale over the image
        // (a binding edit — keeps the rig root pinned in image space).
        int wheel = ms.ScrollWheelValue - _prevMs.ScrollWheelValue;
        if (wheel != 0)
        {
            float f = MathF.Pow(1.0015f, wheel);
            if (shift)
            {
                // Wheel up = rig larger over the art = fewer rig units per image px.
                float s = _doc.ImageToRigScale;
                float ns = MathHelper.Clamp(s / f, 1e-4f, 1e4f);
                Vector2 img0 = new Vector2(-_doc.ImageToRigTx, -_doc.ImageToRigTy) / s;
                _doc.ImageToRigScale = ns;
                _doc.ImageToRigTx = -img0.X * ns;
                _doc.ImageToRigTy = -img0.Y * ns;
                _dirty = true; _skinDirty = true;
            }
            else
            {
                float nz = MathHelper.Clamp(_zoom * f, 0.05f, 64f);
                _viewOff = mp + (_viewOff - mp) * (nz / _zoom);   // keep the cursor's image point fixed
                _zoom = nz;
            }
        }

        bool leftDown    = ms.LeftButton == ButtonState.Pressed;
        bool leftPressed = leftDown && _prevMs.LeftButton == ButtonState.Released;
        bool leftUp      = !leftDown && _prevMs.LeftButton == ButtonState.Pressed;
        bool rightDown   = ms.RightButton == ButtonState.Pressed || ms.MiddleButton == ButtonState.Pressed;

        var world = _pose.ComputeWorld(RigToScreen());
        _hoverBone = (!_playing && _dragBone < 0 && !_dragRoot) ? PickJoint(world, mp) : _dragBone;

        if (leftPressed && !_playing)
        {
            int bone = PickJoint(world, mp);
            if (bone >= 0 && _skeleton.Bones[bone].IsRoot) _dragRoot = true;
            else if (bone >= 0) _dragBone = bone;
        }
        if (rightDown) { if (!_panning) _panning = true; else _viewOff += mp - new Vector2(_prevMs.X, _prevMs.Y); }
        else _panning = false;

        if (leftDown && _dragRoot)
        {
            // Rig follows the cursor: screen(rigOrigin) = O − T·Z/s ⇒ ΔT = −Δscreen·s/Z.
            var dm = mp - new Vector2(_prevMs.X, _prevMs.Y);
            float k = _doc.ImageToRigScale / _zoom;
            _doc.ImageToRigTx -= dm.X * k;
            _doc.ImageToRigTy -= dm.Y * k;
            _dirty = true; _skinDirty = true;
        }
        else if (leftDown && _dragBone >= 0)
        {
            EditBone(world, _dragBone, mp);
        }
        if (leftUp) { _dragBone = -1; _dragRoot = false; }

        // Screenshot mode holds the MTILE_SHOT_CLIP sample time so captures are
        // deterministic (comparable across runs); live playback advances normally.
        if (_playing && _clips.Count > 0 && _shotPath == null)
        {
            _playTime += (float)gameTime.ElapsedGameTime.TotalSeconds;
            AnimationSampler.SampleAtTime(_clips[_clipIndex], _playTime, _scratchA, _scratchB, _previewPose);
        }

        _prevMs = ms; _prevKb = kb;
        base.Update(gameTime);
    }

    // Same drag semantics as the animation editor, routed to the BINDING: the angular
    // part edits the bind pose rotation; in Resize mode the radial part stretches the
    // binding's bone length (pose translation only — the shared rig is never touched).
    private void EditBone(Affine2[] world, int bone, Vector2 mp)
    {
        int parent = _skeleton.Bones[bone].Parent;
        if (parent < 0) return;

        Vector2 pivot     = world[parent].Translation;
        Vector2 boneVec   = world[bone].Translation - pivot;
        Vector2 cursorVec = mp - pivot;
        if (boneVec.LengthSquared() < 1e-4f || cursorVec.LengthSquared() < 1e-4f) return;

        _pose.Local[bone].Rotation += SignedAngle(boneVec, cursorVec);
        if (_editMode == EditMode.Resize)
        {
            float len = MathF.Max(0.1f, _pose.Local[bone].Translation.X * (cursorVec.Length() / boneVec.Length()));
            _pose.Local[bone].Translation = Vector2.UnitX * len;
        }
        _dirty = true; _skinDirty = true;
    }

    private static float SignedAngle(Vector2 a, Vector2 b)
        => MathF.Atan2(a.X * b.Y - a.Y * b.X, Vector2.Dot(a, b));

    private int PickJoint(Affine2[] world, Vector2 mp)
    {
        int best = -1; float bestD = PickR * PickR;
        for (int i = 0; i < world.Length; i++)
        {
            float d = Vector2.DistanceSquared(world[i].Translation, mp);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // === preview / playback ===================================================

    private void TogglePreview()
    {
        _preview = !_preview;
        if (_preview) RebakeSkinIfNeeded();
        if (!_preview) _playing = false;
    }

    private void TogglePlay()
    {
        if (_clips.Count == 0) return;
        _playing = !_playing;
        if (_playing)
        {
            _playTime = 0f;
            _preview = true;
            RebakeSkinIfNeeded();
        }
    }

    private void CycleClip(int step)
    {
        _clipIndex = ((_clipIndex + step) % _clips.Count + _clips.Count) % _clips.Count;
        _playTime = 0f;
    }

    // Selected layer cycles through -1 (all) then each of _doc.Layers in order.
    private void CycleLayer(int step)
    {
        int n = _doc.Layers.Count;
        _selLayer += step;
        if (_selLayer >= n) _selLayer = -1;
        else if (_selLayer < -1) _selLayer = n - 1;
    }

    private void RebakeSkinIfNeeded()
    {
        if (_skin != null && !_skinDirty) return;
        _skin?.Dispose();
        _skin = null; _skinError = null;
        try
        {
            _doc.CaptureBindPose(_pose);   // bake against the live edit
            _skin = new SpriteSkin(GraphicsDevice, _doc, _skeleton, _texture,
                                   ownsTexture: false, premultiply: false);
            _skinDirty = false;
        }
        catch (Exception e) { _skinError = e.Message; _preview = false; }
    }

    private void Save()
    {
        _doc.CaptureBindPose(_pose);
        _doc.Save(_jsonPath);
        _dirty = false;
        Console.WriteLine($"saved {_jsonPath}");
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
        var rigRoot = RigToScreen();

        // Pass 1: backdrop composite — every participating image at its canvas offset
        // (dimmed under the deformed preview so the skin reads on top; non-selected
        // layers dim slightly so the [ ] selection reads).
        _spriteBatch.Begin(samplerState: _zoom >= 2f ? SamplerState.PointClamp : SamplerState.LinearClamp);
        foreach (var (spec, tex, outline, off) in _backdrops)
        {
            bool deselected = _selLayer >= 0 && spec != null && _doc.Layers[_selLayer] != spec;
            Color tint = _preview ? new Color(70, 70, 70, 255)
                       : deselected ? new Color(120, 120, 120, 255) : Color.White;
            Vector2 pos = _viewOff + off.ToVector2() * _zoom;
            if (_partStyle)
            {
                _spriteBatch.Draw(outline, pos, null, Color.White * (tint.R / 255f),
                                  0f, Vector2.Zero, _zoom, SpriteEffects.None, 0f);
                tint *= PartOpacity;
            }
            _spriteBatch.Draw(tex, pos, null, tint,
                              0f, Vector2.Zero, _zoom, SpriteEffects.None, 0f);
        }
        _spriteBatch.End();

        // Deformed skin (its own device draw, outside any SpriteBatch pass).
        if (_preview)
        {
            RebakeSkinIfNeeded();
            var shownPose = _playing ? _previewPose : _pose;
            _skin?.Draw(Matrix.Identity, shownPose, rigRoot);
        }

        // Pass 2: rig overlay + text.
        _spriteBatch.Begin();
        // Selected layer's deformed mesh outline (positions come from the skin Draw above).
        if (_preview && _selLayer >= 0 && _skin != null)
            _skin.DrawWireframe(_draw, new Color(255, 220, 80), 1f, _doc.Layers[_selLayer].Name);
        var overlayPose = _playing ? _previewPose : _pose;
        var style = SkeletonDrawStyle.Default;
        style.BoneThickness = 2f;
        style.JointRadius = 0f;
        style.BoneColor = _playing ? new Color(120, 200, 255) : Color.White;
        SkeletonRenderer.Draw(_draw, overlayPose, rigRoot, style);

        var world = overlayPose.ComputeWorld(rigRoot);
        if (!_playing)
        {
            for (int i = 0; i < world.Length; i++)
            {
                Vector2 p = world[i].Translation;
                if (i == _dragBone)       _draw.Disc(p, 6f, Color.White);
                else if (i == _hoverBone) _draw.Disc(p, 6f, Color.LightYellow);
                else _draw.Ring(p, 5f, _skeleton.Bones[i].IsRoot ? Color.Yellow : Color.OrangeRed, 12, 1.5f);
            }
            // MLS handle sites (bind side) — shows what actually anchors the deformation.
            Span<Vector2> hs = stackalloc Vector2[_handles.Count];
            _handles.Sample(world, hs);
            foreach (var hp in hs) _draw.Disc(hp, 2f, new Color(120, 230, 200));
        }

        DrawHeader();
        DrawHelpOverlay();
        _spriteBatch.End();

        if (capturing)
        {
            GraphicsDevice.SetRenderTarget(null);
            _spriteBatch.Begin();
            _spriteBatch.Draw(rt, GraphicsDevice.Viewport.Bounds, Color.White);
            _spriteBatch.End();
            try { using var fs = File.Create(_shotPath); rt.SaveAsPng(fs, rt.Width, rt.Height); } catch { }
            rt.Dispose();
            Exit();
        }
        base.Draw(gameTime);
    }

    private void DrawHeader()
    {
        string clip = _clips.Count > 0 ? _clips[_clipIndex].Name : "(no clips)";
        string target = Path.GetFileName(_pngPath ?? _jsonPath);
        string layer = "";
        if (_doc.Layers != null && _doc.Layers.Count > 0)
            layer = _selLayer < 0
                ? $"   |   layer ALL of {_doc.Layers.Count} ([ ])"
                : $"   |   layer {_doc.Layers[_selLayer].Name}: " +
                  $"{string.Join(" ", _doc.Layers[_selLayer].Bones ?? new List<string>())} ([ ])";
        _spriteBatch.DrawString(_font,
            $"BIND {target}  rig {_skeleton.Name}{(_dirty ? "  *unsaved*" : "")}{layer}",
            new Vector2(16, 10), _dirty ? Color.Orange : Color.White);
        _spriteBatch.DrawString(_font,
            $"{_editMode.ToString().ToUpperInvariant()} (Tab)   |   preview {(_preview ? "ON" : "off")} (G)   |   " +
            $"clip {clip} {(_playing ? "PLAYING" : "")} (Space , .)",
            new Vector2(16, 28), new Color(160, 170, 185));
        _spriteBatch.DrawString(_font,
            $"rig scale {1f / _doc.ImageToRigScale:0.00} px/unit (Shift+wheel)   |   zoom {_zoom:0.00} (wheel, Home)   |   H help   Ctrl-S save",
            new Vector2(16, 46), new Color(130, 140, 155));
        if (_skinError != null)
            _spriteBatch.DrawString(_font, $"skin bake failed: {_skinError}", new Vector2(16, 64), Color.OrangeRed);
    }

    private static readonly (string Group, string Keys)[] HelpRows =
    {
        ("Pose",    "drag joint = aim bone at cursor    Tab = Rotate/Resize (Resize also stretches the BINDING length)"),
        ("Place",   "drag root joint = move rig over image    Shift+wheel = scale rig"),
        ("View",    "wheel = zoom at cursor    right/middle-drag or arrows = pan    Home = fit"),
        ("Preview", "G = deformed skin on/off (bind pose = should match the art)    Space = play clip    , . = cycle clip"),
        ("Layers",  "[ ] = cycle selected layer (dims others, outlines its mesh in preview; header shows its bones)"),
        ("Style",   "B = part styling on/off (black outline + translucent parts; on by default)"),
        ("File",    "Ctrl-S save binding json    Esc quit"),
    };

    private void DrawHelpOverlay()
    {
        if (!_showHelp) return;
        int x = 30, y = 110;
        var panel = new Rectangle(x - 14, y - 30, W - 60, HelpRows.Length * 28 + 50);
        _spriteBatch.Draw(_pixel, panel, new Color(16, 18, 26, 240));
        _spriteBatch.DrawString(_font, "BIND MODE CONTROLS", new Vector2(x, y - 22), new Color(150, 200, 255));
        for (int i = 0; i < HelpRows.Length; i++)
        {
            int ry = y + 8 + i * 28;
            _spriteBatch.DrawString(_font, HelpRows[i].Group, new Vector2(x, ry), new Color(255, 200, 120));
            _spriteBatch.DrawString(_font, HelpRows[i].Keys,  new Vector2(x + 90, ry), new Color(205, 210, 220));
        }
    }

    private bool Pressed(KeyboardState kb, Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);

    private static void Premultiply(Texture2D tex)
    {
        var px = new Color[tex.Width * tex.Height];
        tex.GetData(px);
        for (int i = 0; i < px.Length; i++)
            px[i] = Color.FromNonPremultiplied(px[i].R, px[i].G, px[i].B, px[i].A);
        tex.SetData(px);
    }

    // Repo root: walk up from the binary looking for the solution sentinel (the editor
    // runs from bin/), falling back to the current directory.
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

    // The PNG argument: an existing path as-given, else resolved under SpriteBindings/
    // at the repo root (with .png appended if no extension).
    private static string ResolvePng(string repoRoot, string arg)
    {
        if (File.Exists(arg)) return Path.GetFullPath(arg);
        string name = Path.HasExtension(arg) ? arg : arg + ".png";
        string candidate = Path.Combine(repoRoot, "SpriteBindings", name);
        if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException(
            $"PNG not found: tried '{arg}' and '{candidate}'. Put the drawing in SpriteBindings/.");
    }
}
