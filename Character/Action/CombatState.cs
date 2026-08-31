using System;
using Microsoft.Xna.Framework;

namespace MTile;

// What a guard did to an incoming hit — the return of CombatState.ResolveGuard.
// Absorbed is the perfect block (skip the hit entirely); otherwise the two scales say
// how much of the hit survived the stance, and are 1 when the guard wasn't involved
// at all. Kept as a value struct so OnHit can hold it across the whole resolution
// without any allocation on the hit path.
public readonly struct GuardOutcome
{
    public readonly bool  Absorbed;
    public readonly float DamageScale;
    public readonly float KnockbackScale;

    public GuardOutcome(bool absorbed, float damageScale, float knockbackScale)
    {
        Absorbed       = absorbed;
        DamageScale    = damageScale;
        KnockbackScale = knockbackScale;
    }

    // The hit was never filtered — full damage, full knockback.
    public static GuardOutcome None   => new GuardOutcome(false, 1f, 1f);
    // Perfect block. The scales are zero too, so a caller that forgets to branch on
    // Absorbed still applies nothing rather than everything.
    public static GuardOutcome Absorb => new GuardOutcome(true, 0f, 0f);
}

// Defensive combat condition. Sibling of ConditionState (which holds *offensive*
// combo flags like Slash2Ready). Lives on PlayerAbilityState; PlayerCharacter.OnHit
// writes; movement/action FSMs read it via EnvironmentContext.Combat to gate jumps,
// attacks, etc.
//
// Hitstun is the always-on disadvantage state: every hit locks Jump for a short
// window, with diminishing extensions on follow-up hits so true infinite stun-locks
// are impossible. Stun is the heavy-hit state — set by OnHit only when the incoming
// knockback impulse crosses a threshold; gates more (attacks too, not just jump).
public class CombatState
{
    public bool    HitstunActive;     public int HitstunExpireFrame;
    public bool    StunActive;        public int StunExpireFrame;
    // True while the current hitstun window came from a combat hit (vs. a
    // self-inflicted crush/landing). Only combat hitstun mutes self-control —
    // a hard landing locks jump briefly but shouldn't turn walking to mush.
    public bool    HitstunMutesControl;

    // Hitstop (Plans/HIT_FEEL_PLAN.md phase 1): a brief freeze of this player's own
    // agency on a landed combat hit — PlayerCharacter.Update early-returns past FSM/
    // action progression and force application while active (see the early-return
    // right after Tick() below), same "expire-frame, not countdown" shape as
    // HitstunActive so it replays identically across a rollback resimulation.
    // Physics integration (gravity/terrain collision) is deliberately NOT suppressed —
    // that would mean excluding this player's body from the shared per-frame physics
    // batch in Simulation.Step, which is riskier to get right than freezing agency
    // alone. Victim-only for V1 (see the plan's open question on symmetric attacker
    // hitstop). Only set for real combat hits (muteControl == true in
    // OnHitRegistered) — a self-inflicted crush/landing hit stuns Jump but shouldn't
    // also freeze you mid-fall.
    public bool    HitstopActive;     public int HitstopExpireFrame;

    public float   LastHitImpulse;    public int LastHitFrame;
    // Direction the last hit's knockback pushed this player, for render-only cosmetics
    // (directional knockback cue, weapon flash) that need more than the magnitude
    // LastHitImpulse already carries. Set by PlayerCharacter.OnHit from the resolved
    // knockback, not OnHitRegistered — the caller already has it there and this avoids
    // growing OnHitRegistered's parameter list for a value it doesn't otherwise need.
    public Vector2 LastHitDir;

