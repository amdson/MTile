using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Action-FSM states for the gauntlet trio (see EnemyFactory.RegisterBuiltIns for
// the blueprints that use them). Same contract as EnemyActions.cs: flyweight
// state objects, all per-activation data in EnemyActionVars, telegraph is
// entirely a Telegraph concern read off TimeInState / WindupDuration.
//
// What's new here relative to the MVP actions is the use of
// EnemyActionVars.LockedAim — a 2D aim direction frozen at Enter. LockedFacing
// (±1) is fine for an enemy that only ever swings horizontally; a sentry firing
// on a diagonal or a crawler lashing off a ceiling needs the real vector, and
// needs it frozen so the line the player reads during windup is exactly the line
// the hitbox occupies. Aiming live would make every telegraph a lie.

// ── Bastion: charged rail shot ──────────────────────────────────────────────
// An emplacement's whole design problem is that it never moves, so its threat
// has to come from commitment on the player's side rather than pressure on its
// own. This resolves it by being maximally honest and maximally punishing: a
// 1.35s windup that draws the exact firing line across the room, then a bolt
// that crosses it in ~4 frames and eats the cover you hid behind.
//
// The long recovery is load-bearing. It is the window in which the player is
// meant to cross the gallery, and it is why the Bastion reads as a puzzle
// ("when do I move?") rather than as a damage tax.
public class EnemyRailShotAction : EnemyActionState
{
    protected virtual float Windup       => 1.35f;
    protected virtual float Active       => 0.06f;
    protected virtual float Recovery     => 1.15f;
    // Min range keeps the Bastion from charging a shot at a player who is
    // already inside its guard — at that point it's just a stationary target,
    // which is the intended reward for closing the distance.
    protected virtual float MinRange     => 70f;
    protected virtual float MaxRange     => 520f;
    protected virtual float MuzzleOffset => 16f;
    // Length of the drawn sight line. Only cosmetic — the bolt's actual reach is
    // its speed × lifetime — but it should overshoot the engagement range so the
    // line always reads as "this goes through you", never as "this stops short".
    protected virtual float SightLength  => 620f;
    protected virtual int   BoltBudget   => 3;
    protected virtual Color BeamColor    => new(255, 90, 40);

    // Above the MVP melee (30/25) — nothing else is competing on a Bastion, but
    // the ordering matters if this is ever mixed into a fuller kit.
    public override int ActivePriority  => 34;
    public override int PassivePriority => 30;

    // Range band AND line of sight. Without the sight check a Bastion charges at
    // a player it cannot see — through the wall of the room it is standing in —
    // and its bolts quietly excavate that wall over the course of a fight. The
    // check is a read-only tile walk, so it stays deterministic and rollback-safe.
    //
    // Only the long-range action gets this. A melee-band attack like
    // EnemyLashAction is better off without it: a crawler's body sits within a
    // few px of the surface it is latched to, so a centre-to-centre ray clips
    // that surface constantly and would leave it unable to attack from exactly
    // the positions it is designed to attack from.
    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (ctx.Dist < MinRange || ctx.Dist > MaxRange) return false;
        return EnemyAim.HasLineOfSight(ctx.Self.Body.Position, ctx.Input.AimWorld,
                                       ctx.Spawner?.Chunks, MuzzleOffset);
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.LockedAim    = EnemyAim.AimAt(ctx.Input.AimWorld - ctx.Self.Body.Position, v.LockedFacing);
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

        // Exactly one frame per shot satisfies this at a fixed timestep, so no
        // "have I fired" flag needs snapshotting — same argument as
        // EnemyRangedAction. The projectile itself carries all post-spawn state.
        if (prevT < v.WindupDuration && v.TimeInState >= v.WindupDuration)
        {
            var dir    = v.LockedAim.LengthSquared() > 1e-4f ? v.LockedAim : new Vector2(v.LockedFacing, 0f);
            var muzzle = ctx.Self.Body.Position + dir * MuzzleOffset;
            ctx.Spawner?.SpawnEntity(new RailBoltProjectile(muzzle, dir, v.HitId, Faction.Enemy, BoltBudget));
        }
    }

    // The telegraph is the entire fight. Three layers, all keyed off windup
    // progress, all along the frozen LockedAim so what's drawn is what's fired:
    //   * the sight line itself — thin and dim at 0, thick and hot at 1
    //   * chevrons sliding inward along the line toward the muzzle
    //   * a muzzle core that swells, then strobes over the last 20%
    // After the shot, a brief recoil flash marks the release frame.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var dir = v.LockedAim.LengthSquared() > 1e-4f ? v.LockedAim : new Vector2(v.LockedFacing, 0f);
        float angle = MathF.Atan2(dir.Y, dir.X);
        var   origin = body.Position + dir * MuzzleOffset;
        float time = v.TimeInState;

        // Post-fire: short recoil bloom so the release frame is unmistakable.
        const float FlashSeconds = 0.20f;
        if (time >= v.WindupDuration && time < v.WindupDuration + FlashSeconds)
        {
            float fa = 1f - (time - v.WindupDuration) / FlashSeconds;
            t.Ray(origin, angle, SightLength, 3f + fa * 5f, Color.White * (fa * 0.85f));
            int sz = 18 - (int)((1f - fa) * 12f);
            t.Rect(origin, sz, Color.White * fa);
            return;
        }

        if (v.WindupDuration <= 0f || time >= v.WindupDuration) return;

        float p = time / v.WindupDuration;                     // 0 → 1 across windup

        // Sight line. Alpha and thickness both ramp so it's visible from the
        // first frame (you must be able to leave the line, not discover it late)
        // but only alarming near the end.
        t.Ray(origin, angle, SightLength,
              1f + p * 3f, Color.Lerp(new Color(BeamColor, 70), BeamColor, p) * (0.35f + 0.65f * p));

        // Chevrons converging on the muzzle — five markers whose distance
        // collapses as the shot nears, giving the windup a legible "clock".
        var perp = new Vector2(-dir.Y, dir.X);
        for (int i = 0; i < 5; i++)
        {
            float phase = 1f - ((p * 1.6f + i * 0.2f) % 1f);   // slides inward, wraps
            float d     = 40f + phase * 190f;
            var   c     = origin + dir * d;
            var   tint  = BeamColor * (0.25f + 0.75f * (1f - phase));
            t.Rect(c + perp * 5f, 2f, tint);
            t.Rect(c - perp * 5f, 2f, tint);
        }

        // Muzzle core: swells across the windup, then strobes on a fast square
        // wave over the last 20% — the "now" cue.
        int coreSz = 4 + (int)(p * 8f);
        var coreColor = Color.Lerp(BeamColor, Color.White, p);
        if (p > 0.80f && (int)(time * 30f) % 2 == 0) coreSz += 5;
        t.Rect(origin, coreSz, coreColor * (0.5f + 0.5f * p));
    }
}

