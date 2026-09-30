using Microsoft.Xna.Framework;

namespace MTile;

// Which pool action a spec configures. `Special` (the zero value) is what an
// action that carries no spec reports — Zeus / Shrike / Wizard / Warden / Aspid /
// Template actions keep their own private knobs and never read `Spec`.
// See Plans/FIGHTER_DESIGN_PLAN.md §4.
public enum ActionKind
{
    Special = 0,
    Melee,
    Contact,
    Lunge,
    Slam,
    Ranged,
    RailShot,
    PounceSlam,
    Lash,
}

// Every tunable an enemy pool action reads, as a value struct the blueprint
// carries. The action classes are flyweight BEHAVIOUR; this is the KNOBS. A
// designer (or the fighter compiler) varies an attack by handing the action a
// different spec, never by subclassing.
//
// Not every field means something to every kind — the table in Default() is the
// contract. Fields a kind does not read are left at zero there and ignored by it.
// Nothing here is snapshotted: specs are construction inputs, rebuilt through the
// registered blueprint on rehydrate, exactly like the flyweights themselves.
public struct ActionSpec
{
    public ActionKind Kind;

    // The Windup → Active → Recovery triad, seconds. PopulateDurations copies these
    // into EnemyActionVars at Enter and on restore.
    public float Windup, Active, Recovery;

    // Trigger band. MinRange is 0 for actions with no inner exclusion. Slam reads
    // MaxRange as its max horizontal distance to the target. Contact reads it as its
    // trigger range.
    public float MinRange, MaxRange, VerticalSlack;

    // HP straight off the target. Ranged kinds pass it to the projectile they spawn.
    // PounceSlam lerps Damage → DamageMax across FallSpeedMin → FallSpeedRef.
    public float Damage, DamageMax;

    // Knockback impulse. Melee / Contact / Lunge / Slam: (along facing, vertical).
    // Lash: X along the frozen aim axis, Y added vertically (its "up bias").
    // PounceSlam: Knockback.X is the minimum magnitude, KnockbackMax the saturated one.
    public Vector2 Knockback;
    public float   KnockbackMax;

    // Hitbox extents. Reach is the along-axis length (Melee, Lash), the downward
    // offset of the slam box (Slam, PounceSlam), or the muzzle offset (Ranged,
    // RailShot). HalfWidth / HalfHeight are the box half-extents; Contact uses
    // HalfWidth alone as its body half-extent.
    public float Reach, HalfWidth, HalfHeight;

    // Lunge dash speed, or projectile launch speed.
    public float Speed;

    // Fall-speed gate for the slam kinds (px/s, Y-down so positive = falling).
    public float FallSpeedMin, FallSpeedRef;

    // RailShot: how many terrain halts the bolt survives.
    public int Penetration;

    // Block-placing kinds (none yet) — reserved so the cost table has its column.
    public TileType Material;

    // Meter units spent at Enter (phase 2 of the fighter plan). 0 = free.
    public float EnergyCost;

    // Action-FSM priorities. Defaulted per kind; a designer may reorder a kit.
    public int ActivePriority, PassivePriority;

    // The constants every pool action shipped with, so a blueprint that constructs
    // an action with `Default(kind)` — or with the parameterless ctor, which does
    // exactly that — behaves precisely as it did before specs existed. The enemy
    // regression tests (Gauntlet / Template / Zeus / Shrike / Bird / Aspid / Warden
    // / Wizard) are the gate on that claim.
    public static ActionSpec Default(ActionKind kind) => kind switch
    {
        ActionKind.Melee => new ActionSpec
        {
            Kind = kind,
            Windup = 0.45f, Active = 0.12f, Recovery = 0.40f,
            MaxRange = 32f, VerticalSlack = 24f,
            Reach = 22f, HalfHeight = 12f,
            Damage = 1.0f, Knockback = new Vector2(250f, -110f),
            ActivePriority = 30, PassivePriority = 25,
        },
        ActionKind.Contact => new ActionSpec
        {
            Kind = kind,
            Windup = 0f, Active = 0.10f, Recovery = 0.85f,
            MaxRange = 34f,
            HalfWidth = 11f, HalfHeight = 11f,
            Damage = 0.4f, Knockback = new Vector2(190f, -140f),
            ActivePriority = 12, PassivePriority = 10,
        },
        ActionKind.Lunge => new ActionSpec
        {
            Kind = kind,
            Windup = 0.35f, Active = 0.25f, Recovery = 0.45f,
            MinRange = 36f, MaxRange = 90f, VerticalSlack = 24f,
            Speed = 260f,
            HalfWidth = 12f, HalfHeight = 12f,
            Damage = 0.9f, Knockback = new Vector2(300f, -140f),
            ActivePriority = 30, PassivePriority = 24,
        },
        ActionKind.Slam => new ActionSpec
        {
            Kind = kind,
            Windup = 0.20f, Active = 0.16f, Recovery = 0.40f,
            MaxRange = 50f,
            FallSpeedMin = 80f,
            Reach = 16f, HalfWidth = 18f, HalfHeight = 16f,
            Damage = 1.6f, Knockback = new Vector2(360f, -110f),
            ActivePriority = 34, PassivePriority = 30,
        },
        ActionKind.Ranged => new ActionSpec
        {
            Kind = kind,
            Windup = 0.60f, Active = 0.08f, Recovery = 0.50f,
            MinRange = 90f, MaxRange = 360f,
            Speed = EnergyBallProjectile.DefaultSpeed,
            Damage = EnergyBallProjectile.DefaultDamage,
            Reach = 14f,
            ActivePriority = 28, PassivePriority = 22,
        },
        ActionKind.RailShot => new ActionSpec
        {
            Kind = kind,
            Windup = 1.35f, Active = 0.06f, Recovery = 1.15f,
            MinRange = 70f, MaxRange = 520f,
            Speed = RailBoltProjectile.DefaultSpeed,
            Damage = RailBoltProjectile.DefaultBodyDamage,
            Reach = 16f,
            Penetration = RailBoltProjectile.DefaultBudget,
            ActivePriority = 34, PassivePriority = 30,
        },
        ActionKind.PounceSlam => new ActionSpec
        {
            Kind = kind,
            Windup = 0f, Active = 0.95f, Recovery = 0.40f,
            FallSpeedMin = 170f, FallSpeedRef = 560f,
            Damage = 0.8f, DamageMax = 2.4f,
            Knockback = new Vector2(200f, 0f), KnockbackMax = 380f,
            HalfWidth = 15f, Reach = 16f,
            ActivePriority = 34, PassivePriority = 30,
        },
        ActionKind.Lash => new ActionSpec
        {
            Kind = kind,
            Windup = 0.55f, Active = 0.14f, Recovery = 0.45f,
            MinRange = 18f, MaxRange = 62f,
            Reach = 58f, HalfWidth = 7f,
            Damage = 1.3f, Knockback = new Vector2(260f, -150f),
            ActivePriority = 32, PassivePriority = 27,
        },
        _ => new ActionSpec { Kind = ActionKind.Special },
    };
}
