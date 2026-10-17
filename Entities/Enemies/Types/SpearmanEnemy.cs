using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  SPEARMAN — the minion-tier enemy: a long, telegraphed stab and a spear that
//  has to grow back after every thrust.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it: set "Stage": "spearman" in game_config.json.
//
// The read, in one paragraph: while it holds a spear, the spear is its guard —
// any hit arriving from inside the arc it can stab (forward, overhead, and a
// little below the horizontal) clanks off. It walks up to you and stabs: the
// spear swings to follow you while it charges, LOCKS (it flashes), then thrusts
// along that line out to full reach. It leads a moving target, so simply running
// or jumping at it gets you skewered and thrown back to where you started. Every
// thrust spends the spear; until it regrows the spearman is open from every side.
//
// Ways in, all intended:
//   • Bait the stab at the edge of its reach, step out, punish the regrow.
//   • Throw something at it. It stabs projectiles out of the air — which spends
//     the spear exactly like a stab does.
//   • Come at it from below. The spear cannot point steeply downward, so a body
//     under its ledge is outside both its attack and its guard.
//   • Get behind it (it faces you, so this means getting past the spear first).
//
// The spear's regrow clock is the one piece of state the action vars can't hold
// (it outlives the action, and survives a stagger that cancels it), so it lives in
// Scratch.F0, which EnemyEntity already snapshots. SpearmanController reads it but
// never writes it.
//
// Built on the TemplateEnemy pattern (brain / legs / arms); see that file for the
// rules every piece here follows.

// ── Spear geometry and the arc ──────────────────────────────────────────────
// Shared by the stab, the parry, the guard and the drawing, so the spear the
// player sees, the line that hurts, and the sector that blocks can never disagree.
public static class Spear
{
    // Tip distance from the body centre at full thrust. Effective reach against
    // the player is this plus their half-width (~5).
    public const float Reach     = 76f;
    // Visible spear length. While held or charging the tip sits at Reach − Thrust;
    // the thrust drives it the last Thrust px out.
    public const float Length    = 54f;
    public const float Thrust    = 18f;
    // Hitbox half-width along the shaft. The volume runs from the body centre to
    // the tip, so a body that has closed all the way in is still on the line.
    public const float HalfWidth = 3.5f;

    // The arc it can point into, as angles measured from straight-forward (by
    // facing): negative is up, positive is down (Y-down). Straight up and a touch
    // past it, down to 35° below the horizontal. Anything steeper below is the
    // blind spot — no stab, no guard.
    public static readonly float MaxUp   = MathHelper.ToRadians(95f);
    public static readonly float MaxDown = MathHelper.ToRadians(35f);

    // Angle of `d` in the facing frame: 0 = forward, −π/2 = up, +π/2 = down.
    public static float LocalAngle(Vector2 d, int facing) => MathF.Atan2(d.Y, d.X * facing);

    public static bool InArc(Vector2 d, int facing, float margin = 0f)
    {
        float a = LocalAngle(d, facing);
        return a >= -MaxUp - margin && a <= MaxDown + margin;
    }

    public static Vector2 FromLocal(float a, int facing) => new(MathF.Cos(a) * facing, MathF.Sin(a));

    // Unit direction toward `d`, pulled into the arc (to whichever edge is nearer
    // in angle, so a target slipping behind-and-up pins the spear vertical rather
    // than flipping it to the low edge).
    public static Vector2 ClampToArc(Vector2 d, int facing)
    {
        if (d.LengthSquared() < 1e-4f) return new Vector2(facing, 0f);
        float a = LocalAngle(d, facing);
        if (a > MaxDown || a < -MaxUp)
        {
            float toDown = MathF.Abs(a - MaxDown);
            float toUp   = MathF.Abs(a + MaxUp);
            // Wrap: an angle near ±π is ~equally far from both ends through "behind".
            toDown = MathF.Min(toDown, MathHelper.TwoPi - toDown);
            toUp   = MathF.Min(toUp,   MathHelper.TwoPi - toUp);
            a = toDown <= toUp ? MaxDown : -MaxUp;
        }
        return FromLocal(a, facing);
    }