// ── Pouncer: momentum slam ──────────────────────────────────────────────────
// A drop attack whose strength is its fall speed. There is no windup: the
// telegraph already happened, on the ground, as EnemyHopState's crouch, and the
// arc that follows is a second, continuous tell. Adding a windup here would
// telegraph the telegraph.
//
// The hitbox only exists while the body is genuinely falling fast, so the same
// enemy is harmless on the way up and lethal on the way down — which is what
// makes "get out from under it" the correct read, and what makes a slam that
// the player interrupted mid-rise feel earned rather than arbitrary.
//
// IMPORTANT: an enemy using this must NOT register EnemyAttackHoldState. That
// state brakes the body while an action is committed, which would bleed off the
// exact momentum this action is measuring. EnemyHopState is written to hold its
// latch through a committed action for the same reason.
public class EnemyPounceSlamAction : EnemyActionState
{
    protected virtual float Windup          => 0f;      // the fall IS the windup
    // Long enough to cover a full descent from the top of a hop arc; the hitbox
    // self-gates on fall speed, so an early landing just stops it publishing.
    protected virtual float Active          => 0.95f;
    protected virtual float Recovery        => 0.40f;
    // Below this the enemy is rising, hovering, or drifting — no attack. Also
    // the precondition, so the action can't even be selected on the way up.
    protected virtual float MinFallSpeed    => 170f;
    // Fall speed at which damage and knockback saturate. Above the terminal-ish
    // speed of a normal hop, so a pounce launched from a high ledge genuinely
    // hits harder than one from the floor.
    protected virtual float RefFallSpeed    => 560f;
    protected virtual float MinDamage       => 0.8f;
    protected virtual float MaxDamage       => 2.4f;
    protected virtual float MinKnockback    => 320f;
    protected virtual float MaxKnockback    => 780f;
    protected virtual float HitboxHalfWidth => 15f;
    protected virtual float HitboxReachDown => 16f;
    protected virtual Color SlamColor       => new(255, 200, 90);

