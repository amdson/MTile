using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Block-eater tile art (Plans/PLANT_SEEDS_PLAN.md §4.5, §10). Render-only: reads
// ChunkMap.BlockEater / Damage / TerrainFrame and writes nothing back into the sim.
//
// What it has to say, at gameplay zoom:
//   * "a plant is eating this block" — creeping vines/roots over the cell, a blighted wash
//     in the EATER's colour (not the block owner's; the owner's ring is under it);
//   * "how far gone" — vine count, length and thickness grow with the cell's damage, and
//     thick rootlets split the block as it nears breaking;
//   * "where it came from / where it is" — vines creep in from the edge the infection
//     entered by (BlockEaterInfection.Cell.From), so a spreading front reads directionally;
//     the planted cell is the HEART: a pulsing bulb with a maw, vines radiating out of it,
//     and a ring that swells on the spread cadence (the next-spread telegraph).
// Code-built from DrawContext lines / rotated rects, so it is identical on DesktopGL and KNI.
public sealed class BlockEaterRenderer
{
    private readonly DrawContext _dc;

    // Plant green, pulled toward the eater's team colour so two players' eaters differ.
    private static readonly Color PlantGreen = new(78, 160, 62);
    private static readonly Color TeamP1     = new(90, 200, 255);   // matches ChunkRenderer's owner P1
    private static readonly Color TeamP2     = new(255, 140, 60);   // matches ChunkRenderer's owner P2
    private const float TeamPull   = 0.38f;   // vine = lerp(green, team, this)
    private const float BlightLo   = 0.10f;   // cell wash alpha at no damage …
    private const float BlightHi   = 0.38f;   // … and at the break point
    private const int   CreepFrames = 24;     // vines grow in over this many frames after infection
    private const int   VineMin = 2, VineMax = 6;
    private const int   VineSegs = 4;

    public BlockEaterRenderer(SpriteBatch sb, Texture2D pixel) => _dc = new DrawContext(sb, pixel);

    public static Color TeamColor(byte team) => team == BlockOwnership.Player2 ? TeamP2 : TeamP1;

