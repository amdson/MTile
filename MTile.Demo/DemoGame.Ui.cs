using System;
using System.Collections.Generic;
using ImGuiNET;
using Microsoft.Xna.Framework;
using MTile;
using NVec2 = System.Numerics.Vector2;

namespace MTileDemo;

// THE EDITOR'S CHROME — menu bar, clip list, inspector, timeline, popups — on Dear ImGui
// (MTile.Demo/ImGuiRenderer.cs is the MonoGame backend).
//
// Why: every panel used to compute its own rectangles (fixed row heights, hand-rolled
// scroll geometry, a dropdown that measured nothing), so long names and long warnings drew
// over their neighbours and panels could not be resized or reordered. ImGui owns layout,
// clipping, scrolling and z-order instead; the constants are gone.
//
// WHAT DID NOT CHANGE: everything on the canvas. Joint picking, the IK drag session and its
// Escape restore, additions, guide tools, the com/root drag, panning — all still live in
// DemoGame.Update against the same world positions. The one new contract is that the canvas
// only acts when ImGui does not want the cursor (io.WantCaptureMouse) and hotkeys only fire
// when it does not want the keyboard (io.WantCaptureKeyboard, true while a text field has
// focus), which replaces the old "header UI has picking priority" convention.
public sealed partial class DemoGame
{
    private ImGuiRenderer _ui;

    // The working area left over after the panels — the canvas. Everything that used to be
    // measured from SidebarW / W / TrackY reads this instead.
    private Rectangle _canvas;

    private const float LeftW   = 270f;
    private const float RightW  = 330f;
    private const float BottomH = 150f;

    private string _clipFilter = "";
    private bool   _scrollToSelected;     // SelectAnimation asks the list to reveal the row
    private Vector2 _endpointScreen;      // where the selected endpoint drew last frame
    private bool   _openEndpointPopup;    // a right-click (or the "v" button) asked for the menu
    private bool   _nameFocus;            // focus the name field on the frame the modal opens

    private void InitUi() => _ui = new ImGuiRenderer(this);

    // Built at the top of Update: widget actions then mutate editor state on the normal
    // update path, and the canvas can read WantCapture* below.
    private void BuildUi(GameTime time)
    {
        _ui.BeginFrame(time);

        float menuH = MenuBar();
        float h = ImGui.GetIO().DisplaySize.Y, w = ImGui.GetIO().DisplaySize.X;
        ClipsPanel(menuH, h);
        // The right column is split: clip properties on top, the scene list under it.
        float split = menuH + (h - menuH) * 0.58f;
        InspectorPanel(menuH, w, split);
        ScenePanel(w, split, h);
        TimelinePanel(menuH, w, h);
        EndpointPopup();
        ArcEditorWindow();
        ContactCurveWindow();
        NameModal();
        HelpWindow(menuH);

        _canvas = new Rectangle((int)LeftW, (int)menuH,
                                (int)MathF.Max(1f, w - LeftW - RightW),
                                (int)MathF.Max(1f, h - menuH - BottomH));
        _ui.EndFrame();
    }

