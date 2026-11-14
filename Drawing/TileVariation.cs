using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Per-tile colour variation (todo V8): breaks up the repetition of a uniform tile fill.
// Pure function of the global cell coords and material — deterministic, render-only.
//
// Three layers, smallest to largest. The per-tile jitter alone would read as confetti
// and the broad noise alone leaves the grid looking airbrushed; together the eye gets
// individual blocks sitting inside lighter/darker beds. Both noise octaves are sampled
// in world cell coords with no reference to the material, so a patch runs straight
// through a dirt/stone boundary the way a lighting or moisture change would, instead
// of each material carrying its own unrelated pattern.
public static class TileVariation
{
    // Per-tile hash jitter, ± fraction of brightness. Keeps individual blocks legible.
    private const float JitterAmp = 0.035f;

    // Mid octave: beds a handful of tiles across. ± fraction at the noise extremes.
    private const float MidAmp = 0.055f;
    // Mid octave wavelength in tiles, horizontal / vertical. Wider than tall so the
    // patches read as layered strata rather than round blotches.
    private const float MidWaveX = 8f;
    private const float MidWaveY = 4f;

    // Broad octave: screen-scale light/dark regions. ± fraction at the noise extremes.
    private const float BroadAmp = 0.07f;
    // Broad octave wavelength in tiles, horizontal / vertical (same stretch as above).
    private const float BroadWaveX = 26f;
    private const float BroadWaveY = 11f;

    // Per-material scale on the whole variation. Foam is near-white scaffolding that
    // should stay clean and obviously artificial; hardened must stay uniformly dark so
    // the "don't swing at this" read never weakens; sand is fine-grained and bright,
    // where the same swing looks blotchier than it does on the browns.
    private const float StoneScale    = 1.0f;
    private const float DirtScale     = 1.0f;
    private const float SandScale     = 0.75f;
    private const float FoamScale     = 0.3f;
    private const float HardenedScale = 0.6f;

    // Warm/cool drift for Tint: ± fraction pushed onto red and pulled off blue (and
    // vice versa). Small — it should register as mineral staining, not a new material.
    private const float TintAmp = 0.04f;
    // Tint wavelength in tiles, horizontal / vertical. Deliberately off the brightness
    // wavelengths so warm patches don't line up with bright ones.
    private const float TintWaveX = 17f;
    private const float TintWaveY = 7f;

    // Distinct lattice seeds per layer so the octaves are uncorrelated.
    private const uint JitterSeed = 0x9E3779B9u;
    private const uint MidSeed    = 0x85EBCA6Bu;
    private const uint BroadSeed  = 0xC2B2AE35u;
    private const uint TintSeed   = 0x27D4EB2Fu;

    // Brightness multiplier for a tile's fill colour, around 1.0. Combines a per-tile
    // hash jitter with large-scale noise that runs across tile borders.
    public static float Brightness(TileType type, int gtx, int gty)
    {
        float scale = Scale(type);
        if (scale == 0f) return 1f;

        float v = JitterAmp * Signed(Hash(gtx, gty, JitterSeed))
                + MidAmp    * Noise(gtx, gty, MidWaveX, MidWaveY, MidSeed)
                + BroadAmp  * Noise(gtx, gty, BroadWaveX, BroadWaveY, BroadSeed);
        return 1f + scale * v;
    }

    // Optional companion to Brightness: a slow warm/cool drift as a colour multiplier.
    // The brighter channel is pinned at 255 so this never darkens on its own account —
    // it only moves hue, and stacks with Brightness without double-counting.
    public static Color Tint(TileType type, int gtx, int gty)
    {
        float scale = Scale(type);
        if (scale == 0f) return Color.White;

        float w = scale * TintAmp * Noise(gtx, gty, TintWaveX, TintWaveY, TintSeed);
        // w > 0 is warm (blue pulled down), w < 0 is cool (red pulled down); green
        // takes half the shift so the drift stays on the brown/ochre axis.
        float r = w < 0f ? 1f + w : 1f;
        float b = w > 0f ? 1f - w : 1f;
        float g = 1f - MathF.Abs(w) * 0.5f;
        return new Color((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f));
    }

    private static float Scale(TileType type) => type switch
    {
        TileType.Stone    => StoneScale,
        TileType.Dirt     => DirtScale,
        TileType.Sand     => SandScale,
        TileType.Foam     => FoamScale,
        TileType.Hardened => HardenedScale,
        // Spikes are tinted by owning team; that read has to stay exact.
        _                 => 0f,
    };

    // Bilinear value noise in [-1, 1] on a lattice of the given wavelength, smoothstep
    // faded so the lattice lines themselves don't show as creases. Sampled at the cell
    // centre so the pattern isn't pinned to tile corners.
    private static float Noise(int gtx, int gty, float waveX, float waveY, uint seed)
    {
        float x = (gtx + 0.5f) / waveX, y = (gty + 0.5f) / waveY;
        // Floor, not a cast: the world extends left of / above the origin and a
        // truncating cast would mirror the pattern across both axes there.
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int ix = (int)fx, iy = (int)fy;
        float tx = x - fx, ty = y - fy;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);

        float a = Signed(Hash(ix,     iy,     seed));
        float b = Signed(Hash(ix + 1, iy,     seed));
        float c = Signed(Hash(ix,     iy + 1, seed));
        float d = Signed(Hash(ix + 1, iy + 1, seed));
        float top = a + (b - a) * tx, bot = c + (d - c) * tx;
        return top + (bot - top) * ty;
    }

    // Integer-only 2D hash with a full avalanche finisher. A plain multiply-xor leaves
    // the low bits of neighbouring cells correlated, which shows up as diagonal
    // streaks once the result is used as a brightness.
    private static uint Hash(int x, int y, uint seed)
    {
        unchecked
        {
            uint h = seed ^ ((uint)x * 0x27D4EB2Du) ^ ((uint)y * 0x165667B1u + 0x9E3779B9u);
            h ^= h >> 16; h *= 0x7FEB352Du;
            h ^= h >> 15; h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }
    }

    // Top 24 bits → [-1, 1]. 24 so the value is exactly representable in a float.
    private static float Signed(uint h) => (h >> 8) * (2f / 16777215f) - 1f;
}
