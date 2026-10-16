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

    // Read by LedgeJumpState's preconditions to match the side it launches off.
    public int WallDir => _wallDir;

    public override int ActivePriority  => MovementPriorities.LedgeGrabActive;
    public override int PassivePriority => MovementPriorities.LedgeGrabPassive;
    // Blocked during combat hitstun/stun (Phase 4) — a launch past a ledge can't be
    // cancelled by catching it. The pull (LedgePullState) is entered FROM a grab, so
    // gating the grab already prevents the pull; it carries the flag too for clarity.
    public override MovementCapability RequiredCapabilities => MovementCapability.LedgeGrab;

    // From the hang, as mid-pull (LedgePullState.Suppresses): only an AWAY press
    // reads as "kick off the wall"; Space + inward means "jump up onto the ledge",
    // which is LedgeJumpState's — WallJump's precondition accepts either horizontal
    // and would otherwise eject the body from the ledge it is reaching for.
    public override bool Suppresses(MovementState candidate, EnvironmentContext ctx)
        => candidate is WallJumpingState wj
           && !(wj.WallDir == 1 ? ctx.Input.Left : ctx.Input.Right);

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Path A: approach from side — Up pressed, wall + ledge corner above head detected
        if (ctx.TryGetWall(_wallDir, out _) && ctx.TryGetLedgeCorner(_wallDir, out _)
            && RecentUpPress(ctx))
            return true;
        // Path B: drop from above — Down just pressed, exposed corner at foot level
        if (abilities.DownJustPressed && ctx.TryGetExposedCorner(_wallDir, out _))
            return true;
        // Path C: re-grab after an abandoned pull — the pull ended (Up released, or
        // MaxLipManeuverTime ran out) before the body made it over the corner, so the hands
        // are still on the lip. Re-entering the hang lets its spring/damper absorb the
        // pull's velocity through the contact, instead of the body exiting airborne
        // with a jump-sized vy (Plans/LEDGE_PULL_INPUT_MATRIX.md rows D-H, N).
        if (ctx.PreviousState(0) is LedgePullState pull && pull.WallDir == _wallDir
            && !PullCompleted(ctx, abilities))
            return true;
        // Path D: chain from a dropdown — Down released with the slide committed.
        // Dropdown.Exit made the offer (DropChainDir + GrabbedCorner) because the
        // corner checkers can't see the foot-level drop corner from the mid-slide
        // pose; one-frame window, same Exit-before-scan idiom as path C.
        if (ctx.PreviousState(0) is DropdownState && abilities.DropChainDir == _wallDir)
            return true;
        return false;
    }

    // An Up press within the last few frames (a slightly early press still catches a
    // ledge the body is about to reach), not one the ledge family already consumed.
    // Shared by the grab's side approach and the pull-from-above entry.
    internal static bool RecentUpPress(EnvironmentContext ctx)
    {
        const int Buffer = 6;
        for (int i = 0; i < Buffer; i++)
        {
            if (ctx.PreviousState(i + 1) is LedgeGrabState or LedgePullState) break;
            if (ctx.Controller.GetPrevious(i).Up && !ctx.Controller.GetPrevious(i + 1).Up)
                return true;
        }
        return false;
    }

    // Same geometry as LedgePullState's completion test: standing height AND past the
    // corner horizontally. A completed pull must exit onto the platform, not re-grab.
    // The height test keeps a 0.7R buffer (a body that high clears the lip and drops
    // onto it); the horizontal one does not: the body's bottom is a point, so a
    // center short of the corner edge slides back off the lip instead of landing.
    private bool PullCompleted(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var corner = abilities.GrabbedCorner;
        var buffer = 0.7f * PlayerCharacter.Radius;
        bool atStandingHeight = ctx.Body.Position.Y - buffer < corner.Y - 2f * PlayerCharacter.Radius;
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
        // corner — release the hang; LedgeJumpState binds in the same-frame
        // reselection (and preempts outright during the entry grace above;
        // away-presses exited above and go to WallJump, which outbids it).
        if (ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames))
            return false;
        return !ctx.Input.Down;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;

        // Re-grab (path C) keeps the corner the pull was working on — the checkers
        // can't see it from the risen pose. A dropdown chain (path D) is the same
        // shape: corner came through abilities from Dropdown.Exit. Fresh grabs read
        // the corner from the checkers. Every entry KEEPS the body's velocity: a held
        // Up lets a rising grab carry it into the pull, and a tap zeroes it the frame
        // Up comes up (both in Update); other entries' velocity the hang
        // spring/damper dissipates through the hand contact.
        bool regrab = ctx.PreviousState(0) is LedgePullState;
        bool chain  = ctx.PreviousState(0) is DropdownState && abilities.DropChainDir == _wallDir;
        abilities.DropChainDir = 0;   // offer consumed (or moot) either way
        if (!regrab && !chain)
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

        // A tap resolves: the grabbing press came up before LedgeAutoPullHoldTime
        // (held through the grab's every earlier frame, released now). The tap
        // is a plain grab, so it catches impulsively — velocity zeroed, the hang
        // spring takes it from wherever the coast left it.
        if (!ctx.Input.Up && UpHeldSinceEntry(ctx, vars, from: 1))
            ctx.Body.Velocity = Vector2.Zero;

        var force = Vector2.Zero;
        force.X = SpringDampForce(ctx.Body.Position.X - hangX, ctx.Body.Velocity.X, cfg, ctx.Dt);
        // Hold-to-climb, undecided: while the press that made the grab is still
        // held (a tap or a climb — LedgePullState decides at LedgeAutoPullHoldTime),
        // a rising body is not caught. It coasts under gravity alone, so the
        // momentum it grabbed with is still there when the pull takes over. A
        // release (a tap) or the top of the rise hands it back to the hang spring.
        bool coastRise = ctx.Body.Velocity.Y < 0f && UpHeldSinceEntry(ctx, vars);
        force.Y = coastRise ? 0f
                : -cfg.GrabGravityCancel
                + SpringDampForce(ctx.Body.Position.Y - hangY, ctx.Body.Velocity.Y, cfg, ctx.Dt);
        ctx.Body.AppliedForce = force;

        // Ambient Off: body is pinned to the corner — an ambient redirect would fight
        // the hang contacts. Still called so cross-frame anchor state is cleared.
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
    }

    // Up held on every frame of this grab, the entry frame included — the grabbing
    // press is still down. `from` skips the newest frames (1: every frame before this
    // one). Frames in state come from TimeInState, which Update advances by dt before
    // reading this; the Controller keeps 32 frames, past any decision.
    private static bool UpHeldSinceEntry(EnvironmentContext ctx, in MovementVars vars, int from = 0)
    {
        if (ctx.Dt <= 0f) return false;
        int frames = Math.Min((int)MathF.Round(vars.TimeInState / ctx.Dt), 31);
        if (frames <= from) return false;
        for (int i = from; i < frames; i++)
            if (!ctx.Controller.GetPrevious(i).Up) return false;
        return true;
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

// Executes the pull-up from a ledge grab. Activated by pressing Up while grabbed,
// by holding the press that made the grab (hold-to-climb, LedgeAutoPullHoldTime),
// or by pressing Up from anywhere above the hang the pull would still carry the
// body through (FromAbove).
// Releasing Up during the pull interrupts it. The pull FOLLOWS the authored clip
// as a geometric path (PathTracker — the same bead/band/progress solve the
// lattice walk uses): the retargeted clip is the shape, never a schedule, and
// the only actuator is the hands on the corner (CorrectorChannels.BuildGrip),
// capped and pace-faded. Where along the arc the body is comes from its
// position, so a body that arrives rising keeps its momentum through the pull.
// The bespoke fallback (UseReferenceClips=false) has no authored arc and keeps
// its plain spring/ramp shape, uncorrected.
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

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (abilities.IsLedgeGrabbing)
        {
            if (abilities.GrabWallDir != _wallDir) return false;
            // Fresh press from the hang: tap-to-grab, tap-again-to-pull.
            if (abilities.UpJustPressed) return true;
            return HeldThroughGrab(ctx);
        }
        return FromAbove(ctx);
    }

    // Entry without a hang: the body is already where the pull would carry it —
    // airborne beside a lip, above the hang line, not yet past the corner
    // (ExposedUpperCornerChecker.TryFindPullable's band) — and Up was just pressed
    // (the grab's buffered press). Airborne only: every point of a pull is, and a
    // grounded body at a step is the climb family's (Mantle/ArcJump), as is a
    // maneuver already under way — the pull never steals one.
    private bool FromAbove(EnvironmentContext ctx)
        => ctx.PreviousState(0) is not ClimbManeuverBase
           && !ctx.TryGetGround(out _)
           && LedgeGrabState.RecentUpPress(ctx)
           && ExposedUpperCornerChecker.TryFindPullable(ctx.Body, ctx.Chunks, _wallDir, out _);

    // Hold-to-climb: the same Up press that caught the ledge, held past
    // LedgeAutoPullHoldTime, rolls straight into the pull — a tap still just hangs.
    // One-shot per press: a pull anywhere inside the current hold (e.g. a timed-out
    // pull that re-grabbed via path C) disarms it, so a held Up can't loop pulls.
    // A hold longer than the input history can't be attributed to a grab and never fires.
    private static bool HeldThroughGrab(EnvironmentContext ctx)
    {
        float holdTime = MovementConfig.Current.LedgeAutoPullHoldTime;
        if (holdTime <= 0f || ctx.Dt <= 0f || !ctx.Input.Up) return false;
        const int History = 31;   // Controller / state-history depth (32) minus one
        for (int i = 1; i < History; i++)
        {
            if (ctx.PreviousState(i - 1) is LedgePullState) return false;
            if (!ctx.Controller.GetPrevious(i).Up)
                return i * ctx.Dt >= holdTime;   // i frames held, counting this one
        }
        return false;
    }

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
        // press means "jump up onto the ledge" — suppress WallJump so LedgeJumpState
        // launches off the corner instead (row K). Use the candidate's own wall side,
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
        if (vars.TimeInState >= MovementConfig.Current.MaxLipManeuverTime) return false;

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
        // From above (no hang first): grip the lip the probe found, as a grab would.
        if (!abilities.IsLedgeGrabbing
            && ExposedUpperCornerChecker.TryFindPullable(ctx.Body, ctx.Chunks, _wallDir, out var lip))
        {
            abilities.GrabbedCorner   = lip.InnerEdge;
            abilities.HasDoubleJumped = false;
            abilities.Facing          = _wallDir;
        }
        var clip = MovementConfig.Current.UseReferenceClips
            ? ReferenceClipRegistry.Get(ReferenceClipRegistry.LedgePull) : null;
        vars.RefActive = clip != null;
        if (vars.RefActive)
        {
            // Retarget at Enter (BALLISTIC_CORRECTOR_PLAN §1): the clip's Entry anchor
            // = the hang pose the body holds; its Gate anchor = standing on top just past the corner,
            // placed a few px beyond the completion test's thresholds so the path
            // carries the body through them rather than ending on them.
            var corner = abilities.GrabbedCorner;
            // Anchored at the hang height or below: a body already risen above it
            // (a rising grab carried by hold-to-climb) joins the arc partway up
            // rather than retargeting the clip to start above its own gate.
            vars.RefEntry    = new Vector2(ctx.Body.Position.X,
                                           MathF.Max(ctx.Body.Position.Y, corner.Y + PlayerCharacter.Radius));
            vars.RefGate     = new Vector2(corner.X + _wallDir * (PlayerCharacter.Radius + 2f),
                                           corner.Y - (2f * PlayerCharacter.Radius + 4f));
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
        // normally consumes it the same frame; the keep-alive covers a frame it
        // can't bid (the corner briefly out of grip reach). It stops when the pull
        // ends, so an unconsumed press expires on its normal window.
        ctx.Intents.Refresh(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);

        if (vars.RefActive)
        {
            var clip = ReferenceClipRegistry.Get(ReferenceClipRegistry.LedgePull);
            var s = ctx.Corrector;
            if (clip != null && s != null && ctx.Dt > 0f)
            {
                // A mid-pull hot-reload flip can leave bespoke contacts attached —
                // the grip replaces them, so drop any strays.
                if (_spring != null) { ctx.Body.Constraints.Remove(_spring); _spring = null; }
                if (_ramp   != null) { ctx.Body.Constraints.Remove(_ramp);   _ramp   = null; }
                // The arc retargeted at Enter, as a path from the body onward.
                // Its pace: the authored duration over the arc's length — the
                // speed the arms pull to, not a clock the body is held to.
                int nv = PathTracker.BuildClipPolyline(s, ctx.Body.Position, clip,
                    new ReferenceFrame(clip, vars.RefEntry, vars.RefGate), out float arcLength);
                float pace = arcLength / MathF.Max(MovementConfig.Current.LedgePullRefDuration, 1e-4f);
                var grip = new GripPathPolicy(abilities.GrabbedCorner, _wallDir, pace);
                ctx.Body.AppliedForce = Vector2.Zero;   // the hands are the only actuator
                PathTracker.FollowPolyline(ctx, s, nv, ref grip);
                // Ambient Off: the grip solve is this state's corrector — no
                // ambient redirect on top. Still called so cross-frame anchor
                // state is cleared.
                ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
                return;
            }
            vars.RefActive = false;   // no clip (dev-only) or no scratch: fall through to bespoke
        }

        EnsureContacts(ctx, abilities);
        float cornerTopY = _spring.Position.Y;
        var   cfg        = MovementConfig.Current;
        var   force      = Vector2.Zero;

        if (ctx.Body.Position.Y >= cornerTopY - 2f * PlayerCharacter.Radius)
        {
            force.Y = -cfg.LipLiftForce;
        }
        else
        {
            if (_ramp != null) { ctx.Body.Constraints.Remove(_ramp); _ramp = null; }
            if (ctx.Body.Velocity.Y < 0f && ctx.Dt > 0f)
                force.Y = Math.Min(-ctx.Body.Velocity.Y / ctx.Dt, 2f * cfg.LipLiftForce);
            force.X = _wallDir * cfg.LipPushForce;
        }

        ctx.Body.AppliedForce = force;
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
    }
}

