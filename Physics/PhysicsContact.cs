using Microsoft.Xna.Framework;

namespace MTile;

public abstract class PhysicsContact
{
    // True for contacts that are *durable simulation state* and must survive a
    // snapshot/restore: the resting SurfaceDistance contacts the collision solver
    // persists across frames (a body at rest doesn't re-collide, so they aren't
    // regenerated). State-owned soft contacts (FloatingSurfaceDistance, SteeringRamp,
    // PointForceContact) leave this false — they're re-derived each frame from
    // {state scalars + abilities + body pose + a world query}, so snapshot skips
    // them and the owning state rebuilds them. See Plans/STATE_SNAPSHOT_PLAN.md.
    public bool Maintained;

    // Per-step transient: total impulse delivered TO the body through this contact
    // during the most recent PhysicsWorld.StepSwept. Accumulates both normal-resolve
    // and tangential-friction components. Zeroed at the top of StepSwept; NOT
    // snapshotted (so it doesn't appear in BodyState). Diagnostics-only — readers
    // (impact-damage gating, jitter detection, steering-ramp tuning) inspect it
    // post-step.
    public Vector2 LastImpulse;
}

public abstract class SurfaceContact(Vector2 position, Vector2 normal, float minDistance) : PhysicsContact
{
    public Vector2 Position = position;
    public Vector2 Normal = normal;
    public float MinDistance = minDistance;
    // Velocity of the surface this contact represents. Zero for static tiles
    // and for state-owned planes derived from tile probes; nonzero when the
    // contact was stamped against a dynamic shape (moving platform, etc.).
    // Used by velocity-resolution logic to zero the *relative* normal velocity
    // (body.Velocity − SurfaceVelocity) rather than the absolute, so a body
    // resting on a moving surface inherits its motion.
    public Vector2 SurfaceVelocity = Vector2.Zero;
    // Coulomb-ish friction coefficient, in acceleration units (force per unit
    // mass). The physics solver caps the relative tangential velocity reduction
    // per step at Friction · dt. Zero (default) means no tangential coupling —
    // walls, ceilings, and state-owned springs leave this alone. Floor-pointing
    // contacts get a default value at collision time so a body inherits a
    // moving platform's horizontal motion and so braking-while-grounded
    // happens without any per-state force.
    public float Friction = 0f;
}

// Hard contact created automatically by collision resolution. Prevents body from penetrating a surface.
public class SurfaceDistance(Vector2 position, Vector2 normal, float minDistance)
    : SurfaceContact(position, normal, minDistance) { }

// Soft contact managed by character movement logic. Prevents body from moving into the surface,
// but the upward push to maintain standing height is applied by the movement state, not the physics step.
public class FloatingSurfaceDistance(Vector2 position, Vector2 normal, float minDistance)
    : SurfaceContact(position, normal, minDistance) { }

public class PointForceContact(Vector2 position) : PhysicsContact
{
    public Vector2 Position = position;
}
