using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The ledge family: hang on a corner (LedgeGrab), pull up from the hang
// (LedgePull), and the jump at the top of a pull (LedgeJump). All servo the
// body against a gripped corner, so the ambient corrector is Off throughout.
// The input contract lives in Plans/LEDGE_PULL_INPUT_MATRIX.md.

// Attaches the player to a ledge. Stays active until the player drops (Down/away)
// or pulls up (Up just pressed → transitions to LedgePullState).
public class LedgeGrabState : MovementState
{
    public override AnimTag AnimationTag => AnimTag.LedgeGrab;

    private readonly int _wallDir;
    private FloatingSurfaceDistance _wall;
    private FloatingSurfaceDistance _floor;

    public override void ResetTransient() { _wall = null; _floor = null; }

    public LedgeGrabState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => MovementPriorities.LedgeGrabActive;
    public override int PassivePriority => MovementPriorities.LedgeGrabPassive;
    // Body is pinned to the corner — an ambient redirect would fight the hang contacts.
    public override AmbientPolicy AmbientPolicy => AmbientPolicy.Off;
    // Blocked during combat hitstun/stun (Phase 4) — a launch past a ledge can't be
    // cancelled by catching it. The pull (LedgePullState) is entered FROM a grab, so
    // gating the grab already prevents the pull; it carries the flag too for clarity.
    public override MovementCapability RequiredCapabilities => MovementCapability.LedgeGrab;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Path A: approach from side — Up pressed, wall + ledge corner above head detected
        if (ctx.TryGetWall(_wallDir, out _) && ctx.TryGetLedgeCorner(_wallDir, out _))
        {
            const int Buffer = 6;
            for (int i = 0; i < Buffer; i++)
            {
                if (ctx.PreviousState(i + 1) is LedgeGrabState or LedgePullState) break;
                if (ctx.Controller.GetPrevious(i).Up && !ctx.Controller.GetPrevious(i + 1).Up)
                    return true;
            }
        }
        // Path B: drop from above — Down just pressed, exposed corner at foot level
        if (abilities.DownJustPressed && ctx.TryGetExposedCorner(_wallDir, out _))
            return true;
        // Path C: re-grab after an abandoned pull — the pull ended (Up released, or
        // MaxVaultTime ran out) before the body made it over the corner, so the hands
        // are still on the lip. Re-entering the hang lets its spring/damper absorb the
        // pull's velocity through the contact, instead of the body exiting airborne
        // with a jump-sized vy (Plans/LEDGE_PULL_INPUT_MATRIX.md rows D-H, N).
        if (ctx.PreviousState(0) is LedgePullState pull && pull.WallDir == _wallDir
            && !PullCompleted(ctx, abilities))
            return true;
        return false;
    }

    // Same geometry as LedgePullState's completion test: standing height AND past the
    // corner horizontally. A completed pull must exit onto the platform, not re-grab.
    private bool PullCompleted(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var corner = abilities.GrabbedCorner;
        bool atStandingHeight = ctx.Body.Position.Y < corner.Y - 2f * PlayerCharacter.Radius;
        bool pastCorner       = _wallDir == 1
            ? ctx.Body.Position.X > corner.X
            : ctx.Body.Position.X < corner.X;
        return atStandingHeight && pastCorner;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Entry grace (~3 frames): drop-in enters with Down still held, and a re-grab
        // from an abandoned pull needs the damper a few frames to absorb the pull's
        // velocity before an away/Down exit may carry it out of the state.
        if (vars.TimeInState < 0.1f) return true;
        bool pressingAway = (_wallDir == 1 && ctx.Input.Left) || (_wallDir == -1 && ctx.Input.Right);
        if (pressingAway) return false;
        // movement_todo #4: an inward/neutral jump press launches OFF the
        // corner — release the hang; JumpingState's corner branch binds in
        // the same-frame reselection (away-presses exited above and go to
        // WallJump as before, which outbids the fresh Jump anyway).
        if (ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames))
            return false;
        return !ctx.Input.Down;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;

        // Re-grab (path C) keeps the corner the pull was working on — the checkers
        // can't see it from the risen pose — and keeps the body's velocity: the hang
        // spring/damper dissipates it through the hand contact rather than an
        // impulsive write. Fresh grabs zero velocity (an impulsive catch) and read
        // the corner from the checkers as before.
        bool regrab = ctx.PreviousState(0) is LedgePullState;
        if (!regrab)
        {
            // Prefer above-head corner (approach from side); fall back to foot-level (drop from above)
            Vector2 cornerEdge;
            if (ctx.TryGetLedgeCorner(_wallDir, out var grabCorner))
                cornerEdge = grabCorner.InnerEdge;
            else
            {
                ctx.TryGetExposedCorner(_wallDir, out var dropCorner);
                cornerEdge = dropCorner.InnerEdge;
            }

            ctx.Body.Velocity = Vector2.Zero;
            abilities.GrabbedCorner = cornerEdge;
        }

        abilities.IsLedgeGrabbing  = true;
        abilities.GrabWallDir      = _wallDir;
        abilities.HasDoubleJumped  = false;
        // Face the ledge for the whole hang, same reason WallSlidingState pins facing: the
        // Hang clip reaches BOTH hands to the corner at +X·Radius, so a drop-in grab (path B,
        // which can enter facing away) would otherwise clutch at empty air behind the body.
        // Facing is snapshot-safe sim state and isn't otherwise refreshed while airborne.
        abilities.Facing           = _wallDir;

        EnsureContacts(ctx, abilities);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_wall  != null) ctx.Body.Constraints.Remove(_wall);
        if (_floor != null) ctx.Body.Constraints.Remove(_floor);
        _wall  = null;
        _floor = null;
        abilities.IsLedgeGrabbing = false;
        abilities.GrabWallDir     = 0;
    }

    // Idempotent pin acquisition, rebuilt from the (snapshotted) GrabbedCorner.
    // Wall pin: detected wall, or derived from corner X when approaching from above.
    // Floor pin: a horizontal plane two radii below the corner. No-op in normal play.
    private void EnsureContacts(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var cornerEdge = abilities.GrabbedCorner;
        if (_wall == null)
        {
            if (!ctx.TryGetWall(_wallDir, out _wall))
                _wall = new FloatingSurfaceDistance(
                    new Vector2(cornerEdge.X, ctx.Body.Position.Y),
                    new Vector2(-_wallDir, 0f),
                    PlayerCharacter.Radius);
            ctx.Body.Constraints.Add(_wall);
        }
        if (_floor == null)
        {
            _floor = new FloatingSurfaceDistance(
                new Vector2(ctx.Body.Position.X, cornerEdge.Y + 2f * PlayerCharacter.Radius),
                new Vector2(0f, -1f),
                PlayerCharacter.Radius);
            ctx.Body.Constraints.Add(_floor);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureContacts(ctx, abilities);
        vars.TimeInState += ctx.Dt;
        var cfg = MovementConfig.Current;
        var corner = abilities.GrabbedCorner;

        // The hang is a 2D anchor — hands gripping a fixed corner — so spring-damp the
        // body toward the hang point on BOTH axes (symmetric: Radius below the corner
        // top, Radius to the body's side of the corner X). The horizontal term is what
        // absorbs a re-grab's retained away-from-wall velocity; without it the body
        // coasts off forever, since the wall pin is one-sided (blocks moving INTO the
        // wall only) and nothing else damps X.
        float hangY = corner.Y + PlayerCharacter.Radius;
        float hangX = corner.X - _wallDir * PlayerCharacter.Radius;

        var force = Vector2.Zero;
        force.X = SpringDampForce(ctx.Body.Position.X - hangX, ctx.Body.Velocity.X, cfg, ctx.Dt);
        force.Y = -cfg.GrabGravityCancel
                + SpringDampForce(ctx.Body.Position.Y - hangY, ctx.Body.Velocity.Y, cfg, ctx.Dt);
        ctx.Body.AppliedForce = force;
    }

    // Spring toward an anchor with a saturated damper. The raw linear damping term
    // (GrabDamping=100 vs 1/dt=30) overshoots per Euler step — harmless while the hang
    // FSDs clamp the body, but a re-grab from an abandoned pull arrives at pull speed
    // and would oscillate divergently. Clamp the damper at the force that exactly zeroes
    // velocity this frame. Same saturated-brake idiom as LedgePull's crest.
    private static float SpringDampForce(float error, float vel, MovementConfig cfg, float dt)
    {
        float damping = -vel * cfg.GrabDamping;
        if (dt > 0f)
        {
            float cancel = -vel / dt;
            if (MathF.Abs(damping) > MathF.Abs(cancel)) damping = cancel;
        }
        return -error * cfg.GrabSpringK + damping;
    }
}

