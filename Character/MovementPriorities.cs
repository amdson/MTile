namespace MTile;

// Centralized priority table for MovementState transitions.
// A candidate state replaces the current one only when candidate.PassivePriority > current.ActivePriority.
public static class MovementPriorities
{
    // Free / passive
    public const int FallingActive    = 0;
    public const int FallingPassive   = 0;
    public const int StandingActive   = 10;
    public const int StandingPassive  = 10;
    public const int CrouchedActive   = 15;
    public const int CrouchedPassive  = 15;
    public const int WallSlideActive  = 20;
    public const int WallSlidePassive = 20;
    // Dropdown: hold Down on the edge of a platform → slip off. Preempts Standing/Crouched;
    // preempted by jumps (so Space mid-drop still launches), LedgeGrab (grab a ledge on the way down),
    // and Guided states.
    public const int DropdownActive   = 20;
    public const int DropdownPassive  = 20;

    // Guided (path-followed) — preempts free air, preempted by jumps
    public const int GuidedActive   = 25;
    public const int GuidedPassive  = 45;

    // Holds
    public const int LedgeGrabActive  = 42;
    public const int LedgeGrabPassive = 42;
    public const int LedgePullActive  = 43;
    public const int LedgePullPassive = 43;

    // Launches (jump family) — preempt guided states
    public const int JumpActive       = 50;
    public const int JumpPassive      = 30;
    public const int RunningJumpActive  = 55;
    public const int RunningJumpPassive = 35;
    // Covered jump (partial-overhang exit). Passive sits above ParkourState's GuidedPassive (45) so
    // that "hold jump + walk toward an overhang edge" goes to the covered jump rather than the duck,
    // and above RunningJump's 35 so a fast run into a low overhang slides out and then jumps rather
    // than jumping straight into the slab.
    public const int CoveredJumpActive  = 52;
    public const int CoveredJumpPassive = 48;
    public const int WallJumpActive   = 50;
    public const int WallJumpPassive  = 40;
    public const int DoubleJumpActive  = 60;
    public const int DoubleJumpPassive = 40;
}
