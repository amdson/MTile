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
    public readonly EntityId    Source;            // attacker entity — back-attribution for recoil / AI
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
    // Newton's-third-law recoil. When > 0, CombatSystem accumulates an opposite
    // impulse per connecting cell/entity into a per-HitId tally that the attacker
    // can read via CombatSystem.PeekRecoil. Default 0 ⇒ no recoil (existing
    // behavior). The attacker scales by its own mass when applying.
    public readonly float       RecoilScale;
    // When true, tile contacts contribute recoil ONLY if the tile survived the
    // hit (DamageCell returned false). Lets a stab plough through breakable
    // material without pogo, but bounce off material too tough to break in one
    // hit. Has no effect on entity recoil.
    public readonly bool        RecoilBreakProtected;
    // Hardness floor (TileMaxHP-units). A tile contributes recoil only if its
    // material's MaxHP > this value — so a stab with floor = 0.6 ignores sand
    // (0.5) but still pogos off dirt (1.0) and stone (2.0), regardless of
    // whether the cell happens to break this frame. Independent of
    // BreakProtected; both gates can apply. Default 0 ⇒ every solid cell counts.
    public readonly float       RecoilMinMaterialHP;
    // Hitstun duration override in SECONDS (COMBAT_FEEL_PLAN Phase 2). Default
    // (< 0) means "derive from impulse magnitude" — see CombatState.OnHitRegistered.
    // Lets a weak multi-hit attack (hold-slash, impulse 60) still carry real
    // hitstun, and a heavy be tuned independently of its knockback number.
    public readonly float       HitstunSecondsOverride;
    // Struggle / grab-break channel (COMBAT_FEEL_PLAN Phase 6). When > 0, this hit
    // erodes the GRABBER's grab strength by this amount instead of dealing the usual
    // knockback / percent / hitstun — PlayerCharacter.OnHit branches on it and returns
    // early. Default 0 ⇒ a normal hit. Only the exempt GrabbedSlash sets it.
    public readonly float       GrabStrengthDamage;
    // ── Knockback model (Plans/HIT_MOMENTUM_PLAN.md) ────────────────────────────
    // Impulse (default): KnockbackImpulse above is applied directly — right for AoE /
    // field hits. Collision: the hit resolves as a 1D partially-elastic collision
    // between a virtual striker and the target (HitResolver.Resolve); the fields
    // below describe the striker and KnockbackImpulse is unused for momentum (keep
    // it authored as a direction hint — BulletProjectile deflection reads it).
    public readonly KnockbackMode Mode;
    public readonly Vector2     StrikeDir;         // unit launch direction n (authored, a feel knob)
    // Full striker world velocity, computed at publish: attackerVel + StrikeDir·strikeSpeed.
    // Carrying the composed vector (not the parts) keeps the resolver attacker-blind.
    public readonly Vector2     StrikeVelocity;
    public readonly float       StrikeMass;        // attackerMass · per-move scale
    public readonly float       Restitution;       // e ∈ [0,1]: 0 = dead thud, 1 = full bounce
    // Floor on the target's Δv (px/s) so a landed hit always visibly connects even
    // when the closing speed is ~0 (target retreating with the swing). Collision only.
    public readonly float       MinLaunch;
    // Floor on collision-mode TILE recoil (px/s of attacker Δv): however slow the
    // approach, bouncing off an eligible surface always pushes back at least this
    // hard — pogo stays a reliable movement tool, not a speed-dependent gamble.
    // Only applies while the approach speed is positive (no floor-powered pull on
    // a retreating swing); CombatSystem additionally latches the bounce to fire
    // once per attack, so a multi-frame overlap can't machine-gun the floor.
    public readonly float       MinRecoilSpeed;

    // Input cheat-sheet (details on the fields above):
    //   Identity   region (broad AABB) · hitId (dedupe key) · owner/source · targets
    //              · shape/shapePos/shapeRotation (optional narrow-phase polygon)
    //   Effect     damage · knockbackImpulse (Impulse-mode Δv·mass; in Collision
    //              mode a direction hint only — parry cone, bullet deflect)
    //              · hitstunSecondsOverride · grabStrengthDamage (struggle channel)
    //   Recoil     recoilScale (0 = off) · recoilBreakProtected · recoilMinMaterialHP
    //   Collision  mode + strikeDir (launch axis n) · strikeVelocity (attackerVel
    //              + n·strikeSpeed) · strikeMass · restitution (vs entities;
    //              tiles use their material's) · minLaunch (target Δv floor)
    //              · minRecoilSpeed (tile-pogo floor)
    public Hitbox(BoundingBox region, int hitId, float damage,
                  Vector2 knockbackImpulse, Faction owner, EntityId source,
                  Color? debugColor = null, HitTargets targets = HitTargets.All,
                  Polygon shape = null, Vector2 shapePos = default, float shapeRotation = 0f,
                  float recoilScale = 0f, bool recoilBreakProtected = false,
                  float recoilMinMaterialHP = 0f, float hitstunSecondsOverride = -1f,
                  float grabStrengthDamage = 0f,
                  KnockbackMode mode = KnockbackMode.Impulse,
                  Vector2 strikeDir = default, Vector2 strikeVelocity = default,
                  float strikeMass = 0f, float restitution = 0.5f, float minLaunch = 0f,
                  float minRecoilSpeed = 0f)
    {
        Region               = region;
        HitId                = hitId;
        Damage               = damage;
        KnockbackImpulse     = knockbackImpulse;
        Owner                = owner;
        Source               = source;
        DebugColor           = debugColor ?? Color.Red;
        Targets              = targets;
        Shape                = shape;
        ShapePos             = shapePos;
        ShapeRotation        = shapeRotation;
        RecoilScale          = recoilScale;
        RecoilBreakProtected = recoilBreakProtected;
        RecoilMinMaterialHP  = recoilMinMaterialHP;
        HitstunSecondsOverride = hitstunSecondsOverride;
        GrabStrengthDamage     = grabStrengthDamage;
        Mode                 = mode;
        StrikeDir            = strikeDir;
        StrikeVelocity       = strikeVelocity;
        StrikeMass           = strikeMass;
        Restitution          = restitution;
        MinLaunch            = minLaunch;
        MinRecoilSpeed       = minRecoilSpeed;
    }
}
