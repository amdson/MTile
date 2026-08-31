using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Supplies tiles for a chunk that has never been loaded. ChunkMap holds one (or none —
// a null generator is the finite, authored world every existing stage uses) and calls
// it from EnsureChunk as the streamer walks out ahead of the players.
//
// The contract that matters is PURITY: Generate(chunk) must depend on nothing but the
// chunk's position and the generator's own immutable config. Streamed chunks are
// journaled as lazy creations, so a rollback drops them and the replay regenerates
// them — which only lands on the same world if generation is a pure function of
// position. Never cache mutable state here, and never read sim state.
public interface IChunkGenerator
{
    void Generate(Chunk chunk);
}

// Tuning for WorldGenerator. Lives in the level json under "WorldGen"; a level that
// sets it gets an endless streamed world instead of the Extents box.
public class WorldGenConfig
{
    public int Seed { get; set; } = 1337;

    // World tile Y of the base plain. Height is measured UP from here (y is down),
    // so a column of height h has its surface at GroundLevel - h.
    public int GroundLevel { get; set; } = 0;

    // ── Rolling base: the gentle ground everything else sits on ────────────────
    public float BaseScale     { get; set; } = 0.010f;
    public int   BaseOctaves   { get; set; } = 4;
    public float BaseAmplitude { get; set; } = 12f;

    // ── Spiky hills: ordinary peaks, tens of tiles ─────────────────────────────
    // Floor/Sharpness are what make them SPIKY rather than rolling: the ridge value
    // is remapped so everything below Floor is flat ground, and the remainder is
    // raised to Sharpness so the peak is a narrow point rather than a dome.
    public float HillScale     { get; set; } = 0.022f;
    public int   HillOctaves   { get; set; } = 3;
    public float HillFloor     { get; set; } = 0.55f;
    public float HillSharpness { get; set; } = 2.2f;
    public float HillAmplitude { get; set; } = 70f;

    // ── Mega-spires: the rare towers, up to thousands of tiles ─────────────────
    // A much lower frequency than the hills, so the tall ones have a base wide
    // enough to read as a mountain rather than a one-tile needle.
    public float SpireScale     { get; set; } = 0.0028f;
    public int   SpireOctaves   { get; set; } = 2;
    public float SpireFloor     { get; set; } = 0.70f;
    public float SpireSharpness { get; set; } = 2.0f;
    public float SpireAmplitude { get; set; } = 3200f;

    // Rarity mask, slower still than the spire ridge — a region either grows giants
    // or it doesn't, so the tall ones come in clusters with long plains between.
    public float SpireMaskScale     { get; set; } = 0.0007f;
    public int   SpireMaskOctaves   { get; set; } = 2;
    public float SpireMaskFloor     { get; set; } = 0.55f;
    public float SpireMaskSharpness { get; set; } = 2.0f;

    // Hard ceiling on column height (tiles above GroundLevel).
    public float MaxHeight { get; set; } = 3200f;

    // ── Materials ──────────────────────────────────────────────────────────────
    // Soil (sand crust over dirt) fades out with elevation and with slope: nothing
    // loose clings to the flank of a spire, so the towers are bare stone and the
    // plains are not.
    public float SoilFadeStart  { get; set; } = 70f;    // elevation where soil starts thinning
    public float SoilFadeSpan   { get; set; } = 190f;   // …and over how many tiles it reaches zero
    public float SlopeFadeStart { get; set; } = 1.1f;   // |dh/dx| where soil starts sliding off
    public float SlopeFadeSpan  { get; set; } = 1.9f;

    public float AridScale { get; set; } = 0.006f;     // sand-vs-dirt banding across the map
    public float SandMin   { get; set; } = 0.6f;       // sand crust depth, tiles
    public float SandRange { get; set; } = 4.4f;
    public float DirtMin   { get; set; } = 3f;         // dirt layer below the sand, tiles
    public float DirtRange { get; set; } = 9f;

    // Pockets of dirt / sand scattered through the deep stone, so digging down is
    // not 100% one material.
    public float PocketScale     { get; set; } = 0.035f;
    public int   PocketOctaves   { get; set; } = 3;
    public float DirtPocketLevel { get; set; } = 0.32f;   // pocket noise above this → Dirt
    public float SandPocketLevel { get; set; } = 0.48f;   // …below its negation → Sand

    // ── Streaming ──────────────────────────────────────────────────────────────
    // Chunk radius kept resident around each player. Horizontal is larger because
    // the view is wider than it is tall and the player travels sideways.
    public int StreamRadiusX { get; set; } = 8;
    public int StreamRadiusY { get; set; } = 6;
}

// Endless heightfield terrain: rolling ground, spiky hills, and rare spires that run
// to thousands of tiles, layered sand-over-dirt-over-stone.
//
// Everything is a function of the world tile X of a column, so the surface can never
// overhang — a column is solid from its surface all the way down. That is the whole
// reason the generator can be a pure per-chunk function with no cross-chunk state:
// two neighbouring chunks agree at their seam because they evaluate the same noise at
// the same X, not because either of them looked at the other.
public sealed class WorldGenerator : IChunkGenerator
{
    private readonly WorldGenConfig _cfg;
    private readonly PerlinNoise1D _base;
    private readonly PerlinNoise1D _hill;
    private readonly PerlinNoise1D _spire;
    private readonly PerlinNoise1D _spireMask;
    private readonly PerlinNoise1D _arid;
    private readonly ValueNoise2D  _pocket;

    public WorldGenerator(WorldGenConfig cfg)
    {
        _cfg       = cfg ?? new WorldGenConfig();
        _base      = new PerlinNoise1D(_cfg.Seed);
        _hill      = new PerlinNoise1D(_cfg.Seed + 101);
        _spire     = new PerlinNoise1D(_cfg.Seed + 227);
        _spireMask = new PerlinNoise1D(_cfg.Seed + 353);
        _arid      = new PerlinNoise1D(_cfg.Seed + 479);
        _pocket    = new ValueNoise2D (_cfg.Seed + 601);
    }

    public WorldGenConfig Config => _cfg;

    // Height of a column, in tiles above GroundLevel.
    public float HeightAt(int worldTileX)
    {
        float x = worldTileX;

        float rolling = _base.Fbm(x * _cfg.BaseScale, _cfg.BaseOctaves, 0.5f, 2f) * _cfg.BaseAmplitude;

        float hill = Spike(_hill.Fbm(x * _cfg.HillScale, _cfg.HillOctaves, 0.5f, 2f),
                           _cfg.HillFloor, _cfg.HillSharpness) * _cfg.HillAmplitude;

        float spire = Spike(_spire.Fbm(x * _cfg.SpireScale, _cfg.SpireOctaves, 0.5f, 2f),
                            _cfg.SpireFloor, _cfg.SpireSharpness)
                    * SpireMask(x) * _cfg.SpireAmplitude;

        return MathF.Min(rolling + hill + spire, _cfg.MaxHeight);
    }

    // World tile Y of the topmost solid cell of a column.
    public int SurfaceY(int worldTileX)
        => _cfg.GroundLevel - (int)MathF.Round(HeightAt(worldTileX));

    public void Generate(Chunk chunk)
    {
        int baseX = chunk.ChunkPos.X * Chunk.Size;
        int baseY = chunk.ChunkPos.Y * Chunk.Size;

        // Surfaces for this chunk's columns PLUS one on each side, so the slope at
        // the edge columns is a real central difference instead of a one-sided guess
        // (the material layering reads slope, and a seam in the layering is as
        // visible as a seam in the shape).
        Span<int>   surface = stackalloc int[Chunk.Size + 2];
        Span<float> height  = stackalloc float[Chunk.Size + 2];
        for (int i = 0; i < Chunk.Size + 2; i++)
        {
            height[i]  = HeightAt(baseX - 1 + i);
            surface[i] = _cfg.GroundLevel - (int)MathF.Round(height[i]);
        }

        for (int tx = 0; tx < Chunk.Size; tx++)
        {
            int surfaceY = surface[tx + 1];
            // Nothing to write in a column whose ground is below this chunk.
            if (baseY + Chunk.Size <= surfaceY) continue;

            float elev  = _cfg.GroundLevel - surfaceY;
            float slope = MathF.Abs(height[tx + 2] - height[tx]) * 0.5f;

            float soil = MathF.Min(Fade(elev,  _cfg.SoilFadeStart,  _cfg.SoilFadeSpan),
                                   Fade(slope, _cfg.SlopeFadeStart, _cfg.SlopeFadeSpan));
            float arid = 0.5f + 0.5f * _arid.Fbm((baseX + tx) * _cfg.AridScale, 3, 0.5f, 2f);

            float sandDepth = soil * (_cfg.SandMin + arid * _cfg.SandRange);
            float dirtDepth = sandDepth + soil * (_cfg.DirtMin + (1f - arid) * _cfg.DirtRange);

            for (int ty = 0; ty < Chunk.Size; ty++)
            {
                int worldY = baseY + ty;
                if (worldY < surfaceY) continue;          // open sky

                ref var t = ref chunk.Tiles[tx, ty];
                t.State = TileState.Solid;
                t.Type  = MaterialAt(baseX + tx, worldY, worldY - surfaceY, sandDepth, dirtDepth);
            }
        }
    }

    private TileType MaterialAt(int worldX, int worldY, int depth, float sandDepth, float dirtDepth)
    {
        if (depth < sandDepth) return TileType.Sand;
        if (depth < dirtDepth) return TileType.Dirt;

        float p = _pocket.Fbm(worldX * _cfg.PocketScale, worldY * _cfg.PocketScale,
                              _cfg.PocketOctaves, 0.5f, 2f);
        if (p >  _cfg.DirtPocketLevel) return TileType.Dirt;
        if (p < -_cfg.SandPocketLevel) return TileType.Sand;
        return TileType.Stone;
    }

    // Ridge + floor + power: |noise| near zero is a ridge crest, everything below the
    // floor is flattened away entirely, and the power sharpens what is left into a
    // spike. Returns [0, 1].
    private static float Spike(float noise, float floor, float sharpness)
    {
        float ridge = 1f - MathF.Abs(noise);
        if (ridge <= floor) return 0f;
        float t = (ridge - floor) / (1f - floor);
        return MathF.Pow(t, sharpness);
    }

    // Rarity mask for the mega-spires, in [0, 1] and zero over most of the world.
    private float SpireMask(float x)
    {
        float m = 0.5f + 0.5f * _spireMask.Fbm(x * _cfg.SpireMaskScale, _cfg.SpireMaskOctaves, 0.5f, 2f);
        if (m <= _cfg.SpireMaskFloor) return 0f;
        float t = (m - _cfg.SpireMaskFloor) / (1f - _cfg.SpireMaskFloor);
        return MathF.Pow(t, _cfg.SpireMaskSharpness);
    }

    // 1 below `start`, ramping smoothly to 0 across `span`.
    private static float Fade(float v, float start, float span)
    {
        if (v <= start) return 1f;
        float t = (v - start) / span;
        if (t >= 1f) return 0f;
        return 1f - t * t * (3f - 2f * t);
    }
}
