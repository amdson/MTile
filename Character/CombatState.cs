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

    // Most-recent hit's energy + direction. Read by the stun-threshold check in
    // OnHit and (later) by the guard-cone filter.
    public float   LastHitImpulse;    public int LastHitFrame;
    public Vector2 LastHitDirection;

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
    private const int   GuardChargedFrames    = 24;
    // Cone half-angle in radians — 60° each side of facing → 120° total coverage.
    private const float GuardConeCos          = 0.5f;   // cos(60°)

    // Hitstun tuning. Initial window is the no-jump lockout after a single hit;
    // extension is what each follow-up hit adds while hitstun is still active.
    // Diminishing: a 3-hit combo extends by Initial + 2*Extension = 16 frames
    // rather than 3*Initial = 24, so a true stun-lock cannot grow unbounded.
    private const int InitialHitstunFrames   = 8;
    private const int ExtensionHitstunFrames = 4;

    // Stun tuning. Threshold compares against the incoming Hitbox.KnockbackImpulse
    // magnitude (pre-mass-division) so attack strength controls stun-vs-not
    // independent of player Mass. At 350f, Slash1 (200), Slash2 (260), CrouchSlash
    // (240), AirSlash1 (180), AirSlash2 (280) all fall short of stunning — they
    // only land hitstun. Slash3 (380), Stab (380), and Bullet (1200) cross the
    // threshold and flag StunActive on top of hitstun. Duration is ~600ms at 30fps.
    private const float StunImpulseThreshold = 350f;
    private const int   StunFrames           = 18;

    public void OnHitRegistered(int currentFrame, float impulse, Vector2 direction)
    {
        LastHitImpulse   = impulse;
        LastHitDirection = direction;
        LastHitFrame     = currentFrame;

        int extension = HitstunActive ? ExtensionHitstunFrames : InitialHitstunFrames;
        int newHitstunExpire = currentFrame + extension;
        if (newHitstunExpire > HitstunExpireFrame) HitstunExpireFrame = newHitstunExpire;
        HitstunActive = true;

        if (impulse >= StunImpulseThreshold)
        {
            int newStunExpire = currentFrame + StunFrames;
            if (newStunExpire > StunExpireFrame) StunExpireFrame = newStunExpire;
            StunActive = true;
        }
    }

    public void Tick(int currentFrame)
    {
        if (HitstunActive && currentFrame >= HitstunExpireFrame) HitstunActive = false;
        if (StunActive    && currentFrame >= StunExpireFrame)    StunActive    = false;
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
    public bool TryParry(in Vector2 knockbackImpulse, float hitDamage, int facing, int currentFrame)
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
            int newExpire = currentFrame + GuardChargedFrames;
            if (newExpire > GuardChargedExpireFrame) GuardChargedExpireFrame = newExpire;
            GuardCharged = true;
        }
        return true;
    }
}
