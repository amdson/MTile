namespace MTile;

// Configures a PhysicsBody to damage tiles when it crashes into them with
// enough momentum. Null on a body (the default) means the body bounces off
// tiles without damaging them — that's the player, balloons, etc. Crashers
// (balls) get an instance attached at construction by EntityFactory.
//
// PhysicsWorld reads this at every collision impulse site. Mass lives here
// (not on PhysicsBody, which is currently massless — AppliedForce is treated
// as direct acceleration) so the existing physics tuning stays untouched.
// Damage is split equally among all tiles touching the impact face — see
// PhysicsWorld.TryApplyImpactDamage.
public sealed class ImpactDamage
{
    // Converts the body's normal-velocity change at impact into impulse magnitude.
    // Independent of Entity.Mass (which is for knockback accounting) so this stays
    // a physics-internal concern and PhysicsWorld doesn't reach back into Entity.
    public float Mass = 1f;
    // Below this impulse, no damage is dealt — lets a body settle on a tile under
    // gravity without slowly chipping it away from sub-frame micro-impacts.
    public float ImpulseThreshold = 200f;
    // Damage = (impulse - ImpulseThreshold) * DamagePerUnitImpulse, then split
    // equally among the tiles touching the impact face.
    public float DamagePerUnitImpulse = 0.01f;
}
