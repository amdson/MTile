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

    // Directional knockback cue (Plans/HIT_FEEL_PLAN.md phase 5): a couple of streaks
    // shot along the resolved knockback direction, so a hit's effect reads even when
    // the camera doesn't follow. `strength` (0..1, same normalization HitFeelSystem
    // and GameAudio.HitConnect use) scales count/speed/size — a light tap is barely
    // visible, a heavy hit throws a visible streak.
    public static void KnockbackCue(ParticleSystem ps, Vector2 pos, Vector2 dir, float strength)
    {
        if (dir.LengthSquared() > 1e-4f) dir.Normalize(); else dir = new Vector2(1f, 0f);
        int count = 1 + (int)(strength * 3f);
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            float jit = ((float)_rng.NextDouble() - 0.5f) * 0.25f;
            float spd = (140f + strength * 220f) * (0.8f + (float)_rng.NextDouble() * 0.4f);
            p.Position     = pos + dir * (6f + i * 4f);
            p.Velocity     = Rotate(dir, jit) * spd;
            p.Acceleration = -p.Velocity * 2.5f;
            p.MaxLife      = 0.10f + strength * 0.08f;
            p.Life         = p.MaxLife;
            p.StartColor   = Color.White;
            p.EndColor     = new Color(255, 255, 255, 0);
            p.StartSize    = 10f + strength * 14f;
            p.EndSize      = 2f;
            p.Kind         = ParticleKind.Line;
        }
    }

    // Persistent-ish debris left at a hit/crush impact point (Plans/HIT_FEEL_PLAN.md
    // phase 7). Pragmatic reuse of the particle pool as a stand-in for a real decal —
    // near-zero launch velocity and a life measured in seconds (not the usual
    // fractions-of-a-second particle burst) so the chunks settle and sit rather than
    // fly apart, reading as debris left behind instead of a burst effect.
    public static void Decal(ParticleSystem ps, Vector2 pos, Color color, int count = 3)
    {
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            float ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            float spd = 6f + (float)_rng.NextDouble() * 14f;
            p.Position        = pos;
            p.Velocity        = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd;
            p.Acceleration    = new Vector2(0f, 60f);
            p.MaxLife         = 2.2f + (float)_rng.NextDouble() * 1.2f;
            p.Life            = p.MaxLife;
            p.StartColor      = color;
            p.EndColor        = color * 0f;
            p.StartSize       = 2.5f + (float)_rng.NextDouble() * 1.5f;
            p.EndSize         = p.StartSize * 0.8f;
            p.AngularVelocity = (float)(_rng.NextDouble() - 0.5) * 2f;
            p.Kind            = ParticleKind.Square;
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
