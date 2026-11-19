using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The siege pod's figure (Plans/PLANT_SEEDS_PLAN.md §4.4, §10): a loaded weapon pointing
// somewhere. A ribbed gourd on a rooted stem — fat breech at the back, a narrow barrel, a
// flared muzzle closed by three team-coloured petals — whose axis is the plant's locked aim.
// Five ribs across the breech light up in the team colour as it grows (the "how close to
// firing" meter both players read), and three sight beads run out of the muzzle along the
// aim. Charging runs light down the ribs toward a muzzle glow and shakes the pod; Fire blows
// the petals open and kicks the pod back. Dormant (buried) is an x-ray outline meant to be
// drawn OVER the covering tile.
//
// Parameters only — no entity type is referenced; the plant entity maps its states onto
// SiegePodFigure.PodPose. Render-only.
//
// Local space: origin = centre of the plant's own (open) cell, y down, px. The stem roots
// on the cell face named by `RootFace`, half a tile from the origin, and the figure stays
// within about two tiles of that face whatever the aim. Draw with a Sprite/SpriteTransform
// at the cell centre, rotation 0, scale 1 (or use Draw()).
public sealed class SiegePodFigure
{
    public enum PodPose { Seed, Sprout, Growing, Charging, Fire, Dormant, Wilt }

    // Which face of the plant's cell the stem grows from.
    public enum RootFace { Floor, Ceiling, LeftWall, RightWall }

    // Neutral plant greens; the team colour is the accent (petals, lit ribs, beads).
    private static readonly Color Body      = new(92, 150, 72);
    private static readonly Color BodyDark  = new(52, 98, 46);
    private static readonly Color Highlight = new(160, 210, 118);
    private static readonly Color Outline   = new(16, 34, 20);
    private static readonly Color StemCol   = new(70, 120, 52);
    private static readonly Color LeafCol   = new(110, 170, 80);
    private static readonly Color RootCol   = new(96, 70, 44);
    private static readonly Color Bore      = new(14, 18, 14);
    private static readonly Color Dead      = new(128, 104, 66);
    private static readonly Color DeadDark  = new(78, 60, 38);
    private static readonly Color Ghost     = new(214, 236, 206);

    private const float Half = Chunk.TileSize * 0.5f;
    private const int   Ribs = 4;
    private const float PodLength = 14f;

    // ── Cached access ─────────────────────────────────────────────────────────────
    // Inputs are quantised (growth 1/32, aim 1/96 turn, stage 1/20, time 12 fps over a 2 s
    // loop) so a pod reuses Poses instead of allocating per frame. Chunky by design, like
    // the other frame-snapped figures.
    private readonly Dictionary<(int, int, int, int, int, int, uint), Pose> _cache = new();

    public Pose Get(PodPose pose, float growth01, float aimRadians, RootFace root, Color team,
                    float time, float stage01 = 0f)
    {
        int gq = (int)MathF.Round(Math.Clamp(growth01, 0f, 1f) * 32f);
        int aq = ((int)MathF.Round(aimRadians / MathHelper.TwoPi * 96f) % 96 + 96) % 96;
        int sq = (int)MathF.Round(Math.Clamp(stage01, 0f, 1f) * 20f);
        int tq = ((int)MathF.Floor(time * 12f) % 24 + 24) % 24;
        var key = ((int)pose, gq, aq, (int)root, sq, tq, team.PackedValue);
        if (_cache.TryGetValue(key, out var p)) return p;
        if (_cache.Count > 4096) _cache.Clear();
        p = Build(pose, gq / 32f, aq / 96f * MathHelper.TwoPi, root, team, tq / 12f, sq / 20f);
        _cache[key] = p;
        return p;
    }

    public void Draw(DrawContext ctx, Vector2 cellCentre, PodPose pose, float growth01, float aimRadians,
                     RootFace root, Color team, float time, float stage01 = 0f, float flash = 0f)
        => Get(pose, growth01, aimRadians, root, team, time, stage01)
            .Draw(ctx, new SpriteTransform(cellCentre, 0f, 1f), Color.White, flash);

