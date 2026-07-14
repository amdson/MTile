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

    // The shared base rig (Skeletons/<name>.json). `_skeleton` is the working rig the
    // editor poses/draws: base + the ACTIVE clip's ExtraBones composed in, rebuilt on
    // every clip switch. Keeping them separate means clip-local bones (a slash's knife)
    // never leak into the base rig on save, and walk/idle don't show another clip's knife.
    private Skeleton     _baseSkeleton;
    private Skeleton     _skeleton;
    private SkeletonPose _pose;          // rendered / working pose
    private SkeletonPose _kfA, _kfB, _kfC, _kfD;   // scratch for the C1 keyframe quad (iL,i0,i1,iR)
    private Affine2      _root;
    private Vector2      _playerOffset;   // global player-position offset folded into _root, so the
                                          // skeleton AND root-parented additions (e.g. "com") move together.
                                          // Drag the root joint to move, arrows nudge, Home resets.
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
    private bool          _dragRoot;          // dragging the root joint = moving the whole player (_playerOffset)
    private Vector2       _vaultBlockOffset;  // skeleton-local offset added to the vault reference block's base placement
    private bool          _dragVaultBlock;    // dragging the vault reference block
    private enum EditMode { Rotate, Resize }
    private EditMode      _editMode = EditMode.Rotate;   // Tab cycles
    private bool          _playing;           // timeline playback
    private float         _playTime;          // seconds into playback
    private MouseState    _prevMs;
    private KeyboardState _prevKb;

    // Animation additions (labeled points/vectors) editing.
    private int  _selectedAdd = -1;           // index into active keyframe's Additions
    private int  _dragAdd      = -1;          // addition being dragged
    private bool _dragAddTip;                 // dragging a vector's tip vs its origin
    // Text-input naming for a pending addition or a new bone (label-on-create).
    private enum NameTarget { None, Addition, Bone }
    private NameTarget   _naming = NameTarget.None;
    private string       _nameBuffer = "";
    private AnimAddition _pendingAddition;
    private int          _pendingBoneParent;
    private Vector2      _pendingBoneLocal;
    private bool         _pendingBoneBase;     // Shift+B: add to the base rig vs the active clip
    private bool         _showHelp;           // H toggles the grouped controls panel

    private const int   SidebarW = 250;
    private const int   PadTop   = 12;
    private const int   RowH     = 40;
    private const float RigScale = 5f;
    private const float PickR    = 12f;
    private const float SnapEps  = 0.012f;

    private AnimationDocument Doc => _selected >= 0 && _selected < _docs.Count ? _docs[_selected] : null;
    private int W => GraphicsDevice.Viewport.Width;
    private int H => GraphicsDevice.Viewport.Height;

    // Name of the clip to open on launch (case-insensitive, matches AnimationDocument.Name),
    // or null to open the first. Lets you jump straight to a clip when the sidebar has more
    // entries than fit on screen.
    private readonly string _openClip;

    public DemoGame(string openClip = null)
    {
        _openClip = openClip;
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

        // Printable characters for the naming mode. Control chars (Enter/Back/Esc) are
        // handled by keyboard polling; this only fires once naming is active, so the key
        // that *starts* naming (P/V/B) isn't captured (naming isn't on yet when it fires).
        Window.TextInput += (s, e) =>
        {
            if (_naming != NameTarget.None && !char.IsControl(e.Character))
                _nameBuffer += e.Character;
        };

        _shotPath = Environment.GetEnvironmentVariable("MTILE_SHOT");
        if (Environment.GetEnvironmentVariable("MTILE_SHOT_HELP") != null) _showHelp = true;

        _dir = FindStatesDir();
        // Authored-only content: the rig comes from Skeletons/biped.json and throws
        // if missing (no procedural fallback), and the clip list is exactly what's
        // on disk in SkeletonStates/ (no seed autogeneration). N / C create clips.
        _baseSkeleton = SkeletonExamples.Biped();
        _skeleton = _baseSkeleton;
        _pose = _skeleton.CreatePose();
        _kfA  = _skeleton.CreatePose();
        _kfB  = _skeleton.CreatePose();
        _kfC  = _skeleton.CreatePose();
        _kfD  = _skeleton.CreatePose();

        // Floor line = the bind-pose sole (lowest joint/tip), so authored feet have a
        // ground reference to plant against. Matches CharacterAnimator's sole logic.
        // Recomputed whenever the rig bind changes (see RecomputeFloorLine).
        RecomputeFloorLine();

        UpdateRoot();

        _typeOptions = BuildTypeOptions();
        _docs = AnimationStore.LoadAll(_dir);
        if (_docs.Count > 0)
        {
            int open = 0;
            if (!string.IsNullOrEmpty(_openClip))
            {
                int found = _docs.FindIndex(d =>
                    string.Equals(d.Name, _openClip, StringComparison.OrdinalIgnoreCase));
                if (found >= 0) open = found;
                else Console.WriteLine($"clip '{_openClip}' not found; opening '{_docs[0].Name}'. " +
                                       $"Available: {string.Join(", ", _docs.ConvertAll(d => d.Name))}");
            }
            SelectAnimation(open);
        }

        Console.WriteLine($"Animation editor - states in: {_dir}");
        Console.WriteLine("Controls cheatsheet: MTile.Demo/CONTROLS.md");
        Console.WriteLine("Tab cycle edit mode | M+click mark | F flip | K sample | Space play | Del | [ ] dur | L loop | Ctrl-S | N new | C clone");
        Console.WriteLine("Skeleton Bones");
        for (int i = 0; i < _skeleton.Count; i++)
        {
            var b = _skeleton.Bones[i];
            Console.WriteLine($"  {i}: {b.Name} (parent={b.Parent}, rot={b.Rotation:F2}, len={b.Length:F2})");
        }
    }

    protected override void Update(GameTime gameTime)
    {
        var ms = Mouse.GetState();
        var kb = Keyboard.GetState();
        var mp = new Vector2(ms.X, ms.Y);

        // Naming mode swallows all other input: Enter commits, Esc cancels, Back edits.
        if (_naming != NameTarget.None)
        {
            if (Pressed(kb, Keys.Back) && _nameBuffer.Length > 0) _nameBuffer = _nameBuffer[..^1];
            else if (Pressed(kb, Keys.Enter))  CommitName();
            else if (Pressed(kb, Keys.Escape)) CancelName();
            _prevMs = ms; _prevKb = kb;
            base.Update(gameTime);
            return;
        }

        // Edge-triggered so an Escape still held from cancelling a label (which
        // returned above last frame) doesn't immediately fall through and Exit.
        if (Pressed(kb, Keys.Escape)) Exit();
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
        if (Pressed(kb, Keys.Tab)) _editMode = (EditMode)(((int)_editMode + 1) % 2);
        if (Pressed(kb, Keys.F)) FlipAnimation();
        if (Pressed(kb, Keys.Space)) TogglePlay();
        if (Pressed(kb, Keys.Delete)) { if (_selectedAdd >= 0) RemoveSelectedAddition(); else DeleteActiveKeyframe(); }
        // Add labeled constructs: P point, V vector (to the active keyframe), B child bone.
        // B adds the bone to the active clip (clip-local); Shift+B adds it to the base rig.
        if (Pressed(kb, Keys.P)) BeginAddAddition(AnimAdditionKind.Point, mp);
        if (Pressed(kb, Keys.V)) BeginAddAddition(AnimAdditionKind.Vector, mp);
        if (Pressed(kb, Keys.B)) BeginAddBone(mp, toBase: kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift));
        if (Pressed(kb, Keys.H)) _showHelp = !_showHelp;

        // Move the whole player (skeleton + com): arrows nudge (Shift = faster), Home recenters.
        float nudge = (kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift)) ? 6f : 1.5f;
        if (kb.IsKeyDown(Keys.Left))  _playerOffset.X -= nudge;
        if (kb.IsKeyDown(Keys.Right)) _playerOffset.X += nudge;
        if (kb.IsKeyDown(Keys.Up))    _playerOffset.Y -= nudge;
        if (kb.IsKeyDown(Keys.Down))  _playerOffset.Y += nudge;
        if (Pressed(kb, Keys.Home))   _playerOffset = Vector2.Zero;
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
                // Addition handles take priority over joints (they sit on top of the rig).
                if (_activeKey >= 0 && TryPickAddition(world, mp, out int ai, out bool tip))
                {
                    _selectedAdd = ai; _dragAdd = ai; _dragAddTip = tip;
                }
                // Grabbing the root joint moves the whole player (skeleton + com), independent of
                // the active keyframe — root drag is otherwise a no-op (EditBone skips the root).
                else if (PickJoint(world, mp) is int rb && rb >= 0 && _skeleton.Bones[rb].IsRoot)
                {
                    _dragRoot = true; _selectedAdd = -1;
                }
                else
                {
                    int bone = _activeKey >= 0 ? PickJoint(world, mp) : -1;
                    if (mDown && bone >= 0) ToggleContact(bone);   // M + click marks/unmarks the node
                    else if (bone >= 0) _dragBone = bone;
                    // No joint under the cursor: grab the vault reference block if the click is on it.
                    else if (TryGetVaultBlockRect(out var vb) && vb.Contains((int)mp.X, (int)mp.Y)) _dragVaultBlock = true;
                    _selectedAdd = -1;
                }
            }
        }

        if (leftDown && _dragRoot)
        {
            // Move the player by the mouse delta (screen space); folded into _root next UpdateRoot.
            _playerOffset += mp - new Vector2(_prevMs.X, _prevMs.Y);
        }
        else if (leftDown && _dragVaultBlock)
        {
            // Block offset is skeleton-local; _root is uniform-scaled (no rotation), so undo the scale.
            _vaultBlockOffset += (mp - new Vector2(_prevMs.X, _prevMs.Y)) / RigScale;
        }
        else if (leftDown && _dragBar >= 0)
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
        else if (leftDown && _dragAdd >= 0 && _activeKey >= 0)
        {
            DragAddition(world, mp);
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
                // A base-bone bind edit dirties the rig file; a clip-local bone's edit
                // dirties the active clip (its ExtraBones live in the animation file).
                if (IsBaseBone(_skeleton.Bones[_dragBone].Name)) _skelDirty = true;
                else _dirty = true;
                RecomputeFloorLine();
            }
        }

        if (leftUp) { _dragBone = -1; _dragBar = -1; _dragPlayhead = false; _dragAdd = -1; _dragRoot = false; _dragVaultBlock = false; }

        _prevMs = ms;
        _prevKb = kb;
        base.Update(gameTime);
    }

    // Which side(s) of the data model an edit mutates this frame. The drag handler
    // routes the dirty flag accordingly: Pose touches force a keyframe re-capture +
    // anim-dirty; Rig touches set the skeleton-dirty flag for Ctrl-S.
    [System.Flags]
    private enum EditTouched { None = 0, Pose = 1, Rig = 2 }

    // Edit-mode write split (both drag the clicked joint toward the cursor):
    //   • Rotate — pose only. Pivots the bone (and its subtree) about its joint; the
    //              keyframe absorbs the rotation. The bone's rest length is unchanged.
    //   • Resize — split. The angular part rolls the pose rotation (as in Rotate); the
    //              radial part scales the bone's rest Length on the rig so its tip
    //              reaches the cursor.
    private EditTouched EditBone(Affine2[] world, int bone, Vector2 mp)
    {
        int parent = _skeleton.Bones[bone].Parent;
        if (parent < 0) return EditTouched.None;   // the root joint is whole-rig placement, not editable here

        Vector2 pivot     = world[parent].Translation;          // parent's tip = this bone's joint
        Vector2 boneVec   = world[bone].Translation - pivot;    // current bone direction (drawn in screen space)
        Vector2 cursorVec = mp - pivot;

        if (boneVec.LengthSquared() < 1e-4f || cursorVec.LengthSquared() < 1e-4f)
            return EditTouched.None;

        // The angular delta from where the bone currently points to the cursor. Common to
        // both modes; self-stabilizing — once the bone tracks the cursor, next frame's δ ≈ 0.
        // Since the pure-chain rig has no bind orientation, Local[bone].Rotation IS the
        // parent-relative angle, so adding this delta points the bone straight at the cursor.
        _pose.Local[bone].Rotation += SignedAngle(boneVec, cursorVec);

        if (_editMode == EditMode.Resize)
        {
            // Radial part → rig: scale the bone's rest Length so its tip lands on the cursor.
            // boneVec already carries the root scale, so the screen-space ratio is the
            // length ratio. Mirror it into the pose (whose local +X offset is the length).
            float scale = cursorVec.Length() / boneVec.Length();
            SetBoneLength(bone, _skeleton.Bones[bone].Length * scale);
            return EditTouched.Pose | EditTouched.Rig;
        }
        return EditTouched.Pose;
    }

    // Replace a bone's rest Length on the live rig, and mirror it into the working pose
    // (whose local +X offset is the length) so the on-screen figure reflects the change
    // immediately. Every other keyframe picks the new length up on its next SetToDefault()
    // (i.e. the next time it's selected or sampled). Persisted on Ctrl-S via SaveAll().
    private void SetBoneLength(int i, float length)
    {
        var old = _skeleton.Bones[i];
        _skeleton.Bones[i] = new Bone(old.Name, old.Parent, old.Rotation, length);
        _pose.Local[i].Translation = Vector2.UnitX * length;

        // Mirror into the backing store so the edit survives a recompose / save. A base
        // bone writes back to the base rig; a clip-local bone updates its ExtraBones
        // record on the active clip (the working rig is rebuilt from those two sources).
        int bi = _baseSkeleton.IndexOf(old.Name);
        if (bi >= 0)
        {
            var b = _baseSkeleton.Bones[bi];
            _baseSkeleton.Bones[bi] = new Bone(b.Name, b.Parent, b.Rotation, length);
        }
        else
        {
            var rec = Doc?.ExtraBones?.Find(r => r.Name == old.Name);
            if (rec != null) rec.Length = length;
        }
    }

    // True when the named bone belongs to the base rig (vs the active clip's ExtraBones).
    private bool IsBaseBone(string name) => _baseSkeleton.IndexOf(name) >= 0;

    // Floor line tracks the bind-pose sole; recompute after any rig edit so the
    // ground reference stays consistent with the live silhouette.
    private void RecomputeFloorLine()
    {
        var bindWorld = _skeleton.CreatePose().ComputeWorld(Affine2.Identity);
        _floorLocalY = 0f;
        // Each bone's far end (and every joint) is exactly world[i].Translation under the R·T·S
        // chain, so the sole is the lowest of those — no separate +Length tip term (that would
        // overshoot a whole bone past the real silhouette).
        for (int i = 0; i < _skeleton.Count; i++)
            _floorLocalY = MathF.Max(_floorLocalY, bindWorld[i].Translation.Y);
    }

    // Rig placement in the editor: centered in the working area, scaled. Recomputed
    // each frame so it tracks window size.
    private void UpdateRoot()
    {
        float cx = SidebarW + (W - SidebarW) / 2f;
        float cy = H * 0.48f;
        _root = Affine2.FromTRS(new Vector2(cx, cy) + _playerOffset, 0f, new Vector2(RigScale, RigScale));
    }

    // === draw ================================================================

    protected override void Draw(GameTime gameTime)
    {
        // Dev screenshot: MTILE_SHOT=path captures one frame (optionally with the help
        // panel open via MTILE_SHOT_HELP) and exits. Render through a target so the
        // capture is immune to window focus.
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
        DrawSidebar();
        DrawEditor();
        DrawTimeline();
        DrawHeader();
        DrawHelpOverlay();
        DrawNamingOverlay();
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

    private string _shotPath;
    private int _shotFrame;

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
            else
            {
                // Clip-local bones (this clip's ExtraBones) ring in cyan so it's obvious
                // which joints belong to the active clip vs the shared base rig.
                Color ring = !IsBaseBone(_skeleton.Bones[i].Name) ? new Color(90, 220, 230)
                           : _skeleton.Bones[i].IsRoot            ? Color.Yellow
                                                                  : Color.OrangeRed;
                _draw.Ring(p, 5f, ring, 12, 1.5f);
            }
        }

        DrawAdditions(world);
    }

    // Draw the keyframe's labeled additions: points as a ringed dot, vectors as a labeled
    // arrow. Editable (bright) on the active keyframe; dimmed + interpolated otherwise.
    private void DrawAdditions(Affine2[] world)
    {
        var doc = Doc; if (doc == null) return;
        bool editable = _activeKey >= 0;
        var adds = editable ? doc.Keyframes[_activeKey].Additions
                            : AnimAdditionSampler.Sample(doc, _scrubT);
        if (adds == null) return;

        for (int i = 0; i < adds.Count; i++)
        {
            var a = adds[i];
            Vector2 o = AdditionOriginWorld(a, world);
            Color col = !editable             ? new Color(90, 140, 130)
                      : i == _selectedAdd     ? Color.White
                                              : new Color(120, 230, 200);
            if (a.Kind == AnimAdditionKind.Vector)
            {
                Vector2 t = AdditionTipWorld(a, world);
                _draw.Line(o, t, col, 2f);
                Vector2 dir = t - o;
                if (dir.LengthSquared() > 1e-3f)
                {
                    dir.Normalize();
                    var n = new Vector2(-dir.Y, dir.X);
                    _draw.Line(t, t - dir * 8f + n * 5f, col, 2f);
                    _draw.Line(t, t - dir * 8f - n * 5f, col, 2f);
                }
                _draw.Disc(o, 3f, col);
            }
            else
            {
                _draw.Ring(o, 5f, col, 14, 1.5f);
                _draw.Disc(o, 2f, col);
            }
            if (!string.IsNullOrEmpty(a.Name))
                _spriteBatch.DrawString(_font, a.Name, o + new Vector2(8f, -6f), col);
        }
    }

    // Reference obstacle drawn under the rig only while authoring a Vault clip. Sized
    // in skeleton-local units (the rig's natural frame) so it scales with RigScale.
    // ~one game-tile wide, knee-ish tall — tune to taste; nothing reads this geometry.
    // Skeleton-local geometry of the vault reference block: ~one game-tile wide, knee-ish tall,
    // a hair ahead of the rig in +X. _vaultBlockOffset (drag / arrows) shifts it from there.
    private const float VaultBlockW = 18f, VaultBlockH = 14f, VaultBlockX = 8f;

    // Screen-space rect of the vault block, false when no block is shown (non-Vault clip).
    // Shared by the renderer and the drag hit-test so they never disagree.
    private bool TryGetVaultBlockRect(out Rectangle rect)
    {
        rect = default;
        if (Doc?.Type != "Vault") return false;
        Vector2 tl = _root.TransformPoint(new Vector2(VaultBlockX,               _floorLocalY - VaultBlockH) + _vaultBlockOffset);
        Vector2 br = _root.TransformPoint(new Vector2(VaultBlockX + VaultBlockW, _floorLocalY)               + _vaultBlockOffset);
        rect = new Rectangle((int)tl.X, (int)tl.Y, (int)(br.X - tl.X), (int)(br.Y - tl.Y));
        return true;
    }

    private void DrawVaultBlock()
    {
        if (!TryGetVaultBlockRect(out var rect)) return;

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

    // === labeled additions (points / vectors) + new bones ====================

    // Start adding a point/vector at the cursor (root-local), then prompt for a name.
    private void BeginAddAddition(AnimAdditionKind kind, Vector2 mp)
    {
        if (Doc == null || _activeKey < 0) return;   // only on an editable keyframe
        Vector2 local = _root.Inverse().TransformPoint(mp);
        _pendingAddition = new AnimAddition
        {
            Kind = kind, Parent = null, Px = local.X, Py = local.Y,
            Dx = kind == AnimAdditionKind.Vector ? 12f : 0f, Dy = 0f,   // default vector points +X
        };
        _naming = NameTarget.Addition;
        _nameBuffer = "";
    }

    // Start adding a child bone of the hovered joint (or root) at the cursor, then name it.
    // toBase = Shift+B: a base-rig bone; otherwise a clip-local bone on the active clip.
    private void BeginAddBone(Vector2 mp, bool toBase)
    {
        int parent = _hoverBone >= 0 ? _hoverBone : 0;
        var world  = _pose.ComputeWorld(_root);
        _pendingBoneParent = parent;
        _pendingBoneLocal  = world[parent].Inverse().TransformPoint(mp);
        _pendingBoneBase   = toBase;
        _naming = NameTarget.Bone;
        _nameBuffer = "";
    }

    private void CommitName()
    {
        string name = string.IsNullOrWhiteSpace(_nameBuffer) ? DefaultName() : _nameBuffer.Trim();
        if (_naming == NameTarget.Addition && _pendingAddition != null && Doc != null && _activeKey >= 0)
        {
            _pendingAddition.Name = name;
            var kf = Doc.Keyframes[_activeKey];
            kf.Additions ??= new List<AnimAddition>();
            kf.Additions.Add(_pendingAddition);
            _selectedAdd = kf.Additions.Count - 1;
            _dirty = true;
        }
        else if (_naming == NameTarget.Bone)
        {
            AddBone(name, _pendingBoneParent, _pendingBoneLocal, _pendingBoneBase);
        }
        EndNaming();
    }

    private void CancelName() => EndNaming();
    private void EndNaming() { _naming = NameTarget.None; _nameBuffer = ""; _pendingAddition = null; }

    private string DefaultName()
    {
        if (_naming == NameTarget.Bone) return UniqueBoneName("bone");
        string stem = _pendingAddition?.Kind == AnimAdditionKind.Vector ? "vector" : "point";
        int n = Doc?.Keyframes[_activeKey].Additions?.Count ?? 0;
        return $"{stem}{n}";
    }

    // Add a bone. Default (B) is clip-local: it lives in the ACTIVE clip's ExtraBones and
    // is saved into that animation file, so it shows only while editing this clip and never
    // touches the shared rig. Shift+B adds to the base rig instead (toBase). `parent` is an
    // index into the current working rig; we store the parent by NAME so it resolves after
    // recomposition. With no active clip, a clip-local add falls back to the base rig.
    private void AddBone(string name, int parent, Vector2 local, bool toBase)
    {
        if (_skeleton.IndexOf(name) >= 0) name = UniqueBoneName(name);
        string parentName = _skeleton.Bones[parent].Name;

        // Pure-chain placement: the new bone attaches at the parent's tip and is described
        // by an angle + length. Convert the click (in the parent's local frame) into the
        // rotation/length whose tip lands on the cursor — d is the click relative to the tip.
        Vector2 d      = local - new Vector2(_skeleton.Bones[parent].Length, 0f);
        float rotation = MathF.Atan2(d.Y, d.X);
        float length   = d.Length();

        if (toBase || Doc == null)
        {
            int p = _baseSkeleton.IndexOf(parentName);
            if (p < 0) return;   // can't parent a base bone to a clip-local one
            _baseSkeleton = _baseSkeleton.WithBone(name, p, rotation, length);
            _skelDirty = true;
        }
        else
        {
            Doc.ExtraBones ??= new List<SkeletonBoneRecord>();
            Doc.ExtraBones.Add(new SkeletonBoneRecord
            {
                Name = name, Parent = parentName, Rotation = rotation, Length = length,
            });
            _dirty = true;
        }
        RebuildWorkingRig();
    }

    // Recompose the working rig from the base plus the active clip's ExtraBones, and
    // recreate the pose buffers (sized to the bone count). Called on clip switch and after
    // any bone add.
    private void RebuildWorkingRig()
    {
        _skeleton = SkeletonComposition.Compose(_baseSkeleton, Doc?.ExtraBones);
        RecreatePoses();
    }

    // The pose scratch buffers are sized to the bone count, so a rig that grew needs fresh
    // poses; reapply the active keyframe (by name) so the figure is unchanged but for the
    // new bone (which sits at bind everywhere until posed).
    private void RecreatePoses()
    {
        _pose = _skeleton.CreatePose();
        _kfA  = _skeleton.CreatePose();
        _kfB  = _skeleton.CreatePose();
        _kfC  = _skeleton.CreatePose();
        _kfD  = _skeleton.CreatePose();
        if (Doc != null && _activeKey >= 0) PoseData.Apply(Doc.Keyframes[_activeKey].Bones, _pose);
        else SamplePose(_scrubT);
        RecomputeFloorLine();
    }

    private string UniqueBoneName(string baseName)
    {
        if (_skeleton.IndexOf(baseName) < 0) return baseName;
        for (int n = 2; ; n++)
            if (_skeleton.IndexOf($"{baseName}{n}") < 0) return $"{baseName}{n}";
    }

    private void RemoveSelectedAddition()
    {
        if (Doc == null || _activeKey < 0) return;
        var adds = Doc.Keyframes[_activeKey].Additions;
        if (adds == null || _selectedAdd < 0 || _selectedAdd >= adds.Count) return;
        adds.RemoveAt(_selectedAdd);
        if (adds.Count == 0) Doc.Keyframes[_activeKey].Additions = null;
        _selectedAdd = -1;
        _dirty = true;
    }

    // Pick the nearest addition handle (a vector's tip, or a point/vector origin) within
    // PickR; reports whether the tip was hit (so a drag edits direction vs position).
    private bool TryPickAddition(Affine2[] world, Vector2 mp, out int idx, out bool tip)
    {
        idx = -1; tip = false;
        var adds = _activeKey >= 0 ? Doc?.Keyframes[_activeKey].Additions : null;
        if (adds == null) return false;
        float best = PickR * PickR;
        for (int i = 0; i < adds.Count; i++)
        {
            var a = adds[i];
            if (a.Kind == AnimAdditionKind.Vector)
            {
                float dt = Vector2.DistanceSquared(AdditionTipWorld(a, world), mp);
                if (dt < best) { best = dt; idx = i; tip = true; }
            }
            float doo = Vector2.DistanceSquared(AdditionOriginWorld(a, world), mp);
            if (doo < best) { best = doo; idx = i; tip = false; }
        }
        return idx >= 0;
    }

    private void DragAddition(Affine2[] world, Vector2 mp)
    {
        var a = Doc.Keyframes[_activeKey].Additions[_dragAdd];
        Vector2 local = AdditionParentTransform(a, world).Inverse().TransformPoint(mp);
        if (_dragAddTip && a.Kind == AnimAdditionKind.Vector) { a.Dx = local.X - a.Px; a.Dy = local.Y - a.Py; }
        else                                                  { a.Px = local.X;        a.Py = local.Y; }
        _dirty = true;
    }

    // An addition lives in its Parent bone's frame, or the character root when Parent is null.
    private Affine2 AdditionParentTransform(AnimAddition a, Affine2[] world)
    {
        if (a.Parent != null) { int pi = _skeleton.IndexOf(a.Parent); if (pi >= 0) return world[pi]; }
        return _root;
    }
    private Vector2 AdditionOriginWorld(AnimAddition a, Affine2[] world)
        => AdditionParentTransform(a, world).TransformPoint(new Vector2(a.Px, a.Py));
    private Vector2 AdditionTipWorld(AnimAddition a, Affine2[] world)
        => AdditionParentTransform(a, world).TransformPoint(new Vector2(a.Px + a.Dx, a.Py + a.Dy));

    private static List<AnimAddition> CloneAdditions(List<AnimAddition> src)
    {
        if (src == null || src.Count == 0) return null;
        var copy = new List<AnimAddition>(src.Count);
        foreach (var a in src) copy.Add(a.Clone());
        return copy;
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

    // Compact status header: what clip / where in time / current values. The full
    // key cheatsheet lives in the H help panel so the top bar stays uncluttered.
    private void DrawHeader()
    {
        var doc = Doc;
        string title = doc != null ? $"[{doc.Type}] {doc.Name}" : "(none)";
        // Two dirty flags: animation keyframes (orange) and the rig itself (cyan).
        // Both clear on Ctrl-S; the rig flag means Skeletons/<rig>.json will be rewritten.
        bool anyDirty = _dirty || _skelDirty;
        string tag = (_dirty && _skelDirty) ? "  *unsaved (anim+rig)*"
                   : _dirty                 ? "  *unsaved*"
                   : _skelDirty             ? "  *unsaved rig*"
                                            : "";
        _spriteBatch.DrawString(_font, $"{title}{tag}",
            new Vector2(SidebarW + 16, 10), anyDirty ? Color.Orange : Color.White);

        string state = _playing      ? $"PLAYING @ t={_scrubT:0.00}"
                     : _activeKey >= 0 ? $"keyframe {_activeKey} @ t={_scrubT:0.00}  (editable)"
                                       : $"interpolated @ t={_scrubT:0.00}  (K to sample)";
        Color stateColor = _playing ? new Color(255, 200, 80)
                         : _activeKey >= 0 ? new Color(150, 230, 150) : new Color(150, 160, 175);
        _spriteBatch.DrawString(_font,
            $"{state}    |    {_editMode.ToString().ToUpperInvariant()} (Tab)",
            new Vector2(SidebarW + 16, 28), stateColor);

        if (doc != null)
            _spriteBatch.DrawString(_font,
                $"dur {doc.Duration:0.0}s  |  loop {(doc.Loop ? "on" : "off")}  |  region {doc.Region}",
                new Vector2(SidebarW + 16, 46), new Color(160, 170, 185));

        _spriteBatch.DrawString(_font, "H controls   |   Ctrl-S save   |   K sample keyframe",
            new Vector2(SidebarW + 16, 64), new Color(130, 140, 155));
    }

    // Grouped key cheatsheet, toggled by H. Sections so related controls cluster instead
    // of one dense line — scales as the editor grows (additions, bones, …).
    private static readonly (string Group, string Keys)[] HelpRows =
    {
        ("Clip",     "[ ] duration    L loop    R region    T type    N new    C clone"),
        ("Edit",     "Tab mode (rotate/resize)    drag joint    M+click contact    F flip"),
        ("Move",     "drag root joint = move player    arrows nudge (Shift faster)    Home recenter"),
        ("Vault",    "drag the brown block to reposition the obstacle (Vault clips only)"),
        ("Add",      "P point    V vector    B clip bone  (Shift+B base rig)    (then name, Enter)"),
        ("Keyframe", "K sample    Del delete    click / drag a timeline bar    Space play"),
        ("File",     "Ctrl-S save  (writes clips + rig)"),
    };

    private void DrawHelpOverlay()
    {
        if (!_showHelp) return;
        int x = SidebarW + 30, y = 130;
        var panel = new Rectangle(x - 16, y - 34, W - SidebarW - 60, HelpRows.Length * 30 + 60);
        Fill(panel, new Color(16, 18, 26, 240));
        _draw.Line(new Vector2(panel.Left, panel.Top),    new Vector2(panel.Right, panel.Top),    new Color(90, 130, 120), 1f);
        _draw.Line(new Vector2(panel.Left, panel.Bottom), new Vector2(panel.Right, panel.Bottom), new Color(90, 130, 120), 1f);

        _spriteBatch.DrawString(_font, "CONTROLS", new Vector2(x, y - 26), new Color(150, 200, 255));
        _spriteBatch.DrawString(_font, "H to close", new Vector2(panel.Right - 100, y - 26), new Color(120, 130, 145));
        for (int i = 0; i < HelpRows.Length; i++)
        {
            int ry = y + 10 + i * 30;
            _spriteBatch.DrawString(_font, HelpRows[i].Group, new Vector2(x, ry), new Color(255, 200, 120));
            _spriteBatch.DrawString(_font, HelpRows[i].Keys,  new Vector2(x + 96, ry), new Color(205, 210, 220));
        }
    }

    // Modal name prompt while adding a point/vector/bone.
    private void DrawNamingOverlay()
    {
        if (_naming == NameTarget.None) return;
        string what = _naming == NameTarget.Bone ? "bone"
                    : _pendingAddition?.Kind == AnimAdditionKind.Vector ? "vector" : "point";
        var box = new Rectangle(SidebarW + 40, H / 2 - 26, 420, 52);
        Fill(box, new Color(18, 20, 28));
        _draw.Line(new Vector2(box.Left,  box.Top),    new Vector2(box.Right, box.Top),    new Color(120, 230, 200), 1f);
        _draw.Line(new Vector2(box.Left,  box.Bottom), new Vector2(box.Right, box.Bottom), new Color(120, 230, 200), 1f);
        _spriteBatch.DrawString(_font, $"name {what}:  {_nameBuffer}_", new Vector2(box.Left + 12, box.Top + 9), Color.White);
        _spriteBatch.DrawString(_font, "Enter = accept    Esc = cancel", new Vector2(box.Left + 12, box.Top + 28), new Color(150, 160, 175));
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
            Vector2 endpoint = world[i].Translation;
            // world[i].TransformPoint(Vector2.UnitX * _skeleton.Bones[i].Length);
            float d = Vector2.DistanceSquared(endpoint, mp);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // === animation / keyframe state ==========================================

    private void SelectAnimation(int i)
    {
        _playing = false;
        _selected = i;
        _activeKey = -1;            // stale index from the previous clip; SelectKeyframe resets it
        var doc = _docs[i];
        RebuildWorkingRig();        // compose base + THIS clip's ExtraBones
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
        _selectedAdd = -1;
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
        if (Doc == null) { _pose.SetToDefault(); return; }
        // C1 Catmull-Rom — the SAME spline the runtime plays (AnimationSampler.SampleSmooth),
        // closing the WYSIWYG gap: scrubbed in-betweens now match what ships. Keyframes are
        // exact on the spline, so keyframe editing is unaffected.
        AnimationSampler.SampleSmooth(Doc, t, _kfA, _kfB, _kfC, _kfD, _pose);
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
            Additions = AnimAdditionSampler.CloneEffectiveAt(doc, _scrubT),
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
                Time      = kf.Time,
                Bones     = CloneBones(kf.Bones),
                Contacts  = CloneContacts(kf.Contacts),
                Additions = CloneAdditions(kf.Additions),
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
            // Capture the BASE rig only — clip-local bones live in their clip's ExtraBones
            // (saved above), so they must never be baked back into the shared rig file.
            string skelDir = SkeletonsDir(_dir);
            var doc = SkeletonStore.Capture(_baseSkeleton.Name, _baseSkeleton);
            SkeletonStore.Save(doc, skelDir);
            _skelDirty = false;
            Console.WriteLine($"Saved rig '{_baseSkeleton.Name}' to {skelDir}");
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
