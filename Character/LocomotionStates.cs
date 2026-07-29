using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Baseline locomotion: standing/walking, crouching, free fall, and the
// platform slip-off. Standing/Crouched/Falling are FOLD states — support,
// walk drive, braking, and the landing catch are all the ambient corrector's
// job (FoldProfile reference shaping + the channel stack); the states keep
// classification and the gravity-hold baseline only.

public class FallingState : MovementState
{
    public override int ActivePriority => MovementPriorities.FallingActive;
    public override int PassivePriority => MovementPriorities.FallingPassive;

    // Falling is a fold state: the landing catch (anchor re-binding on descent)
    // and the graze/duck assists all run through the fold solve. High free fall
    // is naturally unbound (anchor beyond leg reach ⇒ no envelope rows).
    public override FoldProfile FoldProfile => FoldProfile.Stand;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities) => true;
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars) => true;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        var force = Vector2.Zero;
        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

        if (ctx.Input.Down)
            force.Y += cfg.FastFallForce;

        ctx.Body.AppliedForce = force;
    }
}

public class StandingState : MovementState
{
    public override int ActivePriority => MovementPriorities.StandingActive;
    public override int PassivePriority => MovementPriorities.StandingPassive;

    public override FoldProfile FoldProfile => FoldProfile.Stand;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return IsStandingGround(ctx);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Stay-active uses plain ground detection — only ENTRY is gated (below). An
        // already-standing body that's briefly flung up (e.g. a sprout growing up
        // under it) must keep Standing so its spring tracks the lift; kicking it to
        // Falling for a frame would slam its velocity. The launch case the entry gate
        // guards against never has Standing as the current state (the jump does).
        return ctx.TryGetGround(out _);
    }

    // GroundChecker's 20px ProbeSlack reports "grounded" for a body up to ~20px above
    // rest height — which, with the slow JumpVelocity launch, holds for the whole jump
    // window. So a quick jump-release would drop JumpingState and let Standing re-grab
    // the still-ascending body. Refuse to grab a body rising faster than support could
    // ever push it (SpringMaxRiseSpeed): that's a launch, not standing. ENTRY also
    // requires support proximity (SupportReach — the gravity-hold band): a body the
    // probe merely SEES 40px up is still flying (a pass-by, or an inbound landing whose
    // descent will cross the engagement gate and flicker the state) — it becomes
    // Standing when support can actually bind it. Continuation (CheckConditions) stays
    // the plain probe: states are sticky.
    private static bool IsStandingGround(EnvironmentContext ctx)
    {
        if (!ctx.TryGetGround(out var ground)) return false;
        float riseSpeed = Vector2.Dot(ctx.Body.Velocity - ground.SurfaceVelocity, ground.Normal);
        if (riseSpeed > MovementConfig.Current.SpringMaxRiseSpeed) return false;
        float dist = Vector2.Dot(ctx.Body.Position - ground.Position, ground.Normal);
        return dist <= BallisticPredictor.SupportReach;
    }

    // The stand fold (see AmbientCorrector): no ground FSD, no hover spring —
    // vertical support, walk drive, braking, and the landing catch are all the
    // ambient solve's job (soft envelope rows + the channel stack). Standing
    // keeps classification only.
    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // The one baseline Standing applies is the gravity hold — sustained
        // support is feedforward (mirrored by the predictor's grounded branch);
        // the solver's channels act relative to it. DC demands never belong in
        // the solver's soft rows: without the hold it must win a tug-of-war
        // against gravity at dt² leverage every frame, which it structurally
        // cannot (the post-landing dead-rest bug).
        //
        // The hold engages only while the support anchor binds (floor within
        // SupportReach below the center — same gate as the coast's grounded
        // classification). The ground PROBE reaches ~40px down, and Standing can
        // legitimately be active over that whole band; holding against gravity
        // up there would turn a ballistic pass-by into a zero-g floater and
        // hand the solver a coast the live tick contradicts.
        ctx.Body.AppliedForce = FoldBaseline(ctx);
    }

    // Shared fold-state baseline, mirrored tick-for-tick by the predictor's
    // grounded branch: gravity hold iff supported (see Update above), plus
    // station friction at no input — the fold body never physically touches
    // the floor, so SurfaceContact.Friction can't brake it; this term is that
    // friction re-expressed as feedforward (the old "no-input → braking"
    // role). Hitstun scales it down through Modifiers.GroundFriction exactly
    // as it scaled the old contact friction.
    internal static Vector2 FoldBaseline(EnvironmentContext ctx)
    {
        // Supported = floor within reach AND not rising beyond what support
        // could push (SpringMaxRiseSpeed). Without the rise gate, a body flung
        // upward while near a floor (a sprout growing under it, a pop-out)
        // keeps the hold — zero gravity — and rides its launch indefinitely.
        bool supported = ctx.TryGetGround(out var ground)
            && -ctx.Body.Velocity.Y <= MovementConfig.Current.SpringMaxRiseSpeed;
        if (!supported) return Vector2.Zero;
        float dist = ground.Position.Y - ctx.Body.Position.Y;
        // The hold FADES across the spring's old support range: full inside
        // the rest band (2R ≈ the old FSD MinDistance), zero at SupportReach.
        // A body floating above hover gets mostly-real gravity — the old
        // spring gave nothing above its rest length, and a binary hold out to
        // SupportReach let sprout-lifted bodies coast up in zero-g.
        float holdScale = Math.Clamp(
            (BallisticPredictor.SupportReach - dist)
                / (BallisticPredictor.SupportReach - BallisticPredictor.HoldFullDist), 0f, 1f);
        if (holdScale <= 0f) return Vector2.Zero;
        var force = new Vector2(0f, -ctx.Gravity.Y * holdScale);
        if (ctx.Intent.CurrentHorizontal == 0 && ctx.Dt > 0f)
        {
            float cap = MovementConfig.Current.GroundFriction * ctx.Modifiers.GroundFriction;
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -cap, cap) * holdScale;
        }
        return force;
    }
}

