using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Thorn-patch spike tiles (Plans/PLANT_SEEDS_PLAN.md §4.3, §10), drawn as a small
// code-built bramble instead of ChunkRenderer's stone teeth, so a grown thorn reads as a
// PLANT and as distinct from a hand-placed spike at a glance. Render-only: reads
// ChunkMap.Thorns / SpikeSeeds / the grid, writes nothing.
//
// One cell = one clump standing on its supporting face (below preferred, then a side,
// then above — the order ChunkRenderer's spike seeds use), drawn in the face's frame:
//   • canes  — two arched woody stems, crossing, in a dark bark tone leaning a little
//     toward the owner's team colour;
//   • thorns — short hooked barbs along the canes in the team colour (the part that
//     hurts, so the part that carries the side);
//   • leaves — a couple of dark-green flecks;
//   • runner — when the next cell along the same face is a thorn too, a low stem runs
//     to the cell edge so a patch reads as one bramble, not a row of tufts.
// The SEED cell is bigger, rooted on a woody knot, and carries a team-tinted bud with a
// pale eye — the cell an attacker wants to knock off to stop the spread.
// Arming telegraph: while a thorn cell is still seeding (TileSpikeSeeds) its canes grow
// from a stub to full height and carry no barbs; in the flash phase the barbs blink
// white, mirroring the plain spike seed's last warning. Offshoots of a patch whose seed
// is gone are drawn withered (desaturated), telling both players it won't spread again.
public sealed class ThornRenderer
{
    private static readonly Color Bark     = new(78, 58, 40);
    private static readonly Color BarkDark = new(48, 36, 26);
    private static readonly Color Leaf     = new(58, 108, 52);
    private static readonly Color BudEye   = new(250, 240, 210);
    private const float BarkTeamTint = 0.22f;
    private const float WitherGrey   = 0.55f;
    private const float StubScale    = 0.3f;   // cane height at the instant of seeding
    private const int   FlashBlinkFrames = 3;

    private DrawContext _ctx;

