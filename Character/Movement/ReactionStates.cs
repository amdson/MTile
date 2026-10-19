using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Combat-reaction movement states: the body under someone else's control.
// Grounded stun (StunnedState) and airborne launch (TumbleState) share the
// muted-DI profile; both run with the ambient corrector Off so knockback plows
// into terrain honestly.

// Held by another player's grab (Combat.GrabbedActive, re-marked every frame the grab
// field holds this body). The body is a carried object: no fold support, no hover, no
// landing catch, no jump servo — the grab field (which cancels gravity) is the only
// thing positioning it, and terrain contact is plain collision. Without this the
// victim stayed in Standing, whose ambient solve read every pull toward the floor as a
// landing to cushion.
//
// The victim keeps a little directional control: a flat push along their held
// direction (PlayerGrabbedDriftAccel), with no speed cap and no braking — the field
// owns their velocity, so input only leans against the grip. Ends when the flag lapses
// (GrabbedGraceFrames after the field stops); a throw's stun then hands the body to
// Tumble/Stunned on their passives.
public class GrabbedState : MovementState
{
    public override int ActivePriority  => MovementPriorities.GrabbedActive;
    public override int PassivePriority => MovementPriorities.GrabbedPassive;

    // Held off the ground and flailing — the tumble clip reads right; a dedicated
    // grabbed clip would need its own tag.
    public override AnimTag AnimationTag => AnimTag.Tumble;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => ctx.Combat?.GrabbedActive == true;

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => ctx.Combat?.GrabbedActive == true;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Nothing the victim's own movement does may brake the carry or the throw.
        ctx.Modifiers.PreserveExternalVelocity = true;

        var dir = new Vector2(
            (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f),
            (ctx.Input.Down  ? 1f : 0f) - (ctx.Input.Up   ? 1f : 0f));
        if (dir.LengthSquared() > 1f) dir = Vector2.Normalize(dir);
        ctx.Body.AppliedForce = dir * MovementConfig.Current.PlayerGrabbedDriftAccel;

        // No reflex assists while held. (Called even though Off: clears cross-frame
        // ambient anchor state.)
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
    }
}

// Heavy-hit lock-out. Preempts Standing/Crouched/WallSliding/Falling so the
// muted air-control profile applies as soon as a stun-flagged hit lands. Does
// NOT preempt active jumps (50+) — a player hit mid-jump finishes the existing
// arc and only enters StunnedState after Falling takes over.
//
// While stunned:
//   - Horizontal accel × 0.4, max-air-speed × 0.7, air-drag × 1.5 — player can
//     nudge but can't redirect the knockback trajectory.
//   - Action FSM gates (Slash*, Stab) refuse to fire (gated on Combat.StunActive).
//   - HitstunActive is also true throughout (every hit sets it), keeping the
//     jump preconditions blocked even past the 8-frame hitstun base window.
//
// State holds no constraints — physics handles ground/wall contact through
// the world's collision resolver. HasDoubleJumped is NOT reset on exit; a
// player stunned out of a double-jump doesn't suddenly regain it.
public class StunnedState : MovementState
{
    public override int ActivePriority  => MovementPriorities.StunnedActive;
    public override int PassivePriority => MovementPriorities.StunnedPassive;

    // Recoil flinch, not the generic ground clips: without this the muted-control window
    // is invisible (a stunned body sliding under knockback reads as a walk cycle).
    public override AnimTag AnimationTag => AnimTag.Stunned;

    // Grounded-only since Phase 4: an airborne heavy hit goes to TumbleState (launch
    // band) instead, so a launched body can't be rescued by terrain. A grounded
    // stun (horizontal hit, body stays on the floor) still lands here. When a
    // grounded stun gets knocked airborne mid-window, this CheckConditions drops
    // (→ Falling) and TumbleState's higher passive grabs the body.
    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => ctx.Combat?.StunActive == true && ctx.TryGetGround(out _);

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => ctx.Combat?.StunActive == true && ctx.TryGetGround(out _);

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        var force = Vector2.Zero;
        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel    * 0.4f,
            cfg.MaxAirSpeed * m.MaxAirSpeed * 0.7f,
            cfg.AirDrag     * m.AirDrag     * 1.5f);

        ctx.Body.AppliedForce = force;

        // No reflex assists while stunned — knockback must plow into corners honestly.
        // (Called even though Off: clears cross-frame ambient anchor state.)
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
    }
}

// Airborne heavy-hit launch (COMBAT_FEEL_PLAN Phase 4). A hit whose impulse crosses
// the stun threshold sets StunActive; while the victim is airborne that becomes a
// Tumble rather than a grounded Stun. Tumble lives in the launch band (Active 51) so
// once launched the body stays tumbling until it lands or techs — combined with the
// capability mask (which blocks WallCling/LedgeGrab during stun/hitstun) this is what
// makes a knockback into a juggle/edgeguard instead of a free wall-cling reset.
//
// Control is muted air-control (DI only), like StunnedState. PreserveExternalVelocity
// is forced on so the muted speed cap never brakes the launch even in the stun tail
// after hitstun lapses.
//
// Tech (defensive option): a buffered Jump intent while a surface is within the tech
// probe (just before landing) ends the launch early, grants brief i-frames, and pops
// the body up — so a read launch can be survived with precise timing. Outside that
// window the body just rides the tumble down and eats the landing.
public class TumbleState : MovementState
{
    // Tech window: ground detected within this slack below the body (but the body
    // isn't yet "grounded" by the normal 20px probe, which would exit Tumble) opens
    // the tech window — roughly the last few frames of the descent.
    private const float TechProbeSlack   = 60f;
    private const float TechInvulnSeconds = 0.25f;
    private const float TechBounceVy     = 260f;   // upward pop on a successful tech
    private const float TechHorizKeep    = 0.3f;   // fraction of horizontal speed kept

    public override int ActivePriority  => MovementPriorities.TumbleActive;
    public override int PassivePriority => MovementPriorities.TumblePassive;

    // Airborne out-of-control tumble, distinct from StunnedState's grounded recoil flinch
    // (AnimTag.Stunned): without this the launch plays the generic Jump/Fall clip and the
    // heavy hit doesn't read.
    public override AnimTag AnimationTag => AnimTag.Tumble;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => ctx.Combat?.StunActive == true && !ctx.TryGetGround(out _);

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => ctx.Combat?.StunActive == true && !ctx.TryGetGround(out _);

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Tech: buffered jump + a surface within the tech probe ⇒ bail the launch.
        if (ctx.Combat != null
            && ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)
            && GroundChecker.TryFind(ctx.Body, ctx.Chunks,
                   PlayerCharacter.Radius, PlayerCharacter.Radius,
                   TechProbeSlack, ctx.Dt, out _))
        {
            ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);
            ctx.Combat.Tech(ctx.CurrentFrame, ctx.Dt, TechInvulnSeconds);
            ctx.Body.Velocity = new Vector2(ctx.Body.Velocity.X * TechHorizKeep, -TechBounceVy);
            ctx.Body.AppliedForce = Vector2.Zero;
            ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
            return;
        }

        // Launch must never be braked by the muted speed cap (the stun tail can
        // outlive hitstun, which is what otherwise forces PreserveExternalVelocity).
        ctx.Modifiers.PreserveExternalVelocity = true;

        var force = Vector2.Zero;
        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel    * 0.4f,
            cfg.MaxAirSpeed * m.MaxAirSpeed * 0.7f,
            cfg.AirDrag     * m.AirDrag     * 1.5f);

        ctx.Body.AppliedForce = force;

        // No reflex assists while launched — same reasoning as StunnedState.
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
    }
}
