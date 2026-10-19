using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Plain-data snapshot of a PhysicsBody's mutable per-frame state, plus a deep copy
// of its *durable* (Maintained) constraints. Shared capture/restore unit for both
// entities and players (roadmap goal 4 / Plans/STATE_SNAPSHOT_PLAN.md §B).
//
// What's snapshotted:
//   • Pose + kinematics: Position, Velocity, AppliedForce, LastImpulseMagnitude,
//     LastCrushImpulse, SproutCrushCount, FrictionScale — all value types, trivially
//     copied. The step outputs (impulses, crush count) are read by PlayerCharacter.Update at the
//     START of the next frame, before StepSwept resets them, so they cross the frame
//     boundary a snapshot sits on.
//   • Every constraint, in list order: the Maintained hard contacts the collision
//     solver persists AND the soft state-owned ones (FloatingSurfaceDistance,
//     PointForceContact). The solver walks the list in order, and a state's held
//     contact keeps a pose no re-query reproduces, so both the values and the order
//     must come back exactly. Movement states re-take their soft contacts from the
//     restored list by PhysicsContact.Owner (MovementState.ResetTransient).
//
// What's NOT here: Polygon (immutable shape) and Impact (immutable config) — those
// are shared/rebuilt at the entity/player level, never mutated mid-sim.
//
// Constraints are deep-cloned on BOTH capture and restore so a single snapshot can
// be restored repeatedly (rollback re-restores the same frame) without aliasing the
// live body's list.
public struct BodyState
{
    public Vector2 Position;
    public Vector2 Velocity;
    public Vector2 AppliedForce;
    public float   LastImpulseMagnitude;
    public float   LastCrushImpulse;
    public int     SproutCrushCount;
    public float   FrictionScale;
    public PhysicsContact[] Contacts;

    public static BodyState Capture(PhysicsBody b)
    {
        List<PhysicsContact> kept = null;
        var cons = b.Constraints;
        for (int i = 0; i < cons.Count; i++)
            (kept ??= new List<PhysicsContact>()).Add(Clone(cons[i]));
        return new BodyState
        {
            Position             = b.Position,
            Velocity             = b.Velocity,
            AppliedForce         = b.AppliedForce,
            LastImpulseMagnitude = b.LastImpulseMagnitude,
            LastCrushImpulse     = b.LastCrushImpulse,
            SproutCrushCount     = b.SproutCrushCount,
            FrictionScale        = b.FrictionScale,
            Contacts             = kept?.ToArray() ?? System.Array.Empty<PhysicsContact>(),
        };
    }

    public readonly void RestoreInto(PhysicsBody b)
    {
        b.Position             = Position;
        b.Velocity             = Velocity;
        b.AppliedForce         = AppliedForce;
        b.LastImpulseMagnitude = LastImpulseMagnitude;
        b.LastCrushImpulse     = LastCrushImpulse;
        b.SproutCrushCount     = SproutCrushCount;
        b.FrictionScale        = FrictionScale;

        // Drop everything, re-add fresh clones of every contact in captured order.
        b.Constraints.Clear();
        if (Contacts != null)
            for (int i = 0; i < Contacts.Length; i++)
                b.Constraints.Add(Clone(Contacts[i]));
    }

    // Deep-copy this snapshot, including a fresh clone of every contact.
    // Used as the World store's Cloner for BodyStateComp so capture/restore never
    // alias the contact array (the structs copy by value, but the PhysicsContact[]
    // is a shared ref until re-cloned). Pose/kinematics are value types, copied free.
    public readonly BodyState DeepCopy()
    {
        var copy = this;
        if (Contacts != null && Contacts.Length > 0)
        {
            var contacts = new PhysicsContact[Contacts.Length];
            for (int i = 0; i < Contacts.Length; i++) contacts[i] = Clone(Contacts[i]);
            copy.Contacts = contacts;
        }
        return copy;
    }

    // Deep-copy a single contact, preserving its concrete type, Maintained flag and
    // Owner tag. LastImpulse is a per-step diagnostic (zeroed by StepSwept) — not copied.
    private static PhysicsContact Clone(PhysicsContact c) => c switch
    {
        SurfaceDistance sd => new SurfaceDistance(sd.Position, sd.Normal, sd.MinDistance)
            { SurfaceVelocity = sd.SurfaceVelocity, Friction = sd.Friction, Maintained = sd.Maintained, Owner = sd.Owner },
        FloatingSurfaceDistance f => new FloatingSurfaceDistance(f.Position, f.Normal, f.MinDistance)
            { SurfaceVelocity = f.SurfaceVelocity, Friction = f.Friction, Maintained = f.Maintained, Owner = f.Owner },
        PointForceContact p => new PointForceContact(p.Position) { Maintained = p.Maintained, Owner = p.Owner },
        // Any other type would be a bug.
        _ => throw new System.InvalidOperationException($"BodyState can't clone a {c.GetType().Name}."),
    };
}