    public void Draw(SpriteBatch sb, Texture2D pixel, ChunkMap chunks,
                     Vector2 viewCenter, float halfW, float halfH)
    {
        if (chunks.Thorns.CellCount == 0) return;
        if (_ctx == null || _ctx.SpriteBatch != sb || _ctx.Pixel != pixel) _ctx = new DrawContext(sb, pixel);
        int now = chunks.TerrainFrame;
        var life = TileSpikeSeeds.Lifecycle;
        const float T = Chunk.TileSize;

        foreach (var (cell, _) in chunks.Thorns.CellEntries)
        {
            var (gtx, gty) = cell;
            if (chunks.GetCellState(gtx, gty) != TileState.Spike) continue;
            float cx = (gtx + 0.5f) * T, cy = (gty + 0.5f) * T;
            if (MathF.Abs(cx - viewCenter.X) > halfW || MathF.Abs(cy - viewCenter.Y) > halfH) continue;
            if (!chunks.Thorns.TryGetCell(gtx, gty, out var patch)) continue;

            // Support face: n = outward normal of the supporting block (into this cell).
            Vector2 n;
            if      (chunks.GetCellState(gtx, gty + 1) == TileState.Solid) n = new Vector2(0, -1);
            else if (chunks.GetCellState(gtx + 1, gty) == TileState.Solid) n = new Vector2(-1, 0);
            else if (chunks.GetCellState(gtx - 1, gty) == TileState.Solid) n = new Vector2(1, 0);
            else if (chunks.GetCellState(gtx, gty - 1) == TileState.Solid) n = new Vector2(0, 1);
            else continue;
            var t = new Vector2(-n.Y, n.X);                      // along the face
            var baseC = new Vector2(cx, cy) - n * (T * 0.5f);    // centre of the supporting face

            // Growth / telegraph from the arming seed.
            float grow = 1f; bool barbs = true; bool flashOn = false;
            if (chunks.SpikeSeeds.TryGet(gtx, gty, out int seeded))
            {
                int el = now - seeded;
                grow   = MathHelper.Lerp(StubScale, 1f, Math.Clamp(el / (float)life.FramesToGrown, 0f, 1f));
                var ph = life.PhaseAt(el);
                barbs  = ph >= CrystalPhase.Flash;
                flashOn = barbs && ((el - life.SeedFrames) / FlashBlinkFrames) % 2 == 0;
            }

            bool seed = patch.SeedGtx == gtx && patch.SeedGty == gty;
            var team = TilePalette.SpikeColor(patch.Team);
            var bark = Color.Lerp(Bark, team, BarkTeamTint);
            var thorn = flashOn ? Color.White : Color.Lerp(team, Color.White, 0.15f);
            var leaf = Leaf;
            if (!patch.Spreading)
            {
                bark  = Wither(bark);
                thorn = flashOn ? thorn : Wither(thorn);
                leaf  = Wither(leaf);
            }

            // Per-cell variation (render-only): mirror + height jitter from the cell hash.
            uint h = (uint)(gtx * 73856093) ^ (uint)(gty * 19349663);
            float mirror = (h & 1) == 0 ? 1f : -1f;
            float jitter = ((h >> 3) & 7) / 7f;   // 0..1
            var ta = t * mirror;

            float scale = (seed ? 1.0f : 0.8f + 0.1f * jitter) * grow;

            // Runners to same-patch neighbours along the face.
            if (grow >= 1f)
                for (int s = -1; s <= 1; s += 2)
                {
                    int nx = gtx + (int)(t.X * s), ny = gty + (int)(t.Y * s);
                    if (!chunks.Thorns.TryGetCell(nx, ny, out var np) || np.Id != patch.Id) continue;
                    var a = baseC + n * 1.5f;
                    _ctx.Line(a, a + t * s * (T * 0.5f), bark, 1.5f);
                }

            if (seed)
            {
                // Woody knot the clump roots in.
                _ctx.Disc(baseC + n * 1.5f, 2.2f * MathF.Max(grow, 0.6f), BarkDark);
            }

            // Cane A: rises from one side, arches over, tips past the centre.
            var a0 = baseC + ta * (-0.32f * T);
            var a1 = baseC + ta * (-0.22f * T) + n * (0.55f * T * scale);
            var a2 = baseC + ta * (0.05f * T)  + n * (0.85f * T * scale);
            var a3 = baseC + ta * (0.30f * T)  + n * (0.62f * T * scale);
            Cane(a0, a1, a2, a3, bark, seed ? 1.6f : 1.3f);

            // Cane B: shorter, from the other side, crossing A.
            var b0 = baseC + ta * (0.28f * T);
            var b1 = baseC + ta * (0.22f * T)  + n * (0.42f * T * scale);
            var b2 = baseC + ta * (-0.02f * T) + n * (0.58f * T * scale);
            var b3 = baseC + ta * (-0.20f * T) + n * (0.40f * T * scale);
            Cane(b0, b1, b2, b3, bark, 1.1f);

            // Leaves.
            _ctx.Disc(Vector2.Lerp(a1, a2, 0.4f) + ta * 1.2f, 0.9f, leaf);
            _ctx.Disc(Vector2.Lerp(b1, b2, 0.5f) - n * 0.6f - ta * 1.0f, 0.8f, leaf);

            if (barbs)
            {
                float barb = (seed ? 2.6f : 2.2f) * grow;
                Barb(a0, a1, n, ta, 0.55f, barb, thorn, +1);
                Barb(a1, a2, n, ta, 0.50f, barb, thorn, -1);
                Barb(a2, a3, n, ta, 0.55f, barb, thorn, +1);
                Barb(b1, b2, n, ta, 0.45f, barb * 0.85f, thorn, +1);
                Barb(b2, b3, n, ta, 0.60f, barb * 0.85f, thorn, -1);
            }

            if (seed)
            {
                // The bud: a team-coloured head with a pale eye at the top of cane A.
                var bud = a2 + n * 0.8f;
                _ctx.Disc(bud, 1.8f * grow, flashOn ? Color.White : team);
                _ctx.Disc(bud, 0.7f * grow, BudEye);
            }
        }
    }

    private void Cane(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color c, float thick)
    {
        _ctx.Line(p0, p1, c, thick);
        _ctx.Line(p1, p2, c, thick * 0.9f);
        _ctx.Line(p2, p3, c, thick * 0.75f);
    }

    // A hooked barb on segment p→q at parameter u: a short spur leaving the cane on one
    // side (sideSign), leaning outward from the support so it reads as a thorn.
    private void Barb(Vector2 p, Vector2 q, Vector2 n, Vector2 ta, float u, float len, Color c, int sideSign)
    {
        var at = Vector2.Lerp(p, q, u);
        var d = q - p;
        if (d.LengthSquared() < 1e-4f) return;
        d.Normalize();
        var side = new Vector2(-d.Y, d.X) * sideSign;
        var dir = side * 0.8f + n * 0.45f + d * 0.25f;
        if (dir.LengthSquared() < 1e-4f) return;
        dir.Normalize();
        _ctx.Line(at, at + dir * len, c, 1f);
    }

    private static Color Wither(Color c)
    {
        float g = (c.R + c.G + c.B) / 3f;
        var grey = new Color((int)g, (int)g, (int)(g * 0.9f));
        return Color.Lerp(c, grey, WitherGrey);
    }
}