    // ── The figure ────────────────────────────────────────────────────────────────
    // growth01: 0 (just past sprout) .. 1 (ripe) — swells the pod, lengthens the stem and
    //   lights the four ribs (one per quarter of growth). Seed ignores it; Sprout scales
    //   within its own small range; Growing/Dormant/Wilt use it; Charging/Fire assume ripe.
    // aimRadians: the locked aim, any direction (0 = +x, y down).
    // time: seconds, drives the breathing sway / charge pulse (2 s loop).
    // stage01: progress through the pose itself — Charging 0..1 (glow build to the
    //   instant of release), Fire 0..1 (flash dying away), Wilt 0..1 (droop to dead).
    public static Pose Build(PodPose pose, float growth01, float aimRadians, RootFace root, Color team,
                             float time, float stage01 = 0f)
    {
        var p   = new Pose();
        var n   = Normal(root);                        // away from the surface
        var tan = new Vector2(-n.Y, n.X);
        var r   = -n * Half;                           // root point on the face
        var aim = new Vector2(MathF.Cos(aimRadians), MathF.Sin(aimRadians));
        float g = Math.Clamp(growth01, 0f, 1f);
        float s = Math.Clamp(stage01, 0f, 1f);

        if (pose == PodPose.Seed) { SeedFig(p, r, n, tan, aim, team, time); return p; }

        var a = new PodArgs { Aim = aim, Team = team, Time = time };
        float breathe = MathF.Sin(time * MathHelper.Pi);
        float stem;
        switch (pose)
        {
            case PodPose.Sprout:
                a.K = 0.32f + 0.08f * g; stem = 2.5f + 1.5f * g; a.Lit = 0; a.Petal = 0f; a.Leaves = 0.45f;
                a.Breathe = 0.02f * breathe; break;
            case PodPose.Growing:
                a.K = 0.48f + 0.52f * g; stem = 4f + 5f * g; a.Lit = LitRibs(g); a.Petal = 0f; a.Leaves = 1f;
                a.Breathe = 0.05f * breathe; a.Beads = 0.35f + 0.4f * g; break;
            case PodPose.Charging:
                a.K = 1f; stem = 9f; a.Lit = Ribs; a.Charge = MathF.Max(s, 0.15f); a.Leaves = 1f;
                a.Petal = 0.12f + 0.18f * s; a.Breathe = -0.06f * s; a.Beads = 1f;
                a.Shake = (0.35f + 0.6f * s) * (((int)(time * 12f) & 1) == 0 ? 1f : -1f); break;
            case PodPose.Fire:
                a.K = 1f; stem = 8f; a.Lit = Ribs; a.Fire = 1f - s; a.Petal = 1f; a.Leaves = 1f;
                a.Recoil = 2.5f * (1f - s); a.Breathe = -0.15f * (1f - s); break;
            case PodPose.Dormant:
                // Closed and pulled down into its own cell, so it sits inside the covering tile.
                a.K = 0.42f + 0.12f * g; stem = 1.5f; a.Lit = LitRibs(g); a.Ghost = true; a.Leaves = 0f; break;
            case PodPose.Wilt:
                a.K = (0.48f + 0.52f * g) * (1f - 0.2f * s); stem = (4f + 5f * g) * (1f - 0.35f * s);
                a.Wilt = s; a.Lit = 0; a.Petal = 0.35f * s; a.Leaves = 1f; break;
            default: throw new ArgumentOutOfRangeException(nameof(pose));
        }

        // Wilting: the pod keels over to the side it leaned toward and its nose sags —
        // never through the surface it grew from.
        float side = Vector2.Dot(aim, tan) >= 0f ? 1f : -1f;
        if (a.Wilt > 0f)
        {
            var slump = Vector2.Normalize(tan * side - n * 0.45f);
            var m = Vector2.Lerp(aim, slump, a.Wilt);
            a.Aim = m.LengthSquared() < 1e-4f ? slump : Vector2.Normalize(m);
        }

        // Stem top: straight out of the face, leaning a little toward the aim's side and
        // swaying slowly; the pod hangs its breech here.
        float lean = Vector2.Dot(a.Aim, tan);
        var top = r + n * stem + tan * (lean * 1.5f + 0.6f * breathe * (a.Ghost ? 0f : 1f));
        if (a.Wilt > 0f)
            top = r + Vector2.Lerp(top - r, tan * side * stem * 0.55f + n * MathF.Max(4.5f * a.K + 1f, stem * 0.4f), a.Wilt);
        // Buried: the closed pod is centred in its own cell (the pod body spans -0.3L..0.7L
        // from `top`), so the whole x-ray sits inside the covering tile.
        if (a.Ghost) top = -a.Aim * (PodLength * a.K * 0.2f);

        Roots(p, r, n, tan, a);
        StemFig(p, r, top, n, a);
        PodFig(p, top, a);
        return p;
    }

    private struct PodArgs
    {
        public Vector2 Aim;
        public Color   Team;
        public float   Time, K, Breathe, Petal, Charge, Fire, Recoil, Shake, Wilt, Leaves, Beads;
        public int     Lit;
        public bool    Ghost;
    }

