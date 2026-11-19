using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The Snapper's figure (Plans/PLANT_SEEDS_PLAN.md §4.2): a flytrap-like jawed head on a short
// thick stalk, rooted in a dirt surface. Pure parameters in, one Pose out — no entity
// dependence, render-only. Colours follow the Prism convention (dimming = `c * a`).
//
// Local space: y = 0 is the rooted surface where the stalk meets the dirt, -y is OUT of the
// surface (authored for a floor, then mapped onto the real root face). One tile is 11 px; a
// mature plant stands ~11 px of stalk and the open head reaches ~20 px, so it may overhang
// its cell up to ~2 tiles.
//
// Reading the state at a glance (the whole point):
//   armed      green, jaws open ~70 degrees each way, head tracking `lean`, gentle sway.
//   wind-up    head rears BACK from the target, jaws flung widest, jaws/collar flash
//              white/accent, alert ticks radiate from the head.
//   bite       head lunges at the target, jaws slammed shut, white streaks and a flash.
//   spent      jaws shut, head hanging low, colour drained to grey-olive; climbs and
//              reopens as rearm01 goes 0 -> 1.
//   dormant    buried: outline-only ghost with an X, so it still reads through a tile.
public static class SnapperFigure
{
    public enum State { Seed, Sprout, Idle, WindUp, Bite, Rearm, Dormant, Wilt }

    // Which face of the world the stalk is rooted to. The figure grows along that face's
    // outward normal; `lean` runs along the surface tangent (floor/ceiling: +x right;
    // walls: +y down).
    public enum RootFace { Floor, Ceiling, WallOnLeft, WallOnRight }

    public const float PulseOmega  = 14f;                       // wind-up flash, rad/s (~2.2 Hz)
    public const float WindupLoop  = MathF.PI * 2f / PulseOmega;   // seconds per flash period
    public const float IdleOmega   = 2f;                        // idle sway, rad/s
    public const float IdleLoop    = MathF.PI * 2f / IdleOmega; // seconds per sway period

    public const float StalkHeight = 11f;   // mature stalk length, px
    public const float JawLength   = 8f;    // mature jaw length, px

    private static readonly Color Leaf    = new(72, 186, 78);
    private static readonly Color LeafDk  = new(34, 112, 56);
    private static readonly Color LeafHi  = new(140, 225, 110);
    private static readonly Color Spent   = new(122, 132, 98);
    private static readonly Color SpentDk = new(78, 88, 66);
    private static readonly Color Tooth   = new(246, 240, 214);
    private static readonly Color Root    = new(92, 62, 38);
    private static readonly Color SeedCol = new(150, 110, 62);
    private static readonly Color Dry     = new(158, 132, 58);
    private static readonly Color DryDk   = new(104, 82, 40);
    private static readonly Color Alarm   = new(255, 245, 190);

