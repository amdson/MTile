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
//
// This file is the CANVAS half: state, picking, drags, and the rig/scene rendering. The
// chrome (menu bar, clip list, inspector, timeline, popups) lives in DemoGame.Ui.cs on
// Dear ImGui, which owns panel layout, clipping and z-order.
public sealed partial class DemoGame : Game
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
    private Affine2      _root;           // the rig root, from _placement each frame
    // PER-KEYFRAME body path (Animation/BodyPath.cs — the com anchor's scene position,
    // formerly the editor-only "edref" track): authored by dragging the com marker — the
    // PLAYER ensemble (com marker + skeleton) offsets from the scene anchor by its
    // interpolated value, while the ground references (floor line, obstacle block) stay
    // put. Scrubbing shows the body arcing over the fixed scenery (e.g. parkour clearing
    // its block). Saves with the clip and rides the additions machinery (K inherits,
    // retime follows); gameplay placement doesn't consume it yet (see BodyPath's
    // runtime-status note).
    private const string BodyPathName = BodyPath.ChannelName;
    // The scene components (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Refactor boundaries"):
    // placement (view + the shared ClipMotion query), guide editing, preview drawing, and the
    // header's Scene dropdown. DemoGame stays the MonoGame host wiring them together.
    private readonly ScenePlacement _placement = new();
    private readonly SceneGuideView _guides    = new();
    private readonly ScenePreview   _preview   = new();
    private SkeletonPose _ghostPose;            // scratch for keyframe ghosts
    // ENDPOINT MENU (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Endpoint menus"): click an
    // endpoint (a bone's far tip) to select it and show a small "v" affordance beside it;
    // right-click opens the same menu directly. Items add elements (knife), named points,
    // and contact annotations for a scope, backed by EndpointResolver / NamedPoint. The menu
    // itself is an ImGui popup now (DemoGame.Ui.cs EndpointPopup) — selection stays here.
    private int     _selectedEndpoint = -1;     // bone whose End is selected
    private bool    _contactWholeClip;          // edit scope: this key → next key (default) or the whole clip
    private Vector2 _pressPos;                  // where the left button went down (click vs drag)
    private string  _selectedPointId;           // a clip point selected from the menu (Delete removes it)
    // IK DRAG MODE (workplan chunk 3.5): a header toggle; dragging a joint then runs an
    // interactive solve (PoseIk.DragSession) pulling the clicked node toward the mouse,
    // biased toward the drag-start pose and last frame's solution, instead of the direct
    // rotate-one-bone edit. Escape mid-drag restores the drag-start pose.
    private bool _ikMode;                       // toggled from Edit ▸ IK drag / the inspector
    private PoseIk.DragSession _ikDrag;

    // COM-ANCHORED placement: the rig is drawn exactly the way the game places it —
    // root = anchor − com·scale — so the com marker sits at a FIXED screen point
    // (ScenePlacement.Anchor) and the nominal ground line sits 2·Radius/SkeletonScale rig-units
    // below it. Dragging the ROOT joint edits the active keyframe's com INVERSELY: the skeleton
    // follows the cursor while com marker and ground stay put — "place the body against the
    // ground", the control for authoring the com arc.

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
    // Timeline span drag (attachment windows today; contact spans next). The grabbed part
    // and where inside the span the grab landed, so a body slide keeps its grip point.
    private AnimAttachment _dragAttachSpan;
    private ContactSpan    _dragContactSpan;
    private SpanPart       _dragSpanPart;
    private float          _dragSpanOffset;
    private ContactSpan    _selectedContact;   // a contact span picked from its timeline bar
    private bool          _dragRoot;          // dragging the root joint = placing the body (or panning)
    private enum EditMode { Rotate, Resize, Stretch }
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
    private enum NameTarget { None, Addition, Bone, Effect, Point }
    private string _effectBone;
    private AnimAttachment _selectedAttachment;
    private SpriteAttachmentRenderer _attachments;
    private readonly List<AttachmentSample> _attachmentSamples = new();
    private NameTarget   _naming = NameTarget.None;
    private string       _nameBuffer = "";
    private AnimAddition _pendingAddition;
    private int          _pendingBoneParent;
    private Vector2      _pendingBoneLocal;
    private bool         _pendingBoneBase;     // Shift+B: add to the base rig vs the active clip
    private bool         _showHelp;           // H toggles the grouped controls panel

    private bool _panDrag;   // middle-button view pan in progress
    private ArcEditSession _arcEdit;   // the open in-clip arc edit, or null
    private const float PickR    = 12f;
    private const float SnapEps  = 0.012f;

    private AnimationDocument Doc => _selected >= 0 && _selected < _docs.Count ? _docs[_selected] : null;
    private int W => GraphicsDevice.Viewport.Width;
    private int H => GraphicsDevice.Viewport.Height;

    // Name of the clip to open on launch (case-insensitive, matches AnimationDocument.Name),
    // or null to open the first. Lets you jump straight to a clip when the sidebar has more
    // entries than fit on screen.
    private readonly string _openClip;

    // --usebind <binding>: superimpose a sprite skin (SpriteBindings/<name>.json) on the
    // rig through every scrubbed/played pose. G toggles the sprite, W the mesh wireframe.
    // The skin is baked against a pristine copy of the base rig (_skinRig) and fed a pose
    // synced by bone NAME each frame, so clip-local ExtraBones and live rig edits in the
    // editor can't desync it.
    private readonly string _bindingArg;
    private SpriteSkin   _skin;
    private Skeleton     _skinRig;
    private SkeletonPose _skinPose;
    private bool _showSkin = true, _skinWire, _showRig = true;

    // --rig <name>: edit against Skeletons/<name>.json instead of the default
    // biped_rabbit. Clips apply by bone name (bones a clip doesn't mention stay at rest),
    // and Ctrl-S rig saves write back to the rig's OWN file (keyed on its Name field).
    private readonly string _rigArg;

    public DemoGame(string openClip = null, string binding = null, string rig = null)
    {
        _openClip = openClip;
        _bindingArg = binding;
        _rigArg = rig;
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

        // The UI layer owns text entry now (the name modal's field), so it takes the
        // window's TextInput; the editor no longer polls characters itself.
        InitUi();

        _shotPath = Environment.GetEnvironmentVariable("MTILE_SHOT");
        if (Environment.GetEnvironmentVariable("MTILE_SHOT_HELP") != null) _showHelp = true;
        if (Environment.GetEnvironmentVariable("MTILE_SHOT_WIRE") != null) _skinWire = true;
        if (Environment.GetEnvironmentVariable("MTILE_SHOT_NOSKEL") != null) _showRig = false;
        if (Environment.GetEnvironmentVariable("MTILE_SHOT_BODY") != null) _preview.ShowBody = true;

        // Authored-only content: the rig comes from Skeletons/<name>.json and throws
        // if missing (no procedural fallback), and the clip list is exactly what's
        // on disk in SkeletonStates/<rigName>/ — one dir per base rig (no seed
        // autogeneration). N / C create clips there.
        // Default rig is biped — the one actually being authored. --rig biped_rabbit
        // opens the strutted rig and its own SkeletonStates/biped_rabbit/ clip dir.
        _baseSkeleton = SkeletonExamples.Load(_rigArg ?? SkeletonExamples.BipedName);
        _dir = Path.Combine(FindStatesDir(), _baseSkeleton.Name);
        // Derived overlays (SceneReferences: the hover lines) read the game's live tuning, so
        // load the same movement config Game1 does — otherwise they would draw code defaults.
        try
        {
            string cfg = Path.Combine(Path.GetDirectoryName(FindStatesDir()) ?? ".", "configs", "movement_config.json");
            if (File.Exists(cfg)) MovementConfig.Load(cfg);
        }
        catch { /* the overlays fall back to code defaults; never block startup on tuning */ }
        _attachments = new SpriteAttachmentRenderer(GraphicsDevice,
            Path.GetFullPath(Path.Combine(_dir, "..", "..", "Assets", "AnimationEffects")));
        if (_bindingArg != null)
        {
            string path = ResolveBindingPath(_bindingArg);
            // The skin bakes against the BINDING's rig (its Skeleton field; default biped) —
            // a fresh instance either way, so editor rig edits (which mutate _baseSkeleton
            // in place) can't desync it. SyncSkinPose matches bones by name, so a skin rig
            // with extra bones (e.g. a collarbone) rides along with them at bind rest.
            string skinRigName = path != null ? SpriteBindingDocument.Load(path)?.Skeleton : null;
            _skinRig = SkeletonExamples.Load(
                string.IsNullOrWhiteSpace(skinRigName) ? SkeletonExamples.BipedName : skinRigName);
            _skin = path != null ? SpriteSkin.TryLoad(GraphicsDevice, path, _skinRig) : null;
            if (_skin != null) { _skinPose = _skinRig.CreatePose(); Console.WriteLine($"sprite skin: {path} (G sprite, W wireframe)"); }
            else Console.WriteLine($"sprite skin: could not load binding '{_bindingArg}' (looked at {path ?? "SpriteBindings/"})");
        }
        _skeleton = _baseSkeleton;
        _pose = _skeleton.CreatePose();
        _kfA  = _skeleton.CreatePose();
        _kfB  = _skeleton.CreatePose();
        _kfC  = _skeleton.CreatePose();
        _kfD  = _skeleton.CreatePose();
        _ghostPose = _skeleton.CreatePose();

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
        // The chrome is built FIRST: its widget actions land on this update like any other
        // edit, and it reports whether the cursor/keyboard belong to a panel this frame.
        BuildUi(gameTime);
        var io = ImGuiNET.ImGui.GetIO();
        bool uiMouse = io.WantCaptureMouse;      // cursor is over a panel/popup
        bool uiKeys  = io.WantCaptureKeyboard;   // a text field has focus

        var ms = Mouse.GetState();
        var kb = Keyboard.GetState();
        var mp = new Vector2(ms.X, ms.Y);
        bool onCanvas = !uiMouse && _canvas.Contains((int)mp.X, (int)mp.Y);

        // Escape cancels an IK drag, then a guide placement/drag, and quits only when there
        // is nothing left to cancel. The name modal owns Escape while it is open (uiKeys).
        if (!uiKeys && Pressed(kb, Keys.Escape))
        {
            if (_arcEdit != null) CloseArcEditor(save: false);
            else if (_ikDrag != null && _dragBone >= 0)
            {
                // Cancel the IK drag: back to the drag-start pose (the drag itself ends).
                _ikDrag.Restore(_pose);
                if (Doc != null && _activeKey >= 0) Doc.Keyframes[_activeKey].Bones = PoseData.Capture(_pose);
                _ikDrag = null; _dragBone = -1;
            }
            else if (!_guides.Cancel(Doc)) Exit();
        }
        UpdateRoot();   // tracks the canvas rect + the playhead
        var (guideFrame, groundY) = _placement.GuideFrame();

        // VIEW NAVIGATION (canvas only — over a panel the wheel belongs to ImGui):
        // wheel zooms about the cursor, middle-drag pans. Both are view-only; the root-joint
        // drag still authors the com, and the arrow keys still nudge.
        int wheel = ms.ScrollWheelValue - _prevMs.ScrollWheelValue;
        if (wheel != 0 && onCanvas) _placement.ZoomBy(MathF.Pow(1.1f, wheel / 120f), mp);
        bool midDown = ms.MiddleButton == ButtonState.Pressed;
        if (midDown && _prevMs.MiddleButton == ButtonState.Released && onCanvas) _panDrag = true;
        if (!midDown) _panDrag = false;
        if (_panDrag) _placement.Pan += mp - new Vector2(_prevMs.X, _prevMs.Y);

        bool mDown       = kb.IsKeyDown(Keys.M);   // M + click toggles a node's contact mark
        bool leftDown    = ms.LeftButton == ButtonState.Pressed;
        bool leftPressed = leftDown && _prevMs.LeftButton == ButtonState.Released;
        bool leftUp      = !leftDown && _prevMs.LeftButton == ButtonState.Pressed;

        if (!uiKeys) Hotkeys(kb, mp);

        // Playback: advance the playhead per the animation's Duration/Loop.
        if (_playing && Doc != null)
        {
            _playTime += (float)gameTime.ElapsedGameTime.TotalSeconds;
            _scrubT = AnimationSampler.NormalizedTime(Doc, _playTime);
            _activeKey = -1;
            SamplePose(_scrubT);
        }

        var world = _pose.ComputeWorld(_root);
        _hoverBone = (!_playing && onCanvas && _dragBone < 0) ? PickJoint(world, mp) : _dragBone;

        // Endpoint selection: right-click any endpoint selects it and opens its menu; the "v"
        // affordance beside the selection opens the same menu. Both are ImGui popups now
        // (DemoGame.Ui.cs) — this only tracks WHICH endpoint and WHERE it drew.
        if (_selectedEndpoint >= _skeleton.Count) _selectedEndpoint = -1;
        if (_selectedEndpoint >= 0) _endpointScreen = world[_selectedEndpoint].Translation;
        bool rightPressed = ms.RightButton == ButtonState.Pressed && _prevMs.RightButton == ButtonState.Released;
        if (rightPressed && !_playing && onCanvas && Doc != null)
        {
            int b = PickJoint(world, mp);
            if (b >= 0) { _selectedEndpoint = b; _endpointScreen = world[b].Translation; _openEndpointPopup = true; }
        }

        if (leftPressed && onCanvas && !_playing)
        {
            _pressPos = mp;
            // PICK ORDER. The rig's handles — the com marker and any other addition, the root
            // joint, the bone joints — sit ON TOP of the scene, so they win over a guide's
            // BODY. That ordering used to be academic: the Select tool was only armed from the
            // menu, so a block under the figure rarely consumed anything. The Scene panel
            // leaves Select armed most of the time, and without this a block drawn under the
            // player swallowed every com/joint drag on clips like stepup.
            // A guide's EDGES and corners still win (they are what HandlePress hit-tests
            // first), so resizing a block through the figure keeps working.
            int ai = -1; bool tip = false;
            bool onAddition = _activeKey >= 0 && TryPickAddition(world, mp, out ai, out tip);
            int joint = PickJoint(world, mp);
            bool onRig = onAddition || (joint >= 0 && (_skeleton.Bones[joint].IsRoot || _activeKey >= 0));
            // An open arc edit owns the canvas: its keys and handles are the topmost thing
            // drawn, so they pick before guides, additions and joints.
            bool arcTook = _arcEdit != null && PickArcSession(mp);
            var guidePart = _guides.Peek(mp, guideFrame, groundY, Doc);
            bool guideWins = guidePart != GuidePart.None && guidePart != GuidePart.Body   // edge/corner/ground line
                          || _guides.Tool is GuideTool.AddGround or GuideTool.AddBlock;    // armed placement

            if (arcTook) { }
            else if ((guideWins || !onRig) && _guides.HandlePress(mp, guideFrame, groundY, Doc, ref _dirty))
            {
                _selectedAdd = -1;
            }
            else if (onAddition)
            {
                _selectedAdd = ai; _dragAdd = ai; _dragAddTip = tip;
            }
            // Grabbing the root joint moves the whole player (skeleton + com), independent of
            // the active keyframe — root drag is otherwise a no-op (EditBone skips the root).
            else if (joint >= 0 && _skeleton.Bones[joint].IsRoot)
            {
                _dragRoot = true; _selectedAdd = -1;
            }
            else
            {
                int bone = _activeKey >= 0 ? joint : -1;
                if (mDown && bone >= 0) ToggleContact(bone);   // M + click marks/unmarks the node
                else if (bone >= 0)
                {
                    _dragBone = bone;
                    // IK mode: a limb chain (up to the torso) — the root/torso themselves keep
                    // the direct edit (a chain of length 0 has nothing to solve).
                    _ikDrag = null;
                    if (_ikMode && !_skeleton.Bones[bone].IsRoot)
                    {
                        var session = new PoseIk.DragSession(_skeleton, _pose, bone);
                        if (session.Chain.Length > 0) _ikDrag = session;
                    }
                }
                _selectedAdd = -1;
            }
        }

        if (leftDown && _arcEdit != null && _arcEdit.Dragging)
        {
            DragArcSession(mp);
        }
        else if (leftDown && _guides.Dragging)
        {
            _guides.HandleDrag(mp, mp - new Vector2(_prevMs.X, _prevMs.Y), guideFrame, groundY, ref _dirty);
        }
        else if (leftDown && _dragRoot)
        {
            var dm = mp - new Vector2(_prevMs.X, _prevMs.Y);
            var comAdd = ActiveKeyCom();
            if (comAdd != null)
            {
                // COM-ANCHORED: dragging the root moves the BODY against the fixed ground/com —
                // implemented as the INVERSE edit of the active keyframe's com (root = anchor −
                // com·scale, so com -= Δ/scale draws the skeleton +Δ under the cursor while the
                // com marker and floor hold still). This is the com-arc authoring control:
                // scrub to a keyframe, drag the base node to where the body should be.
                comAdd.Px -= dm.X / _placement.Scale;
                comAdd.Py -= dm.Y / _placement.Scale;
                _dirty = true;
            }
            else
            {
                // The playhead is between keyframes, so there is no single com to author:
                // plain view pan.
                _placement.Pan += dm;
            }
        }
        else if (leftDown && _dragAdd >= 0 && _activeKey >= 0)
        {
            DragAddition(world, mp);
        }
        else if (leftDown && _dragBone >= 0 && _activeKey >= 0 && _ikDrag != null)
        {
            // IK drag: target in root-local rig units; solve in place; write the keyframe back
            // exactly as the direct edit does (save / dirty / Escape semantics unchanged).
            Vector2 local = _root.Inverse().TransformPoint(mp);
            _ikDrag.Step(_pose, local);
            Doc.Keyframes[_activeKey].Bones = PoseData.Capture(_pose);
            _dirty = true;
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
            }
        }

        if (leftUp)
        {
            // A click on a joint (press + release without moving) selects that endpoint.
            // (The timeline's own drags end with its widget — see TimelinePanel.)
            if (_dragBone >= 0 && Vector2.Distance(mp, _pressPos) < 3f) _selectedEndpoint = _dragBone;
            _ikDrag = null;
            _arcEdit?.EndDrag();
            _dragBone = -1; _dragAdd = -1; _dragRoot = false; _guides.Release(groundY);
        }

        _prevMs = ms;
        _prevKb = kb;
        base.Update(gameTime);
    }

    // The editor's key bindings. Only runs when ImGui does not want the keyboard, so a
    // single-letter shortcut can never fire while a name/filter field has focus.
    private void Hotkeys(KeyboardState kb, Vector2 mp)
    {
        bool ctrl  = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        bool shift = kb.IsKeyDown(Keys.LeftShift)   || kb.IsKeyDown(Keys.RightShift);

        // An open arc edit takes A (add a key on the curve under the cursor) and Delete for
        // its own keys, so the clip's keyframe bindings cannot fire into it by accident.
        if (_arcEdit != null)
        {
            if (Pressed(kb, Keys.A)
                && ArcEditOps.AddKeyNear(_arcEdit.Working, ScreenToArc(_arcEdit.Working, mp), out int addedKey))
            { _arcEdit.Selected = addedKey; _arcEdit.Dirty = true; }
            if ((Pressed(kb, Keys.Delete) || Pressed(kb, Keys.X))
                && ArcEditOps.DeleteKey(_arcEdit.Working, _arcEdit.Selected))
            { _arcEdit.Selected = -1; _arcEdit.Dirty = true; }
        }

        if (ctrl && Pressed(kb, Keys.S)) SaveAll();
        if (Pressed(kb, Keys.N)) NewAnimation();
        if (Pressed(kb, Keys.C)) CloneAnimation();
        if (Pressed(kb, Keys.K)) SampleKeyframe();
        if (Pressed(kb, Keys.Tab)) _editMode = (EditMode)(((int)_editMode + 1) % 3);
        if (Pressed(kb, Keys.F)) FlipAnimation();
        if (Pressed(kb, Keys.Space)) TogglePlay();
        // Delete acts on the selected GUIDE only in guide mode; keyframe/addition deletion is unchanged.
        if (_arcEdit == null && (Pressed(kb, Keys.Delete) || Pressed(kb, Keys.Back)))
        {
            if (_guides.Tool == GuideTool.Select && _guides.Selected(Doc) != null) _guides.DeleteSelected(Doc, ref _dirty);
            else if (_selectedAttachment != null && Doc?.Attachments?.Contains(_selectedAttachment) == true)
                RemoveAttachment(_selectedAttachment);
            else if (_selectedPointId != null) RemoveSelectedPoint();
            else if (_selectedAdd >= 0) RemoveSelectedAddition();
            else DeleteActiveKeyframe();
        }
        // Add labeled constructs: P point, V vector (to the active keyframe), B child bone.
        // B adds the bone to the active clip (clip-local); Shift+B adds it to the base rig.
        if (Pressed(kb, Keys.P)) BeginAddAddition(AnimAdditionKind.Point, mp);
        if (Pressed(kb, Keys.V)) BeginAddAddition(AnimAdditionKind.Vector, mp);
        if (Pressed(kb, Keys.B)) BeginAddBone(mp, toBase: shift);
        if (Pressed(kb, Keys.E) && Doc != null)
        {
            var joints = _pose.ComputeWorld(_root);
            int b = PickJoint(joints, mp);
            // A zero-length socket shares its parent's location. Prefer an existing
            // effect there so E edits it instead of adding a duplicate on the hand.
            float nearest = PickR * PickR;
            if (Doc.Attachments != null)
                foreach (var a in Doc.Attachments)
                {
                    int ab = EditorBoneOf(a.Point);
                    if (ab < 0) continue;
                    float d = Vector2.DistanceSquared(joints[ab].Translation, mp);
                    if (d < nearest) { nearest = d; b = ab; }
                }
            // An attachment picked from the endpoint menu or a timeline bar is the target;
            // only fall back to "nearest attached joint" when nothing is selected. The old
            // order re-found by bone and threw the explicit selection away.
            if (shift && _selectedAttachment != null && Doc.Attachments?.Contains(_selectedAttachment) == true)
                RemoveAttachment(_selectedAttachment);
            else if (b >= 0)
            {
                _effectBone = _skeleton.Bones[b].Name;
                _selectedAttachment = Doc.Attachments?.Find(a => EditorBoneOf(a.Point) == b);
                if (shift)
                {
                    if (_selectedAttachment != null) RemoveAttachment(_selectedAttachment);
                    _selectedAttachment = null;
                }
                else { _naming = NameTarget.Effect; _nameBuffer = _selectedAttachment?.Effect ?? "knife"; }
            }
        }
        if (_selectedAttachment != null && Doc?.Attachments?.Contains(_selectedAttachment) == true)
        {
            if (Pressed(kb, Keys.U) && _scrubT < _selectedAttachment.End)
            { _selectedAttachment.Start = _scrubT; _dirty = true; }
            if (Pressed(kb, Keys.I) && _scrubT > _selectedAttachment.Start)
            { _selectedAttachment.End = _scrubT; _dirty = true; }
        }
        if (Pressed(kb, Keys.OemComma))   StepKeyframe(-1);
        if (Pressed(kb, Keys.OemPeriod))  StepKeyframe(+1);
        if (ctrl && Pressed(kb, Keys.D0)) _placement.ResetZoom();
        if (Pressed(kb, Keys.H)) _showHelp = !_showHelp;
        if (Pressed(kb, Keys.OemTilde)) _preview.ShowGrid = !_preview.ShowGrid;
        if (Pressed(kb, Keys.O)) _preview.ShowBody = !_preview.ShowBody;
        if (_skin != null && Pressed(kb, Keys.G)) _showSkin = !_showSkin;
        if (_skin != null && Pressed(kb, Keys.W)) _skinWire = !_skinWire;
        if (_skin != null && Pressed(kb, Keys.X)) _showRig  = !_showRig;

        // Pan the whole VIEW (rig + com + floor together): arrows nudge (Shift = faster), Home
        // recenters. Placing the BODY against the ground is the root-joint drag (edits com).
        float nudge = shift ? 6f : 1.5f;
        if (kb.IsKeyDown(Keys.Left))  _placement.Pan.X -= nudge;
        if (kb.IsKeyDown(Keys.Right)) _placement.Pan.X += nudge;
        if (kb.IsKeyDown(Keys.Up))    _placement.Pan.Y -= nudge;
        if (kb.IsKeyDown(Keys.Down))  _placement.Pan.Y += nudge;
        if (Pressed(kb, Keys.Home))   _placement.Pan = Vector2.Zero;
        if (Doc != null)
        {
            if (Pressed(kb, Keys.OemOpenBrackets))  { Doc.Duration = MathF.Max(0.1f, Doc.Duration - 0.1f); _dirty = true; }
            if (Pressed(kb, Keys.OemCloseBrackets)) { Doc.Duration += 0.1f; _dirty = true; }
            if (Pressed(kb, Keys.L))                { Doc.Loop = !Doc.Loop; _dirty = true; }
            if (Pressed(kb, Keys.R))                { Doc.Region = (AnimRegion)(((int)Doc.Region + 1) % 3); _dirty = true; }
            if (Pressed(kb, Keys.T))                CycleType(shift ? -1 : +1);
            }
    }

    // Which side(s) of the data model an edit mutates this frame. The drag handler
    // routes the dirty flag accordingly: Pose touches force a keyframe re-capture +
    // anim-dirty; Rig touches set the skeleton-dirty flag for Ctrl-S.
    [System.Flags]
    private enum EditTouched { None = 0, Pose = 1, Rig = 2 }

    // Edit-mode write split (all drag the clicked joint toward the cursor):
    //   • Rotate  — pose only. Pivots the bone (and its subtree) about its joint; the
    //               keyframe absorbs the rotation. The bone's rest length is unchanged.
    //   • Resize  — split. The angular part rolls the pose rotation (as in Rotate); the
    //               radial part scales the bone's rest Length on the rig so its tip
    //               reaches the cursor.
    //   • Stretch — pose only, PER-KEYFRAME. Projects the cursor onto the bone's axis
    //               and writes the ratio as this keyframe's length Stretch (pseudo-3D
    //               foreshortening; see bakeyaw). Signed — dragging past the joint
    //               flips the bone slightly negative. Rotation is untouched, the rig
    //               is untouched, and other keyframes keep their own stretch.
    private EditTouched EditBone(Affine2[] world, int bone, Vector2 mp)
    {
        int parent = _skeleton.Bones[bone].Parent;
        if (parent < 0) return EditTouched.None;   // the root joint is whole-rig placement, not editable here

        Vector2 pivot     = world[parent].Translation;          // parent's tip = this bone's joint
        Vector2 boneVec   = world[bone].Translation - pivot;    // current bone direction (drawn in screen space)
        Vector2 cursorVec = mp - pivot;

        if (_editMode == EditMode.Stretch)
        {
            // Axis from the bone's world +X (magnitude = root scale) — NOT from boneVec,
            // which collapses at stretch 0 and would block dragging through the flip.
            float restLen = _skeleton.Bones[bone].Length;
            Vector2 axis  = world[bone].TransformVector(Vector2.UnitX);
            float axisSq  = axis.LengthSquared();
            if (restLen < 1e-3f || axisSq < 1e-6f) return EditTouched.None;
            float s = Vector2.Dot(cursorVec, axis) / (axisSq * restLen);
            _pose.Local[bone].Translation = Vector2.UnitX * (restLen * s);
            return EditTouched.Pose;
        }

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

    // Rig placement in the editor (ScenePlacement): centered in the working area, scaled, and —
    // when the clip authors a com track — COM-ANCHORED like the game. Recomputed each frame so
    // it tracks window size, the playhead, and (continuous-loop playback) the accumulated
    // cycle displacement. Exactly ONE source owns the body's scene path (ClipMotion).
    private void UpdateRoot()
    {
        var center = new Vector2(_canvas.Left + _canvas.Width / 2f, _canvas.Top + _canvas.Height * 0.46f);
        bool unwrapped = _playing && _placement.ContinuousLoop && Doc != null;
        float t = unwrapped ? _playTime / MathF.Max(Doc.Duration, 1e-4f) : _scrubT;
        _placement.Update(Doc, t, unwrapped, center);
        _root = _placement.Root;
    }
    // Documents behind the TRAJECTORY overlays (SceneOverlayKind.Arc / ClipPath), resolved by
    // name and cached. An arc is a REFERENCE now — it never places the clip, so nothing here
    // can disturb placement. The cache means an arc edited in another window is picked up on
    // the next editor run, not live.
    private readonly Dictionary<string, HermiteClipDocument> _overlayArcs = new();

    // Clip-local first when the overlay says so (AnimationDocument.Arcs), else the shared pool.
    private HermiteClipDocument ResolveArc(string name, bool local)
        => local ? ClipArcs.FindLocal(Doc, name) : ResolveSharedArc(name);

    // ── the open arc edit (ArcEditSession) ──────────────────────────────────────────────
    // The arc is drawn and dragged through the SAME frame ClipMotion.ArcOffset uses, so a key
    // sits exactly where mapping the arc would put the body: drag in the clip's scene, store
    // in the pixels the arc is authored in.
    //
    // The anchors are NOT draggable here. That frame pins clip-space Entry to the scene origin
    // and Gate to Span/scale, so both anchors are fixed points on screen whatever their values
    // — dragging them would be a gesture that cannot move anything. They are a framing choice
    // in this view (what the pixels MEAN), so the panel edits them as numbers; the standalone
    // `--ref` editor, whose view IS arc space, keeps dragging them spatially.
    private ReferenceFrame ArcFrame(HermiteClipDocument arc)
        => new(arc, Vector2.Zero, arc.Span / Game1.SkeletonScale);

    private Vector2 ArcToScreen(HermiteClipDocument arc, Vector2 p) => _placement.ToScreen(ArcFrame(arc).Map(p));
    private Vector2 ScreenToArc(HermiteClipDocument arc, Vector2 s) => ArcFrame(arc).Unmap(_placement.ToScene(s));

    private void DrawArcSession()
    {
        var s = _arcEdit;
        if (s == null) return;
        var arc = s.Working;
        if (arc.Keys.Count == 0) return;
        var curve = new Color(255, 200, 120);

        const int Samples = 64;
        Vector2 prev = ArcToScreen(arc, arc.Eval(0f));
        for (int i = 1; i <= Samples; i++)
        {
            Vector2 p = ArcToScreen(arc, arc.Eval(i / (float)Samples));
            _draw.Line(prev, p, curve, 2f);
            prev = p;
        }
        // The anchors, as read-only marks: entry green, gate pink.
        _draw.Ring(ArcToScreen(arc, arc.Entry), 6f, new Color(120, 220, 160), 12, 1.5f);
        _draw.Ring(ArcToScreen(arc, arc.Gate),  6f, new Color(240, 140, 160), 12, 1.5f);

        for (int i = 0; i < arc.Keys.Count; i++)
        {
            Vector2 k = ArcToScreen(arc, arc.Keys[i].Pos);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector2 h = ArcToScreen(arc, ArcEditOps.HandleTip(arc, i, side));
                _draw.Line(k, h, curve * 0.5f, 1f);
                _draw.Ring(h, 3.5f, curve * 0.8f, 8, 1f);
            }
            if (i == s.Selected) { _draw.Disc(k, 6f, Color.White); _draw.Ring(k, 9f, curve, 12, 1.5f); }
            else _draw.Disc(k, 4.5f, curve);
        }
    }

    // Keys win over tangent handles — the standalone editor's order, minus the anchors.
    private bool PickArcSession(Vector2 mp)
    {
        var s = _arcEdit;
        if (s == null) return false;
        var arc = s.Working;

        float best = PickR * PickR; int key = -1;
        for (int i = 0; i < arc.Keys.Count; i++)
        {
            float d = Vector2.DistanceSquared(ArcToScreen(arc, arc.Keys[i].Pos), mp);
            if (d < best) { best = d; key = i; }
        }
        if (key >= 0) { s.DragKey = key; s.Selected = key; return true; }

        best = PickR * PickR;
        for (int i = 0; i < arc.Keys.Count; i++)
            for (int side = -1; side <= 1; side += 2)
            {
                float d = Vector2.DistanceSquared(ArcToScreen(arc, ArcEditOps.HandleTip(arc, i, side)), mp);
                if (d < best) { best = d; s.DragHandle = i; s.HandleSide = side; s.Selected = i; }
            }
        return s.DragHandle >= 0;
    }

    private void DragArcSession(Vector2 mp)
    {
        var s = _arcEdit;
        var arc = s.Working;
        Vector2 p = ScreenToArc(arc, mp);
        if (s.DragKey >= 0)         ArcEditOps.DragKey(arc, s.DragKey, p);
        else if (s.DragHandle >= 0) ArcEditOps.DragHandle(arc, s.DragHandle, s.HandleSide, p);
        s.Dirty = true;
    }

    private HermiteClipDocument ResolveSharedArc(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_overlayArcs.TryGetValue(name, out var cached)) return cached;
        string path = RefArcPath(name);
        var doc = (path != null ? HermiteClipDocument.Load(path) : null) ?? ReferenceClipRegistry.Get(name);
        _overlayArcs[name] = doc;
        return doc;
    }

    private AnimationDocument ResolveClipOverlay(string name)
        => string.IsNullOrWhiteSpace(name) ? null
         : _docs.Find(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private string RefArcPath(string name)
    {
        string root = Path.GetDirectoryName(FindStatesDir());
        return root == null ? null : Path.Combine(root, "ReferenceClips", name + ".json");
    }

    // Every arc name the editor can offer: the baked registry's, plus any other file
    // sitting in ReferenceClips/. Sorted, so cycling is stable across runs.
    private List<string> ArcNames()
    {
        var names = new SortedSet<string>(ReferenceClipRegistry.KnownNames, StringComparer.OrdinalIgnoreCase);
        string root = Path.GetDirectoryName(FindStatesDir());
        string dir = root == null ? null : Path.Combine(root, "ReferenceClips");
        if (dir != null && Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.json"))
                names.Add(Path.GetFileNameWithoutExtension(f));
        return new List<string>(names);
    }

    // A named root-space Point track at normalized time t — the shared sparse-channel C1
    // sampler (same one the runtime uses, so the editor preview matches the game): only
    // keyframes that author the point are its keys, gaps bridge smoothly, and motion
    // between authored keys is Catmull-Rom like the pose spline.
    private bool TryPointAt(float t, string name, out Vector2 p)
        => AnimAdditionSampler.SamplePoint(Doc, t, name, out p);

    // The active keyframe's com addition, if it has one (the thing a root drag edits).
    private AnimAddition ActiveKeyCom() => ActiveKeyPoint("com");

    private AnimAddition ActiveKeyPoint(string name)
    {
        if (Doc == null || _activeKey < 0 || _activeKey >= Doc.Keyframes.Count) return null;
        var adds = Doc.Keyframes[_activeKey].Additions;
        if (adds == null) return null;
        foreach (var a in adds)
            if (a.Kind == AnimAdditionKind.Point && a.Name == name && a.Parent == null) return a;
        return null;
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

        // Sprite skin under the rig overlay: deform to the current working pose (its own
        // device draw, so it must run outside the SpriteBatch pass). fill:false still
        // deforms so the wireframe overlay (drawn inside the batch) has fresh positions.
        if (_skin != null && (_showSkin || _skinWire))
        {
            SyncSkinPose();
            _skin.Draw(Matrix.Identity, _skinPose, _root, fill: _showSkin);
        }

        _attachmentSamples.Clear();
        AttachmentSampling.Append(Doc, _scrubT, 1, _attachmentSamples, _skeleton);
        if (!_playing) _attachments.ClearHistory();
        _attachments.Draw(Matrix.Identity, _pose, _root, _attachmentSamples,
            _playing ? this : null, (float)gameTime.ElapsedGameTime.TotalSeconds);

        _spriteBatch.Begin();
        DrawEditor();
        _spriteBatch.End();

        // The chrome (built during Update) draws last, so panels and popups sit above the
        // canvas and clip their own contents.
        _ui.RenderDrawData();

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

    // Copy the working pose onto the skin's pristine base rig BY NAME, so the skin
    // follows scrub/playback regardless of clip-local ExtraBones (absent from _skinRig)
    // or bones added to the base rig this session (left at bind on the skin).
    private void SyncSkinPose()
    {
        for (int i = 0; i < _skinRig.Count; i++)
        {
            int wi = _skeleton.IndexOf(_skinRig.Bones[i].Name);
            if (wi >= 0) _skinPose.Local[i] = _pose.Local[wi];
        }
    }

    // --usebind argument → SpriteBindings/<name>.json (accepts a bare name, a .png/.json
    // name, or an existing path). Null when nothing plausible exists.
    private static string ResolveBindingPath(string arg)
    {
        if (File.Exists(arg) && arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(arg);
        string name = Path.HasExtension(arg) ? Path.ChangeExtension(arg, ".json") : arg + ".json";
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "SpriteBindings", name);
            if (File.Exists(c)) return c;
            if (File.Exists(Path.Combine(d.FullName, "MTile.sln"))) break;
            d = d.Parent;
        }
        return null;
    }

    private void DrawEditor()
    {
        // Scene layers under the rig: the tile grid, the clip's authored guides, the body's
        // scene path, keyframe ghosts, the physics silhouette.
        var (frame, groundY) = _placement.GuideFrame();
        _preview.DrawGrid(_draw, frame, groundY, _canvas.Left, _canvas.Right, _canvas.Top, _canvas.Bottom);
        _guides.Draw(_draw, _spriteBatch, _font, frame, Doc, _canvas.Left, _canvas.Right);
        _preview.DrawOverlays(_draw, _spriteBatch, _font, frame, _placement, Doc,
                              _guides.Effective(Doc), ResolveArc, ResolveClipOverlay,
                              _canvas.Left, _canvas.Right);
        _preview.DrawPath(_draw, _placement, Doc);
        DrawArcSession();
        _preview.DrawGhosts(_draw, _placement, Doc, _skeleton, _ghostPose, _kfA, _kfB, _kfC, _kfD, _activeKey);
        _preview.DrawBody(_draw, _placement);

        // The rig overlay (bones + joint markers + additions) hides when toggled off
        // (X, skin-view only) so the sprite can be judged unobstructed. Editing still
        // works while hidden — picking is position-based, not marker-based.
        bool showRig = _showRig || _skin == null;
        if (showRig)
        {
            var style = SkeletonDrawStyle.Default;
            style.BoneThickness = 3f;
            style.JointRadius   = 0f;
            // Dim the figure when on an interpolated (non-editable) frame.
            style.BoneColor = _activeKey >= 0 ? Color.White : new Color(150, 150, 160);
            SkeletonRenderer.Draw(_draw, _pose, _root, style);
        }
        if (_skin != null && _skinWire)
            _skin.DrawWireframe(_draw, new Color(90, 220, 230) * 0.5f);

        var world = _pose.ComputeWorld(_root);
        if (!showRig) return;
        // The selected endpoint's segment reads highlighted (at a shared joint this says which
        // bone's END is the target), and the endpoint itself gets a bright ring.
        if (_selectedEndpoint >= 0 && _selectedEndpoint < world.Length)
        {
            int par = _skeleton.Bones[_selectedEndpoint].Parent;
            Vector2 from = par >= 0 ? world[par].Translation : new Vector2(_root.Tx, _root.Ty);
            _draw.Line(from, world[_selectedEndpoint].Translation, new Color(150, 200, 255) * 0.6f, 6f);
            _draw.Ring(world[_selectedEndpoint].Translation, 8f, new Color(150, 200, 255), 16, 1.5f);
        }
        var contacts = Doc.Contacts;
        for (int i = 0; i < world.Length; i++)
        {
            Vector2 p = world[i].Translation;
            if (_activeKey < 0) { _draw.Ring(p, 4f, new Color(90, 95, 110), 10, 1f); continue; }

            // Contact-labeled nodes get a green halo behind the normal marker.
            if (HasContactOnBone(contacts, i))
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

        // IK drag feedback: the target cross under the cursor and the miss distance (rig
        // units) — an unreachable target shows the closest reachable pose plus how far off.
        if (_ikDrag != null && _dragBone >= 0)
        {
            var ms = Mouse.GetState();
            var mp = new Vector2(ms.X, ms.Y);
            var c = new Color(255, 200, 120);
            _draw.Line(mp - new Vector2(6f, 0f), mp + new Vector2(6f, 0f), c, 1.5f);
            _draw.Line(mp - new Vector2(0f, 6f), mp + new Vector2(0f, 6f), c, 1.5f);
            _spriteBatch.DrawString(_font, $"miss {_ikDrag.Last.Miss:0.00}", mp + new Vector2(10f, -6f), c);
        }
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
            if (a.Name == BodyPathName) continue;   // hidden channel — placement, not a marker
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

    // Does a contact span covering the PLAYHEAD resolve to bone `i`? The canvas halo follows
    // the playhead now rather than the active keyframe — a span is live between keys too.
    private bool HasContactOnBone(List<ContactSpan> spans, int i)
    {
        if (spans == null) return false;
        foreach (var c in spans)
            if (c.Covers(_scrubT, out _) && EditorBoneOf(c.Point) == i) return true;
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
        }
        if (doc.Contacts != null)
            foreach (var c in doc.Contacts)
            {
                // Mirror the point id (support_l ↔ support_r). Spans keep their timing:
                // a mirror swaps which foot plants, never when.
                string mp = MirrorBoneName(c.Point);
                if (mp != null) c.Point = mp;
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

    // Toggle a No-slip (SelfPlant) contact on a bone's End at the playhead — the M+click quick
    // path. Removes the span under the playhead if there is one, else starts a new one there.
    private void ToggleContact(int bone) => ApplyContact(bone, SpanAtPlayhead(bone) >= 0 ? null : ContactSource.SelfPlant);

    // Index of the span on `bone` that covers the playhead, or -1. Contacts are intervals, so
    // "the contact on this bone" is only meaningful relative to a time.
    private int SpanAtPlayhead(int bone)
    {
        var spans = Doc?.Contacts;
        if (spans == null) return -1;
        for (int i = 0; i < spans.Count; i++)
            if (spans[i].Covers(_scrubT, out _) && EditorBoneOf(spans[i].Point) == bone) return i;
        return -1;
    }

    // The named point at a bone's End (rig first, then the clip), or null.
    private NamedPoint PointFor(int bone)
    {
        string name = _skeleton.Bones[bone].Name;
        static bool At(NamedPoint p, string n) => p.Bone == n && p.End == BoneEnd.End && p.Ox == 0f && p.Oy == 0f;
        foreach (var p in _skeleton.Points) if (At(p, name)) return p;
        if (Doc?.Points != null) foreach (var p in Doc.Points) if (At(p, name)) return p;
        return null;
    }

    // The interval a newly-authored contact gets: the playhead to the NEXT keyframe, or the
    // whole clip under the whole-clip scope. Keyframes are a sensible first guess at where a
    // plant ends — but only a guess now, because the span's ends are draggable afterwards.
    private (float start, float end) NewSpanRange()
    {
        if (_contactWholeClip) return (0f, 1f);
        float start = _scrubT, end = 1f;
        foreach (var kf in Doc.Keyframes) if (kf.Time > start + 1e-4f) { end = kf.Time; break; }
        if (end <= start + 1e-3f) end = MathF.Min(1f, start + 0.1f);
        return (start, end);
    }

    // Make sure the bone's End has a named point (a "contact point"), creating a clip point
    // when neither the rig nor the clip names it. One setup step for a simple plant.
    private NamedPoint EnsureContactPoint(int bone)
    {
        var p = PointFor(bone);
        if (p != null) return p;
        Doc.Points ??= new List<NamedPoint>();
        string stem = _skeleton.Bones[bone].Name + "_tip";
        string id = stem; for (int n = 2; Doc.Points.Exists(x => x.Id == id) || _skeleton.IndexOf(id) >= 0; n++) id = $"{stem}{n}";
        p = new NamedPoint { Id = id, Bone = _skeleton.Bones[bone].Name, End = BoneEnd.End, Role = "contact" };
        Doc.Points.Add(p);
        _dirty = true;
        return p;
    }

    // Set (source) or clear (null) the contact on a bone's End AT THE PLAYHEAD. Setting a source
    // on a span that already covers the playhead retypes it in place — the interval is the
    // author's, so changing SelfPlant to PlannedSupport must not silently retime the plant.
    // Otherwise a new span is authored over NewSpanRange, then dragged on the timeline.
    // No keyframe is sampled: a contact no longer needs one to exist.
    private void ApplyContact(int bone, ContactSource? src)
    {
        if (Doc == null || bone < 0) return;
        int at = SpanAtPlayhead(bone);
        if (src == null)
        {
            if (at < 0) return;
            Doc.Contacts.RemoveAt(at);
            if (Doc.Contacts.Count == 0) Doc.Contacts = null;
            _dirty = true;
            return;
        }
        if (at >= 0) { Doc.Contacts[at].Source = src.Value; _dirty = true; return; }

        string point = EnsureContactPoint(bone).Id;
        var (start, end) = NewSpanRange();
        Doc.Contacts ??= new List<ContactSpan>();
        Doc.Contacts.Add(new ContactSpan { Point = point, Start = start, End = end, Source = src.Value });
        Doc.Contacts.Sort((x, y) => x.Start.CompareTo(y.Start));
        _dirty = true;
    }

    // "Add knife" in one operation: a clip-local orientation bone at the endpoint plus the
    // knife attachment on it (the existing ExtraBones + Attachments mechanisms, exactly the
    // shape groundslash1 authors by hand), selected so U/I trim its window.
    private void AddKnife(int bone)
    {
        if (Doc == null) return;
        string name = UniqueBoneName("knife");
        Doc.ExtraBones ??= new List<SkeletonBoneRecord>();
        Doc.ExtraBones.Add(new SkeletonBoneRecord { Name = name, Parent = _skeleton.Bones[bone].Name, Rotation = 0f, Length = 0f });
        RebuildWorkingRig();
        // Name the knife bone's tip and hang the attachment off THAT, not off the bone name.
        // The bare-name fallback would resolve identically today, but only a declared point
        // survives a rename — which is the whole reason attachments moved onto points.
        int kb = _skeleton.IndexOf(name);
        string point = kb >= 0 ? EnsureContactPoint(kb).Id : name;
        Doc.Attachments ??= new List<AnimAttachment>();
        _selectedAttachment = new AnimAttachment { Point = point, Effect = "knife", Start = _scrubT,
                                                   End = 1f - MathHelper.Clamp(Doc.SettleShare, 0f, 0.95f) };
        if (_selectedAttachment.End <= _selectedAttachment.Start) _selectedAttachment.Start = 0f;
        Doc.Attachments.Add(_selectedAttachment);
        _selectedEndpoint = _skeleton.IndexOf(name);
        _dirty = true;
    }

    // Remove an attachment AND, when it was the last thing holding a clip-local bone up, that
    // bone's ExtraBones record. AddKnife creates the pair together, so deleting only half left
    // an orphan bone in the clip that nothing could ever remove — the file grew a dead bone per
    // knife you changed your mind about. A base-rig bone is never touched; it isn't ours.
    private void RemoveAttachment(AnimAttachment a)
    {
        if (Doc?.Attachments == null || a == null) return;
        Doc.Attachments.Remove(a);
        if (Doc.Attachments.Count == 0) Doc.Attachments = null;
        if (_selectedAttachment == a) _selectedAttachment = null;

        // Resolve the anchor before deciding what else goes with it.
        int bi = EditorBoneOf(a.Point);
        string bone = bi >= 0 ? _skeleton.Bones[bi].Name : null;

        // The attachment's OWN point goes too when nothing else references it — otherwise the
        // point AddKnife created would hold its own bone up forever and the knife could never
        // be fully removed.
        var own = Doc.Points?.Find(p => p.Id == a.Point);
        if (own != null && !PointStillUsed(a.Point))
        {
            Doc.Points.Remove(own);
            if (Doc.Points.Count == 0) Doc.Points = null;
        }

        string keep = bone == null || IsBaseBone(bone) || Doc.ExtraBones == null ? "base rig bone" : BoneHolder(bone, bi);
        if (keep != null)
        {
            Console.WriteLine($"removed attachment '{a.Effect}' on '{a.Point}'; kept the bone ({keep})");
            _dirty = true;
            return;
        }

        // The bone's own animation data goes with it: its pose entries and anything hung off
        // it exist only to orient the thing we just deleted.
        int shed = 0;
        foreach (var kf in Doc.Keyframes)
        {
            if (kf.Bones != null)     shed += kf.Bones.RemoveAll(e => e.Bone == bone);
            if (kf.Additions != null) shed += kf.Additions.RemoveAll(ad => ad.Parent == bone);
        }
        Doc.ExtraBones.RemoveAll(r => r.Name == bone);
        if (Doc.ExtraBones.Count == 0) Doc.ExtraBones = null;
        Console.WriteLine($"removed attachment '{a.Effect}' and its clip-local bone '{bone}'"
                        + (shed > 0 ? $" ({shed} pose/addition entr{(shed == 1 ? "y" : "ies")})" : ""));
        RebuildWorkingRig();
        _dirty = true;
    }

    // Does anything still reference this point id?
    private bool PointStillUsed(string id)
    {
        if (Doc.Attachments != null) foreach (var a in Doc.Attachments) if (a.Point == id) return true;
        if (Doc.Contacts    != null) foreach (var c in Doc.Contacts)    if (c.Point == id) return true;
        return false;
    }

    // Why this clip-local bone must survive its attachment's removal, or null if nothing
    // needs it. Pose entries and additions deliberately do NOT count — those are the bone's
    // own data and are shed with it; a knife you posed must still be deletable.
    private string BoneHolder(string bone, int boneIndex)
    {
        if (Doc.Attachments != null) foreach (var a in Doc.Attachments) if (EditorBoneOf(a.Point) == boneIndex) return "another attachment uses it";
        if (Doc.Contacts    != null) foreach (var c in Doc.Contacts)    if (EditorBoneOf(c.Point) == boneIndex) return "a contact is on it";
        if (Doc.ExtraBones  != null) foreach (var r in Doc.ExtraBones)  if (r.Parent == bone) return $"bone '{r.Name}' hangs off it";
        if (Doc.Points      != null) foreach (var p in Doc.Points)      if (p.Bone == bone) return $"point '{p.Id}' is on it";
        return null;
    }

    private void RemoveSelectedPoint()
    {
        if (Doc?.Points == null || _selectedPointId == null) return;
        // A point's dependent annotations go with it (reported), never left dangling.
        int deps = 0;
        if (Doc.Contacts != null)
        {
            deps = Doc.Contacts.RemoveAll(c => c.Point == _selectedPointId);
            if (Doc.Contacts.Count == 0) Doc.Contacts = null;
        }
        Doc.Points.RemoveAll(p => p.Id == _selectedPointId);
        if (Doc.Points.Count == 0) Doc.Points = null;
        Console.WriteLine($"removed point '{_selectedPointId}'" + (deps > 0 ? $" and {deps} contact annotation(s) on it" : ""));
        _selectedPointId = null;
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
        if (_naming == NameTarget.Effect && Doc != null)
        {
            Doc.Attachments ??= new List<AnimAttachment>();
            if (_selectedAttachment == null)
            {
                int eb = _skeleton.IndexOf(_effectBone);
                _selectedAttachment = new AnimAttachment {
                    Point = eb >= 0 ? EnsureContactPoint(eb).Id : _effectBone,
                    End = 1 - MathHelper.Clamp(Doc.SettleShare, 0, .95f) };
                Doc.Attachments.Add(_selectedAttachment);
            }
            _selectedAttachment.Effect = name;
            _dirty = true;
        }
        else if (_naming == NameTarget.Addition && _pendingAddition != null && Doc != null && _activeKey >= 0)
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
        else if (_naming == NameTarget.Point && Doc != null && _pendingBoneParent >= 0 && _pendingBoneParent < _skeleton.Count)
        {
            Doc.Points ??= new List<NamedPoint>();
            string id = name; for (int n = 2; Doc.Points.Exists(x => x.Id == id) || _skeleton.IndexOf(id) >= 0; n++) id = $"{name}{n}";
            Doc.Points.Add(new NamedPoint { Id = id, Bone = _skeleton.Bones[_pendingBoneParent].Name, End = BoneEnd.End, Role = "marker" });
            _selectedPointId = id;
            _dirty = true;
        }
        EndNaming();
    }

    private void CancelName() => EndNaming();
    private void EndNaming() { _naming = NameTarget.None; _nameBuffer = ""; _pendingAddition = null; }

    private string DefaultName()
    {
        if (_naming == NameTarget.Effect) return "knife";
        if (_naming == NameTarget.Bone) return UniqueBoneName("bone");
        if (_naming == NameTarget.Point) return "marker";
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
        _ghostPose = _skeleton.CreatePose();
        if (Doc != null && _activeKey >= 0) PoseData.Apply(Doc.Keyframes[_activeKey].Bones, _pose);
        else SamplePose(_scrubT);
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
            if (a.Name == BodyPathName) continue;   // hidden channel — the com marker is its proxy
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
        // The com is the player's BASE FRAME (the sim body anchor). Dragging it places the
        // PLAYER ensemble — com marker + skeleton — against the fixed scenery (floor line,
        // obstacle block) PER KEYFRAME: the drag writes this keyframe's "edref" placement,
        // and scrubbing interpolates the track so the body visibly arcs over the refs.
        // Editor-only visualization data (saved with the clip; runtime ignores it).
        // Moving the body WITHIN the com frame (the keyframe's com channel) is the
        // root-joint drag; panning everything together is the arrow keys.
        if (a.Kind == AnimAdditionKind.Point && a.Name == "com" && a.Parent == null)
        {
            // The clip's own body_path is the ONLY placement channel, so a com drag always
            // authors it — there is nothing else that could own the body's position, and no
            // refusal to explain. Dragging a clip that was declared stationary simply makes
            // it a travelling one, so the declaration follows the data.
            if (Doc.Motion != MotionSource.Track) { Doc.Motion = MotionSource.Track; _dirty = true; }
            var refAdd = ActiveKeyPoint(BodyPathName);
            if (refAdd == null)
            {
                // Seed from the value currently displayed (the sampled/held track, or zero)
                // so the first drag continues smoothly instead of jumping.
                TryPointAt(_scrubT, BodyPathName, out var seed);
                var kf = Doc.Keyframes[_activeKey];
                kf.Additions ??= new List<AnimAddition>();
                kf.Additions.Add(refAdd = new AnimAddition
                {
                    Name = BodyPathName, Kind = AnimAdditionKind.Point, Px = seed.X, Py = seed.Y,
                });
            }
            var dm = (mp - new Vector2(_prevMs.X, _prevMs.Y)) / _placement.Scale;
            refAdd.Px += dm.X;
            refAdd.Py += dm.Y;
            _dirty = true;
            return;
        }
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

    // Grouped key cheatsheet, toggled by H. Sections so related controls cluster instead
    // of one dense line — scales as the editor grows (additions, bones, …).
    private static readonly (string Group, string Keys)[] HelpRows =
    {
        ("Clip",     "[ ] duration    L loop    R region    T type    N new    C clone"),
        ("Edit",     "Tab mode (rotate/resize/stretch)    drag joint    M+click contact    F flip    IK drag box (header): drag a joint = IK pull of its limb, Esc restores"),
        ("Move",     "drag root joint = place body vs fixed ground/com (edits keyframe com)    arrows pan view (Shift faster)    Home recenter"),
        ("View",     "wheel zoom (about the cursor)    middle-drag pan    Ctrl+0 reset zoom    View > Frame scene/path fits    ` block grid on/off (1 cell = 1 game tile, anchored to the floor line)    O physics hexagon at the com"),
        ("Scene",    "Scene menu (top right): add/select/duplicate/delete/hide/lock guides, snap, motion source, path/ghosts, frame/follow view"),
        ("Add",      "P point    V vector    B clip bone  (Shift+B base rig)    (then name, Enter)"),
        ("Endpoint", "click a joint = select its endpoint, then the small v (or right-click) opens: add knife / element / contact point / marker, contact no-slip / planned / external / clear, scope"),
        ("Effect",   "E over joint: attach/name effect (knife)    Shift+E remove    U/I set selected effect start/end at playhead"),
        ("Keyframe", "K sample    Del delete    , . previous/next keyframe    click / drag a timeline bar    Space play"),
        ("Skin",     "G sprite skin on/off    W mesh wireframe    X skeleton on/off    (launch with --usebind <binding>)"),
        ("File",     "Ctrl-S save  (writes clips + rig)"),
    };

    // Modal name prompt while adding a point/vector/bone.
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
        _selectedAttachment = null;
        _attachments?.ClearHistory();
        _playing = false;
        _selected = i;
        // Ask the clip list to reveal the row (the open-by-name jump and N/C appends land
        // off screen; a plain click is already visible, and scrolling to it is harmless).
        _scrollToSelected = true;
        _activeKey = -1;            // stale index from the previous clip; SelectKeyframe resets it
        var doc = _docs[i];
        RebuildWorkingRig();        // compose base + THIS clip's ExtraBones
        if (doc.Keyframes.Count == 0)
            doc.Keyframes.Add(new AnimationKeyframe { Time = 0f, Bones = PoseData.Capture(_skeleton.CreatePose()) });
        doc.SortKeyframes();
        SelectKeyframe(0);          // render the first frame
    }

    // , / . — step to the previous/next keyframe and make it the editable one. From an
    // interpolated playhead it jumps to the neighbouring key in that direction.
    private void StepKeyframe(int dir)
    {
        var doc = Doc;
        if (doc == null || doc.Keyframes.Count == 0) return;
        int k;
        if (_activeKey >= 0) k = MathHelper.Clamp(_activeKey + dir, 0, doc.Keyframes.Count - 1);
        else if (dir > 0)
        {
            k = doc.Keyframes.FindIndex(x => x.Time > _scrubT + SnapEps);
            if (k < 0) k = doc.Keyframes.Count - 1;
        }
        else
        {
            k = doc.Keyframes.FindLastIndex(x => x.Time < _scrubT - SnapEps);
            if (k < 0) k = 0;
        }
        _playing = false;
        SelectKeyframe(k);
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
            Additions = AnimAdditionSampler.CloneEffectiveAt(doc, _scrubT),
        };
        doc.Keyframes.Add(kf);
        doc.SortKeyframes();
        _activeKey = doc.Keyframes.IndexOf(kf);
        _dirty = true;
    }

    // Deep-copy the contact marks from the keyframe at or before `t` (the marks "in
    // effect" there), so a sampled keyframe carries them over rather than starting bare.
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
            SettleShare = src.SettleShare,
            OffRegionWeight = src.OffRegionWeight,
            Motion   = src.Motion,
            Scene    = src.Scene?.Clone(),
            Points   = src.Points?.ConvertAll(p => p.Clone()),
            ExtraBones = src.ExtraBones?.ConvertAll(b => new SkeletonBoneRecord
                { Name = b.Name, Parent = b.Parent, Rotation = b.Rotation, Length = b.Length }),
            Attachments = src.Attachments?.ConvertAll(a => a.Clone()),
            Contacts    = src.Contacts?.ConvertAll(c => c.Clone()),
        };
        foreach (var kf in src.Keyframes)
            copy.Keyframes.Add(new AnimationKeyframe
            {
                Time      = kf.Time,
                Bones     = CloneBones(kf.Bones),
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
            copy.Add(new PoseBoneEntry { Bone = b.Bone, Rotation = b.Rotation, Stretch = b.Stretch });
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

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        base.OnExiting(sender, args);
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

    // Skeletons/ sits beside SkeletonStates/ at the repo root. `statesDir` is the
    // rig-scoped SkeletonStates/<rigName>/, so walk up two levels.
    private static string SkeletonsDir(string statesDir)
        => Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(statesDir)) ?? Directory.GetCurrentDirectory(),
            "Skeletons");

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
                && t.Name != nameof(NullAction) && t.Name != nameof(RecoveryAction))
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

    protected override void UnloadContent()
    {
        _attachments?.Dispose();
        _skin?.Dispose();
        _ui?.Dispose();
        base.UnloadContent();
    }

}