// Jump off a gripped corner — from the hang (LedgeGrab) or from any point of a
// ledge pull — on an inward or neutral jump press (away + Space is WallJump's,
// which outbids this). The corner is the launch surface: a JumpServo drives vy
// toward LedgeJumpTargetVy relative to the (static) ledge for as long as the
// legs can still push off it, i.e. until they are fully extended above the
// corner (body center at standing height over its top). That push is committed —
// the jump is that powerful from a dead hang. Past it the state is an ordinary
// jump hold: the servo keeps powering while Space is held, up to MaxJumpHoldTime
// — which is all a launch from the top of a pull, already at standing height,
// gets. Two-sided servo: a pull's surplus vy is braked to a normal jump, never
// stacked onto one (Plans/LEDGE_PULL_INPUT_MATRIX.md rows I, J′, K).
public class LedgeJumpState : MovementState
{
    private readonly int _wallDir;

    public LedgeJumpState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => MovementPriorities.LedgeJumpActive;
    public override int PassivePriority => MovementPriorities.LedgeJumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;
    public override AnimTag AnimationTag => AnimTag.LedgeJump;

    // Legs fully extended below the body center: a standing body's floor line.
    private static float LegReach => PlayerCharacter.StandingHeight - PlayerCharacter.Radius;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        bool gripping = ctx.PreviousState(0) switch
        {
            LedgeGrabState grab => grab.WallDir == _wallDir,
            LedgePullState pull => pull.WallDir == _wallDir,
            _ => false,
        };
        if (!gripping) return false;
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        return CorrectorChannels.InGripReach(ctx.Body.Position, abilities.GrabbedCorner);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        // Push phase: the legs still reach the corner. Liveness-capped like the
        // other lip maneuvers (a ceiling over the lip can stall it).
        bool legsOnCorner = ctx.Body.Position.Y + LegReach > abilities.GrabbedCorner.Y;
        if (legsOnCorner) return vars.TimeInState < cfg.MaxLipManeuverTime;
        // Then an ordinary jump hold.
        return !vars.JumpReleased && vars.TimeInState < cfg.MaxJumpHoldTime;
    }

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

        // Ambient Off: the launch off the corner is a committed arc past the very
        // corner an ambient ramp would try to steer around — keep the layer out for
        // the launch frames. Still called so cross-frame anchor state is cleared.
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
    }
}
