using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// MVP movement-FSM states. Tactical decisions (when to chase, when to jump,
// where to face) live in EnemyController. These states only execute the
// controller's intent — read ctx.Input.MoveX / .Jump / .AimWorld rather than
// poking at ctx.Player or ctx.Dist. The only world-state they touch is their
// own body (Velocity, position) and cross-FSM signals (IsActionCommitted) —
// nothing that would change if a different brain were attached.
//
// See Plans/ENEMY_CAPABILITY_FRAMEWORK.md §2.

// Always-on fallback. Brakes horizontal velocity so the body settles in place
// when no chase intent is active. Index 0 in EnemyEntity's movement registry
// by convention.
public class EnemyIdleState : EnemyMovementState
{
    public override int ActivePriority  => 5;
    public override int PassivePriority => 0;

    public override bool CheckPreConditions(in EnemyContext ctx) => true;
    public override bool CheckConditions  (in EnemyContext ctx, ref EnemyMovementVars v) => true;

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        ctx.Self.Body.Velocity.X *= 0.8f;
    }
}

// Ground locomotion. Active only when the controller asks for horizontal
// movement and no action is mid-commit. Speed is the per-subclass execution
// knob; engagement range and chase logic live on the controller.
public class EnemyChaseState : EnemyMovementState
{
    protected virtual float Speed => 70f;
    // Below this absolute X component, we treat the brain's intent as "no
    // horizontal motion" — keeps a clinger that emits a near-vertical MoveDir
    // from accidentally walking.
    protected virtual float MoveDeadzone => 0.1f;

    public override int ActivePriority  => 20;
    public override int PassivePriority => 15;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > MoveDeadzone;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > MoveDeadzone;

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        ctx.Self.Body.Velocity.X = MathF.Sign(ctx.Input.MoveDir.X) * Speed;
    }
}

// Root the body while an action is committed — so the windup / strike /
// recovery reads as a planted swing rather than a moving slide. Highest
// priority of the MVP movement states (except Stagger) so it preempts Idle
// and Chase when an attack is firing.
//
// Action.Update runs AFTER movement.Update in EnemyEntity, so an action that
// wants to MOVE during its active window (e.g. EnemyLungeAction) writes
// Body.Velocity AFTER this state has decelerated — the lunge wins by virtue
// of running last, exactly like the player's ApplyActionForces hook.
public class EnemyAttackHoldState : EnemyMovementState
{
    public override int ActivePriority  => 40;
    public override int PassivePriority => 35;

    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Self.IsActionCommitted;
    public override bool CheckConditions  (in EnemyContext ctx, ref EnemyMovementVars v) => ctx.Self.IsActionCommitted;

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        ctx.Self.Body.Velocity.X *= 0.6f;
    }
}

// Interrupt state entered via EnemyEntity.OnHit. Doesn't touch velocity — lets
// the knockback impulse the hit applied (Entity.OnHit adds impulse / Mass to
// Velocity) actually play out before chase/jump/etc. clobber it on the next
// frame. Exits once the body has slowed back to chase-speed-ish AND a minimum
// duration has elapsed, mirroring StalkerEnemy.AIState.Stagger.
//
// CheckPreConditions returns false so the precondition scan never picks
// stagger on its own; entry is force-driven by EnemyEntity.OnHit. ActivePriority
// is the highest so an in-flight chase/attack-hold/jump all lose to it.
public class EnemyStaggerState : EnemyMovementState
{
    protected virtual float MinDuration => 0.30f;
    protected virtual float ResumeSpeed => 90f;

    public override int ActivePriority  => 50;
    public override int PassivePriority => 45;

    public override bool CheckPreConditions(in EnemyContext ctx) => false;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        if (v.TimeInState < MinDuration) return true;
        return ctx.Self.Body.Velocity.LengthSquared() > ResumeSpeed * ResumeSpeed;
    }

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        // Deliberately empty — knockback velocity should bleed off via friction
        // and gravity, not be overwritten.
    }
}

