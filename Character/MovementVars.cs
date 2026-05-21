namespace MTile;

// Two-phase progression for CoveredJumpState. Lifted to namespace scope (was a
// private nested enum) so it can live in MovementVars — the snapshot blob.
public enum CoveredJumpPhase { SlidingOut, Jumping }

// Plain-data per-activation state for the movement FSM. One instance lives on each
// PlayerCharacter and is passed by ref into the current movement state's lifecycle
// methods (Enter/Update/Exit/CheckConditions). Only the active state's fields are
// meaningful at any moment; a transition reinitializes the incoming state's slice.
//
// This is the snapshot unit for movement: a flat value type, so capture/restore is
// a struct copy (roadmap goal 4 / Plans/STATE_SNAPSHOT_PLAN.md). Soft constraints
// are NOT here — they're rebuilt by the states (see PhysicsContact.Maintained); only
// re-derivable scalars live in this struct. It's the superset of every movement
// state's mutable fields; fields are reused across states where the meaning matches
// (TimeInState, JumpReleased, SlideSpeed/SlideTime), since states are mutually
// exclusive in time.
public struct MovementVars
{
    public float TimeInState;       // Jumping, RunningJump, WallJumping, DoubleJumping, LedgeGrab, LedgePull
    public bool  JumpReleased;      // Jumping, RunningJump, WallJumping, DoubleJumping, CoveredJump
    public float SlideSpeed;        // CoveredJump, Dropdown
    public float SlideTime;         // CoveredJump, Dropdown
    public int   OpenDir;           // CoveredJump
    public int   DropDir;           // Dropdown
    public float JumpHoldTime;      // CoveredJump (phase 2)
    public float EntrySpeed;        // Parkour
    public bool  ExitingAirborne;   // Dropdown
    public CoveredJumpPhase CoveredPhase;  // CoveredJump
}
