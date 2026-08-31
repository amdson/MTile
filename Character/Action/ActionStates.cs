using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Concurrent second FSM, owned by PlayerCharacter alongside the MovementState FSM.
// Same shape (preconditions, conditions, Enter/Exit/Update, priority-based selection),
// separate registry, separate history.
//
// Coupling rule: actions may read movement (ctx.Body, ctx.TryGet*, ctx.PreviousState)
// but movement code MUST NOT read action state. Enforced by convention.
public abstract class ActionState
{
    public abstract int ActivePriority  { get; }
    public abstract int PassivePriority { get; }

    // Sentinel for CommitProfile: the action refuses voluntary eviction outright.
    // Escape hatch only — nothing returns it today; commitment is normally
    // expressed as a PRICE (a long quote), not a refusal, so user intent stays
    // expressible at any time. Priorities are the guard rail instead: eviction
    // bids with the future candidate's PassivePriority (see RecoveryAction), so
    // a request that couldn't preempt this action directly can't evict it either.
    public const int Blocked = -1;

    // Commitment envelope: the frames of mandatory recovery this action would hand
    // RecoveryAction if evicted at this instant. RecoveryAction's lookahead calls
    // this on the LIVE action to decide whether an early exit pays; an action whose
    // Exit stamps its own recovery should stamp from the same number so eviction
    // pays exactly what was quoted. Default 0: freely evictable (the Exit stamp,
    // if any, is still the actual price).
    public virtual int CommitProfile(EnvironmentContext ctx, in ActionVars vars) => 0;

    // Involuntary-eviction price knob — CommitProfile's twin (HIT_AIRLOCK_PLAN §4).
    // Strength threshold below which an incoming hit does NOT interrupt this
    // action: armored hits still cost their full HP and recoil the attacker, but
    // arrive at heavily scaled knockback and never register hitstun/stun — so no
    // flinch eviction and no disadvantage window. Compared against
    // HitResult.Strength (pre-mass, the same units as CombatState's stun
    // threshold; reference points: Slash1 ~100, Slash3 ~300, Stab ~650).
    // Default 0 = no armor: any hit flinches.
    public virtual float ArmorProfile(in ActionVars vars) => 0f;

    // Hub states (Null, the build holds) that entrants may fire out of directly,
    // as if from neutral: ctx.RecoveryIndex() reads through them to the recovery
    // countdown. Every other action is opaque — entering over a live action needs
    // an explicit chord in the entrant's precondition (GuardRetaliate←GuardCharged,
    // Burst←Recovery-charge, GrabbedSlash←grabbed) or the eviction lookahead.
    public virtual bool NeutralForEntry => false;

    // Standard entry gate for the strict transition graph: fire only from neutral
    // or recovery, and only once the countdown has reached maxEntryIndex frames.
    // 0 (the default) = fully recovered. The countdown includes hit-imposed
    // disadvantage (hitstun/stun — see EnvironmentContext.RecoveryIndex), so a
    // finite window like guard's 0.2s doubles as its stun-escape window.
    //
    // The UNBOUNDED window (int.MaxValue) is the combo-chaining privilege: it
    // spans only the player's own recovery stamp, never hit disadvantage —
    // being interrupted drops your combo. `ignoreHitDisadvantage` is the
    // struggle channel's exemption (GrabbedSlash).
    protected static bool EntryOk(EnvironmentContext ctx, int maxEntryIndex = 0,
                                  bool ignoreHitDisadvantage = false)
    {
        if (ctx.RecoveryIndex(includeHitDisadvantage: !ignoreHitDisadvantage) is not int i)
            return false;
        if (maxEntryIndex == int.MaxValue && !ignoreHitDisadvantage
            && ctx.HitDisadvantageFrames() > 0) return false;
        return i <= maxEntryIndex;
    }

    // CheckPreConditions (candidate selection) reads only ctx + abilities, never the
    // current activation's vars — so it keeps the lean signature. The lifecycle methods
    // below run on the active/transitioning action and carry ActionVars, the plain-data
    // per-activation state (see ActionVars). Read-only hooks take it by `in`.
    public abstract bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities);
    public abstract bool CheckConditions  (EnvironmentContext ctx, PlayerAbilityState abilities, ref ActionVars vars);

    public virtual void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref ActionVars vars) {}
    public virtual void Exit (EnvironmentContext ctx, PlayerAbilityState abilities, ref ActionVars vars) {}

    public abstract void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref ActionVars vars);

    // Declare this frame's world-space overlay shapes (charge dots, rings, cell
    // flashes) into the frame's TelegraphList. Render-only: Game1 calls it once per
    // rendered frame, Drawing/TelegraphRenderer draws the list. Never touches a
    // SpriteBatch — see Presentation/TelegraphList.cs.
    public virtual void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars) {}

    // Declare multiplicative scalars on movement knobs (walk speed, friction, …).
    // Called by PlayerCharacter once per frame between action selection and
    // Movement.Update — the values go into ctx.Modifiers, movement reads them
    // at its config sites. Default no-op = identity, no effect on physics.
    public virtual void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars) { }

    // Augment the player body's physics directly: add to AppliedForce for a
    // sustained push, or write Velocity for an impulse / "ensure-at-least" assist.
    // Called by PlayerCharacter AFTER Movement.Update has written its force for
    // the frame and BEFORE Action.Update — so action-driven forces stack on top
    // of, not in competition with, the movement-driven force. Default no-op.
    public virtual void ApplyActionForces(EnvironmentContext ctx, in ActionVars vars) { }

    // How far through this activation we are, normalized [0,1], for the animation layer ONLY
    // (render-only — never read by the sim). The overlay clip is remapped onto it, so the
    // authored pose sweeps once over the action regardless of how long the action actually
    // runs or how long the clip's own timeline is. This is the action-side mirror of
    // MovementState.AnimationProgress.
    //
    // **Return negative to decline.** The animator then plays the clip at its own authored
    // seconds — which is the right answer for a HELD, open-ended action (Guard runs as long as
    // the button is down; its clip loops). Everything with a definite lifetime should report,
    // and a fixed-length action is simply `vars.TimeInState / Duration`. Reporting is what keeps
    // the overlay honest when the action's real length is variable (Grab's early throw) or
    // differs from the clip's (Beam outlives its 0.6s clip).
    public virtual float AnimationProgress(in ActionVars vars) => -1f;

    // The world AIM direction of an input-parametrized action (a stab's StabDir), exposed to the
    // animation layer so it can re-aim the authored (horizontal) overlay pose along the actual
    // input direction. Render-only, same contract as AnimationProgress — derived from ActionVars,
    // never read by the sim. Default none. The animator owns WHICH bones re-aim (see CharacterAnimator).
    public virtual bool TryAnimationAim(in ActionVars vars, out Vector2 dir) { dir = default; return false; }
}

// Always-on fallback. Mirrors FallingState's role in the movement FSM.
public class NullAction : ActionState
{
    public override int ActivePriority  => 0;
    public override int PassivePriority => 0;
    public override bool NeutralForEntry => true;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab) => true;
    public override bool CheckConditions  (EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars) => true;
    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars) {}
}

// Post-attack countdown AND pre-attack wind-up — the single airlock state between
// actions. The countdown ("recovery index", ctx.RecoveryIndex()) is the frames
// remaining on Condition.RecoveryActive; index 0 is the READY posture (the old
// ReadyAction, now merged): at zero the state persists only while a pressed-and-
// held LMB is charging, so buffered gestures resolve out of one place.
//
// Three ways in (CheckPreConditions):
//   1. An attack's Exit stamped recovery frames — the normal handoff.
//   2. LMB press-hold from a neutral state — the wind-up role, at index 0. The
//      press FRAME itself is left to press-edge chords (BlockBurst, Beam, Grab,
//      all of which outrank or consume the press); the wind-up picks up what
//      they declined one frame later.
//   3. Eviction lookahead: re-running the candidate preconditions under the
//      hypothetical future ("recovery entered now at the live action's
//      CommitProfile index, intents aged accordingly") finds some request that
//      will still be alive when the countdown reaches an index it can fire from.
//      Recovery then bids with THAT CANDIDATE'S PassivePriority — it inherits the
//      priority of the move it is chaining into, so eviction wins exactly when
//      the request could have preempted the live action directly. Guard (P40)
//      evicts a stab (A30); a mashed slash (P30) does not.
//   4. FLINCH — involuntary eviction (HIT_AIRLOCK_PLAN): a hit that registered
//      hitstun on the previous frame evicts any live action at InterruptBid,
//      above every Active. Flinch is universal; exceptions are ArmorProfile's
//      job (armored hits never register, so they never trip this). While
//      hitstun/stun frames remain, Recovery also holds the slot from neutral
//      (case 1) — the victim visibly sits in the airlock through disadvantage.
//
// Active is LOW (10): recovery is a waiting room, and the index gates in each
// entrant's precondition — not this state's priority — decide who may leave it
// and when.
public class RecoveryAction : ActionState
{
    // ── Charge curve (BuildMeters-style ramp → sweet spot → settle) ──────────
    // The wind-up hold is a timing minigame, not a monotonic bank: charge ramps
    // linearly to full over ChargeRampSeconds, holds full through a short sweet
    // spot, then SETTLES at SettleFraction for as long as the button stays down.
    // Releasing inside the sweet spot is the reward for precise timing; over-
    // holding is punished with the flat settle, never a bleed to zero (unlike
    // BuildMeters' EruptDecay — a held stab charge is a stance, not a resource).
    // The fraction is stamped into vars.StabCharge on exit; StabAction maps it
    // to a damage multiplier.
    public const float ChargeRampSeconds = 1.0f;   // linear 0 → 1 (the old MaxChargeHold)
    public const float SweetSpotSeconds  = 0.1f;   // full-charge window after the ramp
    public const float SettleFraction    = 0.6f;   // flat value for an overheld charge

    private const int   PassiveBase   = 45;     // countdown handoff / wind-up entry bid
    // Flinch bid — above every action's Active (Grab is 48), so a registered hit
    // always interrupts. Universality is deliberate: "this move can't be
    // interrupted by light hits" is expressed with ArmorProfile, not priority.
    private const int   InterruptBid  = 60;

    // Set by CheckPreConditions each time it passes, read by the selection loop
    // immediately after. Transient within a Step (recomputed before every read),
    // so it needs no snapshot for rollback.
    private int _bid = PassiveBase;

    public override int ActivePriority  => 10;
    public override int PassivePriority => _bid;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _bid = PassiveBase;

        // The LIVE incumbent (falls back to PreviousAction for hand-built test
        // contexts that don't wire ctx.CurrentAction).
        var cur = ctx.CurrentAction ?? ctx.PreviousAction(0);
        bool neutral = cur == null || cur is RecoveryAction || cur.NeutralForEntry;
        if (neutral)
        {
            // (1) Post-attack handoff: an Exit stamped recovery frames. Gated on
            // a NEUTRAL incumbent: a state that legitimately fired mid-countdown
            // (guard's MaxEntry window) owns its activation — the still-ticking
            // stamp must not drag it back into the airlock (that was a 1-frame
            // guard↔recovery oscillation). Reaching over a live action is
            // exclusively the eviction lookahead's business, below. Hit
            // disadvantage (hitstun/stun) holds the slot the same way — a hit
            // taken while neutral parks the victim here for its window.
            if (ab.Condition.RecoveryActive || ctx.HitDisadvantageFrames() > 0) return true;

            // (2) Wind-up role. PressEdge age ≥ 1: the press frame belongs to the
            // press-edge chords; Shift+LMB belongs to Beam/EnergyBall/Grab.
            return !ctx.Input.Shift && ctx.Input.LeftClick
                && ctx.Intents.Peek(IntentType.PressEdge, ctx.CurrentFrame, out var pe)
                && ctx.CurrentFrame - pe.IssuedFrame >= 1;
        }

        // (4) FLINCH — involuntary eviction. CombatSystem applies hits after all
        // updates, so a connect on frame N registers with LastHitFrame == N and
        // is seen here on N+1 (crush self-registration happens earlier in the
        // same Update, hence <= 1). Armored, parried, invulnerable, and
        // struggle hits never reach OnHitRegistered, so they never trip this.
        // The evicted action's Exit still stamps its CommitProfile; the recovery
        // index max-merges it with the hitstun window, so a jab can't be used
        // as a cheap self-cancel of a long tail.
        if (ctx.Combat != null && ctx.Combat.HitstunActive
            && ctx.CurrentFrame - ctx.Combat.LastHitFrame <= 1)
        {
            _bid = InterruptBid;
            return true;
        }

        // (3) Eviction lookahead against the live action, bidding with the found
        // candidate's own PassivePriority.
        if (ctx.ActionRegistry == null) return false;
        int commit = cur.CommitProfile(ctx, in ctx.CurrentActionVars);
        if (commit == Blocked) return false;
        int bid = LookaheadBestBid(ctx, ab, cur, commit);
        if (bid <= cur.ActivePriority) return false;
        _bid = bid;
        return true;
    }

    // Hypothetical re-run of the candidate scan: enter recovery now at index
    // `commitFrames`, tick it down, and at each future frame ask every candidate's
    // REAL precondition whether it would fire — with ctx.CurrentFrame shifted (so
    // intent ages grow and expire honestly) and RecoveryIndex() overlaid. The
    // world is frozen (ctx's geometry caches, input levels, combat flags stay at
    // this frame's values); environment-sensitive preconditions are re-checked
    // for real at fire time, and a lookahead that guessed wrong just leaves the
    // player resting in recovery → Null. Peek-only: nothing is consumed here.
    //
    // Returns the highest PassivePriority among candidates that (a) could NOT
    // fire directly this frame — a chord that already passes (EnergyBall over
    // guard, GuardRetaliate) preempts on its own; routing it through recovery
    // would only add a frame — but (b) would fire at some point of the
    // hypothetical countdown, and (c) outrank the incumbent's Active, so the
    // inherited bid can actually win the selection loop. int.MinValue if none.
    private int LookaheadBestBid(EnvironmentContext ctx, PlayerAbilityState ab,
                                 ActionState cur, int commitFrames)
    {
        var registry = ctx.ActionRegistry;
        int frame0 = ctx.CurrentFrame;
        int best = int.MinValue;
        try
        {
            for (int i = 0; i < registry.Count; i++)
            {
                var a = registry[i];
                if (a is RecoveryAction or NullAction) continue;
                if (a == cur) continue;   // evicting an action to re-enter itself is a no-op
                if (a.PassivePriority <= cur.ActivePriority) continue;   // bid couldn't win
                if (a.PassivePriority <= best) continue;                 // can't improve

                // (a) Direct-fire pre-check at the real present, no overlay.
                ctx.CurrentFrame = frame0;
                ctx.LookaheadRecoveryIndex = null;
                if (a.CheckPreConditions(ctx, ab)) continue;

                // (b) The countdown walk.
                for (int k = 0; k <= commitFrames; k++)
                {
                    ctx.CurrentFrame = frame0 + k;
                    ctx.LookaheadRecoveryIndex = commitFrames - k;
                    if (a.CheckPreConditions(ctx, ab)) { best = a.PassivePriority; break; }
                }
            }
            return best;
        }
        finally
        {
            ctx.CurrentFrame = frame0;
            ctx.LookaheadRecoveryIndex = null;
        }
    }

    // Charge fraction at hold-time `t` — the curve documented on the constants
    // above. Public so tests and the HUD/animation layer can read the same shape.
    public static float ChargeFraction(float t)
    {
        if (t <= 0f) return 0f;
        if (t < ChargeRampSeconds) return t / ChargeRampSeconds;
        if (t <= ChargeRampSeconds + SweetSpotSeconds) return 1f;
        return SettleFraction;
    }

    // Inside the full-charge release window? Shared by the telegraph dot and the
    // render-side charge glow (AttackGlowSystem) so the flash and the curve can
    // never disagree about where the sweet spot is.
    public static bool InSweetSpot(float t)
        => t >= ChargeRampSeconds && t <= ChargeRampSeconds + SweetSpotSeconds;

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (ab.Condition.RecoveryActive || ctx.HitDisadvantageFrames() > 0) return true;
        // Index 0 — the READY posture: persist while a held LMB charge lives. No
        // time cap — an overheld charge settles at SettleFraction rather than
        // dumping the player out of the stance (the old MaxChargeHold did, which
        // under charge-scaled stabs would have meant "held longer, got weaker").
        return vars.Charging && ctx.Input.LeftClick;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // AttackDir is deliberately NOT touched — the combo hold-field
        // continuation in Update re-broadcasts the exiting slash's aim.
        vars.TimeInState = 0f;
        vars.Facing      = ab.Facing == 0 ? 1 : ab.Facing;
        vars.IsGrounded  = ctx.TryGetGround(out _);
        vars.Charging    = ChargeInputLive(ctx);
    }

    // A live (unconsumed) press with the button still down and no Shift — the
    // wind-up. Latched per frame rather than at Enter so a press issued DURING
    // the countdown still charges once the countdown runs out.
    private static bool ChargeInputLive(EnvironmentContext ctx)
        => !ctx.Input.Shift && ctx.Input.LeftClick
        && ctx.Intents.Peek(IntentType.PressEdge, ctx.CurrentFrame, out _);

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;

        // Latch a charge START off a live PressEdge; once latched, the charge
        // persists on the raw button alone. The distinction matters past ~2 s:
        // the PressEdge intent ages out of the IntentBuffer, but a player deep
        // in the settle is still charging — dropping them there would be the
        // old MaxChargeHold cliff wearing a different hat.
        if (ChargeInputLive(ctx) && !vars.Charging)
        {
            vars.Charging    = true;
            vars.TimeInState = 0f;   // charge clock — drives the curve + pulse indicator
            vars.IsGrounded  = ctx.TryGetGround(out _);
            vars.Facing      = ab.Facing == 0 ? 1 : ab.Facing;
        }
        else if (!ctx.Input.LeftClick || ctx.Input.Shift) vars.Charging = false;
        // (The hold-field continuation that used to live here is gone — see the
        // HoldVictims note on SlashLikeAction. It ran off Slash2Ready/Slash3Ready
        // rather than off HoldVictims, so it kept pulling victims through the combo
        // gap after a slash that no longer holds during its own active frames. That
        // made S1 retain a victim it had visibly released, which is the behaviour
        // being removed. Restoring the feature means turning HoldVictims back on AND
        // re-adding a continuation gated on the same flag, not on the combo window.)
    }

    // Hand the finished charge to whatever fires off the release. The stamp goes
    // through ActionVars (never the intent — InputParser measures gestures, the
    // state owns what "charging" means), and Exit is the right moment: on the
    // release frame this state fails CheckConditions and exits BEFORE the
    // selection loop picks the stab, with NullAction's no-op Enter bridging in
    // between — so StabAction.Enter reads a stamp that is 0 frames old. The
    // freshness gate on the consumer side is what keeps a charge whose release
    // resolved into something else (a circle → Pulse) from riding a buffered
    // stab intent seconds later.
    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (!vars.Charging) return;
        vars.StabCharge      = ChargeFraction(vars.TimeInState);
        vars.StabChargeFrame = ctx.CurrentFrame;
        vars.Charging        = false;
    }

    // The old ReadyAction's charge slowdown — applied only while the wind-up hold
    // is live, never during a bare countdown. Slashes flick through the wind-up in
    // 1–2 frames so the dip is imperceptible; a long-held stab charge lingers and
    // feels heavy. The GravityScale dip gives a floaty hover while winding up in
    // the air; grounded, the standing spring overrides gravity so it's a no-op.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        if (!vars.Charging) return;
        m.MaxWalkSpeed   *= 0.6f;
        m.WalkAccel      *= 0.7f;
        m.GroundFriction *= 1.3f;
        m.MaxAirSpeed    *= 0.7f;
        // Floaty hover only through the ramp + sweet spot. The hold is uncapped
        // now, so a settle-length dip would be a free 0.3-gravity glide for as
        // long as LMB stays down — the charge stance outstays its wind-up, the
        // hover must not.
        if (vars.TimeInState < ChargeRampSeconds + SweetSpotSeconds)
            m.GravityScale *= 0.3f;
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        // Wind-up indicator (the old ReadyAction visual): pulsing dot offset
        // toward facing, colored by posture. Nothing drawn for a bare countdown.
        // The dot grows with the charge fraction and flashes white through the
        // sweet spot — the release-timing cue the whole curve exists for.
        if (!vars.Charging) return;
        const float ArcR = PlayerCharacter.Radius * 1.5f;
        float pulse  = MathF.Sin(vars.TimeInState * MathF.PI * 4f) * 0.5f + 0.5f;
        float offset = ArcR * 0.5f * pulse;
        var pos = body.Position + new Vector2(vars.Facing * offset, 0f);
        float f = ChargeFraction(vars.TimeInState);
        bool sweet = InSweetSpot(vars.TimeInState);
        var color = sweet ? Color.White
                          : (vars.IsGrounded ? Color.Red : Color.DeepSkyBlue) * 0.7f;
        t.Rect(pos, 3f + 3f * f, color);
    }
}

