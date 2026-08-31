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
//   climb band        29     Parkour/Mantle/ArcJump's feasibility-triggered bid
//   jump passives     30–48  the launch family's bids (low — trigger-driven)
//   holds             42–44  LedgeGrab / LedgePull / LedgeJump's bid
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

    // Terrain-carried: the body is being displaced by growing terrain with a real
    // horizontal component (mass sweeping the player, not just a floor rising —
    // pure vertical lift stays Standing's). Environmental like Dropdown, one notch
    // above it: preempts the whole free/ground band, but loses to stun (25 — hits
    // win), the climb assists (29 — a vault over the crest still fires), and every
    // deliberate jump (30–48 — jumping out of the wave is the payoff move).
    // Active == Passive: as easy to leave as to enter; it holds only while the
    // push itself persists (CheckConditions).
    public const int TerrainCarriedActive  = 22;
    public const int TerrainCarriedPassive = 22;

    // Stun: heavy-hit lock-out. Preempts the free/ground band so the muted air-control
    // profile applies on a stun-flagged hit, but its Passive (25) sits BELOW the active
    // jumps (50+) — a player hit mid-jump finishes the arc, entering Stunned only once
    // Falling takes over. See StunnedState.
    public const int StunnedActive  = 25;
    public const int StunnedPassive = 25;

    // Tumble: airborne heavy-hit launch (COMBAT_FEEL_PLAN Phase 4). Active 51 keeps
    // it in the launch band — once launched, Falling/WallSlide/ledge-grabs can't
    // steal the body (and those grabs are capability-blocked during the disadvantage
    // window anyway). Passive 26 sits just above StunnedState (25) so a grounded
    // stun that gets knocked airborne flips into Tumble, while staying below the
    // active jumps (50+) so a player hit mid-jump finishes the arc before tumbling.
    public const int TumbleActive  = 51;
    public const int TumblePassive = 26;

    // The climb family (Parkour/ArcJump/Mantle) — AUTOMATIC maneuvers,
    // triggered by feasibility, never by a button. Their Passive (29) sits below
    // every deliberate launch's Passive (Jump 30, RunningJump 35, DoubleJump 40,
    // WallJump 45, CoveredJump 48) so a player's own input ALWAYS wins the
    // same-frame race at a lip: press jump at a step and you jump, the assist
    // yields. (The old 46/46 values claimed "< 50 so jumps preempt" — but
    // preemption compares the candidate's PASSIVE to the current ACTIVE, and
    // the jump family's passives are 30–48: climbs were in fact unbeatable.)
    // Passive 29 still outbids every free state (Standing 10, Crouched 15,
    // WallSlide/Dropdown 20) and the stun band's passives (25/26). Active 29
    // too: a deliberate launch overrides a COMMITTED vault as well — mid-arc
    // the contact context picks the jump (WallJump beside the step face,
    // DoubleJump once airborne — vault entry re-arms it, CoveredJump under a
    // low ceiling, the ground jumps while the probe still binds). The holds
    // band (42–44) only bids on deliberate Up/Down edges, and the stun band
    // (25/26) still cannot break a committed arc.
    public const int ClimbActive  = 29;
    public const int ClimbPassive = 29;

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
    // Covered jump (partial-overhang exit). Passive sits above the climb band (29) so
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
