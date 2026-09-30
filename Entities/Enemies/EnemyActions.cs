using System;
using Microsoft.Xna.Framework;

namespace MTile;

// MVP action-FSM states. One concrete action: a melee swing with the standard
// Windup → Active → Recovery triad. Telegraph is purely a render concern, read
// off TimeInState / WindupDuration in EnemyActionVars — the sim never declares
// a separate telegraph descriptor. See Plans/ENEMY_CAPABILITY_FRAMEWORK.md §3.

// Forward melee swing. Every tunable comes from the ActionSpec handed to the
// ctor; ActionSpec.Default(ActionKind.Melee) is the stock swing. The stock
// numbers' rationale, preserved from when they were constants here:
//   Damage 1.0 — a full HP off the player's pool of 5, five clean swings down you.
//   Knockback (250, -110) — OnHit divides by target mass, so a player at Mass 2.5
//   gains ~100 px/s of shove and ~44 up: enough that a hit reads as contact and
//   interrupts you without taking the fight away. Was (460, -180), a body-check
//   off every routine swing. Launching is the player's job (Stab); |impulse| ≈ 120
//   stays under the 280 stun threshold, so no creature swing stuns on its own.
public class EnemyMeleeAction : EnemyActionState
{
    public EnemyMeleeAction() : this(ActionSpec.Default(ActionKind.Melee)) {}
    public EnemyMeleeAction(ActionSpec spec) { Spec = spec; }

    protected float   Windup            => Spec.Windup;
    protected float   Active            => Spec.Active;
    protected float   Recovery          => Spec.Recovery;
    protected float   Range             => Spec.MaxRange;
    protected float   VerticalSlack     => Spec.VerticalSlack;
    protected float   HitboxReach       => Spec.Reach;
    protected float   HitboxHalfHeight  => Spec.HalfHeight;
    protected float   Damage            => Spec.Damage;
    protected Vector2 Knockback         => Spec.Knockback;
    protected virtual Color   TelegraphColor    => Color.Red;
    protected virtual Color   StrikeColor       => Color.OrangeRed;

    public override int ActivePriority  => Spec.ActivePriority;
    public override int PassivePriority => Spec.PassivePriority;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist < Range && MathF.Abs(ctx.ToPlayer.Y) < VerticalSlack;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration) return;
        if (t >= v.WindupDuration + v.ActiveDuration) return;

        // Active window — publish a forward hitbox; CombatSystem dedupes by HitId
        // so a multi-frame active window only damages each target once per swing.
        float halfReach = HitboxReach * 0.5f;
        var center = ctx.Self.Body.Position + new Vector2(v.LockedFacing * (8f + halfReach), 0f);
        var region = new BoundingBox(
            center.X - halfReach, center.Y - HitboxHalfHeight,
            center.X + halfReach, center.Y + HitboxHalfHeight);
        ctx.Hitboxes?.Publish(new Hitbox(
            region, v.HitId, Damage,
            new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
            Faction.Enemy, ctx.Self.Id, StrikeColor,
            targets: HitTargets.EntitiesOnly,
            origin: ctx.Self.Body.Position));
    }

    // Telegraph reads everything off vars, just like the player's
    // slash dot or stab tip. Windup: a growing dot offset toward facing, color
    // ramping toward TelegraphColor. Strike flash: a faint slab where the
    // hitbox sits. Recovery: nothing — the body sprite alone reads the lockout.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        if (v.WindupDuration > 0f && time < v.WindupDuration)
        {
            float p = time / v.WindupDuration;       // 0 → 1 across windup
            int   sz = 2 + (int)(p * 4f);
            float off = 8f + p * 14f;
            var pos = body.Position + new Vector2(v.LockedFacing * off, 0f);
            var color = Color.Lerp(new Color(TelegraphColor, 100), TelegraphColor, p);
            t.Rect(pos, sz, color);
        }
        else if (time < v.WindupDuration + v.ActiveDuration)
        {
            float halfReach = HitboxReach * 0.5f;
            var c = body.Position + new Vector2(v.LockedFacing * (8f + halfReach), 0f);
            t.Rect(c, new Vector2(HitboxReach, HitboxHalfHeight * 2f), StrikeColor * 0.55f);
        }
    }
}