// Shared base for slash-shaped moves. Subclasses configure arc shape, color, posture
// requirements, and what combo flag they set on exit. The base handles: trigger via
// Click intent, lifetime, trail buffer, hurtbox publishing, the arc math, and Draw.
//
// Arc parametrization:
//   outwardFactor = sin(π t)                          0 → 1 → 0 (radial out-and-back)
//   angle         = (SweepAngleDeg/2) · (1 - 2t) · SweepDirection
//                                                    +half → 0 → -half through 0 at t=0.5
// The dot rotates `_slashDir` by `angle` and scales by ArcRadius · outwardFactor.
// Apex (max extent) is along `_slashDir` at t=0.5 — that's where the hurtbox sits.
public abstract class SlashLikeAction : ActionState
{
    // Damage window expressed as fractions of Duration so the window scales when
    // the slash duration is tuned. Window covers ~20%–70% of the slash, with the
    // hitbox apex (max radial extent) sitting at the 50% mark.
    private const float HurtboxStartFraction  = 0.20f;
    private const float HurtboxActiveFraction = 0.50f;
    // Per-frame damage tuned so 2 frames of active window at 30 fps total ≈ TileMaxHP.
    // (Slashes now fire fast enough that the active window is ~2 frames, not 4.)
    private const float SlashDamagePerFrame   = TileDamage.TileMaxHP / 2f;
    // Hitbox scale bump per roadmap §1.7 (1.75× — was 1.0×). Combat felt
    // unrewarding with apex-only hitboxes that just barely covered the dot's
    // visible reach; widening makes near-misses rarer without changing the
    // arc shape or apex position. Per-variant ArcRadiusScale still stacks on
    // top of this, so AirSlash1 (0.9×) and GroundSlash3 (1.3×) still differ.
    // Internal (not private) so RecoveryAction can publish the hold-field
    // continuation at the same geometry.
    internal const float BaseArcRadius        = PlayerCharacter.Radius * 1.5f * 1.75f;
    internal const float HoldFieldBaseRadius  = BaseArcRadius;

    // Hold-field tuning (COMBAT_FEEL_PLAN Phase 2). Variants with HoldVictims=true
    // broadcast a ForceField each frame of the slash that servo-pulls enemies
    // toward a focus in front of the attacker — keeping them in range for the next
    // slice instead of knocking them out of it. Stateless: the field is re-published
    // per frame and dies with the action (see ForceField). MaxAccel is the escape
    // valve — strong enough to beat hitstun-muted control, weak enough that a jump
    // or launch tears free.
    private const float HoldFieldTargetSpeed  = 160f;   // px/s toward the focus
    private const float HoldFieldMaxAccel     = 4000f;  // px/s² servo clamp
    private const float HoldFieldFocusDist    = 0.7f;   // focus at this × radius along SlashDir
    private const float HoldFieldRegionScale  = 1.4f;   // region half-size = this × radius
    // Cosmetic trail behind the apex dot. Lifetime ≈ a few frames so the ribbon
    // reads as motion blur, not afterimage.
    private const int   TrailCapacity         = 8;
    private const float TrailLifetime         = 0.12f;


    // Collision-mode strike parameters (HIT_MOMENTUM_PLAN). Slashes deliberately
    // strike LIGHTER than the player's body mass (2.5): they're quick arm arcs,
    // not full-body thrusts like the stab — so equal-or-heavier targets shrug
    // more and light targets still ping. MinLaunch keeps repeat juggle hits
    // alive: a ball already flying away faster than the swing (u ≤ 0) still
    // visibly pops instead of a dead connect.
    private   const   float    SlashStrikeMass     = 2.5f;
    private   const   float    SlashRestitution    = 0.5f;
    // Was 180 — half the player's run speed handed out for merely connecting, which
    // is a good part of why the light slashes read as hitting a beach ball. The floor
    // is here so a connect is never a dead touch, not so a poke is a launch.
    private   const   float    SlashMinLaunch      = 100f;   // default for MinLaunch below

    // ----- per-variant knobs ------------------------------------------------
    protected abstract float   Duration            { get; }   // seconds
    protected abstract float   ArcRadiusScale      { get; }   // multiplier on BaseArcRadius
    protected abstract float   SweepAngleDeg       { get; }   // total sweep (90, 150, …)
    protected abstract float   SweepDirection      { get; }   // +1 CCW, -1 CW (mirror)
    protected abstract float   KnockbackMagnitude  { get; }
    // Multiplier on SlashDamagePerFrame. Damage is one number on every path — tile
    // HP carved, and HP off a player or entity — so a variant that scales this bites
    // harder in every sense. 1.0 for every stock slash; DownAirSlash
    // raises it because it's the slowest, most committed swing in the air kit.
    protected virtual  float   DamageScale         => 1f;
    // Collision-mode strike speed (px/s), stacked on the attacker's velocity at
    // publish. 0 (default) ⇒ the variant publishes legacy Impulse mode: the
    // hold-slashes (S1/S2) keep their designed tap so the hold field isn't
    // fighting a launch, and GrabbedSlash stays out of momentum entirely.
    // Launcher parity mapping from the old impulse numbers: vs a mass-1 target
    // the impulse Δv was KnockbackMagnitude; collision Δv = (1+e)·μ·u ≈ 0.75·u
    // at strike mass 1 ⇒ StrikeSpeed ≈ 1.33 × old magnitude.
    protected virtual  float   StrikeSpeed         => 1f;
    // Fraction of the attacker's body velocity folded into the published
    // StrikeVelocity. 1.0 (default) is the honest physical reading: you are swinging
    // from a moving frame, so a slash driven into the target by your own momentum
    // closes faster and hits harder.
    //
    // It is a knob because that reading has a blind spot — it assumes body velocity is
    // only ever WEAKLY aligned with the swing. For every horizontal slash that holds:
    // the dot product against a mostly-sideways AttackDir picks up run speed (~200 px/s)
    // at most, and only when you run into your own swing. DownAirSlash breaks it. Its
    // AttackDir points straight down, which is exactly where gravity has been
    // accelerating you, so the term stops being a garnish on StrikeSpeed and starts
    // dominating it: at terminal velocity the fall alone is nearly twice the swing.
    // Knockback then scales linearly and WITHOUT BOUND in how long you fell before
    // connecting, which is not a difficulty curve anyone chose.
    //
    // Damping the share rather than clamping the result keeps "a committed dive hits
    // harder" as a real, readable mechanic — it just stops the height of the fall from
    // being the dominant term in the hit.
    protected virtual  float   StrikeBodyVelocityShare => 1f;
    // Launch floor (px/s): a connect below this still visibly moves a movable target,
    // so a hit never reads as a dead touch. This carries more weight than it looks —
    // StrikeSpeed defaults to 1, so a variant that doesn't override it publishes
    // Collision mode with a near-zero closing speed and this floor IS its knockback.
    // Override to 0 only for a variant whose closing speed is large by construction.
    protected virtual  float   MinLaunch           => SlashMinLaunch;
    protected abstract Color   SlashColor          { get; }
    protected abstract bool    RequireGround       { get; }
    protected abstract bool    RequireAir          { get; }
    // Override to gate on combo flags (Slash2 → cond.Slash2Ready).
    protected virtual  bool    CombosOk(ConditionState cond) => true;
    // Combo steps may fire from ANY point of the recovery countdown (their combo
    // flag is the real gate); openers must wait for index 0.
    protected virtual  bool    EntryFromAnyRecoveryIndex => false;
    // Override to clear the flag we just used + the recovery flag.
    protected virtual  void    OnEnterClearFlags(ConditionState cond) { }
    // Override to set the next-stage flag + recovery duration. Durations are
    // authored in seconds (SetForSeconds) — `dt` is the step rate to convert at.
    // `connected` is the Phase 3 hit-confirm: true iff this slash landed on an
    // entity. Combo openers gate their follow-up flag on it (whiffed pokes don't
    // chain); finishers/one-shots ignore it and just schedule recovery.
    protected abstract void    OnExitSetFlags(ConditionState cond, int currentFrame, float dt, bool connected);
    // AirTurnSlash overrides this to true so a click behind the player gives a
    // genuine backward slash instead of a perpendicular one (roadmap §1.6).
    protected virtual  bool    AllowBackward       => false;
    // Hitstun override in seconds (< 0 ⇒ derive from impulse). Hold-slashes carry
    // a tiny impulse (they pull, not push) so they declare their hitstun explicitly.
    protected virtual  float   HitstunSecondsOverride => -1f;
    // When true, the slash broadcasts a holding ForceField each frame (see the
    // HoldField* constants above).
    //
    // OFF for every slash today, deliberately: victim retention was cut (2026-08-29)
    // because a slash that pulls its target back in reads as the hit not landing.
    // The machinery below (PublishHoldField + the HoldField* constants) is kept wired
    // but dormant, so re-enabling is flipping an override to true rather than
    // rebuilding it. Note that GroundSlash1 still carries the low KnockbackMagnitude
    // (60, "hold, don't shove") it was given as a holding slash — retune that if the
    // no-hold S1 now feels weightless.
    protected virtual  bool    HoldVictims         => false;
    // When > 0, this slash erodes a grabber's grab strength instead of dealing the
    // usual knockback / hitstun (the struggle channel — see Hitbox.GrabStrengthDamage).
    // Only GrabbedSlash overrides this; every normal slash hits normally.
    protected virtual  float   GrabStrengthDamage  => 0f;
    // Newton's-third-law attacker recoil (Plans/HIT_FEEL_PLAN.md phase 2b). Same
    // mechanism StabAction already uses (Hitbox.RecoilScale + CombatSystem's per-HitId
    // recoil inbox); ApplyActionForces below is the shared consumer for every slash
    // variant. Ballpark of Stab's 0.2f, a touch lighter since slashes are quicker arm
    // arcs rather than a full-body thrust. GrabbedSlash overrides to 0 — a struggle
    // hit must have zero effect on either side, not just zero knockback.
    protected virtual  float   RecoilScale         => 0.15f;
    // Tile-recoil gates (see Hitbox.RecoilBreakProtected / RecoilMinMaterialHP).
    // Defaults reproduce the old behavior — every slash bounced off every cell it
    // touched, which was harmless at RecoilScale 0.15. A variant that turns the
    // recoil up wants these on, so it pogos off hard SURVIVING rock instead of off
    // the dirt it just shattered.
    protected virtual  bool    RecoilBreakProtected => false;
    protected virtual  float   RecoilMinMaterialHP  => 0f;
    // -----------------------------------------------------------------------

    protected float ArcRadius => BaseArcRadius * ArcRadiusScale;

    private readonly Trail _trail = new(TrailCapacity, TrailLifetime);

    // Render-only accessors so a glow pass (Game1) can render the slash apex as a glowing
    // shape + trail instead of the flat ribbon. The trail is the swept apex history.
    public Trail SlashTrail     => _trail;
    public Color SlashGlowColor => SlashColor;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    // The slash lives for [0, Duration]; the overlay clip is remapped onto that so it
    // sweeps once over the swing regardless of the authored clip's own timeline length.
    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        // Strict from-set: fire only out of neutral/recovery (never over a live
        // action), and openers wait out the full countdown.
        if (!EntryOk(ctx, EntryFromAnyRecoveryIndex ? int.MaxValue : 0)) return false;
        // Grab gate (BlocksAttack is grabbed-only now): hitstun/stun gate through
        // the recovery index inside EntryOk above. Guard's escape from stun is
        // its own 0.2s entry window — see GuardAction.
        if (ctx.Combat?.BlocksAttack == true) return false;
        bool grounded = ctx.TryGetGround(out _);
        if (RequireGround && !grounded) return false;
        if (RequireAir    &&  grounded) return false;
        if (!CombosOk(ab.Condition)) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState     = 0f;
        vars.AttackDir        = ComputeSlashDir(ctx, ab);
        vars.HitId           = ctx.HitIds.Next();
        vars.AttackConnected = false;
        _trail.Clear();
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);
        OnEnterClearFlags(ab.Condition);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => OnExitSetFlags(ab.Condition, ctx.CurrentFrame, ctx.Dt, vars.AttackConnected);

    // Mouse-to-body direction, hemisphere-clamped (unless AllowBackward) so a
    // click behind the player produces a perpendicular slash rather than a
    // backward one. Degenerate inputs fall back to (Facing, 0).
    private Vector2 ComputeSlashDir(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        Vector2 raw = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        if (!AllowBackward && raw.X * facing < 0f) raw.X = 0f;
        if (raw.LengthSquared() < 1e-4f) return new Vector2(facing, 0f);
        return Vector2.Normalize(raw);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
        var dot = ComputeDotPosition(ctx.Body.Position, in vars);
        _trail.Tick(ctx.Dt);
        _trail.Push(dot);

        // Hit-confirm latch (Phase 3): poll the prior frame's connection count for
        // this HitId. Entity hits are deduped per target, so the count is non-zero
        // only on the frame after a fresh connection — latch it so OnExitSetFlags
        // (which fires a few frames later, after the active window) can gate combos.
        if (!vars.AttackConnected && ctx.CombatSystem != null
            && ctx.CombatSystem.PeekHits(vars.HitId) > 0)
            vars.AttackConnected = true;

        float windowStart = Duration * HurtboxStartFraction;
        float windowEnd   = windowStart + Duration * HurtboxActiveFraction;
        if (vars.TimeInState >= windowStart && vars.TimeInState <= windowEnd && ctx.Hitboxes != null)
        {
            var apex = ctx.Body.Position + vars.AttackDir * ArcRadius;
            var region = new BoundingBox(
                apex.X - ArcRadius * 0.5f, apex.Y - ArcRadius * 0.5f,
                apex.X + ArcRadius * 0.5f, apex.Y + ArcRadius * 0.5f);
            // Launcher variants (StrikeSpeed > 0) publish Collision mode; the
            // strike fields are ignored under Impulse, so one call covers both.
            // KnockbackImpulse stays authored either way — parry cone, bullet
            // deflect, and the OnHit early-outs read it as the swing direction.
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, SlashDamagePerFrame * DamageScale,
                vars.AttackDir * KnockbackMagnitude,
                ctx.Faction, ctx.SelfId, SlashColor,
                hitstunSecondsOverride: HitstunSecondsOverride,
                grabStrengthDamage: GrabStrengthDamage,
                recoilScale: RecoilScale,
                recoilBreakProtected: RecoilBreakProtected,
                recoilMinMaterialHP: RecoilMinMaterialHP,
                mode: StrikeSpeed > 0f ? KnockbackMode.Collision : KnockbackMode.Impulse,
                strikeDir: vars.AttackDir,
                strikeVelocity: ctx.Body.Velocity * StrikeBodyVelocityShare
                                + vars.AttackDir * StrikeSpeed,
                strikeMass: SlashStrikeMass,
                restitution: SlashRestitution,
                minLaunch: MinLaunch,
                origin: ctx.Body.Position));
        }

        // Holding slashes broadcast their pull field for the WHOLE slash (not just
        // the damage window) so a victim clipped early in the arc is still held
        // through the follow-through. Re-published every frame; see ForceField.
        if (HoldVictims)
            PublishHoldField(ctx, vars.AttackDir, ArcRadius, strengthScale: 1f);
    }

    // Shared by the slash Update (full strength) and RecoveryAction's combo-gap
    // continuation (weaker). Focus sits in front of the attacker along `dir`;
    // the region is wide enough to cover the arc's reach so anything the slash
    // can touch is also held.
    internal static void PublishHoldField(EnvironmentContext ctx, Vector2 dir, float radius, float strengthScale)
    {
        if (ctx.ForceFields == null) return;
        var focus = ctx.Body.Position + dir * (radius * HoldFieldFocusDist);
        float r = radius * HoldFieldRegionScale;
        ctx.ForceFields.Publish(new ForceField(
            new BoundingBox(focus.X - r, focus.Y - r, focus.X + r, focus.Y + r),
            focus,
            HoldFieldTargetSpeed * strengthScale,
            HoldFieldMaxAccel   * strengthScale,
            ctx.Faction, ctx.SelfId));
    }

    private Vector2 ComputeDotPosition(Vector2 anchor, in ActionVars vars)
    {
        float t = MathHelper.Clamp(vars.TimeInState / Duration, 0f, 1f);
        float outF       = MathF.Sin(MathF.PI * t);
        float halfSweep  = SweepAngleDeg * 0.5f * MathF.PI / 180f;
        float angle      = halfSweep * (1f - 2f * t) * SweepDirection;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        Vector2 dir = new Vector2(
            vars.AttackDir.X * cos - vars.AttackDir.Y * sin,
            vars.AttackDir.X * sin + vars.AttackDir.Y * cos);
        return anchor + dir * (ArcRadius * outF);
    }

    // The slash apex is rendered as a glowing triangle + trail by Game1's glow pass
    // (GlowRenderer, its own PrimitiveBatch pass, not the telegraph list).
    // SlashTrail/SlashGlowColor expose what it needs; nothing to telegraph here.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars) { }

    // Newton's-third-law recoil from last frame's connecting hit (Plans/HIT_FEEL_PLAN.md
    // phase 2b) — same 1-frame-inbox read StabAction.ApplyActionForces already does.
    // Shared here so every slash variant gets it for free; RecoilScale (0 for
    // GrabbedSlash) gates whether there's ever anything to read.
    public override void ApplyActionForces(EnvironmentContext ctx, in ActionVars vars)
    {
        if (ctx.CombatSystem == null) return;
        var recoil = ctx.CombatSystem.PeekRecoil(vars.HitId);
        if (recoil != Vector2.Zero) ctx.Body.Velocity += recoil;
    }
}

// ---------- Ground combo: S1 → S2 → S3 -----------------------------------------

// Opening ground slash. Wide CCW sweep, red. Holds rather than launches
// (COMBAT_FEEL_PLAN Phase 2): the knockback is a light tap and the slash
// broadcasts a holding field pulling the victim into S2's reach — the combo
// finisher (S3) is where the launch lives. Real hitstun comes from the
// explicit override, not the (now tiny) impulse.
public class GroundSlash1 : SlashLikeAction
{
    // Slashes are fast — Duration tuned so the active damage window is ~2 frames at 30 fps.
    // Variants scale around this baseline for combo-feel variety.
    protected override float Duration            => 0.14f;
    protected override float ArcRadiusScale      => 1.0f;
    protected override float SweepAngleDeg       => 100f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 60f;     // was 200 — hold, don't shove
    protected override Color SlashColor          => Color.Red;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;
    protected override float HitstunSecondsOverride => 0.30f;
    protected override bool  HoldVictims         => false;
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // Hit-confirm (`connected`) is tracked but intentionally does NOT gate the
        // chain right now — the S2 window opens whether or not S1 landed. To make
        // the combo hit-confirmed (Phase 3 whiff-punish), wrap the Slash2Ready set in
        // `if (connected)`.
        ConditionState.SetForSeconds(ref c.Slash2Ready, ref c.Slash2ExpireFrame, 1.0f, f, dt);
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame,  0.1f, f, dt);
    }
}

// Combo step 2 — mirror-handedness sweep, slightly faster, slightly harder hit.
public class GroundSlash2 : SlashLikeAction
{
    protected override float Duration            => 0.13f;
    protected override float ArcRadiusScale      => 1.05f;
    protected override float SweepAngleDeg       => 110f;
    protected override float SweepDirection      => -1f;
    protected override float KnockbackMagnitude  => 80f;     // was 260 — still holding
    protected override Color SlashColor          => Color.Red;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;
    protected override float HitstunSecondsOverride => 0.30f;
    protected override bool  HoldVictims         => false;

    // Combo moves preempt Recovery via higher passive priority.
    public override int PassivePriority => 50;

    protected override bool EntryFromAnyRecoveryIndex => true;
    protected override bool CombosOk(ConditionState c) => c.Slash2Ready;
    protected override void OnEnterClearFlags(ConditionState c)
    {
        c.Slash2Ready    = false;
        c.RecoveryActive = false;
    }
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // `connected` tracked but not gating — see GroundSlash1.OnExitSetFlags.
        ConditionState.SetForSeconds(ref c.Slash3Ready, ref c.Slash3ExpireFrame, 1.0f, f, dt);
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame,  0.1f, f, dt);
    }
}

// Combo finisher — wide 160° CCW sweep, longer reach, hot color, big knockback.
public class GroundSlash3 : SlashLikeAction
{
    protected override float Duration            => 0.18f;
    protected override float ArcRadiusScale      => 1.30f;
    protected override float SweepAngleDeg       => 160f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 230f;
    protected override float StrikeSpeed         => 300f;    // the combo launcher — 225 px/s on a player
    protected override Color SlashColor          => Color.OrangeRed;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;

    public override int PassivePriority => 50;

    protected override bool EntryFromAnyRecoveryIndex => true;
    protected override bool CombosOk(ConditionState c) => c.Slash3Ready;
    protected override void OnEnterClearFlags(ConditionState c)
    {
        c.Slash3Ready    = false;
        c.RecoveryActive = false;
    }
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // End of chain — no further combo flag.
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.167f, f, dt);
    }
}

// Crouch slash — only fires from CrouchedState. Longer reach than a stand slash,
// no combo chain (deliberately a one-and-done from a low stance). Preempts
// GroundSlash1 via higher passive priority when crouched; precondition fails
// when not crouched so the regular slash takes over.
public class CrouchSlash : SlashLikeAction
{
    protected override float Duration            => 0.16f;
    protected override float ArcRadiusScale      => 1.45f;
    protected override float SweepAngleDeg       => 90f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 150f;
    protected override float StrikeSpeed         => 200f;
    protected override Color SlashColor          => Color.Goldenrod;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;