    public override int ActivePriority  => 34;
    public override int PassivePriority => 30;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Self.Body.Velocity.Y > MinFallSpeed;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.LockedAim    = new Vector2(0f, 1f);      // slams are down, always
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

        // Re-gate on live fall speed every frame: landing (or being knocked
        // upward) ends the threat immediately, without waiting for the action to
        // time out. This is the whole "momentum-based" contract.
        float vy = ctx.Self.Body.Velocity.Y;
        if (vy <= MinFallSpeed) return;

        float m = MathHelper.Clamp((vy - MinFallSpeed) / MathF.Max(RefFallSpeed - MinFallSpeed, 1f), 0f, 1f);
        float damage    = MathHelper.Lerp(MinDamage,    MaxDamage,    m);
        float knockback = MathHelper.Lerp(MinKnockback, MaxKnockback, m);

        // Body-centred but biased downward, so the pouncer connects with what
        // it lands ON rather than with what it brushes past.
        var c = ctx.Self.Body.Position + new Vector2(0f, HitboxReachDown * 0.5f);
        var region = new BoundingBox(
            c.X - HitboxHalfWidth, c.Y - HitboxHalfWidth,
            c.X + HitboxHalfWidth, c.Y + HitboxHalfWidth + HitboxReachDown * 0.5f);

        // Launch outward-and-up relative to the pouncer, so a hit shoves the
        // player clear instead of pinning them under a body that's still
        // falling. Sign is taken live (not from LockedFacing) so a slam that
        // lands behind the target still pushes them away from it.
        float side = ctx.ToPlayer.X >= 0f ? 1f : -1f;
        var impulse = new Vector2(side * knockback * 0.75f, -knockback * 0.55f);

        ctx.Hitboxes?.Publish(new Hitbox(
            region, v.HitId, damage, impulse,
            Faction.Enemy, ctx.Self.Id, SlamColor,
            targets: HitTargets.EntitiesOnly,
            origin: ctx.Self.Body.Position));
    }

    // Speed-proportional tell, read straight off the live body velocity — the
    // one piece of state Telegraph is handed besides vars. Chevrons stack under the
    // body and the aura widens as the slam gets deadlier, so "how dangerous is
    // this right now" is legible without a HUD.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float vy = body.Velocity.Y;
        if (vy <= MinFallSpeed) return;

        float m = MathHelper.Clamp((vy - MinFallSpeed) / MathF.Max(RefFallSpeed - MinFallSpeed, 1f), 0f, 1f);
        var   c = body.Position;
        var   tint = Color.Lerp(SlamColor, Color.White, m);

        // Danger aura — a widening slab under the body matching the hitbox.
        int w = (int)(HitboxHalfWidth * 2f);
        int h = (int)(HitboxReachDown * (0.6f + 0.8f * m));
        t.Box((int)c.X - w / 2, (int)c.Y + 4, w, h, tint * (0.20f + 0.35f * m));

        // Stacked chevrons: one at the slowest lethal speed, up to four at full
        // momentum. Counting them is a direct read of incoming damage.
        int chevrons = 1 + (int)(m * 3f);
        for (int i = 0; i < chevrons; i++)
        {
            int y = (int)c.Y + 10 + i * 5;
            int cw = 12 - i * 2;
            t.Box((int)c.X - cw / 2, y, cw, 2, tint * (0.9f - i * 0.18f));
        }
    }
}

