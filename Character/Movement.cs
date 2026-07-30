using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The movement-state contract. Concrete states are grouped by move type:
//   LocomotionStates.cs — Standing, Crouched, Falling, Dropdown
//   JumpStates.cs       — Jumping, RunningJump, DoubleJumping, CoveredJump
//   WallStates.cs       — WallSliding, WallJumping
//   LedgeStates.cs      — LedgeGrab, LedgePull, LedgeJump
//   ClimbStates.cs      — Parkour (vault), Mantle, ArcJump (ClimbManeuverBase)
//   ReactionStates.cs   — Stunned, Tumble
//
// The corrector solve is SHARED INFRASTRUCTURE, not a class of state — any
// state opts into as much of it as fits its physics:
//   - AmbientPolicy (default: on): the per-frame ambient layer assists around
//     the state's own forces with the redirect disc (AmbientCorrector). Off
//     for states that servo against fixed contacts — an ambient assist would
//     fight the owned maneuver.
//   - FoldProfile: fold states (Standing/Crouched/Falling) go further and
//     delegate support/walk/brake/landing-catch to the ambient solve entirely;
//     the profile shapes the reference (hover height, progress cap).
//   - ManeuverCorrector.Apply: a committed maneuver whose future is PHYSICS
//     (a launch arc, a slide) predicts its guided coast and solves body-force
//     corrections around it, per-tick and as a trigger-by-feasibility probe
//     (the climb family, Dropdown's bespoke fallback).
//   - ReferenceCorrector.DeformedTarget: a GUIDED move whose future is an
//     AUTHORED arc (a retargeted clip) has no coast — the reference path is
//     the swept trajectory, the solve deforms the arc around terrain, and the
//     state's servo tracks the deformed target (LedgePull, Dropdown's clip
//     mode).
// Whatever path a state uses, the applied solve's output is bookkept in
// CorrectorLedger (per-channel forces + per-contact tile attribution).
public abstract class MovementState
{
    public abstract int ActivePriority { get; }
    public abstract int PassivePriority { get; }

    // Capabilities this state needs to be ENTERED. The selection loop skips this state
    // as a candidate while any required capability is in the frame's blocked mask
    // (currently: combat hitstun/stun blocks Jump). Does NOT gate continuation — a
    // running state keeps running via CheckConditions regardless. See MovementCapability.
    public virtual MovementCapability RequiredCapabilities => MovementCapability.None;

    // Lets the currently-active state (and, for one frame after it exits, the
    // just-departed state — the loop queries PreviousState(0)) veto specific candidates
    // that priority alone would let win. Used to keep an owned maneuver (e.g. an
    // in-progress ledge pull) from being stolen by a higher-passive bystander. Default:
    // suppress nothing.
    public virtual bool Suppresses(MovementState candidate, EnvironmentContext ctx) => false;

    // CheckPreConditions (candidate selection) reads only ctx + abilities, never the
    // current activation's vars — so it keeps the lean signature. The lifecycle
    // methods below run on the active/transitioning state and carry MovementVars,
    // the plain-data per-activation state (see MovementVars).
    public abstract bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities);
    public abstract bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars);

    public virtual void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars) {}
    public virtual void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars) {}

    public abstract void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars);

    // Snapshot/restore hook (roadmap goal 4). The only per-player instance data left
    // on a movement state is its transient soft-contact ref cache(s) (_ground,
    // _source, _wall, …). A restore drops the soft contacts from Body.Constraints
    // (only Maintained hard contacts survive — see BodyState), so these caches would
    // be left dangling. PlayerCharacter.RestoreState calls this on every registry
    // state to null them; the owning state's idempotent Ensure… then rebuilds its
    // contact on the next Update from the restored body pose. No-op for stateless
    // states (Falling, Stunned, jumps without a source cache).
    public virtual void ResetTransient() { }

    // What the ambient corrector may do while this state is active. Published per
    // frame, MovementModifiers-style. Default: both assists on. Override to Off in
    // states that servo the body against fixed contacts — an ambient redirect
    // would fight the owned maneuver.
    public virtual AmbientPolicy AmbientPolicy => AmbientPolicy.Default;

    // How this state participates in the stand fold (reference shaping — see
    // FoldProfile). Default: not a fold state; the state owns its own support.
    // Fold states delegate hover/walk/brake/landing-catch to the ambient solve
    // and apply only the gravity-hold baseline themselves.
    public virtual FoldProfile FoldProfile => FoldProfile.None;

    // The animation-facing CATEGORY of this state (AnimTag.None = generic: the animator picks
    // by grounded/velocity). Replaces substring matching on state class names, which silently
    // broke on renames and false-matched future states. Same render-only contract as the
    // virtuals below: the sim never reads it.
    public virtual AnimTag AnimationTag => AnimTag.None;

    // Normalized progress [0,1] of a guided maneuver, exposed to the animation layer for
    // overlays whose clip time is driven by SPATIAL progress rather than a clock — a vault
    // advances by body position vs. the ledge corner, not elapsed time, so its hand overlay
    // can't be timed off ActionTime. Default 0 (states with no natural progress). Render-only:
    // the sim never reads it; it is derived from deterministic body/world data each Update.
    public virtual float AnimationProgress => 0f;

    // A world point a limb should GRIP during a guided maneuver — the ledge corner a vault hand
    // reaches for. The animation layer turns this into a FixedPoint pin (which bone is animation
    // policy, see CharacterAnimator) so the hand lands exactly on the feature instead of just
    // playing an approximate canned reach. Default none. Render-only, same contract as
    // AnimationProgress: derived from deterministic body/world data, the sim never reads it.
    public virtual bool TryAnimationGrip(out Vector2 target) { target = default; return false; }
}
