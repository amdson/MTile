using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Colours shared by every plant figure, telegraph and HUD swatch. Plain Color values (no
// graphics types), so sim-side Telegraph code may read them too.
public static class PlantPalette
{
    public static readonly Color Stem     = new(70, 150, 70);
    public static readonly Color StemDark = new(40, 95, 50);
    public static readonly Color Leaf     = new(95, 190, 80);
    public static readonly Color LeafDark = new(55, 125, 55);
    public static readonly Color Soil     = new(120, 85, 55);
    public static readonly Color Seed     = new(190, 160, 95);
    public static readonly Color Spit     = new(170, 255, 90);
    public static readonly Color Dormant  = new(150, 165, 150);
    public static readonly Color Wilt     = new(170, 130, 70);
    public static readonly Color Legal    = new(120, 255, 140);
    public static readonly Color Illegal  = new(255, 90, 80);

    // The owner's colour: the same per-team palette spikes use, so a player's spikes
    // and plants read as one set.
    public static Color Team(int team) => team switch
    {
        0 => new Color(120, 200, 255),
        1 => new Color(255, 120, 110),
        2 => new Color(200, 120, 255),
        3 => new Color(140, 235, 130),
        _ => new Color(230, 230, 230),
    };

    // Seed-bar swatch colour per kind (HUD). One line each — M3-M6 may retint theirs.
    public static Color SeedSwatch(SeedKind k) => k switch
    {
        SeedKind.Spitter    => new Color(120, 200, 80),
        SeedKind.Snapper    => new Color(200, 90, 120),
        SeedKind.Thorns     => new Color(170, 170, 120),
        SeedKind.SiegePod   => new Color(230, 160, 60),
        SeedKind.BlockEater => new Color(150, 90, 200),
        _                   => Color.Gray,
    };
}

// Building blocks for the code-built plant figures (Drawing/Plants/*Figure.cs). Every
// figure is drawn UPRIGHT in its cell's local frame: origin at the cell centre, root face
// along y = +RootY (the bottom of the cell), growing toward −y. PlantEntity.SyncSprite
// rotates the sprite onto the real root face, so no figure ever deals with walls or
// ceilings. Shared here so the next four figures look like one family.
public static class PlantFigureKit
{
    public const float RootY = Chunk.TileSize * 0.5f;

    public static Color Scale(Color c, float k)
        => new((int)MathHelper.Clamp(c.R * k, 0, 255), (int)MathHelper.Clamp(c.G * k, 0, 255),
               (int)MathHelper.Clamp(c.B * k, 0, 255), c.A);

    // A curved stem: a quadratic from `from` to `to` bowed sideways by `bend` px, as
    // `segments` lines tapering from `thick0` to `thick1`. Returns the tip direction.
    public static Vector2 Stem(Pose p, Vector2 from, Vector2 to, float bend, Color color,
                               float thick0 = 2f, float thick1 = 1.2f, int segments = 4)
    {
        var mid = (from + to) * 0.5f;
        var axis = to - from;
        var normal = axis.LengthSquared() > 1e-4f ? Vector2.Normalize(new Vector2(-axis.Y, axis.X)) : Vector2.UnitX;
        var ctrl = mid + normal * bend;
        Vector2 prev = from, dir = Vector2.UnitY * -1f;
        for (int i = 1; i <= segments; i++)
        {
            float t = i / (float)segments;
            var q = (1 - t) * (1 - t) * from + 2 * (1 - t) * t * ctrl + t * t * to;
            p.Line(prev, q, color, MathHelper.Lerp(thick0, thick1, t));
            if ((q - prev).LengthSquared() > 1e-4f) dir = Vector2.Normalize(q - prev);
            prev = q;
        }
        return dir;
    }

    // A lancet leaf from `at` along `angle` (radians, 0 = +x, y-down): a filled blade and
    // a darker midrib. `curl` 0..1 shortens and fattens it (closed / drooping leaves).
    public static void Leaf(Pose p, Vector2 at, float angle, float length, float width, Color color, Color vein,
                            float curl = 0f)
    {
        float len = length * (1f - 0.45f * curl);
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var c = at + dir * (len * 0.5f);
        p.Box(c, new Vector2(len, width * (1f + 0.4f * curl)), angle, color);
        p.Box(at + dir * (len * 0.85f), new Vector2(len * 0.35f, width * 0.6f), angle, color);
        p.Line(at, at + dir * (len * 0.9f), vein, 0.8f);
    }

    // The same leaf as an outline only (dormant / ghost variants that must read through
    // a covering block without hiding it).
    public static void LeafOutline(Pose p, Vector2 at, float angle, float length, float width, Color color)
    {
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var n = new Vector2(-dir.Y, dir.X) * (width * 0.5f);
        var tip = at + dir * length;
        var mid = at + dir * (length * 0.5f);
        p.Line(at, mid + n, color, 1f).Line(mid + n, tip, color, 1f)
         .Line(at, mid - n, color, 1f).Line(mid - n, tip, color, 1f);
    }

    // A little mound of disturbed soil at the root face, so a seedling reads as planted.
    public static void SoilTuft(Pose p, float spread = 1f)
    {
        float y = RootY - 0.5f;
        p.Box(new Vector2(0f, y), new Vector2(7f * spread, 1.6f), 0f, PlantPalette.Soil);
        p.Box(new Vector2(-1.5f * spread, y - 1f), new Vector2(2.5f * spread, 1.2f), 0.3f, PlantPalette.Soil);
        p.Box(new Vector2(1.8f * spread, y - 0.8f), new Vector2(2f * spread, 1.1f), -0.4f, PlantPalette.Soil);
    }

    // A team-coloured collar band across the stem — the owner mark every plant carries.
    public static void Collar(Pose p, Vector2 at, Vector2 stemDir, Color team, float width = 3.2f)
    {
        float a = MathF.Atan2(stemDir.Y, stemDir.X) + MathHelper.PiOver2;
        p.Box(at, new Vector2(width, 1.4f), a, team);
    }
}

// Converts a figure Pose into TelegraphList shapes (render-only), for the cursor ghost
// and any overlay that wants a plant's silhouette without its Sprite. Every shape is
// recoloured to `tint` (alpha-scaled by `alpha`), so the silhouette reads as one colour.
public static class PoseTelegraph
{
    public static void Emit(TelegraphList t, Pose pose, Vector2 position, float rotation, Color tint)
    {
        if (pose == null) return;
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        Vector2 W(Vector2 v) => position + new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
        foreach (var sh in pose.Shapes)
        {
            switch (sh)
            {
                case LineShape l: t.Line(W(l.A), W(l.B), tint, l.Thickness); break;
                case RingShape r: t.Ring(W(r.Center), r.Radius, tint, r.Segments, r.Thickness); break;
                case DiscShape d: t.Disc(W(d.Center), d.Radius, tint); break;
                case BoxShape b:  t.RotatedRect(W(b.Center), b.Size, b.LocalRotation + rotation, tint); break;
            }
        }
    }
}
