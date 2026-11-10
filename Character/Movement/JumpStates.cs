using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The jump family: standard ground jump, running jump, mid-air double jump, and
// the under-an-overhang covered jump. All are launch-band states (Active 50+)
// anchored to a source surface (or a gripped corner) with classic hand-written
// forces; the ambient corrector's default redirect assist rides along except
// where noted (CoveredJump owns its own contacts → Off).

// Standing body clearance: the floor-to-ceiling gap a full jump needs overhead.
// Below it the head would smack and CoveredJumpState owns the escape. This is a
// BODY dimension in px — deliberately independent of Chunk.TileSize.
internal static class JumpClearance
{
    public const float Headroom = 32f;
}

public class JumpingState : MovementState
{
    public override int ActivePriority => MovementPriorities.JumpActive;
    public override int PassivePriority => MovementPriorities.JumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;
    // The jump channel replaces the leg servo while it lives (BuildFold);
    // Drive and Tuck stay the legs' wherever the Legs probe passes.
    public override MovementResource Uses => MovementResource.Legs | MovementResource.Hands;

    // The jump is one solver channel (Plans/MOVEMENT_RESOURCE_MODEL.md §11),
    // created here and nowhere else:
    //   - created when jump is pressed with the feet planted (the Legs probe)
    //     — or, out of a hang or a climb at the lip, with a gripped corner in
    //     the hands (a hands-created jump, §10 q2);
    //   - after creation not gated on the environment: it is live while the
    //     button is held, up to MaxJumpHoldTime (vars.JumpHoldFrames), over an
    //     edge, beside a step or in open air;
    //   - an upward push capped at JumpPushForce that never takes the rise
    //     (relative to the surface it left) past JumpTopSpeed: a tap is a short
    //     hop, each held frame adds about the same height. Nothing writes
    //     velocity; the solver spends the push along the planned rise.
    // The state lasts while the channel lives and then while the body still
    // rises (the coast), so a rising body is not re-caught by Standing.
    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Buffered jump intent rather than a raw edge: a press up to
        // JumpBufferFrames before landing still fires. Consumed in Enter.
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        // Hitstun/stun lock-out is enforced centrally via RequiredCapabilities.Jump.
        // No deferral under a low ceiling: the planner plans the rise from under the
        // slab — the bevel escape near an edge, an honest bonk deeper in (§7.3, row 3).
        return HasLaunchSource(ctx, abilities);
    }

    // Is there anything a jump could be created from — the feet planted, or a
    // gripped corner? DoubleJumpingState bids only when there is not, so an air
    // jump can never outbid (or be spent in place of) a jump the body is entitled to.
    internal static bool HasLaunchSource(EnvironmentContext ctx, PlayerAbilityState abilities)
        => HasGroundSource(ctx) || TryCornerLaunch(ctx, abilities, out _);

    // Feet planted: the Legs probe. WallJumpingState yields to it.
    internal static bool HasGroundSource(EnvironmentContext ctx) => ctx.Legs;

    // A corner the body's hands hold — the hang, or a climb's lip — within arm's
    // reach counts as a push-off point. Both publish it as sim state in
    // abilities.GrabbedCorner (snapshotted); nothing here reads animation data.
    // Arm's reach: the grip channels' own (CorrectorChannels.GripReach).
    private const float CornerLaunchReach = CorrectorChannels.GripReach;
    internal static bool TryCornerLaunch(EnvironmentContext ctx, PlayerAbilityState abilities,
                                         out Vector2 corner)
    {
        corner = abilities.GrabbedCorner;
        if (ctx.PreviousState(0) is not (LedgeGrabState or ClimbManeuverBase)) return false;
        return Vector2.DistanceSquared(corner, ctx.Body.Position)
               <= CornerLaunchReach * CornerLaunchReach;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => vars.JumpHoldFrames > 0 || ctx.Body.Velocity.Y < vars.JumpSourceVy;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);
        // Clear any soft contact a previous state held (CoveredJump's, WallSlide's).
        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
        // The channel is created: its top speed is relative to the surface it
        // left — the floor's own velocity (a rising sprout, a carrier), or a
        // static corner's zero.
        float sourceVy = ctx.Legs ? ctx.Floor.SurfaceVelocity.Y : 0f;
        // Terrain-ride handoff: a jump preempting TerrainCarriedState launches in
        // the CARRIER's frame (its flow velocity, deposited on exit this frame).
        if (vars.JumpCarryFrame > 0 && ctx.CurrentFrame - vars.JumpCarryFrame <= 3)
        {
            sourceVy = MathF.Min(sourceVy, vars.JumpCarrySource.Y);
            vars.JumpCarryFrame = 0;
        }
        vars.JumpSourceVy = sourceVy;
        vars.JumpHoldFrames = vars.JumpReleased ? 1   // a buffered tap still kicks once
            : Math.Max(1, SimFrames.FromSeconds(MovementConfig.Current.MaxJumpHoldTime, ctx.Dt));
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => vars.JumpHoldFrames = 0;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState += ctx.Dt;
        // Released: the channel ends now (the press frame itself always kicks).
        if (!ctx.Input.Space && vars.TimeInState > ctx.Dt) { vars.JumpReleased = true; vars.JumpHoldFrames = 0; }
        var mo = ctx.Modifiers; var cf = MovementConfig.Current;
        ctx.Body.AppliedForce = new Vector2(AirControl.Apply(ctx,
            cf.AirAccel * mo.AirAccel, cf.MaxAirSpeed * mo.MaxAirSpeed, cf.AirDrag * mo.AirDrag), 0f);
        // While the channel lives the plan rises (FoldProfile.Jump); after it,
        // the coast is free flight under the fall profile.
        if (vars.JumpHoldFrames > 0)
            ApplyAmbient(ctx, ref vars, FoldProfile.Jump);
        else
            ApplyAmbient(ctx, ref vars, FoldProfile.Fall, MovementResource.Air | MovementResource.Hands);
        if (vars.JumpHoldFrames > 0) vars.JumpHoldFrames--;
    }
}

