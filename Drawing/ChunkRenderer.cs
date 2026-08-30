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
