using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// World-geometry pass: frustum-culled chunk tiles (tinted darker by accumulated
// damage), platform rects, live sprouts, and ghost outlines of queued sprouts.
// Runs inside Game1's world-space SpriteBatch pass — Begin/End is the caller's
// responsibility. Tile fills use TilePalette so colors match the HUD/particles.
public sealed class ChunkRenderer
{
    private readonly SpriteBatch    _spriteBatch;
    private readonly Texture2D      _pixel;
    private readonly Camera         _camera;
    private readonly GraphicsDevice _graphicsDevice;
    // Optional grain atlas for tile fills; null → flat palette fills.
    public TileTextureAtlas Atlas;

    // Tile fills are inset by a gutter so the grid reads; scales with tile size
    // (1px per 16px of tile, rounded up) instead of a fixed 1px.
    private const int TileGutter   = (Chunk.TileSize + 15) / 16;
    private const int TileDrawSize = Chunk.TileSize - TileGutter;

    // How far a charged tile's fill is pulled toward white. Charge is binary, so this is
    // one constant and not a ramp — the tint has to read as a state, not a quantity.
    private const float ChargeTint = 0.7f;

    public ChunkRenderer(SpriteBatch spriteBatch, Texture2D pixel, Camera camera,
                         GraphicsDevice graphicsDevice)
    {
        _spriteBatch    = spriteBatch;
        _pixel          = pixel;
        _camera         = camera;
        _graphicsDevice = graphicsDevice;
    }

    // The full world-geometry pass: tiles, platforms, then sprouts (live + pending).
    // `time` is render wall-clock seconds, used only for the pending-ghost pulse.
    public void Draw(Simulation sim, float time = 0f)
    {
        var chunks = sim.Chunks;

        foreach (var chunk in chunks)
            DrawChunk(chunk, chunks);

        DrawOwnership(chunks);
        DrawSpikes(chunks);
        DrawSpikeSeeds(chunks);
        DrawCrystalWave(chunks);

        foreach (var (r, color) in sim.Platforms)
            _spriteBatch.Draw(_pixel, new Rectangle(
                (int)r.Left, (int)r.Top,
                (int)(r.Right - r.Left), (int)(r.Bottom - r.Top)),
                color);

        DrawSprouts(chunks, time);
    }

    // ---- Sprouts: one "under construction" look (todo #6) -------------------------------
    //
    // A building cell has two visible parts, shared by both sprout states so they read as
    // one family and never as finished terrain:
    //   * the SLOT: corner brackets on the target cell, in the requester's colour. A
    //     Pending ghost is only the slot (faint fill, brackets breathing); a Growing
    //     sprout keeps its slot drawn so the destination is visible while it fills.
    //   * the VOLUME: a Growing sprout's moving quad(s), exactly the physics volumes --
    //     a flat (un-grained), lightened fill of the material, an accent rim, and a
    //     near-white 2px growth FRONT on the edge(s) advancing into the cell. Flat +
    //     rimmed + lit front is what separates it from a solid tile's grained,
    //     black-guttered fill, and it reads on frame one: a cell may only live 2-6
    //     frames at the current sprout speeds, so nothing here ramps in.
    // Accent = owner colour (BlockOwnership), so each player's build front is theirs.
    // Pure render: reads the sprout graph, writes nothing.

    private const float SproutFillWhite   = 0.35f;  // material -> white: lit, not yet solid
    private const float SproutRimWhite    = 0.25f;  // accent -> white for the volume rim
    private const float SproutFrontWhite  = 0.75f;  // accent -> white for the growth front
    private const float SlotGrowingAlpha  = 0.55f;  // the growing cell's destination brackets
    private const float GhostAlphaLo      = 0.40f;  // pending brackets breathe between these
    private const float GhostAlphaHi      = 0.85f;
    private const float GhostFillAlpha    = 0.10f;
    private const float GhostPulseHz      = 1.6f;
    private const float GhostPhasePerCell = 0.55f;  // radians; neighbours shimmer out of phase

    private static readonly Color SproutAccentNone = new(205, 225, 255);

    private static Color SproutAccent(byte owner) => owner switch
    {
        BlockOwnership.Player1 => OwnerP1,
        BlockOwnership.Player2 => OwnerP2,
        _                      => SproutAccentNone,
    };

