using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace MTile;

// SCENE GUIDES and MOTION INTENT (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md, "Proposed document
// additions"; workplan chunk 3). A clip may carry an explicit Scene — fixed reference geometry
// in CLIP SCENE SPACE (rig units, +X right, +Y down, canonical right-facing, origin = the
// clip's scene anchor; see BodyPath) — and an explicit Motion source naming which channel
// owns the body's scene path. Both are optional: a missing Scene keeps the editor's legacy
// floor-line + obstacle-block preview, and a missing Motion keeps the legacy precedence
// (ReferenceArc → body_path track → stationary) — see ClipMotion.Resolve.
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

public sealed class ClipScene
{
    // An explicitly EMPTY list means "no guides" (distinct from a missing Scene, which
    // means "legacy preview").
    public List<SceneGuide> Guides { get; set; } = new();

    public ClipScene Clone()
    {
        var c = new ClipScene();
        foreach (var g in Guides) c.Guides.Add(g.Clone());
        return c;
    }
}

// Which channel owns the body's scene path p(t). Exactly one owner.
public enum MotionSource
{
    InPlace,        // the clip is authored stationary — p(t) ≡ 0 (an explicit declaration)
    Track,          // the reserved `body_path` point channel on the keyframes (BodyPath)
    ReferenceArc,   // the clip's named ReferenceArc (HermiteClipDocument), mapped to scene units
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

    // The materialized LEGACY preview — the floor line 2·Radius/scale under the anchor and,
    // for the lip-maneuver clips, the one-tile obstacle block one tile ahead — so a first
    // scene edit on an old clip starts from exactly what the editor was showing.
    public static ClipScene Legacy(float groundY, bool withBlock, Vector2 blockOffset, float tileRig)
    {
        var s = new ClipScene();
        AddGround(s, groundY).Label = "floor";
        if (withBlock)
            AddBlock(s, tileRig + blockOffset.X, groundY - tileRig + blockOffset.Y, tileRig, tileRig).Label = "block";
        return s;
    }

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