    // Rotate unit `from` toward unit `to` by at most `maxStep` radians.
    public static Vector2 TurnToward(Vector2 from, Vector2 to, float maxStep)
    {
        float a0 = MathF.Atan2(from.Y, from.X);
        float a1 = MathF.Atan2(to.Y, to.X);
        float d  = MathHelper.WrapAngle(a1 - a0);
        float a  = a0 + MathHelper.Clamp(d, -maxStep, maxStep);
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }

    // The shaft as a hitbox: a rotated box from the body centre out to Reach.
    // `launch` (unit) switches it to collision-mode knockback along that axis, with
    // `strikeSpeed` / `minLaunch` as in Hitbox; zero keeps the plain impulse.
    public static void PublishShaft(in EnemyContext ctx, Vector2 dir, int hitId, float damage,
                                    Vector2 knockback, Color color,
                                    Vector2 launch = default, float strikeSpeed = 0f, float minLaunch = 0f)
    {
        float rotation = MathF.Atan2(dir.Y, dir.X);
        var poly   = Polygon.CreateRectangle(Reach, HalfWidth * 2f);
        var center = ctx.Self.Body.Position + dir * (Reach * 0.5f);
        bool collide = launch != Vector2.Zero;
        ctx.Hitboxes?.Publish(new Hitbox(
            poly.GetBoundingBox(center, rotation), hitId, damage * ctx.Self.Strength,
            knockback, Faction.Enemy, ctx.Self.Id, color,
            targets: HitTargets.EntitiesOnly,
            shape: poly, shapePos: center, shapeRotation: rotation,
            mode: collide ? KnockbackMode.Collision : KnockbackMode.Impulse,
            strikeDir: launch, strikeVelocity: launch * strikeSpeed,
            strikeMass: StrikeMass, restitution: 0.5f, minLaunch: minLaunch,
            origin: ctx.Self.Body.Position));
    }

    // The virtual striker's mass for collision-mode hits: well above the player's 2.5,
    // so the spear reads as planted — it reflects a body's approach speed back at it.
    private const float StrikeMass = 10f;

    // The spear's art: an entry of Assets/AnimationEffects/vfx.json, pivot at its tip.
    public const string Art = "spearman_spear";

    // Render: a spear from `tipDist − len` to `tipDist` along `dir`, with a head. `look`
    // names the art's animation (held / charge / lock / thrust / spent / regrow) shown at
    // `progress`; the line spear is its stand-in when the art isn't loaded. `artTip`
    // places the art when it differs from the line spear's tip: spent and regrow frames
    // draw the whole spear's span themselves, so they sit where the full spear would.
    public static void Draw(TelegraphList t, Vector2 pos, Vector2 dir, float tipDist, float len,
                            Color shaft, Color head, float thickness = 2f,
                            string look = null, float progress = 0f, float artTip = -1f)
    {
        int mark = look == null ? -1
            : t.Sprite(Art, look, progress, pos + dir * (artTip >= 0f ? artTip : tipDist),
                       MathF.Atan2(dir.Y, dir.X), Color.White, mirror: dir.X < 0f);
        if (len > 0.5f)
        {
            var tip  = pos + dir * tipDist;
            var butt = tip - dir * len;
            t.Line(butt, tip, shaft, thickness);
            if (len >= 6f)
            {
                float rot = MathF.Atan2(dir.Y, dir.X);
                t.RotatedRect(tip - dir * 2f, new Vector2(6f, 4f), rot, head);
            }
        }
        if (mark >= 0) t.EndFallback(mark);
    }

    public static readonly Color ShaftColor = new(150, 110, 70);
    public static readonly Color HeadColor  = new(210, 220, 235);
}

