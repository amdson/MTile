using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// World-space debug visualizations: hit/hurt/force-field regions, physics
// constraints, steering ramps, body polygons, and entity health bars. Each draw
// is gated by a GameConfig.DebugDraw* flag at the call site; this type just
// renders. Runs inside Game1's world-space SpriteBatch pass — Begin/End is the
// caller's responsibility.
public sealed class DebugOverlayRenderer
{
    private readonly DrawContext _draw;
    private readonly SpriteBatch _spriteBatch;
    private readonly Texture2D   _pixel;

    public DebugOverlayRenderer(DrawContext draw)
    {
        _draw        = draw;
        _spriteBatch = draw.SpriteBatch;
        _pixel       = draw.Pixel;
    }

    public void DrawPolygon(Polygon polygon, Vector2 position, Color color)
    {
        var verts = polygon.GetVertices(position);
        for (int i = 0; i < verts.Length; i++)
            DrawLine(verts[i], verts[(i + 1) % verts.Length], color);
    }

    // Compact 16×2 bar floating above the entity. Background dark gray, fill
    // green→red as HP drops, both in world space so the bar tracks the body.
    public void DrawEntityHealthBar(Entity e)
    {
        const int BarWidth   = 18;
        const int BarHeight  = 2;
        var bounds = e.Body.Bounds;
        int x = (int)(bounds.CenterX - BarWidth * 0.5f);
        int y = (int)(bounds.Top - 6);
        float frac = MathHelper.Clamp(e.Health / e.MaxHealth, 0f, 1f);
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, BarWidth, BarHeight), new Color(40, 40, 40));
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, (int)(BarWidth * frac), BarHeight),
            Color.Lerp(Color.Red, Color.LimeGreen, frac));
    }

    // Force-field overlay (hold / grab / throw) — faint region fill + outline + a
    // focus marker, colored by the field's DebugColor. Mirrors DrawHitbox; world-space.
    public void DrawForceField(ForceField f)
    {
        var color = f.DebugColor;
        var r = f.Region;
        var rect = new Rectangle((int)r.Left, (int)r.Top,
            (int)(r.Right - r.Left), (int)(r.Bottom - r.Top));
        _spriteBatch.Draw(_pixel, rect, color * 0.12f);
        var tl = new Vector2(r.Left,  r.Top);
        var tr = new Vector2(r.Right, r.Top);
        var br = new Vector2(r.Right, r.Bottom);
        var bl = new Vector2(r.Left,  r.Bottom);
        DrawLine(tl, tr, color, 1);
        DrawLine(tr, br, color, 1);
        DrawLine(br, bl, color, 1);
        DrawLine(bl, tl, color, 1);
        // Focus marker — the point the servo pulls/flings toward.
        _spriteBatch.Draw(_pixel, new Rectangle((int)f.Focus.X - 2, (int)f.Focus.Y - 2, 4, 4), color);
    }

    public void DrawConstraintArrow(Vector2 position, Vector2 normal, Color color)
    {
        const float shaftLength = 20f;
        const float headLength = 8f;
        var tip = position + normal * shaftLength;
        var perp = new Vector2(-normal.Y, normal.X);
        DrawLine(position, tip, color);
        DrawLine(tip, tip + (-normal + perp) * headLength * 0.707f, color);
        DrawLine(tip, tip + (-normal - perp) * headLength * 0.707f, color);
    }

    // Visualize one SteeringRamp at its corner: the surface tangent (the "ramp" the
    // body skims along), the banned direction (into the solid), and the corner dot.
    // Color = Sense (Over: lime / Under: orange); opacity fades with Weight so inert
    // ramps appear ghosted.
    public void DrawSteeringRamp(SteeringRamp ramp)
    {
        var baseColor = ramp.Sense == SteeringSense.Over ? Color.LimeGreen : Color.Orange;
        float alpha = 0.25f + 0.75f * MathHelper.Clamp(ramp.Weight, 0f, 1f);
        var color   = baseColor * alpha;
        var banned  = baseColor * (alpha * 0.45f);

        // Tangent line through the corner (the implicit ramp surface, on both sides).
        const float Tangent = 28f;
        var tan = ramp.SurfaceDir * Tangent;
        DrawLine(ramp.Corner - tan, ramp.Corner + tan, color, 2);

        // Arrowhead at the leading tip so the travel direction reads.
        var lead = ramp.Corner + tan;
        var perp = new Vector2(-ramp.SurfaceDir.Y, ramp.SurfaceDir.X) * 6f;
        DrawLine(lead, lead - tan * 0.25f + perp, color, 1);
        DrawLine(lead, lead - tan * 0.25f - perp, color, 1);

        // Banned direction (into the solid) — a short ghosted stub from the corner.
        DrawLine(ramp.Corner, ramp.Corner + ramp.BannedDir * 14f, banned, 1);

        // Corner marker.
        _draw.Disc(ramp.Corner, 3f, color);
        _draw.Ring(ramp.Corner, 5f, color, 12, 1f);
    }

    // Corridor probe overlay (Plans/CORRIDOR_MANEUVER_PLAN.md): per-column floor gates
    // (white) and ceiling gates (violet), corner markers (rise: lime, drop: sky blue,
    // ceiling lip: orange), and a red truncation post with the reason initial when the
    // scan stopped on infeasible geometry. Render-only — callers Scan() fresh; nothing
    // here touches the sim.
    public void DrawCorridor(Corridor c)
    {
        if (c == null) return;

        for (int i = 0; i < c.ColumnCount; i++)
        {
            float x0 = c.ColumnEdgeX(i);
            float x1 = x0 + c.Dir * Chunk.TileSize;
            DrawLine(new Vector2(x0, c.FloorY[i]), new Vector2(x1, c.FloorY[i]), Color.White * 0.75f, 2);
            if (!float.IsNegativeInfinity(c.CeilY[i]))
                DrawLine(new Vector2(x0, c.CeilY[i]), new Vector2(x1, c.CeilY[i]), Color.Violet * 0.8f, 2);
        }

        for (int i = 0; i < c.FloorCornerCount; i++)
        {
            var corner = c.FloorCorners[i];
            var color  = corner.Delta > 0f ? Color.LimeGreen : Color.DeepSkyBlue;
            _draw.Disc(corner.Pos, 3f, color);
            _draw.Ring(corner.Pos, 5f, color, 12, 1f);
        }
        for (int i = 0; i < c.CeilCornerCount; i++)
        {
            _draw.Disc(c.CeilCorners[i].Pos, 3f, Color.Orange);
            _draw.Ring(c.CeilCorners[i].Pos, 5f, Color.Orange, 12, 1f);
        }

        if (c.Truncation != CorridorTruncation.Horizon)
        {
            float x = c.ColumnEdgeX(c.TruncationColumn);
            // Post spans the local gate band so it reads against the geometry that killed the scan.
            float yTop = c.WindowTop, yBot = c.WindowBottom;
            if (c.ColumnCount > 0)
            {
                int last = c.ColumnCount - 1;
                yBot = c.FloorY[last] + 8f;
                yTop = float.IsNegativeInfinity(c.CeilY[last]) ? c.FloorY[last] - 40f : c.CeilY[last] - 8f;
            }
            DrawLine(new Vector2(x, yTop), new Vector2(x, yBot), Color.Red * 0.85f, 2);
            // Reason tick: one short stub per truncation kind (P/T/N have 1/2/3 stubs — cheap
            // to read without text rendering).
            int stubs = c.Truncation switch
            {
                CorridorTruncation.Pinch    => 1,
                CorridorTruncation.TallRise => 2,
                _                           => 3,   // NoFloor
            };
            for (int s = 0; s < stubs; s++)
            {
                float y = yTop + 4f + s * 5f;
                DrawLine(new Vector2(x, y), new Vector2(x + c.Dir * 7f, y), Color.Red * 0.85f, 2);
            }
        }
    }

    // Translucent fill + crisp outline. Color owned by the publisher (Hitbox.DebugColor).
    public void DrawHitbox(Hitbox hb)
    {
        var color = hb.DebugColor;
        var rect = new Rectangle(
            (int)hb.Region.Left, (int)hb.Region.Top,
            (int)(hb.Region.Right - hb.Region.Left),
            (int)(hb.Region.Bottom - hb.Region.Top));

        if (hb.Shape != null)
        {
            _spriteBatch.Draw(_pixel, rect, color * 0.12f);
            var verts = hb.Shape.GetVertices(hb.ShapePos, hb.ShapeRotation);
            for (int i = 0; i < verts.Length; i++)
                DrawLine(verts[i], verts[(i + 1) % verts.Length], color, 1);
        }
        else
        {
            _spriteBatch.Draw(_pixel, rect, color * 0.35f);
            var tl = new Vector2(hb.Region.Left,  hb.Region.Top);
            var tr = new Vector2(hb.Region.Right, hb.Region.Top);
            var br = new Vector2(hb.Region.Right, hb.Region.Bottom);
            var bl = new Vector2(hb.Region.Left,  hb.Region.Bottom);
            DrawLine(tl, tr, color, 1);
            DrawLine(tr, br, color, 1);
            DrawLine(br, bl, color, 1);
            DrawLine(bl, tl, color, 1);
        }
    }

    // Defensive region outline (cyan), always an axis-aligned AABB.
    public void DrawHurtbox(Hurtbox hb)
    {
        var color = Color.Cyan;
        var rect = new Rectangle(
            (int)hb.Region.Left, (int)hb.Region.Top,
            (int)(hb.Region.Right - hb.Region.Left),
            (int)(hb.Region.Bottom - hb.Region.Top));
        _spriteBatch.Draw(_pixel, rect, color * 0.18f);
        var tl = new Vector2(hb.Region.Left,  hb.Region.Top);
        var tr = new Vector2(hb.Region.Right, hb.Region.Top);
        var br = new Vector2(hb.Region.Right, hb.Region.Bottom);
        var bl = new Vector2(hb.Region.Left,  hb.Region.Bottom);
        DrawLine(tl, tr, color, 1);
        DrawLine(tr, br, color, 1);
        DrawLine(br, bl, color, 1);
        DrawLine(bl, tl, color, 1);
    }

    public void DrawLine(Vector2 start, Vector2 end, Color color, int thickness = 2)
    {
        var edge = end - start;
        float angle = MathF.Atan2(edge.Y, edge.X);
        _spriteBatch.Draw(_pixel, start, null, color, angle, Vector2.Zero,
            new Vector2(edge.Length(), thickness), SpriteEffects.None, 0f);
    }
}