    private void DrawSprouts(ChunkMap chunks, float time)
    {
        const float half = Chunk.TileSize * 0.5f;
        int bracket = Math.Max(2, Chunk.TileSize / 3);

        // Pending ghosts first, so a growing volume sliding over a neighbour's slot sits
        // on top of it.
        foreach (var s in chunks.PendingSprouts)
        {
            var accent = SproutAccent(s.Owner);
            float wave = 0.5f + 0.5f * MathF.Sin(
                time * GhostPulseHz * MathHelper.TwoPi + (s.Gtx + s.Gty) * GhostPhasePerCell);
            float a = MathHelper.Lerp(GhostAlphaLo, GhostAlphaHi, wave);
            int left = s.Gtx * Chunk.TileSize, top = s.Gty * Chunk.TileSize;
            _spriteBatch.Draw(_pixel, new Rectangle(left, top, TileDrawSize, TileDrawSize),
                accent * GhostFillAlpha);
            Brackets(left, top, TileDrawSize, bracket, accent * a);
        }

        foreach (var s in chunks.ActiveSprouts)
        {
            var accent = SproutAccent(s.Owner);
            Brackets(s.Gtx * Chunk.TileSize, s.Gty * Chunk.TileSize, TileDrawSize, bracket,
                accent * SlotGrowingAlpha);

            var fill  = Color.Lerp(TilePalette.BaseColor(s.Type), Color.White, SproutFillWhite);
            var rim   = Color.Lerp(accent, Color.White, SproutRimWhite);
            var front = Color.Lerp(accent, Color.White, SproutFrontWhite);

            // One quad per supporting face, matching the physics volumes exactly: a sprout
            // with two opposed parents visibly pushes out of both and the quads meet in the
            // middle. A corner sprout reports one combined volume for every face -- draw it
            // once.
            Vector2 last = new(float.NaN);
            foreach (var face in TileSproutNode.FaceOrder)
            {
                if ((s.Faces & face) == 0) continue;
                var c = s.VolumeCenter(face);
                if (c == last) continue;
                last = c;
                int left = (int)MathF.Round(c.X - half), top = (int)MathF.Round(c.Y - half);
                _spriteBatch.Draw(_pixel, new Rectangle(left, top, TileDrawSize, TileDrawSize), fill);
                Outline(left, top, TileDrawSize, rim);
                // Growth front: the edge(s) facing the direction of travel (away from the
                // parent). Derived from the volume's own velocity, so a combined diagonal
                // volume lights both of its leading edges.
                var v = s.VolumeVelocity(face);
                const int F = 2;
                if (v.Y < 0f) _spriteBatch.Draw(_pixel, new Rectangle(left, top, TileDrawSize, F), front);
                if (v.Y > 0f) _spriteBatch.Draw(_pixel, new Rectangle(left, top + TileDrawSize - F, TileDrawSize, F), front);
                if (v.X < 0f) _spriteBatch.Draw(_pixel, new Rectangle(left, top, F, TileDrawSize), front);
                if (v.X > 0f) _spriteBatch.Draw(_pixel, new Rectangle(left + TileDrawSize - F, top, F, TileDrawSize), front);
            }
        }
    }

    private void Outline(int left, int top, int size, Color c)
    {
        _spriteBatch.Draw(_pixel, new Rectangle(left,            top,            size, 1),        c);
        _spriteBatch.Draw(_pixel, new Rectangle(left,            top + size - 1, size, 1),        c);
        _spriteBatch.Draw(_pixel, new Rectangle(left,            top + 1,        1,    size - 2), c);
        _spriteBatch.Draw(_pixel, new Rectangle(left + size - 1, top + 1,        1,    size - 2), c);
    }

