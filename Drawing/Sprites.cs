using Microsoft.Xna.Framework;

namespace MTile;

// Factory helpers for the V1 dummy sprites. Each sprite is line-drawn — there's
// no sprite sheet yet, so "art" is rings, lines, and tiny squares in local space.
// When real pixel art lands, swap these out — Sprite.Draw is virtual, so the
// wiring at the Entity/PlayerCharacter side stays.
public static class Sprites
{
    // Hexagonal body (matches the player's 6-vertex physics polygon) + an inner
    // ring + a small "eye" dot. The 4-frame idle bobs the eye dot 0→-2→-1→0 px,
    // giving a subtle breathing motion.
    public static AnimatedSprite Player(float radius)
    {
        var bodyColor  = Color.LimeGreen;
        var innerColor = new Color(40, 180, 80);
        var eyeColor   = Color.White;

        Pose Frame(float eyeBob) => new Pose()
            .Ring(Vector2.Zero, radius,        bodyColor,  6, 1.5f)
            .Ring(Vector2.Zero, radius * 0.5f, innerColor, 6, 1f)
            .Disc(new Vector2(radius * 0.4f, -2f + eyeBob), 2f, eyeColor);

        var anim = new SpriteAnimation(
            new[] { Frame(0f), Frame(-1f), Frame(-2f), Frame(-1f) },
            frameDuration: 0.15f, loop: true);

        var sprite = new AnimatedSprite();
        sprite.Play(anim);
        return sprite;
    }

    // Balloon: pink outline with an inner highlight ring + a knot below.
    public static Sprite Balloon(float radius) => new Sprite
    {
        Pose = new Pose()
            .Ring(Vector2.Zero, radius,        Color.HotPink,                 14, 1.5f)
            .Ring(Vector2.Zero, radius * 0.55f, new Color(255, 180, 210),     12, 1f)
            .Line(new Vector2(0f, radius), new Vector2(0f, radius + 4f), Color.HotPink, 1f)
    };

    // Ball: steel-blue outline with a cross-hatch suggesting it can roll.
    public static Sprite Ball(float radius) => new Sprite
    {
        Pose = new Pose()
            .Ring(Vector2.Zero, radius, Color.SteelBlue, 14, 1.5f)
            .Line(new Vector2(-radius * 0.75f, 0f), new Vector2(radius * 0.75f, 0f), Color.SteelBlue, 1f)
            .Line(new Vector2(0f, -radius * 0.75f), new Vector2(0f, radius * 0.75f), Color.SteelBlue, 1f)
    };
}