    // Cumulative HP this player has lost within the current life — every source
    // (a landed hit, a crush impact) adds to it, and only a KO/respawn resets it.
    // Health itself regenerates, so it cannot answer "has this player been hurt,
    // and how badly" a few seconds after the fact; this can. Read by tests and by
    // presentation; nothing in the sim branches on it. Snapshotted (CopyFrom).
    //
    // It replaced DamagePercent, the escalation meter this field's slot used to
    // hold. That model routed every direct hit into a monotonic percent which
    // scaled knockback, so hits pushed you around harder and harder and HP only
    // came off when the resulting launch slammed you into terrain. Two things went
    // wrong with it in play: knockback everywhere ended up enormous (a 100% player
    // eats 2.5x launches from a light poke), and damage became indirect enough that
    // a clean hit read as no consequence. Hits now cost HP directly — the same rule
    // entities have always followed — and this meter is just the tally.
    public float DamageTaken;
    public void AddDamage(float hp) { if (hp > 0f) DamageTaken += hp; }

    // Short i-frame window, currently granted by a successful tech (Phase 4).
    // PlayerCharacter.OnHit early-returns while the current frame is before this.
    // Distinct from PlayerCharacter's respawn invuln (which suppresses the hurtbox
    // outright); this lets the hurtbox keep publishing but no-ops incoming hits.
    public int     InvulnExpireFrame;
    public bool IsInvulnerable(int currentFrame) => currentFrame < InvulnExpireFrame;

    // Grabbed flag (COMBAT_FEEL_PLAN Phase 6). Mirrors HitstunActive: a grab ForceField
    // re-marks the victim every frame it holds them (ForceFieldSystem → MarkGrabbed),
    // and Tick clears it a couple of frames after the field stops. While grabbed,
    // normal attacks and jump are gated (BlocksAttack / BlockedCapabilities) — only
    // the exempt struggle attacks fire. Snapshotted (a bool + an int, like hitstun).
    public bool GrabbedActive;   public int GrabbedExpireFrame;
    // Grace so a 1-frame gap in the field (e.g. broad-phase jitter) doesn't drop the
    // grabbed state — the field re-marks each frame it overlaps.
    private const int GrabbedGraceFrames = 2;
    // Set by the grab field each frame it holds this victim. `frame` is the victim's
    // own frame counter, so IsGrabbed lines up with the gates that read it next step.
    public void MarkGrabbed(int frame)
    {
        GrabbedActive = true;
        int expire = frame + GrabbedGraceFrames;
        if (expire > GrabbedExpireFrame) GrabbedExpireFrame = expire;
    }

    // Grab strength (struggle mechanic). Lives on the GRABBER's combat state: GrabAction
    // sets it to full on Enter, and each connecting struggle slash from the victim erodes
    // it (ErodeGrab, routed through the grab-strength hit path in PlayerCharacter.OnHit).
    // GrabAction.CheckConditions drops the hold once it reaches 0 — that's the grab-break,
    // replacing the old "one struggle hit stuns the grabber → break". The struggle hit
    // deliberately applies no knockback/hitstun, so wearing a grab down never stuns the
    // grabber (which unbalanced trades). Snapshotted (CopyFrom).
    public float GrabStrength;
    public void ErodeGrab(float amount)
    {
        GrabStrength -= amount;
        if (GrabStrength < 0f) GrabStrength = 0f;
    }

    // A throw flings + stuns the victim (Phase 6). Routed through OnHitRegistered with
    // a stun-threshold impulse so the victim exits the throw into Tumble (airborne):
    // committed, control-muted, able to tech, and bouncing hard off terrain — instead
    // of keeping full control out of the throw. Called by the throw field's onThrown.
    private const float ThrowStunImpulse = 450f;   // > StunImpulseThreshold ⇒ stun + Tumble
    public void RegisterThrown(int frame, float dt) => OnHitRegistered(frame, ThrowStunImpulse, dt);

    // Hoisted gates so callers can write `ctx.Combat?.BlocksAttack == true` instead
    // of repeating raw flag checks at every action/movement precondition site.
    //
    // BlocksAttack is GRABBED-ONLY since the hit-airlock unification
    // (Plans/HIT_AIRLOCK_PLAN.md): hitstun/stun now gate actions through the
    // recovery index (EnvironmentContext.HitDisadvantageFrames folds into
    // RecoveryIndex, consumed by each entrant's EntryOk window), so a stun is a
    // countdown every action prices with its own entry window rather than a
    // blanket refusal. Grabbed stays a flag — it's a live external hold
    // re-marked every frame, not a timed window.
    public bool BlocksAttack => GrabbedActive;
    public bool BlocksJump   => HitstunActive || StunActive || GrabbedActive;

