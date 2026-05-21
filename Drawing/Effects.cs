using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Preset particle bursts. Each helper writes N particles into the supplied system —
// callers don't deal with the Particle struct directly. Tune values here, not at
// the call site.
public static class Effects
{
    private static readonly Random _rng = new();

    // Tile shatter: chunky squares burst outward and arc down under gravity. Color
    // is the tile's material color so a sand break reads sandy, stone reads gray.
    public static void TileBreak(ParticleSystem ps, Vector2 pos, Color color, int count = 8)
    {
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            float ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            float spd = 30f + (float)_rng.NextDouble() * 60f;
            p.Position        = pos;
            p.Velocity        = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd;
            p.Acceleration    = new Vector2(0f, 200f);
            p.MaxLife         = 0.4f + (float)_rng.NextDouble() * 0.3f;
            p.Life            = p.MaxLife;
            p.StartColor      = color;
            p.EndColor        = color * 0f;
            p.StartSize       = 3f;
            p.EndSize         = 1f;
            p.AngularVelocity = (float)(_rng.NextDouble() - 0.5) * 8f;
            p.Kind            = ParticleKind.Square;
        }
    }

    // Short bright streaks at the contact point, biased along `dir`. Used when a
    // slash/stab hitbox lands on a tile or an entity.
    public static void HitSpark(ParticleSystem ps, Vector2 pos, Vector2 dir, int count = 4)
    {
        if (dir.LengthSquared() > 1e-4f) dir.Normalize(); else dir = new Vector2(1f, 0f);
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            float jit = ((float)_rng.NextDouble() - 0.5f) * 0.9f;
            var v = Rotate(dir, jit) * (80f + (float)_rng.NextDouble() * 80f);
            p.Position     = pos;
            p.Velocity     = v;
            p.Acceleration = -v * 4f;
            p.MaxLife      = 0.12f + (float)_rng.NextDouble() * 0.08f;
            p.Life         = p.MaxLife;
            p.StartColor   = Color.LightYellow;
            p.EndColor     = new Color(255, 80, 20, 0);
            p.StartSize    = 6f;
            p.EndSize      = 1f;
            p.Kind         = ParticleKind.Line;
        }
    }

    // Soft puff that grows + fades. Use for landings, jump-dust, sprout puffs.
    public static void Puff(ParticleSystem ps, Vector2 pos, Color color, int count = 5)
    {
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            float ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            float spd = 10f + (float)_rng.NextDouble() * 20f;
            p.Position     = pos + new Vector2(((float)_rng.NextDouble() - 0.5f) * 6f, 0f);
            p.Velocity     = new Vector2(MathF.Cos(ang) * spd, -10f - (float)_rng.NextDouble() * 20f);
            p.Acceleration = new Vector2(0f, 30f);
            p.MaxLife      = 0.5f + (float)_rng.NextDouble() * 0.3f;
            p.Life         = p.MaxLife;
            p.StartColor   = color;
            p.EndColor     = color * 0f;
            p.StartSize    = 4f;
            p.EndSize      = 8f;
            p.Kind         = ParticleKind.Disc;
        }
    }

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }
}
