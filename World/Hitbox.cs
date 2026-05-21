using Microsoft.Xna.Framework;

namespace MTile;

// Faction tag for self-damage filtering. Used on both Hitbox.Owner (attacker) and
// Hurtbox.Owner (target); intersection skipped when they match. Each player gets its
// own faction (Player1/Player2) so the two can damage each other while still being
// self-immune; NPC code that means "any player" must use Factions.IsPlayer, not an
// equality check. Add Player3/… here if more local players are ever needed.
public enum Faction { Player1, Player2, Enemy, Neutral }

public static class Factions
{
    // "Belongs to a player" — the test NPC/projectile code wants instead of
    // `== Faction.Player1`, so it stays correct as player factions are added.
    public static bool IsPlayer(Faction f) => f == Faction.Player1 || f == Faction.Player2;

    // The faction for the Nth player (0-based): primary = Player1, first secondary =
    // Player2, etc. Clamped to the last defined player faction.
    public static Faction ForPlayerIndex(int index) =>
        index <= 0 ? Faction.Player1 : Faction.Player2;
}

// Which kinds of targets a hitbox can damage. Useful for "shockwave" effects that
// reach further into terrain than they do entities, or buff hitboxes (heal allies
// only, etc.). Default `All` matches the simple case — damages everything.
public enum HitTargets { All, TilesOnly, EntitiesOnly }

// Value-typed, single-frame OFFENSIVE region. Renamed from the old `Hurtbox` to
// match fighting-game conventions: the attacker broadcasts hitboxes; the target
// broadcasts hurtboxes; intersection of the two triggers damage.
//
// Hitboxes have no cross-frame identity — HitboxWorld is cleared each frame and
// publishers re-broadcast. The `HitId` field is the *logical* identity: stable
// across the broadcast window of a single attack, used by CombatSystem to dedupe
// so a multi-frame slash lands on a balloon once, not 4 times.
public readonly struct Hitbox
{
    public readonly BoundingBox Region;            // broad-phase AABB; always present
    public readonly int         HitId;             // stable across an attack's broadcast window
    public readonly float       Damage;            // amount applied per intersection event
    public readonly Vector2     KnockbackImpulse;  // px/s · mass-units; target's mass divides this
    public readonly Faction     Owner;             // for self-damage filtering
    public readonly object      Source;            // opaque back-pointer (SlashAction, AI, ...)
    public readonly Color       DebugColor;        // tint for Game1.DrawHitbox overlay
    public readonly HitTargets  Targets;           // tile-only / entity-only / both (default both)
    // Optional narrow-phase shape. When non-null, CombatSystem refines AABB overlap
    // with polygon-vs-AABB SAT — lets stab (and future moves) hit precisely along a
    // diagonal axis instead of the loose axis-aligned bounding box of the rotated
    // shape. `Region` should be Shape.GetBoundingBox(ShapePos, ShapeRotation) so the
    // broad-phase test stays correct.
    public readonly Polygon     Shape;
    public readonly Vector2     ShapePos;
    public readonly float       ShapeRotation;     // radians

    public Hitbox(BoundingBox region, int hitId, float damage,
                  Vector2 knockbackImpulse, Faction owner, object source,
                  Color? debugColor = null, HitTargets targets = HitTargets.All,
                  Polygon shape = null, Vector2 shapePos = default, float shapeRotation = 0f)
    {
        Region           = region;
        HitId            = hitId;
        Damage           = damage;
        KnockbackImpulse = knockbackImpulse;
        Owner            = owner;
        Source           = source;
        DebugColor       = debugColor ?? Color.Red;
        Targets          = targets;
        Shape            = shape;
        ShapePos         = shapePos;
        ShapeRotation    = shapeRotation;
    }
}