// Executes the pull-up from a ledge grab. Activated by pressing Up while grabbed.
// Releasing Up during the pull interrupts it. Like DropdownState (its mirror —
// down over an edge vs up over one), the pull is a committed maneuver on the
// shared solve: ManeuverCorrector.Apply runs around the authored servo (clip or
// bespoke), airborne entry, so the lip graze and the over-the-corner carry get
// the same corrector treatment as the climb family.
public class LedgePullState : MovementState
{
    public override AnimTag AnimationTag => AnimTag.LedgePull;

    private readonly int _wallDir;
    private PointForceContact _spring;
    private FloatingSurfaceDistance _ramp;

    public override void ResetTransient() { _spring = null; _ramp = null; }

    public LedgePullState(int wallDir) => _wallDir = wallDir;

    // Read by LedgeGrabState's re-grab path and LedgeJumpState's preconditions to
    // match the side they're taking over from.
    public int WallDir => _wallDir;

    public override int ActivePriority  => MovementPriorities.LedgePullActive;
    public override int PassivePriority => MovementPriorities.LedgePullPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.LedgeGrab;
    // Servo pull along its own path — no ambient redirect on top.
    public override AmbientPolicy AmbientPolicy => AmbientPolicy.Off;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => abilities.UpJustPressed
        && abilities.IsLedgeGrabbing
        && abilities.GrabWallDir == _wallDir;

