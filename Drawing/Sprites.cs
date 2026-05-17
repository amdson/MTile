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

    // Turret: octagon body with a forward-pointing barrel along local +X. The
    // entity's SyncSprite rotates the sprite so the barrel tracks the aim each
    // frame. Two-frame animation pulses a small "core" inside the body so the
    // turret reads as "powered, watching."
    public static AnimatedSprite Turret(float radius)
    {
        var bodyColor   = Color.MediumPurple;
        var innerColor  = new Color(70, 50, 100);
        var coreColor   = Color.OrangeRed;
        var barrelColor = new Color(40, 30, 60);

        Pose Frame(float corePulse) => new Pose()
            .Ring(Vector2.Zero, radius,         bodyColor,  8, 1.5f)
            .Ring(Vector2.Zero, radius * 0.55f, innerColor, 8, 1f)
            // Barrel is a rotated rect along +X. LocalRotation = 0 → aligns with
            // sprite's Rotation (set by TurretEnemy.SyncSprite from _aim).
            .Box(new Vector2(radius * 0.9f, 0f),
                 new Vector2(radius * 1.3f, radius * 0.45f),
                 0f, barrelColor)
            // Core dot — pulses between frames to telegraph "alive."
            .Disc(Vector2.Zero, 2f + corePulse, coreColor);

        var anim = new SpriteAnimation(
            new[] { Frame(0f), Frame(1f) },
            frameDuration: 0.30f, loop: true);

        var sprite = new AnimatedSprite();
        sprite.Play(anim);
        return sprite;
    }

    // Bullet: tiny solid disc with a thin trailing line — a one-frame pose, no
    // animation needed. Rotation is unused (the disc reads identically at any
    // angle), so BulletProjectile doesn't bother syncing it.
    public static Sprite Bullet(float radius) => new Sprite
    {
        Pose = new Pose()
            .Disc(Vector2.Zero, radius, Color.OrangeRed)
            .Ring(Vector2.Zero, radius, new Color(255, 220, 100), 8, 1f)
    };

    // Stalker: hex body in dark orange with twin red eyes and a small jagged
    // mouth-line. Two-frame idle pulse on the eyes (subtly menacing).
    public static AnimatedSprite Stalker(float radius)
    {
        var bodyColor  = Color.DarkOrange;
        var innerColor = new Color(150, 60, 20);
        var eyeColor   = Color.Red;

        Pose Frame(float eyeBright) => new Pose()
            .Ring(Vector2.Zero, radius,        bodyColor,  6, 1.5f)
            .Ring(Vector2.Zero, radius * 0.55f, innerColor, 6, 1f)
            .Disc(new Vector2(-radius * 0.35f, -1f), 1.5f + eyeBright * 0.5f, eyeColor)
            .Disc(new Vector2( radius * 0.35f, -1f), 1.5f + eyeBright * 0.5f, eyeColor)
            // Jagged mouth — two short tilted lines suggesting fangs.
            .Line(new Vector2(-radius * 0.3f, radius * 0.35f),
                  new Vector2(-radius * 0.1f, radius * 0.55f), eyeColor, 1f)
            .Line(new Vector2( radius * 0.3f, radius * 0.35f),
                  new Vector2( radius * 0.1f, radius * 0.55f), eyeColor, 1f);

        var anim = new SpriteAnimation(
            new[] { Frame(0f), Frame(1f) },
            frameDuration: 0.25f, loop: true);

        var sprite = new AnimatedSprite();
        sprite.Play(anim);
        return sprite;
    }
}