// Crouched is a fold state (CORRECTOR_CONSOLIDATION_PLAN §3.1): same mechanism
// as Standing, lower reference — FoldProfile.Crouch drops the hover target to
// the C-obstacle surface and caps progress at crawl speed. No FSD, no spring,
// no bespoke drive: the crouch IS reference shaping. The state keeps only
// classification (when crouching applies) and the gravity-hold baseline.
public class CrouchedState : MovementState
{
    public override AnimTag AnimationTag => AnimTag.Crouch;
    public override int ActivePriority => MovementPriorities.CrouchedActive;
    public override int PassivePriority => MovementPriorities.CrouchedPassive;

    public override FoldProfile FoldProfile => FoldProfile.Crouch;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.TryGetCrouchGround(out _)) return false;
        if (ctx.Input.Down) return true;
        // Auto-crouch: STANDING AT FOLD HOVER doesn't fit here. The fold-era
        // standing envelope is hover offset + body height ≈ 30.8px — lower than
        // the old FSD StandingHeight (32.8, float height + body), which made
        // 2-high/32px corridors auto-crouch even though the hover-held body
        // threads them upright with ~1px to spare (the restricted corridor
        // harness proves it at full walk speed). Crouch only when the gap is
        // genuinely below the hover-standing envelope.
        float standingClearance = MovementConfig.Current.FoldHoverOffset
            + (PlayerCharacter.StandingHeight - PlayerCharacter.Radius);
        return ctx.TryGetGround(out var ground)
            && ctx.TryGetCeiling(out var ceiling)
            && ground.Position.Y - ceiling.Position.Y < standingClearance + 0.5f;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        return (ctx.Input.Down || ctx.TryGetCeiling(out _)) && ctx.TryGetCrouchGround(out _);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // The shared fold baseline (see StandingState.Update).
        ctx.Body.AppliedForce = StandingState.FoldBaseline(ctx);
    }
}

// Hold Down while standing on the edge of a platform → slip off. Removes the float-height ground
// constraint so the body's no longer spring-held above the surface (gravity + tile collision keep
// it on the platform until its center clears the edge), applies a horizontal slide force in the
// chosen direction (so a crouched/sunken body actually gets pushed out from over the platform —
// same idiom as CoveredJumpState's phase 1). The ambient corrector's corner rows are the
// insurance against clipping the drop edge mid-slip. Exits to FallingState the instant the
// body's no longer over any floor.
public class DropdownState : MovementState
{
    // DropDir, SlideSpeed, SlideTime, ExitingAirborne now live in MovementVars.

    public override int ActivePriority  => MovementPriorities.DropdownActive;
    public override int PassivePriority => MovementPriorities.DropdownPassive;
    public override AnimTag AnimationTag => AnimTag.Dropdown;