    // The pull owns the corner while it executes — and for the one frame after it exits,
    // since the selection loop calls Suppresses on PreviousState(0). Both rules below
    // previously lived as `PreviousState(0) is LedgePullState` gates inside the candidate
    // states; centralizing them here makes the pull's contract local and complete.
    public override bool Suppresses(MovementState candidate, EnvironmentContext ctx)
    {
        // Once the body rises beside the lip the ledge top reads as ground, so an inward
        // hold satisfies the climb family's preconditions and its passive (46) would
        // steal the maneuver from the pull (43). The pull completes/exits on its own terms
        // and a queued jump routes to LedgeJumpState (LEDGE_PULL_INPUT_MATRIX.md rows B, K).
        if (candidate is ClimbManeuverBase) return true;
        // Mid-pull, only an *away* press reads as "kick off the wall and bail". An inward
        // press means "jump up onto the ledge" — suppress WallJump so the intent stays
        // queued for LedgeJumpState at the top (row K). Use the candidate's own wall side,
        // exactly as the old in-WallJump gate did.
        if (candidate is WallJumpingState wj)
        {
            bool pressingAway = wj.WallDir == 1 ? ctx.Input.Left : ctx.Input.Right;
            return !pressingAway;
        }
        return false;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (!ctx.Input.Up) return false;
        if (vars.TimeInState >= MovementConfig.Current.MaxVaultTime) return false;

        // Completion is judged against the grabbed corner. Reference mode has no
        // spring contact — the corner comes straight from abilities (the same
        // value the bespoke spring was seeded with, and snapshot-covered).
        Vector2 corner;
        if (vars.RefActive) corner = abilities.GrabbedCorner;
        else { if (_spring == null) return false; corner = _spring.Position; }

        bool atStandingHeight = ctx.Body.Position.Y < corner.Y - 2f * PlayerCharacter.Radius;
        bool pastCorner       = _wallDir == 1
            ? ctx.Body.Position.X > corner.X
            : ctx.Body.Position.X < corner.X;
        return !(atStandingHeight && pastCorner);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        vars.ManeuverChannelPrev = default;   // fresh Δ anchors for this maneuver's solve
        var clip = MovementConfig.Current.UseReferenceClips
            ? ReferenceClipRegistry.Get(ReferenceClipRegistry.LedgePull) : null;
        vars.RefActive = clip != null;
        if (vars.RefActive)
        {
            // Retarget at Enter (BALLISTIC_CORRECTOR_PLAN §1): clip (0,0) = the hang
            // pose the body actually holds; clip (1,-1) = standing on top just past
            // the corner, placed a few px beyond the completion test's thresholds so
            // the servo carries the body through them rather than stalling on them.
            var corner = abilities.GrabbedCorner;
            vars.RefEntry    = ctx.Body.Position;
            vars.RefGate     = new Vector2(corner.X + _wallDir * (PlayerCharacter.Radius + 2f),
                                           corner.Y - (2f * PlayerCharacter.Radius + 4f));
            vars.RefProgress = 0f;
            return;
        }
        EnsureContacts(ctx, abilities);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_spring != null) ctx.Body.Constraints.Remove(_spring);
        if (_ramp   != null) ctx.Body.Constraints.Remove(_ramp);
        _spring = null;
        _ramp   = null;
    }

    // Idempotent acquisition, rebuilt from the (snapshotted) GrabbedCorner. The
    // spring lasts the whole pull; the ramp only applies until the body rises past
    // the corner lip (Update removes it then) — so its rebuild is gated on the same
    // height test, otherwise a restore taken after the ramp was dropped would wrongly
    // re-add it. No-op in normal play.
    private void EnsureContacts(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var cornerEdge = abilities.GrabbedCorner;
        if (_spring == null)
        {
            _spring = new PointForceContact(cornerEdge);
            ctx.Body.Constraints.Add(_spring);
        }
        bool rampApplies = ctx.Body.Position.Y >= cornerEdge.Y - 2f * PlayerCharacter.Radius;
        if (_ramp == null && rampApplies)
        {
            var rampNormal = new Vector2(-_wallDir * 0.5f, -0.5f);
            _ramp = new FloatingSurfaceDistance(cornerEdge, rampNormal, 1000f);
            ctx.Body.Constraints.Add(_ramp);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState += ctx.Dt;

        // Keep a queued jump alive while committed to the pull: LedgeJumpState
        // consumes it at the natural jump point (standing height beside the lip).
        // The keep-alive stops when the pull ends, so an unconsumed press expires
        // on its normal window (e.g. after a release → re-grab).
        ctx.Intents.Refresh(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);

        if (vars.RefActive)
        {
            var clip = ReferenceClipRegistry.Get(ReferenceClipRegistry.LedgePull);
            if (clip != null)
            {
                // A mid-pull hot-reload flip can leave bespoke contacts attached —
                // the servo replaces them, so drop any strays.
                if (_spring != null) { ctx.Body.Constraints.Remove(_spring); _spring = null; }
                if (_ramp   != null) { ctx.Body.Constraints.Remove(_ramp);   _ramp   = null; }
                var cfg2 = MovementConfig.Current;
                vars.RefProgress += ctx.Dt / MathF.Max(cfg2.LedgePullRefDuration, 1e-4f);
                ctx.Body.AppliedForce = ReferencePath.TrackForce(clip,
                    new ReferenceFrame(vars.RefEntry, vars.RefGate),
                    vars.RefProgress, cfg2.LedgePullRefDuration, ctx.Body, ctx.Gravity, cfg2);
                ApplyCorrector(ctx, ref vars);
                return;
            }
            vars.RefActive = false;   // clip vanished (dev-only): fall through to bespoke
        }

        EnsureContacts(ctx, abilities);
        float cornerTopY = _spring.Position.Y;
        var   cfg        = MovementConfig.Current;
        var   force      = Vector2.Zero;

        if (ctx.Body.Position.Y >= cornerTopY - 2f * PlayerCharacter.Radius)
        {
            force.Y = -cfg.VaultLiftForce;
        }
        else
        {
            if (_ramp != null) { ctx.Body.Constraints.Remove(_ramp); _ramp = null; }
            if (ctx.Body.Velocity.Y < 0f && ctx.Dt > 0f)
                force.Y = Math.Min(-ctx.Body.Velocity.Y / ctx.Dt, 2f * cfg.VaultLiftForce);
            force.X = _wallDir * cfg.VaultPushForce;
        }

        ctx.Body.AppliedForce = force;
        ApplyCorrector(ctx, ref vars);
    }

    // The shared maneuver solve around the committed pull (Dropdown's mirror):
    // airborne entry — a hang is not a stand — with the guided drive mirroring
    // the over-the-lip carry at walk speed toward the ledge side.
    private void ApplyCorrector(EnvironmentContext ctx, ref MovementVars vars)
        => ManeuverCorrector.Apply(ctx, _wallDir, MovementConfig.Current.MaxWalkSpeed,
                                   ref vars.ManeuverChannelPrev);
}