// Contact damage — the entire attack kit of a creature that hurts by existing.
// No windup and no telegraph: touching it IS the attack, so a tell would be
// describing something that already happened.
//
// Three things here are deliberate and each breaks something if changed:
//
//   * Committed stays FALSE. Every other action in this file sets it on Enter to
//     freeze the body mid-swing, but EnemyFlyState's preconditions are
//     `!IsActionCommitted` — a committed contact hit would drop the bird out of
//     flight and it would fall out of the sky the first time it touched anyone.
//
//   * Recovery is the re-hit cooldown, and it is the reason this is an action at
//     all rather than a hitbox published from the movement state. CombatSystem
//     dedupes by HitId, so one long-lived hitbox damages a target exactly once
//     ever; a fresh HitId per Enter turns "sit inside the bird" into a repeating
//     tick at Cooldown intervals, which is the behaviour a hazard needs.
//
//   * The hitbox is centred on the body and sized to it, so the damage region is
//     the creature. The precondition below is only a cheap broad gate — actual
//     overlap against the player's hurtbox is CombatSystem's call.
//
// Spec mapping (ActionSpec.Default(ActionKind.Contact) is the stock hazard):
//   HalfWidth  — half-extent of the damage box; slightly over the body radius so
//                contact registers on touch rather than on overlap.
//   MaxRange   — trigger range. Generous: it only has to be wide enough that the
//                box gets published on the frame contact happens.
//   Damage 0.4 — well under the 1.0 a committed melee swing deals; brushing a
//                hazard that repeats on a cooldown should sting, not trade evenly
//                with an attack the player could read.
//   Knockback  — signed away from the creature at Enter, with lift so a hit reads
//                as being knocked off rather than shoved into the floor. Light
//                (was (340, -240)): walking into a creature should push you off it.
//   Active     — the short damage window; Recovery is the re-hit cooldown.
public class EnemyContactAction : EnemyActionState
{
    public EnemyContactAction() : this(ActionSpec.Default(ActionKind.Contact)) {}
    public EnemyContactAction(ActionSpec spec) { Spec = spec; }

    protected float   BodyHalfExtent => Spec.HalfWidth;
    protected float   TriggerRange   => Spec.MaxRange;
    protected float   Damage         => Spec.Damage;
    protected Vector2 Knockback      => Spec.Knockback;
    protected float   ActiveWindow   => Spec.Active;
    protected float   Cooldown       => Spec.Recovery;
    protected virtual Color StrikeColor    => new(215, 120, 90);

    // Low priority by default: this is what the creature does when it has nothing
    // better to do, and any real attack a blueprint pairs it with should win.
    public override int ActivePriority  => Spec.ActivePriority;
    public override int PassivePriority => Spec.PassivePriority;

    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Dist < TriggerRange;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        // Push the player away from the creature, not along its facing: a bird
        // clipped from behind should still knock the player backwards.
        v.LockedFacing = ctx.ToPlayer.X >= 0f ? 1 : -1;
        v.HitId        = ctx.Spawner.HitIds.Next();
        // NOT Committed — see the header. Flight has to survive the hit.
        PopulateDurations(ref v);
    }

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = 0f;
        v.ActiveDuration   = ActiveWindow;
        v.RecoveryDuration = Cooldown;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        if (v.TimeInState >= v.ActiveDuration) return;   // cooling down

        var c = ctx.Self.Body.Position;
        var region = new BoundingBox(
            c.X - BodyHalfExtent, c.Y - BodyHalfExtent,
            c.X + BodyHalfExtent, c.Y + BodyHalfExtent);
        ctx.Hitboxes?.Publish(new Hitbox(
            region, v.HitId, Damage,
            new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
            Faction.Enemy, ctx.Self.Id, StrikeColor,
            targets: HitTargets.EntitiesOnly,
            origin: c));
    }

    // A brief flash on the body while the box is live. There is no windup tell to
    // draw — by the time this renders, the hit has landed — so this exists to
    // explain the damage after the fact, not to warn about it.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.TimeInState >= v.ActiveDuration) return;
        t.Rect(body.Position, new Vector2(BodyHalfExtent * 2f), StrikeColor * 0.5f);
    }
}