    // Beats GroundSlash1 (30/30) on ties without out-prioritizing Slash2/3 combos (50).
    public override int PassivePriority => 32;

    protected override bool CombosOk(ConditionState c) => true;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Standard slash gating PLUS the player must be in CrouchedState. The
        // base class doesn't expose CheckPreConditions in a way we can extend
        // cleanly, so we duplicate its check and add the crouch requirement.
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        if (!EntryOk(ctx)) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (!ctx.TryGetGround(out _)) return false;
        if (ctx.PreviousState(0) is not CrouchedState) return false;
        return true;
    }

    // No combo flag set on exit — crouch slash terminates the chain.
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.167f, f, dt);
    }
}

// ---------- Air combo: AS1 → AS2 -----------------------------------------------

// Opening air slash. Tighter & faster than ground S1, blue.
public class AirSlash1 : SlashLikeAction
{
    protected override float Duration            => 0.12f;
    protected override float ArcRadiusScale      => 0.90f;
    protected override float SweepAngleDeg       => 110f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 110f;
    protected override float StrikeSpeed         => 150f;
    protected override Color SlashColor          => Color.DeepSkyBlue;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => true;
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // `connected` tracked but not gating — see GroundSlash1.OnExitSetFlags.
        ConditionState.SetForSeconds(ref c.AirSlash2Ready, ref c.AirSlash2ExpireFrame, 1.0f, f, dt);
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame,  0.1f, f, dt);
    }
}

// Air combo finisher — bigger CW sweep, more knockback.
public class AirSlash2 : SlashLikeAction
{
    protected override float Duration            => 0.14f;
    protected override float ArcRadiusScale      => 1.10f;
    protected override float SweepAngleDeg       => 140f;
    protected override float SweepDirection      => -1f;
    protected override float KnockbackMagnitude  => 170f;
    protected override float StrikeSpeed         => 230f;
    protected override Color SlashColor          => Color.DeepSkyBlue;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => true;

    public override int PassivePriority => 50;

    protected override bool EntryFromAnyRecoveryIndex => true;
    protected override bool CombosOk(ConditionState c) => c.AirSlash2Ready;
    protected override void OnEnterClearFlags(ConditionState c)
    {
        c.AirSlash2Ready = false;
        c.RecoveryActive = false;
    }
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.133f, f, dt);
    }
}

// Air turn-around slash. Roadmap §1.6: clicking on the opposite side of facing
// in air fires a fast, narrow, long-reach slash AND flips Facing in air (the
// only mechanism that does so, since PlayerCharacter.Update no longer writes
// Facing in air). Higher passive priority than AirSlash1 so a backward-click
// in air picks this instead of being clamped to perpendicular AirSlash1.
public class AirTurnSlash : SlashLikeAction
{
    protected override float Duration            => 0.11f;
    protected override float ArcRadiusScale      => 1.40f;   // long reach
    protected override float SweepAngleDeg       => 60f;     // narrow
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 150f;
    protected override float StrikeSpeed         => 200f;
    protected override Color SlashColor          => Color.Violet;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => true;
    protected override bool  AllowBackward       => true;

    // Beat AirSlash1 (30/30) when both could fire; AirSlash2 combo (50) still wins.
    public override int PassivePriority => 35;

    // Mouse must be on the side opposite Facing for the turn-around to make
    // sense. Without this, this state would just steal every air-click.
    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!base.CheckPreConditions(ctx, ab)) return false;
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        float dx = ctx.Input.MouseWorldPosition.X - ctx.Body.Position.X;
        return dx * facing < 0f;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Flip facing FIRST so the base class's ComputeSlashDir reads the new
        // facing when hemisphere-clamping (well, it doesn't clamp since
        // AllowBackward=true, but the fallback (Facing, 0) at degenerate input
        // still points the right way).
        ab.Facing = -(ab.Facing == 0 ? 1 : ab.Facing);
        base.Enter(ctx, ab, ref vars);
    }

    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // No combo follow-up — turn-around is one-and-done. Short recovery.
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.133f, f, dt);
    }
}

// ---------- Down-air: the pogo chop --------------------------------------------

// Overhead-to-downward chop, fired by an air click aimed into the BOTTOM SEXTANT —
// the 60°-wide wedge centred on straight down. Before this existed, a downward air
// click just produced a normal AirSlash1 with its arc rotated; now the aim reads as
// a distinct move with its own clip, its own (heavier) damage, and a long commitment.
//
// The point of it is the POGO. Every slash already carries Newton's-third-law recoil
// (Plans/HIT_FEEL_PLAN.md phase 2b): CombatSystem negates the impulse it delivered,
// scales it by the hitbox's RecoilScale, and drops it in the attacker's per-HitId
// inbox for ApplyActionForces to read one frame later. At the stock 0.15 that's a
// nudge. Turned up to PogoRecoilScale, a connect below you throws you back UP.
//
// Two things stop the raw recoil from being a usable pogo on its own, and
// ApplyActionForces below fixes both:
//
//   1. It scales with what you hit. Recoil is (1+e)·μ·u·RecoilScale, so a light
//      enemy hands back a fraction of what a heavy one does — the bounce would be
//      unpredictable exactly when the player needs to trust it. Hence PogoSpeed as
//      a floor (and PogoMaxSpeed as a ceiling, so a heavy target doesn't fire you
//      off the top of the screen).
//   2. It's ADDITIVE, and you dive into a pogo with downward velocity. Adding 300
//      px/s up to 400 px/s of fall still leaves you falling. So on an entity connect
//      the vertical component is REPLACED, not added — and only ever in the upward
//      direction (MathF.Min against the current Vy), so a hit can never brake an
//      ascent that was already faster.
//
// Tile contacts deliberately keep the plain additive recoil instead: bouncing off
// terrain is a smaller, more incidental effect than bouncing off a body, and it's
// gated (RecoilBreakProtected + RecoilMinMaterialHP) so you only kick off hard rock
// that SURVIVED the chop, never off the dirt you just carved through.
public class DownAirSlash : SlashLikeAction
{
    // Aim wedge. A sextant is 60° wide, so the click direction must sit within 30°
    // of straight down — i.e. its normalized +y component is at least cos(30°).
    private const float AimCosThreshold  = 0.8660254f;

    // Pogo tuning, calibrated against the jump: JumpVelocity (-100) plus JumpHoldForce
    // (-1500) over MaxJumpHoldTime (0.12) nets roughly -280 px/s. The band sits BELOW
    // that on purpose — a floor bounce is half a jump and even the ceiling barely
    // matches one, so chaining pogos down a line of enemies is a way to stay up, not a
    // cheaper elevator than jumping. (It ran 300/520 first, which put every connect at
    // or above a full jump and made the down-air the best vertical movement in the kit.)
    private const float PogoSpeed        = 140f;
    private const float PogoMaxSpeed     = 270f;
    // ~3.3× the stock slash recoil. Below PogoSpeed this never shows (the floor wins);
    // it's what makes a heavy, fast-closing connect kick back harder than a light one.
    private const float PogoRecoilScale  = 0.50f;
    // The hitbox carries ONE RecoilScale, and it feeds the tile path too — so turning
    // it up for the entity pogo would also treble the bounce you get off stone, which
    // is a change to terrain feel nobody asked for (a down-aimed air click used to
    // bounce at the stock 0.15). This trims the tile share back to ≈ that: 0.50 × 0.35
    // ≈ 0.175. Raise it toward 1.0 to make chopping off hard rock a real pogo too.
    private const float TileRecoilShare  = 0.35f;

    protected override float Duration             => 0.18f;   // the most committed air swing
    protected override float ArcRadiusScale       => 1.25f;   // reach below the feet
    protected override float SweepAngleDeg        => 70f;     // narrow — a chop, not a fan
    protected override float SweepDirection       => +1f;
    protected override float KnockbackMagnitude   => 210f;
    protected override float StrikeSpeed          => 300f;
    // The one slash that swings along gravity, so the only one for which the
    // attacker's velocity is fully collinear with AttackDir — see the base class.
    // Undamped, a terminal-velocity connect closed at ~1300 px/s against the 511 of a
    // hovering one and the 795 of the hardest ground launcher, rising without bound in
    // fall height. At 0.3 a dive still reads as heavier than a hover (511 → ~800) and
    // tops out around that ground launcher instead of running past it.
    protected override float StrikeBodyVelocityShare => 0.3f;
    // No launch floor. The shared 180 exists for variants whose closing speed can be
    // ~0 (StrikeSpeed defaults to 1); this one's is 450 before the dive is counted, so
    // the floor could never bind — and a floor is the wrong shape for a move whose
    // whole point is that the knockback tracks how hard you came down.
    protected override float MinLaunch            => 0f;
    protected override float DamageScale          => 1.4f;
    protected override Color SlashColor           => Color.MediumSpringGreen;
    protected override bool  RequireGround        => false;
    protected override bool  RequireAir           => true;
    // The wedge is only 30° off vertical, so hemisphere-clamping X would barely
    // change the direction — but it would also flatten a back-aimed chop into a
    // perfectly vertical one, losing the bit of aim the player expressed.
    protected override bool  AllowBackward        => true;

    protected override float RecoilScale          => PogoRecoilScale;
    protected override bool  RecoilBreakProtected => true;
    protected override float RecoilMinMaterialHP  => 0.5f;    // same hardness floor the stab uses

    // Beats AirSlash1 (30), AirTurnSlash (35) AND the AirSlash2 combo (50): aiming
    // into the wedge is an explicit request for this move, and having the air combo
    // silently eat it would make the pogo unreliable in exactly the situation you
    // want it (mid-chain, over an enemy). Still under GuardRetaliate (55) and the
    // grab family. Active stays at the shared slash 30, so nothing about how this
    // gets preempted mid-swing changes.
    public override int PassivePriority => 52;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!base.CheckPreConditions(ctx, ab)) return false;
        // +y is down. Inside the wedge iff the click is below us and within 30° of
        // vertical: dy/|d| >= cos(30°).
        var raw = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        float lenSq = raw.LengthSquared();
        if (lenSq < 1e-4f) return false;
        if (raw.Y <= 0f) return false;
        return raw.Y >= AimCosThreshold * MathF.Sqrt(lenSq);
    }

    // One-and-done: no combo flag. The short recovery is the chain — it's what lets a
    // landed pogo roll straight into the next down-air on the enemy below.
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.133f, f, dt);
    }

    // Replaces the base's plain `Velocity += recoil` on an ENTITY connect (see the
    // class comment for why additive can't work there). Terrain-only contacts keep the
    // additive form, scaled by TileRecoilShare.
    public override void ApplyActionForces(EnvironmentContext ctx, in ActionVars vars)
    {
        if (ctx.CombatSystem == null) return;

        // PeekHits is the count of entities this HitId connected with on the frame
        // just resolved — the same 1-frame inbox PeekRecoil rides, and deduped per
        // (HitId, target), so it reads non-zero exactly once per victim per attack.
        if (ctx.CombatSystem.PeekHits(vars.HitId) <= 0)
        {
            // Nothing but terrain (or nothing at all): plain additive bounce, trimmed
            // to the stock slash's strength so hard rock feels the way it always did.
            var tile = ctx.CombatSystem.PeekRecoil(vars.HitId);
            if (tile != Vector2.Zero) ctx.Body.Velocity += tile * TileRecoilShare;
            return;
        }

        var recoil = ctx.CombatSystem.PeekRecoil(vars.HitId);
        // Recoil opposes the swing, and the swing points (mostly) down, so -Y is the
        // upward share. Clamp it into the pogo band; the floor is what makes a bounce
        // off a featherweight still feel like a bounce.
        float bounce = MathHelper.Clamp(MathF.Abs(recoil.Y), PogoSpeed, PogoMaxSpeed);
        var v = ctx.Body.Velocity;
        // Min, not assignment: y is down, so this only ever makes the velocity MORE
        // upward. A player already rising faster than the pogo keeps their ascent.
        v.Y = MathF.Min(v.Y, -bounce);
        // Horizontal recoil is small (the chop is near-vertical) and reads fine added
        // straight on — it's just the sideways lean of an off-axis hit.
        v.X += recoil.X;
        ctx.Body.Velocity = v;
    }
}

// ---------- Stab — long-hold + swipe gesture -----------------------------------

// Linear thrust along the captured swipe direction. Longer duration, longer recovery,
// more knockback than a slash; no combo chain (can't immediately roll into another move).
public class StabAction : ActionState
{
    private const float Duration              = 0.60f;
    // Active window spans the strike + hold of the visual curve (TipExtension): the
    // box opens as the tip starts whipping forward out of the wind-up, SWEEPS OUTWARD
    // with the tip (each box's length tracks vars.TipExt below) through the strike,
    // then dwells at full reach through the early hold. In normalized state-time that's
    // ≈ 0.18–0.55 of Duration (WindupEnd → mid-hold). The hold tail matters now that
    // the boxes grow: the far cells are only covered late in the sweep, so the window
    // has to stay open long enough (≥ TileMaxHP/DamagePerFrame frames at full reach)
    // for them to break — otherwise the proportional box would dig only the near cells.
    // Startup bumped 0.12 → 0.18 s (≈11 frames at 60 fps) as part of the Phase 3
    // commitment spectrum: the stab is now a launcher (3× knockback below), so it
    // earns a real wind-up — whiffing it is punishable, landing it is a kill move.
    // (Entities are HitId-deduped, so the longer window doesn't multi-hit them; it
    // only gives tiles more frames to break and makes a point-blank connect easier.)
    private const float HurtboxStartTime      = 0.18f;
    private const float HurtboxActiveDuration = 0.37f;

    // Lunge window: a short forward-glide phase AFTER the hitbox active window
    // (0.25–0.40) and BEFORE the settle (0.55–0.60). During this window the
    // ground-friction modifier dips so the velocity assist below can actually
    // translate the body — outside it, friction is back up to sell the plant.
    private const float LungeStart   = 0.10f;
    private const float LungeEnd     = 0.4f;
    private const float LungeSpeed   = 90f;     // px/s horizontal target during lunge

    // Roadmap §1.7 hitbox bump (1.75×). Reach + half-width both grow so the
    // stab feels longer AND wider, not just longer-thin. BlockReach/HalfWidth
    // below get the same scale.
    private const float Reach                 = PlayerCharacter.Radius * 3.3f  * 1.75f;
    private const float PrimaryHalfWidth      = PlayerCharacter.Radius * 0.55f * 1.75f;
    // Soft mid-attack steering. The captured _stabDir rotates toward the current
    // mouse direction at up to MaxSteerSpeed rad/s, with the total deviation from
    // the initial swipe angle capped at MaxTotalSteer. Lets the player adjust the
    // angle slightly during the wind-up + active window without making stab feel
    // like a homing missile.
    private const float MaxSteerSpeed = 1.8f;     // rad/s
    private const float MaxTotalSteer = 0.55f;    // rad (~31°)
    // Tile-shockwave box — wider than the entity-hitbox (digs a channel rather than a
    // thin slot) and extended past the visible tip by BlockReachFactor, so it ploughs
    // a little deeper than the entity reach. Both boxes' LENGTHS now track the live tip
    // extension (vars.TipExt) per frame rather than springing to a fixed reach, so the
    // dig propagates outward in sync with the thrust instead of detonating at once.
    private const float BlockHalfWidth        = PlayerCharacter.Radius * 0.9f * 1.75f;
    // Block tip overshoot past the primary (entity) tip — "35% deeper". Was an absolute
    // BlockReach ≈ 1.82× Reach; now relative to the live tip so it sweeps in lock-step.
    private const float BlockReachFactor      = 1.35f;
    // Floor on each box's length during the active window so a point-blank stab still
    // connects while the tip is still near the body (vars.TipExt dips slightly negative
    // at the wind-up tail) and the per-frame polygons never go degenerate.
    private const float MinHitLength          = PlayerCharacter.Radius * 2f;
    // Direction-hint magnitude only (HIT_MOMENTUM_PLAN): the stab is the first
    // Collision-mode move, so momentum comes from StrikeSpeed below, not this.
    // KnockbackImpulse stays authored because the parry cone (CombatState.TryParry)
    // and bullet deflection (BulletProjectile.OnHit) read it as the attack's
    // direction, and the parry/invuln early-outs echo it back as recoil.
    private const float KnockbackMagnitude    = 1140f;
    private const float DamagePerFrame        = TileDamage.TileMaxHP / 4f;
    // Collision-mode striker (HitResolver.Resolve): the stab is a virtual body
    // flying at StrikeSpeed along the thrust, on top of the attacker's real
    // velocity — so a dive stab genuinely hits (and pogos) harder than a
    // standing poke. A stationary player target takes (1+e)·u/2 ≈ 488 px/s, and
    // Strength = u clears the stun threshold, so a clean stab launches AND stuns
    // (→ Tumble).
    //
    // Deliberately UNTOUCHED by the knockback pass that cut every slash ~40% and
    // halved the launch floor. The stab is the designated launcher of the kit —
    // slow, committed, single-target, telegraphed by a whole wind-up — and the
    // point of pulling the rest down was to let it read that way instead of being
    // one shove among many. It is now ~2.2× GroundSlash3's launch, where it used
    // to be ~1.3×.
    private const float StrikeSpeed           = 650f;
    // Entity-collision restitution + minimum visible launch for a connect on a
    // fleeing target.
    private const float Restitution           = 0.5f;
    private const float MinLaunch             = 120f;
    // Tile recoil (HitResolver.TileRecoil): bounce = (1+e_material)·approach·RecoilScale,
    // fired ONCE PER ATTACK (CombatSystem latches on the dedupe set) against the
    // bounciest surviving surface, floored at MinRecoilSpeed. Tuned so the
    // DEFAULT stab-into-ground pogo is exactly a jump's worth of upward momentum
    // (full-hop launch ≈ -100 initial + -1500·0.12 hold ≈ 280 px/s): a standing
    // stab into stone computes 1.7·650·0.2 ≈ 221 and rides the 280 floor. Speed
    // earns more — a 400 px/s dive → 1.7·1050·0.2 ≈ 357, terminal ~700 → ≈ 459.
    // BreakProtected ⇒ cells the stab destroys don't contribute (ploughing
    // through sand / dirt stays thrust-positive); survivors (stone) pogo.
    private const float RecoilScale           = 0.2f;
    private const float MinRecoilSpeed        = 280f;
    // Hardness floor: sand (MaxHP 0.5) doesn't pogo even on its first contact
    // frame (when BreakProtected can't yet save us — sand takes 2 hits to
    // break). Dirt (1.0) and stone (2.0) still pogo.
    private const float RecoilMinMaterialHP   = 0.5f;

    // Air-stab dive boost. Velocity projected onto _stabDir at the moment of commit
    // maps via clamp + lerp to a scalar in [MinBoost, MaxBoost]. Applied to damage
    // on both hitboxes and to the tile-shockwave box's dimensions (length × boost,
    // width × √boost so the box doesn't grow disproportionately wide). Ground stab
    // always reads 1×.
    private const float MinBoost            = 1.0f;
    private const float MaxBoost            = 2.5f;
    // velAlongStab at which the boost saturates. A clean downward dive easily
    // reaches ~400 px/s (terminal-ish fast-fall); a casual mid-air stab sees
    // 50–100 px/s and stays near baseline.
    private const float BoostReferenceSpeed = 400f;

    // Charge scaling (RecoveryAction's wind-up curve → vars.StabCharge → this).
    // Damage-only, on both boxes: a sweet-spot release doubles what the stab
    // takes off a body AND digs per frame, without inflating the box geometry
    // (that stays the dive boost's job) or the launch — StrikeSpeed keeps its
    // "deliberately untouched" calibration against the parry/recoil/stun stack.
    // The curve makes the effective range: tap ≈ 1×, full ramp + sweet spot 2×,
    // overheld settle 1.6×.
    private const float MaxChargeBoost = 2.0f;


    // Tip ribbon — short lifetime so the trail snaps with the strike rather than
    // lingering past the retract. Render-only; not part of ActionVars.
    private readonly Trail _tipTrail = new(capacity: 10, lifetime: 0.14f);

    // Render-only accessors so Game1's glow pass can render the stab tip as a glowing
    // sphere + trail instead of the flat ribbon. Color depends on grounded-ness.
    public Trail TipTrail => _tipTrail;
    public Color StabColorFor(bool grounded) => ColorFor(grounded);

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    // Remap the overlay clip onto the stab's [0, Duration] so the authored thrust sweeps
    // once over the swing — windup/strike/hold/retract stay synced to the hitbox windows.
    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    // The stab's live aim (captured at commit, steered toward the cursor within MaxTotalSteer).
    // The animator rotates the authored horizontal thrust onto this so an up/diagonal stab reads.
    public override bool TryAnimationAim(in ActionVars vars, out Vector2 dir)
    { dir = vars.StabDir; return dir.LengthSquared() > 1e-6f; }

    private static Color ColorFor(bool isGrounded) => isGrounded ? Color.Goldenrod : Color.MediumPurple;

