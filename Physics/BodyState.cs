using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Plain-data snapshot of a PhysicsBody's mutable per-frame state, plus a deep copy
// of its *durable* (Maintained) constraints. Shared capture/restore unit for both
// entities and players (roadmap goal 4 / Plans/STATE_SNAPSHOT_PLAN.md §B).
//
// What's snapshotted:
//   • Pose + kinematics: Position, Velocity, AppliedForce, LastImpulseMagnitude,
//     FrictionScale — all value types, trivially copied.
//   • Maintained constraints only (the resting SurfaceDistance hard contacts the
//     collision solver persists). Soft state-owned contacts (FloatingSurfaceDistance,
//     SteeringRamp, PointForceContact) leave Maintained == false; they are NOT
//     captured — the owning movement state rebuilds them idempotently after a
//     restore (see PhysicsContact.Maintained + the per-state Ensure… methods).
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
    public float   FrictionScale;
    public PhysicsContact[] Maintained;

    public static BodyState Capture(PhysicsBody b)
    {
        List<PhysicsContact> kept = null;
        var cons = b.Constraints;
        for (int i = 0; i < cons.Count; i++)
        {
            if (!cons[i].Maintained) continue;
            (kept ??= new List<PhysicsContact>()).Add(Clone(cons[i]));
        }
        return new BodyState
        {
            Position             = b.Position,
            Velocity             = b.Velocity,
            AppliedForce         = b.AppliedForce,
            LastImpulseMagnitude = b.LastImpulseMagnitude,
            FrictionScale        = b.FrictionScale,
            Maintained           = kept?.ToArray() ?? System.Array.Empty<PhysicsContact>(),
        };
    }

    public readonly void RestoreInto(PhysicsBody b)
    {
        b.Position             = Position;
        b.Velocity             = Velocity;
        b.AppliedForce         = AppliedForce;
        b.LastImpulseMagnitude = LastImpulseMagnitude;
        b.FrictionScale        = FrictionScale;

        // Drop everything, re-add fresh clones of the maintained contacts. Soft
        // contacts are intentionally gone — states re-derive them next frame.
        b.Constraints.Clear();
        if (Maintained != null)
            for (int i = 0; i < Maintained.Length; i++)
                b.Constraints.Add(Clone(Maintained[i]));
    }

    // Deep-copy a single contact, preserving its concrete type + Maintained flag.
    private static PhysicsContact Clone(PhysicsContact c) => c switch
    {
        SurfaceDistance sd => new SurfaceDistance(sd.Position, sd.Normal, sd.MinDistance)
            { SurfaceVelocity = sd.SurfaceVelocity, Friction = sd.Friction, Maintained = sd.Maintained },
        FloatingSurfaceDistance f => new FloatingSurfaceDistance(f.Position, f.Normal, f.MinDistance)
            { SurfaceVelocity = f.SurfaceVelocity, Friction = f.Friction, Maintained = f.Maintained },
        PointForceContact p => new PointForceContact(p.Position) { Maintained = p.Maintained },
        // SteeringRamp is always a soft (non-Maintained) contact, so it never reaches
        // capture/clone — no branch needed. Any other type would be a bug.
        _ => null,
    };
}