// The running jump: the same jump channel, entered with speed — horizontal
// speed of at least RunJumpEntrySpeed in the direction being pressed (§0). It
// differs from JumpingState only in that entry (and its animation identity).
public class RunningJumpState : JumpingState
{
    public override int ActivePriority => MovementPriorities.RunningJumpActive;
    public override int PassivePriority => MovementPriorities.RunningJumpPassive;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        if (!ctx.Legs) return false;
        // A running start is a run along a surface. Climbing a flight, speed along the
        // stairs straddles the threshold riser by riser, so it would pick between two
        // launches at random — stairs always take JumpingState.
        if (ctx.PreviousState(0) is StairClimbState) return false;
        int dir = ctx.Intent.CurrentHorizontal;
        return dir != 0
            && dir * (ctx.Body.Velocity.X - ctx.Floor.SurfaceVelocity.X) >= MovementConfig.Current.RunJumpEntrySpeed;
    }
}

public class DoubleJumpingState : MovementState
{
    public override int ActivePriority => MovementPriorities.DoubleJumpActive;
    public override int PassivePriority => MovementPriorities.DoubleJumpPassive;
    public override MovementResource Uses => MovementResource.Legs | MovementResource.Hands;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;
    public override AnimTag AnimationTag => AnimTag.DoubleJump;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // No wall check: when the player IS pressing into a wall, WallJumpingState wins outright
        // (its Passive 45 beats DoubleJump's 40). When they're NOT pressing into a wall — e.g.
        // dropping off a platform while holding the away direction — DoubleJump is the right fire.
        // Only with nothing to launch a ground jump from — see JumpingState.HasLaunchSource.
        // Shift+Space in the air is the dash chord (DashState), never a double jump.
        if (ctx.Input.Shift) return false;
        return ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)
            && !abilities.HasDoubleJumped && !JumpingState.HasLaunchSource(ctx, abilities);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        return !vars.JumpReleased && vars.TimeInState < MovementConfig.Current.DoubleJumpMaxHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);
        abilities.HasDoubleJumped = true;
        if (ctx.Intent.CurrentHorizontal != 0 && ctx.Intent.CurrentHorizontal != abilities.Facing)
            abilities.Facing = ctx.Intent.CurrentHorizontal;

        // Kill existing vertical momentum entirely
        ctx.Body.Velocity.Y = MovementConfig.Current.DoubleJumpVelocity;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        var force = Vector2.Zero;
        force.Y += cfg.DoubleJumpHoldForce;
        if (vars.TimeInState <= ctx.Dt)
            force.Y += cfg.DoubleJumpInitForce;

        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

        ctx.Body.AppliedForce = force;

        // Lattice engine: the launch stays the state's own — an impulse and a
        // hold force, since in free air there is nothing to push against and
        // the engine allows no upward air force — and the tracker shapes the
        // arc around terrain under the Jump profile (hover off, rising along
        // intent). Unlike the ground jump, the legs play no part.
        ApplyAmbient(ctx, ref vars, FoldProfile.Jump);
    }
}

