namespace MTile;

// Multiplicative scalars on the six movement knobs that the action FSM can
// influence each frame. Reset to Identity by PlayerCharacter.Update; populated
// via ActionState.ApplyMovementModifiers; consumed by the movement states.
//
// Stacking is multiplicative (set m.MaxWalkSpeed *= 0.5f to halve top speed).
// The movement-side consumer multiplies into MovementConfig.Current.X at the
// read site — these are pure scalars, never absolute values.
public struct MovementModifiers
{
    public float WalkAccel;
    public float MaxWalkSpeed;
    public float GroundFriction;
    public float AirAccel;
    public float MaxAirSpeed;
    public float AirDrag;
    // Scalar on the global gravity vector applied to the player. 1 = unchanged,
    // 0 = weightless float, 0.3 = light-and-floaty. Same convention as Entity.GravityScale.
    // Applied by PlayerCharacter.Update via a counter-force at the very end of the frame,
    // identical in shape to what Entity.PreStep does for entities.
    public float GravityScale;
    // When true, the walk/air speed caps stop BRAKING velocity that already exceeds
    // them — input can still add speed up to the cap, but externally-applied velocity
    // (knockback, throws) is left to drag/friction instead of being clipped back to
    // the cap in one frame. Set during hitstun (COMBAT_FEEL_PLAN Phase 1) so a hit
    // actually displaces; consumed by AirControl.Apply and the ground states'
    // overspeed correction.
    public bool PreserveExternalVelocity;

    public static MovementModifiers Identity => new()
    {
        WalkAccel      = 1f,
        MaxWalkSpeed   = 1f,
        GroundFriction = 1f,
        AirAccel       = 1f,
        MaxAirSpeed    = 1f,
        AirDrag        = 1f,
        GravityScale   = 1f,
        PreserveExternalVelocity = false,
    };
}
