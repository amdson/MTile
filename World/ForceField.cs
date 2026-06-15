using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Value-typed, single-frame ATTRACTIVE/REPULSIVE region (COMBAT_FEEL_PLAN Phase 2).
// Mirrors the Hitbox model exactly: no cross-frame identity, the world registry is
// cleared every frame, and live states re-broadcast while active. When the
// publishing action ends, its field simply stops existing — so "holds" need no
// expiry bookkeeping and NOTHING here is snapshotted; after a rollback restore the
// fields regenerate from the (snapshotted) FSM state on the next step, exactly like
// hitboxes do.
//
// Force shape is a saturated velocity-servo toward Focus — the same vocabulary as
// AirControl.SoftClampVelocity / JumpServo, in 2D:
//   targetVel = dir(Focus - pos) · min(TargetSpeed, dist/dt)   (stops AT the focus)
//   force     = clamp((targetVel - vel) / dt, |·| ≤ MaxAccel)
// Force-capped means escape is always a physics question, never a flag: a body
// with enough velocity or counter-force leaves; overlapping fields compose by
// summation.
public readonly struct ForceField
{
    public readonly BoundingBox Region;       // broad-phase AABB; bodies inside are affected
    public readonly Vector2     Focus;        // pull target point (world space)
    public readonly float       TargetSpeed;  // px/s the servo pulls toward the focus
    public readonly float       MaxAccel;     // px/s² clamp on the servo force
    public readonly Faction     Owner;        // self/team filter, same as Hitbox.Owner
    public readonly EntityId    Source;       // publishing entity — excluded from the pull
    public readonly Color       DebugColor;   // for a future debug overlay
    // Grab field (COMBAT_FEEL_PLAN Phase 6). When true, any victim the field holds is
    // flagged grabbed (CombatState.MarkGrabbed via ForceFieldSystem's onGrabHeld
    // callback) so their normal attacks/jump gate off and only struggle attacks fire.
    // Hold fields and throw fields leave this false.
    public readonly bool        IsGrab;
    // Throw field (Phase 6). When true, the victim is stunned on contact (onThrown
    // callback → CombatState.RegisterThrown) so they exit the throw into Tumble —
    // committed, control-muted, can tech, and bounce hard off terrain — rather than
    // keeping full control. Mutually exclusive with IsGrab in practice.
    public readonly bool        IsThrow;

    public ForceField(BoundingBox region, Vector2 focus, float targetSpeed, float maxAccel,
                      Faction owner, EntityId source, Color? debugColor = null,
                      bool isGrab = false, bool isThrow = false)
    {
        Region      = region;
        Focus       = focus;
        TargetSpeed = targetSpeed;
        MaxAccel    = maxAccel;
        Owner       = owner;
        Source      = source;
        DebugColor  = debugColor ?? Color.MediumPurple;
        IsGrab      = isGrab;
        IsThrow     = isThrow;
    }
}

// Frame-scoped registry, lifecycle identical to HitboxWorld:
//   Clear() → publishers (action states) call Publish() → ForceFieldSystem.Apply
//   adds forces before the physics step. Nothing persists across frames.
public class ForceFieldWorld
{
    private readonly System.Collections.Generic.List<ForceField> _fields = new();
    public System.Collections.Generic.IReadOnlyList<ForceField> All => _fields;

    public void Clear() => _fields.Clear();

    public void Publish(in ForceField f) => _fields.Add(f);
}

// Applies the frame's fields to every hurtbox-publishing body. Runs AFTER all
// player/entity updates (so every publisher has broadcast) and BEFORE
// PhysicsWorld.StepSwept (so the pull acts this frame) — unlike hitboxes, which
// resolve post-step. Targets come from the HurtboxWorld so the affected set is
// exactly the hittable set; `resolveBody` maps a hurtbox's EntityId to its live
// PhysicsBody (mirrors CombatSystem's resolve callback).
public static class ForceFieldSystem
{
    // `onGrabHeld` / `onThrown` (optional) are invoked with each victim EntityId a grab
    // (IsGrab) / throw (IsThrow) field affects this frame, so the sim can flag that
    // victim grabbed / stunned. Mirrors how CombatSystem dispatches OnHit — the field
    // system owns the geometry, the caller owns the per-victim state write.
    public static void Apply(ForceFieldWorld fields, HurtboxWorld hurtboxes,
                             Func<EntityId, PhysicsBody> resolveBody, float dt,
                             Action<EntityId> onGrabHeld = null,
                             Action<EntityId> onThrown = null)
    {
        if (fields == null || dt <= 0f) return;
        foreach (var f in fields.All)
        {
            foreach (var hb in hurtboxes.All)
            {
                if (hb.Owner == f.Owner) continue;          // self/team immune
                if (hb.Target == f.Source) continue;        // never pull the publisher
                if (!HitboxWorld.Overlaps(f.Region, hb.Region)) continue;
                var body = resolveBody(hb.Target);
                if (body == null) continue;
                if (f.IsGrab)  onGrabHeld?.Invoke(hb.Target);
                if (f.IsThrow) onThrown?.Invoke(hb.Target);

                var toFocus = f.Focus - body.Position;
                float dist = toFocus.Length();
                if (dist < 0.5f) continue;                  // at the focus — no force
                var dir = toFocus / dist;
                // Cap the approach speed by dist/dt so the servo lands ON the focus
                // instead of oscillating across it.
                float speed = MathF.Min(f.TargetSpeed, dist / dt);
                var needed = (dir * speed - body.Velocity) / dt;
                float mag = needed.Length();
                if (mag > f.MaxAccel) needed *= f.MaxAccel / mag;
                body.AppliedForce += needed;
            }
        }
    }
}