    private static int LitRibs(float g) => Math.Clamp((int)MathF.Floor(g * Ribs + 0.001f), 0, Ribs);

    private static Vector2 Normal(RootFace f) => f switch
    {
        RootFace.Floor     => new Vector2(0f, -1f),
        RootFace.Ceiling   => new Vector2(0f, 1f),
        RootFace.LeftWall  => new Vector2(1f, 0f),
        RootFace.RightWall => new Vector2(-1f, 0f),
        _ => new Vector2(0f, -1f),
    };

    private static Color Fade(Color c, float alpha) => c * alpha;

    // ── Parts ─────────────────────────────────────────────────────────────────────

    private static void SeedFig(Pose p, Vector2 r, Vector2 n, Vector2 tan, Vector2 aim, Color team, float time)
    {
        // A dark nut half-sunk in the face, a team band round it, and a sharp team-coloured
        // tip already pointing down the aim — the threat is visible from the moment it lands.
        var c = r + n * 2f;
        p.Line(r - tan * 3f, r + tan * 3f, RootCol, 1.5f);
        p.Line(c - aim * 2.2f, c + aim * 2.2f, Outline, 5f)
         .Line(c - aim * 1.6f, c + aim * 1.6f, RootCol, 3.5f)
         .Line(c - aim * 0.4f, c + aim * 0.4f, team, 3.5f);
        float pulse = 0.6f + 0.4f * MathF.Sin(time * MathHelper.Pi);
        p.Line(c + aim * 1.8f, c + aim * 4.5f, Outline, 2.5f)
         .Line(c + aim * 1.8f, c + aim * (3.8f + 0.6f * pulse), team, 1.5f);
    }

    private static void Roots(Pose p, Vector2 r, Vector2 n, Vector2 tan, in PodArgs a)
    {
        var col = a.Ghost ? Fade(Ghost, 0.45f) : (a.Wilt > 0f ? Color.Lerp(RootCol, DeadDark, a.Wilt) : RootCol);
        p.Line(r - tan * 3.5f, r + tan * 3.5f, col, 1.5f)
         .Line(r + n * 0.5f, r - tan * 4.5f - n * 0.5f, col, 1f)
         .Line(r + n * 0.5f, r + tan * 4.5f - n * 0.5f, col, 1f);
    }

    private static void StemFig(Pose p, Vector2 r, Vector2 top, Vector2 n, in PodArgs a)
    {
        // Two-segment stem with a knee, thicker at the base.
        var knee = r + n * (Vector2.Dot(top - r, n) * 0.55f) + (top - r - n * Vector2.Dot(top - r, n)) * 0.3f;
        var col  = a.Wilt > 0f ? Color.Lerp(StemCol, Dead, a.Wilt) : StemCol;
        if (a.Ghost)
        {
            p.Line(r, knee, Fade(Outline, 0.45f), 2f).Line(knee, top, Fade(Outline, 0.45f), 2f)
             .Line(r, knee, Fade(Ghost, 0.6f), 1f).Line(knee, top, Fade(Ghost, 0.6f), 1f);
        }
        else
        {
            p.Line(r, knee, Outline, 3.5f).Line(knee, top, Outline, 3f)
             .Line(r, knee, col, 2f).Line(knee, top, col, 1.5f);
        }

        // Two leaves off the knee, swaying out of phase.
        if (a.Leaves <= 0f) return;
        var tan = new Vector2(-n.Y, n.X);
        float sway = MathF.Sin(a.Time * MathHelper.Pi) * 0.5f;
        float len  = 4.5f * a.Leaves * (a.Wilt > 0f ? 1f - 0.3f * a.Wilt : 1f);
        for (int side = -1; side <= 1; side += 2)
        {
            float droop = a.Wilt * 0.5f;
            var dir = Vector2.Normalize(tan * side + n * MathF.Max(0.08f, 0.55f - droop + side * sway * 0.3f));
            var mid = knee + dir * len * 0.55f;
            var tip = knee + dir * len;
            if (a.Ghost) { p.Line(knee, tip, Fade(Ghost, 0.35f), 1f); continue; }
            var lc = a.Wilt > 0f ? Color.Lerp(LeafCol, Dead, a.Wilt) : LeafCol;
            p.Line(knee, mid, Outline, 3f).Line(mid, tip, Outline, 2f)
             .Line(knee, mid, lc, 2f).Line(mid, tip, lc, 1f);
        }
    }

    // Pod width profile, breech (s = 0) to muzzle lip (s = 1), as a fraction of max width.
    private static readonly float[] Profile = { 0.55f, 0.88f, 1.0f, 0.98f, 0.82f, 0.58f, 0.44f, 0.42f, 0.52f, 0.66f };