// Forward lunge. Active window overrides Body.Velocity.X so the brute glides
// into the player; the hitbox is published on the body itself so contact during
// the dash damages on touch (à la StalkerEnemy.Lunge). Mid-range trigger
// disjoint from EnemyMeleeAction so the two don't fight for the same situation.
//
// Stock spec (ActionSpec.Default(ActionKind.Lunge)): MinRange 36 is disjoint with
// the melee swing's 32. Knockback (300, -140) is heavier than the swing — a lunge
// is a committed dash, so it shoves noticeably harder (~120 px/s horizontal + ~56
// up against player Mass 2.5) — but still short of a launch and under the stun
// threshold: the creature kit trades in interruption and chip, not in juggles.
public class EnemyLungeAction : EnemyActionState
{
    public EnemyLungeAction() : this(ActionSpec.Default(ActionKind.Lunge)) {}
    public EnemyLungeAction(ActionSpec spec) { Spec = spec; }

    protected float   Windup       => Spec.Windup;
    protected float   Active       => Spec.Active;
    protected float   Recovery     => Spec.Recovery;
    protected float   MinRange     => Spec.MinRange;
    protected float   MaxRange     => Spec.MaxRange;
    protected float   VertSlack    => Spec.VerticalSlack;
    protected float   LungeSpeed   => Spec.Speed;
    protected float   HitHalfWidth => Spec.HalfWidth;
    protected float   HitHalfHeight=> Spec.HalfHeight;
    protected float   Damage       => Spec.Damage;
    protected Vector2 Knockback    => Spec.Knockback;

    public override int ActivePriority  => Spec.ActivePriority;
    public override int PassivePriority => Spec.PassivePriority;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist >= MinRange && ctx.Dist <= MaxRange
        && MathF.Abs(ctx.ToPlayer.Y) < VertSlack;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration) return;
        if (t >= v.WindupDuration + v.ActiveDuration) return;

        // Active — overwrite the movement-FSM velocity baseline with the dash.
        // Runs after movement.Update because EnemyEntity orders action.Update last.
        ctx.Self.Body.Velocity.X = v.LockedFacing * LungeSpeed;

        // Body-anchored hitbox; CombatSystem dedupes via HitId so a multi-frame
        // active window only damages each target once per lunge.
        var p = ctx.Self.Body.Position;
        var region = new BoundingBox(
            p.X - HitHalfWidth, p.Y - HitHalfHeight,
            p.X + HitHalfWidth, p.Y + HitHalfHeight);
        ctx.Hitboxes?.Publish(new Hitbox(
            region, v.HitId, Damage,
            new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
            Faction.Enemy, ctx.Self.Id, Color.MediumPurple,
            targets: HitTargets.EntitiesOnly,
            origin: ctx.Self.Body.Position));
    }

    // Layered telegraph — readable across the 0.35s windup. Phases:
    //   0 → 0.50  danger bar forms along the lunge path
    //   0.50→0.80 bar pulses + side ticks pop out at the tip (suggesting hit zone)
    //   0.80→1.00 anticipation recoil flash behind the body
    // Active: forward streak + 3 fanning speed lines. Recovery: nothing —
    // body sprite alone reads the post-strike lockout (same as the player slash).
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        const int BarMaxLen     = 64;
        const float HitZoneTickStart = 0.50f;
        const float RecoilStart      = 0.80f;
        var hot = Color.MediumPurple;

        float time = v.TimeInState;
        if (v.WindupDuration > 0f && time < v.WindupDuration)
        {
            float p = time / v.WindupDuration;

            // Phase 1: danger bar — extends + brightens across full windup. Pulse
            // (fast sin) layers in starting at HitZoneTickStart so the late
            // half reads as "imminent" rather than steady.
            int barLen = (int)(p * BarMaxLen);
            var origin = body.Position + new Vector2(v.LockedFacing * 12f, 0f);
            var start  = v.LockedFacing > 0 ? origin : origin + new Vector2(-barLen, 0f);
            float pulse = p < HitZoneTickStart ? 1f
                : 0.55f + 0.45f * MathF.Abs(MathF.Sin(time * 32f));
            t.Box((int)start.X, (int)start.Y - 1, barLen, 2,
                  hot * ((0.25f + 0.55f * p) * pulse));

            // Phase 2: hit-zone tick marks at the tip — vertical ticks framing
            // the impact band so the player reads "this is where it hits".
            if (p > HitZoneTickStart)
            {
                float tickP = (p - HitZoneTickStart) / (1f - HitZoneTickStart);
                int tickH = 4 + (int)(tickP * 4f);
                float tipX = v.LockedFacing > 0 ? origin.X + barLen : origin.X - barLen;
                t.Box((int)tipX - 1, (int)origin.Y - tickH - 2, 2, tickH, hot * tickP);
                t.Box((int)tipX - 1, (int)origin.Y + 2,         2, tickH, hot * tickP);
            }

            // Phase 3: anticipation recoil — a pulsing mark BEHIND the body,
            // reading as "I'm winding up to spring forward." Sized + alpha-
            // modulated by a half-sine so it crescendos through the last 20%.
            if (p > RecoilStart)
            {
                float rp = (p - RecoilStart) / (1f - RecoilStart);
                float anticip = MathF.Sin(rp * MathF.PI);
                int sz = 3 + (int)(anticip * 5f);
                var back = body.Position + new Vector2(-v.LockedFacing * (8f + anticip * 4f), 0f);
                t.Rect(back, sz, hot * (0.5f + 0.5f * anticip));
            }
        }
        else if (time < v.WindupDuration + v.ActiveDuration)
        {
            // Active: main streak through the body PLUS three fanning speed
            // lines behind so the dash reads as motion rather than a teleport.
            var p = body.Position;
            t.Box((int)(p.X - 14), (int)(p.Y - 1), 28, 2, hot * 0.75f);
            for (int i = 0; i < 3; i++)
            {
                int yOff   = -7 + i * 7;   // -7, 0, +7 vertical spread
                int len    = 12 - i * 3;
                int lineX  = (int)(p.X - v.LockedFacing * (10 + i * 5));
                int startX = v.LockedFacing > 0 ? lineX - len : lineX;
                t.Box(startX, (int)(p.Y + yOff), len, 1, hot * (0.55f - i * 0.12f));
            }
        }
    }
}

