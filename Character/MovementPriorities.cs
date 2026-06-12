namespace MTile;

// Single source of truth for movement-state arbitration priorities.
//
// Selection model (see PlayerCharacter.Update): every frame, each registered state
// whose CheckPreConditions passes is a candidate. The highest-PassivePriority
// candidate replaces the current state iff its Passive STRICTLY exceeds the current
// state's Active:
//
//     candidate replaces current  ⟺  candidate.PassivePriority > current.ActivePriority
//
//   - Passive = the strength of a state's bid to take over (how assertively it
//     preempts whatever's running).
//   - Active  = a state's resistance to being taken over once it IS running.
//
// A high Active with a low Passive (the jump family) means "hard to interrupt while
// active, but doesn't aggressively grab control" — it only fires from a deliberate
// trigger in its precondition. Equal Active==Passive (the free/ground states) means
// "as easy to leave as to enter". Ties between equal-Passive candidates break by
// registration order in PlayerCharacter's _stateRegistry (first-found wins).
//
// Bands (Passive unless noted):
//   free / ground     0–20   Falling, Standing, Crouched, WallSlide, Dropdown
//   stun              25     StunnedState (preempts free air, NOT active jumps)
//   jump passives     30–48  the launch family's bids (low — trigger-driven)
//   holds             42–44  LedgeGrab / LedgePull / LedgeJump's bid
//   guided passive    45     ParkourState's auto-trigger bid
//   launch actives    50–60  jump family's resistance-while-active
public static class MovementPriorities
{
    // Free / passive (Active == Passive: trivially enter and leave).
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

    // Stun: heavy-hit lock-out. Preempts the free/ground band so the muted air-control
    // profile applies on a stun-flagged hit, but its Passive (25) sits BELOW the active
    // jumps (50+) — a player hit mid-jump finishes the arc, entering Stunned only once
    // Falling takes over. See StunnedState.
    public const int StunnedActive  = 25;
    public const int StunnedPassive = 25;

    // Guided (path-followed) — preempts free air, preempted by jumps.
    public const int GuidedActive   = 25;
    public const int GuidedPassive  = 45;

    // Holds.
    public const int LedgeGrabActive  = 42;
    public const int LedgeGrabPassive = 42;
    public const int LedgePullActive  = 43;
    public const int LedgePullPassive = 43;
    // LedgeJump: launches off the top of a ledge pull. Passive 44 preempts the pull
    // (Active 43) the moment its height gate opens, but stays below WallJump's Passive
    // (45) so a same-frame Space+away still wins the bail-out. Active 55 is in the
    // launch band — Falling/Stunned/DoubleJump can't steal it mid-launch.
    public const int LedgeJumpActive  = 55;
    public const int LedgeJumpPassive = 44;

    // Launches (jump family) — high Active (hard to interrupt while airborne), low
    // Passive (only fire from a deliberate trigger in their preconditions).
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
    // WallJump: Passive 45 strictly above DoubleJump's 40 — when both could fire (near a
    // wall, jump tapped, double-jump still available), WallJump wins outright; DoubleJump
    // fires only when no wall is detected.
    public const int WallJumpActive   = 50;
    public const int WallJumpPassive  = 45;
    public const int DoubleJumpActive  = 60;
    public const int DoubleJumpPassive = 40;
}
