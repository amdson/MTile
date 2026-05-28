using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// MVP enemy implementation: chase + a single melee swing. Exists so the
// EnemyEntity framework has a working concrete example without locking the
// framework to one set of tuning knobs. See
// Plans/ENEMY_CAPABILITY_FRAMEWORK.md.
public sealed class BruteEnemy : EnemyEntity
{
    private const float Radius = 12f;

    public override EntityKind Kind => EntityKind.Brute;

    public BruteEnemy(Vector2 pos)
        : base(new PhysicsBody(Polygon.CreateRegular(Radius, 6), pos),
               health: 3f,
               movement: BuildMovement(),
               actions:  BuildActions())
    {
        // Light enough that player slashes (knockback 200–450) visibly fling
        // the brute (170–375 px/s velocity gain) instead of barely budging it.
        // FrictionScale stays low so a knockback actually slides for a while
        // before friction eats it — same trick StalkerEnemy uses.
        Mass               = 1.2f;
        GravityScale       = 1f;
        Body.FrictionScale = 0.12f;
        Color              = new Color(150, 30, 30);
        Sprite             = Sprites.Brute(Radius);
    }

    // Index order == snapshot identity. EnemyIdleState is required at index 0
    // (EnemyEntity treats it as the fallback when CheckConditions drops). New
    // states are appended, not inserted, so existing indices stay stable.
    private static List<EnemyMovementState> BuildMovement() => new()
    {
        new EnemyIdleState(),         // 0 — fallback
        new EnemyChaseState(),        // 1
        new EnemyAttackHoldState(),   // 2
        new EnemyJumpState(),         // 3
        new EnemyStaggerState(),      // 4 — force-entered via EnemyEntity.OnHit
    };

    // Action FSM picks the highest passive priority whose precondition passes.
    // Selection map (most → least specific gating):
    //   airborne brute, player below       → Slam      (passive 30)
    //   player above + close (<70, Y<-4)    → Shockwave (passive 26) — AoE swat
    //   point-blank (Dist <22)              → Spin      (passive 27)
    //   close (22 ≤ Dist < 32)              → Melee     (passive 25)
    //   mid  (36 ≤ Dist ≤  90)              → Lunge     (passive 24)
    //   far  (90 ≤ Dist ≤ 360)              → Ranged    (passive 22)
    //
    // Index order is the snapshot identity — new actions are APPENDED so saves
    // and replays keep working against old data.
    private static List<EnemyActionState> BuildActions() => new()
    {
        new EnemyMeleeAction(),       // 0
        // new EnemyLungeAction(),       // 1
        // new EnemyRangedAction(),      // 2
        // new EnemyShockwaveAction(),   // 3 — appended
        new EnemySlamAction(),        // 4 — appended
        // new EnemySpinAction(),        // 5 — appended
    };
}
