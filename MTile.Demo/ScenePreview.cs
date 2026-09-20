using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MTile;

namespace MTileDemo;

// SCENE PREVIEW (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Refactor boundaries" 3): the tile
// grid, the body's scene path with its keyframe dots, pose ghosts at the keyframes with their
// contact marks, and the game's physics outline — all placed through the same ScenePlacement /
// ClipMotion the rendered pose uses, so what is drawn where the ghost stands is where the pose
// will render when scrubbed there.
internal sealed class ScenePreview
{
    public bool ShowGrid = true, ShowPath = true, ShowGhosts, ShowContacts = true, ShowBody;

    // One grid cell = one game tile, anchored to the ground line and the scene X origin, so an
    // authored rise reads straight off in blocks and a reference block fills exactly one cell.
    public void DrawGrid(DrawContext draw, in Affine2 frame, float groundY, float x0, float x1, float y0, float y1)
    {
        if (!ShowGrid) return;
        // The frame carries the live view scale (ScenePlacement.Scale), so the grid zooms with it.
        float cell = SceneGuideView.TileRig * frame.TransformVector(Vector2.UnitX).X;
        if (cell < 4f) return;
        var minor = new Color(38, 42, 52);
        var axis  = new Color(58, 64, 78);
        float originX = frame.TransformPoint(Vector2.Zero).X;
        float floorY  = frame.TransformPoint(new Vector2(0f, groundY)).Y;
        for (float x = originX; x <= x1; x += cell) if (x >= x0) draw.Line(new Vector2(x, y0), new Vector2(x, y1), x == originX ? axis : minor, 1f);
        for (float x = originX - cell; x >= x0; x -= cell)      draw.Line(new Vector2(x, y0), new Vector2(x, y1), minor, 1f);
        for (float y = floorY; y <= y1; y += cell) if (y >= y0) draw.Line(new Vector2(x0, y), new Vector2(x1, y), y == floorY ? axis : minor, 1f);
        for (float y = floorY - cell; y >= y0; y -= cell)       draw.Line(new Vector2(x0, y), new Vector2(x1, y), minor, 1f);
    }

    // The body's scene path: the clip's own body_path track over [0,1]. Keyframes drop a dot
    // where they land; the ring at the playhead is the player's body circle at the game's
    // radius. (A reference arc is an OVERLAY now — DrawOverlays — never the path itself.)
    public void DrawPath(DrawContext draw, ScenePlacement pl, AnimationDocument doc)
    {
        var m = pl.Motion;
        if (m == null || doc == null) return;
        if (ShowPath && m.Source == MotionSource.Track)
        {
            const int Samples = 64;
            Vector2 prev = pl.ToScreen(m.BodyAt(0f));
            for (int i = 1; i <= Samples; i++)
            {
                Vector2 p = pl.ToScreen(m.BodyAt(i / (float)Samples));
                draw.Line(prev, p, new Color(90, 170, 210), 2f);
                prev = p;
            }
            if (doc.Loop && m.CycleDisplacement.LengthSquared() > 1e-6f)
                draw.Ring(pl.ToScreen(m.BodyAt(1f)), 5f, new Color(120, 220, 160), 12, 1.5f);   // the seam: D from here
        }
        if (ShowPath && m.Source != MotionSource.InPlace && doc.Keyframes != null)
            foreach (var kf in doc.Keyframes)
                draw.Disc(pl.ToScreen(m.BodyAt(kf.Time)), 3.5f, new Color(150, 200, 235));
        if (ShowPath && m.Source != MotionSource.InPlace)
            draw.Ring(pl.Anchor, PlayerCharacter.Radius / Game1.SkeletonScale * pl.Scale,
                      new Color(230, 190, 90), 24, 1.5f);
    }

    // Dim pose ghosts at every keyframe, each hung from its own placement, with the keyframe's
    // planted nodes marked — the "does the foot stay on the tread across the step" read.
    public void DrawGhosts(DrawContext draw, ScenePlacement pl, AnimationDocument doc, Skeleton rig,
                           SkeletonPose ghost, SkeletonPose a, SkeletonPose b, SkeletonPose c, SkeletonPose d, int activeKey)
    {
        if (doc?.Keyframes == null || pl.Motion == null) return;
        if (!ShowGhosts && !ShowContacts) return;
        var style = SkeletonDrawStyle.Default;
        style.BoneThickness = 2f; style.JointRadius = 0f;
        style.BoneColor = new Color(70, 80, 100);
        for (int k = 0; k < doc.Keyframes.Count; k++)
        {
            var kf = doc.Keyframes[k];
            if (k == activeKey) continue;   // the live pose is drawn there
            AnimationSampler.SampleSmooth(doc, kf.Time, a, b, c, d, ghost);
            var root = pl.RootAt(kf.Time);
            if (ShowGhosts) SkeletonRenderer.Draw(draw, ghost, root, style);
            // Contact marks on a ghost show the spans LIVE at that keyframe's phase, faded by
            // the span's own weight there — so a ghost mid-crossfade reads as mid-crossfade.
            if (ShowContacts && doc.Contacts != null)
            {
                var world = ghost.ComputeWorld(root);
                foreach (var cs in doc.Contacts)
                {
                    if (!cs.Covers(kf.Time, out _)) continue;
                    float w = cs.WeightAt(kf.Time);
                    if (w <= 0.01f) continue;
                    // Tolerant here, unlike the solver: a preview that throws on a contact the
                    // clip cannot honor hides every OTHER mark and takes the editor with it.
                    if (!EndpointResolver.TryResolvePoint(rig, doc, cs.Point, out var rp) || !rp.IsExactTip) continue;
                    draw.Disc(world[rp.Bone].Translation, 4f, new Color(70, 220, 110) * (0.25f + 0.55f * w));
                }
            }
        }
    }

