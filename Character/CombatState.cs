using System;
using Microsoft.Xna.Framework;

namespace MTile;

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

    public float   LastHitImpulse;    public int LastHitFrame;

    // Escalation percent (COMBAT_FEEL_PLAN Phase 5). Monotonic within a life — every
    // hit taken adds to it; it only resets on KO/respawn. Scales the knockback applied
    // TO this player (KnockbackScale below), so early hits barely move you and high-%
    // hits launch you — into terrain, where the real HP damage happens via the crush
    // path. HP itself is a fast-regenerating pool; this percent is the lasting
    // pressure. Snapshotted (CopyFrom). No blast zones — KO is HP→0 from hard splats.
    public float DamagePercent;
    // Per point of a move's Hitbox.Damage, how many percent points an incoming hit
    // adds. Tuned so a light slash (Damage 0.5) adds ~7-8%.
    private const float PercentPerDamage    = 15f;
    // Knockback multiplier per percent point: applied = base × (1 + pct × this). At
    // 100% knockback is ~2.5×, at 200% ~4×.
    private const float KnockbackPerPercent = 0.015f;

    // Multiplier on incoming knockback at the current percent (Phase 5).
    public float KnockbackScale => 1f + DamagePercent * KnockbackPerPercent;
    // Register an incoming hit's contribution to the escalation percent. `hitDamage`
    // is the move's Hitbox.Damage (no longer applied to HP directly for players).
    public void AddPercent(float hitDamage) => DamagePercent += hitDamage * PercentPerDamage;

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
    public bool BlocksAttack => StunActive || GrabbedActive;
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

    // A parry only "charges" off weak incoming hits — strong attacks parry to
    // zero damage but don't reward a counter. Tuned so Slash1 (KbI 200, dmg = MaxHP/2
    // ≈ 1.5) DOES charge while Slash3 (KbI 380) and Stab (KbI 380) don't.
    // Threshold compares against Hitbox.Damage, NOT KnockbackImpulse, so an attack
    // with high knockback but low per-frame damage (e.g. a beam, sustained) still
    // qualifies.
    private const float GuardChargeMaxDamage = 1.0f;
    private const float GuardChargedSeconds   = 0.8f;
    // Cone half-angle in radians — 60° each side of facing → 120° total coverage.
    private const float GuardConeCos          = 0.5f;   // cos(60°)

    // Hitstun tuning (COMBAT_FEEL_PLAN Phase 1). Hitstun scales with the incoming
    // knockback impulse — strong hits stun longer — instead of the old flat
    // 8-frame window: seconds = impulse × HitstunSecondsPerImpulse, clamped to
    // [Min, Max]. Reference points: Slash1's reduced 60-impulse hold-hit relies on
    // its HitstunSecondsOverride (it would floor at Min); Slash3/Stab (380) →
    // ~0.51 s; Pulse (450) → ~0.61 s; crush impulses (700+) cap at Max so a hard
    // landing isn't a full second of lockout.
    //
    // Follow-up hits while hitstun is still active extend by only
    // HitstunExtensionScale × the fresh window — diminishing, so a true
    // stun-lock cannot grow unbounded (same principle as the old 8+4+4).
    private const float HitstunSecondsPerImpulse = 0.00135f;
    private const float MinHitstunSeconds        = 0.10f;
    private const float MaxHitstunSeconds        = 0.70f;
    private const float HitstunExtensionScale    = 0.5f;

    // Stun tuning. Threshold compares against HitResult.Strength (pre-mass): the
    // authored impulse magnitude for Impulse-mode hits, the closing speed u for
    // Collision-mode hits — so attack strength controls stun-vs-not independent
    // of target Mass either way. Collision u runs ≈ 1.33× the old impulse
    // numbers (the parity mapping), so the threshold moved 350 → 440 to keep the
    // designed spectrum: hold-slashes (60–80), CrouchSlash/AirTurn (u 320+vel),
    // AirSlash1/2 (u 240/375) stay hitstun-only at baseline — though a fast dive
    // can push a swing over the line, which is speed earning the stun. Slash3
    // (u 500), GuardRetaliate (u 560), Stab (u 950), Pulse (impulse 450), and
    // Bullet (1200) cross and flag StunActive on top.
    private const float StunImpulseThreshold = 440f;
    private const float StunSeconds          = 0.6f;

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

    public void Tick(int currentFrame)
    {
        if (HitstunActive && currentFrame >= HitstunExpireFrame)
        {
            HitstunActive       = false;
            HitstunMutesControl = false;
        }
        if (StunActive    && currentFrame >= StunExpireFrame)    StunActive    = false;
        if (GrabbedActive && currentFrame >= GrabbedExpireFrame) GrabbedActive = false;
        if (GuardCharged  && currentFrame >= GuardChargedExpireFrame) GuardCharged = false;
    }

    // Filter incoming hit through Guard. Returns true if the hit was parried —
    // caller should skip damage/knockback/hitstun entirely. A weak in-cone hit
    // also charges GuardRetaliate. Out-of-cone hits fall through to normal
    // damage even while GuardActive (parry has a coverage window, not omnidir).
    //
    // facing: +1 = facing right, -1 = facing left. knockbackImpulse is the
    // attack's directional impulse — the source direction is the opposite
    // (attacker → player → so the "into the cone" vector is -knockbackImpulse).
    public bool TryParry(in Vector2 knockbackImpulse, float hitDamage, int facing, int currentFrame, float dt)
    {
        if (!GuardActive) return false;
        if (knockbackImpulse.LengthSquared() < 1e-4f) return false;
        var fromAttacker = -knockbackImpulse;
        fromAttacker.Normalize();
        var facingVec = new Vector2(facing == 0 ? 1f : facing, 0f);
        // dot > GuardConeCos => fromAttacker is within ±60° of facing direction.
        if (Vector2.Dot(fromAttacker, facingVec) < GuardConeCos) return false;

        if (hitDamage <= GuardChargeMaxDamage)
        {
            int newExpire = currentFrame + SimFrames.FromSeconds(GuardChargedSeconds, dt);
            if (newExpire > GuardChargedExpireFrame) GuardChargedExpireFrame = newExpire;
            GuardCharged = true;
        }
        return true;
    }

    // Snapshot/restore (roadmap goal 4 §E). All fields are value types — a clone is
    // a flat field-copy with no aliasing back into the live combat state.
    public CombatState Clone() => (CombatState)MemberwiseClone();

    public void CopyFrom(CombatState o)
    {
        HitstunActive = o.HitstunActive; HitstunExpireFrame = o.HitstunExpireFrame;
        HitstunMutesControl = o.HitstunMutesControl;
        StunActive = o.StunActive; StunExpireFrame = o.StunExpireFrame;
        LastHitImpulse = o.LastHitImpulse; LastHitFrame = o.LastHitFrame;
        DamagePercent = o.DamagePercent;
        InvulnExpireFrame = o.InvulnExpireFrame;
        GrabbedActive = o.GrabbedActive; GrabbedExpireFrame = o.GrabbedExpireFrame;
        GrabStrength = o.GrabStrength;
        GuardActive = o.GuardActive;
        GuardCharged = o.GuardCharged; GuardChargedExpireFrame = o.GuardChargedExpireFrame;
    }
}
