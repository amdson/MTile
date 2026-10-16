using System;
using Microsoft.Xna.Framework;

namespace MTile;

public enum QuillbackPose { Idle, Run, Crouch, Strike, Ball, Bristle, Dazed }

// The Quillback boss's figure (see Sprites.Quillback): a low armored dome bristling
// with quills, a pointed snout and an amber eye on the facing side, short legs, and
// a spiked tail club behind. Local space is the body's: y = 0 at the body centre, the
// dome's top at -QuillbackEnemy.HalfHeight, the feet at +HalfHeight. Facing is baked
// per animation (a Pose has no flip), so there are two of each.
public sealed class QuillbackFigure
{
    private readonly SpriteAnimation[,] _anims = new SpriteAnimation[7, 2];

    private static readonly Color Shell = new(130, 150, 185);
    private static readonly Color Plate = new(85, 100, 130);
    private static readonly Color Quill = new(225, 230, 240);
    private static readonly Color Skin  = new(205, 150, 115);
    private static readonly Color Eye   = new(255, 190, 60);
    private static readonly Color Hot   = new(255, 70, 70);

    public QuillbackFigure()
    {
        foreach (QuillbackPose k in Enum.GetValues(typeof(QuillbackPose)))
            for (int f = 0; f < 2; f++)
                _anims[(int)k, f] = Build(k, f == 0 ? -1 : 1);
    }

    public SpriteAnimation Get(QuillbackPose kind, int facing) => _anims[(int)kind, facing < 0 ? 0 : 1];