    // Cross-cutting movement-capability lock-out (COMBAT_FEEL_PLAN Phase 4). The
    // whole combat disadvantage window (hitstun OR stun) blocks Jump and the
    // terrain-grab capabilities, so a launched/stunned player can't cancel
    // knockback by jumping, wall-clinging, or grabbing a ledge — knockback becomes
    // juggle/edgeguard pressure. Consumed by PlayerCharacter's selection loop,
    // which drops any candidate movement state whose RequiredCapabilities intersect
    // this mask. Gates ENTRY only — a state already running finishes on its own
    // CheckConditions (a player hit mid-jump still completes the arc).
    public MovementCapability BlockedCapabilities =>
        (HitstunActive || StunActive || GrabbedActive)
            ? MovementCapability.Jump | MovementCapability.WallCling | MovementCapability.LedgeGrab
            : MovementCapability.None;

    // Guard (parry) — roadmap §1.5. GuardActive is the moment-to-moment "Shift
    // held + no L/R" gate, written by GuardAction.Enter/Exit. GuardCharged is the
    // window in which a successful low-damage parry has armed GuardRetaliate.
    public bool    GuardActive;
    public bool    GuardCharged;     public int GuardChargedExpireFrame;
    // The frame the stance went up, written by BeginGuard. The whole timing model
    // is (currentFrame - GuardStartFrame): guard is a parry you time, not a shield
    // you hold.
    public int     GuardStartFrame;
    // Re-entry cooldown, set every time the stance comes DOWN (EndGuard) — including a
    // clean release, a movement cancel, or a break. Without it the timing model is free
    // to game: tapping Shift over and over hands out a fresh perfect window per press,
    // and mashing covers most frames with perfect blocks. Blocking re-entry for slightly
    // longer than the window itself means a mash is off more often than it is on, so
    // guessing WHEN to guard stays the skill.
    public int     GuardCooldownExpireFrame;
    // Set by a perfect block and consumed by the deactivation it pays for. A clean block
    // spends the stance like any other — but it is the one drop that costs nothing, so
    // reading the attack right lets you come straight back up for the next one, while a
    // mistimed or speculative guard still eats the cooldown.
    public bool    GuardBlockRefund;
    // Break recovery. Set when a hit gets through the guard; the stance can't come
    // back up until the countdown expires AND the button has been released (Tick).
    // The release half matters: without it, holding Shift through a break would hand
    // out a fresh perfect window every recovery period, and "held indefinitely" would
    // never reach the saturated penetration below.
    public bool    GuardBroken;      public int GuardBreakExpireFrame;

    // Render-only cue stamps for a successful parry, written by TryParry. Same "the
    // stamp IS the identity" contract as LastHitFrame/LastHitDir (Audio/GameAudio.cs
    // header): a rollback replays the same parry on the same sim frame, so a
    // presentation system that dedupes on the frame number fires exactly once. No
    // presentation event needed. LastParryDir points from the player TOWARD the
    // attacker, so sparks spray back the way the hit came in.
    public int     LastParryFrame;
    public Vector2 LastParryDir;
    public bool    LastParryCharged;   // that parry also armed GuardRetaliate

