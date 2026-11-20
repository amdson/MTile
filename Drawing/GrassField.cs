using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Grass caps on dirt, spreading the way it does in Terraria / Minecraft: terrain that is
// there when a stage loads starts grassed, freshly exposed or freshly placed dirt starts
// bare, and grass creeps onto a bare cell from a grassy neighbour, slowly. A dirt cell
// can hold grass only while the cell above it is not solid; bury it and the grass dies.
// Dirt only — stone, sand and the rest stay bare.
//
// RENDER-ONLY, on purpose: the grass is a cap drawn on the tile, not a tile type. It is
// never read by the sim, so it needs no snapshot and cannot desync rollback — the cost is
// that two peers see slightly different grass, since each rolls its own spread.
// Eligibility is re-tested against the live terrain every draw, so a rollback that
// rewrites tiles is simply reflected on the next frame.
public sealed class GrassField
{
    // Mean seconds for grass to take hold on a bare cell that has one grassy neighbour.
    private const float SpreadSeconds = 10f;
    // Seconds for a newly started cell to fill in completely.
    private const float GrowSeconds = 5f;
    // Mean seconds for a bare cell with NO grassy neighbour to sprout on its own. Without
    // this a stage with no dirt in its starting terrain (the stone corridor) could never
    // grow any: there would be nothing for placed dirt to catch grass from.
    private const float SelfSeedSeconds = 45f;
    // A neighbour must be at least this grown before it can seed the next cell, so a
    // front advances cell by cell instead of racing along a floor in one tick.
    private const float SeedMaturity = 0.6f;
    // Spread bookkeeping rate. Growth is cosmetic and slow, so a few Hz is plenty.
    private const float TickSeconds = 0.25f;

    // Rows of the tile's top edge the turf covers (it replaces the outline + highlight).
    private const int TurfPx = 3;
    // Tallest blade standing above the surface, px.
    private const int BladeMaxPx = 2;

    private static readonly Color Turf      = new(104, 150, 62);
    private static readonly Color TurfDark  = new(66, 108, 48);    // the ragged lower fringe
    private static readonly Color Blade     = new(142, 186, 78);   // tips catching the light

    // (gtx, gty) → growth 0..1. Only eligible dirt cells are ever in here.
    private readonly Dictionary<(int, int), float> _growth = new();
    // Chunks whose starting terrain has been grassed. A chunk is seeded exactly once, the
    // first tick it exists — later exposure has to earn its grass by spreading.
    private readonly HashSet<Point> _seeded = new();
    private readonly List<(int, int)> _scratch = new();
    private readonly Random _rng = new(12345);
    private ChunkMap _map;
    private float _lastTime = float.NaN, _tickDebt;

    public void Update(ChunkMap chunks, float time)
    {
        // A different map is a different stage: nothing carries over.
        if (!ReferenceEquals(chunks, _map))
        {
            _map = chunks;
            _growth.Clear();
            _seeded.Clear();
            _tickDebt = TickSeconds;   // seed on the very first frame
        }
        float dt = float.IsNaN(_lastTime) ? 0f : MathHelper.Clamp(time - _lastTime, 0f, 0.5f);
        _lastTime = time;
        _tickDebt += dt;
        if (_tickDebt < TickSeconds) return;
        float step = _tickDebt;
        _tickDebt = 0f;
        Tick(chunks, step);
    }

    private static bool Eligible(ChunkMap chunks, int gtx, int gty)
        => chunks.GetCellState(gtx, gty - 1) != TileState.Solid;