    // AirSpinStab overrides true so the swipe (and mid-attack mouse-steer clamp)
    // can point backward relative to Facing. Default Stab still clamps to front.
    protected virtual bool AllowBackward => false;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Intents.Peek(IntentType.Stab, ctx.CurrentFrame, out _)) return false;
        // Strict from-set: only out of neutral/recovery, at a finished countdown.
        if (!EntryOk(ctx)) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        // Shift+LMB-hold-swipe is reserved for BeamAction. A Stab intent
        // emitted from a Shift-held press would otherwise route to a normal
        // stab on release; gate it off so the beam path doesn't double-fire.
        // AirSpinStab overrides this by checking its own preconditions.
        if (ctx.Input.Shift) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.IsGrounded  = ctx.TryGetGround(out _);
        vars.HitId       = ctx.HitIds.Next();
        _tipTrail.Clear();

        // Capture swipe direction from the intent; hemisphere-clamp like slash
        // (unless AllowBackward — AirSpinStab keeps backward swipes intact).
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        if (ctx.Intents.Peek(IntentType.Stab, ctx.CurrentFrame, out var intent))
        {
            var raw = intent.Direction;
            if (!AllowBackward && raw.X * facing < 0f) raw.X = 0f;
            if (raw.LengthSquared() < 1e-4f) raw = new Vector2(facing, 0f);
            vars.StabDir = Vector2.Normalize(raw);
            ctx.Intents.Consume(IntentType.Stab, ctx.CurrentFrame);
        }
        else
        {
            vars.StabDir = new Vector2(facing, 0f);
        }
        vars.InitialStabAngle = MathF.Atan2(vars.StabDir.Y, vars.StabDir.X);

        // Air-stab dive boost: project velocity onto the captured stab direction at the
        // instant of commit. Ground stab + negative projection (velocity opposite to
        // stab) collapse to MinBoost; high-speed aligned dives saturate at MaxBoost.
        // Boost feeds the per-frame publish below: damage × boost on both boxes, and the
        // block box's length × boost / width × √boost so a clean dive digs deeper and a
        // bit wider without ballooning into a giant rectangle.
        if (vars.IsGrounded)
        {
            vars.Boost = 1f;
        }
        else
        {
            float velAlongStab = MathF.Max(0f, Vector2.Dot(vars.StabDir, ctx.Body.Velocity));
            float t = MathHelper.Clamp(velAlongStab / BoostReferenceSpeed, 0f, 1f);
            vars.Boost = MathHelper.Lerp(MinBoost, MaxBoost, t);
        }

        // Wind-up charge → damage multiplier. Honored only when the stamp is at
        // most a frame old — the direct release path stamps and fires in the
        // SAME frame (Recovery exits → Null bridges → Stab enters), so anything
        // older is a leftover from a hold that resolved into a different move,
        // reached here on a buffered intent. Consumed either way so it can't
        // feed a second stab.
        bool chargeFresh = vars.StabCharge > 0f && ctx.CurrentFrame - vars.StabChargeFrame <= 1;
        vars.ChargeBoost = chargeFresh ? MathHelper.Lerp(1f, MaxChargeBoost, vars.StabCharge) : 1f;
        vars.StabCharge  = 0f;
    }

    // Superarmor through the wind-up + strike (HIT_AIRLOCK_PLAN §4): light pokes
    // (Slash1 strength ~100) can't stuff a committed stab; another stab (~650) or
    // anything stun-tier still breaks it. Armored hits land at scaled-down
    // knockback and full damage (see PlayerCharacter.OnHit) — no flinch, no
    // hitstun. The tail (retract/settle) is unarmored: a whiffed stab is
    // punishable as before.
    //
    // 300 → 190 with the knockback pass, which cut every strength number feeding
    // this comparison by ~40%. Left at 300 the stab would have become unstuffable
    // by anything but another stab — armor that was tuned to lose to the combo
    // finisher (Slash3, now u 300) would silently have started beating it.
    private const float ArmorStrength = 190f;
    public override float ArmorProfile(in ActionVars vars)
        => vars.TimeInState < HurtboxStartTime + HurtboxActiveDuration ? ArmorStrength : 0f;

    // Larger recovery than slashes — stab can't roll directly into anything.
    private const float RecoverySeconds     = 0.3f;
    // Recovery price of bailing during the wind-up: nothing was swung, so the
    // cancel is nearly free — just enough that guard-flicker can't zero it out.
    private const float WindupEvictSeconds  = 0.10f;

    // Commitment envelope: cheap cancel out of the wind-up, the FULL tail price
    // anywhere from the strike on. User intent is always expressible — a guard
    // request mid-strike evicts the stab (guard P40 > stab A30) — but bailing
    // during the swing forfeits the strike AND pays the whole whiff tail, so it
    // is a real trade, not a free cancel. A mashed slash (P30) can't evict at
    // all: recovery's inherited bid loses to the stab's Active 30. Exit stamps
    // recovery from the same number so an eviction pays exactly what was quoted.
    public override int CommitProfile(EnvironmentContext ctx, in ActionVars vars)
        => vars.TimeInState < HurtboxStartTime
            ? SimFrames.FromSeconds(WindupEvictSeconds, ctx.Dt)
            : SimFrames.FromSeconds(RecoverySeconds,    ctx.Dt);

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetFor(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame,
                              CommitProfile(ctx, in vars), ctx.CurrentFrame);
    }

    // Heavy-stance modifiers throughout the stab; friction dips during the lunge
    // window so the ApplyActionForces velocity assist isn't immediately braked.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        bool inLunge = vars.TimeInState >= LungeStart && vars.TimeInState <= LungeEnd;
        m.MaxWalkSpeed   *= 0.35f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= inLunge ? 0.15f : 1.5f;
        m.MaxAirSpeed    *= 0.6f;
        m.AirDrag        *= 1.3f;
    }

    // Forward glide along the stab direction during the lunge window. Generalized
    // from "Velocity.X" to a full-vector projection so a diagonal or vertical
    // stab carries the player in that direction, not just horizontally (roadmap
    // §1.7). "Ensure-at-least" semantic preserved: project current velocity onto
    // _stabDir, raise to LungeSpeed if below. A player already moving faster
    // along the stab direction isn't nerfed.
    public override void ApplyActionForces(EnvironmentContext ctx, in ActionVars vars)
    {
        // Lunge first (the "ensure ≥ LungeSpeed along stab" assist), then recoil.
        // Order matters: recoil after lunge means a hard surface's back-impulse
        // can actually flip Vx negative (pogo); recoil before lunge would let
        // the lunge re-positivize Vx and erase the pogo entirely.
        if (vars.IsGrounded && vars.TimeInState >= LungeStart && vars.TimeInState <= LungeEnd)
        {
            var v = ctx.Body.Velocity;
            float velAlongStab = Vector2.Dot(v, vars.StabDir);
            if (velAlongStab < LungeSpeed)
                ctx.Body.Velocity = v + vars.StabDir * (LungeSpeed - velAlongStab);
        }

        // Newton's-third-law recoil from last frame's connecting hits. Read once
        // per frame; applied as an instantaneous Δv (body has no mass, impulse
        // and Δv coincide). BreakProtected on the primary box means only cells
        // that survived the hit / entities that were struck contribute, so
        // ploughing through sand stays thrust-positive while a stab into stone
        // bounces the player off. Runs regardless of grounded/lunge windows so
        // air-stab pogo (the canonical pogo case) also fires.
        if (ctx.CombatSystem != null)
        {
            var recoil = ctx.CombatSystem.PeekRecoil(vars.HitId);
            if (recoil != Vector2.Zero) ctx.Body.Velocity += recoil;
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
        _tipTrail.Tick(ctx.Dt);

        // Soft mouse-tracking: rotate _stabDir toward the cursor direction, with
        // both a per-frame angular-velocity cap and a total-deviation cap from
        // the initial angle. Hemisphere-clamp the target like the initial capture
        // so a cursor behind the player doesn't yank the stab backwards.
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        Vector2 toMouse = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        if (!AllowBackward && toMouse.X * facing < 0f) toMouse.X = 0f;
        if (toMouse.LengthSquared() > 1e-4f)
        {
            float targetAngle = MathF.Atan2(toMouse.Y, toMouse.X);
            float currentAngle = MathF.Atan2(vars.StabDir.Y, vars.StabDir.X);
            float delta        = WrapAngle(targetAngle - currentAngle);
            float maxStep      = MaxSteerSpeed * ctx.Dt;
            if (delta >  maxStep) delta =  maxStep;
            if (delta < -maxStep) delta = -maxStep;
            float newAngle = currentAngle + delta;
            // Clamp total deviation from initial.
            float dev = WrapAngle(newAngle - vars.InitialStabAngle);
            if (dev >  MaxTotalSteer) newAngle = vars.InitialStabAngle + MaxTotalSteer;
            if (dev < -MaxTotalSteer) newAngle = vars.InitialStabAngle - MaxTotalSteer;
            vars.StabDir = new Vector2(MathF.Cos(newAngle), MathF.Sin(newAngle));
        }

        // Sample the visible tip into the trail so Draw can render a fading ribbon
        // chasing the strike. Cache the extension so Draw doesn't recompute it.
        vars.TipExt = TipExtension(vars.TimeInState / Duration);
        _tipTrail.Push(ctx.Body.Position + vars.StabDir * vars.TipExt);

        if (vars.TimeInState >= HurtboxStartTime &&
            vars.TimeInState <= HurtboxStartTime + HurtboxActiveDuration &&
            ctx.Hitboxes != null)
        {
            // Both stab hitboxes use the actual rotated-rectangle polygon for narrow-phase
            // intersection. Rotation = angle of _stabDir from +X. The broad-phase AABB
            // is computed from the rotated polygon so the cell sweep in CombatSystem
            // still reads correctly.
            float rotation = MathF.Atan2(vars.StabDir.Y, vars.StabDir.X);
            float dmg = DamagePerFrame * vars.Boost * vars.ChargeBoost;

            // Each box grows from the body out to the LIVE tip (vars.TipExt, the same
            // curve that drives the visible thrust + glow), floored so a point-blank
            // connect still lands and the polygon never degenerates. A box of length L
            // centered at Body + dir*(L/2) spans Body → Body + dir*L, so its leading
            // edge sits exactly on the tip and the dig carves outward in sync with the
            // animation instead of detonating the whole reach at once. Built per frame
            // (varying length) instead of from a cached static polygon.
            float primaryLen = MathHelper.Clamp(vars.TipExt, MinHitLength, Reach);
            float blockLen   = MathF.Max(vars.TipExt * BlockReachFactor, MinHitLength) * vars.Boost;
            float blockHalfW = BlockHalfWidth * MathF.Sqrt(vars.Boost);

            // Primary thrust — entity + tile damage along the thrust line, body → tip.
            var primaryPoly   = Polygon.CreateRectangle(primaryLen, PrimaryHalfWidth * 2f);
            var primaryCenter = ctx.Body.Position + vars.StabDir * (primaryLen * 0.5f);
            var primaryAABB   = primaryPoly.GetBoundingBox(primaryCenter, rotation);
            ctx.Hitboxes.Publish(new Hitbox(
                primaryAABB, vars.HitId, dmg,
                vars.StabDir * KnockbackMagnitude,
                ctx.Faction, ctx.SelfId, ColorFor(vars.IsGrounded),
                shape: primaryPoly, shapePos: primaryCenter, shapeRotation: rotation,
                recoilScale: RecoilScale, recoilBreakProtected: true,
                recoilMinMaterialHP: RecoilMinMaterialHP,
                mode: KnockbackMode.Collision,
                strikeDir: vars.StabDir,
                strikeVelocity: ctx.Body.Velocity + vars.StabDir * StrikeSpeed,
                strikeMass: ctx.Mass,
                restitution: Restitution,
                minLaunch: MinLaunch,
                minRecoilSpeed: MinRecoilSpeed,
                origin: ctx.Body.Position));

            // Block-shockwave — same HitId so entities that overlap both count once. No
            // knockback (knockback comes from the primary box). Tiles only — passes
            // cleanly past entities along the thrust axis. Tracks the tip like the primary
            // box but BlockReachFactor longer and boost-scaled in length×boost / width×√boost,
            // so a well-aligned air dive digs a substantially deeper + slightly wider channel.
            var blockPoly   = Polygon.CreateRectangle(blockLen, blockHalfW * 2f);
            var blockCenter = ctx.Body.Position + vars.StabDir * (blockLen * 0.5f);
            var blockAABB   = blockPoly.GetBoundingBox(blockCenter, rotation);
            // Brighten the debug color in proportion to boost so the bigger box reads
            // visually distinct from a baseline stab when DebugDrawHitboxes is on.
            float boostT = (vars.Boost - MinBoost) / (MaxBoost - MinBoost);
            var blockColor = Color.Lerp(Color.Lerp(ColorFor(vars.IsGrounded), Color.Gray, 0.4f), Color.White, boostT * 0.5f);
            ctx.Hitboxes.Publish(new Hitbox(
                blockAABB, vars.HitId, dmg,
                Vector2.Zero,
                ctx.Faction, ctx.SelfId,
                blockColor,
                HitTargets.TilesOnly,
                shape: blockPoly, shapePos: blockCenter, shapeRotation: rotation,
                origin: ctx.Body.Position));
        }
    }

    // Wrap an angle into [-π, π] so steering math doesn't wind up around a full circle.
    private static float WrapAngle(float a)
    {
        while (a >  MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    // Phase boundaries for the visible extension curve (in normalized state time
    // t = _timeInState / Duration). The strike phase is what the hurtbox active
    // window is timed against — it opens at ~WindupEnd and closes inside Hold.
    private const float WindupEnd      = 0.18f;  // small backward draw of the arm
    private const float StrikeEnd      = 0.42f;  // tip reaches full reach
    private const float HoldEnd        = 0.67f;  // holds at full reach before retracting
    private const float PullbackFrac   = 0.10f;  // how far back the tip pulls, as a fraction of Reach

    // Tip extension along _stabDir at normalized state-time `t`, in pixels.
    // Negative = pulled back behind the body. Built as four piecewise cubic
    // Béziers so each phase keeps a tangent we control: a soft windup, a fast
    // snap, a hold at full reach, and a smooth retract. The control-point
    // biases shape the easing:
    //   • Windup:   P1 near P0 → slow start (the wind-up "settles").
    //   • Strike:   P1 near P0, P2 near P3 → ease-in-out with a steep middle,
    //               reading as anticipation → snap.
    //   • Retract:  P1 near P0, P2 ~ P3 → ease-out, the arm relaxes.
    private static float TipExtension(float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        float pb = -PullbackFrac * Reach;
        if (t < WindupEnd)
        {
            float u = t / WindupEnd;
            return Bezier.Cubic(0f, 0f, pb, pb, u);
        }
        if (t < StrikeEnd)
        {
            float u = (t - WindupEnd) / (StrikeEnd - WindupEnd);
            return Bezier.Cubic(pb, pb, Reach * 1.08f, Reach, u);
        }
        if (t < HoldEnd) return Reach;
        {
            float u = (t - HoldEnd) / (1f - HoldEnd);
            return Bezier.Cubic(Reach, Reach, 0f, 0f, u);
        }
    }

    // The stab tip is rendered as a glowing sphere + trail by Game1's glow pass
    // (GlowRenderer, its own PrimitiveBatch pass, not the telegraph list).
    // TipTrail/StabColorFor expose what it needs; nothing to telegraph here.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars) { }
}

// Air spin-stab. Roadmap §1.6: a Stab swipe pointed opposite of facing while in
// air. Inherits stab's hitboxes, dive-boost, and steer logic — the only delta is
// AllowBackward + an air+backward-swipe precondition + a Facing flip on Enter.
public class AirSpinStab : StabAction
{
    protected override bool AllowBackward => true;

    // Beat the default StabAction (30/30) on ties when both could fire.
    public override int PassivePriority => 35;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!base.CheckPreConditions(ctx, ab)) return false;
        // Air-only.
        if (ctx.TryGetGround(out _)) return false;
        // Backward swipe — intent direction's X must oppose Facing.
        if (!ctx.Intents.Peek(IntentType.Stab, ctx.CurrentFrame, out var intent)) return false;
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        return intent.Direction.X * facing < 0f;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Flip facing so the spin-stab leaves the player oriented the new way.
        // Done BEFORE base.Enter so any facing-derived fallback in capture
        // matches the new direction.
        ab.Facing = -(ab.Facing == 0 ? 1 : ab.Facing);
        base.Enter(ctx, ab, ref vars);
    }
}

// ---------- Guard — hold Shift to parry incoming hits ---------------------------

// Defensive posture. Held while Shift is down (with no L/R held — moving cancels
// the guard). Calls Combat.BeginGuard so PlayerCharacter.OnHit's guard path can
// run; applies a slowdown to walk/air speeds; draws a small shield indicator.
//
// Guard is a TIMED parry, not a shield (CombatState.ResolveGuard): the stance only
// absorbs a hit outright in the brief window right after it comes up, and leaks
// progressively more the longer Shift is held, saturating at three quarters of the
// percent and half the knockback. Anything that leaks through also BREAKS the guard,
// which is why entry is refused while Combat.GuardBroken — the recovery countdown
// after a break, which additionally requires the button to be released, so holding
// Shift through a break can't farm fresh perfect windows. A shorter cooldown
// (Combat.GuardOnCooldown) follows every ordinary deactivation for the mirror-image
// reason: without it, mashing Shift would hand out a fresh window per press.
//
// A clean block spends the stance too — but it is the one deactivation that refunds
// the cooldown (Combat.GuardBlockRefund), so guard can come straight back up for the
// next hit. Blocking a flurry is a sequence of reads, not one button held down.
//
// A weak in-cone hit absorbed inside the perfect window sets Combat.GuardCharged,
// arming GuardRetaliateAction (LMB-press while charged → fast forward slash).
// Air-allowed per user note in the roadmap §9: yes, allow guard in air. The
// slowdown via modifiers is identical air-vs-ground; no separate movement state.
public class GuardAction : ActionState
{
    // How deep into a recovery countdown guard may fire: the defensive move gets
    // the TAIL of any recovery (attack re-arm stays the full countdown). This is
    // also what the eviction lookahead keys off — a stab wind-up's short evict
    // stamp lands inside this window, so guard comes out almost immediately.
    private const float MaxEntrySeconds = 0.2f;

    public override int ActivePriority  => 35;   // beats slash candidates (30)
    public override int PassivePriority => 40;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift)        return false;
        if (ctx.Input.Left || ctx.Input.Right) return false;  // no activation while pushing L/R
        if (ctx.Input.RightClick)    return false;            // Shift+RMB is the build gesture
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ctx.Combat?.GuardBroken  == true) return false;   // still recovering from a break
        // ...and a brief lockout after ANY deactivation, so guard can't be mashed into
        // continuous perfect-window coverage.
        if (ctx.Combat?.GuardOnCooldown(ctx.CurrentFrame) == true) return false;
        // Strict from-set: neutral or the tail of recovery — never over a live
        // action (reaching one is the eviction lookahead's call, not a priority race).
        if (!EntryOk(ctx, SimFrames.FromSeconds(MaxEntrySeconds, ctx.Dt))) return false;
        return true;
    }

    // Guard yields to Shift+RMB in both directions. Without this, Guard's Passive 40
    // buries BlockReadyAction (Passive 10) — which is capped low on purpose so an attack
    // can always cancel a charge — and the eruption gesture could never start. Building
    // isn't a guard stance, so declining is the honest reading rather than a workaround.
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (!ctx.Input.Shift) return false;
        if (ctx.Input.Left || ctx.Input.Right) return false;
        if (ctx.Input.RightClick) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        // A break drops the stance the same frame it happens, so the shield indicator
        // disappears on contact instead of lingering over a guard that isn't guarding.
        if (ctx.Combat?.GuardBroken  == true) return false;
        // ...and so does a clean block, which spends the stance rather than holding it.
        // The refund it leaves behind means the precondition scan can bring guard back
        // on the very next frame while Shift is still down.
        if (ctx.Combat?.GuardActive  == false) return false;
        return true;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ctx.Combat?.BeginGuard(ctx.CurrentFrame);
        vars.Facing = ab.Facing == 0 ? 1 : ab.Facing;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ctx.Combat?.EndGuard(ctx.CurrentFrame, ctx.Dt);
    }

    // Slow walk, slower air. Gravity normal — guard doesn't levitate.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.5f;
        m.WalkAccel    *= 0.5f;
        m.MaxAirSpeed  *= 0.8f;
        m.AirAccel     *= 0.8f;
    }

    // Track facing for the telegraph. ResolveGuard tests the cone against the LIVE
    // ab.Facing, so mirroring it every frame keeps the drawn arc honest even if
    // something else turns the player mid-stance.
    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.Facing = ab.Facing == 0 ? 1 : ab.Facing;
    }

    // How far out the cone arc sits, and how thick it reads.
    private const float ArcRadius    = PlayerCharacter.Radius * 1.6f;
    private const float ArcThickness = 2f;

    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        // Shield indicator above the head. Charged-state cue is the
        // GuardRetaliateAction firing on click, not a Draw tint (Draw doesn't
        // have ab and we'd rather not thread a static through for visuals).
        const int W = 4;
        const int H = 12;
        var pos = body.Position;
        t.Box((int)pos.X - W / 2, (int)pos.Y - (int)PlayerCharacter.Radius - H - 2, W, H,
              Color.LightSteelBlue * 0.8f);

        // ...and the covered arc, so "protected from WHERE" is readable at a glance
        // instead of learned by getting hit from behind. Same 120° cone ResolveGuard
        // tests (CombatState.GuardConeHalfAngle), centred on facing — the arc is the
        // rule, not a decoration near it. Radial ticks at both ends close the wedge
        // visually so the edge of coverage is a hard line rather than a fade.
        int   facing = vars.Facing == 0 ? 1 : vars.Facing;
        float centre = facing < 0 ? MathF.PI : 0f;
        float half   = CombatState.GuardConeHalfAngle;
        var   color  = Color.LightSteelBlue * 0.8f;
        t.Arc(pos, ArcRadius, centre, half, color, segments: 12, thickness: ArcThickness);
        for (int i = -1; i <= 1; i += 2)
        {
            var dir = new Vector2(MathF.Cos(centre + i * half), MathF.Sin(centre + i * half));
            t.Line(pos + dir * (ArcRadius - 4f), pos + dir * (ArcRadius + 2f), color, ArcThickness);
        }
    }
}