// Vertical pursuit. Triggers when the controller signals Jump intent AND the
// body is approximately at rest vertically (a physics feasibility check —
// |Velocity.Y| stays small while standing on the floor and grows mid-air).
// "Is the player high enough to be worth jumping at?" is a tactical decision
// the controller already made when it set Input.Jump.
//
// On Enter, writes a one-shot upward impulse to Body.Velocity.Y; the state
// then drifts the body horizontally toward Facing (set by the controller's
// AimWorld) while gravity completes the arc. CheckConditions exits after a
// fixed max-air-time, after which Chase or Idle takes over while the body
// falls. The natural cool-down comes from the precondition: re-triggering
// requires the body to land (|Vy| dropping back below GroundedVyMax), which
// gives a roughly one-arc cadence.
public class EnemyJumpState : EnemyMovementState
{
    protected virtual float JumpImpulse     => -260f;   // Y-up is negative in this engine
    protected virtual float HorizontalDrift => 70f;
    protected virtual float MaxAirTime      => 0.9f;
    protected virtual float GroundedVyMax   => 30f;

    // Sits above Chase (20) so a triggered jump pre-empts walking, below
    // AttackHold (40) so a committed attack still roots — chasing the player
    // vertically while swinging would feel chaotic.
    public override int ActivePriority  => 28;
    public override int PassivePriority => 25;

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (!ctx.Input.Jump) return false;
        if (ctx.Self.IsActionCommitted) return false;
        if (MathF.Abs(ctx.Self.Body.Velocity.Y) > GroundedVyMax) return false; // mid-air → can't jump again
        return true;
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        if (ctx.Self.IsActionCommitted) return false;
        return v.TimeInState < MaxAirTime;
    }

    public override void Enter(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        // One-shot upward velocity, just like the player's JumpingState does
        // through MovementConfig.JumpVelocity. Adding rather than assigning so
        // a body that's already drifting upward (e.g. on a moving platform)
        // doesn't get its lift truncated.
        ctx.Self.Body.Velocity.Y += JumpImpulse;
    }

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        // Drift horizontally toward Facing — derived from the controller's
        // AimWorld, so a brain that wants the body to glide away mid-jump can
        // simply aim somewhere else.
        ctx.Self.Body.Velocity.X = ctx.Facing * HorizontalDrift;
    }
}

// Surface-anchored locomotion ("crampon feet"). While active, gravity is
// effectively off and the body moves along whichever direction keeps it
// within AnchorDist of a solid tile and best matches Input.MoveDir.
//
// Algorithm (per Update):
//   1. Sample N directions evenly around the body.
//   2. For each candidate, check that moving Speed * dt along it lands the
//      body within AnchorDist of a solid tile (the anchor invariant).
//   3. Among anchored candidates, pick the one with the highest dot product
//      with the requested direction.
//   4. If that best dot product fails to clear DotThreshold, hold still —
//      this is the "request is essentially unsatisfiable" case (e.g. asked
//      to move up while standing on flat ground).
//
// Enter/Exit toggle the entity's GravityScale; the original value is saved
// into EnemyMovementVars so Exit restores it. Snapshot round-trip handles
// gravity correctly because Entity.WriteState/ReadState already capture
// GravityScale — a snapshot taken mid-cling restores a 0 scale alongside
// the state index.
//
// Not in the priority pyramid by default — enemies opt into clinging by
// adding this state to their movement list (analogous to how only some
// enemies opt into EnemyJumpState). Priority sits above Chase/Jump so a
// clinger that's also given those states prefers crawling along the surface
// when anchored.
public class EnemyClingMoveState : EnemyMovementState
{
    // Moderate pace. Slightly below Chase (70) so climbing reads as deliberate.
    protected virtual float Speed         => 60f;
    // Max distance from the body center to the nearest solid tile point to
    // count as "anchored." Body radii here run ~10–12 px; AnchorDist of 14
    // gives a couple px of slack outside the body before detachment.
    protected virtual float AnchorDist    => 14f;
    // Angular resolution of the direction-search and anchor-probe rings.
    // 16 samples → 22.5° apart → at AnchorDist=14 the arc gap is ~5.5 px,
    // well below the 16-px tile size, so no anchored tile slips between probes.
    protected virtual int   NumSamples    => 16;
    // Dot-product floor for selecting a move direction. Requested-up on flat
    // ground tops out at dot ≈ 0 against left/right candidates, so 0.25
    // rejects the "perpendicular bail-out" and the body holds still — what
    // the user asked for as the "essentially nowhere" case.
    protected virtual float DotThreshold  => 0.25f;