    private void Tick(ChunkMap chunks, float step)
    {
        // Existing grass: die if buried or dug out, otherwise keep filling in.
        _scratch.Clear();
        foreach (var kv in _growth) _scratch.Add(kv.Key);
        foreach (var cell in _scratch)
        {
            var (gtx, gty) = cell;
            if (!IsDirt(chunks, gtx, gty) || !Eligible(chunks, gtx, gty)) { _growth.Remove(cell); continue; }
            float g = _growth[cell];
            if (g < 1f) _growth[cell] = MathF.Min(1f, g + step / GrowSeconds);
        }

        // New grass. Starts are collected and applied after the scan so a cell seeded
        // this tick cannot seed its own neighbour in the same pass.
        _scratch.Clear();
        float pSpread = 1f - MathF.Exp(-step / SpreadSeconds);
        float pSelf   = 1f - MathF.Exp(-step / SelfSeedSeconds);
        foreach (var chunk in chunks)
        {
            bool fresh = _seeded.Add(chunk.ChunkPos);
            int baseX = chunk.ChunkPos.X * Chunk.Size, baseY = chunk.ChunkPos.Y * Chunk.Size;
            for (int tx = 0; tx < Chunk.Size; tx++)
                for (int ty = 0; ty < Chunk.Size; ty++)
                {
                    ref var t = ref chunk.Tiles[tx, ty];
                    if (!t.IsSolid || t.Type != TileType.Dirt) continue;
                    // The cell above is usually in this chunk; only the top row pays for
                    // a map lookup.
                    bool open = ty > 0 ? !chunk.Tiles[tx, ty - 1].IsSolid
                                       : Eligible(chunks, baseX + tx, baseY);
                    if (!open) continue;
                    var cell = (baseX + tx, baseY + ty);
                    if (_growth.ContainsKey(cell)) continue;
                    if (fresh) { _growth[cell] = 1f; continue; }
                    float p = HasGrassyNeighbour(cell.Item1, cell.Item2) ? pSpread : pSelf;
                    if (_rng.NextDouble() < p) _scratch.Add(cell);
                }
        }
        foreach (var cell in _scratch) _growth[cell] = 0.05f;
    }

    private static bool IsDirt(ChunkMap chunks, int gtx, int gty)
        => chunks.GetCellState(gtx, gty) == TileState.Solid && chunks.GetCellType(gtx, gty) == TileType.Dirt;

    // Sideways, and one step up or down, so grass climbs stairs and slopes.
    private bool HasGrassyNeighbour(int gtx, int gty)
    {
        for (int dx = -1; dx <= 1; dx += 2)
            for (int dy = -1; dy <= 1; dy++)
                if (_growth.TryGetValue((gtx + dx, gty + dy), out float g) && g >= SeedMaturity)
                    return true;
        return false;
    }

    // Caps for the visible cells. Runs in the world-space SpriteBatch pass right after
    // the tile fills. Everything is `pixel` rects, so it costs no texture switch.
    public void Draw(SpriteBatch batch, Texture2D pixel, ChunkMap chunks,
                     int minGtx, int minGty, int maxGtx, int maxGty)
    {
        const int T = Chunk.TileSize;
        foreach (var kv in _growth)
        {
            var (gtx, gty) = kv.Key;
            if (gtx < minGtx || gtx > maxGtx || gty < minGty || gty > maxGty + 1) continue;
            // Live test, not the last tick's: a cap must vanish the frame its tile is
            // broken or built over, not a quarter-second later.
            if (!IsDirt(chunks, gtx, gty) || !Eligible(chunks, gtx, gty)) continue;
            if (chunks.Charge.IsCharged(gtx, gty)) continue;   // the charged border must stay readable

            float g = kv.Value;
            float shade = TileVariation.Brightness(TileType.Dirt, gtx, gty);
            var turf  = Shade(Turf, shade);
            var dark  = Shade(TurfDark, shade);
            var blade = Shade(Blade, shade);
            int x = gtx * T, y = gty * T;

            // A grown cell is one band; a growing one fills in column by column, in a
            // hashed order, so the cap thickens like growth instead of fading like a decal.
            bool full = g >= 1f;
            if (full) batch.Draw(pixel, new Rectangle(x, y, T, TurfPx), turf);
            uint h = Hash(gtx, gty);
            for (int c = 0; c < T; c++)
            {
                uint hc = Hash((int)h + c * 7919, c);
                if (!full)
                {
                    if ((hc & 0xFF) / 255f > g) continue;
                    batch.Draw(pixel, new Rectangle(x + c, y, 1, TurfPx), turf);
                }
                // Ragged lower fringe: some columns hang a pixel further down.
                uint r = (hc >> 8) & 7;
                if (r < 4) batch.Draw(pixel, new Rectangle(x + c, y + TurfPx, 1, 1), r < 2 ? dark : turf);
                else       batch.Draw(pixel, new Rectangle(x + c, y + TurfPx - 1, 1, 1), dark);
                // Blades standing above the surface, in the open cell.
                int bh = (int)((hc >> 12) % (uint)(BladeMaxPx + 2)) - 1;   // -1..BladeMaxPx
                if (bh > 0) batch.Draw(pixel, new Rectangle(x + c, y - bh, 1, bh), blade);
            }
        }
    }