// Aerial slam. Only triggers when the brute is mid-air AND falling AND the
// player is below — i.e. as the natural finisher to an EnemyJumpState arc.
// Quick windup (the body is already committed to falling); active publishes a
// downward-biased hitbox just below the brute. Big damage, sideways+up
// knockback that reads as an overhead smash punting the player away.
//
// The "I'm airborne and aimed at you" precondition is what makes this feel
// distinct from melee. It pairs with EnemyJumpState to give the brute a
// vertical engagement pattern.
//
// Stock spec (ActionSpec.Default(ActionKind.Slam)): Damage 1.6 is the kit's
// heaviest single blow — a third of the player's pool per connect — and Knockback
// (360, -110) its hardest shove (~145 px/s + 45 up), but a shove: a diving
// body-slam, not a launcher. |impulse| ≈ 380 does clear the 280 stun threshold,
// which is the one creature attack that should. Passive 30 wins vs melee/lunge
// when airborne.
public class EnemySlamAction : EnemyActionState
{
    public EnemySlamAction() : this(ActionSpec.Default(ActionKind.Slam)) {}
    public EnemySlamAction(ActionSpec spec) { Spec = spec; }

    protected float   Windup        => Spec.Windup;
    protected float   Active        => Spec.Active;
    protected float   Recovery      => Spec.Recovery;
    protected float   MinFallSpeed  => Spec.FallSpeedMin;
    protected float   MaxHorizDist  => Spec.MaxRange;
    protected float   HitHalfWidth  => Spec.HalfWidth;
    protected float   HitHalfHeight => Spec.HalfHeight;
    protected float   HitOffsetY    => Spec.Reach;       // hitbox sits below the body
    protected float   Damage        => Spec.Damage;
    protected Vector2 Knockback     => Spec.Knockback;

