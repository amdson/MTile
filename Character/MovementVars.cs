using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Per-channel Δu anchors for the ambient fold solve — the last APPLIED tick-0
// correction of each channel in the stand-fold stack (CorrectionSolver's
// Δ-smoothness chain is anchored here, so corrections stay continuous across
// frames). A flat inline struct rather than an array so MovementVars stays a
// plain value-copy snapshot blob (no reference-typed state to deep-copy).
// Sized to CorrectionSolver.MaxChannels.
public struct ChannelAnchors
{
    public Vector2 C0, C1, C2, C3, C4, C5, C6, C7;

    public Vector2 this[int i]
    {
        readonly get => i switch
        {
            0 => C0, 1 => C1, 2 => C2, 3 => C3, 4 => C4, 5 => C5, 6 => C6, 7 => C7,
            _ => throw new IndexOutOfRangeException(),
        };
        set
        {
            switch (i)
            {
                case 0: C0 = value; break;
                case 1: C1 = value; break;
                case 2: C2 = value; break;
                case 3: C3 = value; break;
                case 4: C4 = value; break;
                case 5: C5 = value; break;
                case 6: C6 = value; break;
                case 7: C7 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }
}

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
    public Vector2 MantleCorner;    // Mantle: the step lip being climbed (from the corridor probe)
    public float MantleTargetY;     // Mantle: body-center Y to deliver into the landing gate
    public float MantleEntryY;      // Mantle: body-center Y at entry (for AnimationProgress)
    public ChannelAnchors ManeuverChannelPrev; // Corrector climb family: per-channel Δu anchors
                                    // of the maneuver stack (rollback-critical, like the
                                    // ambient anchors below)
    public Vector2 AmbientPrevDv;   // AmbientCorrector: the ambient Δu anchor (same role)
    public ChannelAnchors AmbientChannelPrev; // AmbientCorrector fold: per-channel Δu anchors
                                    // (rollback-critical: an unsnapshotted anchor desyncs the
                                    // Δ-smoothness chain on restore)
    public sbyte AmbientElectiveLatch; // Elective-refusal hysteresis (AmbientCorrector):
                                    // >0 = committed to the R1 climb for n frames,
                                    // <0 = refusal window (R0) counting back to 0
    public float FoldDownAnchorY;   // AmbientCorrector fold: the down-side binding limit —
                                    // rises with the true anchor instantly, FALLS toward a
                                    // lower one at gravity (the lip "falling reference";
                                    // rollback-critical like the Δ anchors)
    public float FoldDownAnchorVy;  // its virtual fall speed (px/s)
    public bool  FoldDownAnchorValid;
}
