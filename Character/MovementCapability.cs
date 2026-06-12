using System;

namespace MTile;

// Cross-cutting gate over movement-state arbitration. A status effect (currently only
// combat hitstun/stun) publishes a blocked-capability mask; the selection loop drops
// any candidate state whose RequiredCapabilities intersect it. This replaces the
// per-state `if (ctx.Combat?.BlocksJump) return false;` checks that the jump family
// each carried (and that CoveredJumpState silently lacked).
//
// The mask gates candidate ENTRY only — never a running state's continuation — so a
// player hit mid-jump still finishes the arc (see PlayerCharacter.Update's selection
// loop, which applies the mask only to candidates, not to CheckConditions).
//
// Start small (Jump only). Future effects (root → a Move capability, silence-like
// lockouts, cutscene control) add flags here and a producer in the blocked-mask
// computation, with no per-state boilerplate.
[Flags]
public enum MovementCapability
{
    None = 0,
    Jump = 1 << 0,
}