// Jump initiated while partially under an overhang. Phase 1 (SlidingOut): the jump impulse is
// withheld — the body stays grounded (just gravity + tile collision, so at its natural low resting
// height, which is what fits under a low slab) and walks toward the open side (the ambient
// corrector's head-tuck rows are the clearance insurance the old Under ramp provided),
// stamped on the overhang's bottom corner keeping the head from clipping it. The instant nothing's
// overhead (!TryGetCeiling) it flips to phase 2 (Jumping): a verbatim ground jump (JumpVelocity +
// JumpHoldForce) that does NOT consume the double jump. Held-jump only for now (a tapped-jump
// buffered variant is TBD). Replaces the old diagonal "ceiling jump" launch.
public class CoveredJumpState : MovementState
{
    // Scalar per-activation state (OpenDir, SlideSpeed, CoveredPhase, SlideTime,
    // JumpHoldTime, JumpReleased) lives in MovementVars now; only the soft-contact
    // refs stay as transient instance caches (rebuilt by EnsureContacts).
    public override void ResetTransient(PhysicsBody body)
    {
        _groundIsCrouch = false;
        _ground = Owned(body, SoftContactOwner.CoveredGroundStand);
        if (_ground == null && (_ground = Owned(body, SoftContactOwner.CoveredGroundCrouch)) != null)
            _groundIsCrouch = true;
    }
    private FloatingSurfaceDistance _ground;  // held through phase 1: the body keeps its rest
                                              // height so the ceiling probe (anchored on the
                                              // head) doesn't slip off the overhead slab and fire
                                              // phase 2 prematurely. Removed on the phase-2 transition.
    private bool _groundIsCrouch;             // which float height _ground was acquired at: standing
                                              // only fits under tall ceilings — under a sub-standing
                                              // slab (2-high tunnels at R=12) the slide holds CROUCH
                                              // height, else the head jams into the ceiling and the
                                              // launch dies. Update's refresh must query the same kind.

    public override int ActivePriority  => MovementPriorities.CoveredJumpActive;
    public override int PassivePriority => MovementPriorities.CoveredJumpPassive;
    // Like the rest of the jump family, the hitstun/stun lock-out applies — a stunned
    // player under an overhang can't covered-jump out. (Previously missing: the inline
    // BlocksJump gate the other jumps carried was never added here.)
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;