    // A parry only "charges" off weak incoming hits — strong attacks parry to
    // zero damage but don't reward a counter. Threshold compares against
    // Hitbox.Damage (HP off the victim), NOT the knockback, so an attack that
    // shoves hard but barely hurts still qualifies. At 1.0 the whole slash kit
    // (0.5), the stab (0.25) and a creature's melee swing (1.0) charge, while the
    // heavy end — a brute's dive (1.6), a charged blast (1.5), a rail bolt or a
    // Zeus laser (2.6) — parries clean but hands back no counter.
    private const float GuardChargeMaxDamage = 1.0f;
    private const float GuardChargedSeconds   = 0.8f;
    // Cone half-angle in radians — 60° each side of facing → 120° total coverage.
    // Public because GuardAction's telegraph draws exactly this cone: the arc the
    // player sees and the arc ResolveGuard tests are the same number, so a retune
    // here can't leave the indicator lying about what's covered.
    public  const float GuardConeCos          = 0.5f;   // cos(60°)
    public static readonly float GuardConeHalfAngle = MathF.Acos(GuardConeCos);

    // Guard timing (ResolveGuard). A hit landing within GuardPerfectWindowSeconds of
    // the stance coming up is absorbed completely — no damage, no knockback, no
    // hitstun. Past that the guard leaks, and the leak ramps linearly over
    // GuardFalloffSeconds to the saturation values: at worst three quarters of the
    // percent and half the knockback come through. So a well-timed guard is a parry,
    // and a guard held as a permanent shield decays into a mediocre damage reduction
    // that still breaks on contact.
    //
    // 0.12 s is ~7 frames at 60 Hz — tight enough to be a read, loose enough to hit
    // on reaction to a telegraph.
    private const float GuardPerfectWindowSeconds    = 0.12f;
    private const float GuardFalloffSeconds          = 0.60f;
    private const float GuardMaxDamagePenetration    = 0.75f;
    private const float GuardMaxKnockbackPenetration = 0.50f;
    // How long the stance stays down after something got through it.
    private const float GuardBreakRecoverySeconds    = 0.50f;
    // ...and after any ordinary deactivation. Deliberately a touch longer than the
    // perfect window (0.12 s), so mashing Shift buys less than half the frames.
    private const float GuardCooldownSeconds         = 0.15f;

    // Hitstun tuning (COMBAT_FEEL_PLAN Phase 1). Hitstun scales with the incoming
    // knockback impulse — strong hits stun longer — instead of the old flat
    // 8-frame window: seconds = impulse × HitstunSecondsPerImpulse, clamped to
    // [Min, Max]. Reference points at the CURRENT strike speeds: Slash3 (u 300)
    // → 0.66 s, GuardRetaliate (380) → cap, Stab (650) → cap, an enemy lunge
    // (~270) → 0.59 s; crush impulses (700+) cap so a hard landing isn't a full
    // second of lockout. Slash1's tiny hold-hit relies on its
    // HitstunSecondsOverride (it would floor at Min).
    //
    // Was 0.00135 when every strike hit ~1.65× harder. Hitstun is the disadvantage
    // window, and nothing about it was wrong — so when the knockback pass below cut
    // the impulses feeding this, the rate went up to keep the WINDOWS where they
    // were. Dropping knockback and shortening hitstun with it would have quietly
    // deleted combo pressure too.
    //
    // Follow-up hits while hitstun is still active extend by only
    // HitstunExtensionScale × the fresh window — diminishing, so a true
    // stun-lock cannot grow unbounded (same principle as the old 8+4+4).
    private const float HitstunSecondsPerImpulse = 0.0022f;
    private const float MinHitstunSeconds        = 0.10f;
    private const float MaxHitstunSeconds        = 0.70f;
    private const float HitstunExtensionScale    = 0.5f;

    // Stun tuning. Threshold compares against HitResult.Strength (pre-mass): the
    // authored impulse magnitude for Impulse-mode hits, the closing speed u for
    // Collision-mode hits — so attack strength controls stun-vs-not independent
    // of target Mass either way.
    //
    // Rescaled 440 → 280 alongside the knockback cut, which is what keeps the
    // designed spectrum rather than collapsing it: with every strike speed down
    // ~40%, holding 440 would have left the stab as the only move in the game that
    // stuns. At 280 the same moves cross as before — Slash3 (u 300),
    // GuardRetaliate (380), Stab (650), Pulse (impulse 300) — while the hold
    // slashes (60–80), CrouchSlash/AirTurn (200), AirSlash1/2 (150/230) and, by
    // design, every creature attack stay hitstun-only. A fast dive can still push
    // a swing over the line, which is speed earning the stun.
    private const float StunImpulseThreshold = 280f;
    private const float StunSeconds          = 0.6f;

