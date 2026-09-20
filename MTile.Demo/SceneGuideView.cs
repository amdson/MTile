using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MTile;

namespace MTileDemo;

internal enum GuideTool { None, AddGround, AddBlock, Select }

// SCENE GUIDE EDITING (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Refactor boundaries" 2): the
// input glue and drawing for a clip's scene guides — selection, hit tests, drag state, tool
// placement — over the pure operations in SceneGuideOps. Every guide a clip shows is authored
// data in its own Scene: there is no synthesized preview, so what you see is what the file
// holds and what ClipSceneBake reads. Every drag is cancelable: Escape restores the pre-drag
// values or drops a half-placed block.
internal sealed class SceneGuideView
{
    public GuideTool Tool;
    public bool      Snap;                 // snap edits to the tile grid (anchored at the ground line)
    public string    SelectedId;
    public string    Hint;                 // one-line status for the header (what a click will do)

    private SceneGuide _dragGuide; private GuidePart _dragPart; private SceneGuide _preDrag;
    private SceneGuide _placing;   private Vector2   _placeStart;
    public bool Dragging => _dragGuide != null || _placing != null;

    public const float TileRig = Chunk.TileSize / Game1.SkeletonScale;   // one game tile in rig units
    private const float PickR = 12f;   // screen px

    // Read-only stand-in for a clip that carries no Scene at all (a doc built in memory, or one
    // authored before scenes existed). Never mutated — EnsureScene gives the clip its own.
    private static readonly ClipScene Empty = new();

    // The guides in effect: the clip's own Scene, or nothing to draw.
    public ClipScene Effective(AnimationDocument doc) => doc?.Scene ?? Empty;

    // Give the clip a Scene to edit — what the Scene panel calls before adding a guide or an
    // overlay. A clip that has none starts EMPTY rather than pre-populated: guides are the
    // author's statement about the terrain, so the editor never invents one.
    public ClipScene EnsureScene(AnimationDocument doc, ref bool dirty)
    {
        if (doc == null) return null;
        if (doc.Scene == null) { doc.Scene = new ClipScene(); dirty = true; }
        return doc.Scene;
    }

    public SceneGuide Selected(AnimationDocument doc)
        => SelectedId == null ? null : doc?.Scene?.Guides.Find(g => g.Id == SelectedId);

    // What a press at `mp` WOULD hit in the guide layer, without consuming it. The editor uses
    // this to resolve the one real overlap between the two layers: the rig is drawn on top of
    // the scene, so its handles beat a guide's BODY, while a guide's edges and corners (which
    // HitTest already prioritises) still beat the rig. Only meaningful in Select — the
    // placement tools are an armed intent that always wins.
    public GuidePart Peek(Vector2 mp, in Affine2 frame, float groundY, AnimationDocument doc)
    {
        if (doc == null || Tool != GuideTool.Select) return GuidePart.None;
        Vector2 sp = frame.Inverse().TransformPoint(mp);
        float handleR = PickR / frame.TransformVector(Vector2.UnitX).X;
        var (hit, part) = SceneGuideOps.HitTest(Effective(doc), sp, handleR);
        return hit == null ? GuidePart.None : part;
    }

    // A left-press in the working area. Returns true when the guide layer consumed it.
    public bool HandlePress(Vector2 mp, in Affine2 frame, float groundY, AnimationDocument doc, ref bool dirty)
    {
        if (doc == null) return false;
        Vector2 sp = frame.Inverse().TransformPoint(mp);
        float handleR = PickR / frame.TransformVector(Vector2.UnitX).X;   // live view scale
        switch (Tool)
        {
            case GuideTool.AddGround:
            {
                var scene = EnsureScene(doc, ref dirty);
                var g = SceneGuideOps.AddGround(scene, Snapped(sp, groundY).Y);
                SelectedId = g.Id; dirty = true; Tool = GuideTool.Select;
                return true;
            }
            case GuideTool.AddBlock:
            {
                var scene = EnsureScene(doc, ref dirty);
                _placeStart = Snapped(sp, groundY);
                _placing = SceneGuideOps.AddBlock(scene, _placeStart.X, _placeStart.Y, SceneGuideOps.MinBlockSize, SceneGuideOps.MinBlockSize);
                SelectedId = _placing.Id; dirty = true;
                return true;
            }
            case GuideTool.Select:
            {
                var (hit, part) = SceneGuideOps.HitTest(Effective(doc), sp, handleR);
                if (hit == null) { SelectedId = null; return false; }
                SelectedId = hit.Id;
                if (!hit.Locked) { _dragGuide = hit; _dragPart = part; _preDrag = hit.Clone(); }
                return true;
            }
            default: return false;
        }
    }

    public void HandleDrag(Vector2 mp, Vector2 dmScreen, in Affine2 frame, float groundY, ref bool dirty)
    {
        Vector2 dm = dmScreen / frame.TransformVector(Vector2.UnitX).X;   // live view scale
        if (_placing != null)
        {
            Vector2 sp = Snapped(frame.Inverse().TransformPoint(mp), groundY);
            float l = MathF.Min(_placeStart.X, sp.X), r = MathF.Max(_placeStart.X, sp.X);
            float t = MathF.Min(_placeStart.Y, sp.Y), b = MathF.Max(_placeStart.Y, sp.Y);
            _placing.X = l; _placing.Y = t;
            _placing.W = MathF.Max(SceneGuideOps.MinBlockSize, r - l);
            _placing.H = MathF.Max(SceneGuideOps.MinBlockSize, b - t);
            dirty = true;
        }
        else if (_dragGuide != null)
        {
            SceneGuideOps.Drag(_dragGuide, _dragPart, dm);
            dirty = true;
        }
    }