    // Side to exit toward: if the player's pressing a direction, honor it (never flip to the opposite
    // side even if its edge is closer). From a standstill, pick whichever edge is nearer. An edge
    // only "counts" if the body's leading vertex has actually pushed past it — until then the player
    // is still deep enough under the overhang that a slide-then-jump isn't the right move yet.
    // (Derived from CeilingChecker, not ExposedLowerCornerChecker: the latter only sees slabs whose
    // bottom is within ~Radius of the head, which never holds for a grounded body on a tile-aligned
    // floor — see CeilingChecker.TryFindExitEdge.)
    private static bool TryPickOpenDir(EnvironmentContext ctx, out int dir, out Vector2 corner)
    {
        int want = ctx.Intent.CurrentHorizontal;
        var bounds = ctx.Body.Bounds;

        if (want != 0)
        {
            if (CeilingChecker.TryFindExitEdge(ctx.Body, ctx.Chunks, want, out corner)
                && IsStickingOut(bounds, want, corner.X))
            { dir = want; return true; }
            dir = 0; corner = default; return false;
        }
        // Standstill: closer edge wins. If only one side has one (and the body's past it), pick it.
        bool hasR = CeilingChecker.TryFindExitEdge(ctx.Body, ctx.Chunks,  1, out var cR) && IsStickingOut(bounds,  1, cR.X);
        bool hasL = CeilingChecker.TryFindExitEdge(ctx.Body, ctx.Chunks, -1, out var cL) && IsStickingOut(bounds, -1, cL.X);
        if (!hasR && !hasL) { dir = 0; corner = default; return false; }
        if (!hasL) { dir =  1; corner = cR; return true; }
        if (!hasR) { dir = -1; corner = cL; return true; }
        float distR = cR.X - ctx.Body.Position.X;
        float distL = ctx.Body.Position.X - cL.X;
        if (distR <= distL) { dir =  1; corner = cR; return true; }
        dir = -1; corner = cL; return true;
    }