    // Hitstop tuning. Deliberately much shorter than hitstun — this is a freeze-frame
    // punch, not a disadvantage window — scaled the same way (impulse-derived, clamped)
    // so a light tap barely pauses and a heavy hit holds noticeably longer.
    // Rate raised with HitstunSecondsPerImpulse and for the same reason — the
    // freeze-frame should read the same after the knockback cut.
    private const float HitstopSecondsPerImpulse = 0.0004f;
    private const float MinHitstopSeconds        = 0.03f;
    private const float MaxHitstopSeconds        = 0.12f;

    // While hitstunned, the victim's self-control is muted so knockback actually
    // displaces (COMBAT_FEEL_PLAN Phase 1). Applied by PlayerCharacter.Update as
    // movement-modifier scalars — the same channel actions use — together with
    // MovementModifiers.PreserveExternalVelocity so the air/walk speed caps don't
    // brake externally-applied velocity back down. The residual accel IS the
    // directional-influence (DI) budget; raise to give victims more say.
    public const float HitstunAccelScale    = 0.15f;
    public const float HitstunDragScale     = 0.2f;
    public const float HitstunFrictionScale = 0.3f;

    // `hitstunSecondsOverride` ≥ 0 replaces the impulse-derived window — used by
    // weak multi-hit attacks (hold-slashes) whose tiny impulse should still carry
    // real hitstun. `dt` is the caller's fixed timestep for seconds→frames.
    // `muteControl` = false for self-inflicted registration (crush landings):
    // jump still gates, but movement modifiers are left alone.
    public void OnHitRegistered(int currentFrame, float impulse, float dt,
                                float hitstunSecondsOverride = -1f, bool muteControl = true)
    {
        LastHitImpulse   = impulse;
        LastHitFrame     = currentFrame;

        float seconds = hitstunSecondsOverride >= 0f
            ? hitstunSecondsOverride
            : Math.Clamp(impulse * HitstunSecondsPerImpulse, MinHitstunSeconds, MaxHitstunSeconds);
        int frames = SimFrames.FromSeconds(seconds, dt);
        if (HitstunActive)
            frames = Math.Max(1, (int)(frames * HitstunExtensionScale));

        int newHitstunExpire = currentFrame + frames;
        if (newHitstunExpire > HitstunExpireFrame) HitstunExpireFrame = newHitstunExpire;
        HitstunActive = true;
        if (muteControl) HitstunMutesControl = true;

        if (impulse >= StunImpulseThreshold)
        {
            int newStunExpire = currentFrame + SimFrames.FromSeconds(StunSeconds, dt);
            if (newStunExpire > StunExpireFrame) StunExpireFrame = newStunExpire;
            StunActive = true;
        }

        // Real combat hits only — a self-inflicted crush/landing (muteControl=false)
        // shouldn't also freeze the player mid-fall.
        if (muteControl)
        {
            float hitstopSeconds = Math.Clamp(impulse * HitstopSecondsPerImpulse,
                                              MinHitstopSeconds, MaxHitstopSeconds);
            int newHitstopExpire = currentFrame + SimFrames.FromSeconds(hitstopSeconds, dt);
            if (newHitstopExpire > HitstopExpireFrame) HitstopExpireFrame = newHitstopExpire;
            HitstopActive = true;
        }
    }

