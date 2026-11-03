using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public class PhysicsBody
{
    public Vector2 Position;
    public Vector2 Velocity;
    public Vector2 AppliedForce;
    public Polygon Polygon;
    public List<PhysicsContact> Constraints = new();
    // Null = doesn't damage tiles on impact (the default — player, balloons).
    // Set by EntityFactory for "crasher" entities like Ball. PhysicsWorld checks
    // for null at each impulse site and dispatches damage when present.
    public ImpactDamage Impact;

    // Per-body multiplier on the global ground friction. 1.0 (default) matches
    // the player's tuned feel; enemies set this lower so a slash impulse actually
    // visibly chucks them across the ground instead of being eaten by friction
    // in 1-2 frames. Applied wherever PhysicsWorld assigns or computes friction.
    public float FrictionScale = 1f;

    // Quadratic air resistance, in 1/px: StepSwept applies a deceleration of
    // QuadraticDrag·|v|² opposing v, in every state (airborne or not). Negligible at
    // walking speed, strong at launch speed; terminal velocity under gravity alone is
    // √(g / QuadraticDrag). 0 (default) = none — projectiles and passive props keep
    // their authored flight. Players set it each frame from MovementConfig; enemies at
    // construction (EnemyEntity.DefaultQuadraticDrag).
    public float QuadraticDrag;

    // Opt out of terrain collision entirely: StepSwept integrates position and skips
    // constraints + chunk resolution. Set by MassBall, which deposits terrain as it
    // flies — give it collision and it walls itself in a few frames after release.
    public bool IgnoreTiles;

    // Largest |vnRel| seen during the most-recent PhysicsWorld.StepSwept — i.e.
    // the magnitude of the velocity change the body was forced to absorb at
    // collision resolution. Reset to 0 at the start of each step; max'd across
    // every collision the body experiences. Read by PlayerCharacter.Update to
    // dispatch crush damage when the body's "ouch" exceeds CrushThreshold.
    public float LastImpulseMagnitude;

    // The part of LastImpulseMagnitude that was a HARD stop — what crush damage bills.
    // Equal to it everywhere except a terrain impact that broke material: tile HP the
    // impact permanently destroyed cushioned the stop, so its energy is taken off the
    // body's before the Δv is counted (see PhysicsWorld.ResolveChunkCollisionsSwept).
    // A separate field rather than a rewrite of LastImpulseMagnitude because that one
    // is also a contact detector (LobbedAreaProjectile's strike fuse, landing audio),
    // which must still see a clod that ploughs into sand. Reset, max'd and
    // snapshotted alongside it.
    public float LastCrushImpulse;
    // Scratch for the above: the vector sum of this step's hard-stop Δv so far, whose
    // length LastCrushImpulse tracks. Reset at the top of every StepSwept and only read
    // inside it, so it never crosses a frame boundary and needs no snapshot.
    internal Vector2 CrushDvThisStep;

    // Number of sprout cells destroyed out from under this body during the most-recent
    // StepSwept because the depenetration solver could not resolve the overlap within
    // its iteration budget. Reset to 0 at the start of each step, same as
    // LastImpulseMagnitude; read by the NEXT frame's PlayerCharacter.Update to dispatch
    // crush damage — so, like LastImpulseMagnitude, it crosses the frame boundary and
    // is snapshotted (BodyState). Unsnapshotted, a crush seen only on mispredicted
    // frames survived the rollback and billed a phantom hit on the replay.
    public int SproutCrushCount;

    // Wall pin (todo #2): at the end of the most recent StepSwept this body was
    // sandwiched — a growing AVALANCHE sprout pushing it along a contact normal, and a
    // static surface on the opposite side (a wall, a roof) it is being pushed into.
    // Unlike SproutCrushCount this fires while the solver still resolves the overlap
    // (the body squeezes out along the free axis — the "elevator" up a wall face), which
    // is exactly the case that used to deal nothing. Read next frame by
    // PlayerCharacter.Update, so snapshotted (BodyState) like SproutCrushCount.
    public bool WavePinned;
    // Per-step scratch for the pin test (contact normals resolved this step), cleared
    // at the top of StepSwept — never crosses a frame boundary, so not snapshotted.
    internal readonly System.Collections.Generic.List<Vector2> PinPushNormals   = new(4);
    internal readonly System.Collections.Generic.List<Vector2> PinStaticNormals = new(4);

    // ── Launch bookkeeping (Physics/Launch.cs) ───────────────────────────────────
    // The share of Velocity that a hit or throw put there, and how it decays. Physics
    // never reads it: LaunchSystem decays it before the sweep and reconciles it with
    // what the sweep did afterwards. Snapshotted (BodyState).
    public Launch Launch;

    // Input to the sweep: multiplies Impact.Mass in the terrain-impact energy math, so
    // a body can hit harder than its speed alone would. 1 = normal. Rewritten by
    // LaunchSystem.PreStep before every sweep, so it never needs a snapshot.
    public float ImpactMassScale = 1f;

    // True while the body's owner has it parked this step (hitstop hold, rooting), set
    // by Entity/PlayerCharacter.PreStep. LaunchSystem pauses on frozen bodies: their
    // Velocity is zero and the real one sits in the owner's stash. Rewritten every step
    // before it is read, so it needs no snapshot.
    public bool Frozen;

    // What the most recent StepSwept did at each terrain impact, in order. Physics only
    // records here, it never reads it back; consumers read it after the step. Cleared at
    // the top of every StepSwept and read only within the same Step, so no snapshot.
    public readonly List<ContactEvent> ContactEvents = new();

    // Float-precision AABB of the polygon at the body's current position. Recomputes on access (cheap
    // for a 6-vertex hex). Doubles as a probe-region builder — `body.Bounds.StripAbove(20)` gives the
    // 20px slab right above the body for ceiling-probing, etc. See BoundingBox.
    public BoundingBox Bounds => Polygon.GetBoundingBox(Position);

    public PhysicsBody(Polygon polygon, Vector2 position)
    {
        Polygon = polygon;
        Position = position;
    }
}