    // Body's leading edge has crossed the ceiling lip — i.e. some part of the polygon is no longer
    // shadowed by the overhang on side `dir`. Until this is true the player's still deep inside the
    // overhang and a slide-and-jump isn't the right maneuver yet.
    private static bool IsStickingOut(BoundingBox bounds, int dir, float edgeX)
        => dir == 1 ? bounds.Right > edgeX : bounds.Left < edgeX;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Input.Space) return false;            // held-jump (tapped-jump variant TBD)
        if (!ctx.TryGetGround(out var ground)) return false;
        if (!ctx.TryGetCeiling(out var ceiling)) return false;   // must actually be under something
        if (!ctx.Input.Left && !ctx.Input.Right) return false;  // must be pressing a direction
        // Only relevant for ceilings inside the standing body headroom. Above it a regular
        // jump fits with margin — JumpingState handles those, and its precondition is the
        // complement of this one.
        if (ground.Position.Y - ceiling.Position.Y > JumpClearance.Headroom) return false;
        return TryPickOpenDir(ctx, out _, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (vars.CoveredPhase == CoveredJumpPhase.SlidingOut)
        {
            if (!ctx.Input.Space) return false;                                          // let go of jump → abort
            if (ctx.Intent.CurrentHorizontal == -vars.OpenDir) return false;             // reversing interrupts cleanly
            return vars.SlideTime < MovementConfig.Current.MaxCoveredSlideTime;          // stuck → bail to Falling
        }
        return !vars.JumpReleased && vars.JumpHoldTime < MovementConfig.Current.MaxJumpHoldTime;  // same as JumpingState
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        TryPickOpenDir(ctx, out vars.OpenDir, out _);
        vars.SlideSpeed = MathF.Max(MathF.Abs(ctx.Body.Velocity.X), MovementConfig.Current.MaxWalkSpeed);
        vars.CoveredPhase = CoveredJumpPhase.SlidingOut;
        vars.SlideTime = 0f;
        EnsureContacts(ctx, ref vars);
        // HasDoubleJumped intentionally left untouched (already false from being grounded) — this jump is "free".
    }

    // Idempotent contact acquisition for the slide-out phase: the ground contact
    // held through phase 1 — see _ground. No-op once phase 2 (Jumping) has dropped
    // it. Rebuilds after a restore drops the soft contacts.
    private void EnsureContacts(EnvironmentContext ctx, ref MovementVars vars)
    {
        if (vars.CoveredPhase != CoveredJumpPhase.SlidingOut) return;
        if (_ground == null)
        {
            _groundIsCrouch = ctx.TryGetGround(out var stand) && ctx.TryGetCeiling(out var ceil)
                && stand.Position.Y - ceil.Position.Y < PlayerCharacter.StandingHeight + 1f;
            if (_groundIsCrouch ? ctx.TryGetCrouchGround(out var ground) : ctx.TryGetGround(out ground))
            {
                _ground = ground;
                Hold(ctx.Body, _ground, _groundIsCrouch ? SoftContactOwner.CoveredGroundCrouch
                                                        : SoftContactOwner.CoveredGroundStand);
            }
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_ground != null) ctx.Body.Constraints.Remove(_ground);
        _ground = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureContacts(ctx, ref vars);
        var cfg = MovementConfig.Current;

        if (vars.CoveredPhase == CoveredJumpPhase.SlidingOut)
        {
            vars.SlideTime += ctx.Dt;

            // Flip to the jump the instant nothing's overhead.
            if (!ctx.TryGetCeiling(out _))
            {
                // vy is set relative to the source surface, never added — see
                // JumpingState.Enter. A moving floor carries in through
                // SurfaceVelocity; an additive write rockets whenever this
                // state is entered mid-rise (e.g. stealing a vault's arc).
                float sourceVy = _ground?.SurfaceVelocity.Y ?? 0f;
                if (_ground != null) { ctx.Body.Constraints.Remove(_ground); _ground = null; }
                ctx.Body.Velocity.Y = sourceVy + cfg.JumpVelocity;
                vars.CoveredPhase = CoveredJumpPhase.Jumping;
                vars.JumpHoldTime = 0f;
                vars.JumpReleased = !ctx.Input.Space;
                ctx.Body.AppliedForce = Vector2.Zero;
                // Off: owns its own Under ramp for the slide-out (EnsureContacts) — no
                // ambient duplicates. Still called so stale cross-frame anchor state clears.
                ApplyAmbient(ctx, ref vars, FoldProfile.None);
                return;
            }

            // Refresh the ground constraint's pose so a sloped/stepped floor doesn't fight the
            // spring — from the SAME query kind the contact was acquired at (see _groundIsCrouch).
            if (_ground != null
                && (_groundIsCrouch ? ctx.TryGetCrouchGround(out var refreshedGround)
                                    : ctx.TryGetGround(out refreshedGround)))
            {
                _ground.Position    = refreshedGround.Position;
                _ground.Normal      = refreshedGround.Normal;
                _ground.MinDistance = refreshedGround.MinDistance;
            }

            // Spring force toward the held float height (standing under tall ceilings, crouch
            // under a sub-standing slab) so the body holds its rest height through the slide.
            // Without this, gravity pulls the head down and the ceiling probe slips off the
            // overhead slab, firing phase 2 mid-tunnel.
            var slideForce = Vector2.Zero;
            if (_ground != null)
            {
                float dist           = Vector2.Dot(ctx.Body.Position - _ground.Position, _ground.Normal);
                float gap            = _ground.MinDistance - dist;
                float velAlongNormal = Vector2.Dot(ctx.Body.Velocity, _ground.Normal);
                if (gap > 0f)
                    slideForce += _ground.Normal * (gap * cfg.SpringK - velAlongNormal * cfg.SpringDamping);
                float velExcess = velAlongNormal - cfg.SpringMaxRiseSpeed;
                if (velExcess > 0f && ctx.Dt > 0f)
                    slideForce -= _ground.Normal * velExcess / ctx.Dt;
            }

            // Walk toward the open side, preserving entry speed (WalkAccel·dt ≈ MaxWalkSpeed ⇒ a
            // from-standstill press leaves the overhang in one frame). The Under ramp's redirect
            // (in StepSwept) handles the head if the body rises into the overhang's bottom edge.
            float along = vars.OpenDir * ctx.Body.Velocity.X;
            slideForce.X += vars.OpenDir * AirControl.SoftClampVelocity(along, vars.SlideSpeed, cfg.WalkAccel, ctx.Dt);
            ctx.Body.AppliedForce = slideForce;
            ApplyAmbient(ctx, ref vars, FoldProfile.None);
            return;
        }

        // Phase.Jumping — verbatim ground jump.
        vars.JumpHoldTime += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        var force = Vector2.Zero;
        force.Y += cfg.JumpHoldForce;
        if (vars.JumpHoldTime <= ctx.Dt)
            force.Y += cfg.JumpInitForce;
        force.X += AirControl.Apply(ctx, cfg.AirAccel, cfg.MaxAirSpeed, cfg.AirDrag);

        ctx.Body.AppliedForce = force;

        ApplyAmbient(ctx, ref vars, FoldProfile.None);
    }
}