    // Successful tech (Phase 4): end the launch (hitstun + stun + control-mute) and
    // grant a short i-frame window so the recovery isn't immediately re-punished.
    // Called by TumbleState when the tech input lands inside the window.
    public void Tech(int currentFrame, float dt, float invulnSeconds)
    {
        HitstunActive       = false;
        HitstunMutesControl = false;
        StunActive          = false;
        int expire = currentFrame + SimFrames.FromSeconds(invulnSeconds, dt);
        if (expire > InvulnExpireFrame) InvulnExpireFrame = expire;
    }

    public void Tick(int currentFrame, bool guardHeld)
    {
        if (HitstunActive && currentFrame >= HitstunExpireFrame)
        {
            HitstunActive       = false;
            HitstunMutesControl = false;
        }
        if (StunActive    && currentFrame >= StunExpireFrame)    StunActive    = false;
        if (GrabbedActive && currentFrame >= GrabbedExpireFrame) GrabbedActive = false;
        if (GuardCharged  && currentFrame >= GuardChargedExpireFrame) GuardCharged = false;
        // Break recovery needs BOTH halves: the countdown, and the button back up.
        if (GuardBroken && currentFrame >= GuardBreakExpireFrame && !guardHeld) GuardBroken = false;
        if (HitstopActive && currentFrame >= HitstopExpireFrame) HitstopActive = false;
    }

    // Bring the stance up, stamping the frame the timing window is measured from.
    // GuardAction owns the calls; nothing else should touch GuardActive directly.
    public void BeginGuard(int currentFrame)
    {
        GuardActive     = true;
        GuardStartFrame = currentFrame;
    }

    // Drop the stance and start the re-entry cooldown. Called from GuardAction.Exit, so
    // it covers every way guard ends — released, cancelled by movement, or broken.
    public void EndGuard(int currentFrame, float dt)
    {
        GuardActive = false;
        // Earned: a perfect block re-arms immediately.
        if (GuardBlockRefund) { GuardBlockRefund = false; return; }
        int expire = currentFrame + SimFrames.FromSeconds(GuardCooldownSeconds, dt);
        if (expire > GuardCooldownExpireFrame) GuardCooldownExpireFrame = expire;
    }

    public bool GuardOnCooldown(int currentFrame) => currentFrame < GuardCooldownExpireFrame;

    // Filter an incoming hit through Guard. Returns how much of the hit survives the
    // stance; the caller applies the scales and, if Absorbed, skips the hit entirely.
    //
    // Three outcomes:
    //   - Not guarding, or the hit came from outside the front cone -> nothing filtered
    //     (GuardOutcome.None, full damage and knockback). An out-of-cone hit still
    //     breaks the stance: the guard didn't meet it, so it isn't holding anything.
    //   - Guarding and hit inside the perfect window -> Absorbed. No damage, no
    //     knockback, no hitstun, and a weak hit also charges GuardRetaliate. The stance
    //     drops (it stopped its hit) but pays no re-entry cooldown, so a player who
    //     keeps reading correctly can block a flurry one hit at a time.
    //   - Guarding and hit late -> partial penetration ramping with how long the button
    //     has been held, and the guard BREAKS (see GuardBroken).
    //
    // facing: +1 = facing right, -1 = facing left. knockbackImpulse is the attack's
    // directional impulse - the source direction is the opposite (attacker -> player,
    // so the "into the cone" vector is -knockbackImpulse).
    public GuardOutcome ResolveGuard(in Vector2 knockbackImpulse, float hitDamage, int facing,
                                     int currentFrame, float dt)
    {
        if (!GuardActive) return GuardOutcome.None;
        if (knockbackImpulse.LengthSquared() < 1e-4f) return GuardOutcome.None;
        var fromAttacker = -knockbackImpulse;
        fromAttacker.Normalize();
        var facingVec = new Vector2(facing == 0 ? 1f : facing, 0f);
        // dot > GuardConeCos => fromAttacker is within +/-60 deg of the facing direction.
        if (Vector2.Dot(fromAttacker, facingVec) < GuardConeCos)
        {
            BreakGuard(currentFrame, dt);
            return GuardOutcome.None;
        }

        int age     = currentFrame - GuardStartFrame;
        int perfect = SimFrames.FromSeconds(GuardPerfectWindowSeconds, dt);
        if (age <= perfect)
        {
            // A clean parry. The charge stays gated on weak hits - a perfectly-timed
            // block of a heavy attack is its own reward and doesn't also hand over the
            // counter (the same GuardChargeMaxDamage reasoning as before, now on top
            // of the timing requirement).
            bool charged = hitDamage <= GuardChargeMaxDamage;
            if (charged)
            {
                int newExpire = currentFrame + SimFrames.FromSeconds(GuardChargedSeconds, dt);
                if (newExpire > GuardChargedExpireFrame) GuardChargedExpireFrame = newExpire;
                GuardCharged = true;
            }

            LastParryFrame   = currentFrame;
            LastParryDir     = fromAttacker;
            LastParryCharged = charged;

            // The block spends the stance: GuardAction drops out next frame (its
            // CheckConditions requires GuardActive), and with the refund set it can
            // come straight back up. So a clean block stops ONE hit and hands the
            // player a fresh window for the next, rather than making a held guard
            // an omni-directional shield again.
            GuardActive      = false;
            GuardBlockRefund = true;
            return GuardOutcome.Absorb;
        }

        // Late. Linear ramp from "just missed the window" (leaks nothing) to the
        // saturation values, reached GuardFalloffSeconds later and held from there on.
        float falloff = MathF.Max(SimFrames.FromSeconds(GuardFalloffSeconds, dt), 1);
        float t       = MathHelper.Clamp((age - perfect) / falloff, 0f, 1f);
        BreakGuard(currentFrame, dt);
        return new GuardOutcome(absorbed:       false,
                                damageScale:    GuardMaxDamagePenetration    * t,
                                knockbackScale: GuardMaxKnockbackPenetration * t);
    }

