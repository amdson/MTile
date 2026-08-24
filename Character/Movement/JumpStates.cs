using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The jump family: standard ground jump, running jump, mid-air double jump, and
// the under-an-overhang covered jump. All are launch-band states (Active 50+)
// anchored to a source surface (or a gripped corner) with classic hand-written
// forces; the ambient corrector's default redirect assist rides along except
// where noted (CoveredJump owns its own contacts → Off).

public class JumpingState : MovementState
{
    public override int ActivePriority => MovementPriorities.JumpActive;
    public override int PassivePriority => MovementPriorities.JumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;

    private FloatingSurfaceDistance _source;

    public override void ResetTransient() => _source = null;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Buffered jump intent rather than a raw edge: a press up to
        // JumpBufferFrames before landing still fires. Consumed in Enter.
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        if (!ctx.TryGetGround(out var ground))
        {
            // Corner push-off (movement_todo #4): a gripped corner is a
            // legal launch surface — jumping out of a hang (LedgeGrab
            // releases on an inward/neutral jump press) or out of a
            // committed vault at the lip, where no ground or wall binds and
            // the steal would otherwise fizzle. Corners are static terrain:
            // Enter's sourceVy is 0 and the hold window rides
            // vars.JumpFromCorner instead of a source FSD.
            if (!TryCornerLaunch(ctx, abilities, out _)) return false;
            return !(ctx.TryGetCeiling(out var c)
                     && ctx.Body.Position.Y - c.Position.Y <= 2 * Chunk.TileSize);
        }
        // Hitstun/stun lock-out is enforced centrally via RequiredCapabilities.Jump
        // (the selection loop drops jump candidates while BlocksJump). Movement
        // otherwise stays free — it only blocks the cheap vertical-reset option.
        // Low ceiling (≤ 2 tiles) overhead: head would smack — defer to CoveredJumpState.
        if (ctx.TryGetCeiling(out var ceiling)
            && ground.Position.Y - ceiling.Position.Y <= 2 * Chunk.TileSize) return false;
        return true;
    }

    // A corner the body just gripped (hang, or a climb's animation grip) within
    // arm's reach counts as a push-off point.
    private const float CornerLaunchReach = 24f;
    internal static bool TryCornerLaunch(EnvironmentContext ctx, PlayerAbilityState abilities,
                                         out Vector2 corner)
    {
        corner = default;
        switch (ctx.PreviousState(0))
        {
            case LedgeGrabState:
                corner = abilities.GrabbedCorner;
                break;
            //TODO remove dependency on Animation layer data
            case ClimbManeuverBase climb when climb.TryAnimationGrip(out corner):
                break;
            default:
                return false;
        }
        return Vector2.DistanceSquared(corner, ctx.Body.Position)
               <= CornerLaunchReach * CornerLaunchReach;
    }

    // Lattice engine (Plans/LATTICE_PATH_PLANNER.md §7.3): the jump is a
    // planned rise, not a fired impulse. Enter sets no velocity, Update
    // applies no hold force and adds no source constraint; the state hands
    // the tracker FoldProfile.Jump (hover off, u up-and-along-intent) and the
    // legs spend themselves along the rising path. The state lasts while the
    // button is held and the body rises; Falling owns the descent as before.
    private static bool OnLattice => MovementConfig.Current.FoldEngine == "lattice";

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (OnLattice)
        {
            if (vars.JumpReleased) return false;
            // The apex ends the jump — after the first ticks, so the legs
            // have had a chance to act on the plan.
            if (vars.TimeInState >= 2f * ctx.Dt && ctx.Body.Velocity.Y >= 0f) return false;
            return vars.JumpFromCorner || TryFindSource(ctx, out _);
        }
        if (vars.JumpReleased || vars.TimeInState >= MovementConfig.Current.MaxJumpHoldTime) return false;
        // The jump is anchored to its source surface. Once the body has risen out
        // of the (wider-than-Standing) probe window, the "relative-to-source" frame
        // no longer means anything — end the jump and let Falling take over.
        // Corner launches have no source FSD at all: their frame is the static
        // corner, valid for the whole hold window.
        return vars.JumpFromCorner || TryFindSource(ctx, out _);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);

        // Replace any pre-existing FSD (e.g. StandingState's _ground) with our own
        // source FSD: same kind of contact, just tuned for an airborne body.
        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
        if (OnLattice)
        {
            // No source constraint, no impulse: the tracker's legs are the
            // support and the launch (see OnLattice).
            vars.JumpFromCorner = !TryFindSource(ctx, out _);
            return;
        }
        EnsureSource(ctx);

        // Vertical velocity is set *relative* to the source surface, not added to
        // the body's current vy. Adding to the current velocity produces pathological
        // launches when the body enters with redirected vy (e.g. mid-Parkour ramp).
        // With no source at all this is a corner launch — corners are static, so
        // the frame is the world's.
        float sourceVy = _source?.SurfaceVelocity.Y ?? 0f;
        ctx.Body.Velocity.Y = sourceVy + MovementConfig.Current.JumpVelocity;
        vars.JumpFromCorner = _source == null;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_source != null) ctx.Body.Constraints.Remove(_source);
        _source = null;
    }

    // Idempotent source-FSD acquisition — see StandingState.EnsureGround. No-op in
    // normal play (Enter established it); rebuilds after a restore drops it.
    private void EnsureSource(EnvironmentContext ctx)
    {
        if (_source != null) return;
        if (TryFindSource(ctx, out _source))
        {
            // Airborne — no tangential coupling to the source surface, else friction
            // would dominate the gentle air-drag tangential dynamics.
            _source.Friction = 0f;
            ctx.Body.Constraints.Add(_source);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (OnLattice)
        {
            vars.TimeInState += ctx.Dt;
            if (!ctx.Input.Space) vars.JumpReleased = true;
            var mo = ctx.Modifiers; var cf = MovementConfig.Current;
            ctx.Body.AppliedForce = new Vector2(AirControl.Apply(ctx,
                cf.AirAccel * mo.AirAccel, cf.MaxAirSpeed * mo.MaxAirSpeed, cf.AirDrag * mo.AirDrag), 0f);
            ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default, FoldProfile.Jump);
            return;
        }
        EnsureSource(ctx);
        vars.TimeInState += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        // Refresh the source FSD's pose so the body's vertical motion is tracked
        // relative to a moving source surface throughout the jump.
        if (_source != null && TryFindSource(ctx, out var refreshed))
        {
            _source.Position        = refreshed.Position;
            _source.Normal          = refreshed.Normal;
            _source.MinDistance     = refreshed.MinDistance;
            _source.SurfaceVelocity = refreshed.SurfaceVelocity;
        }

        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        var force = Vector2.Zero;
        force.Y += cfg.JumpHoldForce;
        if (vars.TimeInState <= ctx.Dt)
            force.Y += cfg.JumpInitForce;

        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

        ctx.Body.AppliedForce = force;

        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default, FoldProfile.None);
    }

    private static bool TryFindSource(EnvironmentContext ctx, out FloatingSurfaceDistance source)
        => GroundChecker.TryFind(
            ctx.Body, ctx.Chunks,
            PlayerCharacter.Radius, PlayerCharacter.Radius,
            MovementConfig.Current.JumpSourceProbeSlack,
            ctx.Dt,
            out source);
}

