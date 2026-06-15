using Microsoft.Xna.Framework;

namespace MTile;

// Plain-data per-activation state for the action FSM — the action-side analogue of
// MovementVars. One instance lives on each PlayerCharacter and is passed by ref into
// the current action state's lifecycle methods (Enter/Update/Exit/CheckConditions),
// and by `in` into the read-only hooks (ApplyMovementModifiers/ApplyActionForces/Draw).
// Only the active action's fields are meaningful at any moment; a transition
// reinitializes the incoming action's slice in its Enter.
//
// This is the snapshot unit for actions: a flat value type, so capture/restore is a
// struct copy (roadmap goal 4 / Plans/STATE_SNAPSHOT_PLAN.md). Reference-type
// per-activation state is deliberately NOT here:
//   • Render-only caches (Trail _trail/_tipTrail, BeamAction._lastBeamDir/_lastBeamReach,
//     BlockEruptionAction._simResult) — cosmetic, self-heal as play resumes.
//   • BlockEruptionAction._pen (SmoothPen) + _samples (List<PathSample>) — genuine
//     accumulating gesture buffers, neither value-copyable nor cheaply re-derivable.
//     They stay as instance fields and get a deep-copy at snapshot time (goal 6).
// Polygon BlockPoly IS stored here: Polygon is immutable, so the shallow struct-copy
// of its reference is snapshot-safe.
//
// Fields are reused across states where the meaning matches (TimeInState, HitId,
// IsGrounded, ChargeTime), since actions are mutually exclusive in time.
public struct ActionVars
{
    public float   TimeInState;       // Ready, Slash*, Stab, Pulse, EnergyBall, Grenade, BlockEruption
    public float   ChargeTime;        // BlockReady, BlockEruption, Beam, LobbedArea
    public bool    IsGrounded;        // Ready, Stab, Pulse
    public int     Facing;            // Ready
    public int     HitId;             // Slash*, Stab, Pulse, Beam
    public bool    AttackConnected;   // Slash* — latched true once the HitId connects with an
                                      // entity (read from CombatSystem.PeekHits), so combo
                                      // openers can gate their follow-up on a hit (Phase 3).

    public Vector2 SlashDir;          // Slash* (incl. GuardRetaliate)

    public Vector2 StabDir;           // Stab / AirSpinStab
    public float   InitialStabAngle;  // Stab
    public float   Boost;             // Stab
    public float   BlockReach;        // Stab
    public Polygon BlockPoly;         // Stab (immutable ref — see note above)
    public float   TipExt;            // Stab (cached tip extension, read back by Draw)

    public Vector2 OriginCell;        // BlockReady
    public Vector2 CursorPosition;     // BlockReady
    public bool    InSolidLastFrame;  // BlockReady
    public Vector2 Origin;            // BlockEruption

    public float   FiringTime;        // Beam
    public bool    Firing;            // Beam
    public Vector2 BeamDir;           // Beam — aim direction, locked the frame firing starts (sim state: drives hitbox placement)

    public Vector2 CursorAtPress;     // LobbedArea

    public bool    GrabThrowing;      // GrabAction — false during the hold phase, true once releasing into the throw
    public Vector2 GrabDir;           // GrabAction — hold focus / throw direction
}

// Deep-copyable snapshot of BlockEruptionAction's reference-type gesture buffer —
// the one piece of per-activation action state that can't ride in the flat
// ActionVars struct (a mutable SmoothPen + a growing List<PathSample>). PathSample
// is a readonly struct, so the array is a true deep copy. Captured/restored by
// BlockEruptionAction.CaptureGesture/RestoreGesture and carried in PlayerData.
public struct EruptionGestureState
{
    public bool         HasPen;
    public Vector2      PenPosition;
    public Vector2      PenVelocity;
    public PathSample[] Samples;
}
