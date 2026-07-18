using System;
using Microsoft.Xna.Framework;

namespace MTile;

// How a hitbox turns into a velocity change on the target (Plans/HIT_MOMENTUM_PLAN.md).
//
//   Impulse   — legacy model: a fixed authored vector, target.Velocity += impulse/mass.
//               Right for AoE/field effects (Pulse, beam) where there's no meaningful
//               "striker" flying at the target.
//   Collision — 1D partially-elastic collision along the authored hit direction
//               between the target and a virtual striker (attacker velocity + swing).
//               Mass ratio shapes the outcome with no special cases: light targets
//               ping off with the closing speed reflected, equal masses trade, heavy
//               targets barely deflect and the attacker takes the recoil instead.
public enum KnockbackMode { Impulse, Collision }

// Output contract shared by both modes so everything downstream (hitstun, recoil
// inbox, escalation) stays mode-blind. If a caller ever needs to branch on the
// mode, the abstraction has leaked — extend this struct instead.
public readonly struct HitResult
{
    // Add to the target body's velocity. Already divided by target mass; zero for
    // immovable (mass <= 0) targets.
    public readonly Vector2 TargetDeltaV;
    // Impulse delivered to the target (px/s · mass-units, same units as the old
    // KnockbackImpulse). OnHit returns this to CombatSystem, which negates and
    // scales by RecoilScale into the attacker's per-HitId recoil inbox. In
    // Impulse mode this is the raw authored KnockbackImpulse (preserving the old
    // recoil behavior exactly); in Collision mode it's the resolved J·n.
    public readonly Vector2 Impulse;
    // Scalar attack strength for hitstun / stun-threshold / parry gates, in the
    // same px/s-equivalent units CombatState's constants were tuned against:
    // |KnockbackImpulse|·scale in Impulse mode, the (scaled) closing speed u in
    // Collision mode. Pre-mass, so strength reads consistently across masses.
    public readonly float Strength;

    public HitResult(Vector2 targetDeltaV, Vector2 impulse, float strength)
    {
        TargetDeltaV = targetDeltaV;
        Impulse      = impulse;
        Strength     = strength;
    }
}

// Pure hit → momentum resolution. Stateless and deterministic — safe to call from
// any OnHit without snapshot implications. `scale` is the game-feel multiplier
// applied on top of the authored numbers (the player's escalation KnockbackScale);
// it scales the impulse in Impulse mode and the closing speed in Collision mode,
// so Strength (and therefore stun thresholds) follows it consistently in both.
public static class HitResolver
{
    public static HitResult Resolve(in Hitbox hit, float targetMass, Vector2 targetVelocity,
                                    float scale = 1f)
    {
        if (hit.Mode == KnockbackMode.Impulse)
        {
            var dv = targetMass > 0f ? hit.KnockbackImpulse * scale / targetMass : Vector2.Zero;
            return new HitResult(dv, hit.KnockbackImpulse, hit.KnockbackImpulse.Length() * scale);
        }

        // --- Collision mode -------------------------------------------------
        // 1D collision along the authored launch direction n. Tangential target
        // velocity is untouched — the hit reads as a crisp strike, not a scoop.
        var n = hit.StrikeDir;

        // Closing speed: how fast the striker approaches the target along n.
        // Negative (target already fleeing faster than the swing) clamps to 0 —
        // a hit never *pulls*; MinLaunch below guarantees a visible connect.
        float u = Vector2.Dot(hit.StrikeVelocity - targetVelocity, n) * scale;
        if (u < 0f) u = 0f;

        float ms = MathF.Max(hit.StrikeMass, 1e-4f);   // guard misconfigured 0-mass strikes
        float e  = hit.Restitution;

        float j;                      // scalar impulse magnitude along n
        Vector2 dvTarget;
        if (targetMass > 0f)
        {
            float mu = ms * targetMass / (ms + targetMass);   // reduced mass
            j        = (1f + e) * mu * u;
            dvTarget = n * (j / targetMass);
        }
        else
        {
            // Immovable target (wall-like): striker bounces off with its full
            // share, target doesn't move. mu → ms in the m_t → ∞ limit.
            j        = (1f + e) * ms * u;
            dvTarget = Vector2.Zero;
        }

        // Launch floor: a landed hit always visibly moves a movable target, even
        // when the closing speed was tiny (target retreating with the swing).
        if (targetMass > 0f && hit.MinLaunch > 0f && dvTarget.Length() < hit.MinLaunch)
            dvTarget = n * hit.MinLaunch;

        return new HitResult(dvTarget, n * j, u);
    }

    // Collision-mode recoil off a TILE surface — the m_t → ∞ limit of the collision,
    // expressed directly in attacker Δv (the recoil inbox is applied as a raw
    // velocity add by the attacker's ApplyActionForces). Tiles are stationary, so
    // the closing speed is just the striker's velocity along n; `restitution` is
    // the surface material's (MaterialStrengths), not the hitbox's entity e.
    //
    // NOT self-limiting across frames: the authored swing speed inside
    // StrikeVelocity re-adds every re-publish, so u stays positive even after the
    // bounce reversed the attacker's body velocity. CombatSystem therefore latches
    // tile recoil ONCE PER ATTACK (EntityId.None sentinel in the dedupe set) and
    // calls this at most once per hitbox per frame, with the bounciest eligible
    // material (a wall face of N cells is one surface, not N collisions). The
    // u ≤ 0 clamp still matters within a frame: a backdash-cancelled swing that
    // brushes a wall shouldn't yank the attacker toward it via the floor.
    public static Vector2 TileRecoil(in Hitbox hit, float restitution)
    {
        var n = hit.StrikeDir;
        float u = Vector2.Dot(hit.StrikeVelocity, n);
        if (u <= 0f) return Vector2.Zero;

        float bounce = (1f + restitution) * u * hit.RecoilScale;
        if (bounce < hit.MinRecoilSpeed) bounce = hit.MinRecoilSpeed;
        return -n * bounce;
    }
}