    // Above Chase (20) and Jump (28) so a clinger prefers crawling when
    // anchored, below AttackHold (40) so committed attacks still root, below
    // Stagger (50) so hits still interrupt.
    public override int ActivePriority  => 32;
    public override int PassivePriority => 26;

    // Anchored-ness is the ONLY gate, on entry and on exit alike. Neither check
    // consults IsActionCommitted, and that is load-bearing in both directions:
    //
    //   * On exit — Exit restores GravityScale, so a clinger that bailed to Idle
    //     for the duration of its own attack would peel off the ceiling the
    //     moment it started swinging. Instead it holds the latch and Update
    //     freezes the body for the swing (below), which is what
    //     EnemyAttackHoldState would have done anyway.
    //   * On entry — a crawler that first sees the player from inside its own
    //     attack range commits the action on the same frame it would otherwise
    //     have latched. Gating entry on "no attack in flight" meant it never
    //     latched at all: it fell, attacking, all the way to the floor.
    //
    // Clinging is a relationship with a surface, not a locomotion choice, so it
    // simply is not the action FSM's business. Corollary: don't put
    // EnemyAttackHoldState in a clinger's movement list — at priority 40 it
    // would preempt this state and un-latch it through Exit.
    public override bool CheckPreConditions(in EnemyContext ctx)
        => IsAnchored(ctx.Self.Body.Position, ctx);

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => IsAnchored(ctx.Self.Body.Position, ctx);

    public override void Enter(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        // Save and zero gravity. Saved in vars (not on the flyweight) so a
        // snapshot taken mid-state restores the right value on Exit, and so
        // two entities sharing this state instance don't clobber each other.
        v.SavedGravityScale  = ctx.Self.GravityScale;
        ctx.Self.GravityScale = 0f;
        // Cancel any residual fall velocity so the first frame's anchor-search
        // isn't biased by gravity built up before entering.
        ctx.Self.Body.Velocity = Vector2.Zero;
    }

    public override void Exit(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        ctx.Self.GravityScale = v.SavedGravityScale;
    }

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;

        // Planted swing: an attack roots the body without giving up the latch.
        if (ctx.Self.IsActionCommitted)
        {
            ctx.Self.Body.Velocity = Vector2.Zero;
            return;
        }

        var request = ctx.Input.MoveDir;
        if (request.LengthSquared() < 1e-4f)
        {
            // No request → no motion. Holding zero velocity (rather than
            // letting friction decay it) keeps the cling perfectly stationary,
            // which is the visual signature of a crampon hold.
            ctx.Self.Body.Velocity = Vector2.Zero;
            return;
        }

        var reqN = request;
        reqN.Normalize();

        Vector2 best = Vector2.Zero;
        float   bestDot = DotThreshold;
        var pos = ctx.Self.Body.Position;
        float moveLen = Speed * ctx.Dt;

        for (int i = 0; i < NumSamples; i++)
        {
            float a = i * MathHelper.TwoPi / NumSamples;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));

            // Reject directions that would un-anchor the body. Tested at the
            // landing position, not just the current one — this is what stops
            // the cling from walking off the edge of a tile cluster.
            var landing = pos + dir * moveLen;
            if (!IsAnchored(landing, ctx)) continue;

            float dot = Vector2.Dot(reqN, dir);
            if (dot > bestDot)
            {
                bestDot = dot;
                best    = dir;
            }
        }

        // Write velocity directly. PhysicsWorld still resolves collisions, so
        // if a candidate direction would clip into geometry the sweep halts;
        // the anchor invariant means we'll be near a wall when this happens,
        // which is fine.
        ctx.Self.Body.Velocity = best * Speed;
    }

    // True iff any of NumSamples ring-probes at AnchorDist from `pos` lands
    // inside a solid tile. Cheap and direction-agnostic — same probe ring as
    // the move-selection loop above so a direction that's flagged anchored
    // by the search is consistent with the precondition check.
    private bool IsAnchored(Vector2 pos, in EnemyContext ctx)
        => SurfaceProbe.IsAnchored(pos, ctx.Spawner.Chunks, AnchorDist, NumSamples);
}

