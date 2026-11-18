using Microsoft.Xna.Framework;

namespace MTile;

// What a sensor hitbox (Hitbox.Sensor) touched first along its sweep this frame.
// CombatSystem.Apply delivers it to the hitbox's Source through IContactSensor.
public readonly struct SensorContact
{
    public readonly float    T;        // fraction of Hitbox.Sweep travelled at first touch, [0,1]
    public readonly Vector2  Center;   // the box's center at that moment
    public readonly Vector2  Normal;   // unit, pointing out of what was touched
    public readonly EntityId Target;   // the hurtbox owner; EntityId.None = terrain

    public bool IsTerrain => Target.IsNone;

    public SensorContact(float t, Vector2 center, Vector2 normal, EntityId target)
    {
        T = t; Center = center; Normal = normal; Target = target;
    }
}

// Implemented by an entity that publishes sensor hitboxes. Called from inside
// CombatSystem.Apply, like IHittable.OnHit: the callee may change its own state (body
// position/velocity, fuse flags) but must not publish hitboxes — the hitbox list is
// being iterated. Anything that outlives the frame belongs in snapshotted entity state.
public interface IContactSensor
{
    void OnSensorContact(in Hitbox sensor, in SensorContact contact);
}
