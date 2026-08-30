using System;
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

    // Bird: a small body with two wings that beat between the two frames. The
    // wings are the animation — a flying enemy that holds a fixed pose reads as a
    // floating rock, and the beat rate is the only cue that separates it from one.
    // Drawn around local origin like every other sprite here, so the entity's
    // facing flip mirrors it for free.
    public static AnimatedSprite Bird(float radius)
    {
        var bodyColor = new Color(70, 80, 110);
        var wingColor = new Color(150, 165, 200);
        var eyeColor  = new Color(240, 200, 90);

        // wing = vertical offset of the wingtips: negative is the upstroke.
        Pose Frame(float wing) => new Pose()
            .Ring(Vector2.Zero, radius * 0.65f, bodyColor, 8, 1.5f)
            .Disc(Vector2.Zero, radius * 0.3f, bodyColor)
            // Wings sweep out from the shoulders to the tips.
            .Line(new Vector2(-radius * 0.5f, 0f), new Vector2(-radius * 1.5f, wing), wingColor, 1.5f)
            .Line(new Vector2( radius * 0.5f, 0f), new Vector2( radius * 1.5f, wing), wingColor, 1.5f)
            // Beak along +X (the facing direction) and an eye above it.
            .Line(new Vector2(radius * 0.6f, 0f), new Vector2(radius * 1.0f, 0f), eyeColor, 1f)
            .Disc(new Vector2(radius * 0.3f, -radius * 0.25f), 1.5f, eyeColor);

        var anim = new SpriteAnimation(
            new[] { Frame(-radius * 0.7f), Frame(radius * 0.5f) },
            frameDuration: 0.12f, loop: true);

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

    // Bastion: the gauntlet's emplacement. Heavy octagonal shell with an armoured
    // outer ring, a recessed aperture, and four bracing struts that read as
    // "bolted to the floor" — the silhouette has to say *immobile*, because the
    // player's correct response to it is positional rather than evasive. The
    // idle pulse is on the aperture core only; the shell never moves.
    public static AnimatedSprite Bastion(float radius)
    {
        var shell    = new Color(90, 95, 115);
        var plate    = new Color(55, 60, 78);
        var aperture = new Color(255, 90, 40);

        Pose Frame(float glow) => new Pose()
            .Ring(Vector2.Zero, radius,         shell, 8, 2.5f)
            .Ring(Vector2.Zero, radius * 0.72f, plate, 8, 1.5f)
            // Bracing struts — four short outward stubs at the diagonals.
            .Line(new Vector2(-radius * 0.7f,  radius * 0.7f), new Vector2(-radius * 1.05f, radius * 1.0f), shell, 1.5f)
            .Line(new Vector2( radius * 0.7f,  radius * 0.7f), new Vector2( radius * 1.05f, radius * 1.0f), shell, 1.5f)
            .Line(new Vector2(-radius * 0.85f, 0f),            new Vector2(-radius * 1.15f, 0f),            shell, 1.5f)
            .Line(new Vector2( radius * 0.85f, 0f),            new Vector2( radius * 1.15f, 0f),            shell, 1.5f)
            // Aperture — the muzzle the rail shot charges in.
            .Disc(Vector2.Zero, 3f + glow, aperture);

        var anim = new SpriteAnimation(
            new[] { Frame(0f), Frame(0.8f), Frame(1.4f), Frame(0.8f) },
            frameDuration: 0.22f, loop: true);

        var sprite = new AnimatedSprite();
        sprite.Play(anim);
        return sprite;
    }

    // Pouncer: compact triangular body (apex down — it lands point-first) with
    // coiled hind legs and a pair of forward eyes. Read at a glance it should
    // look sprung, so the two idle frames swap between a loaded and a released
    // leg angle rather than pulsing a colour.
    public static AnimatedSprite Pouncer(float radius)
    {
        var body = new Color(200, 130, 40);
        var limb = new Color(120, 70, 20);
        var eye  = Color.White;

        Pose Frame(float coil) => new Pose()
            // Downward-pointing triangle: two upper corners and a bottom apex.
            .Line(new Vector2(-radius,        -radius * 0.6f), new Vector2( radius,        -radius * 0.6f), body, 2f)
            .Line(new Vector2( radius,        -radius * 0.6f), new Vector2( 0f,             radius),        body, 2f)
            .Line(new Vector2( 0f,             radius),        new Vector2(-radius,        -radius * 0.6f), body, 2f)
            // Hind legs — the coil parameter folds them up toward the body.
            .Line(new Vector2(-radius * 0.75f, -radius * 0.5f),
                  new Vector2(-radius * 1.0f,   radius * (0.55f - coil * 0.5f)), limb, 1.5f)
            .Line(new Vector2( radius * 0.75f, -radius * 0.5f),
                  new Vector2( radius * 1.0f,   radius * (0.55f - coil * 0.5f)), limb, 1.5f)
            .Disc(new Vector2(-radius * 0.32f, -radius * 0.15f), 1.5f, eye)
            .Disc(new Vector2( radius * 0.32f, -radius * 0.15f), 1.5f, eye);

        var anim = new SpriteAnimation(
            new[] { Frame(0f), Frame(0.6f) },
            frameDuration: 0.28f, loop: true);

        var sprite = new AnimatedSprite();
        sprite.Play(anim);
        return sprite;
    }

    // Latcher: low, wide, many-legged. The silhouette deliberately has no clear
    // "up" — it spends as much time inverted on a ceiling as it does on a floor,
    // and a sprite with an obvious top would read as broken the moment it
    // crawls over an overhang. Six radial legs + a central eye ring do that.
    public static AnimatedSprite Latcher(float radius)
    {
        var carapace = new Color(60, 150, 130);
        var limb     = new Color(35, 95, 85);
        var eye      = new Color(180, 255, 230);

        Pose Frame(float step) => new Pose()
            .Ring(Vector2.Zero, radius * 0.72f, carapace, 8, 2f)
            .Ring(Vector2.Zero, radius * 0.34f, limb,     6, 1f)
            .Disc(Vector2.Zero, 1.8f, eye)
            // Six legs at 60° spacing; alternating legs extend on each frame so
            // the crawl reads as a gait regardless of which way is down.
            .Line(LegInner(radius, 0, 0f),   LegOuter(radius, 0, 0f,   step), limb, 1.5f)
            .Line(LegInner(radius, 1, 0f),   LegOuter(radius, 1, 0f,   1f - step), limb, 1.5f)
            .Line(LegInner(radius, 2, 0f),   LegOuter(radius, 2, 0f,   step), limb, 1.5f)
            .Line(LegInner(radius, 3, 0f),   LegOuter(radius, 3, 0f,   1f - step), limb, 1.5f)
            .Line(LegInner(radius, 4, 0f),   LegOuter(radius, 4, 0f,   step), limb, 1.5f)
            .Line(LegInner(radius, 5, 0f),   LegOuter(radius, 5, 0f,   1f - step), limb, 1.5f);

        var anim = new SpriteAnimation(
            new[] { Frame(0f), Frame(1f) },
            frameDuration: 0.20f, loop: true);

        var sprite = new AnimatedSprite();
        sprite.Play(anim);
        return sprite;
    }

    private static Vector2 LegInner(float radius, int i, float phase)
    {
        float a = i * MathHelper.TwoPi / 6f + phase;
        return new Vector2(MathF.Cos(a), MathF.Sin(a)) * (radius * 0.6f);
    }

    private static Vector2 LegOuter(float radius, int i, float phase, float extend)
    {
        float a = i * MathHelper.TwoPi / 6f + phase;
        return new Vector2(MathF.Cos(a), MathF.Sin(a)) * (radius * (1.0f + 0.35f * extend));
    }

    // Zeus: a weathered stone statue, not a machine. The silhouette is a plinth
    // (a wide base rectangle) under a robed torso with a raised arm, because the
    // whole encounter reads as "the statue on the hill turns to look at you" and
    // a mechanical shell would read as another Bastion. The only thing that
    // animates is the eye-slit and the glow at the raised hand: stone doesn't
    // move, and holding the body perfectly still is what makes the telegraphs —
    // which are the actual animation — pop.
    public static AnimatedSprite Zeus(float radius)
    {
        var stone = new Color(205, 200, 175);
        var shade = new Color(140, 136, 118);
        var spark = new Color(150, 200, 255);

        Pose Frame(float glow) => new Pose()
            // Plinth — wide, flat, and unmistakably sitting ON the ground.
            .Box(new Vector2(0f, radius * 0.82f), new Vector2(radius * 1.9f, radius * 0.5f), 0f, shade)
            .Box(new Vector2(0f, radius * 0.45f), new Vector2(radius * 1.5f, radius * 0.4f), 0f, stone)
            // Robed torso: a tapered body, narrower at the shoulders.
            .Line(new Vector2(-radius * 0.62f,  radius * 0.30f), new Vector2(-radius * 0.40f, -radius * 0.55f), stone, 2.5f)
            .Line(new Vector2( radius * 0.62f,  radius * 0.30f), new Vector2( radius * 0.40f, -radius * 0.55f), stone, 2.5f)
            .Line(new Vector2(-radius * 0.40f, -radius * 0.55f), new Vector2( radius * 0.40f, -radius * 0.55f), stone, 2f)
            // Head, and the eye-slit that is the only live thing on the body.
            .Ring(new Vector2(0f, -radius * 0.80f), radius * 0.28f, stone, 8, 2f)
            .Box(new Vector2(0f, -radius * 0.80f), new Vector2(radius * 0.34f, 1.5f + glow), 0f, spark)
            // Raised arm — bent at the elbow, hand held above the head. This is
            // where every beam's muzzle visually belongs.
            .Line(new Vector2( radius * 0.35f, -radius * 0.45f), new Vector2( radius * 0.80f, -radius * 0.85f), stone, 2f)
            .Line(new Vector2( radius * 0.80f, -radius * 0.85f), new Vector2( radius * 0.70f, -radius * 1.35f), stone, 2f)
            .Disc(new Vector2( radius * 0.70f, -radius * 1.40f), 1.8f + glow * 1.6f, spark)
            // Lowered arm, tucked against the robe.
            .Line(new Vector2(-radius * 0.35f, -radius * 0.45f), new Vector2(-radius * 0.72f,  radius * 0.10f), stone, 2f);

        var anim = new SpriteAnimation(
            new[] { Frame(0f), Frame(0.6f), Frame(1.2f), Frame(0.6f) },
            frameDuration: 0.26f, loop: true);

        var sprite = new AnimatedSprite();
        sprite.Play(anim);
        return sprite;
    }

    // Brute: thicker hex body in dark red with a single big central eye and
    // angled "horn" lines on top. Two-frame idle pulse on the eye reads as a
    // slow breath.
    public static AnimatedSprite Brute(float radius)
    {
        var bodyColor  = new Color(150, 30, 30);
        var innerColor = new Color(80, 20, 20);
        var eyeColor   = Color.Goldenrod;

        Pose Frame(float eyePulse) => new Pose()
            .Ring(Vector2.Zero, radius,         bodyColor,  6, 2f)
            .Ring(Vector2.Zero, radius * 0.65f, innerColor, 6, 1f)
            .Disc(new Vector2(0f, -1f), 2.5f + eyePulse * 0.5f, eyeColor)
            // Horns — short outward-angled lines off the top.
            .Line(new Vector2(-radius * 0.45f, -radius * 0.7f),
                  new Vector2(-radius * 0.7f,  -radius * 1.0f), bodyColor, 1f)
            .Line(new Vector2( radius * 0.45f, -radius * 0.7f),
                  new Vector2( radius * 0.7f,  -radius * 1.0f), bodyColor, 1f);

        var anim = new SpriteAnimation(
            new[] { Frame(0f), Frame(1f) },
            frameDuration: 0.30f, loop: true);

        var sprite = new AnimatedSprite();
        sprite.Play(anim);
        return sprite;
    }
}
