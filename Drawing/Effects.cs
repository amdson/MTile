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

    // Guard parry: a hit bounced off the shield. Sparks spray back along `dir` (which
    // points toward the attacker) in a wide fan, cold steel-blue fading to white so it
    // reads as deflection rather than as the warm HitSpark of a hit that landed.
    // `charged` — the parry also armed GuardRetaliate — throws more, faster sparks, so
    // the counter window has a visual tell of its own.
    public static void GuardSpark(ParticleSystem ps, Vector2 pos, Vector2 dir, bool charged)
    {
        if (dir.LengthSquared() > 1e-4f) dir.Normalize(); else dir = new Vector2(1f, 0f);
        int   count = charged ? 14 : 9;
        float speed = charged ? 220f : 150f;
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            // Near-hemispherical fan (±75°) off the incoming axis — a deflection
            // scatters, unlike HitSpark's tight cone along the strike direction.
            float jit = ((float)_rng.NextDouble() - 0.5f) * 2.6f;
            var   v   = Rotate(dir, jit) * (speed * (0.5f + (float)_rng.NextDouble()));
            p.Position        = pos;
            p.Velocity        = v;
            p.Acceleration    = -v * 3.5f;
            p.MaxLife         = 0.14f + (float)_rng.NextDouble() * 0.12f;
            p.Life            = p.MaxLife;
            p.StartColor      = charged ? Color.Cyan : Color.LightSteelBlue;
            p.EndColor        = new Color(255, 255, 255, 0);
            p.StartSize       = charged ? 8f : 6f;
            p.EndSize         = 1f;
            p.Kind            = ParticleKind.Line;
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

    // Crumbs shed by a flying clod: spawned within its radius, thrown back against its
    // travel and dropped by gravity, in the material's color. Called per rendered frame
    // with a count scaled to speed (CosmeticUpdateSystem) — level-triggered, so a
    // rollback needs nothing.
    public static void DirtTrail(ParticleSystem ps, Vector2 pos, Vector2 vel, float radius, Color color, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            float ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            float rr  = (float)_rng.NextDouble() * radius;
            var jitter = new Vector2(((float)_rng.NextDouble() - 0.5f), ((float)_rng.NextDouble() - 0.5f)) * 40f;
            p.Position     = pos + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rr;
            p.Velocity     = -vel * 0.2f + jitter;
            p.Acceleration = new Vector2(0f, 240f);
            p.MaxLife      = 0.25f + (float)_rng.NextDouble() * 0.25f;
            p.Life         = p.MaxLife;
            p.StartColor   = color;
            p.EndColor     = color * 0f;
            p.StartSize    = 2.5f;
            p.EndSize      = 1f;
            p.AngularVelocity = (float)(_rng.NextDouble() - 0.5) * 10f;
            p.Kind         = ParticleKind.Square;
        }
    }

    // A clod landing: a wide, low fan of chunks in the material's color, count scaled
    // by the blocks it carried. Bigger and flatter than TileBreak — it's a splash, not
    // a shatter.
    public static void MassSplash(ParticleSystem ps, Vector2 pos, Color color, int blocks)
    {
        int count = Math.Clamp(6 + blocks * 2, 6, 40);
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            // Upward-biased fan: −160°..−20° from +x (y-down), heavier toward the sides.
            float ang = MathHelper.ToRadians(-160f + (float)_rng.NextDouble() * 140f);
            float spd = 60f + (float)_rng.NextDouble() * 120f;
            p.Position        = pos;
            p.Velocity        = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd;
            p.Acceleration    = new Vector2(0f, 300f);
            p.MaxLife         = 0.45f + (float)_rng.NextDouble() * 0.35f;
            p.Life            = p.MaxLife;
            p.StartColor      = color;
            p.EndColor        = color * 0f;
            p.StartSize       = 4f;
            p.EndSize         = 1.5f;
            p.AngularVelocity = (float)(_rng.NextDouble() - 0.5) * 12f;
            p.Kind            = ParticleKind.Square;
        }
    }

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }
}