    // Growth on a cell, 0 when it has none. The caller owns the live eligibility test.
    public float GrowthAt(int gtx, int gty) => _growth.TryGetValue((gtx, gty), out float g) ? g : 0f;

    // Tallest tuft, world px, at full growth and scale 1.
    private const float TuftHeight = 7f;
    // Back tufts per grassed cell.
    private const int TuftsPerCell = 3;
    // Tuft bases sit this far below the surface line, so they grow out of the turf wash.
    private const float TuftSink = 0.7f;

    // Drawn tufts for the ink look. Back tufts (front: false) go down with the terrain;
    // front tufts are a sparser, shorter set drawn after the characters.
    public void DrawTufts(SpriteBatch batch, GrassTuftSheet sheet, ChunkMap chunks, TerrainLightField light,
                          int minGtx, int minGty, int maxGtx, int maxGty, bool front)
    {
        const int T = Chunk.TileSize;
        float unit = TuftHeight / sheet.MaxHeight;
        foreach (var kv in _growth)
        {
            var (gtx, gty) = kv.Key;
            if (gtx < minGtx - 1 || gtx > maxGtx + 1 || gty < minGty || gty > maxGty + 1) continue;
            if (!IsDirt(chunks, gtx, gty) || !Eligible(chunks, gtx, gty)) continue;
            if (chunks.Charge.IsCharged(gtx, gty)) continue;

            float g = kv.Value;
            // Lit like the air they stand in.
            float b = TerrainLightField.Brightness(light.LightAt(gtx, gty - 1))
                    * TileVariation.Brightness(TileType.Dirt, gtx, gty);
            // Pulled toward the turf wash's green, so tufts and band read as one plant.
            var tint = new Color(0.80f * b, 0.93f * b, 0.70f * b, 1f);
            uint h = Hash(gtx, gty);
            int count = front ? ((h & 3) == 0 ? 0 : 1) : TuftsPerCell;
            for (int i = 0; i < count; i++)
            {
                uint hi = Hash((int)h + i * 7919 + (front ? 104729 : 0), i);
                var src = sheet.Tufts[(int)(hi % (uint)sheet.Tufts.Length)];
                float r1 = ((hi >> 8) & 0xFF) / 255f, r2 = ((hi >> 16) & 0xFF) / 255f;
                float scale = unit * (0.75f + 0.5f * r1) * (0.35f + 0.65f * g) * (front ? 0.6f : 1f);
                float cx = gtx * T + (i + 0.15f + 0.7f * r2) / count * T;
                batch.Draw(sheet.Texture, new Vector2(cx, gty * T + TuftSink + (front ? 0.6f : 0f)), src, tint, 0f,
                    new Vector2(src.Width * 0.5f, src.Height), scale,
                    (hi & 1) != 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
            }
        }
    }

    private static Color Shade(Color c, float f) => new(
        (int)Math.Min(255f, c.R * f), (int)Math.Min(255f, c.G * f), (int)Math.Min(255f, c.B * f), 255);

    private static uint Hash(int a, int b)
    {
        uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return h;
    }
}