    public void Draw(ChunkMap chunks, Camera camera, Viewport viewport, float time)
    {
        var eater = chunks.BlockEater;
        if (eater.Count == 0) return;
        const float T = Chunk.TileSize;
        float halfW = viewport.Width  / (2f * camera.Zoom) + T;
        float halfH = viewport.Height / (2f * camera.Zoom) + T;
        int now = chunks.TerrainFrame;

        // Two passes so a neighbour's vines never paint over a heart.
        for (int pass = 0; pass < 2; pass++)
        foreach (var kv in eater.Entries)
        {
            var (gtx, gty) = kv.Key;
            var c = kv.Value;
            if (chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
            float left = gtx * T, top = gty * T;
            if (MathF.Abs(left - camera.Position.X) > halfW || MathF.Abs(top - camera.Position.Y) > halfH) continue;
            bool heart = c.IsHeart(gtx, gty);
            if ((pass == 1) != heart) continue;

            float maxHp = TileDamage.MaxHPFor(chunks.GetCellType(gtx, gty));
            float dmg   = maxHp > 0f ? Math.Clamp(chunks.Damage.Get(gtx, gty) / maxHp, 0f, 1f) : 0f;
            float creep = Math.Clamp((now - c.InfectedFrame) / (float)CreepFrames, 0f, 1f);
            DrawCell(gtx, gty, left, top, c, heart, dmg, creep, now, time);
        }
    }

    private void DrawCell(int gtx, int gty, float left, float top, BlockEaterInfection.Cell c,
                          bool heart, float dmg, float creep, int now, float time)
    {
        const float T = Chunk.TileSize;
        var team  = TeamColor(c.Team);
        var vine  = Color.Lerp(PlantGreen, team, TeamPull);
        var dark  = Color.Lerp(vine, Color.Black, 0.65f);
        var light = Color.Lerp(vine, Color.White, 0.35f);

        // Blight wash: the block looks sick before it looks overgrown.
        float wash = MathHelper.Lerp(BlightLo, BlightHi, dmg) * MathF.Max(creep, 0.3f);
        _dc.Box(new Vector2(left, top), new Vector2(T - 1, T - 1), Color.Lerp(dark, team, 0.25f) * wash);

        float density = 0.3f + 0.7f * dmg;
        int n = VineMin + (int)MathF.Round((VineMax - VineMin) * density);
        if (heart) n += 1;

        // Entry edge + inward direction.
        Vector2 dir, edgeA, edgeB;
        switch (c.From)
        {
            case BlockEaterInfection.FromAbove: dir = new(0, 1);  edgeA = new(left, top);         edgeB = new(left + T, top);     break;
            case BlockEaterInfection.FromBelow: dir = new(0, -1); edgeA = new(left, top + T);     edgeB = new(left + T, top + T); break;
            case BlockEaterInfection.FromLeft:  dir = new(1, 0);  edgeA = new(left, top);         edgeB = new(left, top + T);     break;
            case BlockEaterInfection.FromRight: dir = new(-1, 0); edgeA = new(left + T, top);     edgeB = new(left + T, top + T); break;
            default:                            dir = Vector2.Zero; edgeA = edgeB = new(left + T * 0.5f, top + T * 0.5f); break;
        }

        float vineThick = 1f + dmg;   // thickens as it bites in
        for (int i = 0; i < n; i++)
        {
            float h0 = Hash01(gtx, gty, i, 0), h1 = Hash01(gtx, gty, i, 1), h2 = Hash01(gtx, gty, i, 2);
            Vector2 start, d;
            if (heart)
            {
                float a = MathHelper.TwoPi * (i + 0.35f * h0) / n;
                d = new Vector2(MathF.Cos(a), MathF.Sin(a));
                start = edgeA + d * (T * 0.18f);
            }
            else
            {
                start = Vector2.Lerp(edgeA, edgeB, 0.15f + 0.7f * h0);
                // Splay a little off the inward axis so vines fan out.
                var perp0 = new Vector2(-dir.Y, dir.X);
                d = Vector2.Normalize(dir + perp0 * (h1 - 0.5f) * 0.9f);
            }
            float len = T * (heart ? 0.32f + 0.18f * density : 0.45f + 0.5f * density) * (0.7f + 0.3f * h2) * creep;
            if (len < 0.5f) continue;
            var perp = new Vector2(-d.Y, d.X);
            float wig = T * 0.10f * (0.6f + h1);
            float sway = 0.35f * MathF.Sin(time * 1.3f + h0 * 6.28f);

            Vector2 prev = start;
            for (int s = 1; s <= VineSegs; s++)
            {
                float f = s / (float)VineSegs;
                var p = start + d * (len * f)
                      + perp * (MathF.Sin(f * 5.2f + h2 * 6.28f) * wig + sway * f);
                float th = MathF.Max(1f, vineThick * (1.15f - 0.5f * f));
                _dc.Line(prev, p, dark, th + 1f);
                _dc.Line(prev, p, vine, th);
                // A leaf or thorn bud at alternating joints.
                if (s < VineSegs && ((s + i) & 1) == 0)
                {
                    float side = (s & 2) == 0 ? 1f : -1f;
                    var leaf = p + perp * side * 1.2f;
                    float ang = MathF.Atan2(d.Y, d.X) + side * 0.8f;
                    _dc.RotatedRect(leaf, new Vector2(2.2f, 1.1f), ang, light);
                }
                prev = p;
            }

            // Rootlets: as the cell nears breaking, short dark cracks branch off the vine.
            if (dmg > 0.45f && i % 2 == 0)
            {
                var mid = start + d * (len * 0.5f);
                var rd  = Vector2.Normalize(d + perp * (h0 > 0.5f ? 1.2f : -1.2f));
                _dc.Line(mid, mid + rd * (T * 0.3f * (dmg - 0.3f)), dark, 1f);
            }
        }

        if (heart) DrawHeart(left, top, c, team, vine, dark, light, dmg, now, time);
    }

    // The planted cell: a bulb with a maw that gapes as the block is eaten, and a ring that
    // swells over each spread interval and snaps back when the patch spreads.
    private void DrawHeart(float left, float top, BlockEaterInfection.Cell c, Color team,
                           Color vine, Color dark, Color light, float dmg, int now, float time)
    {
        const float T = Chunk.TileSize;
        var ctr = new Vector2(left + T * 0.5f, top + T * 0.5f);
        float beat = 0.5f + 0.5f * MathF.Sin(time * 5.0f);
        float r = T * (0.26f + 0.04f * beat);

        // Spread telegraph: progress through the current spread interval.
        int age = Math.Max(0, now - c.InfectedFrame);
        float cyc = (age % BlockEaterInfection.SpreadIntervalFrames) / (float)BlockEaterInfection.SpreadIntervalFrames;
        _dc.Ring(ctr, r + T * 0.45f * cyc, team * (0.75f * (1f - cyc)), 10, 1f);

        // Bulb: dark rim, body (an octagon from two rotated squares), highlight.
        _dc.RotatedRect(ctr, new Vector2(2f * r + 2f, 2f * r + 2f), 0f, dark);
        _dc.RotatedRect(ctr, new Vector2(2f * r + 2f, 2f * r + 2f), MathHelper.PiOver4, dark);
        var body = Color.Lerp(vine, team, 0.45f + 0.2f * beat);
        _dc.RotatedRect(ctr, new Vector2(2f * r, 2f * r), 0f, body);
        _dc.RotatedRect(ctr, new Vector2(2f * r, 2f * r), MathHelper.PiOver4, body);
        _dc.RotatedRect(ctr + new Vector2(-r * 0.4f, -r * 0.45f), new Vector2(r * 0.6f, r * 0.45f), -0.5f, light);

        // Maw: a dark slit that opens as the heart's own block gives way.
        float gape = 1f + 2.2f * dmg + 0.6f * beat;
        _dc.RotatedRect(ctr + new Vector2(0f, r * 0.2f), new Vector2(r * 1.3f, gape), 0f, Color.Black * 0.85f);
        // Two teeth over the maw.
        _dc.RotatedRect(ctr + new Vector2(-r * 0.3f, r * 0.2f - gape * 0.5f + 0.5f), new Vector2(1f, 1.5f), 0f, light);
        _dc.RotatedRect(ctr + new Vector2( r * 0.3f, r * 0.2f - gape * 0.5f + 0.5f), new Vector2(1f, 1.5f), 0f, light);
    }

    // Stable per-cell, per-vine pseudo-random in [0, 1). Render-only.
    private static float Hash01(int gtx, int gty, int i, int salt)
    {
        uint h = (uint)(gtx * 73856093) ^ (uint)(gty * 19349663) ^ (uint)(i * 83492791) ^ (uint)(salt * 2654435761u);
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777216f;
    }
}