    public void Release(float groundY)
    {
        if (_dragGuide != null && Snap) SceneGuideOps.SnapToGrid(_dragGuide, TileRig, groundY);
        _dragGuide = null; _preDrag = null; _placing = null;
    }

    // Escape: drop a half-placed block, restore a dragged guide, or leave the tool. True when
    // something was canceled (the caller then does NOT treat Escape as quit).
    public bool Cancel(AnimationDocument doc)
    {
        if (_placing != null) { doc?.Scene?.Guides.Remove(_placing); _placing = null; SelectedId = null; return true; }
        if (_dragGuide != null && _preDrag != null)
        {
            _dragGuide.X = _preDrag.X; _dragGuide.Y = _preDrag.Y; _dragGuide.W = _preDrag.W; _dragGuide.H = _preDrag.H;
            _dragGuide = null; _preDrag = null;
            return true;
        }
        if (Tool != GuideTool.None) { Tool = GuideTool.None; return true; }
        return false;
    }

    // ── menu operations on the selection ───────────────────────────────────────────
    public void DeleteSelected(AnimationDocument doc, ref bool dirty)
    {
        var g = Selected(doc); if (g == null) return;
        SceneGuideOps.Remove(doc.Scene, g); SelectedId = null; dirty = true;
    }
    public void DuplicateSelected(AnimationDocument doc, ref bool dirty)
    {
        var g = Selected(doc); if (g == null) return;
        SelectedId = SceneGuideOps.Duplicate(doc.Scene, g, new Vector2(TileRig, 0f)).Id; dirty = true;
    }
    public void ToggleHidden(AnimationDocument doc, ref bool dirty) { var g = Selected(doc); if (g != null) { g.Hidden = !g.Hidden; dirty = true; } }
    public void ToggleLocked(AnimationDocument doc, ref bool dirty) { var g = Selected(doc); if (g != null) { g.Locked = !g.Locked; dirty = true; } }

    private Vector2 Snapped(Vector2 sp, float groundY)
    {
        if (!Snap) return sp;
        float S(float v, float origin) => origin + MathF.Round((v - origin) / TileRig) * TileRig;
        return new Vector2(S(sp.X, 0f), S(sp.Y, groundY));
    }

    // ── drawing ─────────────────────────────────────────────────────────────────────
    public void Draw(DrawContext draw, SpriteBatch sb, SpriteFont font, in Affine2 frame,
                     AnimationDocument doc, int x0, int x1)
    {
        foreach (var g in Effective(doc).Guides)
        {
            if (g.Hidden) continue;
            bool selected = g.Id == SelectedId;
            if (g.Kind == SceneGuideKind.Ground)
            {
                float y = frame.TransformPoint(new Vector2(0f, g.Y)).Y;
                var c = selected ? new Color(150, 200, 160) : new Color(90, 110, 95);
                DrawDashedH(draw, y, x0 + 20, x1 - 20, c, 9f, 7f);
                sb.DrawString(font, g.Label ?? "ground", new Vector2(x0 + 20, y + 3), c);
                if (selected) draw.Disc(new Vector2(x0 + 40, y), 4f, c);
                continue;
            }
            Vector2 tl = frame.TransformPoint(new Vector2(g.X, g.Y));
            Vector2 br = frame.TransformPoint(new Vector2(g.X + g.W, g.Y + g.H));
            var rect = new Rectangle((int)tl.X, (int)tl.Y, (int)(br.X - tl.X), (int)(br.Y - tl.Y));
            sb.Draw(draw.Pixel, rect, g.Locked ? new Color(64, 56, 48) : new Color(82, 64, 48));
            var top = selected ? new Color(220, 190, 140) : new Color(150, 118, 88);
            draw.Line(new Vector2(rect.Left, rect.Top), new Vector2(rect.Right, rect.Top), top, 2f);
            draw.Line(new Vector2(rect.Left, rect.Top), new Vector2(rect.Left, rect.Bottom), new Color(48, 36, 26), 1f);
            draw.Line(new Vector2(rect.Right, rect.Top), new Vector2(rect.Right, rect.Bottom), new Color(48, 36, 26), 1f);
            if (selected)
            {
                foreach (var h in new[] { tl, new Vector2(br.X, tl.Y), new Vector2(tl.X, br.Y), br })
                    draw.Rect(h, new Vector2(6f, 6f), top);
                string info = $"{g.Label ?? g.Id}  x {g.X:0.0}  y {g.Y:0.0}  w {g.W:0.0}  h {g.H:0.0}  (rig units; tile = {TileRig:0.0})"
                            + (g.Locked ? "  locked" : "");
                sb.DrawString(font, info, new Vector2(tl.X, tl.Y - 18f), top);
            }
            else if (g.Label != null) sb.DrawString(font, g.Label, new Vector2(tl.X + 4f, tl.Y + 2f), new Color(150, 118, 88));
        }
        Hint = Tool switch
        {
            GuideTool.AddGround => "click to place a ground line (Esc cancels)",
            GuideTool.AddBlock  => "drag to place a block (Esc cancels)",
            GuideTool.Select    => SelectedId != null ? "drag a guide / edge / corner  ·  Del deletes  ·  Esc leaves guide mode" : "click a guide to select it",
            _ => null,
        };
    }

    public static void DrawDashedH(DrawContext draw, float y, float x0, float x1, Color c, float dash, float gap)
    {
        for (float x = x0; x < x1; x += dash + gap)
            draw.Line(new Vector2(x, y), new Vector2(MathF.Min(x + dash, x1), y), c, 1f);
    }
}