    /// <summary>
    /// Builds one frame. <paramref name="growth01"/>: 0 = young plant size .. 1 = full (Idle/WindUp/
    /// Bite/Rearm/Dormant/Wilt scale 0.45..1; Seed/Sprout grow a little).
    /// <paramref name="lean"/>: -1..1 along the surface tangent toward the target (0 = straight out).
    /// <paramref name="rearm01"/>: 0..1, only read by Rearm (0 just spent, 1 about to be armed).
    /// <paramref name="team"/>: accent colour (jaw rims, collar, mouth lining).
    /// <paramref name="time"/>: seconds; drives sway / flashing only. Allocates a Pose: cache per quantised args if hot.
    /// </summary>
    public static Pose Build(State pose, float growth01, float lean, float rearm01,
                             RootFace rootFace, Color team, float time)
    {
        var b = new Builder(rootFace, pose == State.Dormant);
        growth01 = MathHelper.Clamp(growth01, 0f, 1f);
        lean     = MathHelper.Clamp(lean, -1f, 1f);
        rearm01  = MathHelper.Clamp(rearm01, 0f, 1f);
        float g = MathHelper.Lerp(0.45f, 1f, growth01);
        float dir = lean < -0.001f ? -1f : 1f;                  // which way a hanging head falls
        float sway = MathF.Sin(time * IdleOmega) * 0.5f + MathF.Sin(time * 2f * IdleOmega + 1f) * 0.25f;   // period IdleLoop
        float up = -MathHelper.PiOver2;

        switch (pose)
        {
            case State.Seed:   DrawSeed(b, team, growth01); break;
            case State.Sprout: DrawSprout(b, team, growth01, time); break;
            case State.Idle:
                Plant(b, team, g, nx: lean * 2.2f + sway * 0.9f, hf: 1f, phi: up + lean * 1.0f + sway * 0.08f,
                      open: 0.72f + 0.04f * sway, armed: 1f, flash: 0f, lunge: 0f);
                break;
            case State.WindUp:
            {
                // Flash rate is capped (photosensitivity): PulseOmega = 14 rad/s ~ 2.2 Hz; the
                // positional shake is 3x that (~6.7 Hz) so one WindupLoop tiles both exactly.
                float shake = MathF.Sin(time * 3f * PulseOmega) * 0.5f;
                float pulse = 0.5f + 0.5f * MathF.Sin(time * PulseOmega);
                float nx = -dir * 4.0f * g + shake;
                Plant(b, team, g, nx: nx, hf: 0.9f, phi: up - dir * 0.55f, open: 1.0f, armed: 1f, flash: pulse, lunge: 0f);
                Ticks(b, g, nx, StalkHeight * g * 0.9f, pulse);
                break;
            }
            case State.Bite:
                Plant(b, team, g, nx: dir * 8.5f * g, hf: 0.62f, phi: up + dir * 1.25f,
                      open: 0.04f, armed: 1f, flash: 0.35f, lunge: 1f);
                Streaks(b, g, dir);
                break;
            case State.Rearm:
            {
                float droop = 1f - rearm01;
                float open  = MathHelper.Lerp(0.03f, 0.40f, rearm01 * rearm01);
                // jaw axis falls from "up and toward the target" to "hanging down on that side"
                float phi = dir > 0 ? MathHelper.Lerp(up + 0.7f, 0.55f, droop)
                                    : MathHelper.Lerp(up - 0.7f, -MathHelper.Pi - 0.55f, droop);
                Plant(b, team, g, nx: dir * (2f + 4.5f * droop) * g, hf: 0.9f - 0.45f * droop, phi: phi,
                      open: open, armed: rearm01 * 0.55f, flash: 0f, lunge: 0f);
                break;
            }
            case State.Dormant:
                // Curled up small INSIDE its own (covered) cell — callers pass growth01 0 — so
                // no jaw pokes out of the block on top of it and reads as an awake plant.
                Plant(b, team, g, nx: dir * 1.5f, hf: 0.75f, phi: dir > 0 ? -0.6f : -MathHelper.Pi + 0.6f,
                      open: 0.04f, armed: 0f, flash: 0f, lunge: 0f);
                // an X over the head: 'something is under here, inert'
                var c = V(dir * 4.5f * g, -StalkHeight * g * 0.6f);
                b.Line(c + V(-2.5f, -2.5f), c + V(2.5f, 2.5f), team, 1f);
                b.Line(c + V(-2.5f, 2.5f), c + V(2.5f, -2.5f), team, 1f);
                break;
            case State.Wilt:
                Wilt(b, team, g, dir, time);
                break;
        }
        return b.Pose;
    }

    // ---------------------------------------------------------------- parts

    private static void DrawSeed(Builder b, Color team, float growth01)
    {
        float s = 1f + 0.25f * growth01;
        b.Fill(new[] { V(-3.6f * s, 0.6f), V(-2.2f * s, -2.4f * s), V(0f, -3.4f * s), V(2.2f * s, -2.4f * s), V(3.6f * s, 0.6f) }, SeedCol, Root);
        b.Line(V(-1.2f, -2.8f * s), V(0.4f, -1.0f), Root, 0.8f);
        b.Line(V(0.4f, -1.0f), V(1.6f, -2.2f * s), Root, 0.8f);
        b.Disc(V(0f, -3.8f * s), 1.3f, team);                     // team speck on top
        b.Line(V(-3f, 1.2f), V(-5f, 2.4f), Root, 1f);
        b.Line(V(3f, 1.2f), V(5f, 2.4f), Root, 1f);
    }

