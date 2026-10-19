using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The released half of a player grab-throw — PullPointEntity's hand-off, for a body
// instead of a clod. GrabAction carries its victim with a tracking grab field while the
// button is held; on release it spawns this and ends, so nothing of the throw lives in
// the action (a follow-up press can't be eaten by a lingering throw phase).
//
// Its Body IS the flying point: IgnoreTiles, weightless, no hurtbox, launched at the
// cursor's swipe velocity, flying a straight line. Each frame it publishes a field locked
// to the victim — a THROW field on the first (IsThrow: the victim is stunned into Tumble,
// registered once) and a plain tracking pull after — on the same clod
// tracker with the tighter chase smooth time, so the victim converges onto the point's
// velocity exactly as a released clod does. It ends — the victim flying free — once
// their velocities agree within PlayerGrabCatchSpeed, the moment terrain pushes the
// victim back against the throw (the impact is then left to physics), or at
// PlayerGrabChaseMaxSeconds (the point outran the speed cap ⇒ the victim flies at the
// cap). A still mouse hands off ≈0: the victim is caught almost at once and just drops.
public sealed class GrabThrowEntity : Entity
{
    // The field's reach around the point. The victim trails the point by the tracker's
    // lag; this only has to contain it, the single-target lock does the rest.
    private const float FieldHalfSize = 200f;

    public override EntityKind Kind => EntityKind.GrabThrow;

    public EntityId Victim;
    public float    ChaseTime;

    public GrabThrowEntity(Vector2 pos, Vector2 velocity, EntityId victim, Faction owner)
        : base(new PhysicsBody(Polygon.CreateRegular(2f, 4), pos), health: 1f)
    {
        Faction          = owner;
        Victim           = victim;
        Body.IgnoreTiles = true;
        Body.Velocity    = velocity;
        GravityScale     = 0f;
        Mass             = 0f;
        Color            = Color.Transparent;
        Sprite           = new Sprite { Visible = false };
    }

    // Not a target.
    public override void PublishHurtboxes(HurtboxWorld world) { }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        var cfg    = MovementConfig.Current;
        var victim = spawner?.ResolveBody(Victim);
        var fields = spawner?.ForceFields;
        ChaseTime += dt;
        if (victim == null || fields == null || ChaseTime > cfg.PlayerGrabChaseMaxSeconds) { Health = 0f; return; }
        // Caught up: let go. Not on the first frame, so even a throw that already moves
        // at the point's speed publishes one throw field (the Tumble stun is the throw).
        if (ChaseTime > dt && (victim.Velocity - Body.Velocity).Length() < cfg.PlayerGrabCatchSpeed)
        {
            Health = 0f;
            return;
        }
        // Hit terrain: let go, so the impact plays out as physics — bounce, stop or break
        // through, exactly as a launched body would. The point flies through tiles, so a
        // chase past the wall would cancel the rebound next frame and then pin the
        // victim there until PlayerGrabChaseMaxSeconds, releasing them at rest.
        if (ChaseTime > dt && TerrainPushback(victim, Body.Velocity) > cfg.PlayerGrabCatchSpeed)
        {
            Health = 0f;
            return;
        }

        // IsThrow on the release frame only: it registers the throw (the Tumble stun,
        // and with it the hit stamp the hit sound keys on), which must happen once per
        // throw. The chase after it is a plain tracking pull.
        var p = Body.Position;
        fields.Publish(new ForceField(
            new BoundingBox(p.X - FieldHalfSize, p.Y - FieldHalfSize, p.X + FieldHalfSize, p.Y + FieldHalfSize),
            p, 0f, cfg.PlayerGrabMaxAccel, Faction, Id, Color.HotPink,
            isThrow: ChaseTime <= dt, only: Victim,
            trackSmoothTime: cfg.PlayerGrabChaseSmoothTime, trackMaxSpeed: cfg.PlayerGrabMaxSpeed));
    }

    // How hard terrain pushed the victim back against the throw in the last physics
    // step, px/s: the normal impulse of each contact, weighted by how squarely its
    // surface faces the throw. A floor under a sideways throw faces across it (and its
    // friction is tangential), so it counts for nothing; a wall ahead, or the floor
    // under a downward throw, counts in full.
    private static float TerrainPushback(PhysicsBody victim, Vector2 throwVel)
    {
        if (throwVel.LengthSquared() < 1e-4f) return 0f;
        var dir = Vector2.Normalize(throwVel);
        float push = 0f;
        foreach (var c in victim.Constraints)
        {
            if (c is not SurfaceContact sc) continue;
            float facing = -Vector2.Dot(sc.Normal, dir);
            if (facing <= 0f) continue;
            push += MathF.Max(0f, Vector2.Dot(sc.LastImpulse, sc.Normal)) * facing;
        }
        return push;
    }

    // ── Snapshot ────────────────────────────────────────────────────────────────────
    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.LinkedId    = Victim;
        s.HandoffTime = ChaseTime;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        Victim    = s.LinkedId;
        ChaseTime = s.HandoffTime;
    }
}