// ---------- GuardRetaliate — fast counter from a charged parry ------------------

// Fires when LMB is pressed during the GuardCharged window. A forward slash with
// short duration, narrow sweep, big knockback. Consumes the charge on Enter so
// it doesn't refire while the click is held. Higher passive priority than the
// regular slashes so the click goes here instead of a normal GroundSlash1.
public class GuardRetaliateAction : SlashLikeAction
{
    protected override float Duration            => 0.10f;
    protected override float ArcRadiusScale      => 1.20f;
    protected override float SweepAngleDeg       => 70f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 280f;     // top of the slash kit — counters reward heavily
    protected override float StrikeSpeed         => 380f;
    protected override Color SlashColor          => Color.Cyan;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => false;

    public override int PassivePriority => 55;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Must be charged AND have a click intent. Don't gate on GuardActive
        // itself — the charge persists for a window even after the player
        // releases Shift (and lets go of Guard), so they can parry → release
        // → retaliate in a quick sequence.
        if (ctx.Combat?.GuardCharged != true) return false;
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        return true;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Consume the charge — one retaliate per parry. Set GuardCharged = false
        // BEFORE base.Enter so a held-click doesn't immediately re-enter from
        // CheckPreConditions (which would still see the click intent until base
        // consumes it).
        if (ctx.Combat != null) ctx.Combat.GuardCharged = false;
        base.Enter(ctx, ab, ref vars);
    }

    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.1f, f, dt);
    }
}

// ---------- Pulse — long-hold + circular drag → expanding ring -------------------

// Wide-area attack: N segment hitboxes arranged at a common radius around a captured
// origin point, the radius lerping from StartRadius → EndRadius across the active
// window. All segments share one HitId so an entity in the ring's path takes a
// single hit; tile damage is per-segment-per-frame (CombatSystem doesn't dedupe
// tiles), so a tile under any segment for a couple of frames breaks reliably.
public class PulseAction : ActionState
{
    private const float Duration             = 0.70f;
    private const float HitboxStartTime      = 0.15f;
    private const float HitboxActiveDuration = 0.40f;
    private const int   Segments             = 12;
    private const float StartRadius          = PlayerCharacter.Radius * 1.2f;
    private const float EndRadius            = PlayerCharacter.Radius * 5.0f;
    // Segment AABB half-size. Larger → fewer gaps between segments at full radius,
    // but more tile overlap per frame. ~70% of body radius is a clean balance.
    private const float SegmentHalfSize      = PlayerCharacter.Radius * 0.7f;
    // Impulse mode, so this is the raw impulse: 300 / Mass 2.5 = 120 px/s of shove,
    // and it stays just over the 280 stun threshold, so the pulse still stuns.
    private const float KnockbackMagnitude   = 300f;
    // Damage per frame matches SlashLikeAction.SlashDamagePerFrame so a sand tile
    // crumbles in one ring-pass and dirt cracks meaningfully — same feel as a slash.
    private const float DamagePerFrame       = TileDamage.TileMaxHP / 2f;


    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    private static Color PulseColorFor(bool grounded) => grounded ? Color.Gold : Color.Cyan;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ctx.Intents.Peek(IntentType.Circle, ctx.CurrentFrame, out _)
        && EntryOk(ctx);

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.IsGrounded  = ctx.TryGetGround(out _);
        vars.HitId       = ctx.HitIds.Next();
        ctx.Intents.Consume(IntentType.Circle, ctx.CurrentFrame);
    }

    // Long recovery — pulse is the biggest single attack, can't roll directly
    // into anything else.
    private const float RecoverySeconds    = 0.4f;
    private const float WindupEvictSeconds = 0.10f;

    // Same envelope shape as StabAction: near-free cancel before the ring starts,
    // the full recovery price from the expansion on.
    public override int CommitProfile(EnvironmentContext ctx, in ActionVars vars)
        => vars.TimeInState < HitboxStartTime
            ? SimFrames.FromSeconds(WindupEvictSeconds, ctx.Dt)
            : SimFrames.FromSeconds(RecoverySeconds,    ctx.Dt);

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetFor(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame,
                              CommitProfile(ctx, in vars), ctx.CurrentFrame);
    }

    // Heavy stance throughout the pulse — applies on ground AND in air, unlike
    // Stab which leaves air movement mostly alone. Pairs with the gravity scale
    // to give a hovering "cast" feel mid-air.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed   *= 0.25f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= 1.5f;
        m.MaxAirSpeed    *= 0.25f;
        m.AirAccel       *= 0.5f;
        m.AirDrag        *= 1.5f;
        m.GravityScale   *= 0.3f;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;

        if (vars.TimeInState < HitboxStartTime ||
            vars.TimeInState > HitboxStartTime + HitboxActiveDuration ||
            ctx.Hitboxes == null) return;

        // Radius lerps from start → end across the active window.
        float u = (vars.TimeInState - HitboxStartTime) / HitboxActiveDuration;
        if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
        float r = MathHelper.Lerp(StartRadius, EndRadius, u);

        // Anchor to the player's CURRENT position each frame — the ring drifts
        // with the caster rather than hanging at the cast point. Each segment's
        // knockback also picks up the body's velocity so a moving caster imparts
        // their momentum to anything the ring sweeps.
        var anchor  = ctx.Body.Position;
        var bodyVel = ctx.Body.Velocity;

        var color = PulseColorFor(vars.IsGrounded);
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * MathHelper.TwoPi / Segments;
            var dir    = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var center = anchor + dir * r;
            var region = new BoundingBox(
                center.X - SegmentHalfSize, center.Y - SegmentHalfSize,
                center.X + SegmentHalfSize, center.Y + SegmentHalfSize);
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, DamagePerFrame,
                dir * KnockbackMagnitude + bodyVel,
                ctx.Faction, ctx.SelfId, color,
                origin: anchor));
        }
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        if (vars.TimeInState < HitboxStartTime ||
            vars.TimeInState > HitboxStartTime + HitboxActiveDuration) return;
        float u = (vars.TimeInState - HitboxStartTime) / HitboxActiveDuration;
        if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
        float r = MathHelper.Lerp(StartRadius, EndRadius, u);
        var color = PulseColorFor(vars.IsGrounded);
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * MathHelper.TwoPi / Segments;
            var pos = body.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
            t.Rect(pos, 4f, color);
        }
    }
}

// ---------- Force Burst — RMB while holding a Ready wind-up ---------------------

// The "shove" out of a wind-up: hold LMB (→ ReadyAction) and click RMB to detonate a
// burst in front of the body. Design intent is displacement, not damage —
// it clears terrain and throws whatever it touches, but takes very little HP off
// it, so it reads as a movement/space-making tool rather than a kill move.
//
// Two hitboxes per segment, sharing one HitId (same trick as StabAction):
//   • TilesOnly    — full TileMaxHP per frame, so dirt breaks on a single shell pass.
//   • EntitiesOnly — tiny HP contribution, big Impulse knockback.
// One box can't do both: Hitbox.Damage feeds the tile HP pool and the body damage
// pool alike, so "breaks blocks but tickles players" needs the split.
//
// Priority 30/30 matches the other attacks. Passive 30 clears the wind-up's Active 10
// (RecoveryAction's charge role), which is what lets it preempt the wind-up; the
// precondition below is what keeps it from stealing RMB from the build/eruption
// gesture (Passive 10) outside a wind-up.
public class BurstAction : ActionState
{
    private const float Duration             = 0.42f;
    // Fast detonation: the shell sweeps out over ~0.2s, well short of Pulse's 0.4s.
    private const float HitboxStartTime      = 0.06f;
    private const float HitboxActiveDuration = 0.22f;
    // 20 segments at EndRadius keeps the shell gap-free: spacing at r = 6R is
    // 2π·6R/20 ≈ 1.9R, just under each segment's 2R width.
    private const int   Segments             = 5;
    private const float StartDist          = PlayerCharacter.Radius * 1.5f;
    private const float EndDist            = PlayerCharacter.Radius * 2.5f;
    private const float SegmentHalfSize      = PlayerCharacter.Radius * 1.0f;
    // Hard shove — kept above GroundSlash3, since knockback is the whole point of
    // the move. Came down 700 → 430 with the rest of the kit, holding that ratio.
    private const float KnockbackMagnitude   = 430f;
    // Full tile HP per frame ⇒ dirt (1.0) breaks on one shell contact, stone (2.0)
    // needs the two frames the shell dwells over a cell.
    private const float TileDamagePerFrame   = TileDamage.TileMaxHP;
    // ~30% of a slash's damage: enough to register a hit, not enough to build a
    // kill. Bodies are HitId-deduped, so this lands exactly once.
    private const float EntityDamage         = 0.15f;
    // Declared rather than derived: the impulse is huge but the hit is meant to
    // displace, not to lock the victim down for a follow-up.
    private const float HitstunSeconds       = 0.20f;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    private static Color BurstColorFor(bool grounded) => grounded ? Color.Orange : Color.MediumTurquoise;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Must be mid-wind-up with LMB still down: the merged Recovery at index 0,
        // charging a live press. PreviousAction(0) is last frame's settled action,
        // so this needs the wind-up to have picked up the press first — LMB-then-RMB
        // in the same frame charges first and the burst lands a couple frames later.
        if (ctx.PreviousAction(0) is not RecoveryAction) return false;
        if (ctx.RecoveryIndex() != 0) return false;
        if (!ctx.Input.LeftClick) return false;
        if (!ctx.Intents.Peek(IntentType.PressEdge, ctx.CurrentFrame, out _)) return false;
        // RMB press-edge only, so a held right button (drag-build) doesn't re-fire.
        if (!ctx.Input.RightClick) return false;
        if (ctx.Controller == null || ctx.Controller.GetPrevious(1).RightClick) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        return true;
    }

        private Vector2 ComputeBurstDir(EnvironmentContext ctx, PlayerAbilityState ab)
        {
            int facing = ab.Facing == 0 ? 1 : ab.Facing;
            Vector2 raw = ctx.Input.MouseWorldPosition - ctx.Body.Position;
            if (raw.X * facing < 0f) raw.X = 0f;
            if (raw.LengthSquared() < 1e-4f) return new Vector2(facing, 0f);
            return Vector2.Normalize(raw);
        }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.IsGrounded  = ctx.TryGetGround(out _);
        vars.HitId       = ctx.HitIds.Next();
        vars.AttackDir   = ComputeBurstDir(ctx, ab); 
        // Eat the wind-up's press so releasing LMB after the burst can't also
        // spend the gesture on a slash.
        ctx.Intents.Consume(IntentType.PressEdge, ctx.CurrentFrame);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, 0.30f, ctx.CurrentFrame, ctx.Dt);
    }

    // Planted stance while the shell goes out — the player is bracing against the
    // shove, not steering through it.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed   *= 0.3f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= 1.5f;
        m.MaxAirSpeed    *= 0.3f;
        m.AirDrag        *= 1.5f;
        m.GravityScale   *= 0.4f;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;

        if (vars.TimeInState < HitboxStartTime ||
            vars.TimeInState > HitboxStartTime + HitboxActiveDuration ||
            ctx.Hitboxes == null) return;

        float r = BurstDist(vars.TimeInState);
        // Anchored to the CURRENT body position, like Pulse — the shell rides with
        // the caster instead of hanging at the detonation point.
        var anchor  = ctx.Body.Position + PlayerCharacter.Radius * vars.AttackDir;
        var bodyVel = ctx.Body.Velocity;
        var color   = BurstColorFor(vars.IsGrounded);
        var tileColor = Color.Lerp(color, Color.Gray, 0.4f);
        var angleSpread = (float) 0.07 * MathHelper.TwoPi; 
        var anchorTheta = MathF.Atan2(vars.AttackDir.Y, vars.AttackDir.X);

        for (int i = 0; i < Segments; i++)
        {
            float angle = i * 2 * angleSpread / Segments - angleSpread + anchorTheta;
            var dir    = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var center = anchor + dir * r;
            var region = new BoundingBox(
                center.X - SegmentHalfSize, center.Y - SegmentHalfSize,
                center.X + SegmentHalfSize, center.Y + SegmentHalfSize);

            // Terrain channel — breaks blocks, imparts nothing.
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, TileDamagePerFrame,
                Vector2.Zero,
                ctx.Faction, ctx.SelfId, tileColor,
                HitTargets.TilesOnly,
                origin: ctx.Body.Position));

            // Knockback channel — radial shove plus the caster's momentum. Impulse
            // mode (not Collision): this is an AoE field, so every target in the
            // shell should get the same push regardless of closing speed.
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, EntityDamage,
                dir * KnockbackMagnitude + bodyVel,
                ctx.Faction, ctx.SelfId, color,
                HitTargets.EntitiesOnly,
                hitstunSecondsOverride: HitstunSeconds,
                origin: ctx.Body.Position));
        }
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        if (vars.TimeInState < HitboxStartTime ||
            vars.TimeInState > HitboxStartTime + HitboxActiveDuration) return;
        float r = BurstDist(vars.TimeInState);
        var color = BurstColorFor(vars.IsGrounded);
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * MathHelper.TwoPi / Segments;
            var pos = body.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
            t.Rect(pos, 5f, color);
        }
    }

    // Shell radius at state-time `t`. Eased out (1-(1-u)²) so the burst leaves the
    // body fast and decelerates at the rim — reads as a detonation rather than the
    // linear ring sweep Pulse uses.
    private static float BurstDist(float t)
    {
        float u = MathHelper.Clamp((t - HitboxStartTime) / HitboxActiveDuration, 0f, 1f);
        float eased = 1f - (1f - u) * (1f - u);
        return MathHelper.Lerp(StartDist, EndDist, eased);
    }
}


// ---------- Block Paint — plain RMB: paint outside terrain, charge inside ---------

// Plain RMB does two jobs, and which one depends entirely on where the cursor is:
//   cursor OUTSIDE solid — paint. A ball trails the cursor with inertia, leaking mass
//     into the cell underneath; TileMassField's cascade turns it into sprouts. Because
//     the ball lags and mass spills, a stroke lays down a mound that thickens where you
//     slow down instead of a 1-cell ribbon.
//   cursor INSIDE solid  — charge. Nothing is placed; BuildMeters converts reservoir into
//     eruption charge (see BuildMeters.StepCharging).
//
// That in-solid/out-of-solid split is doing more work than it looks. It's what keeps the
// two gestures from colliding: painting only happens in open space, charging only inside
// terrain, so they're mutually exclusive by construction rather than by a modifier key.
//
// The ERUPTION is a release-time upgrade of this same stroke, not a separate state:
// when a charged hold leaves the ground it simply starts painting (nothing is lost by
// waiting — banked charge decays back into paintable budget), and if RMB is released
// while the ball is moving fast enough, Exit detaches the live paint ball as a
// free-flying MassBall carrying the banked charge. The discriminator is the conjunction
//   (a) real charge banked      — you spent time biting into terrain (EruptMinToFire),
//   (b) a fast release          — the ball is genuinely moving when the button comes up.
// Ordinary painting satisfies neither by default: no time in solid means no charge, so
// a release is just a release. That's the answer to "don't erupt every time I let go
// while painting." (An earlier design put the eruption in its own action armed at the
// solid→air crossing; it stole the stroke for its arming window and a lapsed window
// forfeited the mound, which playtested as pure annoyance.)
//
// Critically damped, so the ball never crosses the cursor — overshoot would deposit on
// the wrong side of the stroke, which reads as the build fighting your aim.
public class BlockPaintAction : ActionState
{
    // Ball lag time constant. Long enough to see the trail bend behind a fast stroke,
    // short enough that the deposit still lands where you're pointing.
    private const float SmoothTime    = 0.12f;
    // Baseline demand, in tiles/sec — what a parked cursor pours into one cell. The METER
    // is the real limiter for expensive material (stone lands ~4/sec); this caps how fast
    // cheap material can spray, and is what the painter's feel was tuned against before
    // costs existed.
    private const float TilesPerSecond = 8f;
    // Mass laid down per cell of ball travel, in tile-equivalents. Demand scales with
    // ball speed so a stroke's line density is speed-invariant — a fast flick spends
    // more per second instead of smearing mass too thin for any cell to reach the
    // sprout threshold (1.0). Above threshold so painted cells solidify with spill to
    // spare.
    private const float MassPerCell    = 6f;
    // Build reach (px) from the player center; a sprout/solid neighbour on the target
    // cell extends it so a build can chain outward. Same numbers the old drag paint
    // used, carried over from Simulation.HandleBuildInput before it.
    // Internal because BlockBurstAction needs the same reach to decide whether a held
    // RMB is actually placing at the cursor — one constant so the two can't disagree.
    internal const float BuildReach   = 64f;
    private const float ChainReachMul = 2f;
    // Minimum ball speed at RELEASE for the stroke to upgrade into an eruption. Measured
    // on the ball (not the raw cursor) at the moment the button comes up — after even a
    // short sustained sweep the damped ball genuinely carries this speed, unlike at the
    // solid→air crossing where it always lags near zero.
    private const float EruptReleaseSpeed = 220f;

    public override int ActivePriority  => 8;
    public override int PassivePriority => 10;
    // Freely interruptible hold — entrants fire over it as if from neutral (an
    // attack can always cancel a build), reading the countdown straight through.
    public override bool NeutralForEntry => true;

