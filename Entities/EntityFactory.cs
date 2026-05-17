using Microsoft.Xna.Framework;

namespace MTile;

// Factory constructors for the V1 entity zoo. Balloons + balls share the Entity
// class; they differ only in initial parameters (Mass, GravityScale, Health, Color).
// Add more constructors here as new entity flavors land.
public static class EntityFactory
{
    public static Entity Balloon(Vector2 pos) => new(
        new PhysicsBody(Polygon.CreateRegular(8f, 8), pos), health: 0.5f)
    {
        Mass         = 0.5f,
        GravityScale = 0f,
        Color        = Color.HotPink,
        Faction      = Faction.Neutral,
        Sprite       = Sprites.Balloon(8f),
    };

    public static Entity Ball(Vector2 pos) => MakeBall(pos, gravityScale: 1f, Color.SteelBlue);

    // Same crasher config as Ball but weightless — for the impact-damage test
    // chamber, where the player slashes balls into a wall without gravity
    // dropping them to the floor first.
    public static Entity FloatingBall(Vector2 pos) => MakeBall(pos, gravityScale: 0f, Color.Coral);

    private static Entity MakeBall(Vector2 pos, float gravityScale, Color color)
    {
        var body = new PhysicsBody(Polygon.CreateRegular(6f, 8), pos)
        {
            // Crasher config: momentum past threshold chips tiles. Terminal-ish
            // falls (~400-500 px/s) break Dirt, harder throws break Stone.
            Impact = new ImpactDamage
            {
                Mass                 = 2.5f,
                ImpulseThreshold     = 200f,
                DamagePerUnitImpulse = 0.1f,
            },
        };
        return new Entity(body, health: 1.0f)
        {
            Mass         = 1.5f,
            GravityScale = gravityScale,
            Color        = color,
            Faction      = Faction.Neutral,
            Sprite       = Sprites.Ball(6f),
        };
    }
}