// Directional ballistic leap. The brain commits a launch velocity via
// Input.JumpVelocity; this state applies the vector once on Enter and then
// lets gravity + collision draw the arc. No mid-air control — that's the
// point. Pair with EnemyClingMoveState to hop between pillars: cling holds
// the body, brain emits JumpVelocity aimed at the next surface, leap runs the
// arc, cling re-engages when the body lands within AnchorDist of a tile.
//
// Distinct from EnemyJumpState, which exists for the "lift + horizontal
// drift" pattern (continuous Vx control mid-air). Leap is pure ballistic,
// trusting the brain to have already chosen a trajectory that lands somewhere
// useful.
public class EnemyLeapState : EnemyMovementState
{
    // Grace window after takeoff during which the state ignores anchored-ness.
    // Without this, the first frame after Enter has the body still ~moveLen
    // from the launch surface — within AnchorDist — so re-anchor would fire
    // immediately and we'd never actually leave. 0.15s clears AnchorDist for
    // typical jump magnitudes (≥ 100 px/s).
    protected virtual float MinAirTime  => 0.15f;
    // Hard cutoff so a leap that never lands (e.g. flung off the map) doesn't
    // pin the FSM here forever. Idle takes over after that.
    protected virtual float MaxAirTime  => 1.5f;
    protected virtual float AnchorDist  => 14f;
    protected virtual int   NumSamples  => 16;

    // Above Cling (32/26) so an anchored clinger can launch via leap. Below
    // AttackHold (40) and Stagger (50) so attacks and hitstun still preempt.
    public override int ActivePriority  => 36;
    public override int PassivePriority => 30;

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (ctx.Self.IsActionCommitted) return false;
        if (ctx.Input.JumpVelocity.LengthSquared() < 1e-4f) return false;
        // Must be touching SOME surface to push off. Same anchor probe Cling
        // uses, so a clinger on a wall qualifies just as well as a grounded
        // brute.
        return SurfaceProbe.IsAnchored(ctx.Self.Body.Position, ctx.Spawner.Chunks, AnchorDist, NumSamples);
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        if (ctx.Self.IsActionCommitted) return false;
        if (v.TimeInState < MinAirTime) return true;
        if (v.TimeInState >= MaxAirTime) return false;
        // After the grace window, exit as soon as the body is near a surface
        // again — Cling (if in the movement list) or Idle/Chase picks up the
        // landing on the next frame.
        return !SurfaceProbe.IsAnchored(ctx.Self.Body.Position, ctx.Spawner.Chunks, AnchorDist, NumSamples);
    }

    public override void Enter(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        // Replace, not add — the brain has full info and committed to this
        // trajectory. Adding would coupling with whatever velocity the prior
        // state happened to be writing (Cling crawling, Chase walking, …).
        ctx.Self.Body.Velocity = ctx.Input.JumpVelocity;
    }

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        // Deliberately no velocity writes — pure ballistic. If you want mid-air
        // steering, use EnemyJumpState; if you want a different trajectory,
        // emit a different JumpVelocity next leap.
    }
}