    // Unbounded hold → no meaningful progress fraction, so the clip must loop.
    public override float AnimationProgress(in ActionVars vars) => -1f;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Plain RMB, no press-edge requirement: this is the resting state of a held right
        // button, so it also resumes after an attack interrupted a stroke.
        if (!ctx.Input.RightClick || ctx.Input.Shift) return false;
        return ctx.Combat?.HitstunActive != true;
    }

    // Alive while RMB is held. Deliberately not gated on Shift: tapping Shift midway
    // through a stroke shouldn't tear the ball away.
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => ctx.Input.RightClick;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.BallPos     = ctx.Input.MouseWorldPosition;   // seeded under the cursor
        vars.BallVel     = Vector2.Zero;
        // The mode is decided HERE and does not get re-derived per frame: cursor buried at
        // the press means this hold is a charge, open air means it's a paint stroke —
        // UNLESS there's already a charge banked, in which case the hold is a charge
        // wherever the cursor is. Painting draws from EruptMove once the working pool is
        // dry, so a press in open air with a live charge used to spend it by accident.
        vars.ChargeGesture = BlockEruptionHelpers.IsCursorInSolid(ctx)
                          || ab.Meters.ChargeLocksPaint;

        // Second click of a double-click, on a block, with a full meter banked: spend the
        // whole charge to mark that block instead. Rides this action's Enter rather than
        // living in an ActionState of its own because it is instantaneous — no duration to
        // animate, no slot to hold — and the hold it arrives on is already this action's.
        // A charged block still starts a charging hold from here, so a player who keeps
        // the button down after the second click begins refilling immediately.
        BlockEruptionHelpers.TryChargeBlock(ctx, ab);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
        SmoothPen.CriticallyDampedStep(ref vars.BallPos, ref vars.BallVel,
                                       ctx.Input.MouseWorldPosition, SmoothTime, ctx.Dt);

        if (!vars.ChargeGesture) { Paint(ctx, ab, vars.BallPos, vars.BallVel.Length()); return; }

        if (BlockEruptionHelpers.IsCursorInSolid(ctx) || ab.Meters.ChargeLocksPaint)
        {
            // Charging: BuildMeters converts reservoir into eruption charge this frame.
            // Out in open air too, once there's a charge worth protecting — the meter
            // keeps filling instead of the hold demoting into a stroke that spends it.
            ab.Meters.ChargingRequested = true;
        }
        else
        {
            // The cursor left the ground with nothing banked: the same hold simply becomes
            // a paint stroke, keeping the ball it already has. Whether this stroke ends as
            // an eruption is decided at RELEASE (see Exit), not here — so nothing is
            // forfeited by sweeping around first. The demotion is deliberately one-way; a
            // per-frame mode test would flip back to charging the instant the first
            // painted tile under the cursor finalizes.
            vars.ChargeGesture = false;
        }
    }

    // A natural release (RMB up) upgrades a charged, fast-moving stroke into the
    // eruption: the live paint ball detaches as a free-flying MassBall carrying the
    // banked charge. A preempt (attack stealing the slot) leaves RMB held and skips the
    // upgrade — the stroke resumes via CheckPreConditions and the charge stays banked.
    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (ctx.Input.RightClick) return;
        // Released while still buried mid-charge: bank the charge for a later stroke
        // (it decays in BuildMeters) rather than erupting from inside the ground.
        //
        // Tested on the CURSOR rather than vars.ChargeGesture, which used to be the same
        // question: a charged hold no longer demotes on leaving the ground (see Update),
        // so ChargeGesture now stays true through exactly the airborne release that ought
        // to erupt. The cursor is what the check always meant — "am I still in the wall?"
        if (BlockEruptionHelpers.IsCursorInSolid(ctx)) return;
        if (!ab.Meters.CanFireEruption) return;
        if (vars.BallVel.Length() < EruptReleaseSpeed) return;

        // Charged blocks around the launch site cash themselves into this eruption. Done
        // only once every other gate has passed, so a release that doesn't erupt never
        // silently discharges the wall the player was saving.
        int   recruited = BlockEruptionHelpers.RecruitChargedBlocks(ctx.Chunks, vars.BallPos);
        float mass = ab.Meters.ConsumeEruptionMass(ctx.ActiveBlockType,
                                                   recruited * BuildMeters.EruptMax);
        if (mass <= 0f || ctx.Spawner == null) return;
        ctx.Spawner.SpawnEntity(new MassBall(
            vars.BallPos, vars.BallVel, mass, ctx.ActiveBlockType, ctx.Faction));
    }

    private static void Paint(EnvironmentContext ctx, PlayerAbilityState ab, Vector2 ballPos, float ballSpeed)
    {
        int gtx = (int)MathF.Floor(ballPos.X / Chunk.TileSize);
        int gty = (int)MathF.Floor(ballPos.Y / Chunk.TileSize);
        var cell = BlockEruptionHelpers.CellCenter(gtx, gty);

        // Reach is measured to the ball's cell, not the cursor — the ball is what's
        // actually depositing, so a stroke flicked out of range stops where it lags to.
        float maxReach = HasSproutNeighbour(ctx.Chunks, gtx, gty)
            ? BuildReach * ChainReachMul : BuildReach;
        if (Vector2.DistanceSquared(ctx.Body.Position, cell) > maxReach * maxReach) return;

        // Ask for the demand rate, get back what the meters could actually fund, and emit
        // exactly that. Paying at emission (rather than on commit) means mass that flows
        // into unsupported air and dies is still charged for — which is visible to the
        // player, since nothing appears.
        float rate = MathF.Max(TilesPerSecond, MassPerCell * ballSpeed / Chunk.TileSize);
        float want = rate * ctx.Dt;
        float paid = ab.Meters.SpendForTiles(want, ctx.ActiveBlockType);
        if (paid <= 0f) return;

        ctx.Chunks.Mass.Deposit(ctx.Chunks, gtx, gty, paid, ctx.ActiveBlockType);
    }

    internal static bool HasSproutNeighbour(ChunkMap chunks, int gtx, int gty) =>
        chunks.Graph.TryGet(gtx,     gty + 1, out _) ||
        chunks.Graph.TryGet(gtx - 1, gty,     out _) ||
        chunks.Graph.TryGet(gtx + 1, gty,     out _) ||
        chunks.Graph.TryGet(gtx,     gty - 1, out _);

    // Ordinary building stays nimble — no stance penalty. The heavy stance belongs to the
    // charge, which is the committed half of the gesture.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.85f;
        m.MaxAirSpeed  *= 0.85f;
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        // The ball, plus a bright core, so the lag behind the cursor is legible.
        t.Rect(vars.BallPos, 7f, new Color(230, 200, 140));
        t.Rect(vars.BallPos, 3f, Color.White);
    }
}

// ---------- Block Place — Shift+RMB, one deliberate block at a time --------------

// The precise counterpart to the painter: no ball, no cascade, no partial mass. One tile
// per cell, paid for in full, placed the moment the cursor enters a new cell. Use it to
// finish an edge the mound rounded off, or to spend the reservoir carefully instead of
// spraying it.
//
// Deliberately bypasses TileMassField entirely. Accumulating fractional mass is what
// gives the painter its organic shape; for single placement that same behaviour would be
// a bug (a tile that appears one frame after you clicked, one cell off where you aimed).
public class BlockPlaceAction : ActionState
{
    private const float Reach         = BlockPaintAction.BuildReach;
    private const float ChainReachMul = 2f;

    public override int ActivePriority  => 8;
    public override int PassivePriority => 10;
    // Same as BlockPaintAction: an interruptible hold, transparent to entrants.
    public override bool NeutralForEntry => true;

    public override float AnimationProgress(in ActionVars vars) => -1f;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.RightClick || !ctx.Input.Shift) return false;
        return ctx.Combat?.HitstunActive != true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => ctx.Input.RightClick && ctx.Input.Shift;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        // Sentinel so the press frame itself places: no cell has been placed yet.
        vars.OriginCell = new Vector2(float.NaN, float.NaN);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;

        var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
        var cell = BlockEruptionHelpers.CellCenter(gtx, gty);
        // One placement per cell entered, so holding the button and dragging lays a
        // single-tile line rather than re-requesting the same cell every frame.
        if (cell == vars.OriginCell) return;

        float maxReach = BlockPaintAction.HasSproutNeighbour(ctx.Chunks, gtx, gty)
            ? Reach * ChainReachMul : Reach;
        if (Vector2.DistanceSquared(ctx.Body.Position, cell) > maxReach * maxReach) return;

        if (!ab.Meters.CanAfford(ctx.ActiveBlockType)) return;
        if (ctx.Chunks.TryRequestTile(gtx, gty, ctx.ActiveBlockType) == null) return;

        // Charge only for a placement that actually took.
        ab.Meters.SpendForTiles(1f, ctx.ActiveBlockType);
        vars.OriginCell = cell;
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        // Nothing to show — the placed tile is the feedback.
    }
}

// ---------- Block Burst — LMB while holding RMB over dead air -------------------

// The block-side twin of BurstAction: where that one is LMB-hold → RMB-click and shoves
// terrain away, this is RMB-hold → LMB-click and conjures terrain out of nothing — a
// plus-shaped puff of foam (cursor cell + its 4 neighbours) in open air. Foam is the
// right material for it: half of dirt's HP and it decays on its own, so a free
// mid-air platform is scaffolding rather than permanent level editing.
//
// Two things guard against stealing a press that RMB was already spending:
//   • Precondition — declines whenever the cursor cell is one the drag-build could
//     actually paint (in reach, free, touching support). That is exactly ChunkMap's
//     placement rule, so "not placing a block" is decided by the same predicate
//     BlockReadyAction paints with rather than by a guess.
//   • Phase gate — the RMB gesture must currently be BlockPaintAction (plain painting).
//     A Shift+RMB charge, an armed flag, or a running eruption all decline the click.
//     That covers the case the predicate can't see: cursor over air, nothing placeable
//     there, but RMB is mid-eruption.
//
// Priority can NOT do that job here: the wind-up (RecoveryAction's charge role, Passive
// 45) contests the press one frame later, and the FSM scan takes the single highest-
// Passive candidate — the press FRAME itself is what this action must win, which its
// 30/30 does only because the wind-up deliberately sits the press frame out. Hence the
// explicit gates above rather than a priority arms race.
public class BlockBurstAction : ActionState
{
    private const float Duration      = 0.26f;
    // Where the foam appears. Beyond BlockReadyAction.BuildReach on purpose — this is
    // the ranged option, so it stays useful past where drag-building gives out.
    // Px, deliberately independent of tile size: calibrated against the player and
    // combat spacing, not the grid.
    private const float BurstReach    = 128f;
    private const float RecoverySeconds = 0.18f;
    // Mass dropped on the (force-sprouted) center cell. Denominated in tile-equivalents,
    // so it rescales with the grid: 4 tiles at the old 16px grid = 8.5 at the 11px grid;
    // same physical volume. At the old grid that was exactly one unit per neighbour once
    // the center forwards them, i.e. the plus. See Enter.
    private const float MassInjection = 8.5f * TileMassField.Threshold;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // The RMB gesture must be plain painting — anything else owns the button, so
        // keep hands off. (The eruption is a release-time upgrade of the paint stroke
        // now, so "painting" covers a charged stroke too; that's fine, the burst needs
        // LMB while the eruption is decided by how RMB comes up.)
        if (ctx.PreviousAction(0) is not BlockPaintAction) return false;
        // RMB held (not the press-edge — that frame belongs to BlockReady) + LMB press-edge.
        if (!ctx.Input.RightClick || !ctx.Input.LeftClick) return false;
        if (ctx.Controller == null || ctx.Controller.GetPrevious(1).LeftClick) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ctx.Combat?.HitstunActive == true) return false;
        if (ab.Condition.RecoveryActive) return false;

        var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
        var center = BlockEruptionHelpers.CellCenter(gtx, gty);
        float distSq = Vector2.DistanceSquared(ctx.Body.Position, center);
        if (distSq > BurstReach * BurstReach) return false;
        // Empty air only — a cursor in solid is the eruption gesture's territory.
        if (ctx.Chunks.GetCellState(gtx, gty) != TileState.Empty) return false;
        // If the drag-build could paint here, RMB is placing: leave the press alone.
        if (distSq <= BlockPaintAction.BuildReach * BlockPaintAction.BuildReach &&
            ctx.Chunks.CanRequestTile(gtx, gty)) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
        vars.OriginCell = BlockEruptionHelpers.CellCenter(gtx, gty);

        // The center goes in unsupported — ForceSprout is the only path that conjures
        // matter with nothing to build from, which is the whole point of this move. The
        // arms are then left to the mass cascade: injecting MassInjection units at the
        // (now occupied) center makes it forward one unit at a time to each neighbour,
        // and those commit off the center once they have a full unit. So the plus isn't
        // hardcoded — it's the shape four units of mass takes when it flows out of a
        // filled cell. Raise MassInjection and it grows a fatter blob for free.
        ctx.Chunks.ForceSprout(gtx, gty, TileType.Foam);
        ctx.Chunks.Mass.Deposit(ctx.Chunks, gtx, gty, MassInjection, TileType.Foam);

        // Spend the click so releasing LMB afterwards can't also buy a slash.
        ctx.Intents.Consume(IntentType.PressEdge, ctx.CurrentFrame);
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState += ctx.Dt;

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }

    // Light planted stance — a flick of the wrist, not a commitment.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.6f;
        m.WalkAccel    *= 0.7f;
        m.MaxAirSpeed  *= 0.7f;
    }

    // A plus of expanding foam-white brackets over the target cells — the placement is
    // already visible as sprouting tiles, so this just marks the shape that was chosen.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        float u = MathHelper.Clamp(vars.TimeInState / Duration, 0f, 1f);
        var color = Color.Lerp(new Color(235, 245, 255), Color.Transparent, u);
        float half = Chunk.TileSize * 0.5f * (0.4f + 0.6f * u);
        Span<Vector2> cells = stackalloc Vector2[5]
        {
            vars.OriginCell,
            vars.OriginCell + new Vector2(0f, -Chunk.TileSize),
            vars.OriginCell + new Vector2(Chunk.TileSize, 0f),
            vars.OriginCell + new Vector2(0f, Chunk.TileSize),
            vars.OriginCell + new Vector2(-Chunk.TileSize, 0f),
        };
        foreach (var c in cells)
            t.Rect(c, half * 2f, color);
    }
}

// ---------- Block Eruption — the primed window between flick and release ---------

// Priority arrangement:
//   BlockPaint     Active 8,  Passive 10
//   BlockPlace     Active 8,  Passive 10
// Both sit with Passive ≤ 10 so an attack (Passive 15) can always cancel out of a build
// or a charge. (BlockEruptionAction is gone — the eruption is BlockPaintAction's
// release-time upgrade, see its Exit — so there is no arming window for an attack to
// race anymore.)

internal static class BlockEruptionHelpers
{
    // True if the cell under the cursor is currently solid. Sprouting cells do
    // NOT count — the move is for shoving *out of* committed terrain, not for
    // chaining off your own growing sprouts.
    public static bool IsCursorInSolid(EnvironmentContext ctx)
    {
        var p = ctx.Input.MouseWorldPosition;
        int gtx = (int)MathF.Floor(p.X / Chunk.TileSize);
        int gty = (int)MathF.Floor(p.Y / Chunk.TileSize);
        return ctx.Chunks.GetCellState(gtx, gty) == TileState.Solid;
    }

    public static (int gtx, int gty) CursorCell(EnvironmentContext ctx)
    {
        var p = ctx.Input.MouseWorldPosition;
        return ((int)MathF.Floor(p.X / Chunk.TileSize),
                (int)MathF.Floor(p.Y / Chunk.TileSize));
    }

    public static Vector2 CellCenter(int gtx, int gty)
        => new Vector2(
            gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
            gty * Chunk.TileSize + Chunk.TileSize * 0.5f);

    // ── Block charge (double-RMB on a block, paid for with the whole meter) ──────
    //
    // The second spend for a banked avalanche charge. The first is the eruption (release
    // a fast stroke); this one is the opposite gesture in every way — stationary, aimed
    // at one cell, and all-or-nothing — so it is worth the whole meter and a gate well
    // above the eruption's.
    //
    // Detected off the Controller ring rather than a latched timer, for the same reason
    // everything else in the sim is: a rollback that rewinds the input rewinds the
    // gesture with it, and there is no half-armed double-click left stranded across a
    // reconcile. The ring is 32 frames, so the whole window has to fit inside that.

    // Max frames the first click may be held. A double-click is two CLICKS: this is what
    // rejects the far more common sequence of a long charging hold, a release, and one
    // ordinary press — which is otherwise indistinguishable from click-release-click.
    private const int DoubleClickHoldFrames = 12;   // 0.2s
    // Max release gap between the two clicks.
    private const int DoubleClickGapFrames  = 12;   // 0.2s

    // True on the exact frame RMB goes down for the second time in a double-click.
    public static bool IsRightDoubleClick(EnvironmentContext ctx)
    {
        var ctrl = ctx.Controller;
        if (ctrl == null) return false;
        if (!ctx.Input.RightClick) return false;
        if (ctrl.GetPrevious(1).RightClick) return false;      // not a press edge

        // Walk back through the gap, then through the first click's hold. Running off
        // either budget means this press isn't the second half of a double-click.
        int i = 1;
        for (; i <= DoubleClickGapFrames; i++)
            if (ctrl.GetPrevious(i).RightClick) break;
        if (i > DoubleClickGapFrames) return false;            // gap too long / no prior press

        // i is now the newest down-frame of the first click; look for where that press
        // began. Finding its rising edge inside the budget means it was a click.
        int end = i + DoubleClickHoldFrames;
        for (i++; i <= end; i++)
            if (!ctrl.GetPrevious(i).RightClick) return true;  // first click ended in time
        return false;                                          // it was a hold, not a click
    }

    // Spend the full avalanche meter to charge the solid cell under the cursor. No-op
    // (and no spend) if the gesture isn't a double-click, the cell isn't solid, the cell
    // is already charged, or the meter is short. Returns true if a block was charged.
    //
    // No reach gate, deliberately: building the charge has none either — you can bury the
    // cursor in any wall on screen and hold — so gating the spend would make the meter
    // fillable at range and spendable only up close.
    public static bool TryChargeBlock(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (ctx.Chunks == null || ab?.Meters == null) return false;
        if (!IsRightDoubleClick(ctx)) return false;

        var (gtx, gty) = CursorCell(ctx);
        if (ctx.Chunks.GetCellState(gtx, gty) != TileState.Solid) return false;
        if (ctx.Chunks.Charge.IsCharged(gtx, gty)) return false;   // don't pay twice
        if (!ab.Meters.TrySpendForBlockCharge()) return false;

        ctx.Chunks.Charge.Set(gtx, gty);
        return true;
    }

    // ── Charged-block recruitment (the eruption's side of the bargain) ───────────
    //
    // An eruption fired near charged blocks pulls their charge into itself. This is the
    // second use for a charge and the only way an eruption gets bigger than the meter:
    // each recruited block throws a WHOLE EruptMax into the pot on top of whatever was
    // banked, so a wall staged with two charges erupts three meters wide.
    //
    // The blocks DISCHARGE but are not broken. An eruption is a building verb — having
    // it eat the wall the player spent four seconds charging would fight the thing it
    // is for — and the tint going out is the feedback that the charge was spent.
    //
    // Scanned row-major over the bounding square in ascending cell order, so the count
    // (and therefore the ball's mass) is identical on both peers and across a rollback
    // replay.
    // Px, deliberately independent of tile size (4.5 tiles at the old 16px grid).
    private const float EruptRecruitRadiusPx = 72f;

    // Discharge every charged cell within the recruit radius of `at`; returns how many.
    // Callers multiply by BuildMeters.EruptMax to get the charge units contributed.
    public static int RecruitChargedBlocks(ChunkMap chunks, Vector2 at)
    {
        if (chunks == null) return 0;

        int   cx     = (int)MathF.Floor(at.X / Chunk.TileSize);
        int   cy     = (int)MathF.Floor(at.Y / Chunk.TileSize);
        int   span   = (int)MathF.Ceiling(EruptRecruitRadiusPx / Chunk.TileSize);
        float r2     = EruptRecruitRadiusPx * EruptRecruitRadiusPx;

        int taken = 0;
        for (int dy = -span; dy <= span; dy++)
        for (int dx = -span; dx <= span; dx++)
        {
            int gtx = cx + dx, gty = cy + dy;
            if (!chunks.Charge.IsCharged(gtx, gty)) continue;
            if (Vector2.DistanceSquared(CellCenter(gtx, gty), at) > r2) continue;
            chunks.Charge.Clear(gtx, gty);
            taken++;
        }
        return taken;
    }
}


// (BlockEruptionAction lived here — a 0.35 s armed window entered at the solid→air
// crossing, firing on release. Retired: it stole the stroke from the painter for its
// window and a lapsed window forfeited the mound. The eruption is now BlockPaintAction's
// release-time upgrade — banked charge + fast ball at RMB-up detaches the live paint
// ball as the MassBall.)

// ---------- Ranged: EnergyBall (Shift + LMB tap) --------------------------------

// Roadmap §4.1. Short action that spawns one EnergyBallProjectile toward the
// cursor and sets a brief recovery. Priority sits ABOVE GuardAction's Active 35
// so a Shift+click during a guard stance momentarily preempts the guard to fire,
// then the FSM re-evaluates and Guard re-arms on the next frame.
public class EnergyBallAction : ActionState
{
    private const float Duration        = 0.15f;
    private const float RecoverySeconds = 0.133f;
    // Distance ahead of the player center where the projectile spawns. Keeps
    // the ball from immediately overlapping the player's body/hurtbox.
    private const float SpawnOffset    = PlayerCharacter.Radius * 1.2f;

    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift) return false;
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)    return false;
        // From-set: neutral/recovery, or straight out of a live Guard (the Shift+
        // click during a guard stance — this action's documented role).
        if (ctx.RecoveryIndex() == null && ctx.PreviousAction(0) is not GuardAction) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);

        if (ctx.Spawner == null) return;
        Vector2 toCursor = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        Vector2 dir = toCursor.LengthSquared() < 1e-4f
            ? new Vector2(ab.Facing == 0 ? 1f : ab.Facing, 0f)
            : Vector2.Normalize(toCursor);
        var spawnPos = ctx.Body.Position + dir * SpawnOffset;
        ctx.Spawner.SpawnEntity(new EnergyBallProjectile(spawnPos, dir, ctx.HitIds.Next(), ctx.Faction));
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }
}

// ---------- Ranged: Beam (Shift + LMB hold) -------------------------------------

// Roadmap §4.2 — sustained particle beam. Shift+LMB press-edge starts charging;
// once _chargeTime ≥ MinChargeTime AND LMB still held, the beam fires every
// frame for up to MaxFiringTime. Release LMB at any point during charge → no
// beam fires (the user's "fails when interrupted early" requirement makes the
// move non-spammable).
//
// Why not piggy-back on Stab intent (which is the roadmap's original gesture
// suggestion): Stab fires on RELEASE, after a long hold + swipe. We want the
// beam visible DURING the hold so the player can sweep it across targets while
// firing. Press-edge + per-frame LMB poll matches the intended feel.
//
// Click coexistence: when a short Shift+LMB tap releases inside the charge
// window, BeamAction.CheckConditions returns false (LMB released), BeamAction
// exits without firing, and the same release frame's Click intent routes to
// EnergyBallAction. So short Shift+LMB = energy ball, long Shift+LMB = beam.
public class BeamAction : ActionState
{
    private const float MinChargeTime    = 0.35f;
    private const float MaxFiringTime    = 0.55f;
    // Hard cap on reach in world pixels. The beam now marches in fixed StepSize
    // increments out to this length; the *effective* reach is usually shorter,
    // cut off wherever the energy model (below) decays past EnergyCutoff. Through
    // open air the beam lances the full length; boring into stone it dies in a
    // few cells. Extended well past the old 220px so it reads as a long lance.
    private const float MaxBeamLength    = 420f;
    private const float StepSize         = 14f;                              // world-px between sampled segments
    private const int   MaxSteps         = (int)(MaxBeamLength / StepSize) + 1;
    private const float SegmentHalfSize  = 6f;
    private const float DamagePerFrame   = TileDamage.TileMaxHP * 0.45f;   // full-energy damage; breaks Stone in 2-3 frames of overlap
    private const float KnockbackImpulse = 200f;
    private const float RecoverySeconds  = 0.2f;