    // Something got through the stance. Drops it and starts the recovery countdown -
    // GuardAction refuses to re-enter while GuardBroken, and Tick only clears the flag
    // once the countdown has expired AND the button is back up.
    private void BreakGuard(int currentFrame, float dt)
    {
        GuardActive           = false;
        GuardBlockRefund      = false;   // a break is never free
        GuardBroken           = true;
        GuardBreakExpireFrame = currentFrame + SimFrames.FromSeconds(GuardBreakRecoverySeconds, dt);
    }

    // Snapshot/restore (roadmap goal 4 §E). All fields are value types — a clone is
    // a flat field-copy with no aliasing back into the live combat state.
    public CombatState Clone() => (CombatState)MemberwiseClone();

    public void CopyFrom(CombatState o)
    {
        HitstunActive = o.HitstunActive; HitstunExpireFrame = o.HitstunExpireFrame;
        HitstunMutesControl = o.HitstunMutesControl;
        StunActive = o.StunActive; StunExpireFrame = o.StunExpireFrame;
        HitstopActive = o.HitstopActive; HitstopExpireFrame = o.HitstopExpireFrame;
        LastHitImpulse = o.LastHitImpulse; LastHitFrame = o.LastHitFrame;
        LastHitDir = o.LastHitDir;
        DamageTaken = o.DamageTaken;
        InvulnExpireFrame = o.InvulnExpireFrame;
        GrabbedActive = o.GrabbedActive; GrabbedExpireFrame = o.GrabbedExpireFrame;
        GrabStrength = o.GrabStrength;
        GuardActive = o.GuardActive;
        GuardCharged = o.GuardCharged; GuardChargedExpireFrame = o.GuardChargedExpireFrame;
        GuardStartFrame = o.GuardStartFrame;
        GuardCooldownExpireFrame = o.GuardCooldownExpireFrame;
        GuardBlockRefund = o.GuardBlockRefund;
        GuardBroken = o.GuardBroken; GuardBreakExpireFrame = o.GuardBreakExpireFrame;
        LastParryFrame = o.LastParryFrame; LastParryDir = o.LastParryDir;
        LastParryCharged = o.LastParryCharged;
    }
}
