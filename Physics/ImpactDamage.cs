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
    // Roadmap §2 destructive-physics: above this impulse AND at least one tile
    // actually broke this contact, the body is considered to have "broken
    // through." The resolver bleeds the normal velocity (NormalRetainOnBreak)
    // instead of zeroing it, so the body emerges from contact with momentum
    // intact (modulo loss-to-breakage). Per the user's directive ("don't
    // substep, simply don't fully stop player when they break through and
    // pause them at block boundary for a frame") the body still halts at the
    // contact face for THIS frame — only the next frame's sweep continues into
    // the now-empty space. Caps penetration at one block / frame, which is
    // acceptable for the desired feel.
    //
    // Distinct from ImpulseThreshold: that one gates *any* damage (chip).
    // BreakThreshold should sit comfortably above the per-cell-break impulse so
    // a glancing hit that breaks a sand cell doesn't suddenly let a walker
    // smash through. With the player's defaults (Mass=2.5, vn≈100 walking,
    // impulse≈250), ImpulseThreshold=700 means walks/runs deal zero damage
    // already. BreakThreshold defaults far above that so only stab-throws and
    // big falls qualify.
    public float BreakThreshold = 1100f;
    // Fraction of pre-impact normal velocity retained after a successful break-
    // through. 0.6 = lose 40% to breakage energy; revisit during playtest.
    public float NormalRetainOnBreak = 0.6f;
}