// ── Brain ───────────────────────────────────────────────────────────────────
// Walk to a hold distance just inside the stab's trigger range, hop small steps,
// and never walk off a ledge — a spearman holding high ground is the point of the
// "come at it from below" answer. While the spear regrows it plants and waits.
public sealed class SpearmanController : EnemyController
{
    // Horizontal hold distance. Ordering (widest → narrowest), per the ⚠ note on
    // TemplateAction.Range:
    //   effective reach   Spear.Reach + player half-width ≈ 81
    //   trigger Range     SpearStabAction.Range = 68 (centre-to-centre)
    //   hold band         PreferredRange ± Deadband = 52..60
    public float PreferredRange { get; init; } = 56f;
    public float Deadband       { get; init; } = 4f;
    public float AlertRange     { get; init; } = 360f;
    public int   MaxStepTiles   { get; init; } = 2;
    public float ProbeAhead     { get; init; } = 3f;

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var to    = ctx.ToPlayer;
        int side  = to.X >= 0f ? 1 : -1;
        var input = new EnemyInput
        {
            AimWorld   = ctx.Target.Position,
            // Every action gates itself (range, arc, spear in hand), and the parry
            // has to be free to fire at a projectile from anywhere.
            WantAttack = true,
        };
        if (ctx.Dist > AlertRange) return input;
        if (ctx.Self is SpearmanEnemy s && !s.SpearReady) return input;   // planted, regrowing

        if (MathF.Abs(to.X) > PreferredRange + Deadband && !DropAhead(in ctx, side))
        {
            input.MoveDir.X = side;
            input.Jump      = WardenController.StepAhead(in ctx, side, MaxStepTiles, ProbeAhead);
        }
        return input;
    }

    // True when the ground just past the leading edge falls away by more than a
    // tile — the edge of a ledge. It stops there rather than following you down.
    private bool DropAhead(in EnemyContext ctx, int side)
    {
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return false;
        var   b      = ctx.Self.Body.Bounds;
        float probeX = side > 0 ? b.Right + ProbeAhead : b.Left - ProbeAhead;
        const float ts = Chunk.TileSize;
        for (int k = 0; k < 2; k++)
            if (TileQuery.IsSolidAt(chunks, probeX, b.Bottom + (k + 0.5f) * ts)) return false;
        // A step UP ahead is not a drop.
        return !TileQuery.IsSolidAt(chunks, probeX, b.Bottom - 0.5f * ts);
    }
}

// ── Legs ────────────────────────────────────────────────────────────────────
// A steady advance, a bit under half the player's walk.
public sealed class SpearmanWalkState : WardenWalkState
{
    protected override float Speed => 42f;
}

// ── Arms: the stab ──────────────────────────────────────────────────────────
// Charge (the spear tracks you, turn-rate capped, leading your velocity) → Lock
// (frozen, flashing — the reaction window) → Thrust (a short lunge along the
// locked line; the shaft is live) → Recovery (spear spent, body planted). The
// spear's regrow starts on the first thrust frame and runs past the action's end:
// SpearmanEnemy.SpearReady is what keeps it from stabbing again until it's back.
public sealed class SpearStabAction : EnemyActionState
{
    private const float Charge   = 0.32f;
    private const float Lock     = 0.22f;
    private const float Active   = 0.10f;
    private const float Recovery = 0.30f;

    // Trigger: centre-to-centre, inside the reach (see SpearmanController).
    public  const float Range        = 68f;
    // How fast the spear can swing to follow during Charge, rad/s. Fast enough to
    // follow a jump; slow enough that a hard reversal shows as the spear lagging.
    private const float TurnRate     = 5f;
    // The lunge: horizontal speed during the thrust.
    private const float LungeSpeed   = 170f;

