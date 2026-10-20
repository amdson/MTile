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

    // A charged block going off: a fast, bright radial shell out to the blast's actual
    // reach, so what the player sees is the hitbox ring rather than a generic puff. No
    // gravity on the sparks — the shell should read as a pressure front leaving the
    // epicentre, and arcing it downward would blur the radius the blast actually has.
    public static void Blast(ParticleSystem ps, Vector2 pos, float radius)
    {
        const int Count = 28;
        for (int i = 0; i < Count; i++)
        {
            ref var p = ref ps.Spawn();
            float ang  = (i / (float)Count) * MathHelper.TwoPi
                       + (float)_rng.NextDouble() * 0.2f;
            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            // Speed set so a spark covers roughly the blast radius over its life —
            // the shell arrives where the ring hitboxes were.
            float life = 0.22f + (float)_rng.NextDouble() * 0.16f;
            float spd  = radius / life * (0.7f + (float)_rng.NextDouble() * 0.5f);
            p.Position     = pos + dir * (radius * 0.15f);
            p.Velocity     = dir * spd;
            p.Acceleration = Vector2.Zero;
            p.MaxLife      = life;
            p.Life         = life;
            p.StartColor   = new Color(255, 245, 210);
            p.EndColor     = new Color(255, 140, 40) * 0f;
            p.StartSize    = 5f;
            p.EndSize      = 1f;
            p.Kind         = ParticleKind.Square;
        }
    }

    // ── Movement ────────────────────────────────────────────────────────────────
    // Soft-cloud presets use ParticleKind.Glow (ParticleSystem.SoftDot); opaque colors
    // read as dust, alpha-0 colors add light. Movement presets are fired by
    // MotionFxSystem off rendered-frame edges/levels — see its header.

    public static readonly Color DustColor = new(200, 185, 150);
    public static readonly Color AirColor  = new(235, 245, 255);

    // One soft cloud puff: grows while it drifts and decelerates, fades out.
    private static void Cloud(ParticleSystem ps, Vector2 pos, Vector2 vel, Color color,
                              float size, float life, float drag = 3f)
    {
        ref var p = ref ps.Spawn();
        p.Position     = pos;
        p.Velocity     = vel;
        p.Acceleration = -vel * drag + new Vector2(0f, -8f);
        p.MaxLife      = life * (0.8f + R() * 0.4f);
        p.Life         = p.MaxLife;
        p.StartColor   = color * 0.85f;
        p.EndColor     = color * 0f;
        p.StartSize    = size * (0.8f + R() * 0.4f);
        p.EndSize      = p.StartSize * 2.2f;
        p.Rotation     = R() * MathHelper.TwoPi;
        p.Kind         = ParticleKind.Glow;
    }

    // Ground take-off: dust kicked out along the floor both ways, biased against the
    // launch's horizontal travel (the feet push back), plus a little lift under the body.
    public static void JumpDust(ParticleSystem ps, Vector2 feet, float vx, float scale = 1f)
    {
        int count = (int)(6 * scale) + 2;
        float back = -MathF.Sign(vx) * MathF.Min(MathF.Abs(vx) / 200f, 1f);
        for (int i = 0; i < count; i++)
        {
            float side = i % 2 == 0 ? -1f : 1f;
            float spd  = (40f + R() * 50f) * scale * (1f + 0.6f * side * back);
            Cloud(ps, feet + new Vector2(Rs() * 4f, -1f), new Vector2(side * spd, -6f - R() * 14f),
                  DustColor, 6f * scale, 0.45f);
        }
        Cloud(ps, feet + new Vector2(0f, -3f), new Vector2(0f, -20f), DustColor, 9f * scale, 0.35f);
    }

    // Mid-air jump: a flat ring of air puffs blown out horizontally under the feet
    // (the "kicked off nothing" read), with a faint ring outline for definition.
    public static void AirJumpRing(ParticleSystem ps, Vector2 feet, float vx)
    {
        const int Count = 12;
        for (int i = 0; i < Count; i++)
        {
            float a   = i / (float)Count * MathHelper.TwoPi + R() * 0.3f;
            var   dir = new Vector2(MathF.Cos(a), MathF.Sin(a) * 0.3f);
            Cloud(ps, feet + dir * 4f, dir * (90f + R() * 30f) + new Vector2(-vx * 0.15f, 30f),
                  AirColor, 7f, 0.38f, drag: 4.5f);
        }
        ref var r = ref ps.Spawn();
        r.Position     = feet;
        r.Velocity     = new Vector2(-vx * 0.1f, 40f);
        r.MaxLife      = r.Life = 0.22f;
        r.StartColor   = new Color(255, 255, 255, 0) * 0.9f;
        r.EndColor     = Color.Transparent;
        r.StartSize    = 10f;
        r.EndSize      = 30f;
        r.Thickness    = 2f;
        r.Kind         = ParticleKind.Ring;
    }

    // Push off a wall: puffs fanned along the wall face (up and down) and a few
    // thrown out from it. `wallDir` is the side the wall is on (+1 = right).
    public static void WallJumpPuff(ParticleSystem ps, Vector2 contact, int wallDir)
    {
        for (int i = 0; i < 8; i++)
        {
            float along = Rs();
            var v = new Vector2(-wallDir * (15f + R() * 40f), along * 80f);
            Cloud(ps, contact + new Vector2(0f, along * 6f), v, DustColor, 6f, 0.4f);
        }
    }

    // Landing: dust splashed sideways along the floor, scaled by impact speed.
    // `impact` is the downward speed at touchdown (px/s).
    public static void LandingDust(ParticleSystem ps, Vector2 feet, float impact)
    {
        float s = MathHelper.Clamp((impact - 150f) / 450f, 0f, 1f);
        int count = 4 + (int)(10 * s);
        for (int i = 0; i < count; i++)
        {
            float side = i % 2 == 0 ? -1f : 1f;
            Cloud(ps, feet + new Vector2(Rs() * 5f, -1f),
                  new Vector2(side * (30f + R() * 60f) * (0.6f + s), -4f - R() * 12f),
                  DustColor, 5f + 4f * s, 0.4f + 0.2f * s);
        }
        if (s > 0.5f) Debris(ps, feet, DustColor, (int)(6 * s));
    }

    // Small grit chips popped up off the floor (hard landings, skids).
    private static void Debris(ParticleSystem ps, Vector2 pos, Color color, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            p.Position        = pos + new Vector2(Rs() * 8f, -1f);
            p.Velocity        = new Vector2(Rs() * 70f, -60f - R() * 90f);
            p.Acceleration    = new Vector2(0f, 500f);
            p.MaxLife         = p.Life = 0.3f + R() * 0.2f;
            p.StartColor      = color;
            p.EndColor        = color * 0f;
            p.StartSize       = 2f;
            p.EndSize         = 1f;
            p.AngularVelocity = Rs() * 10f;
            p.Kind            = ParticleKind.Square;
        }
    }

    // Ground skid on a hard reversal/stop: dust thrown forward along the travel.
    public static void SkidDust(ParticleSystem ps, Vector2 feet, float vx)
    {
        Cloud(ps, feet + new Vector2(Rs() * 3f, -1f),
              new Vector2(vx * (0.25f + R() * 0.25f), -10f - R() * 15f), DustColor, 5f, 0.35f);
        if (R() < 0.35f) Debris(ps, feet, DustColor, 1);
    }

    // Friction dust while sliding down a wall. `contact` sits on the wall face.
    public static void WallSlideDust(ParticleSystem ps, Vector2 contact, int wallDir, float vy)
    {
        Cloud(ps, contact + new Vector2(0f, Rs() * 3f),
              new Vector2(-wallDir * (10f + R() * 20f), -vy * 0.2f), DustColor, 4f, 0.3f);
    }

    // Speed lines: thin white streaks spawned around the body that hang nearly still
    // in world space as the body leaves them behind, so they read as wind rushing past.
    // `strength` 0..1 from how far over the threshold speed the body is.
    public static void SpeedLines(ParticleSystem ps, Vector2 center, Vector2 vel, float radius,
                                  float strength, int count)
    {
        if (vel.LengthSquared() < 1e-3f) return;
        var dir  = Vector2.Normalize(vel);
        var perp = new Vector2(-dir.Y, dir.X);
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            p.Position     = center + perp * Rs() * radius * 1.3f - dir * (radius * (0.2f + R() * 0.8f));
            p.Velocity     = vel * 0.12f;
            p.MaxLife      = p.Life = 0.10f + R() * 0.08f;
            p.StartColor   = new Color(255, 255, 255, 0) * (0.35f + 0.45f * strength);
            p.EndColor     = Color.Transparent;
            p.StartSize    = 10f + 22f * strength + R() * 8f;
            p.EndSize      = p.StartSize * 0.4f;
            p.Kind         = ParticleKind.Line;
        }
    }

    // Dash start: a burst of streaks and air thrown back opposite the dash, dust off the
    // floor when grounded, and a brief flash where the body launched from.
    public static void DashBurst(ParticleSystem ps, Vector2 pos, Vector2 dir, bool grounded, Vector2 feet)
    {
        if (dir.LengthSquared() > 1e-4f) dir.Normalize(); else dir = new Vector2(1f, 0f);
        for (int i = 0; i < 10; i++)
        {
            ref var p = ref ps.Spawn();
            var v = Rotate(-dir, Rs() * 0.7f) * (160f + R() * 160f);
            p.Position     = pos + new Vector2(Rs(), Rs()) * 4f;
            p.Velocity     = v;
            p.Acceleration = -v * 5f;
            p.MaxLife      = p.Life = 0.14f + R() * 0.08f;
            p.StartColor   = new Color(230, 245, 255, 0);
            p.EndColor     = Color.Transparent;
            p.StartSize    = 12f + R() * 10f;
            p.EndSize      = 2f;
            p.Thickness    = 1.5f;
            p.Kind         = ParticleKind.Line;
        }
        for (int i = 0; i < 5; i++)
            Cloud(ps, pos - dir * 4f, Rotate(-dir, Rs() * 0.9f) * (60f + R() * 60f), AirColor, 7f, 0.3f, drag: 5f);
        if (grounded) JumpDust(ps, feet, dir.X * 300f, 0.8f);
        Flash(ps, pos, 26f, new Color(200, 230, 255), 0.08f);
    }

    // While dashing: a fading afterimage blob + a streak or two left at the body.
    public static void DashTrail(ParticleSystem ps, Vector2 pos, Vector2 vel, Color tint)
    {
        ref var p = ref ps.Spawn();
        p.Position   = pos;
        p.MaxLife    = p.Life = 0.18f;
        p.StartColor = new Color(tint.R, tint.G, tint.B, (byte)0) * 0.45f;
        p.EndColor   = Color.Transparent;
        p.StartSize  = PlayerCharacter.Radius * 2.2f;
        p.EndSize    = PlayerCharacter.Radius * 1.4f;
        p.Kind       = ParticleKind.Glow;
        SpeedLines(ps, pos, vel, PlayerCharacter.Radius, 1f, 2);
    }

    // Airborne after a launch: grey smoke left behind the tumbling body (the "you got
    // sent" cue). `strength` 0..1 by speed.
    public static void LaunchSmoke(ParticleSystem ps, Vector2 pos, Vector2 vel, float strength)
    {
        Cloud(ps, pos + new Vector2(Rs(), Rs()) * 4f, -vel * 0.05f + new Vector2(Rs(), Rs()) * 10f,
              new Color(170, 170, 175), 6f + 6f * strength, 0.5f + 0.3f * strength, drag: 1.5f);
        if (R() < 0.5f * strength)
        {
            ref var p = ref ps.Spawn();
            p.Position     = pos;
            p.Velocity     = new Vector2(Rs(), Rs()) * 40f - vel * 0.1f;
            p.Acceleration = new Vector2(0f, 120f);
            p.MaxLife      = p.Life = 0.3f + R() * 0.2f;
            p.StartColor   = new Color(255, 200, 120, 0);
            p.EndColor     = Color.Transparent;
            p.StartSize    = 3f;
            p.EndSize      = 1f;
            p.Kind         = ParticleKind.Glow;
        }
    }

    // ── Combat ──────────────────────────────────────────────────────────────────

    // Blade trail: thin spark streaks strewn along the knife's swept segment this frame,
    // shed backwards off the blade (a narrow cone opposite the tip's motion), braking and
    // shrinking as they burn out. `color` is the attack glow.
    public static void BladeSparks(ParticleSystem ps, Vector2 from, Vector2 to, Color color, float dt)
    {
        var seg = to - from;
        float len = seg.Length();
        if (len < 0.5f || dt <= 0f) return;
        var dir = seg / len;
        float tipSpeed = len / dt;
        int count = Math.Clamp((int)(len / 5f), 1, 6);
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            // Speed rides the swing but is clamped, so a fast flick doesn't throw sparks
            // across the screen. Drag × life stays under 1 so the streak never stalls
            // and loses its heading (Line draws along velocity).
            float speed = MathHelper.Clamp(tipSpeed * (0.25f + 0.3f * R()), 90f, 420f);
            p.Position     = from + seg * R();
            p.Velocity     = Rotate(-dir, Rs() * 0.3f) * speed;
            p.Acceleration = -p.Velocity * 3f + new Vector2(0f, 25f);
            p.MaxLife      = p.Life = 0.14f + R() * 0.14f;
            p.StartColor   = Add(Color.Lerp(color, Color.White, 0.3f + 0.5f * R()));
            p.EndColor     = Color.Transparent;
            p.StartSize    = 7f + R() * 7f;   // streak length
            p.EndSize      = 1.5f;
            p.Thickness    = 1f;
            p.Kind         = ParticleKind.Line;
        }
    }

    // One additive flash: a short-lived glow that pops large and shrinks.
    public static void Flash(ParticleSystem ps, Vector2 pos, float size, Color color, float life = 0.1f)
    {
        ref var p = ref ps.Spawn();
        p.Position   = pos;
        p.MaxLife    = p.Life = life;
        p.StartColor = Add(color);
        p.EndColor   = Color.Transparent;
        p.StartSize  = size;
        p.EndSize    = size * 0.5f;
        p.Kind       = ParticleKind.Glow;
        ref var c = ref ps.Spawn();   // hot white core
        c.Position   = pos;
        c.MaxLife    = c.Life = life * 0.6f;
        c.StartColor = new Color(255, 255, 255, 0);
        c.EndColor   = Color.Transparent;
        c.StartSize  = size * 0.45f;
        c.EndSize    = size * 0.2f;
        c.Kind       = ParticleKind.Glow;
    }

    // Expanding outline ring — shockwave off a heavy hit or a parry.
    public static void Shockwave(ParticleSystem ps, Vector2 pos, float radius, Color color,
                                 float life = 0.2f, float thickness = 2f)
    {
        ref var p = ref ps.Spawn();
        p.Position   = pos;
        p.MaxLife    = p.Life = life;
        p.StartColor = Add(color);
        p.EndColor   = Color.Transparent;
        p.StartSize  = radius * 0.4f;
        p.EndSize    = radius * 2f;
        p.Thickness  = thickness;
        p.Kind       = ParticleKind.Ring;
    }

    // Hot embers: slow glowing motes that fall under gravity after the spark burst.
    public static void Embers(ParticleSystem ps, Vector2 pos, Vector2 dir, Color color, int count)
    {
        if (dir.LengthSquared() > 1e-4f) dir.Normalize(); else dir = new Vector2(0f, -1f);
        for (int i = 0; i < count; i++)
        {
            ref var p = ref ps.Spawn();
            p.Position     = pos;
            p.Velocity     = Rotate(dir, Rs() * 1.4f) * (50f + R() * 110f);
            p.Acceleration = new Vector2(0f, 260f) - p.Velocity * 1.2f;
            p.MaxLife      = p.Life = 0.35f + R() * 0.35f;
            p.StartColor   = Add(color);
            p.EndColor     = new Color(255, 60, 10, 0) * 0f;
            p.StartSize    = 2.5f + R() * 2f;
            p.EndSize      = 1f;
            p.Kind         = ParticleKind.Glow;
        }
    }

    // A strong hit (one that earned hitstop): flash, shockwave, a radial ring of thick
    // sparks, a hard spray along the knockback, and falling embers. `strength` 0..1.
    public static void HeavyHit(ParticleSystem ps, Vector2 pos, Vector2 dir, float strength)
    {
        if (dir.LengthSquared() > 1e-4f) dir.Normalize(); else dir = new Vector2(1f, 0f);
        Flash(ps, pos, 40f + 30f * strength, new Color(255, 230, 170), 0.12f);
        Shockwave(ps, pos, 13f + 9f * strength, new Color(255, 240, 210), 0.22f, 2.5f);
        int radial = 10 + (int)(8 * strength);
        for (int i = 0; i < radial; i++)
        {
            ref var p = ref ps.Spawn();
            float a = i / (float)radial * MathHelper.TwoPi + R() * 0.3f;
            var v = new Vector2(MathF.Cos(a), MathF.Sin(a)) * (180f + R() * 160f) * (0.8f + strength * 0.5f);
            p.Position     = pos;
            p.Velocity     = v;
            p.Acceleration = -v * 5f;
            p.MaxLife      = p.Life = 0.12f + R() * 0.08f;
            p.StartColor   = new Color(255, 250, 220, 0);
            p.EndColor     = new Color(255, 120, 30, 0) * 0f;
            p.StartSize    = 10f + R() * 8f;
            p.EndSize      = 2f;
            p.Thickness    = 2f;
            p.Kind         = ParticleKind.Line;
        }
        HitSpark(ps, pos, dir, count: 6 + (int)(6 * strength));
        Embers(ps, pos, dir, new Color(255, 190, 90), 6 + (int)(6 * strength));
    }

    // A hit landing on a guard/shield: the parry fan (GuardSpark) plus a cold flash at
    // the contact, a tight shockwave, and metallic embers that drop off the shield face.
    public static void ShieldHit(ParticleSystem ps, Vector2 pos, Vector2 dir, bool charged)
    {
        var hue = charged ? Color.Cyan : new Color(255, 210, 150);   // bronze shield glint
        GuardSpark(ps, pos, dir, charged);
        Flash(ps, pos, charged ? 42f : 30f, hue, charged ? 0.14f : 0.1f);
        Shockwave(ps, pos, charged ? 15f : 10f, hue, 0.18f, charged ? 2.5f : 1.5f);
        Embers(ps, pos, dir, charged ? new Color(150, 240, 255) : new Color(255, 200, 120), charged ? 10 : 6);
    }

    // Stab charge wind-up: motes spawned on a ring that converge onto `center` (the weapon hand), so
    // the energy visibly gathers. `f` 0..1 is the charge fraction.
    public static void ChargeMotes(ParticleSystem ps, Vector2 center, Color color, float f)
    {
        float life = 0.25f;
        float r    = 26f - 8f * f;
        float a    = R() * MathHelper.TwoPi;
        var   dir  = new Vector2(MathF.Cos(a), MathF.Sin(a));
        ref var p = ref ps.Spawn();
        p.Position   = center + dir * r;
        p.Velocity   = -dir * (r / life);
        p.MaxLife    = p.Life = life;
        p.StartColor = Add(color) * 0.2f;
        p.EndColor   = Add(color);
        p.StartSize  = 2f + 2f * f;
        p.EndSize    = 1.5f;
        p.Kind       = ParticleKind.Glow;
    }

    // Premultiplied color with alpha 0: drawn through the AlphaBlend batch it adds light.
    private static Color Add(Color c) => new(c.R, c.G, c.B, (byte)0);
    private static float R()  => (float)_rng.NextDouble();
    private static float Rs() => (float)_rng.NextDouble() * 2f - 1f;

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }
}