    // ── menu bar ────────────────────────────────────────────────────────────────────
    private float MenuBar()
    {
        float height = 0f;
        if (!ImGui.BeginMainMenuBar()) return height;
        height = ImGui.GetWindowSize().Y;
        var doc = Doc;

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Save all", "Ctrl+S", false, _dirty || _skelDirty)) SaveAll();
            ImGui.Separator();
            if (ImGui.MenuItem("New clip", "N")) NewAnimation();
            if (ImGui.MenuItem("Clone clip", "C", false, doc != null)) CloneAnimation();
            ImGui.Separator();
            if (ImGui.MenuItem("Quit", "Esc")) Exit();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Clip"))
        {
            if (doc == null) ImGui.TextDisabled("(no clip)");
            else
            {
                if (ImGui.MenuItem("Duration -0.1", "[")) { doc.Duration = MathF.Max(0.1f, doc.Duration - 0.1f); _dirty = true; }
                if (ImGui.MenuItem("Duration +0.1", "]")) { doc.Duration += 0.1f; _dirty = true; }
                if (ImGui.MenuItem("Loop", "L", doc.Loop)) { doc.Loop = !doc.Loop; _dirty = true; }
                if (ImGui.MenuItem("Cycle region", "R")) { doc.Region = (AnimRegion)(((int)doc.Region + 1) % 3); _dirty = true; }
                if (ImGui.MenuItem("Cycle type", "T")) CycleType(+1);
                ImGui.Separator();
                if (ImGui.MenuItem("Flip left/right", "F")) FlipAnimation();
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Edit"))
        {
            foreach (EditMode m in Enum.GetValues<EditMode>())
                if (ImGui.MenuItem(m.ToString(), m == EditMode.Rotate ? "Tab" : null, _editMode == m)) _editMode = m;
            ImGui.Separator();
            if (ImGui.MenuItem("IK drag", null, _ikMode)) { _ikMode = !_ikMode; if (!_ikMode) _ikDrag = null; }
            ImGui.Separator();
            if (ImGui.MenuItem("Sample keyframe", "K", false, doc != null)) SampleKeyframe();
            if (ImGui.MenuItem("Delete keyframe", "Del", false, _activeKey >= 0)) DeleteActiveKeyframe();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Scene")) { SceneMenuItems(); ImGui.EndMenu(); }

        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Path", null, _preview.ShowPath)) _preview.ShowPath = !_preview.ShowPath;
            if (ImGui.MenuItem("Pose ghosts", null, _preview.ShowGhosts)) _preview.ShowGhosts = !_preview.ShowGhosts;
            if (ImGui.MenuItem("Contact marks", null, _preview.ShowContacts)) _preview.ShowContacts = !_preview.ShowContacts;
            if (ImGui.MenuItem("Physics body", "O", _preview.ShowBody)) _preview.ShowBody = !_preview.ShowBody;
            if (ImGui.MenuItem("Tile grid", "`", _preview.ShowGrid)) _preview.ShowGrid = !_preview.ShowGrid;
            ImGui.Separator();
            if (ImGui.MenuItem("Follow view", null, _placement.FollowView)) _placement.FollowView = !_placement.FollowView;
            if (ImGui.MenuItem("Continuous loop preview", null, _placement.ContinuousLoop)) _placement.ContinuousLoop = !_placement.ContinuousLoop;
            if (ImGui.MenuItem("Frame scene/path (fit)", null, false, Doc != null)) FrameScene();
            if (ImGui.MenuItem("Zoom in", "wheel up")) _placement.ZoomBy(1.25f, _canvas.Center.ToVector2());
            if (ImGui.MenuItem("Zoom out", "wheel down")) _placement.ZoomBy(0.8f, _canvas.Center.ToVector2());
            if (ImGui.MenuItem("Reset zoom", "Ctrl+0", false, MathF.Abs(_placement.Zoom - 1f) > 1e-3f)) _placement.ResetZoom();
            if (ImGui.MenuItem("Recenter view", "Home")) _placement.Pan = Vector2.Zero;
            if (_skin != null)
            {
                ImGui.Separator();
                if (ImGui.MenuItem("Sprite skin", "G", _showSkin)) _showSkin = !_showSkin;
                if (ImGui.MenuItem("Mesh wireframe", "W", _skinWire)) _skinWire = !_skinWire;
                if (ImGui.MenuItem("Skeleton", "X", _showRig)) _showRig = !_showRig;
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Controls", "H", _showHelp)) _showHelp = !_showHelp;
            ImGui.EndMenu();
        }

        // Status, right-aligned: what is loaded, where the playhead is, and whether it is saved.
        string state = _playing        ? $"playing  t={_scrubT:0.00}"
                     : _activeKey >= 0 ? $"key {_activeKey}  t={_scrubT:0.00}"
                                       : $"interpolated  t={_scrubT:0.00}  (K samples)";
        string tag = (_dirty && _skelDirty) ? "  *unsaved anim+rig*"
                   : _dirty                 ? "  *unsaved*"
                   : _skelDirty             ? "  *unsaved rig*" : "";
        string status = $"{(doc != null ? $"[{doc.Type}] {doc.Name}" : "(no clip)")}   {state}{tag}";
        float pad = ImGui.CalcTextSize(status).X + 24f;
        ImGui.SameLine(MathF.Max(0f, ImGui.GetWindowWidth() - pad));
        ImGui.TextColored(tag.Length > 0 ? Rgba(255, 170, 70) : Rgba(200, 205, 215), status);
        ImGui.EndMainMenuBar();
        return height;
    }

    // The Scene dropdown's items — same set and semantics as the hand-rolled menu it replaces.
    private void SceneMenuItems()
    {
        var doc = Doc;
        bool haveDoc = doc != null;
        var sel = _guides.Selected(doc);
        if (ImGui.MenuItem("Add ground", null, _guides.Tool == GuideTool.AddGround, haveDoc)) _guides.Tool = GuideTool.AddGround;
        if (ImGui.MenuItem("Add block (drag to size)", null, _guides.Tool == GuideTool.AddBlock, haveDoc)) _guides.Tool = GuideTool.AddBlock;
        if (ImGui.MenuItem("Select guides", null, _guides.Tool == GuideTool.Select, haveDoc))
            _guides.Tool = _guides.Tool == GuideTool.Select ? GuideTool.None : GuideTool.Select;
        if (ImGui.MenuItem("Duplicate selected", null, false, sel != null)) _guides.DuplicateSelected(doc, ref _dirty);
        if (ImGui.MenuItem("Delete selected", null, false, sel != null)) _guides.DeleteSelected(doc, ref _dirty);
        if (ImGui.MenuItem(sel?.Hidden == true ? "Show selected" : "Hide selected", null, false, sel != null)) _guides.ToggleHidden(doc, ref _dirty);
        if (ImGui.MenuItem(sel?.Locked == true ? "Unlock selected" : "Lock selected", null, false, sel != null)) _guides.ToggleLocked(doc, ref _dirty);
        if (ImGui.MenuItem("Snap to tile grid", null, _guides.Snap)) _guides.Snap = !_guides.Snap;
        ImGui.Separator();
        // MAP COM TO ARC: the only relationship between a clip and an arc. It writes the
        // clip's own body_path from the arc, so the clip still owns its path afterwards.
        if (ImGui.BeginMenu("Map com to arc", haveDoc))
        {
            foreach (var (name, local) in AttachedArcs())
                if (ImGui.MenuItem(ArcLabel(name, local))) MapComToArc(name, local, stretch: false);
            ImGui.Separator();
            if (ImGui.BeginMenu("stretched to the clip"))
            {
                foreach (var (name, local) in AttachedArcs())
                    if (ImGui.MenuItem(ArcLabel(name, local))) MapComToArc(name, local, stretch: true);
                ImGui.EndMenu();
            }
            if (AttachedArcs().Count == 0) ImGui.TextDisabled("add an arc overlay first (Scene panel)");
            ImGui.EndMenu();
        }
    }

    // The arcs this clip has attached as overlays, with the namespace each resolves in —
    // what "Map com to arc" and the arc editor offer.
    private List<(string Name, bool Local)> AttachedArcs()
    {
        var arcs = new List<(string, bool)>();
        var overlays = Doc?.Scene?.Overlays;
        if (overlays != null)
            foreach (var o in overlays)
                if (o.Kind == SceneOverlayKind.Arc && o.Ref != null && !arcs.Contains((o.Ref, o.Local)))
                    arcs.Add((o.Ref, o.Local));
        return arcs;
    }

    // Write the clip's body_path from an attached arc (ClipArcMap) — the same operation
    // `probe mapcom` runs. Reports what the sparse track cost against the arc.
    private void MapComToArc(string name, bool local, bool stretch)
    {
        var doc = Doc;
        var arc = ResolveArc(name, local);
        if (doc == null || arc == null) return;
        if (!ClipArcMap.TryMap(doc, arc, stretch, out var r, out string err)) { Console.WriteLine(err); return; }
        _dirty = true;
        Console.WriteLine(ClipArcMap.Describe(doc, arc, r, stretch));
    }

    private void FrameScene()
    {
        _placement.FrameScene(Doc, _guides.Effective(Doc).Guides, _canvas.Center.ToVector2(),
                              new Vector2(_canvas.Width, _canvas.Height));
    }

    // ── clip list ───────────────────────────────────────────────────────────────────
    private void ClipsPanel(float menuH, float screenH)
    {
        ImGui.SetNextWindowPos(new NVec2(0f, menuH), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new NVec2(LeftW, screenH - menuH), ImGuiCond.Always);
        if (!ImGui.Begin("Clips", Fixed)) { ImGui.End(); return; }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##filter", "filter", ref _clipFilter, 64);
        ImGui.Separator();

        string lastType = null;
        for (int i = 0; i < _docs.Count; i++)
        {
            var d = _docs[i];
            if (_clipFilter.Length > 0
                && d.Name.IndexOf(_clipFilter, StringComparison.OrdinalIgnoreCase) < 0
                && (d.Type ?? "").IndexOf(_clipFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (d.Type != lastType) { ImGui.SeparatorText(d.Type ?? "(untyped)"); lastType = d.Type; }
            if (ImGui.Selectable($"{d.Name}##clip{i}", i == _selected)) SelectAnimation(i);
            ImGui.SameLine();
            ImGui.TextDisabled($"{d.Keyframes.Count}kf");
            if (_scrollToSelected && i == _selected) { ImGui.SetScrollHereY(0.5f); _scrollToSelected = false; }
        }
        ImGui.End();
    }

    // ── inspector ───────────────────────────────────────────────────────────────────
    private void InspectorPanel(float menuH, float screenW, float bottom)
    {
        ImGui.SetNextWindowPos(new NVec2(screenW - RightW, menuH), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new NVec2(RightW, bottom - menuH), ImGuiCond.Always);
        if (!ImGui.Begin("Clip properties", Fixed)) { ImGui.End(); return; }

        var doc = Doc;
        if (doc == null) { ImGui.TextDisabled("no clip selected"); ImGui.End(); return; }

        ImGui.TextUnformatted(doc.Name);
        ImGui.TextDisabled($"{_skeleton.Name}   {doc.Keyframes.Count} keyframes");
        ImGui.Separator();

        int type = Array.IndexOf(_typeOptions, doc.Type);
        ImGui.SetNextItemWidth(-90f);
        if (ImGui.Combo("type", ref type, _typeOptions, _typeOptions.Length) && type >= 0)
        { doc.Type = _typeOptions[type]; _dirty = true; }

        float dur = doc.Duration;
        ImGui.SetNextItemWidth(-90f);
        if (ImGui.DragFloat("duration", ref dur, 0.01f, 0.1f, 20f, "%.2f s"))
        { doc.Duration = MathF.Max(0.1f, dur); _dirty = true; }

        bool loop = doc.Loop;
        if (ImGui.Checkbox("loop", ref loop)) { doc.Loop = loop; _dirty = true; }

        var regions = Enum.GetNames<AnimRegion>();
        int region = (int)doc.Region;
        ImGui.SetNextItemWidth(-90f);
        if (ImGui.Combo("region", ref region, regions, regions.Length)) { doc.Region = (AnimRegion)region; _dirty = true; }

        ImGui.Separator();
        var m = _placement.Motion;
        string motion = m == null ? "-" : m.Source switch
        {
            MotionSource.InPlace => m.Explicit ? "in place" : "stationary (no body_path yet)",
            MotionSource.Track   => $"body_path, {BodyPathKeys(doc)} keys",
            _                    => "",
        };
        ImGui.TextWrapped($"path: {motion}");
        ImGui.TextWrapped($"scene: {doc.Scene?.Guides.Count ?? 0} guides");

        ImGui.Separator();
        ImGui.TextDisabled($"view: zoom {_placement.Zoom * 100f:0}%  —  wheel zooms at the cursor, middle-drag pans");
        ImGui.TextUnformatted($"edit mode: {_editMode}");
        bool ik = _ikMode;
        if (ImGui.Checkbox("IK drag", ref ik)) { _ikMode = ik; if (!_ikMode) _ikDrag = null; }
        if (_selectedEndpoint >= 0 && _selectedEndpoint < _skeleton.Count)
            ImGui.TextWrapped($"endpoint: {_skeleton.Bones[_selectedEndpoint].Name} (right-click a joint for its menu)");
        if (_selectedAttachment != null)
            ImGui.TextWrapped($"element: {_selectedAttachment.Effect} on {_selectedAttachment.Point} "
                            + $"[{_selectedAttachment.Start:0.00}-{_selectedAttachment.End:0.00}]"
                            + "  — drag its timeline bar (ends retime, body slides), U/I trim to playhead, Delete removes");
        if (_selectedPointId != null) ImGui.TextWrapped($"point: {_selectedPointId}  (Del removes)");

        // The warnings that used to run off the header as one long line.
        if (_guides.Hint != null) { ImGui.Separator(); ImGui.TextColored(Rgba(220, 190, 140), "guides"); ImGui.TextWrapped(_guides.Hint); }
        if (AnimationSampler.SeamMismatch(doc, out string seamBone, out float seamDelta))
        {
            ImGui.Separator();
            ImGui.TextColored(Rgba(255, 90, 70), "LOOP SEAM MISMATCH");
            ImGui.TextWrapped($"{seamBone} differs {seamDelta:0.000} rad between the first and last keyframe — "
                            + "copy the first pose onto the last (the loop pops and the cadence stalls until fixed).");
        }
        ImGui.End();
    }

    // ── scene list ──────────────────────────────────────────────────────────────────
    // A layers list over the clip's scene: authored guides (ground/blocks) you can select,
    // hide, lock and delete, then the display-only overlays. Adding a block drops one at the
    // view centre already selected — the old flow was "open the menu, arm a tool mode, drag
    // to size", which is three steps for the common case. Drag-to-size stays as a mode for
    // when the size matters up front.
    private void ScenePanel(float screenW, float top, float screenH)
    {
        ImGui.SetNextWindowPos(new NVec2(screenW - RightW, top), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new NVec2(RightW, screenH - top), ImGuiCond.Always);
        if (!ImGui.Begin("Scene", Fixed)) { ImGui.End(); return; }

        var doc = Doc;
        if (doc == null) { ImGui.TextDisabled("no clip selected"); ImGui.End(); return; }

        if (ImGui.Button("+ Block")) AddBlockAtView();
        ImGui.SameLine();
        if (ImGui.Button("+ Ground")) AddGroundAtView();
        ImGui.SameLine();
        if (ImGui.Button("+ Overlay")) ImGui.OpenPopup("##addoverlay");
        if (ImGui.BeginPopup("##addoverlay"))
        {
            // Derived heights: one of each is all that can mean anything.
            foreach (SceneOverlayKind k in Enum.GetValues<SceneOverlayKind>())
                if (SceneReferences.IsHoverLine(k)
                    && ImGui.MenuItem(SceneReferences.Name(k), null, false, !SceneGuideOps.HasOverlay(doc.Scene, k)))
                    AddOverlay(k);
            ImGui.Separator();
            // Trajectories: as many as you like, each naming a document.
            if (ImGui.BeginMenu("Reference arc"))
            {
                foreach (var name in ArcNames())
                    if (ImGui.MenuItem(name)) AddOverlay(SceneOverlayKind.Arc, name);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("New local arc"))
            {
                if (ImGui.MenuItem("empty")) NewLocalArc(seedFromPath: false);
                if (ImGui.MenuItem("traced from this clip's body path", null, false, BodyPathKeys(doc) >= 2))
                    NewLocalArc(seedFromPath: true);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Clip path"))
            {
                foreach (var d in _docs)
                    if (d != doc && ImGui.MenuItem(d.Name)) AddOverlay(SceneOverlayKind.ClipPath, d.Name);
                ImGui.EndMenu();
            }
            ImGui.EndPopup();
        }

        bool snap = _guides.Snap;
        if (ImGui.Checkbox("snap to tiles", ref snap)) _guides.Snap = snap;
        ImGui.SameLine();
        bool sizing = _guides.Tool == GuideTool.AddBlock;
        if (ImGui.Checkbox("drag-to-size", ref sizing)) _guides.Tool = sizing ? GuideTool.AddBlock : GuideTool.Select;
        ImGui.Separator();

        var scene = doc.Scene;
        if (scene == null)
        {
            ImGui.TextWrapped("no scene yet — add a ground line, a block or an overlay to start one.");
            ImGui.End();
            return;
        }

        SceneGuide dropGuide = null;
        foreach (var g in scene.Guides)
        {
            ImGui.PushID(g.Id);
            string kind = g.Kind == SceneGuideKind.Ground ? "ground" : "block";
            if (ImGui.Selectable($"{kind}  {g.Label ?? g.Id}", _guides.SelectedId == g.Id))
            { _guides.SelectedId = g.Id; _guides.Tool = GuideTool.Select; }
            ImGui.SameLine(RightW - 118f);
            if (ImGui.SmallButton(g.Hidden ? "show" : "hide")) { g.Hidden = !g.Hidden; _dirty = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton(g.Locked ? "unlock" : "lock")) { g.Locked = !g.Locked; _dirty = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) dropGuide = g;
            ImGui.PopID();
        }
        if (dropGuide != null)
        {
            if (_guides.SelectedId == dropGuide.Id) _guides.SelectedId = null;
            SceneGuideOps.Remove(scene, dropGuide);
            _dirty = true;
        }

        // THE CLIP'S PATH. One channel, always the clip's own body_path: drag the com marker
        // to author it, or map an attached arc onto it. There is no owner to choose.
        ImGui.SeparatorText("body path");
        int keys = BodyPathKeys(doc);
        if (keys == 0) ImGui.TextWrapped("none — the body is stationary. Drag the com marker, or map an arc below.");
        else ImGui.TextWrapped($"{keys} keys authored");
        var arcsHere = AttachedArcs();
        if (arcsHere.Count > 0)
        {
            if (ImGui.Button("Map com to arc")) ImGui.OpenPopup("##mapcom");
            if (ImGui.BeginPopup("##mapcom"))
            {
                foreach (var (name, local) in arcsHere)
                {
                    if (ImGui.MenuItem(ArcLabel(name, local))) MapComToArc(name, local, stretch: false);
                    if (ImGui.MenuItem($"{ArcLabel(name, local)}  (stretched)")) MapComToArc(name, local, stretch: true);
                }
                ImGui.EndPopup();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("writes this clip's body_path from the arc at every keyframe");
        }

        if (scene.Overlays != null)
        {
            ImGui.SeparatorText("overlays (display only)");
            SceneOverlay dropOverlay = null;
            foreach (var o in scene.Overlays)
            {
                ImGui.PushID(o.Id);
                string label = o.Kind == SceneOverlayKind.Arc
                    ? $"arc: {ArcLabel(o.Ref, o.Local)}"
                    : SceneReferences.IsTrajectory(o.Kind)
                        ? $"{SceneReferences.Name(o.Kind)}: {o.Ref}"
                        : SceneReferences.Name(o.Kind);
                bool missing = o.Kind == SceneOverlayKind.Arc      ? ResolveArc(o.Ref, o.Local) == null
                             : o.Kind == SceneOverlayKind.ClipPath ? ResolveClipOverlay(o.Ref) == null
                                                                   : false;
                if (missing) ImGui.TextColored(Rgba(255, 120, 90), label + "  (not found)");
                else         ImGui.TextUnformatted(label);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(SceneReferences.Describe(o.Kind));
                ImGui.SameLine(RightW - 152f);
                if (o.Kind == SceneOverlayKind.Arc && !missing && ImGui.SmallButton("edit")) OpenArcEditor(o);
                ImGui.SameLine(RightW - 118f);
                if (ImGui.SmallButton(o.Hidden ? "show" : "hide")) { o.Hidden = !o.Hidden; _dirty = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("x")) dropOverlay = o;
                ImGui.PopID();
            }
            if (dropOverlay != null) { SceneGuideOps.RemoveOverlay(scene, dropOverlay); _dirty = true; }
            ImGui.TextDisabled("hover lines come from the live movement config; trajectories from the named document");
        }
        ImGui.End();
    }

    // Scene-space point at the middle of the canvas — where a freshly added guide lands.
    private Vector2 ViewCenterScene(out ClipScene scene, out float groundY)
    {
        var (frame, gy) = _placement.GuideFrame();
        groundY = gy;
        scene = _guides.EnsureScene(Doc, ref _dirty);
        return frame.Inverse().TransformPoint(_canvas.Center.ToVector2());
    }

    private void AddBlockAtView()
    {
        if (Doc == null) return;
        Vector2 p = ViewCenterScene(out var scene, out float groundY);
        float cell = SceneGuideView.TileRig;
        var g = SceneGuideOps.AddBlock(scene, p.X - cell * 0.5f, p.Y - cell * 0.5f, cell, cell);
        if (_guides.Snap) SceneGuideOps.SnapToGrid(g, cell, groundY);
        _guides.SelectedId = g.Id;
        _guides.Tool = GuideTool.Select;
        _dirty = true;
    }

    private void AddGroundAtView()
    {
        if (Doc == null) return;
        Vector2 p = ViewCenterScene(out var scene, out float groundY);
        var g = SceneGuideOps.AddGround(scene, p.Y);
        if (_guides.Snap) SceneGuideOps.SnapToGrid(g, SceneGuideView.TileRig, groundY);
        _guides.SelectedId = g.Id;
        _guides.Tool = GuideTool.Select;
        _dirty = true;
    }

    private void AddOverlay(SceneOverlayKind kind, string reference = null)
    {
        if (Doc == null) return;
        ViewCenterScene(out var scene, out _);
        SceneGuideOps.AddOverlay(scene, kind, reference);
        _dirty = true;
    }

    // ── timeline ────────────────────────────────────────────────────────────────────
    // The track keeps its gestures: click/drag a keyframe bar to move it, click elsewhere to
    // scrub. It is drawn on the window's draw list, so bars and labels are clipped to the
    // panel instead of running into the canvas.
    private void TimelinePanel(float menuH, float screenW, float screenH)
    {
        ImGui.SetNextWindowPos(new NVec2(LeftW, screenH - BottomH), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new NVec2(MathF.Max(1f, screenW - LeftW - RightW), BottomH), ImGuiCond.Always);
        if (!ImGui.Begin("Timeline", Fixed)) { ImGui.End(); return; }

        var doc = Doc;
        if (doc == null) { ImGui.TextDisabled("no clip selected"); ImGui.End(); return; }

        if (ImGui.Button(_playing ? "Stop" : "Play")) TogglePlay();
        ImGui.SameLine();
        if (ImGui.Button("< key")) StepKeyframe(-1);
        ImGui.SameLine();
        if (ImGui.Button("key >")) StepKeyframe(+1);
        ImGui.SameLine();
        if (ImGui.Button("Sample key (K)")) SampleKeyframe();
        ImGui.SameLine();
        if (_activeKey >= 0 && ImGui.Button("Delete key")) DeleteActiveKeyframe();
        ImGui.SameLine();
        ImGui.TextDisabled($"{doc.Duration:0.00}s   t={_scrubT:0.000}");

        var avail = ImGui.GetContentRegionAvail();
        // The track is the click target for every row below the keyframe ticks, so it must
        // be at least as tall as the rows it draws (six contact lanes plus the attachment
        // rows overran a 40 px track and were unclickable).
        int rowsForHeight = ContactRowCount(doc);
        int attachForHeight = doc.Attachments?.Count ?? 0;
        float neededH = 18f + 8f + Math.Max(rowsForHeight, 1) * ContactPitch + 8f + attachForHeight * AttachPitch + 12f;
        float trackH = MathF.Max(MathF.Max(40f, neededH), avail.Y - 4f);
        ImGui.InvisibleButton("##track", new NVec2(MathF.Max(32f, avail.X), trackH));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        float x0 = min.X + 10f, x1 = max.X - 10f, y = min.Y + 18f;
        float TimeToX(float t) => x0 + MathHelper.Clamp(t, 0f, 1f) * (x1 - x0);
        float XToTime(float x) => MathHelper.Clamp((x - x0) / MathF.Max(1f, x1 - x0), 0f, 1f);

        // Interaction — the old InSlider branch, now owned by the widget.
        float mouseX = ImGui.GetIO().MousePos.X;
        float mouseY = ImGui.GetIO().MousePos.Y;

        // Row order must match the draw pass below: contact rows, then one row per attachment.
        // Counted here so a grab and its bar agree.
        var contacts    = doc.Contacts   ?? EmptyContacts;
        var attachments = doc.Attachments ?? EmptyAttachments;
        int contactRowCount = ContactRowCount(doc);

        if (ImGui.IsItemActivated() && !_playing)
        {
            // Span grabs win over keyframe bars: the rows sit below the keyframe ticks, so a
            // click down there is aimed at an annotation, never at retiming the whole pose.
            _dragAttachSpan = null; _dragContactSpan = null; _dragSpanPart = SpanPart.None; _dragSpanTail = false;
            // NEAREST lane wins. Contact lanes are ContactPitch (6 px) apart and SpanHit accepts
            // ±7 px, so a first-hit scan let the lane above swallow a click aimed at the one
            // below wherever the two bars overlap in time. A span that crosses the loop seam
            // is also hittable on its tail at the clip's head, where its END handle lives.
            float bestDy = float.MaxValue;
            for (int i = 0; i < contacts.Count; i++)
            {
                var cs = contacts[i];
                float by = ContactRowY(y, ContactRow(contacts, i));
                float dy = MathF.Abs(mouseY - by);
                if (dy >= bestDy) continue;
                bool tail = false;
                var part = SpanHit(TimeToX(cs.Start), TimeToX(MathF.Min(cs.End, 1f)), mouseX, mouseY, by, capEnd: cs.End <= 1f);
                if (part == SpanPart.None && cs.End > 1f)
                {
                    part = SpanHit(TimeToX(0f), TimeToX(cs.End - 1f), mouseX, mouseY, by, capStart: false);
                    tail = part != SpanPart.None;
                }
                if (part == SpanPart.None) continue;
                bestDy = dy;
                _dragContactSpan = cs; _dragSpanPart = part; _dragSpanTail = tail;
                _dragSpanOffset = XToTime(mouseX) + (tail ? 1f : 0f) - cs.Start;
                _selectedContact = cs; _selectedAttachment = null; _selectedPointId = null;
            }
            for (int i = 0; i < attachments.Count && _dragContactSpan == null && _dragAttachSpan == null; i++)
            {
                var a = attachments[i];
                var part = SpanHit(TimeToX(a.Start), TimeToX(a.End), mouseX, mouseY,
                                   AttachRowY(y, contactRowCount, i));
                if (part == SpanPart.None) continue;
                _dragAttachSpan = a; _dragSpanPart = part;
                _dragSpanOffset = XToTime(mouseX) - a.Start;
                _selectedAttachment = a; _selectedContact = null; _selectedPointId = null;
            }
            if (_dragAttachSpan == null && _dragContactSpan == null)
            {
                int bar = -1; float best = 8f;
                for (int i = 0; i < doc.Keyframes.Count; i++)
                {
                    float d = MathF.Abs(TimeToX(doc.Keyframes[i].Time) - mouseX);
                    if (d < best) { best = d; bar = i; }
                }
                if (bar >= 0) { _dragBar = bar; SelectKeyframe(bar); }
                else { _dragPlayhead = true; Scrub(XToTime(mouseX)); }
            }
        }
        if (ImGui.IsItemActive() && !_playing)
        {
            if (_dragContactSpan != null) DragContactSpan(_dragContactSpan, _dragSpanPart, XToTime(mouseX) + (_dragSpanTail ? 1f : 0f), _dragSpanOffset);
            else if (_dragAttachSpan != null) DragAttachSpan(_dragAttachSpan, _dragSpanPart, XToTime(mouseX), _dragSpanOffset);
            else if (_dragBar >= 0)
            {
                var kf = doc.Keyframes[_dragBar];
                kf.Time = XToTime(mouseX);
                doc.SortKeyframes();
                _dragBar = doc.Keyframes.IndexOf(kf);
                _activeKey = _dragBar;
                _scrubT = kf.Time;
                _dirty = true;
            }
            else if (_dragPlayhead) Scrub(XToTime(mouseX));
        }
        if (ImGui.IsItemDeactivated())
        { _dragBar = -1; _dragPlayhead = false; _dragAttachSpan = null; _dragContactSpan = null; _dragSpanPart = SpanPart.None; _dragSpanTail = false; }

        var dl = ImGui.GetWindowDrawList();
        dl.AddLine(new NVec2(x0, y), new NVec2(x1, y), Col(80, 85, 100), 2f);

        // Contact spans — draggable, through the same SpanHit/DrawSpan pair the attachments
        // use. One row per contact identity, so a foot's successive stances share a lane and
        // a double-support overlap is visible as two lanes running together.
        // The weight CURVE is drawn inside the bar as a filled profile: the shape that used to
        // be an invisible global feather is now the thing you can see and (next) edit.
        for (int i = 0; i < contacts.Count; i++)
        {
            var cs = contacts[i];
            float sx = TimeToX(cs.Start), ex = TimeToX(MathF.Min(cs.End, 1f));
            float by = ContactRowY(y, ContactRow(contacts, i));
            bool sel = _selectedContact == cs;
            uint col = cs.Source == ContactSource.External ? Col(240, 160, 70) : Col(70, 220, 110);
            bool wraps = cs.End > 1f;
            // A span wrapping the loop seam continues at the clip's head; its END handle is
            // there, not at the seam (and it is grabbable there — see the press above).
            DrawSpan(dl, sx, ex, by, col, sel, capEnd: !wraps);
            if (wraps) DrawSpan(dl, TimeToX(0f), TimeToX(cs.End - 1f), by, col, sel, capStart: false);
            // The weight profile, over the UNWRAPPED span (a segment straddling the seam is skipped).
            const int Profile = 24;
            float XOf(float t) => TimeToX(t > 1f ? t - 1f : t);
            for (int k = 0; k < Profile; k++)
            {
                float u0 = k / (float)Profile, u1 = (k + 1) / (float)Profile;
                float t0 = cs.Start + (cs.End - cs.Start) * u0, t1 = cs.Start + (cs.End - cs.Start) * u1;
                if ((t0 > 1f) != (t1 > 1f)) continue;
                float w0 = AnimCurve.ValueAt(cs.EffectiveWeight, u0);
                float w1 = AnimCurve.ValueAt(cs.EffectiveWeight, u1);
                dl.AddLine(new NVec2(XOf(t0), by - 6f * w0), new NVec2(XOf(t1), by - 6f * w1), col, 1f);
            }
            if (sel) dl.AddText(new NVec2(sx + 4f, by - 20f), Col(255, 255, 255),
                                $"{cs.Point} [{cs.Start:0.00}-{cs.End:0.00}] {cs.Source}");
        }

        // Attachment spans — one draggable row each. The bar IS the editor: drag an endpoint
        // to retime that end, drag the body to slide the whole window. A knife's trail window
        // (TrailStart/TrailEnd, a second lifetime nested in this one) draws as a thin inner
        // line so it is visible rather than hidden behind a keystroke.
        for (int i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i];
            float sx = TimeToX(a.Start), ex = TimeToX(a.End), by = AttachRowY(y, contactRowCount, i);
            bool sel = _selectedAttachment == a;
            DrawSpan(dl, sx, ex, by, Col(180, 230, 240), sel);
            if (a.TrailStart.HasValue || a.TrailEnd.HasValue)
                dl.AddLine(new NVec2(TimeToX(a.TrailStart ?? a.Start), by + 4f),
                           new NVec2(TimeToX(a.TrailEnd ?? a.End), by + 4f), Col(255, 210, 120), 1.5f);
            dl.AddText(new NVec2(sx + 4f, by - 13f), sel ? Col(255, 255, 255) : Col(180, 230, 240),
                       $"{a.Effect} on {a.Point}  [{a.Start:0.00}-{a.End:0.00}]");
        }

        // Keyframe bars + playhead.
        for (int i = 0; i < doc.Keyframes.Count; i++)
        {
            float x = TimeToX(doc.Keyframes[i].Time);
            uint c = i == _activeKey ? Col(255, 255, 255) : Col(120, 200, 255);
            dl.AddLine(new NVec2(x, y - 12f), new NVec2(x, y + 6f), c, i == _activeKey ? 3f : 2f);
        }
        float px = TimeToX(_scrubT);
        float contentBottom = MathF.Max(y + 42f, AttachRowY(y, contactRowCount, attachments.Count - 1) + 8f);
        dl.AddLine(new NVec2(px, y - 16f), new NVec2(px, MathF.Min(max.Y - 4f, contentBottom)),
                   Col(255, 180, 60), 1.5f);

        // The contact legend, as text the panel clips — it used to be painted past the track's
        // right edge, over whatever the canvas had there.
        if (contacts.Count > 0)
        {
            var ids = new List<string>();
            foreach (var cs in contacts) if (!ids.Contains(cs.Point ?? "?")) ids.Add(cs.Point ?? "?");
            ImGui.SetCursorScreenPos(new NVec2(min.X + 4f, MathF.Min(max.Y - 18f, contentBottom + 2f)));
            ImGui.TextDisabled("contacts: " + string.Join(", ", ids.GetRange(0, Math.Min(ids.Count, 6))));
        }
        ImGui.End();
    }

    // ── timeline span bars ──────────────────────────────────────────────────────────
    // Row geometry and the hit-test/draw pair every annotation row shares. Deliberately
    // typed in plain floats: contacts are the next client (they move off per-keyframe
    // labels onto explicit spans), and they must land on THIS gesture rather than a
    // second one that drifts from it.
    private AnimAttachment _pendingAttachmentRemoval;   // menu click; applied once the popup is closed
    private static readonly List<ContactSpan> EmptyContacts = new();

    private const float ContactPitch = 6f;
    private const float AttachPitch  = 18f;   // bar + its label above it
    private static readonly List<AnimAttachment> EmptyAttachments = new();

    // Distinct contact identities in the clip, capped at the 6 rows the read-out draws —
    // the attachment rows start below them, so both passes must agree on the count.
    private static int ContactRowCount(AnimationDocument doc)
    {
        var seen = new List<string>();
        if (doc.Contacts != null)
            foreach (var c in doc.Contacts) if (!seen.Contains(c.Point ?? "?")) seen.Add(c.Point ?? "?");
        return Math.Min(seen.Count, 6);
    }

    // The lane a span draws in: one per contact identity, in first-appearance order, capped at
    // the six the panel has room for.
    private static int ContactRow(List<ContactSpan> spans, int i)
    {
        var seen = new List<string>();
        for (int k = 0; k <= i; k++) { string id = spans[k].Point ?? "?"; if (!seen.Contains(id)) seen.Add(id); }
        return Math.Min(seen.Count - 1, 5);
    }

    private static float ContactRowY(float y, int row) => y + 8f + row * ContactPitch;
    private static float AttachRowY(float y, int contactRows, int row)
        => y + 8f + Math.Max(contactRows, 1) * ContactPitch + 8f + row * AttachPitch;

    private enum SpanPart { None, Start, End, Body }

    // Which part of the bar at `by` spanning [sx,ex] the pointer is on. Endpoint handles win
    // over the body so a span squeezed to a few pixels stays resizable instead of only movable.
    // `capStart`/`capEnd` say which ends carry a handle: a seam-crossing span's pre-seam
    // segment has no end handle (the seam is not its end) and its tail has no start handle.
    private static SpanPart SpanHit(float sx, float ex, float mx, float my, float by,
                                    bool capStart = true, bool capEnd = true)
    {
        if (MathF.Abs(my - by) > 7f) return SpanPart.None;
        if (capStart && MathF.Abs(mx - sx) <= 5f) return SpanPart.Start;
        if (capEnd   && MathF.Abs(mx - ex) <= 5f) return SpanPart.End;
        return mx > sx && mx < ex ? SpanPart.Body : SpanPart.None;
    }

    // Apply one frame of a span drag. `t` is the pointer's clip time; `grabOffset` is where
    // inside the span the body drag started, so a slide doesn't snap the span's head to the
    // cursor. MinSpan keeps a window from inverting or collapsing to an unhittable sliver.
    private const float MinSpan = 0.01f;

    // The span arithmetic every draggable row shares: clamp, never invert, preserve width on a
    // body slide. Start always stays inside [0,1) — it is a phase. `allowWrap` lets the END run
    // past 1, which is how a contact stance is made to cross the loop seam; an attachment
    // window has no seam to cross and is capped at the clip's end.
    private const float MaxStart = 1f - 1e-4f;

    private static (float s, float e) SpanDrag(float start, float end, SpanPart part,
                                               float t, float grabOffset, bool allowWrap)
    {
        float s = start, e = end, w = end - start;
        switch (part)
        {
            case SpanPart.Start:
                s = MathHelper.Clamp(t, 0f, MathF.Min(e - MinSpan, MaxStart));
                break;
            case SpanPart.End:
                // A whole cycle is the most a span can cover, wrapping or not.
                e = MathHelper.Clamp(t, s + MinSpan, allowWrap ? s + 1f : 1f);
                break;
            case SpanPart.Body:
                s = MathHelper.Clamp(t - grabOffset, 0f, allowWrap ? MaxStart : 1f - w);
                e = s + w;
                break;
        }
        return (s, e);
    }

    private void DragContactSpan(ContactSpan c, SpanPart part, float t, float grabOffset)
    {
        var (s, e) = SpanDrag(c.Start, c.End, part, t, grabOffset, allowWrap: true);
        if (s == c.Start && e == c.End) return;
        c.Start = s; c.End = e;
        _dirty = true;
    }

    private void DragAttachSpan(AnimAttachment a, SpanPart part, float t, float grabOffset)
    {
        var (s, e) = SpanDrag(a.Start, a.End, part, t, grabOffset, allowWrap: false);
        if (s == a.Start && e == a.End) return;

        // The trail is a SECOND lifetime nested in this one. A body slide carries it along;
        // an endpoint drag clamps it back inside. Either way it can never end up describing a
        // window outside the blade's, which EmitsTrail would then read as a trail with no blade.
        if (part == SpanPart.Body)
        {
            float d = s - a.Start;
            if (a.TrailStart.HasValue) a.TrailStart += d;
            if (a.TrailEnd.HasValue)   a.TrailEnd   += d;
        }
        a.Start = s; a.End = e;
        if (a.TrailStart.HasValue) a.TrailStart = MathHelper.Clamp(a.TrailStart.Value, s, e);
        if (a.TrailEnd.HasValue)   a.TrailEnd   = MathHelper.Clamp(a.TrailEnd.Value, a.TrailStart ?? s, e);
        _dirty = true;
    }

    private static void DrawSpan(ImDrawListPtr dl, float sx, float ex, float by, uint col, bool selected,
                                 bool capStart = true, bool capEnd = true)
    {
        dl.AddLine(new NVec2(sx, by), new NVec2(ex, by), col, selected ? 5f : 3f);
        // Endpoint caps — the affordance that says "this end is draggable".
        float h = selected ? 6f : 4f;
        if (capStart) dl.AddRectFilled(new NVec2(sx - 2f, by - h), new NVec2(sx + 2f, by + h), col);
        if (capEnd)   dl.AddRectFilled(new NVec2(ex - 2f, by - h), new NVec2(ex + 2f, by + h), col);
    }


    // ── contact weight curve editor ─────────────────────────────────────────────────
    // Opens on the selected contact span. The curve is authored on the SPAN'S normalized
    // domain, so this view's x axis is u ∈ [0,1] — a fraction of the span, not clip phase.
    // Retiming the span therefore stretches what is drawn here without changing it.
    //
    // Interaction mirrors the arc editor: drag a key, drag its tangent handle, A adds and
    // Delete removes. The two ends are pinned horizontally (they ARE the span's ends) and only
    // move vertically.
    private int  _curveKey = -1;      // selected key index
    private bool _dragCurveKey, _dragCurveTan;

    private const float TanHandlePx = 30f;   // how far along +u the tangent handle sits

    private void ContactCurveWindow()
    {
        var c = _selectedContact;
        if (c == null || Doc?.Contacts == null || !Doc.Contacts.Contains(c))
        { _curveKey = -1; _dragCurveKey = _dragCurveTan = false; return; }

        ImGui.SetNextWindowPos(new NVec2(LeftW + 24f, 90f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new NVec2(360f, 0f), ImGuiCond.FirstUseEver);
        bool open = true;
        if (!ImGui.Begin($"Weight curve — {c.Point}", ref open,
                         ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        { ImGui.End(); if (!open) _selectedContact = null; return; }

        ImGui.TextDisabled($"span [{c.Start:0.000} – {c.End:0.000}]   {c.Source}"
                         + (c.Weight == null ? "   (default ramp — editing makes a copy)" : ""));

        var dl = ImGui.GetWindowDrawList();
        ImGui.InvisibleButton("##curve", new NVec2(330f, 130f));
        var lo = ImGui.GetItemRectMin();
        var hi = ImGui.GetItemRectMax();
        float x0 = lo.X + 10f, x1 = hi.X - 10f;
        float yOne = lo.Y + 14f, yZero = hi.Y - 14f;          // v = 1 and v = 0 lines
        float X(float u) => x0 + MathHelper.Clamp(u, 0f, 1f) * (x1 - x0);
        float Y(float v) => yZero - MathHelper.Clamp(v, -0.2f, 1.2f) * (yZero - yOne);
        float U(float px) => MathHelper.Clamp((px - x0) / MathF.Max(1f, x1 - x0), 0f, 1f);
        float V(float py) => MathHelper.Clamp((yZero - py) / MathF.Max(1f, yZero - yOne), 0f, 1f);

        dl.AddRectFilled(lo, hi, Col(26, 28, 34));
        dl.AddLine(new NVec2(x0, yZero), new NVec2(x1, yZero), Col(70, 75, 88), 1f);
        dl.AddLine(new NVec2(x0, yOne),  new NVec2(x1, yOne),  Col(48, 52, 62), 1f);
        dl.AddText(new NVec2(x0 - 8f, yOne - 7f),  Col(110, 116, 130), "1");
        dl.AddText(new NVec2(x0 - 8f, yZero - 7f), Col(110, 116, 130), "0");

        var curve = c.EffectiveWeight;
        uint line = c.Weight == null ? Col(120, 130, 150) : Col(120, 210, 255);
        const int N = 80;
        for (int i = 0; i < N; i++)
        {
            float u0 = i / (float)N, u1 = (i + 1) / (float)N;
            dl.AddLine(new NVec2(X(u0), Y(AnimCurve.ValueAt(curve, u0))),
                       new NVec2(X(u1), Y(AnimCurve.ValueAt(curve, u1))), line, 2f);
        }

        // The playhead, when it is inside this span — the reason to look at the curve at all.
        if (c.Covers(_scrubT, out float pu))
        {
            dl.AddLine(new NVec2(X(pu), yOne - 6f), new NVec2(X(pu), yZero + 6f), Col(255, 180, 60), 1.5f);
            dl.AddText(new NVec2(X(pu) + 4f, yOne - 6f), Col(255, 180, 60), $"{c.WeightAt(_scrubT):0.00}");
        }

        // Keys + tangent handles.
        var ks = curve.Keys;
        for (int i = 0; i < ks.Count; i++)
        {
            float kx = X(ks[i].T), ky = Y(ks[i].V);
            bool sel = i == _curveKey;
            if (sel)
            {
                float tan = TangentOf(ks, i);
                float hx = kx + TanHandlePx;
                float hy = ky - tan * (TanHandlePx / MathF.Max(1f, x1 - x0)) * (yZero - yOne);
                dl.AddLine(new NVec2(kx, ky), new NVec2(hx, hy), Col(255, 210, 120), 1f);
                dl.AddCircleFilled(new NVec2(hx, hy), 3.5f, Col(255, 210, 120));
            }
            dl.AddCircleFilled(new NVec2(kx, ky), sel ? 5f : 3.5f,
                               ks[i].Tan.HasValue ? Col(255, 210, 120) : Col(150, 220, 255));
        }

        // Dragging. A press picks the tangent handle first (it sits outside the key's own
        // radius), then a key; a press on empty canvas just selects nothing.
        if (ImGui.IsItemActivated())
        {
            var mp = ImGui.GetIO().MousePos;
            _dragCurveKey = _dragCurveTan = false;
            if (_curveKey >= 0 && _curveKey < ks.Count)
            {
                float kx = X(ks[_curveKey].T), ky = Y(ks[_curveKey].V);
                float tan = TangentOf(ks, _curveKey);
                float hx = kx + TanHandlePx;
                float hy = ky - tan * (TanHandlePx / MathF.Max(1f, x1 - x0)) * (yZero - yOne);
                if (NVec2.Distance(mp, new NVec2(hx, hy)) <= 7f) _dragCurveTan = true;
            }
            if (!_dragCurveTan)
            {
                int best = -1; float bestD = 9f;
                for (int i = 0; i < ks.Count; i++)
                {
                    float d = NVec2.Distance(mp, new NVec2(X(ks[i].T), Y(ks[i].V)));
                    if (d < bestD) { bestD = d; best = i; }
                }
                _curveKey = best;
                _dragCurveKey = best >= 0;
            }
        }
        if (ImGui.IsItemActive() && (_dragCurveKey || _dragCurveTan) && _curveKey >= 0)
        {
            var mp = ImGui.GetIO().MousePos;
            var w = c.EnsureWeight();          // first edit forks the shared default
            ks = w.Keys;
            if (_curveKey < ks.Count)
            {
                var k = ks[_curveKey];
                if (_dragCurveTan)
                {
                    float dxPix = MathF.Max(6f, mp.X - X(k.T));
                    float dyPix = mp.Y - Y(k.V);
                    k.Tan = (-dyPix / MathF.Max(1f, yZero - yOne)) / (dxPix / MathF.Max(1f, x1 - x0));
                }
                else
                {
                    k.V = V(mp.Y);
                    // The ends ARE the span's ends — they move vertically only. Interior keys
                    // stay strictly between their neighbours so the domain never inverts.
                    if (_curveKey > 0 && _curveKey < ks.Count - 1)
                        k.T = MathHelper.Clamp(U(mp.X), ks[_curveKey - 1].T + 1e-3f, ks[_curveKey + 1].T - 1e-3f);
                }
                _dirty = true;
            }
        }
        if (ImGui.IsItemDeactivated()) { _dragCurveKey = _dragCurveTan = false; }

        // Buttons.
        if (ImGui.Button("Add key"))
        {
            var w = c.EnsureWeight();
            float u = c.Covers(_scrubT, out float at) ? at : 0.5f;
            int idx = w.InsertPreservingShape(u);
            _curveKey = idx; _dirty = true;
        }
        ImGui.SameLine();
        bool canDelete = _curveKey > 0 && c.Weight != null && _curveKey < c.Weight.Keys.Count - 1;
        if (!canDelete) ImGui.BeginDisabled();
        if (ImGui.Button("Delete key")) { c.Weight.Keys.RemoveAt(_curveKey); _curveKey = -1; _dirty = true; }
        if (!canDelete) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Auto tangent") && _curveKey >= 0 && c.Weight != null && _curveKey < c.Weight.Keys.Count)
        { c.Weight.Keys[_curveKey].Tan = null; _dirty = true; }
        ImGui.TextDisabled("ends are pinned in time — they are the span's ends. amber = authored tangent");

        if (ImGui.Button("Reset to ramp")) { c.Weight = null; _curveKey = -1; _dirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("Flat 1.0")) { c.Weight = AnimCurve.Constant(1f); _curveKey = -1; _dirty = true; }

        if (_curveKey >= 0 && _curveKey < curve.Keys.Count)
        {
            var k = curve.Keys[_curveKey];
            ImGui.TextDisabled($"key {_curveKey}:  u={k.T:0.000}  v={k.V:0.000}  "
                             + (k.Tan.HasValue ? $"tan={k.Tan.Value:0.00}" : "tan=auto"));
        }

        ImGui.End();
        if (!open) _selectedContact = null;
    }

    // The tangent the curve will actually use at key i — authored, else the Catmull-Rom secant
    // the sampler derives. Mirrored here so the handle shows the real slope, not a placeholder.
    private static float TangentOf(List<AnimCurveKey> ks, int i)
    {
        if (ks[i].Tan.HasValue) return ks[i].Tan.Value;
        int a = Math.Max(0, i - 1), b = Math.Min(ks.Count - 1, i + 1);
        float dt = ks[b].T - ks[a].T;
        return dt <= 1e-9f ? 0f : (ks[b].V - ks[a].V) / dt;
    }


    // ── endpoint menu ───────────────────────────────────────────────────────────────
    // The "v" affordance beside the selected endpoint, and the menu itself. Same items as
    // the hand-rolled popup; ImGui keeps it on screen and above the canvas.
    private void EndpointPopup()
    {
        if (_selectedEndpoint < 0 || _selectedEndpoint >= _skeleton.Count) return;
        ImGui.SetNextWindowPos(new NVec2(_endpointScreen.X + 8f, _endpointScreen.Y - 22f), ImGuiCond.Always);
        if (ImGui.Begin("##endpoint-affordance", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize
                                               | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                                               | ImGuiWindowFlags.NoNav))
        {
            if (ImGui.SmallButton("v")) _openEndpointPopup = true;
            if (_openEndpointPopup) { ImGui.OpenPopup("##endpoint-menu"); _openEndpointPopup = false; }
            if (ImGui.BeginPopup("##endpoint-menu")) { EndpointMenuItems(); ImGui.EndPopup(); }
        }
        ImGui.End();
        if (_pendingAttachmentRemoval != null)
        { RemoveAttachment(_pendingAttachmentRemoval); _pendingAttachmentRemoval = null; }
    }

    // THE ENDPOINT MENU. Two kinds of thing can be added at an endpoint, and the menu is built
    // around that rather than around the order features were written:
    //
    //   ELEMENT — an add-on with its own frame hung off this endpoint (a knife, an effect).
    //             Has a lifetime; drawn by the renderer.
    //   CONTACT — labeled spline data on this endpoint: an interval plus a weight curve.
    //             Has a lifetime; read by the solver, never drawn.
    //
    // Presets (Knife; No slip / Planned support / External pin) are the SUBMENU of their
    // category, not siblings of it — "knife" is one element and "no slip" is one contact
    // source, and flattening them made a nine-item list with no visible structure.
    //
    // The NamedPoint is neither: it is the anchor both hang off, so it lives in the header.
    // "Add contact point" used to sit among the verbs, but ApplyContact already names the
    // endpoint on demand — clicking it first was a ritual that changed nothing.
    private void EndpointMenuItems()
    {
        int b = _selectedEndpoint;
        var doc = Doc;
        if (doc == null || b < 0 || b >= _skeleton.Count) return;
        string bone = _skeleton.Bones[b].Name;
        var pt = PointFor(b);

        // ── identity ────────────────────────────────────────────────────────────────
        ImGui.TextDisabled($"{bone} end" + (pt != null ? $"  [{pt.Id}]" : "  (unnamed)"));
        if (ImGui.MenuItem(pt != null ? "Rename this endpoint..." : "Name this endpoint..."))
        { _pendingBoneParent = b; _naming = NameTarget.Point; _nameBuffer = pt?.Id ?? ""; }

        // Shared joint: a child's Start is its parent's End, so offer the coincident targets.
        var world = _pose.ComputeWorld(_root);
        var overlapping = new List<int>();
        for (int i = 0; i < world.Length && i < _skeleton.Count; i++)
            if (i != b && Vector2.DistanceSquared(world[i].Translation, world[b].Translation) < PickR * PickR) overlapping.Add(i);
        if (overlapping.Count > 0 && ImGui.MenuItem($"Target: cycle ({_skeleton.Bones[overlapping[0]].Name} ...)"))
            _selectedEndpoint = overlapping[0];

        ImGui.Separator();

        // ── add an element ──────────────────────────────────────────────────────────
        if (ImGui.BeginMenu("Add element"))
        {
            if (ImGui.MenuItem("Knife")) AddKnife(b);
            if (ImGui.MenuItem("Custom..."))
            {
                _effectBone = bone;
                _selectedAttachment = doc.Attachments?.Find(a => EditorBoneOf(a.Point) == b);
                _naming = NameTarget.Effect;
                _nameBuffer = _selectedAttachment?.Effect ?? "";
            }
            ImGui.Separator();
            ImGui.TextDisabled("an add-on with its own frame, drawn over a window of the clip");
            ImGui.EndMenu();
        }

        // ── add a contact ───────────────────────────────────────────────────────────
        // These act AT THE PLAYHEAD: setting a source on a span that already covers it retypes
        // that span in place; otherwise a new one is authored over NewSpanRange and dragged.
        int here = SpanAtPlayhead(b);
        ContactSource? cur = here >= 0 ? doc.Contacts[here].Source : null;
        if (ImGui.BeginMenu("Add contact"))
        {
            if (ImGui.MenuItem("No slip", null, cur == ContactSource.SelfPlant)) ApplyContact(b, ContactSource.SelfPlant);
            if (ImGui.MenuItem("External pin", null, cur == ContactSource.External)) ApplyContact(b, ContactSource.External);
            ImGui.Separator();
            if (ImGui.MenuItem("Clear the one at the playhead", null, false, cur != null)) ApplyContact(b, null);
            ImGui.Separator();
            if (ImGui.MenuItem("New span: playhead -> next key", null, !_contactWholeClip)) _contactWholeClip = false;
            if (ImGui.MenuItem("New span: whole clip", null, _contactWholeClip)) _contactWholeClip = true;
            ImGui.TextDisabled("a span's ends are draggable on the timeline afterwards");
            ImGui.EndMenu();
        }

        // ── what is already here ────────────────────────────────────────────────────
        // Each existing item gets its own submenu with the verbs spelled out. Selecting aims
        // U/I and Delete at it; a contact's row also opens its weight-curve editor.
        var items = new List<(string label, Action select, Action remove)>();
        if (doc.Attachments != null)
            foreach (var a in doc.Attachments)
            {
                int ab = EditorBoneOf(a.Point);
                if (ab < 0 || (ab != b && _skeleton.Bones[ab].Parent != b)) continue;
                var att = a;
                items.Add(($"element: {a.Effect} [{a.Start:0.00}-{a.End:0.00}]",
                           () => { _selectedAttachment = att; _selectedContact = null; _selectedPointId = null; },
                           () => { _pendingAttachmentRemoval = att; }));
            }
        if (doc.Contacts != null)
            foreach (var cs in doc.Contacts)
            {
                if (EditorBoneOf(cs.Point) != b) continue;
                var span = cs;
                items.Add(($"contact: {cs.Source} [{cs.Start:0.00}-{cs.End:0.00}]",
                           () => { _selectedContact = span; _selectedAttachment = null; _selectedPointId = null; },
                           () => { doc.Contacts.Remove(span);
                                   if (doc.Contacts.Count == 0) doc.Contacts = null;
                                   if (_selectedContact == span) _selectedContact = null;
                                   _dirty = true; }));
            }
        if (doc.Points != null)
            foreach (var p in doc.Points)
            {
                if (p.Bone != bone) continue;
                var pp = p;
                items.Add(($"point: {p.Id}",
                           () => { _selectedPointId = pp.Id; _selectedAttachment = null; _selectedContact = null; },
                           () => { _selectedPointId = pp.Id; RemoveSelectedPoint(); }));
            }

        if (items.Count == 0) return;
        ImGui.Separator();
        ImGui.TextDisabled("on this endpoint");
        foreach (var (label, select, remove) in items)
            if (ImGui.BeginMenu(label))
            {
                if (ImGui.MenuItem("Select")) select();
                if (ImGui.MenuItem("Remove")) { remove(); ImGui.CloseCurrentPopup(); }
                ImGui.EndMenu();
            }
    }

    // The bone an endpoint-menu query resolves a point to, or -1. Deliberately TOLERANT where
    // EndpointResolver.BoneOf throws: the solver must refuse data it cannot honor, but an
    // editor that throws while painting a menu cannot be used to FIX that data.
    private int EditorBoneOf(string point)
        => EndpointResolver.TryResolvePoint(_skeleton, Doc, point, out var rp) && rp.IsExactTip ? rp.Bone : -1;

    // ── arc editor ──────────────────────────────────────────────────────────────────
    // A floating panel over the canvas, paired with the draggable keys and tangent handles
    // DemoGame draws. Everything edits a WORKING COPY (ArcEditSession) — the clip only changes
    // on Save, and the shared ReferenceClips/ file is never written from here.
    private void ArcEditorWindow()
    {
        var s = _arcEdit;
        if (s == null) return;
        var arc = s.Working;

        ImGui.SetNextWindowPos(new NVec2(LeftW + 24f, 90f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new NVec2(330f, 0f), ImGuiCond.FirstUseEver);
        bool open = true;
        if (!ImGui.Begin("Arc editor", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        { ImGui.End(); if (!open) CloseArcEditor(save: false); return; }

        string name = arc.Name ?? "";
        ImGui.SetNextItemWidth(-70f);
        if (ImGui.InputText("name", ref name, 48)) { arc.Name = name; s.Dirty = true; }
        ImGui.TextDisabled(s.StoredLocal != null ? "clip-local arc"
                         : s.ForkedFrom != null  ? $"new clip-local copy of the shared '{s.ForkedFrom}'"
                                                 : "new clip-local arc");

        float dur = arc.Duration;
        ImGui.SetNextItemWidth(-70f);
        if (ImGui.DragFloat("duration", ref dur, 0.01f, 0.05f, 5f, "%.2f s")) { ArcEditOps.SetDuration(arc, dur); s.Dirty = true; }

        // Anchors are a FRAMING choice in this view (they pin to the scene origin, so there is
        // nothing to drag): the retarget and the scene mapping normalize by their span.
        var entry = new NVec2(arc.EntryX, arc.EntryY);
        var gate  = new NVec2(arc.GateX,  arc.GateY);
        ImGui.SetNextItemWidth(-70f);
        if (ImGui.DragFloat2("entry px", ref entry, 0.25f)) { arc.EntryX = entry.X; arc.EntryY = entry.Y; s.Dirty = true; }
        ImGui.SetNextItemWidth(-70f);
        if (ImGui.DragFloat2("gate px", ref gate, 0.25f)) { arc.GateX = gate.X; arc.GateY = gate.Y; s.Dirty = true; }
        ImGui.TextDisabled("anchors re-frame the arc — the curve rescales, the keys do not move");

        ImGui.Separator();
        ImGui.TextUnformatted($"{arc.Keys.Count} keys   (drag on the canvas; A adds, Del removes)");
        for (int i = 0; i < arc.Keys.Count; i++)
        {
            var k = arc.Keys[i];
            if (ImGui.Selectable($"{i}: ({k.X:0.0}, {k.Y:0.0})  t={k.T:0.00}##k{i}", s.Selected == i)) s.Selected = i;
        }
        if (ImGui.Button("Add key") && ArcEditOps.AddKeyNear(arc, arc.Eval(0.5f), out int added))
        { s.Selected = added; s.Dirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("Delete key") && ArcEditOps.DeleteKey(arc, s.Selected)) { s.Selected = -1; s.Dirty = true; }

        ImGui.Separator();
        bool mapOnSave = s.MapOnSave;
        if (ImGui.Checkbox("map com to this arc on save", ref mapOnSave)) s.MapOnSave = mapOnSave;

        if (ImGui.Button("Save to clip")) CloseArcEditor(save: true);
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) CloseArcEditor(save: false);
        ImGui.SameLine();
        if (ImGui.Button("Promote to shared")) PromoteArcToShared(arc);
        ImGui.TextDisabled("saving writes into THIS clip; the shared ReferenceClips file is untouched");
        ImGui.End();
        if (!open) CloseArcEditor(save: false);
    }

    // Edit an attached arc. A SHARED arc forks: the session edits a copy and only Save puts it
    // in the clip, so cancelling leaves the clip (and the shared file) exactly as they were.
    private void OpenArcEditor(SceneOverlay o)
    {
        var doc = Doc;
        if (doc == null || o?.Ref == null) return;
        var src = ResolveArc(o.Ref, o.Local);
        if (src == null) return;
        if (o.Local)
        {
            _arcEdit = new ArcEditSession(src.Clone(), src, o, src.FromShared);
        }
        else
        {
            var copy = src.Clone();
            copy.FromShared = src.Name;
            copy.Name = ClipArcs.UniqueName(doc, src.Name);
            _arcEdit = new ArcEditSession(copy, null, o, src.Name);
        }
        _arcEdit.Selected = 0;
    }

    private void NewLocalArc(bool seedFromPath)
    {
        var doc = Doc;
        if (doc == null) return;
        // Built detached, like a fork: nothing lands in the clip until Save.
        var probe = ClipArcs.NewLocal(doc, "arc", seedFromPath);
        if (probe == null) return;
        var working = probe.Clone();
        ClipArcs.Remove(doc, probe);
        _arcEdit = new ArcEditSession(working, null, null, null) { Selected = 0 };
    }

    private void CloseArcEditor(bool save)
    {
        var s = _arcEdit;
        var doc = Doc;
        _arcEdit = null;
        if (!save || s == null || doc == null) return;

        var stored = s.StoredLocal;
        if (stored == null) stored = ClipArcs.Add(doc, s.Working.Clone());
        else
        {
            // Copy the working values back into the arc the clip already holds, so anything
            // referencing it by identity keeps pointing at the same object.
            var w = s.Working;
            stored.Name = ClipArcs.FindLocal(doc, w.Name) is { } other && !ReferenceEquals(other, stored)
                        ? ClipArcs.UniqueName(doc, w.Name) : w.Name;
            stored.Duration = w.Duration;
            stored.EntryX = w.EntryX; stored.EntryY = w.EntryY;
            stored.GateX = w.GateX;   stored.GateY = w.GateY;
            stored.FromShared = w.FromShared;
            stored.Keys.Clear();
            foreach (var k in w.Keys) stored.Keys.Add(new HermiteClipKey { T = k.T, X = k.X, Y = k.Y, TX = k.TX, TY = k.TY });
        }

        // Point the overlay at the clip-local arc (a fork flips its row from shared to local).
        if (s.Overlay != null) { s.Overlay.Ref = stored.Name; s.Overlay.Local = true; }
        else
        {
            var scene = _guides.EnsureScene(doc, ref _dirty);
            if (scene != null) SceneGuideOps.AddOverlay(scene, SceneOverlayKind.Arc, stored.Name, local: true);
        }
        _dirty = true;
        Console.WriteLine($"{doc.Name}: saved clip-local arc '{stored.Name}'"
                        + (s.ForkedFrom != null ? $" (forked from shared '{s.ForkedFrom}')" : ""));
        if (s.MapOnSave) MapComToArc(stored.Name, local: true, stretch: false);
    }

    // The one way an arc leaves a clip: a COPY written to ReferenceClips/<name>.json. It does
    // not link the two — the clip keeps editing its own.
    private void PromoteArcToShared(HermiteClipDocument arc)
    {
        string path = RefArcPath(arc.Name);
        if (path == null) return;
        try
        {
            var copy = arc.Clone();
            copy.FromShared = null;
            copy.Save(path);
            _overlayArcs.Remove(arc.Name);   // so the shared pool re-reads it
            Console.WriteLine($"wrote shared arc {path} (a copy — this clip keeps its own)");
        }
        catch (System.Exception e) { Console.WriteLine($"could not write {path}: {e.Message}"); }
    }

    private static string ArcLabel(string name, bool local) => local ? $"{name} (local)" : name;

    // ── name modal ──────────────────────────────────────────────────────────────────
    // Replaces the polled text-input overlay: a real modal, so it owns the keyboard (the
    // editor's single-letter shortcuts are gated on WantCaptureKeyboard) and Enter/Escape
    // are the field's own.
    private void NameModal()
    {
        if (_naming == NameTarget.None) return;
        if (!ImGui.IsPopupOpen("##name")) { ImGui.OpenPopup("##name"); _nameFocus = true; }
        var center = ImGui.GetIO().DisplaySize * 0.5f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Always, new NVec2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("##name")) return;

        string what = _naming == NameTarget.Effect ? $"effect on {_effectBone}"
                    : _naming == NameTarget.Point  ? "named marker"
                    : _naming == NameTarget.Bone   ? (_pendingBoneBase ? "base-rig bone" : "clip bone")
                    : _pendingAddition?.Kind == AnimAdditionKind.Vector ? "vector" : "point";
        ImGui.TextUnformatted($"name {what}:");
        if (_nameFocus) { ImGui.SetKeyboardFocusHere(); _nameFocus = false; }
        ImGui.SetNextItemWidth(320f);
        bool entered = ImGui.InputText("##namefield", ref _nameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        if (entered || ImGui.Button("OK")) { CommitName(); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) { CancelName(); ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    // ── help ────────────────────────────────────────────────────────────────────────
    private void HelpWindow(float menuH)
    {
        if (!_showHelp) return;
        ImGui.SetNextWindowPos(new NVec2(LeftW + 30f, menuH + 30f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new NVec2(620f, 420f), ImGuiCond.FirstUseEver);
        bool open = _showHelp;
        if (ImGui.Begin("Controls", ref open, ImGuiWindowFlags.NoSavedSettings))
            foreach (var (group, keys) in HelpRows)
            {
                ImGui.TextColored(Rgba(255, 200, 120), group);
                ImGui.SameLine(96f);
                ImGui.TextWrapped(keys);
                ImGui.Separator();
            }
        ImGui.End();
        _showHelp = open;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────
    private const ImGuiWindowFlags Fixed = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                                         | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus;

    // How many keyframes author a body_path point — the clip's path, in one number.
    private static int BodyPathKeys(AnimationDocument doc)
    {
        int n = 0;
        if (doc?.Keyframes == null) return 0;
        foreach (var kf in doc.Keyframes)
            if (kf.Additions != null)
                foreach (var a in kf.Additions)
                    if (a.Kind == AnimAdditionKind.Point && a.Name == BodyPath.ChannelName && a.Parent == null) { n++; break; }
        return n;
    }

    private static uint Col(int r, int g, int b, int a = 255)
        => (uint)((a & 255) << 24 | (b & 255) << 16 | (g & 255) << 8 | (r & 255));

    private static System.Numerics.Vector4 Rgba(int r, int g, int b, int a = 255)
        => new(r / 255f, g / 255f, b / 255f, a / 255f);
}
