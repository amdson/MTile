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
        float cell = SceneGuideView.TileRig * ScenePlacement.RigScale;
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

    // The body's scene path. For a reference arc the bright stretch is what the clip's own
    // timeline covers (clip and arc carry independent durations — a shorter clip stops early,
    // a longer one runs past the gate along the extrapolation); a green ring marks the gate.
    // A body_path track draws over [0,1]. Keyframes drop a dot where they land; the ring at
    // the playhead is the player's body circle at the game's radius.
    public void DrawPath(DrawContext draw, ScenePlacement pl, AnimationDocument doc)
    {
        var m = pl.Motion;
        if (m == null || doc == null) return;
        if (ShowPath && m.Source == MotionSource.ReferenceArc && m.Arc != null)
        {
            const int Samples = 96;
            float covered = m.ArcProgress(1f), end = MathF.Max(covered, 1f);
            Vector2 prev = pl.ToScreen(m.ArcOffsetAt(0f));
            for (int i = 1; i <= Samples; i++)
            {
                float u = end * i / Samples;
                Vector2 p = pl.ToScreen(m.ArcOffsetAt(u));
                bool live = u <= covered;
                draw.Line(prev, p, live ? new Color(90, 170, 210) : new Color(55, 80, 95), live ? 2f : 1f);
                prev = p;
            }
            draw.Ring(pl.ToScreen(m.ArcOffsetAt(1f)), 5f, new Color(120, 220, 160), 12, 1.5f);
        }
        else if (ShowPath && m.Source == MotionSource.Track)
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
            draw.Ring(pl.Anchor, PlayerCharacter.Radius / Game1.SkeletonScale * ScenePlacement.RigScale,
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
            if (ShowContacts && kf.Contacts != null)
            {
                var world = ghost.ComputeWorld(root);
                foreach (var cl in kf.Contacts)
                {
                    int bi = EndpointResolver.BoneOf(rig, doc, cl);
                    if (bi >= 0) draw.Disc(world[bi].Translation, 4f, new Color(70, 220, 110) * 0.7f);
                }
            }
        }
    }

    // The player's PHYSICS polygon (the width-squeezed hexagon) at true game scale around the
    // com anchor: its bottom vertex hovers one Radius above the ground line, like in game.
    public void DrawBody(DrawContext draw, Vector2 anchor)
    {
        if (!ShowBody) return;
        float s = ScenePlacement.RigScale / Game1.SkeletonScale;
        var verts = PlayerCharacter.CreateBodyPolygon().GetVertices(Vector2.Zero);
        var col = new Color(230, 190, 90);
        for (int i = 0; i < verts.Length; i++)
            draw.Line(anchor + verts[i] * s, anchor + verts[(i + 1) % verts.Length] * s, col, 1.5f);
        draw.Ring(anchor, 2.5f, col, 8, 1f);
    }
}
