using System;

namespace MTile;

// 2D value noise with smoothstep interpolation and FBM octaves — the 2D sibling of
// PerlinNoise1D, sharing its hash and its [-1, 1] output convention.
//
// Terrain shape is a heightfield (a function of x alone, so PerlinNoise1D covers it);
// this exists for the things that vary with DEPTH as well as position — the dirt and
// sand pockets scattered through the deep stone. Doing those with 1D noise would band
// them into vertical stripes running the full depth of the world.
public sealed class ValueNoise2D
{
    private readonly int _seed;

    public ValueNoise2D(int seed) => _seed = seed;

    public float Fbm(float x, float y, int octaves, float persistence, float lacunarity)
    {
        float value = 0f, amp = 1f, freq = 1f, maxAmp = 0f;
        for (int i = 0; i < octaves; i++)
        {
            value  += Smooth(x * freq, y * freq) * amp;
            maxAmp += amp;
            amp    *= persistence;
            freq   *= lacunarity;
        }
        return value / maxAmp;   // roughly [-1, 1]
    }

    private float Smooth(float x, float y)
    {
        int   ix = (int)MathF.Floor(x);
        int   iy = (int)MathF.Floor(y);
        float tx = x - ix, ty = y - iy;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);

        float a = Hash(ix,     iy);
        float b = Hash(ix + 1, iy);
        float c = Hash(ix,     iy + 1);
        float d = Hash(ix + 1, iy + 1);

        float top    = a + (b - a) * tx;
        float bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    private float Hash(int x, int y)
    {
        uint n = (uint)x * 2654435761u ^ (uint)y * 2246822519u ^ (uint)(_seed * 3266489917);
        n ^= n >> 16;
        n *= 0x45d9f3bu;
        n ^= n >> 16;
        n *= 0x45d9f3bu;
        n ^= n >> 16;
        return (float)(n & 0xFFFF) / 32767.5f - 1f;   // [-1, 1]
    }
}
