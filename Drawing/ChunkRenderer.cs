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
    public void Draw(Simulation sim)
    {
        var chunks = sim.Chunks;

        foreach (var chunk in chunks)
            DrawChunk(chunk, chunks);

        DrawOwnership(chunks);
        DrawSpikes(chunks);

        foreach (var (r, color) in sim.Platforms)
            _spriteBatch.Draw(_pixel, new Rectangle(
                (int)r.Left, (int)r.Top,
                (int)(r.Right - r.Left), (int)(r.Bottom - r.Top)),
                color);

        // One quad per supporting face, matching the physics volumes exactly: a
        // sprout with two parents visibly pushes out of both and the quads meet
        // in the middle of the target cell.
        foreach (var s in chunks.ActiveSprouts)
        foreach (var face in TileSproutNode.FaceOrder)
        {
            if ((s.Faces & face) == 0) continue;
            var c = s.VolumeCenter(face);
            const float half = Chunk.TileSize * 0.5f;
            _spriteBatch.Draw(_pixel, new Rectangle(
                (int)(c.X - half), (int)(c.Y - half),
                TileDrawSize, TileDrawSize),
                Color.LightSkyBlue);
        }

        // Ghost outlines of queued (Pending) sprouts so players can preview the
        // build they're generating before each block starts growing. Pending nodes
        // have no live Center, so draw at the target cell (gtx*16+8, gty*16+8).
        foreach (var s in chunks.PendingSprouts)
        {
            const float half = Chunk.TileSize * 0.5f;
            float cx = s.Gtx * Chunk.TileSize + half;
            float cy = s.Gty * Chunk.TileSize + half;
            int left = (int)(cx - half);
            int top  = (int)(cy - half);
            int size = TileDrawSize;
            var ghost = Color.LightSkyBlue * 0.4f;
            _spriteBatch.Draw(_pixel, new Rectangle(left,            top,            size, 1),    ghost);
            _spriteBatch.Draw(_pixel, new Rectangle(left,            top + size - 1, size, 1),    ghost);
            _spriteBatch.Draw(_pixel, new Rectangle(left,            top,            1,    size), ghost);
            _spriteBatch.Draw(_pixel, new Rectangle(left + size - 1, top,            1,    size), ghost);
        }
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
            if (chunks.GetCellState(gtx, gty) == TileState.Empty) continue;
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