    public override int ActivePriority  => Spec.ActivePriority;
    public override int PassivePriority => Spec.PassivePriority;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Self.Body.Velocity.Y > MinFallSpeed
        && ctx.ToPlayer.Y > 0f
        && MathF.Abs(ctx.ToPlayer.X) < MaxHorizDist;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration) return;
        if (t >= v.WindupDuration + v.ActiveDuration) return;

        var c = ctx.Self.Body.Position + new Vector2(0f, HitOffsetY);
        var region = new BoundingBox(
            c.X - HitHalfWidth, c.Y - HitHalfHeight,
            c.X + HitHalfWidth, c.Y + HitHalfHeight);
        ctx.Hitboxes?.Publish(new Hitbox(
            region, v.HitId, Damage,
            new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
            Faction.Enemy, ctx.Self.Id, Color.Crimson,
            targets: HitTargets.EntitiesOnly,
            origin: ctx.Self.Body.Position));
    }

    // Telegraph: short windup, so the visual is dense — overhead chevron forming
    // above the brute (anticipation "raised arms"), plus a downward target mark
    // that snaps into place under the body. Active: full vertical strike streak.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var hot = Color.Crimson;
        float time = v.TimeInState;
        var c0  = body.Position;

        if (v.WindupDuration > 0f && time < v.WindupDuration)
        {
            float p = time / v.WindupDuration;

            // Overhead chevron — two angled bars meeting above the body, growing
            // outward across the windup.
            int len = 4 + (int)(p * 10f);
            for (int s = 0; s < len; s++)
            {
                var l = c0 + new Vector2(-s,     -16 - s);
                var r = c0 + new Vector2( s,     -16 - s);
                t.Box((int)l.X, (int)l.Y, 2, 2, hot * (0.4f + 0.6f * p));
                t.Box((int)r.X, (int)r.Y, 2, 2, hot * (0.4f + 0.6f * p));
            }

            // Downward target indicator under the body — appears at p > 0.4,
            // grows brighter as fire-time approaches.
            if (p > 0.4f)
            {
                float tp = (p - 0.4f) / 0.6f;
                int mark = 6 + (int)(tp * 6f);
                var t0 = c0 + new Vector2(0f, HitOffsetY);
                t.Rect(t0, new Vector2(mark, 2f), hot * tp);
                t.Rect(t0, new Vector2(2f, mark), hot * tp);
            }
        }
        else if (time < v.WindupDuration + v.ActiveDuration)
        {
            // Active: thick downward streak from body to hitbox center + impact slab.
            t.Box((int)c0.X - 2, (int)c0.Y, 4, (int)HitOffsetY, hot * 0.8f);
            var hc = c0 + new Vector2(0f, HitOffsetY);
            t.Rect(hc, new Vector2(HitHalfWidth * 2f, HitHalfHeight * 2f), hot * 0.55f);
        }
    }
}

// Long-range projectile attack. Spawns an EnergyBallProjectile aimed at the
// player's position captured at windup START so a moving target can sidestep.
// The spawn fires exactly once on the windup→active transition; no extra
// snapshot field is needed because the transition is computable from
// TimeInState + Dt at a fixed timestep.
//
// Stock spec (ActionSpec.Default(ActionKind.Ranged)): MinRange 90 is disjoint
// with the lunge's MaxRange. Speed and Damage are handed to the projectile at
// spawn, so a spec that changes them changes the shot — they are not decorative.
public class EnemyRangedAction : EnemyActionState
{
    public EnemyRangedAction() : this(ActionSpec.Default(ActionKind.Ranged)) {}
    public EnemyRangedAction(ActionSpec spec) { Spec = spec; }

    protected float Windup          => Spec.Windup;
    protected float Active          => Spec.Active;
    protected float Recovery        => Spec.Recovery;
    protected float MinRange        => Spec.MinRange;
    protected float MaxRange        => Spec.MaxRange;
    protected float ProjectileSpeed => Spec.Speed;
    protected float Damage          => Spec.Damage;
    protected float MuzzleOffset    => Spec.Reach;

    public override int ActivePriority  => Spec.ActivePriority;
    public override int PassivePriority => Spec.PassivePriority;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist >= MinRange && ctx.Dist <= MaxRange;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float prevT = v.TimeInState;
        v.TimeInState += ctx.Dt;