    // --- Energy model (the "strength through blocks / air" math) ---------------
    // The beam carries a normalized energy that starts at 1.0 at the muzzle and is
    // multiplicatively attenuated each step. Damage + knockback delivered to a cell
    // scale with the energy ARRIVING at it (before that cell's own absorption), so a
    // tile shields whatever sits behind it. Air bleeds energy slowly (beam diffuses
    // over distance); solids bleed it hard, weighted by the material's durability so
    // stone chokes the beam far faster than sand.
    private const float AirRetentionPerStep  = 0.992f;  // ~0.79 over the full air run — stays strong
    private const float SolidRetentionBase   = 0.55f;   // per-step retention for a 1×TileMaxHP (Dirt) cell
    private const float EnergyCutoff         = 0.05f;   // below this the beam is spent; reach ends here

    // Per-step solid retention, tied to material durability so the falloff curve is
    // driven by the same numbers that set break HP. Stone (2.0) chokes hardest.
    private static float SolidRetention(TileType type)
        => MathF.Pow(SolidRetentionBase, TileDamage.MaxHPFor(type) / TileDamage.TileMaxHP);

    // Streaming-particle look: a handful of motes ride outward along the beam, each
    // dragging a fading Trail ribbon (Drawing/Trail.cs). They re-launch from the
    // muzzle on a staggered cycle so there's always a steady stream in flight.
    private const int   MoteCount    = 5;
    private const float MoteHz       = 12.25f;   // outward runs per second per mote
    private const int   MoteTrailCap = 12;
    private const float MoteTrailLife = 0.11f;

    // Render-only cache. The beam's live sim state (charge/firing timers, hitId,
    // locked BeamDir) lives in ActionVars; these only feed Draw and self-heal on the
    // next firing Update (Trails are advanced there, where ctx.Dt is available, just
    // like the cursor trail is ticked from Game1.Update), so they stay out of the
    // snapshot. See ActionVars header.
    private Vector2   _lastBeamDir   = Vector2.UnitX;
    private float     _lastBeamReach;
    private int       _segCount;
    private readonly Vector2[] _segPos    = new Vector2[MaxSteps];
    private readonly float[]   _segEnergy = new float[MaxSteps];
    private readonly Trail[]   _motes     = new Trail[MoteCount];
    private readonly int[]     _moteCycle = new int[MoteCount];   // last sweep index per mote; change ⇒ re-launch

    public BeamAction()
    {
        for (int m = 0; m < MoteCount; m++)
            _motes[m] = new Trail(MoteTrailCap, MoteTrailLife);
    }

    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift) return false;
        if (!ctx.Input.LeftClick) return false;
        var prev = ctx.Controller.GetPrevious(1);
        if (prev.LeftClick) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)    return false;
        // From-set: neutral/recovery, or straight out of a live Guard (Shift is
        // already down in a guard stance, so the press-edge lands over Guard).
        if (ctx.RecoveryIndex() == null && ctx.PreviousAction(0) is not GuardAction) return false;
        return true;
    }

    // Charge then fire, both bounded — so the overlay spans the WHOLE activation rather
    // than running out partway through the burst (the authored clip is shorter than
    // MinChargeTime + MaxFiringTime). The split is time-proportional; retune it here if the
    // clip is ever authored with a deliberate wind-up/fire ratio.
    private const float ChargeShare = MinChargeTime / (MinChargeTime + MaxFiringTime);
    public override float AnimationProgress(in ActionVars vars)
        => vars.Firing
            ? ChargeShare + (1f - ChargeShare) * (vars.FiringTime / MaxFiringTime)
            : ChargeShare * (vars.ChargeTime / MinChargeTime);

    // Alive while Shift + LMB both held AND we're either charging or within the
    // firing window. Releasing LMB during charge cancels the beam; releasing
    // during firing ends the beam cleanly.
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (!ctx.Input.LeftClick) return false;
        if (!ctx.Input.Shift)     return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        // Moving or jumping breaks concentration — the beam demands a planted stance.
        // Cancels during charge AND firing, so any L/R/Space input drops the beam.
        if (ctx.Input.Left || ctx.Input.Right || ctx.Input.Space) return false;
        if (vars.Firing && vars.FiringTime >= MaxFiringTime) return false;
        return true;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.ChargeTime = 0f;
        vars.FiringTime = 0f;
        vars.Firing     = false;
        vars.HitId      = ctx.HitIds.Next();
        // Drop any ribbons left over from a previous activation.
        for (int m = 0; m < MoteCount; m++)
        {
            _motes[m].Clear();
            _moteCycle[m] = int.MinValue;
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (!vars.Firing)
        {
            vars.ChargeTime += ctx.Dt;
            if (vars.ChargeTime >= MinChargeTime)
            {
                vars.Firing = true;
                // Lock the aim the instant firing begins. The player aims freely
                // during the charge wind-up; once the beam lights it commits to a
                // fixed angle for the rest of the burst (the player can still walk,
                // so the muzzle origin tracks the body — only the direction sticks).
                var aim = ctx.Input.MouseWorldPosition - ctx.Body.Position;
                vars.BeamDir = aim.LengthSquared() > 1e-6f
                    ? Vector2.Normalize(aim)
                    : new Vector2(ab.Facing, 0f);
            }
            return;
        }
        vars.FiringTime += ctx.Dt;
        if (ctx.Hitboxes == null) return;

        // Beam emanates from the (moving) muzzle along the LOCKED direction.
        var start = ctx.Body.Position;
        var dir   = vars.BeamDir;
        if (dir.LengthSquared() < 1e-6f) return;

        // March outward in fixed StepSize cells, attenuating energy as we cross air
        // and solids. Each step publishes a hitbox whose damage scales with the
        // energy arriving there; we stop once the beam is spent (EnergyCutoff) so a
        // wall of stone visibly shortens the beam while open air lets it run long.
        //
        // HitTargets.All so each segment damages BOTH tiles and entities — that's
        // what carves tunnels while also hurting anything in the line of fire. The
        // shared HitId means CombatSystem's (HitId,Target) dedupe treats the whole
        // beam as ONE attack per entity, so chained segments don't multi-hit a body.
        float energy = 1f;
        int   count  = 0;
        for (int s = 0; s < MaxSteps; s++)
        {
            float dist   = (s + 0.5f) * StepSize;
            if (dist > MaxBeamLength) break;
            var   center = start + dir * dist;

            // Damage uses the energy ARRIVING at this cell (before its absorption).
            float arriving = energy;
            _segPos[count]    = center;
            _segEnergy[count] = arriving;
            count++;

            var region = new BoundingBox(
                center.X - SegmentHalfSize, center.Y - SegmentHalfSize,
                center.X + SegmentHalfSize, center.Y + SegmentHalfSize);
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, DamagePerFrame * arriving,
                dir * (KnockbackImpulse * arriving),
                ctx.Faction, ctx.SelfId, Color.Magenta));

            // Attenuate for the next step based on what THIS cell is made of.
            int gtx = (int)MathF.Floor(center.X / Chunk.TileSize);
            int gty = (int)MathF.Floor(center.Y / Chunk.TileSize);
            if (ctx.Chunks.GetCellState(gtx, gty) == TileState.Solid)
                energy *= SolidRetention(ctx.Chunks.GetCellType(gtx, gty));
            else
                energy *= AirRetentionPerStep;

            if (energy < EnergyCutoff) break;
        }

        _lastBeamDir   = dir;
        _lastBeamReach = count * StepSize;
        _segCount      = count;

        // Advance the streaming motes. Each rides a staggered phase from muzzle (f=0)
        // to tip (f=1); when its phase rolls over to a new cycle it re-launches from
        // the muzzle, so we Clear the ribbon to avoid a streak snapping back across
        // the beam. Tick-then-Push mirrors the cursor trail in Game1.Update.
        for (int m = 0; m < MoteCount; m++)
        {
            float phase = vars.FiringTime * MoteHz + (float)m / MoteCount;
            int   cycle = (int)MathF.Floor(phase);
            float f     = phase - cycle;
            if (cycle != _moteCycle[m])
            {
                _motes[m].Clear();
                _moteCycle[m] = cycle;
            }
            _motes[m].Tick(ctx.Dt);
            _motes[m].Push(start + dir * (_lastBeamReach * f));
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Recovery on exit no matter how we left — a successful beam locks the
        // player out of follow-up Shift+LMB for a moment, an interrupted charge
        // does likewise (which is a feel-call; it punishes spamming).
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }

    // Heavy stance while charging + firing — beam needs the player committed.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.4f;
        m.WalkAccel    *= 0.6f;
        m.MaxAirSpeed  *= 0.5f;
        m.AirAccel     *= 0.6f;
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        if (!vars.Firing)
        {
            // Charge ring at the player so the player can see the wind-up. Single
            // dot pulse — keep cheap; tune later if it needs more presence.
            float frac = vars.ChargeTime / MinChargeTime;
            int   r    = (int)(2 + 6 * frac);
            var col = Color.Lerp(new Color(80, 0, 100), Color.Magenta, frac);
            t.Rect(body.Position, r * 2f, col * 0.6f);
            return;
        }
        if (_segCount <= 0) return;

        // Faint beam core: a thin dot at each marched step, dimmed/brightened by the
        // energy that reached it, so the taper toward the tip (and the abrupt end
        // where the beam bored into stone) still reads. Kept subtle — the streaming
        // ribbons below are the main event.
        var dimCol  = new Color(90, 0, 110);
        var coreCol = new Color(220, 140, 255);
        for (int i = 0; i < _segCount; i++)
        {
            float e   = _segEnergy[i];
            var   col = Color.Lerp(dimCol, coreCol, e) * 0.5f;
            int   r   = (int)(1f + 2f * e);
            var   p   = _segPos[i];
            t.Rect(p, r * 2f, col);
        }

        // Streaming particles: each mote's fading Trail ribbon, advanced in Update.
        // Newer (head) end is bright white-magenta; it tapers to transparent.
        var head = new Color(255, 220, 255);
        var tail = new Color(180, 40, 220, 0);
        for (int m = 0; m < MoteCount; m++)
            _motes[m].Emit(t, head, tail, startWidth: 3.5f);
    }
}

// ---------- Ranged: LobbedArea (Shift + RMB charge) -----------------------------

// Roadmap §4.3 — ranged eruption. Hold Shift+RMB to charge a budget like
// BlockReadyAction does; on release, launch a LobbedAreaProjectile on a
// ballistic arc toward the cursor that detonates at landing into a mass-ball
// eruption + radial AOE.
//
// Why this collides with BlockReadyAction's RMB-anywhere charge: BlockReady
// doesn't gate on Shift. LobbedArea adds the Shift requirement, and its
// higher priority (45 passive) wins press-edge selection when Shift is held.
// Non-Shift RMB still routes to BlockReady as before. Once LobbedAreaAction
// is current, RMB-held keeps it active even if the player releases Shift —
// the gesture is committed at press-edge.
public class LobbedAreaAction : ActionState
{
    private const float MinChargeToFire = 0.4f;
    private const float SaturationTime  = 1.8f;
    private const float DipFactor       = 0.7f;
    private const float BudgetMin       = 0f;
    private const float BudgetMax       = 50f;
    private const float RecoverySeconds = 0.2f;
    // Ballistic arc: vertical speed at launch lifts the ball over a tunable apex
    // height; horizontal speed is derived from cursor-distance / time-of-flight
    // so the ball lands AT the cursor under standard MovementConfig gravity.
    // We don't actually integrate gravity ourselves — PhysicsBody handles that;
    // we just pick (vx, vy) such that the parabola hits the cursor.
    // Charge-tracking pose, same shape as BlockReadyAction: holds at full once saturated.
    // (Dormant — the registration is commented out in PlayerCharacter — but kept correct so
    // re-enabling the binding doesn't reintroduce an unpaced overlay.)
    public override float AnimationProgress(in ActionVars vars) => vars.ChargeTime / SaturationTime;

    private const float LaunchApexBoost = 180f;       // upward velocity at launch (px/s)
    private const float SpawnOffset     = PlayerCharacter.Radius * 1.2f;

    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift) return false;
        if (!ctx.Input.RightClick) return false;
        var prev = ctx.Controller.GetPrevious(1);
        if (prev.RightClick) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)    return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => ctx.Input.RightClick;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.ChargeTime    = 0f;
        vars.CursorAtPress = ctx.Input.MouseWorldPosition;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.ChargeTime += ctx.Dt;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Recovery regardless — short-charge release still locks out a follow-up
        // throw for a moment so spamming low-budget lobs is throttled.
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);

        // RMB still held = forced exit (preemption) — don't fire.
        if (ctx.Input.RightClick) return;
        if (vars.ChargeTime < MinChargeToFire) return;
        if (ctx.Spawner == null) return;

        int budget = ComputeBudget(vars.ChargeTime);
        if (budget <= 0) return;

        // Capture cursor at release (player may have re-aimed during the charge).
        var target = ctx.Input.MouseWorldPosition;
        var spawnPos = ctx.Body.Position + Vector2.Normalize(target - ctx.Body.Position + new Vector2(1e-3f, 0f)) * SpawnOffset;
        var launchVel = ComputeBallisticLaunch(spawnPos, target);

        // Pick up the player's active block type for the eruption shape — same
        // material the BlockReady charge would have used.
        ctx.Spawner.SpawnEntity(new LobbedAreaProjectile(spawnPos, launchVel, budget, ctx.ActiveBlockType, ctx.HitIds.Next(), ctx.Faction));
    }

    // Ballistic solve: given gravity g (from MovementConfig.Current.Gravity),
    // pick vy = -LaunchApexBoost (upward), then time-of-flight to reach the
    // target's Y under gravity, then vx = dx / t. Clamps t to a minimum so a
    // target right on top of the player doesn't divide by zero.
    private static Vector2 ComputeBallisticLaunch(Vector2 from, Vector2 to)
    {
        float g = Simulation.WorldGravityY;
        if (g <= 0f) g = 1f;
        Vector2 d = to - from;
        // Solve d.y = vy * t + 0.5 * g * t^2  with vy = -LaunchApexBoost.
        // → 0.5 g t^2 + (-LaunchApexBoost) t - d.y = 0.
        float a = 0.5f * g;
        float b = -LaunchApexBoost;
        float c = -d.Y;
        float disc = b * b - 4f * a * c;
        float t;
        if (disc < 0f)
        {
            // Target above max apex — fall back to a fixed time.
            t = 0.8f;
        }
        else
        {
            float sqrtDisc = MathF.Sqrt(disc);
            // Both roots positive when target below apex; pick the LATER one
            // (descending arc into target). When target above launch point,
            // there's one positive root — Max picks it.
            float t1 = (-b - sqrtDisc) / (2f * a);
            float t2 = (-b + sqrtDisc) / (2f * a);
            t = MathF.Max(t1, t2);
            if (t < 0.1f) t = 0.1f;
        }
        return new Vector2(d.X / t, -LaunchApexBoost);
    }

    private static int ComputeBudget(float chargeTime)
    {
        float raw;
        if (chargeTime < SaturationTime)
            raw = MathHelper.Lerp(BudgetMin, BudgetMax, chargeTime / SaturationTime);
        else
            raw = BudgetMax * DipFactor;
        return (int)MathF.Round(raw);
    }

    // Heavy stance while charging — same shape as BlockReady's.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.4f;
        m.WalkAccel    *= 0.5f;
        m.MaxAirSpeed  *= 0.5f;
        m.AirAccel     *= 0.6f;
        m.GravityScale *= 0.5f;
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        // Charge ring at the player. Color ramps from olive → goldenrod as the
        // budget grows; past saturation it dims to indicate the budget dip.
        bool saturated = vars.ChargeTime >= SaturationTime;
        float frac = saturated ? 1f : (vars.ChargeTime / SaturationTime);
        int r = (int)(2 + 8f * frac);
        Color col = saturated
            ? new Color(160, 120, 40)
            : Color.Lerp(new Color(80, 60, 20), Color.Goldenrod, frac);
        t.Rect(body.Position, r * 2f, col * 0.55f);
    }
}

// ---------- Ranged: StickyGrenade (F key press) ---------------------------------

// Roadmap §4.4 — sticky-grenade throw. F press-edge spawns a grenade toward
// the cursor. Shift+RMB was the original roadmap binding but that gesture is
// now taken by LobbedAreaAction (charge + release for ranged eruption); F is
// the unambiguous fallback. No charging — single-tap throw at fixed velocity.
public class GrenadeAction : ActionState
{
    private const float Duration       = 0.15f;
    private const float RecoverySeconds = 0.167f;
    private const float SpawnOffset    = PlayerCharacter.Radius * 1.2f;

    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.F) return false;
        var prev = ctx.Controller.GetPrevious(1);
        if (prev.F) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)    return false;
        // From-set: neutral/recovery, or over a live Guard (F throw keeps its
        // old ability to preempt the stance).
        if (ctx.RecoveryIndex() == null && ctx.PreviousAction(0) is not GuardAction) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        if (ctx.Spawner == null) return;
        Vector2 toCursor = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        Vector2 dir = toCursor.LengthSquared() < 1e-4f
            ? new Vector2(ab.Facing == 0 ? 1f : ab.Facing, 0f)
            : Vector2.Normalize(toCursor);
        var spawnPos = ctx.Body.Position + dir * SpawnOffset;
        ctx.Spawner.SpawnEntity(new StickyGrenadeProjectile(spawnPos, dir, ctx.HitIds.Next(), ctx.Faction));
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }
}

// ---------- Block Grab — Shift + LMB on terrain: peel blocks out, throw them -----

// The mirror of the RMB build/eruption gesture: instead of pushing terrain out of the
// world, this pulls it in. Shift+LMB press with the cursor on a solid cell within
// reach starts a grab.
//
// The action is deliberately THIN (Plans/BLOCK_THROW_PLAN.md §4.3). On the press it
// spawns a PullPointEntity and keeps its id; every held frame it DRIVES the point
// (writes the cursor — or, once a clod is in hand, the held rest position — as
// TargetPos and the body as OwnerPos) and mirrors a summary back into ActionVars; on
// release it HANDS THE POINT OFF with the cursor's velocity and exits that frame. All
// the mechanics — the paint kernel, the spring, tether/glue wear, the break-out, the
// legacy drag-rip — live on the entity, and the ball is a LobbedAreaProjectile of its
// own from the moment it breaks out, so nothing that must outlive the button is ever
// inside this action.
//
// THE THROW (§4.1/§4.2): the ball follows the point with a velocity-matching tracker.
// Let go with the mouse still and the point stops, the ball is already on it, it drops.
// Swipe and let go and the point flies at the swipe velocity — an EMA of the raw
// cursor's world velocity, measured on the unclamped cursor even though the held ball
// sits near the hand — and the ball converges to that velocity and detaches. Same
// rule whether the clod was in hand or still in the ground when the button came up.
//
// Keep holding and the clod dissipates over GrabDissipateSeconds; at zero the ball is
// gone, the point dies, the action ends. Carrying terrain has a cost.
//
// Priority 46/46, above Beam/EnergyBall's 40/45 — both also live on Shift+LMB, and
// this has to win the press frame AND still be holding the button when they'd
// otherwise fire on release. The cursor-in-solid gate is what keeps the three from
// fighting: on terrain you grab, off terrain you beam. Nothing preempts the carry
// (46 Active), which is intentional — the orb is a commitment. A preempting exit
// (GrabAction 48 / GuardRetaliate 55) hands the point off like a release.
public class BlockGrabAction : ActionState
{
    // Internal because GrabAction defers to this exact reach when deciding whether a
    // Shift+LMB press is aimed at terrain — one constant, so the two can't disagree.
    internal const float GrabReach      = PullPointEntity.GrabReach;
    // Cursor travel from the press point that counts as "a drag" (legacy rip). Under
    // this it's a click, and the action lapses without taking anything.
    // Px, deliberately independent of tile size.
    private const float DragThreshold   = 12f;
    // How long the press waits for that drag / the first tethered cell before giving up.
    private const float GrabWindow      = 0.60f;
    private const float RecoverySeconds = 0.20f;
    // The ball body's radius — the held rest position keeps that much above the feet.
    private const float BallRadius      = 5f;

    public override int ActivePriority  => 46;
    public override int PassivePriority => 46;