    // PER-CLIP DISPLAY OVERLAYS (ClipScene.Overlays) — derived reference geometry, drawn
    // DASHED so it never reads as authored guide geometry. A hover line is drawn over every
    // surface the scene describes: across the view for a ground line, across its own span for
    // a block top, at the height the body centre rides when standing there
    // (Animation/SceneReferences.cs computes it from the live movement config).
    //
    // Overlays live on doc.Scene; the guides they hang off are passed in separately so a clip
    // whose scene was just created still marks the guides drawn this frame.
    public void DrawOverlays(DrawContext draw, SpriteBatch sb, SpriteFont font, in Affine2 frame,
                             ScenePlacement pl, AnimationDocument doc, ClipScene effective,
                             Func<string, bool, HermiteClipDocument> arcs, Func<string, AnimationDocument> clips,
                             float x0, float x1)
    {
        var overlays = doc?.Scene?.Overlays;
        if (overlays == null || effective == null) return;
        foreach (var o in overlays)
        {
            if (o.Hidden) continue;
            if (SceneReferences.IsTrajectory(o.Kind)) DrawTrajectoryOverlay(draw, sb, font, pl, o, arcs, clips);
            else DrawHoverOverlay(draw, sb, font, frame, effective, o, x0, x1);
        }
    }

    private void DrawHoverOverlay(DrawContext draw, SpriteBatch sb, SpriteFont font, in Affine2 frame,
                                  ClipScene effective, SceneOverlay o, float x0, float x1)
    {
        float rig = SceneReferences.ComRig(o.Kind);
        var col = o.Kind == SceneOverlayKind.CrouchLine ? new Color(200, 150, 240) : new Color(120, 220, 200);
        bool labelled = false;
        foreach (var g in effective.Guides)
        {
            if (g.Hidden) continue;
            if (g.Kind == SceneGuideKind.Ground)
            {
                float y = frame.TransformPoint(new Vector2(0f, g.Y - rig)).Y;
                draw.Dashed(new Vector2(x0, y), new Vector2(x1, y), col, 1f);
                if (!labelled)
                {
                    sb.DrawString(font, SceneReferences.Name(o.Kind), new Vector2(x0 + 8f, y - 16f), col);
                    labelled = true;
                }
            }
            else
            {
                Vector2 l = frame.TransformPoint(new Vector2(g.X, g.Y - rig));
                Vector2 r = frame.TransformPoint(new Vector2(g.X + g.W, g.Y - rig));
                draw.Dashed(l, r, col, 1f);
            }
        }
    }

    // A REFERENCE TRAJECTORY: an arc over its own parameter, or another clip's path through
    // that clip's own resolved motion source. Both are sampled in scene units and drawn in
    // THIS clip's scene frame (both are com positions measured from their scene anchor, so
    // the shared origin is the comparison that matters), dashed and dimmer than the owned
    // path — a ring marks the far end. Unresolvable refs draw nothing; the Scene panel is
    // where that is reported.
    private void DrawTrajectoryOverlay(DrawContext draw, SpriteBatch sb, SpriteFont font, ScenePlacement pl,
                                       SceneOverlay o, Func<string, bool, HermiteClipDocument> arcs,
                                       Func<string, AnimationDocument> clips)
    {
        const int Samples = 48;
        Func<float, Vector2> at = null;
        Color col;
        if (o.Kind == SceneOverlayKind.Arc)
        {
            var arc = arcs?.Invoke(o.Ref, o.Local);
            if (arc == null) return;
            at = u => ClipMotion.ArcOffset(arc, u);
            col = new Color(150, 140, 230);
        }
        else
        {
            var other = clips?.Invoke(o.Ref);
            if (other == null) return;
            var m = ClipMotion.Resolve(other);
            if (m.Source == MotionSource.InPlace) return;   // a stationary clip has no path to show
            at = m.BodyAt;
            col = new Color(230, 180, 110);
        }

        Vector2 prev = pl.ToScreen(at(0f));
        for (int i = 1; i <= Samples; i++)
        {
            Vector2 p = pl.ToScreen(at(i / (float)Samples));
            draw.Dashed(prev, p, col, 1.5f, 5f, 3f);
            prev = p;
        }
        draw.Ring(prev, 4f, col, 12, 1.5f);
        string tag = o.Kind == SceneOverlayKind.Arc && o.Local ? $"{o.Ref} (local)" : o.Ref;
        sb.DrawString(font, $"{SceneReferences.Name(o.Kind)}: {tag}", pl.ToScreen(at(0f)) + new Vector2(8f, -14f), col);
    }

    // The player's PHYSICS polygon (the width-squeezed hexagon) at true game scale around the
    // com anchor: its bottom vertex hovers one Radius above the ground line, like in game.
    public void DrawBody(DrawContext draw, ScenePlacement pl)
    {
        if (!ShowBody) return;
        Vector2 anchor = pl.Anchor;
        float s = pl.Scale / Game1.SkeletonScale;
        var verts = PlayerCharacter.CreateBodyPolygon().GetVertices(Vector2.Zero);
        var col = new Color(230, 190, 90);
        for (int i = 0; i < verts.Length; i++)
            draw.Line(anchor + verts[i] * s, anchor + verts[(i + 1) % verts.Length] * s, col, 1.5f);
        draw.Ring(anchor, 2.5f, col, 8, 1f);
    }
}
