using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace MTile;

// SCENE GUIDES and MOTION INTENT (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md, "Proposed document
// additions"; workplan chunk 3). A clip may carry an explicit Scene — fixed reference geometry
// in CLIP SCENE SPACE (rig units, +X right, +Y down, canonical right-facing, origin = the
// clip's scene anchor; see BodyPath) — and an explicit Motion source naming which channel
// owns the body's scene path. A clip's guides are exactly what it authors: nothing is
// synthesized for a missing Scene, so the editor, ClipSceneBake and `probe scenecheck` all
// read the same geometry. A missing Motion is inferred from the body_path track — see
// ClipMotion.Resolve.
//
// Guides are REFERENCE DATA for authoring, never spawned runtime colliders: a rectangle's
// top can describe candidate support and its interior clearance; contacts still say which
// limb is planted and when. Runtime terrain remains the authority for actual support.

public enum SceneGuideKind { Ground, Block }

public sealed class SceneGuide
{
    public string        Id     { get; set; }      // stable identity across edits/saves
    public SceneGuideKind Kind  { get; set; }
    public float         X      { get; set; }      // Block: top-left corner (rig units); Ground: unused
    public float         Y      { get; set; }      // Block: top edge; Ground: the ground line's height
    public float         W      { get; set; }      // Block only, > 0
    public float         H      { get; set; }      // Block only, > 0
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string        Label  { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool          Hidden { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool          Locked { get; set; }

    public SceneGuide Clone() => new()
    { Id = Id, Kind = Kind, X = X, Y = Y, W = W, H = H, Label = Label, Hidden = Hidden, Locked = Locked };
}

// DISPLAY OVERLAYS — the other half of a clip's scene. Where a SceneGuide is geometry the
// clip OWNS (coordinates authored and saved), an overlay carries NO coordinates: it names
// something whose geometry is computed elsewhere, so it cannot go stale against the value it
// draws. A "standing hover" line stored as a number would be a copy of FoldHoverOffset that
// silently lies the next time hover is tuned; the derived kinds below are read back out of
// the game's own constants at draw time (Animation/SceneReferences.cs).
//
// Overlays are an AUTHORING AID ONLY: ClipSceneBake and `probe scenecheck` never look at
// them, so toggling a visual can never move a baked path or a check result. (If foot
// planting ever becomes terrain-aware, that is the rule to revisit — deliberately not yet.)
//
// Two families, told apart by SceneReferences.IsHoverLine / IsTrajectory:
//   DERIVED heights  — no Ref; geometry from the game's constants (hover lines).
//   TRAJECTORIES     — Ref names another document (a ReferenceClips arc, or another clip
//                      whose own motion source is resolved and drawn).
//
// A trajectory overlay is DISPLAY ONLY and never becomes the clip's placement: exactly one
// source owns p(t) (AnimationDocument.Motion — see ClipMotion.Resolve), and that stays true
// however many comparison curves are shown. Referencing the same arc the clip already rides
// is allowed; it just draws over the owned path.
public enum SceneOverlayKind
{
    HoverLine,    // where the body centre rides standing on a surface (fold hover)
    CrouchLine,   // the same, crouched
    Arc,          // Ref = a ReferenceClips arc name, drawn over its own parameter [0,1]
    ClipPath,     // Ref = another clip's name, drawn through ITS resolved motion source
}

public sealed class SceneOverlay
{
    public string           Id    { get; set; }    // stable identity across edits/saves
    public SceneOverlayKind Kind  { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string           Ref   { get; set; }    // arc / clip name for the reference kinds
    // Arc kind only: resolve `Ref` against this clip's OWN arcs (AnimationDocument.Arcs)
    // instead of the shared ReferenceClips/ pool. An explicit flag rather than shadowing by
    // name, so a clip can show the shared arc and its own fork of it side by side.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool             Local { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool             Hidden { get; set; }

    public SceneOverlay Clone() => new() { Id = Id, Kind = Kind, Ref = Ref, Local = Local, Hidden = Hidden };
}

public sealed class ClipScene
{
    public List<SceneGuide> Guides { get; set; } = new();

    // Null when the clip shows no overlays — the common case, and it keeps existing clip
    // files byte-identical until one is added.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SceneOverlay> Overlays { get; set; }

    public ClipScene Clone()
    {
        var c = new ClipScene();
        foreach (var g in Guides) c.Guides.Add(g.Clone());
        if (Overlays != null)
        {
            c.Overlays = new List<SceneOverlay>(Overlays.Count);
            foreach (var o in Overlays) c.Overlays.Add(o.Clone());
        }
        return c;
    }
}

// Where the body's scene path p(t) comes from. The clip's own `body_path` track is the only
// channel — a reference arc is something you MAP onto that track (ClipArcMap), not something
// the clip rides.
public enum MotionSource
{
    InPlace,   // the clip is authored stationary — p(t) ≡ 0
    Track,     // the reserved `body_path` point channel on the keyframes (BodyPath)
}

// The part of a guide a hit landed on — what a drag then edits.
public enum GuidePart
{
    None, Body,
    Left, Right, Top, Bottom,                     // edges (Block)
    TopLeft, TopRight, BottomLeft, BottomRight,   // corners (Block)
    GroundLine,                                   // the Ground guide's draggable height handle
}

// Pure edit operations over a ClipScene, in scene units. The editor's input glue converts
// screen ↔ scene and calls these; the same functions run headless in tests.
public static class SceneGuideOps
{
    public const float MinBlockSize = 1f;   // rig units — a block never collapses to a line

    public static string NewId(ClipScene scene)
    {
        int n = scene.Guides.Count + 1;
        while (scene.Guides.Exists(g => g.Id == $"g{n}")) n++;
        return $"g{n}";
    }

    public static SceneGuide AddGround(ClipScene scene, float y)
    {
        var g = new SceneGuide { Id = NewId(scene), Kind = SceneGuideKind.Ground, Y = y };
        scene.Guides.Add(g);
        return g;
    }

    public static SceneGuide AddBlock(ClipScene scene, float x, float y, float w, float h)
    {
        var g = new SceneGuide { Id = NewId(scene), Kind = SceneGuideKind.Block, X = x, Y = y,
                                 W = MathF.Max(w, MinBlockSize), H = MathF.Max(h, MinBlockSize) };
        scene.Guides.Add(g);
        return g;
    }

    public static SceneGuide Duplicate(ClipScene scene, SceneGuide src, Vector2 offset)
    {
        var g = src.Clone();
        g.Id = NewId(scene);
        g.Locked = false;
        if (g.Kind == SceneGuideKind.Block) { g.X += offset.X; g.Y += offset.Y; }
        else g.Y += offset.Y;
        scene.Guides.Add(g);
        return g;
    }

    public static bool Remove(ClipScene scene, SceneGuide g) => scene.Guides.Remove(g);

    // Nearest hit among visible, unlocked-or-not guides (locking blocks EDITS, not
    // selection). Handles (edges/corners/ground line) win over bodies within `handleR`
    // (scene units); a block's body wins when the point is inside it. Returns the guide and
    // the part, or (null, None).
    public static (SceneGuide guide, GuidePart part) HitTest(ClipScene scene, Vector2 p, float handleR)
    {
        // Priority: corners, then edges / the ground line, then a block body — so a corner
        // within reach is never shadowed by the two edges it sits on.
        SceneGuide bestG = null; GuidePart bestP = GuidePart.None; float bestD = handleR;
        foreach (var g in scene.Guides)
        {
            if (g.Hidden || g.Kind != SceneGuideKind.Block) continue;
            float l = g.X, r = g.X + g.W, t = g.Y, b = g.Y + g.H;
            Corner(g, GuidePart.TopLeft,     new Vector2(l, t)); Corner(g, GuidePart.TopRight,    new Vector2(r, t));
            Corner(g, GuidePart.BottomLeft,  new Vector2(l, b)); Corner(g, GuidePart.BottomRight, new Vector2(r, b));
            void Corner(SceneGuide gg, GuidePart part, Vector2 at)
            { float d = Vector2.Distance(p, at); if (d < bestD) { bestD = d; bestG = gg; bestP = part; } }
        }
        if (bestG != null) return (bestG, bestP);

        SceneGuide bodyG = null;
        foreach (var g in scene.Guides)
        {
            if (g.Hidden) continue;
            if (g.Kind == SceneGuideKind.Ground)
            {
                float d = MathF.Abs(p.Y - g.Y);
                if (d < bestD) { bestD = d; bestG = g; bestP = GuidePart.GroundLine; }
                continue;
            }
            float l = g.X, r = g.X + g.W, t = g.Y, b = g.Y + g.H;
            bool withinY = p.Y >= t - handleR && p.Y <= b + handleR, withinX = p.X >= l - handleR && p.X <= r + handleR;
            void Edge(SceneGuide gg, GuidePart part, float d, bool ok) { if (ok && d < bestD) { bestD = d; bestG = gg; bestP = part; } }
            Edge(g, GuidePart.Left,   MathF.Abs(p.X - l), withinY);
            Edge(g, GuidePart.Right,  MathF.Abs(p.X - r), withinY);
            Edge(g, GuidePart.Top,    MathF.Abs(p.Y - t), withinX);
            Edge(g, GuidePart.Bottom, MathF.Abs(p.Y - b), withinX);
            if (bodyG == null && p.X >= l && p.X <= r && p.Y >= t && p.Y <= b) bodyG = g;
        }
        if (bestG != null) return (bestG, bestP);
        return bodyG != null ? (bodyG, GuidePart.Body) : (null, GuidePart.None);
    }

    // Apply a drag delta (scene units) to `part` of `g`. Locked guides ignore edits. Blocks
    // keep positive width/height (an edge dragged past the opposite one stops at MinBlockSize).
    public static void Drag(SceneGuide g, GuidePart part, Vector2 d)
    {
        if (g.Locked) return;
        if (g.Kind == SceneGuideKind.Ground) { if (part != GuidePart.None) g.Y += d.Y; return; }
        switch (part)
        {
            case GuidePart.Body: g.X += d.X; g.Y += d.Y; break;
            case GuidePart.Left:   case GuidePart.TopLeft:  case GuidePart.BottomLeft:  Left(g, d.X);  break;
        }
        switch (part)
        {
            case GuidePart.Right:  case GuidePart.TopRight: case GuidePart.BottomRight: Right(g, d.X); break;
        }
        switch (part)
        {
            case GuidePart.Top:    case GuidePart.TopLeft:    case GuidePart.TopRight:    Top(g, d.Y);    break;
            case GuidePart.Bottom: case GuidePart.BottomLeft: case GuidePart.BottomRight: Bottom(g, d.Y); break;
        }
    }

    private static void Left(SceneGuide g, float dx)   { float nx = MathF.Min(g.X + dx, g.X + g.W - MinBlockSize); g.W += g.X - nx; g.X = nx; }
    private static void Right(SceneGuide g, float dx)  { g.W = MathF.Max(MinBlockSize, g.W + dx); }
    private static void Top(SceneGuide g, float dy)    { float ny = MathF.Min(g.Y + dy, g.Y + g.H - MinBlockSize); g.H += g.Y - ny; g.Y = ny; }
    private static void Bottom(SceneGuide g, float dy) { g.H = MathF.Max(MinBlockSize, g.H + dy); }

    // ── overlays ────────────────────────────────────────────────────────────────────────
    // Add/remove only: an overlay has no geometry to edit, so there is no Drag/Snap/HitTest
    // counterpart. Kinds are unique per clip — two identical hover lines would just draw on
    // top of each other.
    public static SceneOverlay AddOverlay(ClipScene scene, SceneOverlayKind kind, string reference = null,
                                          bool local = false)
    {
        scene.Overlays ??= new List<SceneOverlay>();
        var existing = scene.Overlays.Find(o => o.Kind == kind && o.Ref == reference && o.Local == local);
        if (existing != null) { existing.Hidden = false; return existing; }
        int n = scene.Overlays.Count + 1;
        while (scene.Overlays.Exists(o => o.Id == $"o{n}")) n++;
        var ov = new SceneOverlay { Id = $"o{n}", Kind = kind, Ref = reference, Local = local };
        scene.Overlays.Add(ov);
        return ov;
    }

    public static bool RemoveOverlay(ClipScene scene, SceneOverlay o)
    {
        if (scene.Overlays == null || !scene.Overlays.Remove(o)) return false;
        if (scene.Overlays.Count == 0) scene.Overlays = null;   // absent, not an empty list
        return true;
    }

    public static bool HasOverlay(ClipScene scene, SceneOverlayKind kind)
        => scene?.Overlays != null && scene.Overlays.Exists(o => o.Kind == kind);

    // Snap a guide's edges to a grid anchored at the ground line (cell = one tile in rig units).
    public static void SnapToGrid(SceneGuide g, float cell, float groundY)
    {
        if (g.Locked || cell <= 1e-4f) return;
        float S(float v) => groundY + MathF.Round((v - groundY) / cell) * cell;
        if (g.Kind == SceneGuideKind.Ground) { g.Y = S(g.Y); return; }
        float l = MathF.Round(g.X / cell) * cell, r = MathF.Round((g.X + g.W) / cell) * cell;
        float t = S(g.Y), b = S(g.Y + g.H);
        if (r - l < MinBlockSize) r = l + cell;
        if (b - t < MinBlockSize) b = t + cell;
        g.X = l; g.W = r - l; g.Y = t; g.H = b - t;
    }
}