public class RunningJumpState : MovementState
{
    public override int ActivePriority => MovementPriorities.RunningJumpActive;
    public override int PassivePriority => MovementPriorities.RunningJumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;

    private FloatingSurfaceDistance _source;

    public override void ResetTransient() => _source = null;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        if (!ctx.TryGetGround(out var ground)) return false;
        if (Math.Abs(ctx.Body.Velocity.X) < MovementConfig.Current.RunJumpMinSpeed) return false;
        if (ctx.TryGetCeiling(out var ceiling)
            && ground.Position.Y - ceiling.Position.Y <= 2 * Chunk.TileSize) return false;
        return true;
    }

    // Lattice engine (Plans/LATTICE_PATH_PLANNER.md §7.3): the jump is a
    // planned rise, not a fired impulse. Enter sets no velocity, Update
    // applies no hold force and adds no source constraint; the state hands
    // the tracker FoldProfile.Jump (hover off, u up-and-along-intent) and the
    // legs spend themselves along the rising path. The state lasts while the
    // button is held and the body rises; Falling owns the descent as before.
    private static bool OnLattice => MovementConfig.Current.FoldEngine == "lattice";

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (OnLattice)
        {
            if (vars.JumpReleased) return false;
            // The apex ends the jump — after the first ticks, so the legs
            // have had a chance to act on the plan.
            if (vars.TimeInState >= 2f * ctx.Dt && ctx.Body.Velocity.Y >= 0f) return false;
            return vars.JumpFromCorner || TryFindSource(ctx, out _);
        }
        if (vars.JumpReleased || vars.TimeInState >= MovementConfig.Current.MaxJumpHoldTime) return false;
        return TryFindSource(ctx, out _);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);

        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
        EnsureSource(ctx);

        // See JumpingState.Enter — vy is relative to source, not additive.
        float sourceVy = _source?.SurfaceVelocity.Y ?? 0f;
        ctx.Body.Velocity.Y = sourceVy + MovementConfig.Current.RunJumpVelocity;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_source != null) ctx.Body.Constraints.Remove(_source);
        _source = null;
    }

    // Idempotent source-FSD acquisition — see JumpingState.EnsureSource.
    private void EnsureSource(EnvironmentContext ctx)
    {
        if (_source != null) return;
        if (TryFindSource(ctx, out _source))
        {
            _source.Friction = 0f;
            ctx.Body.Constraints.Add(_source);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (OnLattice)
        {
            vars.TimeInState += ctx.Dt;
            if (!ctx.Input.Space) vars.JumpReleased = true;
            var mo = ctx.Modifiers; var cf = MovementConfig.Current;
            ctx.Body.AppliedForce = new Vector2(AirControl.Apply(ctx,
                cf.AirAccel * mo.AirAccel, cf.MaxAirSpeed * mo.MaxAirSpeed, cf.AirDrag * mo.AirDrag), 0f);
            ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default, FoldProfile.Jump);
            return;
        }
        EnsureSource(ctx);
        vars.TimeInState += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        if (_source != null && TryFindSource(ctx, out var refreshed))
        {
            _source.Position        = refreshed.Position;
            _source.Normal          = refreshed.Normal;
            _source.MinDistance     = refreshed.MinDistance;
            _source.SurfaceVelocity = refreshed.SurfaceVelocity;
        }

        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        var force = Vector2.Zero;
        force.Y += cfg.RunJumpHoldForce;
        if (vars.TimeInState <= ctx.Dt)
            force.Y += cfg.JumpInitForce;

        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

        ctx.Body.AppliedForce = force;

        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default, FoldProfile.None);
    }

    private static bool TryFindSource(EnvironmentContext ctx, out FloatingSurfaceDistance source)
        => GroundChecker.TryFind(
            ctx.Body, ctx.Chunks,
            PlayerCharacter.Radius, PlayerCharacter.Radius,
            MovementConfig.Current.JumpSourceProbeSlack,
            ctx.Dt,
            out source);
}

public class DoubleJumpingState : MovementState
{
    public override int ActivePriority => MovementPriorities.DoubleJumpActive;
    public override int PassivePriority => MovementPriorities.DoubleJumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;
    public override AnimTag AnimationTag => AnimTag.DoubleJump;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // No wall check: when the player IS pressing into a wall, WallJumpingState wins outright
        // (its Passive 45 beats DoubleJump's 40). When they're NOT pressing into a wall — e.g.
        // dropping off a platform while holding the away direction — DoubleJump is the right fire.
        return ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)
            && !abilities.HasDoubleJumped && !ctx.TryGetGround(out _);
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

        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default, FoldProfile.None);
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
    public override void ResetTransient() { _ground = null; _groundIsCrouch = false; }
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
        // Only relevant for low ceilings (≤ 2 tiles). At 3+ tiles a regular jump fits with margin —
        // JumpingState handles those, and its precondition is the complement of this one.
        if (ground.Position.Y - ceiling.Position.Y > 2 * Chunk.TileSize) return false;
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
                ctx.Body.Constraints.Add(_ground);
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
                ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
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
            ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
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

        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
    }
}
