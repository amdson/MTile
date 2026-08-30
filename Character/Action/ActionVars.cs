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
// (BlockEruptionAction used to keep a SmoothPen + List<PathSample> here for exactly this
// reason; both are gone — its gesture is now the two Vector2s below.)
//
// Fields are reused across states where the meaning matches (TimeInState, HitId,
// IsGrounded, ChargeTime), since actions are mutually exclusive in time.
public struct ActionVars
{
    public float   TimeInState;       // Recovery, Slash*, Stab, Pulse, EnergyBall, Grenade, BlockPaint
    public bool    Charging;          // Recovery — LMB wind-up hold live (the merged ReadyAction role)
    public float   ChargeTime;        // Beam, LobbedArea
    public bool    IsGrounded;        // Ready, Stab, Pulse
    public int     Facing;            // Ready
    public int     HitId;             // Slash*, Stab, Pulse, Beam
    public bool    AttackConnected;   // Slash* — latched true once the HitId connects with an
                                      // entity (read from CombatSystem.PeekHits), so combo
                                      // openers can gate their follow-up on a hit (Phase 3).

    public Vector2 AttackDir;          // Slash* (incl. GuardRetaliate)

    public Vector2 StabDir;           // Stab / AirSpinStab
    public float   InitialStabAngle;  // Stab
    public float   Boost;             // Stab (air-dive multiplier on damage + block box size)
    public float   TipExt;            // Stab — live tip extension; drives the per-frame
                                      // hitbox lengths (sim) AND the glow tip (render)

    public Vector2 OriginCell;        // BlockPlace (last placed cell), BlockBurst (target cell)
    public Vector2 CursorPosition;     // BlockReady

    public float   FiringTime;        // Beam, Laser
    public bool    Firing;            // Beam, Laser
    public Vector2 BeamDir;           // Beam, Laser — aim direction. Beam locks it the frame firing starts;
                                      // Laser tracks the cursor through its scan and locks at the shot
                                      // (sim state either way: it drives hitbox placement).

    // LaserAction. The shot is a budget that pays for the blocks it eats, so the two
    // numbers that decide where it stops are per-activation sim state:
    //   Power — TileMaxHP-units left; each destroyed cell costs its material's MaxHP.
    //   Reach — how far down the scan rectangle the burn front has travelled (px from
    //           the muzzle). Frozen the moment Power hits zero — that IS the endpoint.
    // FireFrame stamps the sim frame the beam lit, so the render shell can key a
    // one-shot on it and stay idempotent under rollback (see GameAudio's frame-stamp
    // promotion). 0 = not firing yet.
    public float   LaserPower;
    public float   LaserReach;
    public int     LaserFireFrame;

    public Vector2 CursorAtPress;     // LobbedArea

    // BlockGrabAction. The grab's mechanics — peel group, pull contest, carried orb —
    // live on a PullPointEntity the action spawns on the press and drives while the
    // button is held (Plans/BLOCK_THROW_PLAN.md §4.3). The action keeps only the id
    // (never the object: a rollback restore replaces it) plus a read-only summary
    // mirrored back each frame for the movement modifiers and the tests.
    public EntityId PullPointId;
    public Vector2  SwipeVel;     // EMA of cursor world velocity — the point's hand-off velocity
    public Vector2  PrevCursor;   // last frame's MouseWorldPosition, for the EMA
    public bool     OrbHeld;      // mirror: a ball is in hand (tracking a driven point)
    public int      PeelCount;    // mirror: live group size
    public float    PeelStrain;   // mirror: spring load / snap cap, 0..1
    // (BallPos below doubles as the mirror of the held ball's position, for the animator.)

    // BlockPaintAction — the mass ball. Seeded at the cursor on RMB down, then
    // critically-damped toward it each frame, leaking mass into the cell beneath it.
    // Two plain Vector2s, so the whole painter snapshots as part of the struct copy.
    public Vector2 BallPos;
    public Vector2 BallVel;
    // BlockPaintAction — which half of the plain-RMB gesture this hold is. Latched at the
    // PRESS (cursor in solid = charge, in open air = paint) rather than re-evaluated per
    // frame, because the painter fills the cell under its own cursor: a per-frame
    // "is the cursor in solid" test flips to charging the instant the first tile
    // finalizes, so a stroke could only ever place one block. See BlockPaintAction.Update.
    public bool    ChargeGesture;

    public bool    GrabThrowing;      // GrabAction — false during the hold phase, true once releasing into the throw
    public Vector2 GrabDir;           // GrabAction — hold focus / throw direction
}

// (PeelMember / PeelMemberBuffer lived here while the peel group rode ActionVars; they
// moved to Sim/ECS/Components/PeelGroupComp.cs with the group's move onto the
// PullPointEntity.)
//
// (EruptionGestureState lived here — a deep-copyable capture of BlockEruptionAction's
// SmoothPen + List<PathSample>, the last per-activation action state that couldn't ride
// in the flat struct above. It went away with the one-shot eruption planners: the
// gesture is now just BallPos/BallVel, and the released ball is a world entity, so
// there is no reference-type action state left to special-case.)
