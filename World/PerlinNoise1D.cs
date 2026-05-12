using System;

namespace MTile;

// 1D value noise with smoothstep interpolation, supports FBM octaves.
public class PerlinNoise1D
{
    private readonly int _seed;

    public PerlinNoise1D(int seed) => _seed = seed;

    public float Fbm(float x, int octaves, float persistence, float lacunarity)
    {
        float value = 0f, amp = 1f, freq = 1f, maxAmp = 0f;
        for (int i = 0; i < octaves; i++)
        {
            value  += Smooth(x * freq) * amp;
            maxAmp += amp;
            amp    *= persistence;
            freq   *= lacunarity;
        }
        return value / maxAmp; // normalized to roughly [-1, 1]
    }

    private float Smooth(float x)
    {
        int   ix = (int)MathF.Floor(x);
        float t  = x - ix;
        t = t * t * (3f - 2f * t); // smoothstep
        return Hash(ix) + (Hash(ix + 1) - Hash(ix)) * t;
    }

    private float Hash(int x)
    {
        uint n = (uint)x * 2654435761u ^ (uint)(_seed * 2246822519);
        n ^= n >> 16;
        n *= 0x45d9f3bu;
        n ^= n >> 16;
        return (float)(n & 0xFFFF) / 32767.5f - 1f; // [-1, 1]
    }
}