// Self-propelled hover. The brain emits a 2D MoveDir (zero = hold position);
// the state applies a velocity correction each frame that aims for the
// implied target velocity AFTER the physics-step gravity addition. The
// per-frame correction is capped at MaxAcceleration / Mass × dt, so:
//
//   * Light entity (Mass ≈ 1):    correction budget ≫ gravity step → hovers cleanly.
//   * Marginal entity (Mass ≈ 1.6 with default MaxAcceleration): budget ≈ gravity step → sloppy hover, drifts.
//   * Heavy entity (Mass ≫ 1.5):  budget < gravity step → can't even null out the fall → plummets.
//
// That's the "too heavy to fly" mechanism the user asked for — Mass acts as
// inertia against the fixed thrust, the same way it acts against knockback
// impulse in Entity.OnHit. No special-case rejection: the state engages and
// flails uselessly when overburdened, which is the readable failure mode.
//
// Gravity stays ON (unlike EnemyClingMoveState, which zeros GravityScale) —
// otherwise heaviness wouldn't matter. Anticipating the gravity step in the
// velocity calculation is what makes a successful hover read as "perfect
// stop" rather than "oscillates around equilibrium."
public class EnemyFlyState : EnemyMovementState
{
    // Maximum acceleration the propulsion system can apply (px/s²) BEFORE
    // mass-scaling. The actual per-frame velocity change is this × dt × (1/Mass).
    // 900 / 1.0 × dt(1/30) ≈ 30 px/s; gravity adds 600 × 1.0 × dt ≈ 20 px/s
    // per frame — so Mass=1 hovers with margin, Mass≈1.5 is marginal, Mass≥2
    // can't keep up.
    protected virtual float MaxAcceleration => 900f;

    // Target speed when MoveDir is nonzero. Zero MoveDir means "hover here"
    // (target velocity = zero → state spends its budget cancelling gravity).
    protected virtual float CruiseSpeed     => 80f;

    // Sits above Chase (20) and Jump (28) so a flying enemy doesn't fall back
    // into walking when MoveDir.X happens to be set. Same band as Cling (32/26)
    // — Fly and Cling are alternative locomotion modes; an enemy registers
    // one or the other, not both.
    public override int ActivePriority  => 30;
    public override int PassivePriority => 26;

    public override bool CheckPreConditions(in EnemyContext ctx) => !ctx.Self.IsActionCommitted;
    public override bool CheckConditions  (in EnemyContext ctx, ref EnemyMovementVars v) => !ctx.Self.IsActionCommitted;

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;

        var   body = ctx.Self.Body;
        float dt   = ctx.Dt;
        // MathF.Max guards a misconfigured Mass=0 entity — would otherwise be
        // infinite budget (effectively magical anti-gravity) and divide-by-zero
        // when computing the delta-cap.
        float mass = MathF.Max(ctx.Self.Mass, 0.01f);

        // Per-frame velocity-change envelope. The (1/Mass) factor is the only
        // place Entity.Mass enters this state — match the player-knockback
        // semantics (heavier = harder to push around).
        float budget = MaxAcceleration / mass * dt;

        // Desired post-step velocity: cruise toward MoveDir, or zero to hover.
        Vector2 desiredV = Vector2.Zero;
        var move = ctx.Input.MoveDir;
        if (move.LengthSquared() > 1e-4f)
        {
            move.Normalize();
            desiredV = move * CruiseSpeed;
        }

        // PhysicsWorld will add gravStep to Velocity AFTER this Update returns.
        // To land at desiredV after gravity, we must set Velocity NOW such
        // that adding gravStep produces desiredV — i.e. targetPre = desiredV
        // − gravStep. The state then closes that gap by `delta`, capped at
        // `budget`. If the cap clips delta, we get as close as we can; the
        // residual is what gravity wins each frame for an overloaded entity.
        Vector2 gravStep  = new(0f, Simulation.WorldGravityY * ctx.Self.GravityScale * dt);
        Vector2 targetPre = desiredV - gravStep;
        Vector2 delta     = targetPre - body.Velocity;
        if (delta.LengthSquared() > budget * budget)
            delta *= budget / delta.Length();
        body.Velocity += delta;
    }
}