    private static SpriteAnimation Build(QuillbackPose kind, int f) => kind switch
    {
        QuillbackPose.Idle    => new SpriteAnimation(new[] {
            Walker(f, quill: 5f, sway: 0f,   stride: 0f, tail: 0f),
            Walker(f, quill: 5f, sway: 1f,   stride: 0f, tail: 0.3f) }, 0.3f, true),
        QuillbackPose.Run     => new SpriteAnimation(new[] {
            Walker(f, quill: 4f, sway: 0f, stride:  1f, tail: 0.5f),
            Walker(f, quill: 4f, sway: 1f, stride: -1f, tail: 0.8f) }, 0.07f, true),
        QuillbackPose.Crouch  => new SpriteAnimation(new[] {
            Walker(f, quill: 7f, sway: 0f, stride: 0f, tail: 1f, crouch: 1f) }, 0.1f, true),
        QuillbackPose.Strike  => new SpriteAnimation(new[] {
            Walker(f, quill: 6f, sway: 0f, stride: 1f,  tail: -0.5f, claw: 1f),
            Walker(f, quill: 6f, sway: 1f, stride: -1f, tail: -0.8f, claw: 0.6f) }, 0.06f, true),
        QuillbackPose.Ball    => new SpriteAnimation(new[] {
            Ball(f, 0f), Ball(f, 0.25f), Ball(f, 0.5f), Ball(f, 0.75f) }, 0.04f, true),
        QuillbackPose.Bristle => new SpriteAnimation(new[] {
            Ball(f, 0f, quill: 9f, open: 1f), Ball(f, 0.06f, quill: 11f, open: 0.6f) }, 0.06f, true),
        QuillbackPose.Dazed   => new SpriteAnimation(new[] {
            Walker(f, quill: 2f, sway: 0f, stride: 0f, tail: -1f, crouch: 0.6f, daze: 1f),
            Walker(f, quill: 2f, sway: 1f, stride: 0f, tail: -1f, crouch: 0.4f, daze: 1f) }, 0.25f, true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private const int DomeQuills = 7;

    // On its feet. quill: quill length. sway: 0..1 quill wobble. stride: -1..1 leg
    // swing. tail: -1 (dragging) .. 1 (raised). crouch: 0..1 dome sinks. claw: 0..1
    // forepaw out. daze: droops the snout, closes the eye.
    private static Pose Walker(int f, float quill, float sway, float stride, float tail,
                               float crouch = 0f, float claw = 0f, float daze = 0f)
    {
        float hw = QuillbackEnemy.HalfWidth, hh = QuillbackEnemy.HalfHeight;
        float sink = crouch * 2.5f;
        var p = new Pose();

        // Legs: two short strokes, swinging opposite.
        float footY = hh;
        p.Line(new Vector2(-4f, 3f + sink), new Vector2(-4f - stride * 2.5f, footY), Skin, 2f)
         .Line(new Vector2( 4f, 3f + sink), new Vector2( 4f + stride * 2.5f, footY), Skin, 2f);

        // Tail: a stalk out the back ending in a spiked club.
        var tailBase = new Vector2(-f * (hw - 2f), 1f + sink);
        var tailTip  = tailBase + new Vector2(-f * 7f, -3f - 4f * tail);
        p.Line(tailBase, tailTip, Plate, 2f)
         .Ring(tailTip, 2.5f, Quill, 6, 1.5f);
        for (int i = 0; i < 3; i++)
        {
            float a = MathHelper.Pi * (0.9f + 0.4f * i) * (f > 0 ? 1f : -1f) + (f > 0 ? 0f : MathHelper.Pi);
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            p.Line(tailTip + d * 2.5f, tailTip + d * 5.5f, Quill, 1f);
        }

        // Belly + snout + eye.
        var snout = new Vector2(f * (hw + 3f), 3f + sink + daze * 2f);
        p.Line(new Vector2(-f * (hw - 2f), 4f + sink), new Vector2(f * (hw - 3f), 4f + sink), Skin, 2f)
         .Line(new Vector2(f * (hw - 3f), 4f + sink), snout, Skin, 2f)
         .Line(new Vector2(f * (hw - 3f), -1f + sink), snout, Skin, 1.5f)
         .Disc(snout, 1.2f, Color.Black);
        if (daze > 0f) p.Line(new Vector2(f * 6f, sink - 1f), new Vector2(f * 9f, sink), Eye, 1f);
        else           p.Disc(new Vector2(f * 7.5f, sink - 0.5f), 1.4f, Eye);
        if (claw > 0f)
        {
            var paw = new Vector2(f * (hw + 2f + 5f * claw), 1f + sink);
            p.Line(new Vector2(f * (hw - 4f), 3f + sink), paw, Skin, 2f);
            for (int i = -1; i <= 1; i++)
                p.Line(paw, paw + new Vector2(f * 3f, i * 2f), Hot, 1f);
        }

        // The dome: plated arc from the back to the shoulder, quills raking back off it.
        Vector2 prev = default;
        for (int i = 0; i <= DomeQuills; i++)
        {
            float u = i / (float)DomeQuills;                         // 0 = rump, 1 = shoulder
            float a = MathHelper.Pi * (1f - u);                       // pi .. 0 over the top
            var at  = new Vector2(-f * MathF.Cos(a) * (hw - 1f) * 0.95f, -MathF.Sin(a) * (hh - 1f) + 2f + sink);
            if (i > 0) p.Line(prev, at, Shell, 2f);
            prev = at;
            if (i == DomeQuills) break;
            var outward = Vector2.Normalize(new Vector2(-f * MathF.Cos(a) * 0.8f - f * 0.6f, -MathF.Sin(a) - 0.2f));
            float wob   = MathF.Sin(sway * MathHelper.TwoPi + i * 1.7f) * 0.8f;
            p.Line(at, at + outward * (quill + wob), Quill, 1f);
        }
        p.Line(new Vector2(-f * (hw - 4f), sink), new Vector2(f * (hw - 6f), sink - 2f), Plate, 1.5f);
        return p;
    }

    // Curled: a spiky ball. spin: 0..1 rotation phase (backwards against the roll).
    // open: 0..1 flashes the shell seam red (the storm's vulnerable curl).
    private static Pose Ball(int f, float spin, float quill = 5f, float open = 0f)
    {
        float r = QuillbackEnemy.HalfHeight;
        var p = new Pose()
            .Ring(Vector2.Zero, r, Shell, 12, 2f)
            .Ring(Vector2.Zero, r * 0.55f, Plate, 8, 1.5f);
        if (open > 0f) p.Ring(Vector2.Zero, r * 0.55f, Color.Lerp(Plate, Hot, open), 8, 2f);
        const int N = 12;
        for (int i = 0; i < N; i++)
        {
            float a = (i / (float)N + f * spin / N * 4f) * MathHelper.TwoPi;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            p.Line(d * r, d * (r + quill), Quill, 1f);
        }
        return p;
    }
}
