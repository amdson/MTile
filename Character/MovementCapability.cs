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
// Started small (Jump only); Phase 4 (COMBAT_FEEL_PLAN) added the terrain-grab
// capabilities so the combat disadvantage window can block them — a launched player
// can no longer cancel knockback by clinging to a wall or grabbing a ledge, which is
// what turns existing knockback into juggles and edgeguards. Future effects (root →
// a Move capability, silence-like lockouts, cutscene control) add flags here and a
// producer in the blocked-mask computation, with no per-state boilerplate.
[Flags]
public enum MovementCapability
{
    None = 0,
    Jump      = 1 << 0,
    // Cling to a wall (WallSlidingState). Blocked during hitstun/stun so a hit
    // toward a wall can't be neutralized by sliding down it.
    WallCling = 1 << 1,
    // Grab / pull up on a ledge (LedgeGrabState, LedgePullState). Blocked during
    // hitstun/stun so a launch past a ledge can't be cancelled by catching it.
    LedgeGrab = 1 << 2,
}
