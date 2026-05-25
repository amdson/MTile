using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MTile;

namespace MTileDemo;

// Rudimentary skeleton ANIMATION editor.
//
//   • Left sidebar lists animations, grouped under a Type header. Click to load
//     (renders the first keyframe).
//   • Timeline slider below the main view: drag the playhead to scrub/interpolate
//     between keyframes. Keyframes show as bars; drag a bar to move it in time.
//   • Click a keyframe bar (or scrub exactly onto one) to make it the ACTIVE,
//     editable frame; then drag joints to edit that keyframe's pose.
//   • K  "samples" the current (possibly interpolated) pose into a NEW keyframe at
//        the playhead, and makes it active.
//   • Ctrl-S saves every animation to its JSON file. N new animation. Tab edit mode.
//
// Animations are AnimationDocuments (Animation/*.cs) — the format a runtime player
// can later consume. The editor never touches the sim.
public sealed class DemoGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;
    private Texture2D   _pixel;
    private SpriteFont  _font;
    private DrawContext _draw;

    private Skeleton     _skeleton;
    private SkeletonPose _pose;          // rendered / working pose
    private SkeletonPose _kfA, _kfB;     // scratch for interpolation
    private Affine2      _root;

    private string                   _dir;
    private List<AnimationDocument>  _docs = new();
    private int  _selected = -1;

    private float _scrubT;               // playhead position [0,1]
    private int   _activeKey = -1;       // editable keyframe index, or -1 (interpolated)
    private bool  _dirty;

    private int           _dragBone = -1, _hoverBone = -1;
    private int           _dragBar  = -1;     // keyframe bar being moved
    private bool          _dragPlayhead;
    private bool          _rotateMode = true;
    private bool          _playing;           // timeline playback
    private float         _playTime;          // seconds into playback
    private MouseState    _prevMs;
    private KeyboardState _prevKb;

    private const int   SidebarW = 250;
    private const int   PadTop   = 12;
    private const int   RowH     = 40;
    private const float RigScale = 5f;
    private const float PickR    = 12f;
    private const float SnapEps  = 0.012f;

    private AnimationDocument Doc => _selected >= 0 && _selected < _docs.Count ? _docs[_selected] : null;
    private int W => GraphicsDevice.Viewport.Width;
    private int H => GraphicsDevice.Viewport.Height;

    public DemoGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = 1040,
            PreferredBackBufferHeight = 680,
        };
        IsMouseVisible = true;
        Content.RootDirectory = "Content";
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _font = Content.Load<SpriteFont>("DebugFont");
        _draw = new DrawContext(_spriteBatch, _pixel);

        _skeleton = SkeletonExamples.Biped();
        _pose = _skeleton.CreatePose();
        _kfA  = _skeleton.CreatePose();
        _kfB  = _skeleton.CreatePose();

        float cx = SidebarW + (W - SidebarW) / 2f;
        float cy = H * 0.48f;
        _root = Affine2.FromTRS(new Vector2(cx, cy), 0f, new Vector2(RigScale, RigScale));

        _dir  = FindStatesDir();
        _docs = AnimationStore.LoadAll(_dir);

        // Add any seed animation whose Name isn't on disk yet — so new seed types
        // appear without clobbering existing files / edits.
        var existing = new HashSet<string>();
        foreach (var d in _docs) existing.Add(d.Name);
        bool added = false;
        foreach (var seed in BuildSeeds(_skeleton))
            if (!existing.Contains(seed.Name)) { AnimationStore.Save(seed, _dir); added = true; }
        if (added) _docs = AnimationStore.LoadAll(_dir);

        if (_docs.Count > 0) SelectAnimation(0);

        Console.WriteLine($"Animation editor - states in: {_dir}");
        Console.WriteLine("Click anim | scrub slider | drag keyframe bars | click bar+drag joints | K sample | Ctrl-S | N | Tab");
    }

    protected override void Update(GameTime gameTime)
    {
        var ms = Mouse.GetState();
        var kb = Keyboard.GetState();
        var mp = new Vector2(ms.X, ms.Y);
        if (kb.IsKeyDown(Keys.Escape)) Exit();

        bool ctrl        = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        bool leftDown    = ms.LeftButton == ButtonState.Pressed;
        bool leftPressed = leftDown && _prevMs.LeftButton == ButtonState.Released;
        bool leftUp      = !leftDown && _prevMs.LeftButton == ButtonState.Pressed;

        if (ctrl && Pressed(kb, Keys.S)) SaveAll();
        if (Pressed(kb, Keys.N)) NewAnimation();
        if (Pressed(kb, Keys.K)) SampleKeyframe();
        if (Pressed(kb, Keys.Tab)) _rotateMode = !_rotateMode;
        if (Pressed(kb, Keys.Space)) TogglePlay();
        if (Pressed(kb, Keys.Delete)) DeleteActiveKeyframe();
        if (Doc != null)
        {
            if (Pressed(kb, Keys.OemOpenBrackets))  { Doc.Duration = MathF.Max(0.1f, Doc.Duration - 0.1f); _dirty = true; }
            if (Pressed(kb, Keys.OemCloseBrackets)) { Doc.Duration += 0.1f; _dirty = true; }
            if (Pressed(kb, Keys.L))                { Doc.Loop = !Doc.Loop; _dirty = true; }
        }

        // Playback: advance the playhead per the animation's Duration/Loop.
        if (_playing && Doc != null)
        {
            _playTime += (float)gameTime.ElapsedGameTime.TotalSeconds;
            _scrubT = AnimationSampler.NormalizedTime(Doc, _playTime);
            _activeKey = -1;
            SamplePose(_scrubT);
        }

        var world = _pose.ComputeWorld(_root);
        _hoverBone = (!_playing && mp.X >= SidebarW && !InSlider(mp) && _dragBone < 0) ? PickJoint(world, mp) : _dragBone;

        if (leftPressed)
        {
            if (mp.X < SidebarW)
            {
                int row = (int)((mp.Y - PadTop) / RowH);
                if (row >= 0 && row < _docs.Count) SelectAnimation(row);
            }
            else if (!_playing && InSlider(mp))
            {
                int bar = PickKeyframeBar(mp);
                if (bar >= 0) { _dragBar = bar; SelectKeyframe(bar); }
                else          { _dragPlayhead = true; Scrub(XToTime(mp.X)); }
            }
            else if (!_playing) _dragBone = _activeKey >= 0 ? PickJoint(world, mp) : -1;
        }

        if (leftDown && _dragBar >= 0)
        {
            // Move the keyframe in time; re-sort and follow it (selection by identity).
            var kf = Doc.Keyframes[_dragBar];
            kf.Time = XToTime(mp.X);
            Doc.SortKeyframes();
            _dragBar = Doc.Keyframes.IndexOf(kf);
            _activeKey = _dragBar;
            _scrubT = kf.Time;
            _dirty = true;
        }
        else if (leftDown && _dragPlayhead)
        {
            Scrub(XToTime(mp.X));
        }
        else if (leftDown && _dragBone >= 0 && _activeKey >= 0)
        {
            EditBone(world, _dragBone, mp);
            Doc.Keyframes[_activeKey].Bones = PoseData.Capture(_pose);
            _dirty = true;
        }

        if (leftUp) { _dragBone = -1; _dragBar = -1; _dragPlayhead = false; }

        _prevMs = ms;
        _prevKb = kb;
        base.Update(gameTime);
    }

    private void EditBone(Affine2[] world, int bone, Vector2 mp)
    {
        int parent = _skeleton.Bones[bone].Parent;
        if (_rotateMode && parent >= 0)
        {
            Vector2 pivot = world[parent].Translation;
            float delta = SignedAngle(world[bone].Translation - pivot, mp - pivot);
            ref var local = ref _pose.Local[bone];
            local.Translation = Rotate(local.Translation, delta);
            local.Rotation   += delta;
        }
        else
        {
            Affine2 pw = parent < 0 ? _root : world[parent];
            _pose.Local[bone].Translation = pw.Inverse().TransformPoint(mp);
        }
    }

    // === draw ================================================================

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(22, 24, 30));
        _spriteBatch.Begin();
        DrawSidebar();
        DrawEditor();
        DrawTimeline();
        DrawHeader();
        _spriteBatch.End();
        base.Draw(gameTime);
    }

    private void DrawSidebar()
    {
        Fill(new Rectangle(0, 0, SidebarW, H), new Color(30, 33, 42));
        string lastType = null;
        for (int i = 0; i < _docs.Count; i++)
        {
            var d = _docs[i];
            int y = PadTop + i * RowH;
            if (i == _selected) Fill(new Rectangle(0, y - 2, SidebarW, RowH), new Color(60, 90, 140));

            Color typeColor = d.Type != lastType ? new Color(150, 200, 255) : new Color(110, 120, 140);
            _spriteBatch.DrawString(_font, $"{d.Type}", new Vector2(10, y), typeColor);
            _spriteBatch.DrawString(_font, $"{d.Name}  ({d.Keyframes.Count}kf)", new Vector2(20, y + 16),
                i == _selected ? Color.White : new Color(200, 205, 215));
            lastType = d.Type;
        }
    }

    private void DrawEditor()
    {
        var style = SkeletonDrawStyle.Default;
        style.BoneThickness = 3f;
        style.JointRadius   = 0f;
        // Dim the figure when on an interpolated (non-editable) frame.
        style.BoneColor = _activeKey >= 0 ? Color.White : new Color(150, 150, 160);
        SkeletonRenderer.Draw(_draw, _pose, _root, style);

        var world = _pose.ComputeWorld(_root);
        for (int i = 0; i < world.Length; i++)
        {
            Vector2 p = world[i].Translation;
            if (_activeKey < 0) { _draw.Ring(p, 4f, new Color(90, 95, 110), 10, 1f); continue; }

            if (i == _dragBone)       _draw.Disc(p, 6f, Color.White);
            else if (i == _hoverBone) _draw.Disc(p, 6f, Color.LightYellow);
            else _draw.Ring(p, 5f, _skeleton.Bones[i].IsRoot ? Color.Yellow : Color.OrangeRed, 12, 1.5f);
        }
    }

    private void DrawTimeline()
    {
        var doc = Doc;
        if (doc == null) return;
        float y = TrackY;
        Fill(new Rectangle(SidebarW, (int)y - 28, W - SidebarW, 56), new Color(28, 30, 38));
        _draw.Line(new Vector2(TrackX0, y), new Vector2(TrackX1, y), new Color(80, 85, 100), 2f);

        // Keyframe bars.
        for (int i = 0; i < doc.Keyframes.Count; i++)
        {
            float x = TimeToX(doc.Keyframes[i].Time);
            Color c = i == _activeKey ? Color.White : new Color(120, 200, 255);
            _draw.Line(new Vector2(x, y - 14), new Vector2(x, y + 14), c, i == _activeKey ? 3f : 2f);
        }

        // Playhead.
        float px = TimeToX(_scrubT);
        _draw.Line(new Vector2(px, y - 20), new Vector2(px, y + 20), new Color(255, 180, 60), 1.5f);
    }

    private void DrawHeader()
    {
        var doc = Doc;
        string title = doc != null ? $"[{doc.Type}] {doc.Name}" : "(none)";
        string state = _playing      ? $"PLAYING @ t={_scrubT:0.00}"
                     : _activeKey >= 0 ? $"keyframe {_activeKey} @ t={_scrubT:0.00}  (editable)"
                                       : $"interpolated @ t={_scrubT:0.00}  (K to sample)";
        Color stateColor = _playing ? new Color(255, 200, 80)
                         : _activeKey >= 0 ? new Color(150, 230, 150) : new Color(150, 160, 175);
        _spriteBatch.DrawString(_font, $"{title}{(_dirty ? "  *unsaved*" : "")}",
            new Vector2(SidebarW + 16, 10), _dirty ? Color.Orange : Color.White);
        _spriteBatch.DrawString(_font, state, new Vector2(SidebarW + 16, 28), stateColor);
        if (doc != null)
            _spriteBatch.DrawString(_font, $"dur {doc.Duration:0.0}s ([ ])  loop {(doc.Loop ? "on" : "off")} (L)",
                new Vector2(SidebarW + 16, 46), new Color(160, 170, 185));
        _spriteBatch.DrawString(_font,
            $"mode: {(_rotateMode ? "ROTATE" : "TRANSLATE")} (Tab) | Space play | K sample | Del delete | Ctrl-S | N",
            new Vector2(SidebarW + 16, 64), new Color(130, 140, 155));
    }

    // === timeline geometry ===================================================

    private float TrackX0 => SidebarW + 40;
    private float TrackX1 => W - 40;
    private float TrackY  => H - 60;
    private float TimeToX(float t) => TrackX0 + t * (TrackX1 - TrackX0);
    private float XToTime(float x) => MathHelper.Clamp((x - TrackX0) / (TrackX1 - TrackX0), 0f, 1f);
    private bool  InSlider(Vector2 p) => p.X >= SidebarW && p.Y >= TrackY - 28 && p.Y <= TrackY + 28;

    private int PickKeyframeBar(Vector2 mp)
    {
        var doc = Doc; if (doc == null) return -1;
        int best = -1; float bestD = 8f;
        for (int i = 0; i < doc.Keyframes.Count; i++)
        {
            float d = MathF.Abs(TimeToX(doc.Keyframes[i].Time) - mp.X);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

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

    // === animation / keyframe state ==========================================

    private void SelectAnimation(int i)
    {
        _playing = false;
        _selected = i;
        var doc = _docs[i];
        if (doc.Keyframes.Count == 0)
            doc.Keyframes.Add(new AnimationKeyframe { Time = 0f, Bones = PoseData.Capture(_skeleton.CreatePose()) });
        doc.SortKeyframes();
        SelectKeyframe(0);          // render the first frame
    }

    private void SelectKeyframe(int k)
    {
        _activeKey = k;
        _scrubT = Doc.Keyframes[k].Time;
        PoseData.Apply(Doc.Keyframes[k].Bones, _pose);
        _dragBone = -1;
    }

    // Scrub the playhead: snap to a keyframe if close (then it's editable), else
    // show the interpolated pose.
    private void Scrub(float t)
    {
        _scrubT = t;
        int k = FindKeyAt(t);
        _activeKey = k;
        if (k >= 0) PoseData.Apply(Doc.Keyframes[k].Bones, _pose);
        else SamplePose(t);
    }

    private int FindKeyAt(float t)
    {
        var doc = Doc; if (doc == null) return -1;
        for (int i = 0; i < doc.Keyframes.Count; i++)
            if (MathF.Abs(doc.Keyframes[i].Time - t) <= SnapEps) return i;
        return -1;
    }

    private void SamplePose(float t)
    {
        if (Doc == null) { _pose.SetToBind(); return; }
        AnimationSampler.SampleNormalized(Doc, t, _kfA, _kfB, _pose);
    }

    // Turn the current (possibly interpolated) pose into a new editable keyframe.
    private void SampleKeyframe()
    {
        var doc = Doc; if (doc == null) return;
        var kf = new AnimationKeyframe { Time = _scrubT, Bones = PoseData.Capture(_pose) };
        doc.Keyframes.Add(kf);
        doc.SortKeyframes();
        _activeKey = doc.Keyframes.IndexOf(kf);
        _dirty = true;
    }

    private void TogglePlay()
    {
        _playing = !_playing;
        if (_playing) { _playTime = _scrubT * (Doc?.Duration ?? 1f); _dragBone = -1; }
        else Scrub(_scrubT);   // settle back onto a keyframe if the playhead landed on one
    }

    private void DeleteActiveKeyframe()
    {
        var doc = Doc;
        if (_playing || doc == null || _activeKey < 0 || doc.Keyframes.Count <= 1) return;
        doc.Keyframes.RemoveAt(_activeKey);
        _dirty = true;
        SelectKeyframe(Math.Min(_activeKey, doc.Keyframes.Count - 1));
    }

    private void NewAnimation()
    {
        var doc = new AnimationDocument { Name = $"anim_{_docs.Count}", Type = "Misc" };
        doc.Keyframes.Add(new AnimationKeyframe { Time = 0f, Bones = PoseData.Capture(_skeleton.CreatePose()) });
        _docs.Add(doc);
        SelectAnimation(_docs.Count - 1);
        _dirty = true;
    }

    private void SaveAll()
    {
        foreach (var d in _docs) AnimationStore.Save(d, _dir);
        _dirty = false;
        Console.WriteLine($"Saved {_docs.Count} animations to {_dir}");
    }

    // === helpers =============================================================

    private void Fill(Rectangle r, Color c) => _spriteBatch.Draw(_pixel, r, c);
    private bool Pressed(KeyboardState kb, Keys k) => kb.IsKeyDown(k) && _prevKb.IsKeyUp(k);

    private static float SignedAngle(Vector2 from, Vector2 to)
        => MathF.Atan2(from.X * to.Y - from.Y * to.X, from.X * to.X + from.Y * to.Y);

    private static Vector2 Rotate(Vector2 v, float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static string FindStatesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "MTile.sln")))
                return Path.Combine(d.FullName, "SkeletonStates");
            d = d.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "SkeletonStates");
    }

    // --- seed animations ------------------------------------------------------

    private static IEnumerable<AnimationDocument> BuildSeeds(Skeleton s)
    {
        var idle = new AnimationDocument { Name = "idle", Type = "Idle", Duration = 1.5f, Loop = true };
        idle.Keyframes.Add(KF(0f, s.CreatePose()));
        yield return idle;

        var wave = new AnimationDocument { Name = "wave", Type = "Misc", Duration = 1.2f, Loop = true };
        wave.Keyframes.Add(KF(0f, s.CreatePose()));
        wave.Keyframes.Add(KF(1f, Pose(s, ("arm_r_upper", 2.2f), ("arm_r_lower", 0.6f))));
        yield return wave;

        var walk = new AnimationDocument { Name = "walk", Type = "Walk", Duration = 0.8f, Loop = true };
        walk.Keyframes.Add(KF(0f,   Pose(s, ("leg_l_upper", 0.5f), ("leg_r_upper", -0.5f), ("arm_l_upper", -0.4f), ("arm_r_upper", 0.4f))));
        walk.Keyframes.Add(KF(0.5f, Pose(s, ("leg_l_upper", -0.5f), ("leg_r_upper", 0.5f), ("arm_l_upper", 0.4f), ("arm_r_upper", -0.4f))));
        walk.Keyframes.Add(KF(1f,   Pose(s, ("leg_l_upper", 0.5f), ("leg_r_upper", -0.5f), ("arm_l_upper", -0.4f), ("arm_r_upper", 0.4f))));
        yield return walk;

        // Crouch: a single held pose (deep knees, sunk hip, slight hunch).
        var crouchPose = Pose(s, ("leg_l_upper", 0.7f), ("leg_l_lower", -1.3f),
                                 ("leg_r_upper", -0.7f), ("leg_r_lower", 1.3f),
                                 ("chest", 0.25f), ("arm_l_upper", -0.3f), ("arm_r_upper", 0.3f));
        Sink(s, crouchPose, 7f);
        var crouch = new AnimationDocument { Name = "crouch", Type = "Crouch", Duration = 0.3f, Loop = false };
        crouch.Keyframes.Add(KF(0f, crouchPose));
        yield return crouch;

        // Jump: anticipation (slight crouch) -> extension (arms up, knees tucked).
        // Loop off so it holds the extension while rising.
        var jumpCrouch = Pose(s, ("leg_l_upper", 0.5f), ("leg_l_lower", -0.9f),
                                 ("leg_r_upper", -0.5f), ("leg_r_lower", 0.9f),
                                 ("arm_l_upper", -0.2f), ("arm_r_upper", 0.2f));
        Sink(s, jumpCrouch, 5f);
        var jumpExtend = Pose(s, ("arm_l_upper", -1.4f), ("arm_r_upper", 1.4f),
                                 ("leg_l_upper", 0.5f), ("leg_l_lower", -0.8f),
                                 ("leg_r_upper", -0.4f), ("leg_r_lower", 0.8f));
        var jump = new AnimationDocument { Name = "jump", Type = "Jump", Duration = 0.35f, Loop = false };
        jump.Keyframes.Add(KF(0f, jumpCrouch));
        jump.Keyframes.Add(KF(1f, jumpExtend));
        yield return jump;

        // Fall: arms up/out bracing, legs reaching down for the ground (held pose).
        var fallPose = Pose(s, ("arm_l_upper", -1.8f), ("arm_r_upper", 1.8f), ("chest", -0.1f),
                               ("leg_l_upper", 0.25f), ("leg_l_lower", 0.5f),
                               ("leg_r_upper", -0.35f), ("leg_r_lower", -0.5f));
        var fall = new AnimationDocument { Name = "fall", Type = "Fall", Duration = 0.4f, Loop = false };
        fall.Keyframes.Add(KF(0f, fallPose));
        yield return fall;

        // Backpedal cycle. Authored lean-neutral — the animator adds the directional
        // (lean-back) cue on top, same as it does for the forward walk.
        var walkback = new AnimationDocument { Name = "walkback", Type = "WalkBack", Duration = 0.9f, Loop = true };
        walkback.Keyframes.Add(KF(0f,   Pose(s, ("leg_l_upper", -0.4f), ("leg_r_upper", 0.4f), ("arm_l_upper", 0.3f), ("arm_r_upper", -0.3f))));
        walkback.Keyframes.Add(KF(0.5f, Pose(s, ("leg_l_upper", 0.4f), ("leg_r_upper", -0.4f), ("arm_l_upper", -0.3f), ("arm_r_upper", 0.3f))));
        walkback.Keyframes.Add(KF(1f,   Pose(s, ("leg_l_upper", -0.4f), ("leg_r_upper", 0.4f), ("arm_l_upper", 0.3f), ("arm_r_upper", -0.3f))));
        yield return walkback;

        // Vault: tuck over the obstacle -> extend legs forward to land. One-shot.
        var vaultTuck = Pose(s, ("chest", 0.6f), ("arm_l_upper", -0.8f), ("arm_r_upper", 0.8f),
                                ("leg_l_upper", 1.0f), ("leg_l_lower", -1.4f),
                                ("leg_r_upper", 1.0f), ("leg_r_lower", -1.4f));
        Sink(s, vaultTuck, 4f);
        var vaultExtend = Pose(s, ("chest", 0.4f), ("arm_l_upper", -1.0f), ("arm_r_upper", 1.0f),
                                  ("leg_l_upper", -0.6f), ("leg_l_lower", 0.3f),
                                  ("leg_r_upper", -0.6f), ("leg_r_lower", -0.3f));
        var vault = new AnimationDocument { Name = "vault", Type = "Vault", Duration = 0.45f, Loop = false };
        vault.Keyframes.Add(KF(0f, vaultTuck));
        vault.Keyframes.Add(KF(1f, vaultExtend));
        yield return vault;
    }

    // Lower the hip by `dy` world units (Y-down) — for crouch/anticipation poses.
    private static void Sink(Skeleton s, SkeletonPose p, float dy)
    {
        int hip = s.IndexOf("hip");
        if (hip >= 0) p.Local[hip].Translation += new Vector2(0f, dy);
    }

    private static AnimationKeyframe KF(float t, SkeletonPose pose)
        => new() { Time = t, Bones = PoseData.Capture(pose) };

    private static SkeletonPose Pose(Skeleton s, params (string bone, float rot)[] rots)
    {
        var p = s.CreatePose();
        foreach (var (bone, rot) in rots)
        {
            int i = s.IndexOf(bone);
            if (i < 0) continue;
            var t = p.Local[i];
            t.Rotation = rot;
            p.SetLocal(i, t);
        }
        return p;
    }
}