// Ballistic hop cycle — crouch, launch, ride the arc, land, repeat. One state
// covers the whole loop (rather than a crouch state + a leap state) because the
// cycle's phase is exactly "how long have I been in this state", and
// EnemyMovementVars.TimeInState is the only movement field EnemyEntity
// snapshots. Splitting the phases across states would have been equivalent;
// keeping them together means the launch impulse and the tell that precedes it
// can never desynchronise.
//
// Distinct from EnemyLeapState, which executes a trajectory the *brain* chose
// (Input.JumpVelocity) and therefore needs a brain that can do ballistics. Hop
// solves its own arc from Input.AimWorld, so it pairs with any aim-at-the-player
// controller — including the stock ones.
//
// Phase timeline (t = TimeInState):
//   [0, CrouchTime)                  compress + telegraph, horizontal brake
//   the frame t crosses CrouchTime   one-shot launch velocity, then hands off
//   (CrouchTime, +MinAirTime)        grace window; anchor probe ignored
//   … until the anchor probe fires   ballistic, zero control (that's the point)
//
// The landing (anchor probe true) drops the state; EnemyEntity falls back to
// index 0, whose CheckPreConditions re-selects Hop on the very next frame. That
// one-frame bounce through Idle is deliberate and deterministic — it's what
// resets TimeInState so the next crouch telegraph starts from zero.
public class EnemyHopState : EnemyMovementState
{
    // Wind-up before takeoff. This is the whole tell for the pounce that
    // follows, so it wants to be long enough to read and short enough that a
    // cornered player still feels pressured.
    protected virtual float CrouchTime      => 0.40f;
    // Target time-of-flight for the ballistic solve. Longer = floatier, higher
    // arcs and more time to dodge; shorter = flatter, faster, meaner.
    protected virtual float FlightTime      => 0.62f;
    // Horizontal reach cap. Keeps "hop toward the player" reading as a series
    // of discrete tile-to-tile bounds instead of one map-crossing pounce, and
    // stops the solve from demanding absurd launch speeds at long range.
    protected virtual float MaxHopRun       => 110f;
    protected virtual float MaxHopRise      => 90f;    // Y-up ⇒ how far ABOVE us we'll aim
    protected virtual float MaxHopDrop      => 160f;   // …and how far below
    // Floor on the upward component so a hop at a target that's level-or-below
    // still visibly leaves the ground rather than sliding along it.
    protected virtual float MinRise         => 200f;
    protected virtual float MaxLaunchSpeed  => 520f;
    // Grace window after takeoff during which the anchor probe is ignored —
    // same rationale as EnemyLeapState.MinAirTime: the body is still within
    // AnchorDist of the surface it just pushed off.
    protected virtual float MinAirTime      => 0.14f;
    // Hard cutoff for a hop that never lands (flung off the map, launched into
    // open sky). Idle takes over and the body just falls.
    protected virtual float MaxAirTime      => 2.0f;
    protected virtual float AnchorDist      => 14f;
    protected virtual int   NumSamples      => 16;
    protected virtual Color TelegraphColor  => new(255, 170, 60);

    // Above Chase (20) and Jump (28) — an enemy given this state hops instead of
    // walking. Below Stagger (50) so a hit still interrupts mid-crouch. NOT
    // below AttackHold (40) on purpose: see the class list note in
    // EnemyFactory — a hopper must not register AttackHold, or the braking
    // would kill the very momentum its attack is built on.
    public override int ActivePriority  => 34;
    public override int PassivePriority => 30;