        // Fire on the exact windup→active transition. At a fixed timestep this
        // condition is true on exactly one frame per swing, so the snapshot
        // doesn't need a Fired flag — the projectile entity itself carries the
        // post-spawn state.
        if (prevT < v.WindupDuration && v.TimeInState >= v.WindupDuration)
        {
            var origin = ctx.Self.Body.Position;
            var toPlayer = ctx.Player.Body.Position - origin;
            Vector2 dir = toPlayer.LengthSquared() > 1e-4f
                ? Vector2.Normalize(toPlayer)
                : new Vector2(v.LockedFacing, 0f);
            var muzzle = origin + dir * MuzzleOffset;
            ctx.Spawner?.SpawnEntity(new EnergyBallProjectile(
                muzzle, dir, v.HitId, Faction.Enemy, ProjectileSpeed, Damage));
        }
    }

    // Layered telegraph across the 0.60s windup, then a brief muzzle flash that
    // bleeds into recovery so the projectile feels launched rather than spawned.
    // Phases (during windup):
    //   0 → 1.0   outer charging ring grows in radius + brightens
    //   0.2→1.0   6 inner particles orbit inward, converging at the muzzle
    //   0.7→1.0   secondary inner ring + bright pulsing core
    // Muzzle flash visible for ~5 frames after fire (covers Active + a slice
    // of Recovery so the "bang" reads at 30 fps).
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        const float CoreStart    = 0.70f;
        const float FlashSeconds = 0.18f;
        var hot = Color.LightCyan;

        float time = v.TimeInState;

        // Post-fire muzzle flash — bright square at the body, fading out across
        // FlashSeconds. Reads as the "bang" frame of the shot.
        if (time >= v.WindupDuration && time < v.WindupDuration + FlashSeconds)
        {
            float fp = (time - v.WindupDuration) / FlashSeconds;
            float fa = 1f - fp;
            int sz  = 14 - (int)(fp * 8f);
            var c   = body.Position;
            t.Rect(c, sz, hot * fa);
            t.Rect(c, new Vector2(sz * 2, 2f), hot * (fa * 0.6f));
            return;
        }

        if (v.WindupDuration <= 0f || time >= v.WindupDuration) return;

        float p = time / v.WindupDuration;
        var ringColor = Color.Lerp(new Color(hot, 60), hot, p);
        var c0 = body.Position;

        // Outer charging ring — 12 samples, radius grows 4 → 12 px across windup.
        int outerR = 4 + (int)(p * 8f);
        for (int i = 0; i < 12; i++)
        {
            float a   = i * MathHelper.TwoPi / 12f;
            var pos   = c0 + new Vector2(MathF.Cos(a), MathF.Sin(a)) * outerR;
            t.Rect(pos, 2f, ringColor);
        }

        // 6 inner particles orbiting and converging — start at radius 18, pulled
        // to radius 4 at fire-time. Angular position rotates as a function of time
        // so the swirl reads as energy gathering rather than a static pattern.
        if (p > 0.2f)
        {
            float gather = (p - 0.2f) / 0.8f;       // 0 → 1 across the late windup
            float orbitR = 18f * (1f - gather) + 4f * gather;
            float spin   = time * 12f;
            var particleColor = hot * (0.5f + 0.5f * gather);
            for (int i = 0; i < 6; i++)
            {
                float a = i * MathHelper.TwoPi / 6f + spin;
                var pos = c0 + new Vector2(MathF.Cos(a), MathF.Sin(a)) * orbitR;
                t.Rect(pos, 2f, particleColor);
            }
        }

        // Late-stage core: pulsing bright spot at the muzzle, signals "fire is
        // imminent." Half-sine envelope so the alpha crescendos toward fire.
        if (p > CoreStart)
        {
            float cp     = (p - CoreStart) / (1f - CoreStart);
            float pulse  = MathF.Sin(cp * MathF.PI);
            int   coreSz = 3 + (int)(pulse * 5f);
            t.Rect(c0, coreSz, Color.White * (0.4f + 0.6f * pulse));
        }
    }
}