    private static void PodFig(Pose p, Vector2 top, in PodArgs a)
    {
        var ax = a.Aim;
        var q  = new Vector2(-ax.Y, ax.X);
        float L = PodLength * a.K, W = 8.5f * a.K * (1f + a.Breathe);
        // Breech sits ~30 % behind the stem top; the rest reaches forward.
        var c = top - ax * (a.Recoil) + q * a.Shake;
        var back  = c - ax * L * 0.3f;
        int N = Profile.Length;
        Vector2 At(float sAlong) => back + ax * (L * sAlong);
        float Wd(int i) => W * Profile[i];
        float segLen = L / N;

        var body = a.Wilt > 0f ? Color.Lerp(Body, Dead, a.Wilt) : Body;
        var dark = a.Wilt > 0f ? Color.Lerp(BodyDark, DeadDark, a.Wilt) : BodyDark;
        var hi   = a.Wilt > 0f ? Color.Lerp(Highlight, Dead, a.Wilt) : Highlight;

        if (a.Ghost)
        {
            // X-ray: a faint wash, then edges and ribs in pale line, plus a team core dot.
            for (int i = 0; i < N; i++)
                p.Line(At(i / (float)N), At((i + 1) / (float)N), Fade(BodyDark, 0.35f), Wd(i));
            // Dark underlay first so the pale edge holds against a light tile too.
            for (int pass = 0; pass < 2; pass++)
            {
                var ec = pass == 0 ? Fade(Outline, 0.55f) : Fade(Ghost, 0.85f);
                float et = pass == 0 ? 2.2f : 1f;
                for (int side = -1; side <= 1; side += 2)
                    for (int i = 0; i < N - 1; i++)
                        p.Line(At((i + 0.5f) / N) + q * side * Wd(i) * 0.5f,
                               At((i + 1.5f) / N) + q * side * Wd(i + 1) * 0.5f, ec, et);
                p.Line(At(0.5f / N) - q * Wd(0) * 0.5f, At(0.5f / N) + q * Wd(0) * 0.5f, ec, et);
            }
            DrawRibs(p, At, Wd, q, a, ghost: true);
            Petals(p, At(1f), ax, q, W, a, ghost: true);
            return;
        }

        // Outline pass then fill pass, each as a stack of short thick strokes along the axis.
        for (int i = 0; i < N; i++)
            p.Line(At((i - 0.15f) / N), At((i + 1.15f) / N), Outline, Wd(i) + 2f);
        // Petals sit behind the lip fill so the lip edge reads over them.
        Petals(p, At(1f), ax, q, W, a, ghost: false);
        for (int i = 0; i < N; i++)
            p.Line(At(i / (float)N), At((i + 1) / (float)N), i >= N - 2 ? dark : body, Wd(i));
        // Shadow along the underside half, highlight along the top.
        for (int i = 1; i < N - 3; i++)
            p.Line(At(i / (float)N) - q * Wd(i) * 0.25f, At((i + 1) / (float)N) - q * Wd(i) * 0.25f, dark, Wd(i) * 0.35f);
        p.Line(At(0.12f) + q * Wd(1) * 0.22f, At(0.45f) + q * Wd(4) * 0.2f, hi, 1f);

        DrawRibs(p, At, Wd, q, a, ghost: false);

        // Bore: a dark mouth at the lip, glowing as it charges.
        var muzzle = At(1f);
        p.Line(At(0.9f), muzzle + ax * 0.3f, Bore, MathF.Max(1.5f, Wd(N - 1) * 0.45f));
        if (a.Charge > 0f)
        {
            var glow = Color.Lerp(a.Team, Color.White, a.Charge * 0.8f);
            float pulse = 1f - ((a.Time * 3f) % 1f);
            Glow(p, muzzle + ax * 0.8f, 0.8f + 2.2f * a.Charge + 0.4f * pulse, glow);
            Glow(p, muzzle + ax * 0.8f, 0.5f + 1.1f * a.Charge, Color.White);
            p.Ring(muzzle + ax * 1f, 3f + 5f * pulse, Fade(glow, a.Charge * (1f - pulse * 0.6f)), 10, 1f);
        }

        // Sight beads along the aim: the pod's direction, readable even when the pod is
        // small or seen edge-on. Brighten with growth; in Charging they run outward.
        if (a.Beads > 0f)
        {
            for (int b = 0; b < 3; b++)
            {
                float along = 3.5f + 3.5f * b;
                float on = a.Charge > 0f
                    ? 0.4f + 0.6f * (((int)(a.Time * 12f) - b) % 3 == 0 ? 1f : 0.3f)
                    : a.Beads * (1f - 0.22f * b);
                var at = muzzle + ax * along;
                p.Disc(at, 0.85f, Fade(Outline, 0.6f * on)).Disc(at, 0.55f, Fade(a.Team, on));
            }
        }

        if (a.Fire > 0f)
        {
            float f = a.Fire;
            var hot = Color.Lerp(a.Team, Color.White, 0.6f);
            p.Line(muzzle, muzzle + ax * (6f + 14f * f), Fade(hot, f), 2f + 3f * f)
             .Line(muzzle, muzzle + ax * (6f + 14f * f), Fade(Color.White, f), 1f + 1.5f * f)
;
            Glow(p, muzzle + ax, 1.5f + 3f * f, Fade(hot, f));
            Glow(p, muzzle + ax, 1f + 1.8f * f, Fade(Color.White, f));
            for (int k = 0; k < 8; k++)
            {
                float ang = k / 8f * MathHelper.TwoPi + 0.3f;
                var d = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                p.Line(c + d * (6f + 6f * (1f - f)), c + d * (9f + 9f * (1f - f)), Fade(hot, f * 0.8f), 1f);
            }
        }
    }