// ── Latcher: telegraphed lash ───────────────────────────────────────────────
// A medium-range strike along a frozen 2D axis. The 2D part is the point: a
// crawler spends most of its life on a wall or a ceiling, where a facing-based
// horizontal swing would either whiff entirely or connect in a direction that
// has nothing to do with where the enemy is pointing.
//
// Reach is roughly three body-lengths — long enough that "back off" is a real
// answer, short enough that it can't contest the room the way the Bastion can.
public class EnemyLashAction : EnemyActionState
{
    protected virtual float Windup      => 0.55f;
    protected virtual float Active      => 0.14f;
    protected virtual float Recovery    => 0.45f;
    // Min range keeps the lash from firing at point-blank, where the strike
    // polygon starts behind the target and reads as a phantom hit.
    protected virtual float MinRange    => 18f;
    protected virtual float MaxRange    => 62f;
    protected virtual float Reach       => 58f;
    protected virtual float HalfWidth   => 7f;
    protected virtual float Damage      => 1.3f;
    protected virtual float Knockback   => 430f;
    // Extra upward bias so a lash from a ceiling still launches the player
    // sideways-and-clear rather than straight into the floor.
    protected virtual float UpBias      => 150f;
    protected virtual Color LashColor   => new(120, 230, 190);

    public override int ActivePriority  => 32;
    public override int PassivePriority => 27;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist >= MinRange && ctx.Dist <= MaxRange;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.LockedAim    = EnemyAim.AimAt(ctx.Input.AimWorld - ctx.Self.Body.Position, v.LockedFacing);
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

        var dir = v.LockedAim.LengthSquared() > 1e-4f ? v.LockedAim : new Vector2(v.LockedFacing, 0f);
        float rotation = MathF.Atan2(dir.Y, dir.X);

        // Rotated box from the body out to the tip, exactly like the player's
        // stab: AABB for the broad phase, the polygon itself for narrow-phase
        // SAT, so a diagonal lash doesn't hit everything in its bounding square.
        var poly   = Polygon.CreateRectangle(Reach, HalfWidth * 2f);
        var center = ctx.Self.Body.Position + dir * (Reach * 0.5f);
        var aabb   = poly.GetBoundingBox(center, rotation);

        ctx.Hitboxes?.Publish(new Hitbox(
            aabb, v.HitId, Damage,
            dir * Knockback + new Vector2(0f, -UpBias),
            Faction.Enemy, ctx.Self.Id, LashColor,
            targets: HitTargets.EntitiesOnly,
            shape: poly, shapePos: center, shapeRotation: rotation,
            origin: ctx.Self.Body.Position));
    }

    // Windup extends a thin probe along the frozen axis, growing to full reach
    // and brightening; the strike is the same axis at full width. Because both
    // use LockedAim, what the player sees during the wind-up is precisely the
    // volume that becomes dangerous — the telegraph can't drift off the attack.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var dir = v.LockedAim.LengthSquared() > 1e-4f ? v.LockedAim : new Vector2(v.LockedFacing, 0f);
        float angle = MathF.Atan2(dir.Y, dir.X);
        float time = v.TimeInState;

        if (v.WindupDuration > 0f && time < v.WindupDuration)
        {
            float p = time / v.WindupDuration;
            // Ease-out so most of the extension happens early and the last third
            // is a held, quivering aim — the part the player reacts to.
            float ext = Reach * (1f - (1f - p) * (1f - p));
            t.Ray(body.Position, angle, ext, 1f + p * 2f,
                  Color.Lerp(new Color(LashColor, 80), LashColor, p));
            // Tip barb — a bright dot at the strike point.
            var tip = body.Position + dir * ext;
            int sz = 2 + (int)(p * 4f);
            t.Rect(tip, sz, Color.Lerp(LashColor, Color.White, p));
            return;
        }

        if (time < v.WindupDuration + v.ActiveDuration)
            t.Ray(body.Position, angle, Reach, HalfWidth * 2f, LashColor * 0.6f);
    }
}