    private const float Damage       = 1.0f;
    // Knockback is a collision along a LAUNCH axis — straight away from the
    // spearman, lifted — not along the stab: a stab that catches a jumper overhead
    // should still throw them back where they came from, not up into the sky. The
    // collision reflects the target's approach speed, so rushing in is what earns the
    // biggest bounce. Measured against the player: a walk-in or a jump-in lands
    // ~190 px back (SpearmanEnemyTests); MinLaunch guarantees a standing target
    // still visibly flies. StrikeSpeed is the knob for "how far".
    private const float StrikeSpeed  = 320f;
    private const float MinLaunch    = 300f;
    private const float LaunchLift   = 0.45f;   // rise per unit of horizontal

    private static readonly Color ChargeColor = new(255, 200, 90);
    private static readonly Color LockColor   = Color.White;
    private static readonly Color StrikeColor = new(255, 80, 50);

    public override int ActivePriority  => 30;
    public override int PassivePriority => 25;

    // Starts on where the target is now OR where it will be when the thrust lands —
    // so a body closing fast (a run, a jump) is met where it arrives, not where it
    // was. A walker who stops short after the trigger is out of reach at the strike:
    // that's the bait.
    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (ctx.Self is not SpearmanEnemy s || !s.SpearReady) return false;
        int facing = ctx.Facing == 0 ? 1 : ctx.Facing;
        float margin = MathHelper.ToRadians(4f);
        var to   = ctx.ToPlayer;
        var pred = Predict(in ctx, StrikeTime) - ctx.Self.Body.Position;
        bool now  = to.Length()   <= Range && Spear.InArc(to,   facing, margin);
        bool soon = pred.Length() <= Range && Spear.InArc(pred, facing, margin)
                    && Vector2.Dot(to, ctx.Target.Velocity) < 0f;
        if (!now && !soon) return false;
        return EnemyAim.HasLineOfSight(ctx.Self.Body.Position, ctx.Target.Position, ctx.Spawner?.Chunks, 8f);
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.LockedAim    = Spear.ClampToArc(AimPoint(in ctx, 0f) - ctx.Self.Body.Position, v.LockedFacing);
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Charge + Lock;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    // Mid-thrust, from the start of the action.
    private const float StrikeTime = Charge + Lock + Active * 0.5f;

    // Where the target will be `dt` from now if it keeps doing what it's doing. A
    // body moving vertically is airborne and gets gravity, floored at the height it
    // (or the spearman) stands at so a landing isn't predicted through the ground.
    // Leading is what makes "just run at it" fail; the lock is what makes it fair.
    private static Vector2 Predict(in EnemyContext ctx, float dt)
    {
        var p = ctx.Input.AimWorld;
        var v = ctx.Target.Velocity;
        var q = p + v * dt;
        if (MathF.Abs(v.Y) > 1f)
            q.Y = MathF.Min(q.Y + 0.5f * Simulation.Gravity.Y * dt * dt, MathF.Max(p.Y, ctx.Self.Body.Position.Y));
        return q;
    }

    private static Vector2 AimPoint(in EnemyContext ctx, float t) => Predict(in ctx, MathF.Max(0f, StrikeTime - t));

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;

        if (t < Charge)
        {
            var want = Spear.ClampToArc(AimPoint(in ctx, t) - ctx.Self.Body.Position, v.LockedFacing);
            v.LockedAim = Spear.TurnToward(v.LockedAim, want, TurnRate * ctx.Dt);
            return;
        }
        if (t < v.WindupDuration) return;                                   // locked
        if (t >= v.WindupDuration + v.ActiveDuration) return;               // recovering