    // Four L-shaped corner marks of arm length `arm`: the "slot" of a cell being built.
    private void Brackets(int left, int top, int size, int arm, Color c)
    {
        int r = left + size - 1, b = top + size - 1;
        _spriteBatch.Draw(_pixel, new Rectangle(left,        top,         arm, 1),       c);
        _spriteBatch.Draw(_pixel, new Rectangle(left,        top + 1,     1,   arm - 1), c);
        _spriteBatch.Draw(_pixel, new Rectangle(r - arm + 1, top,         arm, 1),       c);
        _spriteBatch.Draw(_pixel, new Rectangle(r,           top + 1,     1,   arm - 1), c);
        _spriteBatch.Draw(_pixel, new Rectangle(left,        b,           arm, 1),       c);
        _spriteBatch.Draw(_pixel, new Rectangle(left,        b - arm + 1, 1,   arm - 1), c);
        _spriteBatch.Draw(_pixel, new Rectangle(r - arm + 1, b,           arm, 1),       c);
        _spriteBatch.Draw(_pixel, new Rectangle(r,           b - arm + 1, 1,   arm - 1), c);
    }

    // Placeholder owner colours for the block-ownership ring (P1 cyan, P2 orange).
    private static readonly Color OwnerP1 = new(90, 200, 255);
    private static readonly Color OwnerP2 = new(255, 140, 60);

