using System;
using System.Threading;
using Microsoft.Xna.Framework;

namespace MTile;

// First active NPC. A ground-bound chaser that closes in on the player and
// commits to a short forward lunge when in range. The lunge has a visible
// telegraph (the stalker stops moving for ~0.4s before firing) so the player
// can react — slash to interrupt, jump over, or knock a floating ball into it.
//
// AI = 4-state FSM:
//   Chase     — walk toward player horizontally; if close enough vertically too,
//                fire Telegraph.
//   Telegraph — brake to a stop, hold for TelegraphDuration. If the player slips
//                out of vertical range, back to Chase.
//   Lunge     — dash forward at LungeSpeed for LungeDuration, publishing a
//                forward hitbox each frame (single HitId → CombatSystem deduces
//                so the player only takes the hit once per lunge).
//   Recover   — pause briefly so the player has a counter-attack window after
//                dodging the lunge.
// Stagger:    OnHit transitions to Stagger so the AI stops overwriting velocity
//             while the slash knockback plays out — without this, the chase
//             velocity would clobber the impulse the next frame.
public class StalkerEnemy : Entity
{
    private const float ChaseSpeed         = 70f;
    private const float LungeSpeed         = 260f;
    // Engagement cone — must be within LungeRange horizontally AND
    // LungeVerticalSlack vertically to commit. Keeps the stalker from lunging
    // at a player who's just jumped over it.
    private const float LungeRange         = 70f;
    private const float LungeVerticalSlack = 22f;
    private const float TelegraphDuration  = 0.40f;
    private const float LungeDuration      = 0.25f;
    private const float RecoverDuration    = 0.45f;
    // Minimum stagger; actual stagger ends once velocity also drops back near
    // chase speed. A heavy slash that launches the stalker keeps it ragdolled
    // until the body has actually settled, so attacks visibly chuck enemies
    // around instead of getting clamped a few frames later by the AI grab.
    private const float MinStaggerDuration = 0.35f;
    private const float StaggerResumeSpeed = 90f;
    // Lunge hitbox dimensions in body-relative coords. Reach = forward extent
    // from body center; HalfHeight = vertical thickness.
    private const float LungeReach         = 22f;
    private const float LungeHalfHeight    = 10f;
    private const float LungeDamage        = 0.75f;
    // Knockback in (forward, up) — small upward kick so the player gets popped
    // off the ground a bit, breaking their movement state and reading as "hit."
    private static readonly Vector2 LungeKnockback = new(220f, -110f);

    private enum AIState { Chase, Telegraph, Lunge, Recover, Stagger }
    private AIState _state     = AIState.Chase;
    private float   _stateTime;
    private int     _facing    = 1;
    private int     _hitId;

    public override EntityKind Kind => EntityKind.Stalker;

    protected override void WriteState(ref EntitySnapshot s)
    {
        base.WriteState(ref s);
        s.AIState   = (int)_state;
        s.StateTime = _stateTime;
        s.Facing    = _facing;
        s.HitId     = _hitId;
    }

    protected override void ReadState(in EntitySnapshot s)
    {
        base.ReadState(in s);
        _state     = (AIState)s.AIState;
        _stateTime = s.StateTime;
        _facing    = s.Facing;
        _hitId     = s.HitId;
    }

    public StalkerEnemy(Vector2 pos)
        : base(new PhysicsBody(Polygon.CreateRegular(9f, 6), pos), health: 1.5f)
    {
        // Light + low ground-friction so a slash visibly chucks the stalker.
        // Mass 1.0: 200-impulse slash = +200 px/s. FrictionScale 0.12: ground
        // friction reduces ~12 px/s per step instead of 100 → the body slides
        // for the full Stagger window and only resettles after several body
        // lengths of travel.
        Mass         = 1.0f;
        Body.FrictionScale = 0.12f;
        GravityScale = 1f;
        Color        = Color.DarkOrange;
        Faction      = Faction.Enemy;
        Sprite       = Sprites.Stalker(9f);
    }

    public override void OnHit(in Hitbox hit, in Hurtbox myHurtbox)
    {
        // Base applies damage + knockback impulse to velocity. We pile a brief
        // stagger on top so the AI doesn't immediately overwrite Velocity.X
        // and erase the visible knock-back next frame.
        base.OnHit(hit, myHurtbox);
        if (!IsDead) Transition(AIState.Stagger);
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        _stateTime += dt;

        var toPlayer = player.Body.Position - Body.Position;
        if (MathF.Abs(toPlayer.X) > 1f) _facing = MathF.Sign(toPlayer.X) >= 0 ? 1 : -1;

        switch (_state)
        {
            case AIState.Chase:
                Body.Velocity.X = _facing * ChaseSpeed;
                if (MathF.Abs(toPlayer.X) < LungeRange && MathF.Abs(toPlayer.Y) < LungeVerticalSlack)
                {
                    _hitId = spawner.HitIds.Next();
                    Transition(AIState.Telegraph);
                }
                break;

            case AIState.Telegraph:
                // Hard brake — telegraph reads as a "stop" both visually and through
                // friction once the body lands on the ground.
                Body.Velocity.X *= 0.6f;
                if (MathF.Abs(toPlayer.Y) > LungeVerticalSlack * 2f)
                {
                    Transition(AIState.Chase);
                    break;
                }
                if (_stateTime >= TelegraphDuration)
                    Transition(AIState.Lunge);
                break;

            case AIState.Lunge:
                Body.Velocity.X = _facing * LungeSpeed;
                {
                    var center = Body.Position + new Vector2(_facing * LungeReach * 0.6f, 0f);
                    var region = new BoundingBox(
                        center.X - LungeReach * 0.6f, center.Y - LungeHalfHeight,
                        center.X + LungeReach * 0.6f, center.Y + LungeHalfHeight);
                    hitboxes?.Publish(new Hitbox(
                        region, _hitId, LungeDamage,
                        new Vector2(_facing * LungeKnockback.X, LungeKnockback.Y),
                        Faction.Enemy, Id, Color.OrangeRed,
                        targets: HitTargets.EntitiesOnly));
                }
                if (_stateTime >= LungeDuration) Transition(AIState.Recover);
                break;

            case AIState.Recover:
                Body.Velocity.X *= 0.7f;
                if (_stateTime >= RecoverDuration) Transition(AIState.Chase);
                break;

            case AIState.Stagger:
                // Don't touch Velocity — let the slash knockback play out. Resume
                // chasing only once we've held for the minimum window AND the
                // body has slowed back down. A heavy knockback that launches the
                // stalker far keeps it ragdolled until it actually settles.
                if (_stateTime >= MinStaggerDuration &&
                    Body.Velocity.LengthSquared() < StaggerResumeSpeed * StaggerResumeSpeed)
                    Transition(AIState.Chase);
                break;
        }
    }

    private void Transition(AIState s) { _state = s; _stateTime = 0f; }
}