    // Same pattern as CoveredJumpState.TryPickOpenDir: honor input direction strictly when held,
    // closer edge from a standstill, never flip to the opposite side. Edge from GroundChecker.
    //
    // The IsHangingOver gate (mirrors CoveredJump's IsStickingOut) keeps Dropdown from firing
    // when the body is fully on the platform — the player should crouch in that case, not slip.
    // Only fires once some portion of the body's bounding box has pushed past the drop edge.
    private static bool TryPickDropDir(EnvironmentContext ctx, out int dir, out Vector2 corner)
    {
        int want = ctx.Intent.CurrentHorizontal;
        var bounds = ctx.Body.Bounds;

        if (want != 0)
        {
            if (GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks, want, out corner)
                && IsHangingOver(bounds, want, corner.X))
            { dir = want; return true; }
            dir = 0; corner = default; return false;
        }
        bool hasR = GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks,  1, out var cR) && IsHangingOver(bounds,  1, cR.X);
        bool hasL = GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks, -1, out var cL) && IsHangingOver(bounds, -1, cL.X);
        if (!hasR && !hasL) { dir = 0; corner = default; return false; }
        if (!hasL) { dir =  1; corner = cR; return true; }
        if (!hasR) { dir = -1; corner = cL; return true; }
        float distR = cR.X - ctx.Body.Position.X;
        float distL = ctx.Body.Position.X - cL.X;
        if (distR <= distL) { dir =  1; corner = cR; return true; }
        dir = -1; corner = cL; return true;
    }

    // Some portion of the body's bounding box has crossed the drop edge — i.e. is over
    // empty air rather than over the platform. Mirrors CoveredJumpState.IsStickingOut.
    private static bool IsHangingOver(BoundingBox bounds, int dir, float edgeX)
        => dir == 1 ? bounds.Right > edgeX : bounds.Left < edgeX;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Input.Down) return false;
        if (!ctx.TryGetGround(out _)) return false;
        return TryPickDropDir(ctx, out _, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (!ctx.Input.Down) return false;
        if (!ctx.TryGetGround(out _)) { vars.ExitingAirborne = true; return false; }   // body's airborne ⇒ Falling takes over
        return vars.SlideTime < MovementConfig.Current.MaxDropdownTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        TryPickDropDir(ctx, out vars.DropDir, out var corner);
        // MaxWalkSpeed is the slide target — fast enough to clear the corner within MaxDropdownTime
        // from a standstill. Running entries keep their momentum since Update only applies force when
        // the body's slower than the target.
        vars.SlideSpeed = MovementConfig.Current.MaxWalkSpeed;
        vars.SlideTime = 0f;
        vars.ExitingAirborne = false;
        // No FloatingSurfaceDistance: the body's leaving the surface, so don't spring it back up.
        // StandingState/CrouchedState's ground constraint was already removed on their Exit.

        var clip = MovementConfig.Current.UseReferenceClips
            ? ReferenceClipRegistry.Get(ReferenceClipRegistry.Dropdown) : null;
        vars.RefActive = clip != null && vars.DropDir != 0;
        if (vars.RefActive)
        {
            // Retarget at Enter: clip (0,0) = where the slide starts; clip (1,-1) = past
            // the drop corner and a body-height below it. Only the on-platform stretch
            // plays (going airborne exits to Falling), so the clip shapes the slide-out
            // speed profile and the velocity carried into the drop.
            vars.RefEntry    = ctx.Body.Position;
            vars.RefGate     = new Vector2(corner.X + vars.DropDir * (PlayerCharacter.Radius + 2f),
                                           corner.Y + 2f * PlayerCharacter.Radius);
            vars.RefProgress = 0f;
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Soften the horizontal velocity on the slip-off so the drop lands close to the wall rather
        // than flinging the body forward at the full slide speed. Only apply when we exited via going
        // airborne (not on cancel via !Down or timeout).
        if (vars.ExitingAirborne)
            ctx.Body.Velocity.X *= MovementConfig.Current.DropdownExitVelMult;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.SlideTime += ctx.Dt;
        var cfg = MovementConfig.Current;

        if (vars.RefActive)
        {
            var clip = ReferenceClipRegistry.Get(ReferenceClipRegistry.Dropdown);
            if (clip != null)
            {
                vars.RefProgress += ctx.Dt / MathF.Max(cfg.DropdownRefDuration, 1e-4f);
                ctx.Body.AppliedForce = ReferencePath.TrackForce(clip,
                    new ReferenceFrame(vars.RefEntry, vars.RefGate),
                    vars.RefProgress, cfg.DropdownRefDuration, ctx.Body, ctx.Gravity, cfg);
                return;
            }
            vars.RefActive = false;   // clip vanished (dev-only): fall through to bespoke
        }

        // Slide toward the edge, but never brake a faster-than-target body — a running entry should
        // keep its momentum through the slide. Gravity does the vertical work.
        float along = vars.DropDir * ctx.Body.Velocity.X;
        float fx = 0f;
        if (along < vars.SlideSpeed)
            fx = vars.DropDir * AirControl.SoftClampVelocity(along, vars.SlideSpeed, cfg.WalkAccel, ctx.Dt);
        ctx.Body.AppliedForce = new Vector2(fx, 0f);
    }
}
