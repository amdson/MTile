using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MTile;

namespace MTileDemo;

internal enum GuideTool { None, AddGround, AddBlock, Select }

// SCENE GUIDE EDITING (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Refactor boundaries" 2): the
// input glue and drawing for a clip's scene guides — selection, hit tests, drag state, tool
// placement — over the pure operations in SceneGuideOps. A clip with no explicit Scene shows
// the LEGACY preview (the floor line 2·Radius under the anchor, the one-tile obstacle block
// for the lip-maneuver clips, draggable, persisted in the editor view state); the first guide
// edit materializes that preview into the clip's own Scene. Every drag is cancelable: Escape
// restores the pre-drag values or drops a half-placed block.
internal sealed class SceneGuideView
{
    public GuideTool Tool;
    public bool      Snap;                 // snap edits to the tile grid (anchored at the ground line)
    public string    SelectedId;
    public Vector2   LegacyBlockOffset;    // legacy obstacle block's scene offset (view state)
    public string    Hint;                 // one-line status for the header (what a click will do)

    private SceneGuide _dragGuide; private GuidePart _dragPart; private SceneGuide _preDrag;
    private SceneGuide _placing;   private Vector2   _placeStart;
    private bool       _dragLegacyBlock;
    public bool Dragging => _dragGuide != null || _placing != null || _dragLegacyBlock;

    public const float TileRig = Chunk.TileSize / Game1.SkeletonScale;   // one game tile in rig units
    private const float PickR = 12f;   // screen px

    // The clips the legacy obstacle block is scenery for (keyed on the enum so a rename fails
    // to compile instead of silently losing the block).
    private static readonly AnimClip[] BlockClips = { AnimClip.Parkour, AnimClip.Mantle, AnimClip.ArcJump, AnimClip.LedgePull };
    public static bool LegacyHasBlock(AnimationDocument doc)
        => Enum.TryParse<AnimClip>(doc?.Type, ignoreCase: true, out var c) && Array.IndexOf(BlockClips, c) >= 0;

    // The guides in effect: the clip's explicit Scene, else a transient legacy materialization
    // (never stored until an edit happens).
    public ClipScene Effective(AnimationDocument doc, float groundY)
        => doc?.Scene ?? SceneGuideOps.Legacy(groundY, LegacyHasBlock(doc), LegacyBlockOffset, TileRig);

    private ClipScene Materialize(AnimationDocument doc, float groundY, ref bool dirty)
    {
        if (doc.Scene == null) { doc.Scene = SceneGuideOps.Legacy(groundY, LegacyHasBlock(doc), LegacyBlockOffset, TileRig); dirty = true; }
        return doc.Scene;
    }

    public SceneGuide Selected(AnimationDocument doc)
        => SelectedId == null ? null : doc?.Scene?.Guides.Find(g => g.Id == SelectedId);

    // A left-press in the working area. Returns true when the guide layer consumed it.
    public bool HandlePress(Vector2 mp, in Affine2 frame, float groundY, AnimationDocument doc, ref bool dirty)
    {
        if (doc == null) return false;
        Vector2 sp = frame.Inverse().TransformPoint(mp);
        float handleR = PickR / ScenePlacement.RigScale;
        switch (Tool)
        {
            case GuideTool.AddGround:
            {
                var scene = Materialize(doc, groundY, ref dirty);
                var g = SceneGuideOps.AddGround(scene, Snapped(sp, groundY).Y);
                SelectedId = g.Id; dirty = true; Tool = GuideTool.Select;
                return true;
            }
            case GuideTool.AddBlock:
            {
                var scene = Materialize(doc, groundY, ref dirty);
                _placeStart = Snapped(sp, groundY);
                _placing = SceneGuideOps.AddBlock(scene, _placeStart.X, _placeStart.Y, SceneGuideOps.MinBlockSize, SceneGuideOps.MinBlockSize);
                SelectedId = _placing.Id; dirty = true;
                return true;
            }
            case GuideTool.Select:
            {
                var (hit, part) = SceneGuideOps.HitTest(Effective(doc, groundY), sp, handleR);
                if (hit == null) { SelectedId = null; return false; }
                if (doc.Scene == null)
                {
                    // The hit was on the transient legacy preview: materialize it, then find the
                    // same guide in the clip's own scene (same order, same ids).
                    var scene = Materialize(doc, groundY, ref dirty);
                    hit = scene.Guides.Find(g => g.Id == hit.Id) ?? hit;
                }
                SelectedId = hit.Id;
                if (!hit.Locked) { _dragGuide = hit; _dragPart = part; _preDrag = hit.Clone(); }
                return true;
            }
            default:
            {
                // Legacy obstacle block drag (no explicit scene, not in a guide tool).
                if (doc.Scene != null || !LegacyHasBlock(doc)) return false;
                var block = Effective(doc, groundY).Guides.Find(g => g.Kind == SceneGuideKind.Block);
                if (block == null) return false;
                if (sp.X < block.X || sp.X > block.X + block.W || sp.Y < block.Y || sp.Y > block.Y + block.H) return false;
                _dragLegacyBlock = true;
                return true;
            }
        }
    }

    public void HandleDrag(Vector2 mp, Vector2 dmScreen, in Affine2 frame, float groundY, ref bool dirty)
    {
        Vector2 dm = dmScreen / ScenePlacement.RigScale;
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
        else if (_dragLegacyBlock) LegacyBlockOffset += dm;
    }

    public void Release(float groundY)
    {
        if (_dragGuide != null && Snap) SceneGuideOps.SnapToGrid(_dragGuide, TileRig, groundY);
        _dragGuide = null; _preDrag = null; _placing = null; _dragLegacyBlock = false;
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
        if (_dragLegacyBlock) { _dragLegacyBlock = false; return true; }
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
    public void Draw(DrawContext draw, SpriteBatch sb, SpriteFont font, in Affine2 frame, float groundY,
                     AnimationDocument doc, int x0, int x1)
    {
        var scene = Effective(doc, groundY);
        bool explicitScene = doc?.Scene != null;
        foreach (var g in scene.Guides)
        {
            if (g.Hidden) continue;
            bool selected = explicitScene && g.Id == SelectedId;
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