    // Only the carry phase has a meaningful arc, and its length is player-controlled,
    // so decline and let the overlay clip play at its authored rate.
    public override float AnimationProgress(in ActionVars vars) => -1f;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift || !ctx.Input.LeftClick) return false;
        if (ctx.Controller == null || ctx.Controller.GetPrevious(1).LeftClick) return false;  // press-edge only
        if (ab.Condition.RecoveryActive) return false;
        // The grab is an entity; without a spawner (some headless harnesses) it can't exist.
        if (ctx.Spawner == null) return false;
        // From-set: neutral/recovery, or over a live Guard (Shift is already down).
        if (ctx.RecoveryIndex() == null && ctx.PreviousAction(0) is not GuardAction) return false;
        // Terrain-only gesture: the cursor must be ON a block, and within arm's reach.
        if (!BlockEruptionHelpers.IsCursorInSolid(ctx)) return false;
        return (ctx.Input.MouseWorldPosition - ctx.Body.Position).LengthSquared() <= GrabReach * GrabReach;
    }

    // The point this activation spawned, or null once it has died (snapped, bled out,
    // swept). Looked up by id every time — the object is replaced by a rollback restore.
    private static PullPointEntity Point(EnvironmentContext ctx, in ActionVars vars)
        => ctx.Spawner?.Resolve(vars.PullPointId) as PullPointEntity;

    // The ball in hand: the point's ball, alive and still tracking a driven point.
    private static LobbedAreaProjectile HeldBall(EnvironmentContext ctx, PullPointEntity point)
    {
        if (point == null || !point.HasBall) return null;
        var ball = ctx.Spawner.Resolve(point.BallId) as LobbedAreaProjectile;
        return ball != null && !ball.IsDead && ball.Tracking ? ball : null;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Release always ends the state — Exit hands the point off either way.
        if (!ctx.Input.LeftClick) return false;
        var point = Point(ctx, in vars);
        if (point == null) return false;                           // snapped / bled out
        if (point.HasBall) return HeldBall(ctx, point) != null;    // carrying until it bleeds out
        if (MovementConfig.Current.BlockPeelEnabled)
        {
            // A snapped spring kills the attempt outright; otherwise a live group
            // keeps the state open past the press window — the pull takes as long
            // as it takes.
            if (point.Snapped) return false;
            return point.PeelCount > 0 || vars.TimeInState < GrabWindow;
        }
        return vars.TimeInState < GrabWindow;                      // still waiting for the drag
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState   = 0f;
        vars.OrbHeld       = false;
        vars.PeelCount     = 0;
        vars.PeelStrain    = 0f;
        vars.SwipeVel      = Vector2.Zero;
        vars.PrevCursor    = ctx.Input.MouseWorldPosition;
        vars.CursorAtPress = ctx.Input.MouseWorldPosition;
        vars.IsGrounded    = ctx.TryGetGround(out _);
        vars.GrabDir       = new Vector2(ab.Facing == 0 ? 1f : ab.Facing, 0f);

        var point = new PullPointEntity(ctx.Input.MouseWorldPosition, ctx.Faction, ctx.ActiveBlockType)
        {
            OwnerPos = ctx.Body.Position,
        };
        ctx.Spawner.SpawnEntity(point);      // assigns the id
        vars.PullPointId = point.Id;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
        var cursor = ctx.Input.MouseWorldPosition;

        // Live aim, and the swipe estimate: an EMA of the raw cursor's world velocity.
        // World frame on purpose — the camera follows the player, so a running
        // player's static mouse already carries the run (plan §4.2).
        var aim = cursor - ctx.Body.Position;
        if (aim.LengthSquared() > 1e-4f) vars.GrabDir = Vector2.Normalize(aim);
        if (ctx.Dt > 0f)
        {
            var inst = (cursor - vars.PrevCursor) / ctx.Dt;
            vars.SwipeVel = Vector2.Lerp(vars.SwipeVel, inst, MovementConfig.Current.GrabSwipeSmoothing);
        }
        vars.PrevCursor = cursor;

        var point = Point(ctx, in vars);
        if (point == null) return;                                 // CheckConditions ends the state next frame

        // Drive: peel at the raw cursor; once a clod is in hand, the point becomes the
        // held rest position and the ball tracks it there.
        var ball   = HeldBall(ctx, point);
        var target = ball != null ? RestPosition(ctx, ab, cursor) : cursor;
        point.TargetPos     = target;
        point.OwnerPos      = ctx.Body.Position;
        point.Body.Position = target;
        point.Body.Velocity = Vector2.Zero;

        if (ball == null && !point.HasBall && !MovementConfig.Current.BlockPeelEnabled)
        {
            // Legacy drag-rip. The harvest is centered on CursorAtPress, not the
            // current cursor — the player marks the site with the press and the
            // drag is just the commit gesture, so a fast flick can't smear the dig.
            var travel = cursor - vars.CursorAtPress;
            if (travel.LengthSquared() >= DragThreshold * DragThreshold)
                point.RipBlocks(ctx.Spawner, vars.CursorAtPress);
        }

        // Mirror (one frame behind the entities' own updates, which run after the FSMs).
        vars.OrbHeld    = ball != null;
        vars.PeelCount  = point.PeelCount;
        vars.PeelStrain = point.PeelStrain;
        if (ball != null) vars.BallPos = ball.Body.Position;
    }

    // Where the held clod rests (plan §5 "Held rest position"): orbiting the body at
    // GrabHandDistance in the cursor's direction, leaning outward by at most
    // GrabHandLean as the cursor moves away. A soft constraint on where the ball SITS
    // only — the throw is the cursor's velocity, so a tight hold still throws hard.
    // Kept above the feet so the tracker isn't grinding a floor-pinned body into the
    // ground when the cursor points down.
    private static Vector2 RestPosition(EnvironmentContext ctx, PlayerAbilityState ab, Vector2 cursor)
    {
        var cfg  = MovementConfig.Current;
        var body = ctx.Body.Position;
        var d    = cursor - body;
        float r  = d.Length();
        var dir  = r > 1e-3f ? d / r : new Vector2(ab.Facing == 0 ? 1f : ab.Facing, 0f);
        float hand = cfg.GrabHandDistance;
        float lean = MathF.Max(1e-3f, cfg.GrabHandLean);
        float rho  = hand + lean * MathF.Tanh(MathF.Max(0f, r - hand) / lean);
        var target = body + dir * rho;
        float floorY = body.Y + PlayerCharacter.Radius - BallRadius;
        if (target.Y > floorY) target.Y = floorY;
        return target;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        var point = Point(ctx, in vars);
        if (point != null)
        {
            // Hand-off: the point flies on at the swipe velocity; a ball in hand
            // chases it and detaches at that speed — the throw. A still mouse hands
            // off ≈0 and the ball just drops. Preemption takes the same path (§7.5).
            point.Release(vars.SwipeVel);

            // Recovery only for a grab that actually took something — a lapsed press
            // shouldn't cost the player lag.
            if (point.HarvestBlocks > 0)
                ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
                                      ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
        }

        // Spend the gesture either way, so the release frame can't also route a Click
        // intent into EnergyBallAction.
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);
        ctx.Intents.Consume(IntentType.PressEdge, ctx.CurrentFrame);
        vars.OrbHeld     = false;
        vars.PullPointId = EntityId.None;
    }

    // Carrying terrain is heavy: hauling a clod slows the walk and drags in air. The
    // peel phase leaves movement alone, since nothing's been picked up yet.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        if (!vars.OrbHeld) return;
        m.MaxWalkSpeed *= 0.75f;
        m.WalkAccel    *= 0.8f;
        m.MaxAirSpeed  *= 0.8f;
        m.AirDrag      *= 1.2f;
    }

    // Tether tint is drawn by the PullPointEntity's overlay; the ball draws itself.
}

// ---------- Grab — Shift + LMB: hold an opponent, then throw ---------------------
//
// COMBAT_FEEL_PLAN Phase 6: the grab completes the RPS triangle (grab beats guard,
// attack beats grab, guard beats attack). It's the Phase 2 hold-field turned up — a
// strong short-range ForceField in front of the grabber that flags whoever it holds
// `GrabbedActive` (so their normal attacks/jump gate off; only struggle attacks fire).
// It is stateless like every field: the "grab" persists only while this action keeps
// broadcasting. The action leads with a StartupSeconds windup during which it is the
// live action state but holds nobody — no field goes out, so the grab is a read the
// opponent can jump or hit out of rather than an instant snap. What it is NOT is an area effect — a grab holds ONE body. The action
// latches a single victim (vars.GrabVictim) and stamps it on the field (ForceField.Only)
// so the region's geometry stays generous while the hold, the pull and the throw all
// land on exactly that one opponent. It IGNORES guard for free (a field never goes
// through the OnHit/parry path). Releasing RMB (or hitting the hold cap) flings the victim with a brief
// high-speed directional field — hard enough into terrain and the crush path bills them.
//
// Grab-break is a strength contest: the hold starts at GrabStrengthMax, and each
// connecting struggle slash erodes it (the struggle hit deliberately deals no stun —
// see GrabbedSlash). CheckConditions releases the grab once GrabStrength hits 0, which
// clears the victim's GrabbedActive a couple frames later. A heavier hit on the grabber
// (real hitstun, e.g. a third party) still drops the hold immediately. A whiffed grab
// runs its hold→throw→recovery, so an opponent who reads it punishes the lag.
public class GrabAction : ActionState
{
    // Startup: the grab is committed and on screen, but holds NOTHING yet — no field is
    // published, so no body is flagged, pulled or latched until it elapses. An instant
    // hold made the grab an unreactable snap; this is the window the opponent reads.
    private const float StartupSeconds     = 0.2f;
    private const float GrabHoldMaxSeconds = 1.2f;    // auto-throw if held this long AFTER startup
    // Wall-clock end of the hold phase. TimeInState runs across startup and hold both, so
    // the cap has to include the windup or the startup would eat 0.2s of the hold.
    private const float HoldEndSeconds     = StartupSeconds + GrabHoldMaxSeconds;
    // Grab strength the hold starts with; each connecting struggle slash erodes it by
    // GrabbedSlash.GrabStrengthDamage (1.0), so a fresh grab survives 2 struggles and
    // breaks on the 3rd. Bump for a stickier grab, lower for an easier mash-out.
    private const float GrabStrengthMax    = 3f;
    private const float ThrowSeconds       = 0.12f;   // throw-field duration
    private const float RecoverySeconds    = 0.3f;    // lag after a grab (the whiff-punish window)
    private const float Range       = PlayerCharacter.Radius * 2.4f;   // field region half-size
    private const float FocusDist   = PlayerCharacter.Radius * 1.6f;   // hold focus in front of the grabber
    private const float PullSpeed   = 320f;
    private const float PullAccel   = 9000f;          // strong — overpowers the victim walking away
    private const float ThrowSpeed  = 520f;

    // Two phases with INDEPENDENT lengths, which is why the old fixed-duration remap could
    // not express this: the hold runs until the player releases (up to HoldEndSeconds),
    // then the throw runs its own ThrowSeconds. The authored clip devotes its tail to the
    // throw, so map the hold onto everything before HoldShare and the throw onto the rest —
    // a short hold jump-cuts forward to the throw, which is right: the throw pose must play
    // WHEN the throw happens, not whenever the clip's own clock reaches it.
    private const float HoldShare = HoldEndSeconds / (HoldEndSeconds + ThrowSeconds);
    public override float AnimationProgress(in ActionVars vars)
        => vars.GrabThrowing
            ? HoldShare + (1f - HoldShare) * (vars.ChargeTime / ThrowSeconds)
            : HoldShare * (vars.TimeInState / HoldEndSeconds);
    private const float ThrowAccel  = 12000f;

    // 48/48 — above BlockGrabAction (46/46), which shares the Shift+LMB press. The two
    // deliberately collide: grabbing a body is the more urgent read, so it takes the
    // press whenever a body is there to take, and falls through to the block grab when
    // there isn't. Still below GuardRetaliate(55).
    public override int ActivePriority  => 48;
    public override int PassivePriority => 48;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift || !ctx.Input.LeftClick) return false;
        if (ctx.Controller.GetPrevious(1).LeftClick) return false;      // press-edge only
        if (ctx.Combat?.BlocksAttack == true) return false;             // not while stunned/grabbed
        if (ctx.Combat?.HitstunActive == true) return false;            // not while in hitstun
        if (ab.Condition.RecoveryActive) return false;
        // From-set: neutral/recovery, or over a live Guard (Shift is already down).
        if (ctx.RecoveryIndex() == null && ctx.PreviousAction(0) is not GuardAction) return false;
        // Yield the press to BlockGrabAction ONLY when it could actually use it: the
        // cursor is on a block inside its reach AND there's nobody to grab. Aim at a
        // body and the grab wins on priority; aim at open air and the grab still fires
        // and WHIFFS — which preserves the punish window (the unconditional recovery in
        // Exit is the whole punish, see the header). Terrain aim is the only thing that
        // hands the gesture over.
        if (!AimedAtGrabbableTerrain(ctx)) return true;
        return HasVictimInRange(ctx, ab);
    }

    // Cursor over a solid cell within BlockGrabAction's own reach — i.e. that action's
    // precondition, minus the press-edge. Shares BlockGrabAction.GrabReach so "aimed at
    // terrain" means the same thing on both sides of the handoff.
    private static bool AimedAtGrabbableTerrain(EnvironmentContext ctx)
    {
        if (!BlockEruptionHelpers.IsCursorInSolid(ctx)) return false;
        float reach = BlockGrabAction.GrabReach;
        return (ctx.Input.MouseWorldPosition - ctx.Body.Position).LengthSquared() <= reach * reach;
    }

    // Any non-self hurtbox inside the region the hold field would cover. Same geometry
    // as the hold field in Update (focus in front along the aim, Range half-size), so
    // the gate and the field can't disagree about who is grabbable. Hurtboxes for the
    // frame are already published by the time the action FSM runs (Simulation.Step).
    private static bool HasVictimInRange(EnvironmentContext ctx, PlayerAbilityState ab)
        => !PickVictim(ctx, RegionAround(ctx.Body.Position + AimDir(ctx, ab) * FocusDist),
                       EntityId.None).IsNone;

    // The field's AABB, from its focus. One helper so the latch query and the published
    // field are the same box by construction.
    private static BoundingBox RegionAround(Vector2 focus)
        => new BoundingBox(focus.X - Range, focus.Y - Range, focus.X + Range, focus.Y + Range);

    // Which single body this grab has hold of. `current` is last frame's victim: it wins
    // as long as it is still inside the region, so the hold never hops to whoever happens
    // to drift closer mid-grab. Otherwise (a fresh grab, or the victim escaped) the
    // nearest hurtbox to the focus is latched, which is also what re-lets a whiffing grab
    // catch someone who walks into it. Nearest-to-focus with first-wins ties keeps the
    // pick deterministic: it reads only published hurtbox geometry in registry order.
    private static EntityId PickVictim(EnvironmentContext ctx, BoundingBox region, EntityId current)
    {
        if (ctx.Hurtboxes == null) return EntityId.None;
        var focus  = new Vector2(region.CenterX, region.CenterY);
        var best   = EntityId.None;
        float bestD = float.MaxValue;
        foreach (var hb in ctx.Hurtboxes.Overlapping(region, exclude: ctx.Faction))
        {
            if (hb.Target == ctx.SelfId) continue;
            if (hb.Target == current) return current;        // keep hold of who we already have
            var center = new Vector2(hb.Region.CenterX, hb.Region.CenterY);
            float d = (center - focus).LengthSquared();
            if (d < bestD) { bestD = d; best = hb.Target; }
        }
        return best;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Grab-break: a hard hit on the grabber (hitstun) drops the hold outright, and
        // the struggle attack wears the hold down — once the victim's struggles have
        // eroded GrabStrength to 0, the grab releases (the new primary break path).
        if (ctx.Combat?.HitstunActive == true) return false;
        if (ctx.Combat != null && ctx.Combat.GrabStrength <= 0f) return false;
        if (vars.GrabThrowing) return vars.ChargeTime < ThrowSeconds;
        return true;   // hold phase persists; Update transitions to the throw
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState  = 0f;
        vars.ChargeTime   = 0f;
        vars.GrabThrowing = false;
        vars.GrabDir      = AimDir(ctx, ab);
        vars.GrabVictim   = EntityId.None;   // latched in Update, off the field's own region
        if (ctx.Combat != null) ctx.Combat.GrabStrength = GrabStrengthMax;
    }

    private static Vector2 AimDir(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        Vector2 raw = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        return raw.LengthSquared() > 1e-2f ? Vector2.Normalize(raw) : new Vector2(facing, 0f);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        if (!vars.GrabThrowing)
        {
            vars.TimeInState += ctx.Dt;
            // Windup. Nothing is grabbed yet: no field, so no GrabbedActive, no pull, and
            // vars.GrabVictim stays None. The release check is deliberately BELOW this —
            // the startup is a commitment and runs to completion, so a tap can't skip the
            // windup to reach the throw early. It just arrives at the throw having held
            // nobody, which is the existing whiff (and its recovery lag) either way.
            if (vars.TimeInState < StartupSeconds) return;

            bool holding = ctx.Input.LeftClick && vars.TimeInState < HoldEndSeconds;
            if (holding)
            {
                var focus  = ctx.Body.Position + new Vector2(facing, 0f) * FocusDist;
                var region = RegionAround(focus);
                // One victim per grab: latch (or keep) a single target and hand it to the
                // field, which then ignores every other body its region covers. Without
                // this the field grabs the whole region at once — the hold flag, the pull
                // and the throw all landing on every opponent standing nearby.
                vars.GrabVictim = PickVictim(ctx, region, vars.GrabVictim);
                if (ctx.ForceFields != null)
                    ctx.ForceFields.Publish(new ForceField(
                        region, focus, PullSpeed, PullAccel, ctx.Faction, ctx.SelfId,
                        Color.Magenta, isGrab: true, only: vars.GrabVictim));
                return;
            }
            // Release or hold-cap → enter the throw.
            vars.GrabThrowing = true;
            vars.ChargeTime   = 0f;
            vars.GrabDir      = AimDir(ctx, ab);
        }

        // Throw phase: a brief high-speed directional field flings whoever's still
        // held-adjacent along GrabDir (no IsGrab — the victim is released, not held).
        vars.ChargeTime += ctx.Dt;
        if (ctx.ForceFields != null)
        {
            // Region hugs the held position; focus is far down GrabDir so the servo
            // drives the victim to ThrowSpeed away from the grabber.
            var hold   = ctx.Body.Position + vars.GrabDir * FocusDist;
            var focus  = ctx.Body.Position + vars.GrabDir * 400f;
            var region = RegionAround(hold);
            // The throw flings the body this grab was holding — and only that body. A grab
            // that never caught anyone may still latch here, so an opponent who walks into
            // the release gets thrown; but once a victim is latched the throw never
            // re-latches, because the fling carries the victim out of the region within a
            // frame or two and a re-latch would then hand the same throw to the next body
            // standing in it (that is the multi-victim bug, arriving one frame late).
            if (vars.GrabVictim.IsNone)
                vars.GrabVictim = PickVictim(ctx, region, EntityId.None);
            ctx.ForceFields.Publish(new ForceField(
                region, focus, ThrowSpeed, ThrowAccel, ctx.Faction, ctx.SelfId, Color.HotPink,
                isGrab: false, isThrow: true, only: vars.GrabVictim));
        }
    }

    // Heavy stance while grabbing — the grabber is committed.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.4f;
        m.WalkAccel    *= 0.5f;
        m.MaxAirSpeed  *= 0.6f;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
            ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        int facing = vars.GrabDir.X >= 0f ? 1 : -1;
        var focus = body.Position + (vars.GrabThrowing ? vars.GrabDir : new Vector2(facing, 0f)) * FocusDist;
        var color = vars.GrabThrowing ? Color.HotPink : Color.Magenta;
        // Dim through the windup so the cue reads as "winding up" rather than "holding you".
        bool startup = !vars.GrabThrowing && vars.TimeInState < StartupSeconds;
        t.Rect(focus, 6f, color * (startup ? 0.35f : 0.8f));
    }
}

// ---------- Struggle: the one attack a grabbed player can throw -------------------
//
// COMBAT_FEEL_PLAN Phase 6. A slash exempt from the BlocksAttack gate that grabs impose
// — it requires GrabbedActive and skips the gate the normal slashes obey. It's
// short-range (the grab holds you adjacent) and does NOT stun or knock back the grabber:
// instead each connecting hit erodes the grabber's GrabStrength (GrabStrengthDamage),
// and the grab releases once that reaches 0. Stunning the grabber would let the victim
// trade out of every grab and unbalanced the exchange, so the struggle just wears the
// hold down. Its startup is the grabber's window to throw first: a prompt throw beats a
// struggle, a greedy hold eats it.
public class GrabbedSlash : SlashLikeAction
{
    protected override float Duration            => 0.16f;
    protected override float ArcRadiusScale      => 0.9f;     // short — held adjacent
    protected override float SweepAngleDeg       => 80f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 0f;       // no knockback — erodes grab strength instead
    protected override Color SlashColor          => Color.Yellow;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => false;
    // The struggle channel: each connecting hit removes this much grab strength from
    // the grabber (GrabStrengthMax 3 ⇒ breaks on the 3rd). No hitstun is dealt.
    protected override float GrabStrengthDamage  => 1f;
    // A struggle hit must have zero effect on either side, not just zero knockback —
    // wearing a grab down should never also shove the grabber (see CombatState's
    // struggle-channel comment).
    protected override float RecoilScale         => 0f;

    // Beats NullAction; no combo. Normal slashes are gated off while grabbed, so this
    // is the only attack available.
    public override int PassivePriority => 36;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // EXEMPT from the BlocksAttack gate (which a grab raises) — that's the whole
        // point. Requires being grabbed + a click intent. Still pays its own
        // recovery between struggles (EntryOk) — mash cadence is throttled by the
        // 0.15s stamp, same rhythm the old recovery priority enforced. The
        // exemption extends to hit disadvantage: a pummeling grabber's hitstun
        // must not lock the victim out of struggling, so only the SELF stamp
        // gates re-entry (flinch still interrupts a struggle swing mid-arc).
        if (ctx.Combat?.GrabbedActive != true) return false;
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        if (!EntryOk(ctx, 0, ignoreHitDisadvantage: true)) return false;
        return true;
    }

    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
        => ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.15f, f, dt);
}