    // Block ownership overlay (Plans/BLOCK_OWNERSHIP_SPIKES.md §1): a hollow ring in the
    // owner's colour inset inside each owned solid cell, alpha = the claim's strength, so
    // a wall visibly cools from "yours" to plain terrain over a few minutes. Pure render:
    // reads the sim's table, writes nothing.
    private void DrawOwnership(ChunkMap chunks)
    {
        if (chunks.Ownership.Count == 0) return;
        var viewport = _graphicsDevice.Viewport;
        float halfW = viewport.Width / (2f * _camera.Zoom);
        float halfH = viewport.Height / (2f * _camera.Zoom);
        float minX = _camera.Position.X - halfW - Chunk.TileSize, maxX = _camera.Position.X + halfW;
        float minY = _camera.Position.Y - halfH - Chunk.TileSize, maxY = _camera.Position.Y + halfH;
        int now = chunks.TerrainFrame;

        int inset = Math.Max(1, Chunk.TileSize / 5);
        int size  = TileDrawSize - 2 * inset;
        if (size < 3) return;
        foreach (var kv in chunks.Ownership.Entries)
        {
            var (gtx, gty) = kv.Key;
            float x = gtx * Chunk.TileSize, y = gty * Chunk.TileSize;
            if (x < minX || x > maxX || y < minY || y > maxY) continue;
            // Solid only: a Sprouting cell already wears its owner's colour via the sprout look.
            if (chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
            if (!chunks.Ownership.TryGet(gtx, gty, now, out byte owner, out float s)) continue;
            var c = (owner == BlockOwnership.Player2 ? OwnerP2 : OwnerP1) * s;
            int left = (int)x + inset, top = (int)y + inset;
            _spriteBatch.Draw(_pixel, new Rectangle(left,            top,            size, 1),    c);
            _spriteBatch.Draw(_pixel, new Rectangle(left,            top + size - 1, size, 1),    c);
            _spriteBatch.Draw(_pixel, new Rectangle(left,            top + 1,        1,    size - 2), c);
            _spriteBatch.Draw(_pixel, new Rectangle(left + size - 1, top + 1,        1,    size - 2), c);
        }
    }

    // Spike overlay look: SpikeTeeth small triangles standing out of each EXPOSED face
    // of a spiked cell (a face against another solid cell is buried, so no teeth),
    // tinted by the owning side. Teeth stand in the neighbouring air, outside the cell,
    // so they read as a hazard on the surface rather than a texture on the block.
    private const int   SpikeTeeth  = 2;
    private const float SpikeHeight = Chunk.TileSize * 0.4f;

    private void DrawSpikes(ChunkMap chunks)
    {
        if (chunks.Spikes.Count == 0) return;
        var viewport = _graphicsDevice.Viewport;
        float halfW = viewport.Width  / (2f * _camera.Zoom) + Chunk.TileSize;
        float halfH = viewport.Height / (2f * _camera.Zoom) + Chunk.TileSize;
        foreach (var (cell, side) in chunks.Spikes.Entries)
        {
            var (gtx, gty) = cell;
            if (chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
            float left = gtx * Chunk.TileSize, top = gty * Chunk.TileSize;
            if (MathF.Abs(left - _camera.Position.X) > halfW || MathF.Abs(top - _camera.Position.Y) > halfH)
                continue;
            var color = TilePalette.SpikeColor(side.Faction);
            if (chunks.GetCellState(gtx, gty - 1) != TileState.Solid) Teeth(left, top, 0, -1, color);
            if (chunks.GetCellState(gtx, gty + 1) != TileState.Solid) Teeth(left, top, 0,  1, color);
            if (chunks.GetCellState(gtx - 1, gty) != TileState.Solid) Teeth(left, top, -1, 0, color);
            if (chunks.GetCellState(gtx + 1, gty) != TileState.Solid) Teeth(left, top,  1, 0, color);
        }
    }

    // Spike seed look (todo.txt #8), drawn purely from the seed's lifecycle phase: a
    // small crystal standing on ONE exposed face of the block (top preferred — "a crystal
    // appears on top of the block"; else a side, else the bottom; a fully buried cell
    // shows nothing, same as its eventual spike). Seed: a slim side-tinted shard that
    // swells over the wait. Flash: the full shard blinking white — the last warning
    // before it becomes the teeth DrawSpikes draws.
    private const float SeedMinHeight  = Chunk.TileSize * 0.25f;
    private const float SeedMaxHeight  = Chunk.TileSize * 0.5f;
    private const float SeedHalfWidth  = Chunk.TileSize * 0.16f;
    private const int   FlashBlinkFrames = 3;

    private void DrawSpikeSeeds(ChunkMap chunks)
    {
        if (chunks.SpikeSeeds.Count == 0) return;
        var viewport = _graphicsDevice.Viewport;
        float halfW = viewport.Width  / (2f * _camera.Zoom) + Chunk.TileSize;
        float halfH = viewport.Height / (2f * _camera.Zoom) + Chunk.TileSize;
        int now = chunks.TerrainFrame;
        var life = TileSpikeSeeds.Lifecycle;
        foreach (var (cell, seed) in chunks.SpikeSeeds.Entries)
        {
            var (gtx, gty) = cell;
            if (chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
            float left = gtx * Chunk.TileSize, top = gty * Chunk.TileSize;
            if (MathF.Abs(left - _camera.Position.X) > halfW || MathF.Abs(top - _camera.Position.Y) > halfH)
                continue;

            int nx, ny;
            if      (chunks.GetCellState(gtx, gty - 1) != TileState.Solid) { nx = 0;  ny = -1; }
            else if (chunks.GetCellState(gtx - 1, gty) != TileState.Solid) { nx = -1; ny = 0; }
            else if (chunks.GetCellState(gtx + 1, gty) != TileState.Solid) { nx = 1;  ny = 0; }
            else if (chunks.GetCellState(gtx, gty + 1) != TileState.Solid) { nx = 0;  ny = 1; }
            else continue;

            int elapsed = now - seed.SeededFrame;
            var phase = life.PhaseAt(elapsed);
            var side = TilePalette.SpikeColor(seed.Side.Faction);
            float height; Color color;
            if (phase == CrystalPhase.Seed)
            {
                float p = life.PhaseProgress(elapsed);
                height = MathHelper.Lerp(SeedMinHeight, SeedMaxHeight, p);
                color  = Color.Lerp(side, Color.White, 0.25f) * 0.9f;
            }
            else
            {
                height = SeedMaxHeight;
                bool on = ((elapsed - life.SeedFrames) / FlashBlinkFrames) % 2 == 0;
                color  = on ? Color.White : Color.Lerp(side, Color.White, 0.5f);
            }
            Shard(left, top, nx, ny, height, SeedHalfWidth, color);
        }
    }

    // ---- Crystal wave (todo.txt #7) — render-only view of ChunkMap.Crystals -----------
    // Each seeded crystal is a shard standing on its substrate face (the ground, or the
    // crystal that seeded it), drawn with the SAME length curve the contact system tests
    // (CrystalWave.LengthAt), so what you see is what pushes and what bites:
    //   seed  — a small pale shard (roots sit here for the whole warning);
    //   flash — blinks white while growing to full length (forming: pushes, no damage);
    //   grown — full length, bright, the side's colour mixed into ice; fades out over
    //           its last third, then vanishes. Pending and broken crystals draw nothing.
    private static readonly Color CrystalIce = new(190, 235, 255);

    private void DrawCrystalWave(ChunkMap chunks)
    {
        if (chunks.Crystals.Count == 0) return;
        var viewport = _graphicsDevice.Viewport;
        float halfW = viewport.Width  / (2f * _camera.Zoom) + 2 * Chunk.TileSize;
        float halfH = viewport.Height / (2f * _camera.Zoom) + 2 * Chunk.TileSize;
        int now = chunks.TerrainFrame;
        foreach (var (cell, c) in chunks.Crystals.Entries)
        {
            if (c.Pending || c.Broken) continue;
            var (gtx, gty) = cell;
            float left = gtx * Chunk.TileSize, top = gty * Chunk.TileSize;
            if (MathF.Abs(left - _camera.Position.X) > halfW || MathF.Abs(top - _camera.Position.Y) > halfH)
                continue;

            var life = c.Lifecycle;
            int elapsed = now - c.SeededFrame;
            var phase = life.PhaseAt(elapsed);
            if (phase == CrystalPhase.Gone) continue;
            float length = CrystalWave.LengthAt(in life, elapsed);
            var side = Color.Lerp(CrystalIce, TilePalette.SpikeColor(c.Side.Faction), 0.35f);
            Color color;
            float halfWidth = CrystalWave.HalfWidth;
            if (phase == CrystalPhase.Seed)
            {
                color = Color.Lerp(side, Color.White, 0.3f) * 0.85f;
                halfWidth *= 0.55f;
            }
            else if (phase == CrystalPhase.Flash)
            {
                bool on = ((elapsed - life.SeedFrames) / 2) % 2 == 0;
                color = on ? Color.White : side;
            }
            else
            {
                float p = life.PhaseProgress(elapsed);
                float fade = p < 0.67f ? 1f : MathHelper.Lerp(1f, 0.25f, (p - 0.67f) / 0.33f);
                color = side * fade;
            }
            // Shard() draws outward from a face of the cell it is given, so hand it the
            // SUBSTRATE cell with the growth normal: the shard rises out of the shared face.
            var o = TileSproutNode.FaceOffset(c.Base);
            Shard(left + o.X * Chunk.TileSize, top + o.Y * Chunk.TileSize, -o.X, -o.Y, length, halfWidth, color);
        }
    }

    // A diamond shard standing on the centre of a cell face, outward normal (nx, ny):
    // widest a third of the way up, pointed at the tip. 1px slices, like Teeth.
    private void Shard(float left, float top, int nx, int ny, float height, float halfWidth, Color color)
    {
        const float T = Chunk.TileSize;
        int rows = Math.Max(1, (int)MathF.Round(height));
        float waist = rows / 3f;
        for (int r = 0; r < rows; r++)
        {
            float f = r + 0.5f;
            float hw = f < waist ? halfWidth * (0.4f + 0.6f * f / waist)
                                 : halfWidth * (1f - (f - waist) / (rows - waist));
            // Slice width matches the tile's parity so every slice centres exactly on the
            // face (odd TileSize → odd widths) and the shard stays symmetric.
            int w = Math.Max(1, (int)MathF.Round(2f * hw));
            if ((w & 1) != (Chunk.TileSize & 1)) w++;
            int a = (Chunk.TileSize - w) / 2;
            Rectangle rect;
            if (ny != 0)
            {
                float y = ny < 0 ? top - 1 - r : top + T + r;
                rect = new Rectangle((int)left + a, (int)y, w, 1);
            }
            else
            {
                float x = nx < 0 ? left - 1 - r : left + T + r;
                rect = new Rectangle((int)x, (int)top + a, 1, w);
            }
            _spriteBatch.Draw(_pixel, rect, color);
        }
    }

    // One face's teeth, rasterized as stacked 1px slices so it needs nothing but the
    // pixel texture (identical under DesktopGL and KNI). (nx, ny) is the face's outward
    // normal; each tooth's base sits on the face and its tip points along the normal.
    private void Teeth(float left, float top, int nx, int ny, Color color)
    {
        const float T = Chunk.TileSize;
        float baseW = T / SpikeTeeth;
        int rows = Math.Max(1, (int)MathF.Round(SpikeHeight));
        for (int k = 0; k < SpikeTeeth; k++)
        {
            float along0 = k * baseW;                 // tooth's start along the face
            for (int r = 0; r < rows; r++)
            {
                float w = baseW * (1f - (r + 0.5f) / rows);
                if (w < 1f) w = 1f;
                float a = along0 + (baseW - w) * 0.5f;
                Rectangle rect;
                if (ny != 0)
                {
                    float y = ny < 0 ? top - 1 - r : top + T + r;
                    rect = new Rectangle((int)(left + a), (int)y, Math.Max(1, (int)MathF.Round(w)), 1);
                }
                else
                {
                    float x = nx < 0 ? left - 1 - r : left + T + r;
                    rect = new Rectangle((int)x, (int)(top + a), 1, Math.Max(1, (int)MathF.Round(w)));
                }
                _spriteBatch.Draw(_pixel, rect, color);
            }
        }
    }

    private void DrawChunk(Chunk chunk, ChunkMap chunks)
    {
        var viewport = _graphicsDevice.Viewport;
        float halfW = viewport.Width / (2f * _camera.Zoom);
        float halfH = viewport.Height / (2f * _camera.Zoom);
        int chunkPixelSize = Chunk.Size * Chunk.TileSize;
        var origin = chunk.WorldPosition;

        if (origin.X > _camera.Position.X + halfW || origin.X + chunkPixelSize < _camera.Position.X - halfW ||
            origin.Y > _camera.Position.Y + halfH || origin.Y + chunkPixelSize < _camera.Position.Y - halfH)
            return;

        // Pass 1: black underlay covering each solid tile's full cell — the inset
        // fill on top leaves the gutter showing through as a thin border line.
        // Separate from the fill pass so the batch switches texture once per chunk,
        // not once per tile.
        //
        // Charged tiles invert it: a white border instead of a black one. The fill tint
        // alone can't carry the state for every material — foam's base color is already
        // near-white — so the gutter does the work the fill can't.
        for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++)
            {
                if (!chunk.Tiles[tx, ty].IsSolid) continue;
                bool charged = chunks.Charge.IsCharged(
                    chunk.ChunkPos.X * Chunk.Size + tx, chunk.ChunkPos.Y * Chunk.Size + ty);
                _spriteBatch.Draw(_pixel, new Rectangle(
                    (int)(origin.X + tx * Chunk.TileSize),
                    (int)(origin.Y + ty * Chunk.TileSize),
                    Chunk.TileSize, Chunk.TileSize), charged ? Color.White : Color.Black);
            }

        // Pass 2: palette-tinted fills, modulated by the rock grain atlas when present.
        for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++)
            {
                if (!chunk.Tiles[tx, ty].IsSolid) continue;
                int gtx = chunk.ChunkPos.X * Chunk.Size + tx;
                int gty = chunk.ChunkPos.Y * Chunk.Size + ty;
                var type = chunk.Tiles[tx, ty].Type;
                float dmgFrac = MathF.Min(chunks.Damage.Get(gtx, gty) / TileDamage.MaxHPFor(type), 1f);
                var baseColor = TilePalette.BaseColor(type);
                var color = dmgFrac > 0f
                    ? Color.Lerp(baseColor, Color.Black, dmgFrac * 0.7f)
                    : baseColor;
                // Charge tints AFTER damage so a charged tile that's also cracked still
                // reads as charged — the two are independent states, and the one the
                // player spent a whole meter on is the one that has to survive the mix.
                if (chunks.Charge.IsCharged(gtx, gty))
                    color = Color.Lerp(color, Color.White, ChargeTint);
                var dest = new Rectangle(
                    (int)(origin.X + tx * Chunk.TileSize),
                    (int)(origin.Y + ty * Chunk.TileSize),
                    TileDrawSize, TileDrawSize);
                if (Atlas != null)
                    _spriteBatch.Draw(Atlas.Texture, dest,
                        Atlas.SourceFor(type, gtx, gty, TileDrawSize), color);
                else
                    _spriteBatch.Draw(_pixel, dest, color);
            }
    }
}