    private static void DrawSprout(Builder b, Color team, float growth01, float time)
    {
        float h = 5.5f + 2.5f * growth01;
        float bend = MathF.Sin(time * 2f) * 0.5f;
        var tip = V(bend, -h);
        b.Line(V(-2f, 1.5f), V(0f, 0f), Root, 1f);
        b.Line(V(2f, 1.5f), V(0f, 0f), Root, 1f);
        b.Line(V(0f, 0f), tip, Leaf, 2.2f);
        b.Line(V(0f, -1f), tip, LeafHi, 0.8f);
        // Two shut bud-leaves: they foreshadow the jaws.
        b.Fill(new[] { tip, tip + V(-4.8f, -1.2f), tip + V(-2.6f, 1.9f) }, Leaf, LeafDk);
        b.Fill(new[] { tip, tip + V(4.8f, -1.2f), tip + V(2.6f, 1.9f) }, Leaf, LeafDk);
        b.Disc(tip + V(0f, -1f), 1.5f, team);
    }

    // The full jawed plant. nx: neck x offset; hf: stalk height factor; phi: jaw axis angle
    // (y-down radians, -PI/2 = straight out); open: jaw half-angle (rad); armed: 0..1 colour
    // saturation; flash: 0..1 whitening; lunge: 1 = arched toward the target.
    private static void Plant(Builder b, Color team, float g, float nx, float hf, float phi, float open,
                              float armed, float flash, float lunge)
    {
        float h = StalkHeight * g * hf;
        Color body = Color.Lerp(Spent, Leaf, armed);
        Color dark = Color.Lerp(SpentDk, LeafDk, armed);
        Color hi   = Color.Lerp(Spent, LeafHi, armed);
        Color rim  = Color.Lerp(Color.Lerp(team, Spent, 0.65f), team, armed);
        Color mouth = Color.Lerp(team, Color.Black, 0.45f);
        mouth = Color.Lerp(Color.Lerp(mouth, SpentDk, 0.6f), mouth, armed);
        if (flash > 0f)
        {
            body  = Color.Lerp(body, Alarm, flash * 0.45f);
            rim   = Color.Lerp(rim, Color.White, flash * 0.7f);
            mouth = Color.Lerp(mouth, team, flash * 0.8f);
        }

        // roots: three short dirt-brown claws below the surface line
        b.Line(V(-1f, 0f), V(-5f, 2.8f), Root, 1.4f);
        b.Line(V(1f, 0f), V(5f, 2.8f), Root, 1.4f);
        b.Line(V(0f, 0f), V(0f, 3.4f), Root, 1.4f);

        // stalk: quadratic curve root -> neck as a tapering ribbon
        var P0 = V(0f, 0f);
        var P2 = V(nx, -h);
        var P1 = V(-nx * 0.35f + (nx > 0 ? -0.8f : 0.8f), -h * 0.55f);
        if (lunge > 0.5f) P1 = V(nx * 0.15f, -h * 0.9f);              // arched like a striking neck
        const int N = 7;
        var left = new Vector2[N + 1]; var right = new Vector2[N + 1]; var mid = new Vector2[N + 1];
        for (int i = 0; i <= N; i++)
        {
            float t = i / (float)N, u = 1f - t;
            var p = u * u * P0 + 2f * u * t * P1 + t * t * P2;
            var tan = 2f * u * (P1 - P0) + 2f * t * (P2 - P1);
            if (tan.LengthSquared() < 1e-4f) tan = V(0f, -1f);
            tan.Normalize();
            var n = V(-tan.Y, tan.X);
            float w = MathHelper.Lerp(2.4f, 1.7f, t) * (0.7f + 0.3f * g);
            left[i] = p + n * w; right[i] = p - n * w; mid[i] = p;
        }
        var ribbon = new List<Vector2>();
        for (int i = 0; i <= N; i++) ribbon.Add(left[i]);
        for (int i = N; i >= 0; i--) ribbon.Add(right[i]);
        b.Fill(ribbon.ToArray(), body, dark);
        for (int i = 1; i < N; i++) b.Line(mid[i - 1], mid[i], hi, 0.7f);         // highlight spine

        // two flap leaves part-way up the stalk
        var lp = mid[2];
        float lf = 0.7f + 0.3f * g;
        b.Fill(new[] { lp, lp + V(-6.2f * lf, -1.2f * lf), lp + V(-3.4f * lf, 1.8f) }, dark, dark);
        b.Fill(new[] { lp + V(0.6f, -0.6f), lp + V(5.4f * lf, -2.2f * lf), lp + V(3f * lf, 1.2f) }, dark, dark);

        // team collar under the head
        var neck = mid[N];
        var tanN = mid[N] - mid[N - 1];
        if (tanN.LengthSquared() > 1e-4f) tanN.Normalize(); else tanN = V(0f, -1f);
        var perpN = V(-tanN.Y, tanN.X);
        b.Line(neck + perpN * 2.4f - tanN, neck - perpN * 2.4f - tanN, rim, 1.7f);

        Head(b, neck, phi, open, g, body, dark, hi, rim, mouth);
    }

