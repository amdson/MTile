using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Stationary ranged enemy. A 3-state cycle:
//   Idle      — tracks the player visually, waits a beat between shots
//   Charging  — visibly builds up to a shot; can be interrupted by a hit (Stagger).
//   Cooldown  — short pause after firing before the next charge
// On charge complete, spawns a BulletProjectile aimed at the player's CURRENT
// position (turret doesn't lead, deliberately — a moving target can dodge by
// strafing perpendicular to the muzzle line).
public class TurretEnemy : Entity
{
    private const float Radius          = 10f;
    private const float ChargeDuration  = 1.2f;
    private const float CooldownTime    = 0.45f;
    private const float StaggerTime     = 0.30f;
    private const float BulletSpeed     = 380f;
    private const float MuzzleOffset    = Radius + 4f;     // spawn distance from turret center along aim
    private const float TrackDeadzone   = 6f;

    private enum AIState { Idle, Charging, Cooldown, Stagger }
    private AIState _state = AIState.Idle;
    private float   _stateTime;
    // Aim direction — refreshed every frame except during Charging, where it's
    // locked at the charge start so the player can read the line of fire and dodge.
    private Vector2 _aim = new(1f, 0f);

    public TurretEnemy(Vector2 pos)
        : base(new PhysicsBody(Polygon.CreateRegular(Radius, 8), pos), health: 2.0f)
    {
        // Heavier than the stalker (so it doesn't slide as far) but light enough
        // for slashes to visibly knock it off its perch. Low FrictionScale so it
        // skids along the ground if the player chucks it down.
        Mass         = 3.0f;
        GravityScale = 1f;
        Body.FrictionScale = 0.15f;
        Color        = Color.MediumPurple;
        Faction      = Faction.Enemy;
        Sprite       = Sprites.Turret(Radius);
    }

    public override void OnHit(in Hitbox hit, in Hurtbox myHurtbox)
    {
        base.OnHit(hit, myHurtbox);
        if (IsDead) return;
        // Interrupted mid-charge — drop the build-up and stagger briefly so the
        // player gets a clear feedback that they cancelled the shot.
        Transition(AIState.Stagger);
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        _stateTime += dt;

        // Track the player except while locked into a charge. Skip when too close
        // to avoid jitter as toPlayer goes through zero.
        if (_state != AIState.Charging)
        {
            var toPlayer = player.Body.Position - Body.Position;
            if (toPlayer.LengthSquared() > TrackDeadzone * TrackDeadzone)
                _aim = Vector2.Normalize(toPlayer);
        }

        switch (_state)
        {
            case AIState.Idle:
                Transition(AIState.Charging);
                break;

            case AIState.Charging:
                if (_stateTime >= ChargeDuration)
                {
                    // Fire. Spawn a bullet at the muzzle, headed along _aim at BulletSpeed.
                    var muzzle = Body.Position + _aim * MuzzleOffset;
                    spawner?.SpawnEntity(new BulletProjectile(muzzle, _aim * BulletSpeed));
                    Transition(AIState.Cooldown);
                }
                break;

            case AIState.Cooldown:
                if (_stateTime >= CooldownTime) Transition(AIState.Idle);
                break;

            case AIState.Stagger:
                if (_stateTime >= StaggerTime) Transition(AIState.Idle);
                break;
        }
    }

    // Read-only state accessors for the sprite — lets the visual emote charge
    // progress without the sprite poking at private fields.
    public float ChargeFraction => _state == AIState.Charging
        ? MathHelper.Clamp(_stateTime / ChargeDuration, 0f, 1f)
        : 0f;
    public Vector2 Aim => _aim;
    public bool IsCharging => _state == AIState.Charging;

    private void Transition(AIState s) { _state = s; _stateTime = 0f; }

    public override void SyncSprite()
    {
        if (Sprite == null) return;
        Sprite.Position = Body.Position;
        // Barrel points along the aim direction. Atan2 here matches how SlashAction
        // / StabAction rotate their hitbox polygons — +X is the sprite's "forward."
        Sprite.Rotation = MathF.Atan2(_aim.Y, _aim.X);
    }
}