        if (ctx.Self is SpearmanEnemy s) s.SpendSpear();
        // Runs after the movement state (AttackHold damps X), so this wins.
        ctx.Self.Body.Velocity.X = v.LockedAim.X * LungeSpeed;
        float sx     = MathF.Abs(v.LockedAim.X) > 0.15f ? MathF.Sign(v.LockedAim.X) : v.LockedFacing;
        var   launch = Vector2.Normalize(new Vector2(sx, -LaunchLift));
        Spear.PublishShaft(in ctx, v.LockedAim, v.HitId, Damage, launch * 700f, StrikeColor,
                           launch, StrikeSpeed, MinLaunch);
    }

    // The spear itself is the telegraph: drawn along the live aim, pulled back,
    // with a faint guide to full reach so the length of the threat is legible.
    // Lock flashes it white; the thrust is the solid shaft; recovery shows the
    // spent spear fading where it struck.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var   dir  = v.LockedAim.LengthSquared() > 1e-4f ? v.LockedAim : new Vector2(v.LockedFacing, 0f);
        var   pos  = body.Position;
        float time = v.TimeInState;
        float held = Spear.Reach - Spear.Thrust;

        if (time < Charge)
        {
            float p = time / Charge;
            // Drawn back a little further at the start, settling to the held pose.
            float tip = held - 6f * (1f - p);
            t.Line(pos + dir * tip, pos + dir * Spear.Reach, ChargeColor * (0.15f + 0.25f * p));
            Spear.Draw(t, pos, dir, tip, Spear.Length, Spear.ShaftColor, Color.Lerp(Spear.HeadColor, ChargeColor, p),
                       look: "charge", progress: p);
        }
        else if (time < v.WindupDuration)
        {
            // Locked: the guide becomes solid and the whole spear flashes.
            float p     = (time - Charge) / Lock;
            bool  flash = ((int)(p * 6f) & 1) == 0;
            t.Line(pos + dir * held, pos + dir * Spear.Reach, LockColor * 0.7f, 1.5f);
            t.Rect(pos + dir * Spear.Reach, 3f, LockColor);
            Spear.Draw(t, pos, dir, held, Spear.Length, flash ? LockColor : Spear.ShaftColor, LockColor, 2.5f,
                       look: "lock", progress: p * 3f % 1f);   // the art's flash pair, 3× — same beat
        }
        else if (time < v.WindupDuration + v.ActiveDuration)
        {
            t.Ray(pos, MathF.Atan2(dir.Y, dir.X), Spear.Reach, Spear.HalfWidth * 2f, StrikeColor * 0.45f);
            Spear.Draw(t, pos, dir, Spear.Reach, Spear.Length, Spear.ShaftColor, StrikeColor, 3f,
                       look: "thrust", progress: (time - v.WindupDuration) / v.ActiveDuration);
        }
        else
        {
            float p = v.RecoveryDuration > 0f
                ? (time - v.WindupDuration - v.ActiveDuration) / v.RecoveryDuration : 1f;
            Spear.Draw(t, pos, dir, Spear.Reach, Spear.Length * (1f - p),
                       Spear.ShaftColor * (1f - p), Spear.HeadColor * (1f - p), look: "spent", progress: p);
        }
    }
}

// ── Arms: the parry ─────────────────────────────────────────────────────────
// A snap thrust at an incoming hostile projectile, aimed where it will be. Fires
// only with the spear in hand, preempts a stab that is still charging, and spends
// the spear just the same — so throwing something is a legitimate way to open it.
public sealed class SpearParryAction : EnemyActionState
{
    private const float Windup   = 0.05f;
    private const float Active   = 0.08f;
    private const float Recovery = 0.28f;

    // How far out it looks for something to stab, beyond Reach, px.
    private const float Lookout  = 60f;
    // 1 HP is plenty: thrown clods carry 0.1.
    private const float Damage   = 1.0f;
    private const float Knockback = 400f;

    private static readonly Color ParryColor = new(120, 220, 255);

    public override int ActivePriority  => 45;
    public override int PassivePriority => 40;   // beats the stab's Active (30)

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Self is SpearmanEnemy { SpearReady: true } && FindThreat(in ctx, out _, out _);

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        FindThreat(in ctx, out var aim, out int facing);
        v.LockedFacing = facing;
        v.LockedAim    = aim;
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
        if (t < v.WindupDuration || t >= v.WindupDuration + v.ActiveDuration) return;