    private static void Head(Builder b, Vector2 hinge, float phi, float openHalf, float g,
                             Color body, Color dark, Color hi, Color rim, Color mouth)
    {
        float L = JawLength * g;
        float k = 0.75f + 0.25f * g;
        Vector2 U(float a) => V(MathF.Cos(a), MathF.Sin(a));
        float aUp = phi - openHalf, aDn = phi + openHalf;
        Vector2 Pt(float a, float s, float along, float across)    // jaw-local -> head space
        {
            var u = U(a); var p = V(-u.Y, u.X);
            return hinge + u * along + p * (s * across);
        }
        // Mouth lining: visible only when open.
        if (openHalf > 0.12f)
            b.Fill(new[] { hinge - U(phi) * 0.6f, Pt(aUp, -1, L, 0f), Pt(phi, 1, L * 1.05f, 0f), Pt(aDn, 1, L, 0f) }, mouth, mouth);
        for (int jaw = 0; jaw < 2; jaw++)
        {
            float a = jaw == 0 ? aUp : aDn;
            float s = jaw == 0 ? -1f : 1f;                           // outer side sign
            Vector2 Q(float along, float across) => Pt(a, s, along, across);
            var outer = new[] { Q(-1.2f, 0f), Q(0.5f, 2.1f * k), Q(0.35f * L, 3.0f * k), Q(0.75f * L, 2.0f * k), Q(L * 1.12f, 0.2f) };
            var poly = new List<Vector2>(outer) { Q(L * 1.12f, -0.2f) };
            b.Fill(poly.ToArray(), body, dark);
            b.Line(Q(0.1f * L, 0.9f * k), Q(0.92f * L, 0.5f), hi, 0.7f);
            b.Line(Q(0.45f * L, 1.1f * k), Q(0.6f * L, 3.0f * k), dark, 0.6f);
            for (int i = 1; i < outer.Length; i++) b.Line(outer[i - 1], outer[i], rim, 1.1f);
            // teeth, interleaved between the jaws
            for (int i = 0; i < 4; i++)
            {
                float t0 = (0.22f + 0.2f * i + (jaw == 1 ? 0.1f : 0f)) * L;
                float tw = 0.8f * (0.8f + 0.2f * g);
                b.Fill(new[] { Q(t0 - tw, 0f), Q(t0 + tw, 0f), Q(t0, -2.4f * k) }, Tooth, Tooth);
            }
        }
        b.Disc(hinge, 2.0f * k, dark);
        b.Disc(hinge, 1.1f * k, body);
    }

    private static void Ticks(Builder b, float g, float headX, float headY, float pulse)
    {
        // Alert ticks fanned above the reared head — the 'about to bite' beacon.
        var c = V(headX, -headY - 3f * g);
        Color col = Color.Lerp(new Color(255, 90, 60), Alarm, pulse);
        for (int i = -2; i <= 2; i++)
        {
            float a = -MathHelper.PiOver2 + i * 0.5f;
            var d = V(MathF.Cos(a), MathF.Sin(a));
            float r0 = 7f * g + 1f, r1 = r0 + (i == 0 ? 5.5f : 4f) + 1.5f * pulse;
            b.Line(c + d * r0, c + d * r1, col, 1.4f);
        }
    }

    private static void Streaks(Builder b, float g, float dir)
    {
        // Motion lines trailing back from the lunging head, and an impact star at the tips.
        for (int i = 0; i < 3; i++)
        {
            float y = -StalkHeight * g * (0.45f + 0.18f * i);
            b.Line(V(dir * 8.5f * g - dir * 3f, y), V(dir * 8.5f * g - dir * (9f + 2f * i), y + 1.5f - i), Alarm, 1.1f);
        }
        var tip = V(dir * 17f * g, -StalkHeight * g * 0.62f - 1.5f);
        for (int i = 0; i < 4; i++)
        {
            float a = i * MathHelper.PiOver2 + 0.4f;
            var d = V(MathF.Cos(a), MathF.Sin(a));
            b.Line(tip + d * 1.5f, tip + d * 4.5f, Color.White, 1.2f);
        }
    }

