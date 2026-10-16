using System;
using Microsoft.Xna.Framework;

namespace MTile;

public enum PrismFigurePose { Idle, Crouch, Dash, Fall, Land, Cast }

// The Prism boss's figure (see Sprites.Prism): a floating orb — a ring with a lens
// diamond and an eye slit — under which a cloak of ribs hangs down to the feet. Local
// space is the body's: y = 0 at the body centre, the orb's top at -PrismEnemy.HeadUp,
// the hem at +PrismEnemy.FootDrop — the same envelope as the player's standing figure,
// which is also the physics body. Facing is baked per animation (a Pose has no flip),
// so there are two of each.
public sealed class PrismFigure
{
    private readonly SpriteAnimation[,] _anims = new SpriteAnimation[6, 2];

    private static readonly Color Cloak = new(90, 220, 255);
    private static readonly Color Fold  = new(50, 130, 170);
    private static readonly Color Lens  = new(200, 120, 255);
    private static readonly Color Pale  = new(230, 240, 255);

    public PrismFigure()
    {
        foreach (PrismFigurePose k in Enum.GetValues(typeof(PrismFigurePose)))
            for (int f = 0; f < 2; f++)
                _anims[(int)k, f] = Build(k, f == 0 ? -1 : 1);
    }

    public SpriteAnimation Get(PrismFigurePose kind, int facing) => _anims[(int)kind, facing < 0 ? 0 : 1];

    private static SpriteAnimation Build(PrismFigurePose kind, int facing) => kind switch
    {
        // Idle: the orb bobs and the hem drifts.
        PrismFigurePose.Idle   => new SpriteAnimation(new[] {
            Frame(facing, hover: 0f,   gather: 0f, stream: 0f, flare: 0f, sway: 0f,   glow: 0f),
            Frame(facing, hover: 0.5f, gather: 0f, stream: 0f, flare: 0f, sway: 0.5f, glow: 0.3f),
            Frame(facing, hover: 1f,   gather: 0f, stream: 0f, flare: 0f, sway: 1f,   glow: 0.6f),
            Frame(facing, hover: 0.5f, gather: 0f, stream: 0f, flare: 0f, sway: 0.5f, glow: 0.3f) }, 0.2f, true),
        // Crouch: the cloak draws in tight and the orb sinks — coiling.
        PrismFigurePose.Crouch => new SpriteAnimation(new[] {
            Frame(facing, hover: -0.8f, gather: 0.8f, stream: 0f, flare: 0f, sway: 0f, glow: 1f),
            Frame(facing, hover: -1f,   gather: 1f,   stream: 0f, flare: 0f, sway: 0.3f, glow: 1f) }, 0.08f, true),
        // Dash: the cloak streams out behind, rippling.
        PrismFigurePose.Dash   => new SpriteAnimation(new[] {
            Frame(facing, hover: 0f, gather: 0.3f, stream: 1f, flare: 0f, sway: 0f, glow: 1f),
            Frame(facing, hover: 0f, gather: 0.3f, stream: 1f, flare: 0f, sway: 1f, glow: 1f) }, 0.06f, true),
        // Fall: the hem lifts and billows.
        PrismFigurePose.Fall   => new SpriteAnimation(new[] {
            Frame(facing, hover: 0.5f, gather: 0f, stream: 0f, flare: 1f,   sway: 0f, glow: 0.5f),
            Frame(facing, hover: 0.5f, gather: 0f, stream: 0f, flare: 0.8f, sway: 1f, glow: 0.5f) }, 0.12f, true),
        // Land: splayed flat and wide.
        PrismFigurePose.Land   => new SpriteAnimation(new[] {
            Frame(facing, hover: -1f, gather: -0.8f, stream: 0f, flare: 0f, sway: 0f, glow: 1f) }, 0.1f, true),
        // Cast: the lens burns and a charge rises off the orb.
        PrismFigurePose.Cast   => new SpriteAnimation(new[] {
            Frame(facing, hover: 0.8f, gather: 0.2f, stream: 0f, flare: 0f, sway: 0f, glow: 0.6f, cast: 0.6f),
            Frame(facing, hover: 1f,   gather: 0.2f, stream: 0f, flare: 0f, sway: 0.5f, glow: 1f, cast: 1f) }, 0.1f, true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private const int Ribs = 5;

    // hover: orb bob, -1 (sunk) .. 1 (high).  gather: hem width, -1 (splayed) .. 1 (drawn
    // in).  stream: 0..1 trails the cloak back against the facing.  flare: 0..1 lifts the
    // hem up and out.  sway: 0..1 phase of the hem ripple.  glow: lens brightness.  cast:
    // 0..1 charge above the orb.
    private static Pose Frame(int facing, float hover, float gather, float stream, float flare, float sway, float glow,
                              float cast = 0f)
    {
        float head = -PrismEnemy.HeadUp, feet = PrismEnemy.FootDrop;
        float w    = PrismEnemy.HalfWidth;
        const float OrbR = 5f;

        var orb = new Vector2(facing * stream * 2f, head + OrbR + 1.5f - hover * 1.5f);
        var p   = new Pose();

        // Cloak: Ribs lines from the orb's underside to a hem; the hem polyline joins
        // them. Each rib fans out by its index; streaming shears the whole fall back
        // against the facing; flare lifts the hem and spreads it.
        float hemHalf = (w + 4f) * (1f - 0.6f * gather) * (1f + 0.8f * flare);
        float hemY    = feet - flare * 7f - MathF.Max(0f, -gather) * 3f;
        float shear   = -facing * stream * 14f;
        Vector2 prev = default;
        for (int i = 0; i < Ribs; i++)
        {
            float u    = Ribs == 1 ? 0f : (i / (float)(Ribs - 1)) * 2f - 1f;          // -1..1 across the hem
            float jag  = MathF.Sin(sway * MathHelper.TwoPi + i * 1.9f) * (1.5f + 2f * stream);
            var top    = orb + new Vector2(u * OrbR * 0.7f, OrbR * 0.6f);
            var mid    = new Vector2(u * (w + 1f) + shear * 0.45f, (orb.Y + hemY) * 0.5f);
            var hem    = new Vector2(u * hemHalf + shear + jag * 0.5f, hemY + jag - flare * MathF.Abs(u) * 5f);
            var c      = i % 2 == 0 ? Cloak : Fold;
            p.Line(top, mid, c, 1.5f).Line(mid, hem, c, 1.5f);
            if (i > 0) p.Line(prev, hem, Cloak, 1f);
            prev = hem;
        }

        // The orb: ring, lens diamond, eye slit on the facing side.
        p.Ring(orb, OrbR, Pale, 10, 1.5f)
         .Box(orb, new Vector2(3.5f + 2f * glow, 3.5f + 2f * glow), MathHelper.PiOver4, Color.Lerp(Lens, Color.White, glow * 0.5f))
         .Line(orb + new Vector2(facing * 1.5f, -1.5f), orb + new Vector2(facing * 4f, -1.5f), Pale, 1.5f);
        if (cast > 0f)
        {
            p.Disc(orb + new Vector2(0f, -OrbR - 3f - 3f * cast), 1.5f + 1.5f * cast, Color.Lerp(Lens, Color.White, cast));
            p.Ring(orb, OrbR + 2f + 3f * cast, Lens * (0.5f * cast), 10, 1f);
        }
        return p;
    }
}