        if (ctx.Self is SpearmanEnemy s) s.SpendSpear();
        Spear.PublishShaft(in ctx, v.LockedAim, v.HitId, Damage, v.LockedAim * Knockback, ParryColor);
    }

    // The nearest hostile projectile that is closing and will be within reach, inside
    // the arc, by the time the thrust lands. `aim` is the unit direction to stab;
    // `facing` the side it's on (it turns to parry from behind).
    private static bool FindThreat(in EnemyContext ctx, out Vector2 aim, out int facing)
    {
        aim = default; facing = ctx.Facing == 0 ? 1 : ctx.Facing;
        var hurt = ctx.Spawner?.Hurtboxes;
        if (hurt == null) return false;

        var   pos  = ctx.Self.Body.Position;
        float r    = Spear.Reach + Lookout;
        var   area = new BoundingBox(pos.X - r, pos.Y - r, pos.X + r, pos.Y + r);
        float lead = Windup + Active * 0.5f;
        float best = float.MaxValue;
        bool  found = false;

        foreach (var hb in hurt.Overlapping(area, exclude: Faction.Enemy))
        {
            if (ctx.Spawner.Resolve(hb.Target) is not Projectile p || p.IsDead) continue;
            var rel = p.Body.Position - pos;
            var vel = p.Body.Velocity;
            if (Vector2.Dot(rel, vel) >= 0f) continue;              // not coming at me
            var pred = rel + vel * lead;
            if (pred.Length() > Spear.Reach - 2f) continue;          // won't be in reach in time
            // Aim at where it will be, unless that's on top of me — then where it is.
            var d = pred.LengthSquared() > 64f ? pred : rel;
            int f = MathF.Abs(d.X) > 1f ? MathF.Sign(d.X) : facing;
            if (!Spear.InArc(d, f)) continue;                         // from below: can't reach it
            float dist = rel.LengthSquared();
            if (dist >= best) continue;
            best = dist; found = true;
            facing = f;
            aim = Vector2.Normalize(d);
        }
        return found;
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var   dir  = v.LockedAim.LengthSquared() > 1e-4f ? v.LockedAim : new Vector2(v.LockedFacing, 0f);
        var   pos  = body.Position;
        float time = v.TimeInState;

        if (time < v.WindupDuration)
            Spear.Draw(t, pos, dir, Spear.Reach - Spear.Thrust, Spear.Length, Spear.ShaftColor, ParryColor,
                       look: "held");
        else if (time < v.WindupDuration + v.ActiveDuration)
            Spear.Draw(t, pos, dir, Spear.Reach, Spear.Length, ParryColor, Color.White, 3f,
                       look: "thrust", progress: (time - v.WindupDuration) / v.ActiveDuration);
        else
        {
            float p = v.RecoveryDuration > 0f
                ? (time - v.WindupDuration - v.ActiveDuration) / v.RecoveryDuration : 1f;
            Spear.Draw(t, pos, dir, Spear.Reach, Spear.Length * (1f - p),
                       Spear.ShaftColor * (1f - p), ParryColor * (1f - p), look: "spent", progress: p);
        }
    }
}

// ── Body ────────────────────────────────────────────────────────────────────
public sealed class SpearmanEnemy : EnemyEntity
{
    // Player-sized, the Warden's tall hexagon.
    public const  float HalfHeight = 15f;
    private const float WidthScale = 0.4f;

    // Seconds from a thrust (stab or parry) until the spear is back in hand. Runs
    // through the action's recovery and on past it; the whole span is the opening.
    public const float RegrowSeconds = 1.1f;

    private static readonly Color GuardedColor = new(150, 160, 110);
    private static readonly Color OpenColor    = new(255, 120, 70);

    public override EntityKind Kind => EntityKind.Spearman;