    private static void Wilt(Builder b, Color team, float g, float dir, float time)
    {
        float h = StalkHeight * g * 0.6f;
        var neck = V(dir * 7f * g, -h * 0.35f);
        b.Line(V(-1f, 0f), V(-5f, 2.8f), Root, 1.4f);
        b.Line(V(1f, 0f), V(5f, 2.8f), Root, 1.4f);
        var P1 = V(dir * 0.5f, -h);
        const int N = 6;
        Vector2 prev = V(0f, 0f);
        for (int i = 1; i <= N; i++)
        {
            float t = i / (float)N, u = 1 - t;
            var p = 2 * u * t * P1 + t * t * neck;
            b.Line(prev, p, i % 2 == 0 ? Dry : DryDk, MathHelper.Lerp(3.6f, 2.4f, t));
            prev = p;
        }
        // slack, half-open jaws hanging at the end of the stalk
        Head(b, neck, dir > 0 ? 0.9f : -MathHelper.Pi - 0.9f, 0.32f, g * 0.9f, Dry, DryDk, Dry,
             Color.Lerp(team, DryDk, 0.7f), DryDk);
        // falling leaf bits
        for (int i = 0; i < 2; i++)
        {
            float t = (time * 0.6f + i * 0.5f) % 1f;
            b.Disc(V(dir * (4f + 5f * t) * (i == 0 ? 1 : -0.6f), -h * 0.8f + 12f * t * t), 0.9f * (1f - t), DryDk);
        }
    }

    private static Vector2 V(float x, float y) => new(x, y);

    // ---------------------------------------------------------------- drawing helper

    // Wraps a Pose: authors in floor space, maps every point onto the real root face,
    // fills polygons by scanline strokes (a Pose has only line/ring/disc/box), and in
    // ghost mode (Dormant) draws outlines only, dimmed.
    private sealed class Builder
    {
        public readonly Pose Pose = new();
        private readonly RootFace _face;
        private readonly bool _ghost;
        public Builder(RootFace face, bool ghost) { _face = face; _ghost = ghost; }

        private Vector2 X(Vector2 p) => _face switch
        {
            RootFace.Ceiling     => new Vector2(p.X, -p.Y),
            RootFace.WallOnLeft  => new Vector2(-p.Y, p.X),
            RootFace.WallOnRight => new Vector2(p.Y, p.X),
            _ => new Vector2(p.X, p.Y + PlantFigureKit.RootY),   // floor: surface at the cell's bottom edge
        };

        private Color C(Color c) => _ghost ? c * 0.7f : c;

        public void Line(Vector2 a, Vector2 b, Color c, float w)
        {
            if (_ghost) w = MathF.Min(w, 1f);
            Pose.Line(X(a), X(b), C(c), w);
        }

        public void Disc(Vector2 c, float r, Color col)
        {
            if (_ghost) Pose.Ring(X(c), r, C(col), 10, 0.9f);
            else Pose.Disc(X(c), r, col);
        }

        // Filled polygon (even-odd scanline) with an outline in `edge`.
        public void Fill(Vector2[] pts, Color fill, Color edge)
        {
            int n = pts.Length;
            if (!_ghost)
            {
                float y0 = float.MaxValue, y1 = float.MinValue;
                foreach (var p in pts) { y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y); }
                var xs = new List<float>();
                for (float y = y0 + 0.35f; y <= y1; y += 0.7f)
                {
                    xs.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        var a = pts[i]; var c = pts[(i + 1) % n];
                        if ((a.Y <= y && c.Y > y) || (c.Y <= y && a.Y > y))
                            xs.Add(a.X + (y - a.Y) / (c.Y - a.Y) * (c.X - a.X));
                    }
                    xs.Sort();
                    for (int i = 0; i + 1 < xs.Count; i += 2)
                        Pose.Line(X(V(xs[i], y)), X(V(xs[i + 1], y)), fill, 0.95f);
                }
            }
            var ec = _ghost ? C(Color.Lerp(fill, edge, 0.3f)) : edge;
            for (int i = 0; i < n; i++)
                Pose.Line(X(pts[i]), X(pts[(i + 1) % n]), ec, _ghost ? 0.9f : 0.8f);
        }
    }
}