// Jump executed at the top of a ledge pull — the natural jump point where the body
// reaches standing height beside the lip — without completing the over-the-corner
// push or touching ground. Fires from a queued (or fresh) Jump intent that
// LedgePullState keep-alives; anchors to abilities.GrabbedCorner rather than a
// ground probe, because beside the lip there is no reliable ground yet. The launch
// is a JumpServo toward LedgeJumpTargetVy relative to the (static) ledge, so the
// pull's surplus vy is braked down to a normal jump, not stacked onto one
// (Plans/LEDGE_PULL_INPUT_MATRIX.md rows I, J′, K).
public class LedgeJumpState : MovementState
{
    private readonly int _wallDir;

    public LedgeJumpState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => MovementPriorities.LedgeJumpActive;
    public override int PassivePriority => MovementPriorities.LedgeJumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;
    // Launch off the pull is a committed arc past the very corner an ambient ramp
    // would try to steer around — keep the layer out for the launch frames.
    public override AmbientPolicy AmbientPolicy => AmbientPolicy.Off;
    public override AnimTag AnimationTag => AnimTag.LedgeJump;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (ctx.PreviousState(0) is not LedgePullState pull || pull.WallDir != _wallDir) return false;
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        // Natural jump point: the body has risen to standing height beside the lip.
        return ctx.Body.Position.Y <= abilities.GrabbedCorner.Y - 2f * PlayerCharacter.Radius;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => !vars.JumpReleased && vars.TimeInState < MovementConfig.Current.MaxJumpHoldTime;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState  = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);
        // No velocity write — the servo in Update IS the launch.
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        var force = Vector2.Zero;
        // Ledge terrain is static — sourceVy = 0.
        force.Y = JumpServo.Force(ctx.Body.Velocity.Y, 0f,
            cfg.LedgeJumpTargetVy, cfg.LedgeJumpServoAccel, cfg.LedgeJumpGravityCancel, ctx.Dt);
        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

        ctx.Body.AppliedForce = force;
    }
}