    // Remaining regrow time, s (0 ⇒ spear in hand). Stored in Scratch.F0 so it
    // round-trips through the snapshot EnemyEntity already writes.
    public float SpearRegrow => Scratch.F0;
    // Spear in hand and on its feet: it can stab, parry, and guard.
    public bool SpearReady => Scratch.F0 <= 0f && !IsStaggered;

    // Idempotent across the active window's frames: only the first one starts the clock.
    public void SpendSpear()
    {
        if (Scratch.F0 <= 0f) Scratch.F0 = RegrowSeconds;
    }

    public SpearmanEnemy(Vector2 pos)
        : base(new PhysicsBody(CreateBodyPolygon(), pos),
               health: 1.5f,
               movement: new List<EnemyMovementState>
               {
                   new EnemyIdleState(),        // 0 — fallback
                   new SpearmanWalkState(),     // 1
                   new WardenHopState(),        // 2 — two-block steps
                   new EnemyAttackHoldState(),  // 3 — planted while committed
                   new EnemyStaggerState(),     // 4 — hits land as knockback
               },
               actions: new List<EnemyActionState>
               {
                   new SpearStabAction(),       // 0
                   new SpearParryAction(),      // 1
               },
               controller: new SpearmanController())
    {
        Mass   = 2f;
        Body.FrictionScale = 0.12f;
        Color  = GuardedColor;
        Sprite = null;           // drawn as its body outline in Color
    }

    private static Polygon CreateBodyPolygon()
    {
        var verts = Polygon.CreateRegular(HalfHeight, 6).GetVertices(Vector2.Zero);
        for (int i = 0; i < verts.Length; i++) verts[i].X *= WidthScale;
        return new Polygon(verts);
    }

    private int GuardFacingNow => CurrentActionIndex >= 0 ? CurrentActionVars.LockedFacing : Facing;

    // The guard: spear in hand, and the hit comes from inside the arc it covers.
    // Declined like the Warden's armor — no damage, no knockback, no stagger — but
    // the impulse is echoed so the attacker gets the clank recoil.
    public bool Blocks(in Hitbox hit)
    {
        if (!SpearReady) return false;
        var from = hit.Occluded ? hit.Origin : new Vector2(hit.Region.CenterX, hit.Region.CenterY);
        var d    = from - Body.Position;
        // Something right on top of the body has no meaningful direction; let it land.
        if (d.LengthSquared() < 9f) return false;
        int f = GuardFacingNow == 0 ? 1 : GuardFacingNow;
        return Spear.InArc(d, f);
    }

    public override Vector2 OnHit(in Hitbox hit, in Hurtbox hurtbox)
    {
        if (Blocks(in hit)) return hit.KnockbackImpulse;
        return base.OnHit(in hit, in hurtbox);
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (!IsDead && Scratch.F0 > 0f) Scratch.F0 = MathF.Max(0f, Scratch.F0 - dt);
        base.Update(dt, player, hitboxes, spawner);
        Color = SpearReady ? GuardedColor : OpenColor;
    }

    // Between actions the entity draws the spear itself: held up-and-forward, or
    // growing back from the hand while it regrows.
    public override void Telegraph(TelegraphList t)
    {
        base.Telegraph(t);
        if (IsDead || CurrentActionIndex >= 0) return;
        int   f    = Facing == 0 ? 1 : Facing;
        var   dir  = Spear.FromLocal(MathHelper.ToRadians(-30f), f);
        float grow = 1f - Scratch.F0 / RegrowSeconds;
        float tip  = 10f + (Spear.Length - 18f) * grow;
        Spear.Draw(t, Body.Position, dir, tip, Spear.Length * grow, Spear.ShaftColor,
                   grow >= 1f ? Spear.HeadColor : Spear.HeadColor * 0.4f,
                   look: grow >= 1f ? "held" : "regrow", progress: grow,
                   artTip: 10f + Spear.Length - 18f);   // the line spear's tip once fully grown
    }
}