    // A filled octagon: two squares, one turned 45°. Reads round where Disc (a square) doesn't.
    private static void Glow(Pose p, Vector2 c, float r, Color col)
    {
        var size = new Vector2(r * 1.7f, r * 1.7f);
        p.Box(c, size, 0f, col).Box(c, size, MathHelper.PiOver4, col);
    }

    private static void DrawRibs(Pose p, Func<float, Vector2> at, Func<int, float> wd, Vector2 q, in PodArgs a, bool ghost)
    {
        // Ribs straddle the breech (profile slots 1..5). Lit ribs fill from the breech
        // forward with growth; charging runs a bright pulse toward the muzzle.
        int N = Profile.Length;
        for (int i = 0; i < Ribs; i++)
        {
            float sAlong = (1.2f + i * 1.25f) / N;
            int slot = Math.Clamp((int)(sAlong * N), 0, N - 1);
            float hw = wd(slot) * 0.5f - 0.4f;
            var mid = at(sAlong);
            bool lit = i < a.Lit;
            Color col;
            if (ghost) col = lit ? Fade(a.Team, 0.75f) : Fade(Ghost, 0.4f);
            else if (a.Fire > 0f) col = Color.Lerp(a.Team, Color.White, 0.7f);
            else if (a.Charge > 0f)
            {
                int run = ((int)(a.Time * 12f)) % (Ribs + 1);
                col = run == i ? Color.White : Color.Lerp(a.Team, Color.White, 0.25f * a.Charge);
            }
            else col = lit ? a.Team : Color.Lerp(BodyDark, Outline, 0.35f);
            p.Line(mid - q * hw, mid + q * hw, col, 1f);
        }
    }

    private static void Petals(Pose p, Vector2 lip, Vector2 ax, Vector2 q, float w, in PodArgs a, bool ghost)
    {
        // Three petals. Closed (open = 0) they meet in a point ahead of the lip — a bud
        // tip down the aim. Open (1) they splay back like a blown flower.
        float open = a.Petal;
        float len  = 4.5f * a.K + 2.5f * open;
        float lipHalf = w * Profile[Profile.Length - 1] * 0.5f;
        for (int k = -1; k <= 1; k++)
        {
            var baseP = lip + q * (k * lipHalf * 0.8f) - ax * 0.4f;
            float ang = k * (0.32f + 1.25f * open);           // splay from the axis
            if (k == 0) ang = 0f;
            var dir = ax * MathF.Cos(ang) + q * MathF.Sin(ang);
            var closedTip = lip + ax * len;
            var tip = k == 0 ? baseP + dir * len * (1f - 0.35f * open) : Vector2.Lerp(closedTip, baseP + dir * len, MathF.Min(1f, open * 3f));
            if (a.Wilt > 0f) tip += -ax * a.Wilt * 1.5f;
            if (ghost) { p.Line(baseP, tip, Fade(Outline, 0.5f), 2f).Line(baseP, tip, Fade(a.Team, 0.8f), 1f); continue; }
            var col = a.Wilt > 0f ? Color.Lerp(a.Team, Dead, a.Wilt * 0.8f) : a.Team;
            var midP = Vector2.Lerp(baseP, tip, 0.45f) + q * k * 0.6f;
            p.Line(baseP, midP, Outline, 3.4f).Line(midP, tip, Outline, 2.4f)
             .Line(baseP, midP, col, 2.2f).Line(midP, tip, col, 1.2f);
        }
    }
}