    // Grounded (on any surface, including a wall or ceiling) and not still
    // recovering from an attack. The action-commit gate is what paces the
    // cycle: it inserts the slam's recovery between successive hops instead of
    // letting the pouncer chain bounds with no breathing room.
    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted
           && SurfaceProbe.IsAnchored(ctx.Self.Body.Position, ctx.Spawner?.Chunks, AnchorDist, NumSamples);

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        float t = v.TimeInState;
        if (t < CrouchTime)              return true;   // winding up
        if (t < CrouchTime + MinAirTime) return true;   // just launched
        if (t >= CrouchTime + MaxAirTime) return false; // never landed — bail out
        return !SurfaceProbe.IsAnchored(ctx.Self.Body.Position, ctx.Spawner?.Chunks, AnchorDist, NumSamples);
    }

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        float prev = v.TimeInState;
        v.TimeInState += ctx.Dt;

        if (v.TimeInState < CrouchTime)
        {
            // Plant the feet. Braking rather than zeroing so a hop that landed
            // with residual slide settles over a few frames instead of snapping.
            ctx.Self.Body.Velocity.X *= 0.72f;
            return;
        }

        // Exactly one frame per cycle satisfies this at a fixed timestep, which
        // is what lets the launch be a pure function of TimeInState — no "have I
        // fired yet" flag to snapshot (same trick EnemyRangedAction uses).
        if (prev < CrouchTime) Launch(in ctx);

        // Airborne: deliberately no velocity writes. Gravity and the collision
        // solver own the arc from here.
    }

    // Solve for the launch velocity that lands the body at the brain's aim point
    // in FlightTime seconds, under this entity's own gravity:
    //
    //     p(T) = p0 + v·T + ½·g·T²   ⇒   v = Δ/T − ½·g·T
    //
    // The −½gT term is what supplies the upward pop; with Y-down gravity it is
    // automatically negative (upward), so even a perfectly level target
    // produces a real arc rather than a horizontal skid.
    private void Launch(in EnemyContext ctx)
    {
        var body = ctx.Self.Body;
        var delta = ctx.Input.AimWorld - body.Position;

        // Clamp the target BEFORE solving. Clamping the resulting velocity
        // instead would keep the arc's shape but wreck its landing point; this
        // way an out-of-reach player simply gets approached one bound at a time.
        if (MathF.Abs(delta.X) > MaxHopRun) delta.X = MathF.Sign(delta.X) * MaxHopRun;
        delta.Y = MathHelper.Clamp(delta.Y, -MaxHopRise, MaxHopDrop);

        float g = Simulation.WorldGravityY * ctx.Self.GravityScale;
        float T = MathF.Max(FlightTime, 1e-3f);

        var launch = new Vector2(delta.X / T, delta.Y / T - 0.5f * g * T);
        if (launch.Y > -MinRise) launch.Y = -MinRise;

        float speed = launch.Length();
        if (speed > MaxLaunchSpeed) launch *= MaxLaunchSpeed / speed;

        // Replace rather than add: the crouch already killed the inherited
        // velocity, and the solve assumes it starts from this exact vector.
        body.Velocity = launch;
    }

    // Crouch tell only — nothing is drawn once the body is committed to the arc,
    // because at that point the arc IS the telegraph. A compression bar squashes
    // under the body while a ring tightens around it; both peak right as the
    // launch fires, so the player's cue to move is unambiguous.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyMovementVars v)
    {
        float t = v.TimeInState;
        if (t >= CrouchTime || CrouchTime <= 0f) return;

        float p = t / CrouchTime;                  // 0 → 1 across the crouch
        var   c = body.Position;

        // Compression bar: widens and brightens as the legs load up.
        int w = 8 + (int)(p * 16f);
        sb.Draw(pixel, new Rectangle((int)c.X - w / 2, (int)c.Y + 8, w, 2),
                Color.Lerp(new Color(TelegraphColor, 90), TelegraphColor, p));

        // Tightening ring — radius collapses 16 → 5 px, the visual inverse of
        // the launch that follows.
        float r = 16f - p * 11f;
        for (int i = 0; i < 10; i++)
        {
            float a  = i * MathHelper.TwoPi / 10f;
            var  pos = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            sb.Draw(pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2), TelegraphColor * (0.35f + 0.65f * p));
        }

        // Final quarter: a white flash-up so the exact takeoff frame is readable.
        if (p > 0.75f)
        {
            float fp = (p - 0.75f) / 0.25f;
            int   sz = 3 + (int)(fp * 4f);
            sb.Draw(pixel, new Rectangle((int)c.X - sz / 2, (int)c.Y - sz / 2, sz, sz), Color.White * fp);
        }
    }
}
