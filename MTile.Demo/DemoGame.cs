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
    private float        _floorLocalY;   // bind-pose sole height (skeleton-local), the floor line

    private string                   _dir;
    private List<AnimationDocument>  _docs = new();
    private int  _selected = -1;
    private string[] _typeOptions;       // T cycles Doc.Type through these

    private float _scrubT;               // playhead position [0,1]
    private int   _activeKey = -1;       // editable keyframe index, or -1 (interpolated)
    private bool  _dirty;                // any AnimationDocument unsaved
    private bool  _skelDirty;            // rig (bind translation/scale) unsaved — written on Ctrl-S

    private int           _dragBone = -1, _hoverBone = -1;
    private int           _dragBar  = -1;     // keyframe bar being moved
    private bool          _dragPlayhead;
    private enum EditMode { Rotate, Translate, Resize }
    private EditMode      _editMode = EditMode.Rotate;   // Tab cycles
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

        _dir = FindStatesDir();
        // Authored-only content: the rig comes from Skeletons/biped.json and throws
        // if missing (no procedural fallback), and the clip list is exactly what's
        // on disk in SkeletonStates/ (no seed autogeneration). N / C create clips.
        _skeleton = SkeletonExamples.Biped();
        _pose = _skeleton.CreatePose();
        _kfA  = _skeleton.CreatePose();
        _kfB  = _skeleton.CreatePose();

        // Floor line = the bind-pose sole (lowest joint/tip), so authored feet have a
        // ground reference to plant against. Matches CharacterAnimator's sole logic.
        // Recomputed whenever the rig bind changes (see RecomputeFloorLine).
        RecomputeFloorLine();

        UpdateRoot();

        _typeOptions = BuildTypeOptions();
        _docs = AnimationStore.LoadAll(_dir);
        if (_docs.Count > 0) SelectAnimation(0);

        Console.WriteLine($"Animation editor - states in: {_dir}");
        Console.WriteLine("Controls cheatsheet: MTile.Demo/CONTROLS.md");
        Console.WriteLine("Tab cycle edit mode | M+click mark | F flip | K sample | Space play | Del | [ ] dur | L loop | Ctrl-S | N new | C clone");
    }

    protected override void Update(GameTime gameTime)
    {
        var ms = Mouse.GetState();
        var kb = Keyboard.GetState();
        var mp = new Vector2(ms.X, ms.Y);
        if (kb.IsKeyDown(Keys.Escape)) Exit();
        UpdateRoot();   // tracks window size + the flip toggle

        bool ctrl        = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        bool mDown       = kb.IsKeyDown(Keys.M);   // M + click toggles a node's contact mark
        bool leftDown    = ms.LeftButton == ButtonState.Pressed;
        bool leftPressed = leftDown && _prevMs.LeftButton == ButtonState.Released;
        bool leftUp      = !leftDown && _prevMs.LeftButton == ButtonState.Pressed;

        if (ctrl && Pressed(kb, Keys.S)) SaveAll();
        if (Pressed(kb, Keys.N)) NewAnimation();
        if (Pressed(kb, Keys.C)) CloneAnimation();
        if (Pressed(kb, Keys.K)) SampleKeyframe();
        if (Pressed(kb, Keys.Tab)) _editMode = (EditMode)(((int)_editMode + 1) % 3);
        if (Pressed(kb, Keys.F)) FlipAnimation();
        if (Pressed(kb, Keys.Space)) TogglePlay();
        if (Pressed(kb, Keys.Delete)) DeleteActiveKeyframe();
        if (Doc != null)
        {
            bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
            if (Pressed(kb, Keys.OemOpenBrackets))  { Doc.Duration = MathF.Max(0.1f, Doc.Duration - 0.1f); _dirty = true; }
            if (Pressed(kb, Keys.OemCloseBrackets)) { Doc.Duration += 0.1f; _dirty = true; }
            if (Pressed(kb, Keys.L))                { Doc.Loop = !Doc.Loop; _dirty = true; }
            if (Pressed(kb, Keys.R))                { Doc.Region = (AnimRegion)(((int)Doc.Region + 1) % 3); _dirty = true; }
            if (Pressed(kb, Keys.T))                CycleType(shift ? -1 : +1);
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
            else if (!_playing)
            {
                int bone = _activeKey >= 0 ? PickJoint(world, mp) : -1;
                if (mDown && bone >= 0) ToggleContact(bone);   // M + click marks/unmarks the node
                else _dragBone = bone;
            }
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
            var touched = EditBone(world, _dragBone, mp);
            if ((touched & EditTouched.Pose) != 0)
            {
                Doc.Keyframes[_activeKey].Bones = PoseData.Capture(_pose);
                _dirty = true;
            }
            if ((touched & EditTouched.Rig) != 0)
            {
                _skelDirty = true;
                RecomputeFloorLine();
            }
        }

        if (leftUp) { _dragBone = -1; _dragBar = -1; _dragPlayhead = false; }

        _prevMs = ms;
        _prevKb = kb;
        base.Update(gameTime);
    }

    // Which side(s) of the data model an edit mutates this frame. The drag handler
    // routes the dirty flag accordingly: Pose touches force a keyframe re-capture +
    // anim-dirty; Rig touches set the skeleton-dirty flag for Ctrl-S.
    [System.Flags]
    private enum EditTouched { None = 0, Pose = 1, Rig = 2 }

    // Edit-mode write split:
    //   • Rotate    — pose only. Pivots the bone around its parent joint; the
    //                 keyframe absorbs the rotation. The bind position is unchanged.
    //   • Translate — rig only. Moves the joint in the parent frame; rewrites the
    //                 bone's bind translation, which every keyframe re-reads via
    //                 SetToBind(). No pose channel touched.
    //   • Resize    — split. Same joint-move as Translate goes to the rig, AND the
    //                 angle the move subtends rolls the pose rotation so the subtree
    //                 visibly follows the cursor.
    private EditTouched EditBone(Affine2[] world, int bone, Vector2 mp)
    {
        int parent = _skeleton.Bones[bone].Parent;
        Affine2 pw  = parent < 0 ? _root : world[parent];

        if (_editMode == EditMode.Resize)
        {
            // R*T splits the gesture cleanly: the angular delta to the cursor is the
            // pose rotation change; the radial delta is the bind length change.
            //   rotation roll  → pose   (matches the Rotate mode's contribution)
            //   length scale   → rig    (|bind| is the bone's rest length)
            // Joint lands at the cursor: R(θ+δ) × (oldBind × scale) traces out a
            // direct line to mp in parent's frame.
            Vector2 pivot     = pw.Translation;
            Vector2 boneVec   = world[bone].Translation - pivot;
            Vector2 cursorVec = mp - pivot;
            if (boneVec.LengthSquared() < 1e-4f || cursorVec.LengthSquared() < 1e-4f)
                return EditTouched.None;
            _pose.Local[bone].Rotation += SignedAngle(boneVec, cursorVec);
            float scale = cursorVec.Length() / boneVec.Length();
            SetBoneBindTranslation(bone, _skeleton.Bones[bone].Bind.Translation * scale);
            return EditTouched.Pose | EditTouched.Rig;
        }
        if (_editMode == EditMode.Rotate && parent >= 0)
        {
            // Drag the clicked joint around its parent. Under the R*T composition,
            // bone.Rotation orbits the joint around world[parent].Translation on its
            // bind radius and drags the subtree along — siblings are unaffected
            // because each sibling's translation is rotated by its own θ, not this
            // one's. Self-stabilizing: after the edit the joint sits at the cursor's
            // angle, so next frame's δ ≈ 0.
            Vector2 pivot     = world[parent].Translation;
            Vector2 boneVec   = world[bone].Translation - pivot;
            Vector2 cursorVec = mp - pivot;
            if (boneVec.LengthSquared() < 1e-4f || cursorVec.LengthSquared() < 1e-4f)
                return EditTouched.None;
            _pose.Local[bone].Rotation += SignedAngle(boneVec, cursorVec);
            return EditTouched.Pose;
        }
        // Translate — pure rig edit. R·T·S: the joint sits at R(θ_pose)·t_bind in the
        // parent frame, so the bind that puts the joint under the cursor is the cursor
        // un-rotated by the bone's own pose rotation (skipping this made any bone with
        // a nonzero keyframe rotation leap to the rotated cursor position).
        SetBoneBindTranslation(bone,
            Rotate(pw.Inverse().TransformPoint(mp), -_pose.Local[bone].Rotation));
        return EditTouched.Rig;
    }

    // Replace a bone's bind translation on the live rig, and mirror it into the
    // working pose so the on-screen figure reflects the change immediately. Every
    // other keyframe picks the new bind up on its next SetToBind() (i.e. the next
    // time it's selected or sampled). Persisted on Ctrl-S via SaveAll().
    private void SetBoneBindTranslation(int i, Vector2 t)
    {
        var old = _skeleton.Bones[i];
        var newBind = new BoneTransform(t, old.Bind.Rotation, old.Bind.Scale);
        _skeleton.Bones[i] = new Bone(old.Name, old.Parent, newBind, old.Length);
        _pose.Local[i].Translation = t;
    }

    // Floor line tracks the bind-pose sole; recompute after any rig edit so the
    // ground reference stays consistent with the live silhouette.
    private void RecomputeFloorLine()
    {
        var bindWorld = _skeleton.CreatePose().ComputeWorld(Affine2.Identity);
        _floorLocalY = 0f;
        for (int i = 0; i < _skeleton.Count; i++)
        {
            _floorLocalY = MathF.Max(_floorLocalY, bindWorld[i].Translation.Y);
            _floorLocalY = MathF.Max(_floorLocalY, bindWorld[i].TransformPoint(new Vector2(_skeleton.Bones[i].Length, 0f)).Y);
        }
    }

    // Rig placement in the editor: centered in the working area, scaled. Recomputed
    // each frame so it tracks window size.
    private void UpdateRoot()
    {
        float cx = SidebarW + (W - SidebarW) / 2f;
        float cy = H * 0.48f;
        _root = Affine2.FromTRS(new Vector2(cx, cy), 0f, new Vector2(RigScale, RigScale));
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
        // Floor reference: a dashed horizontal line at the bind-pose sole height.
        float floorY = _root.TransformPoint(new Vector2(0f, _floorLocalY)).Y;
        DrawDashedH(floorY, SidebarW + 20, W - 20, new Color(90, 110, 95), 9f, 7f);
        _spriteBatch.DrawString(_font, "floor", new Vector2(SidebarW + 20, floorY + 3), new Color(90, 110, 95));

        // Vault reference block: sits on the floor a hair ahead of the rig in the +X
        // (canonical facing) direction so the foot/hip arc can be authored against a
        // concrete obstacle. Runtime mirrors clips by facing, so the block lives on the
        // forward side here regardless of the player's in-game facing.
        DrawVaultBlock();

        var style = SkeletonDrawStyle.Default;
        style.BoneThickness = 3f;
        style.JointRadius   = 0f;
        // Dim the figure when on an interpolated (non-editable) frame.
        style.BoneColor = _activeKey >= 0 ? Color.White : new Color(150, 150, 160);
        SkeletonRenderer.Draw(_draw, _pose, _root, style);

        var world = _pose.ComputeWorld(_root);
        var contacts = _activeKey >= 0 ? Doc.Keyframes[_activeKey].Contacts : null;
        for (int i = 0; i < world.Length; i++)
        {
            Vector2 p = world[i].Translation;
            if (_activeKey < 0) { _draw.Ring(p, 4f, new Color(90, 95, 110), 10, 1f); continue; }

            // Contact-labeled nodes get a green halo behind the normal marker.
            if (HasContact(contacts, _skeleton.Bones[i].Name))
                _draw.Disc(p, 8f, new Color(70, 220, 110));

            if (i == _dragBone)       _draw.Disc(p, 6f, Color.White);
            else if (i == _hoverBone) _draw.Disc(p, 6f, Color.LightYellow);
            else _draw.Ring(p, 5f, _skeleton.Bones[i].IsRoot ? Color.Yellow : Color.OrangeRed, 12, 1.5f);
        }
    }

    // Reference obstacle drawn under the rig only while authoring a Vault clip. Sized
    // in skeleton-local units (the rig's natural frame) so it scales with RigScale.
    // ~one game-tile wide, knee-ish tall — tune to taste; nothing reads this geometry.
    private void DrawVaultBlock()
    {
        if (Doc?.Type != "Vault") return;
        const float W_ = 18f, H_ = 14f, OffsetX = 8f;

        Vector2 tl = _root.TransformPoint(new Vector2(OffsetX,      _floorLocalY - H_));
        Vector2 br = _root.TransformPoint(new Vector2(OffsetX + W_, _floorLocalY));
        var rect = new Rectangle((int)tl.X, (int)tl.Y, (int)(br.X - tl.X), (int)(br.Y - tl.Y));

        Fill(rect, new Color(82, 64, 48));
        _draw.Line(new Vector2(rect.Left,  rect.Top),    new Vector2(rect.Right, rect.Top),    new Color(150, 118, 88), 2f);
        _draw.Line(new Vector2(rect.Left,  rect.Top),    new Vector2(rect.Left,  rect.Bottom), new Color(48, 36, 26), 1f);
        _draw.Line(new Vector2(rect.Right, rect.Top),    new Vector2(rect.Right, rect.Bottom), new Color(48, 36, 26), 1f);
    }

    private void DrawDashedH(float y, float x0, float x1, Color c, float dash, float gap)
    {
        for (float x = x0; x < x1; x += dash + gap)
            _draw.Line(new Vector2(x, y), new Vector2(MathF.Min(x + dash, x1), y), c, 1f);
    }

    private static bool HasContact(List<ContactLabel> contacts, string node)
    {
        if (contacts == null) return false;
        foreach (var c in contacts) if (c.Node == node) return true;
        return false;
    }

    // Mirror the whole animation across a vertical axis, in the DATA (persists on
    // save). Because translations now live in the shared skeleton, a flip is purely a
    // rotation-side operation: for every left/right bone pair, swap their rotations
    // and negate both (so the limb that was sweeping forward on the left now sweeps
    // forward on the right, and vice versa). Unpaired bones (hip, chest, head) just
    // get their rotation negated. Contact node names are swapped l↔r in step.
    // Press again to flip back. Use this to face clips the game's canonical direction
    // (the runtime mirrors by player facing, so clips are authored one way).
    private void FlipAnimation()
    {
        var doc = Doc; if (doc == null) return;
        foreach (var kf in doc.Keyframes)
        {
            if (kf.Bones != null)
            {
                var byName = new Dictionary<string, PoseBoneEntry>(kf.Bones.Count);
                foreach (var e in kf.Bones) if (e.Bone != null) byName[e.Bone] = e;
                var done = new HashSet<string>();
                foreach (var e in kf.Bones)
                {
                    if (e.Bone == null || !done.Add(e.Bone)) continue;
                    string mate = MirrorBoneName(e.Bone);
                    if (mate != null && byName.TryGetValue(mate, out var m) && done.Add(mate))
                    {
                        float er = e.Rotation, mr = m.Rotation;
                        e.Rotation = -mr;
                        m.Rotation = -er;
                    }
                    else
                    {
                        e.Rotation = -e.Rotation;
                    }
                }
            }
            if (kf.Contacts != null)
                foreach (var c in kf.Contacts)
                {
                    string m = MirrorBoneName(c.Node);
                    if (m != null) c.Node = m;
                }
        }
        _dirty = true;
        if (_activeKey >= 0) PoseData.Apply(doc.Keyframes[_activeKey].Bones, _pose);
        else                 SamplePose(_scrubT);
    }

    // Map a bone name to its left/right counterpart, or null if it's centerline.
    // Handles both suffix (`foot_l`) and infix (`leg_l_upper`) conventions.
    private static string MirrorBoneName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.EndsWith("_l")) return name.Substring(0, name.Length - 2) + "_r";
        if (name.EndsWith("_r")) return name.Substring(0, name.Length - 2) + "_l";
        int i = name.IndexOf("_l_"); if (i >= 0) return name.Substring(0, i) + "_r_" + name.Substring(i + 3);
        int j = name.IndexOf("_r_"); if (j >= 0) return name.Substring(0, j) + "_l_" + name.Substring(j + 3);
        return null;
    }

    // Toggle a SelfPlant contact label on a node for the active keyframe.
    private void ToggleContact(int bone)
    {
        if (Doc == null || _activeKey < 0 || bone < 0) return;
        var kf = Doc.Keyframes[_activeKey];
        kf.Contacts ??= new List<ContactLabel>();
        string node = _skeleton.Bones[bone].Name;
        int idx = -1;
        for (int i = 0; i < kf.Contacts.Count; i++) if (kf.Contacts[i].Node == node) { idx = i; break; }
        if (idx >= 0) kf.Contacts.RemoveAt(idx);
        else          kf.Contacts.Add(new ContactLabel { Node = node, Weight = 1f });
        _dirty = true;
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
        // Two dirty flags: animation keyframes (orange) and the rig itself (cyan).
        // Both clear on Ctrl-S; the rig flag means Skeletons/<rig>.json will be rewritten.
        bool anyDirty = _dirty || _skelDirty;
        string tag = (_dirty && _skelDirty) ? "  *unsaved (anim+rig)*"
                   : _dirty                 ? "  *unsaved*"
                   : _skelDirty             ? "  *unsaved rig*"
                                            : "";
        _spriteBatch.DrawString(_font, $"{title}{tag}",
            new Vector2(SidebarW + 16, 10), anyDirty ? Color.Orange : Color.White);
        _spriteBatch.DrawString(_font, state, new Vector2(SidebarW + 16, 28), stateColor);
        if (doc != null)
            _spriteBatch.DrawString(_font,
                $"dur {doc.Duration:0.0}s ([ ])  loop {(doc.Loop ? "on" : "off")} (L)  region {doc.Region} (R)  type (T)",
                new Vector2(SidebarW + 16, 46), new Color(160, 170, 185));
        _spriteBatch.DrawString(_font,
            $"mode: {_editMode.ToString().ToUpperInvariant()} (Tab) | M+click mark | F flip | Space play | K sample | Del | Ctrl-S | N | C clone",
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

    // Turn the current (possibly interpolated) pose into a new editable keyframe,
    // inheriting the contact marks active at the playhead by default.
    private void SampleKeyframe()
    {
        var doc = Doc; if (doc == null) return;
        var kf = new AnimationKeyframe
        {
            Time = _scrubT,
            Bones = PoseData.Capture(_pose),
            Contacts = CloneContactsAt(_scrubT),
        };
        doc.Keyframes.Add(kf);
        doc.SortKeyframes();
        _activeKey = doc.Keyframes.IndexOf(kf);
        _dirty = true;
    }

    // Deep-copy the contact marks from the keyframe at or before `t` (the marks "in
    // effect" there), so a sampled keyframe carries them over rather than starting bare.
    private List<ContactLabel> CloneContactsAt(float t)
    {
        var doc = Doc;
        List<ContactLabel> src = null;
        for (int i = 0; i < doc.Keyframes.Count; i++)
        {
            if (doc.Keyframes[i].Time > t) break;
            if (doc.Keyframes[i].Contacts is { Count: > 0 }) src = doc.Keyframes[i].Contacts;
        }
        if (src == null) return null;
        var copy = new List<ContactLabel>(src.Count);
        foreach (var c in src) copy.Add(new ContactLabel { Node = c.Node, Weight = c.Weight, Source = c.Source });
        return copy;
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
        var doc = new AnimationDocument { Name = $"anim_{_docs.Count}", Type = "Misc", Skeleton = _skeleton.Name };
        doc.Keyframes.Add(new AnimationKeyframe { Time = 0f, Bones = PoseData.Capture(_skeleton.CreatePose()) });
        _docs.Add(doc);
        SelectAnimation(_docs.Count - 1);
        _dirty = true;
    }

    // Deep-copy the selected animation (all keyframes, poses, and contacts) into a new
    // document with a fresh name and no FilePath, so Save writes it as a separate file.
    // Use to fork a variant (e.g. derive a run from the walk) without touching the source.
    private void CloneAnimation()
    {
        var src = Doc; if (src == null) return;
        var copy = new AnimationDocument
        {
            Name     = UniqueName(src.Name + "_copy"),
            Type     = src.Type,
            Skeleton = src.Skeleton,
            Duration = src.Duration,
            Loop     = src.Loop,
            Region   = src.Region,
        };
        foreach (var kf in src.Keyframes)
            copy.Keyframes.Add(new AnimationKeyframe
            {
                Time     = kf.Time,
                Bones    = CloneBones(kf.Bones),
                Contacts = CloneContacts(kf.Contacts),
            });
        _docs.Add(copy);
        SelectAnimation(_docs.Count - 1);
        _dirty = true;
    }

    private string UniqueName(string baseName)
    {
        var taken = new HashSet<string>();
        foreach (var d in _docs) taken.Add(d.Name);
        if (!taken.Contains(baseName)) return baseName;
        for (int n = 2; ; n++)
            if (!taken.Contains($"{baseName}{n}")) return $"{baseName}{n}";
    }

    private static List<PoseBoneEntry> CloneBones(List<PoseBoneEntry> src)
    {
        if (src == null) return new List<PoseBoneEntry>();
        var copy = new List<PoseBoneEntry>(src.Count);
        foreach (var b in src)
            copy.Add(new PoseBoneEntry { Bone = b.Bone, Rotation = b.Rotation });
        return copy;
    }

    private static List<ContactLabel> CloneContacts(List<ContactLabel> src)
    {
        if (src == null) return null;
        var copy = new List<ContactLabel>(src.Count);
        foreach (var c in src) copy.Add(new ContactLabel { Node = c.Node, Weight = c.Weight, Source = c.Source });
        return copy;
    }

    private void SaveAll()
    {
        foreach (var d in _docs) AnimationStore.Save(d, _dir);
        _dirty = false;
        Console.WriteLine($"Saved {_docs.Count} animations to {_dir}");

        if (_skelDirty)
        {
            string skelDir = SkeletonsDir(_dir);
            var doc = SkeletonStore.Capture(_skeleton.Name, _skeleton);
            SkeletonStore.Save(doc, skelDir);
            _skelDirty = false;
            Console.WriteLine($"Saved rig '{_skeleton.Name}' to {skelDir}");
        }
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

    // Skeletons/ sits beside SkeletonStates/ at the repo root.
    private static string SkeletonsDir(string statesDir)
        => Path.Combine(Path.GetDirectoryName(statesDir) ?? Directory.GetCurrentDirectory(), "Skeletons");

    // Every Type a clip can carry: the movement categories (AnimClip names) plus
    // every concrete action state's class name (the runtime maps action overlay
    // clips by exact action name). Reflection is fine here — the editor is a
    // desktop-only tool, never compiled to WASM, and this runs once at startup.
    private static string[] BuildTypeOptions()
    {
        var list = new List<string>(Enum.GetNames<AnimClip>());
        var actions = new List<string>();
        foreach (var t in typeof(ActionState).Assembly.GetTypes())
            if (t.IsSubclassOf(typeof(ActionState)) && !t.IsAbstract
                && t.Name != nameof(NullAction) && t.Name != nameof(ReadyAction)
                && t.Name != nameof(RecoveryAction))
                actions.Add(t.Name);
        actions.Sort(StringComparer.Ordinal);
        list.AddRange(actions);
        list.Add("Misc");
        return list.ToArray();
    }

    // Cycle Doc.Type through the known options; a Type not in the list (hand-edited
    // JSON) restarts the cycle from the first option.
    private void CycleType(int dir)
    {
        var doc = Doc; if (doc == null) return;
        int n = _typeOptions.Length;
        int idx = Array.IndexOf(_typeOptions, doc.Type);
        idx = idx < 0 ? 0 : ((idx + dir) % n + n) % n;
        doc.Type = _typeOptions[idx];
        _dirty = true;
    }

}
