using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  QUILLBACK — a small, fast, armored hedgehog with a spiked tail. All close
//  range: a pile of quick hits from every angle, behind a shell that heals.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it:  set "Stage": "quillback" in configs/game_config.json, then
//          dotnet run --project MTile.Desktop
//
//   SHELL   The armor is a shield bar over a tiny health bar (QuillbackEnemy.OnHit).
//           A hit drains the shell first; whatever the shell can't hold spills into
//           HP. The shell refills fast (ShieldRegen/s) after RegenDelay without being
//           hit, but a shell knocked to zero stays down for BrokenDelay — that's the
//           window to hurt the body. Knockback is heavy-armored while the shell holds.
//
//   FLURRY  (QuillbackFlurryAction) three claw jabs, each re-aimed at you on its own
//           first frame — any angle, so jumping over it is not a dodge.
//   SPIN    (QuillbackSpinAction)   the tail goes all the way round: a square of
//           hurt on every side of it. Its answer to being stood on or under.
//   ROLL    (QuillbackRollAction)   curls and barrels along the floor at you; it
//           breaks soft blocks on the way.
//   DIG     (QuillbackDigAction)    claws a two-tile bite out of whatever terrain is
//           between it and you — walls, floors you're standing under, your box.
//   PILLAR  (QuillbackPillarAction) a build hop: jumps and grows dirt under its own
//           feet, two tiles a hop, to climb up to a target it can't reach.
//   STORM   (QuillbackStormAction)  ONLY when you're far away: it curls with every
//           quill raised, then flings them out as a fast expanding ring. Terrain
//           stops the quills (occluded), so cover works. While it is winding up and
//           throwing, the shell is OPEN: any hit lands at VulnerableMultiplier ×
//           damage, shatters the shell, and knocks it into a long daze.
//
// Movement is a fast scurry (faster than the player walks) and a very short hop
// (about two tiles) that only climbs single-tile steps; anything taller it digs
// through or builds over.
//
// The brain is stateless (EnemyController's contract); its cooldowns and the
// commit-edge detector live in EnemyEntity.Scratch. The shell lives on the
// snapshotted energy meter (Energy/EnergyMax — it has no other use for one) with its
// regen delay in Scratch.F2, and the storm's "broken" flag in Scratch.I1; the body
// writes those two, the brain only reads them.
//
// Numbers are first guesses, deliberately untuned.


// ── Brain ───────────────────────────────────────────────────────────────────
public sealed class QuillbackController : EnemyController
{
    // Action indices — must match the order QuillbackEnemy's ctor registers them.
    public const int Flurry = 0, Spin = 1, Roll = 2, Dig = 3, Pillar = 4, Storm = 5;

    public float AlertRange    { get; init; } = 900f;
    // The storm's whole rule: never inside FarRange, never past the ring's reach.
    public float FarRange      { get; init; } = 190f;
    public float StormReach    { get; init; } = QuillbackStormAction.MaxRadius - 10f;
    public float StormCooldown { get; init; } = 4.5f;
    // Melee: inside SpinRange (or close and mostly above/below) the tail goes round;
    // otherwise inside FlurryRange it jabs.
    public float SpinRange     { get; init; } = 26f;
    public float FlurryRange   { get; init; } = 44f;
    // Roll: a level target at this horizontal distance band.
    public float RollMin       { get; init; } = 70f;
    public float RollMax       { get; init; } = 180f;
    public float RollCooldown  { get; init; } = 1.6f;
    // Climb when the target stands this far above it — directly overhead, or with a
    // wall in the way.
    public float ClimbRise     { get; init; } = 26f;
    public float ClimbUnder    { get; init; } = 40f;
    // Stop scurrying inside this horizontal distance.
    public float EngageRange   { get; init; } = 14f;

    // BrainScratch layout:
    //   I0  last frame's action index + 1          F0  storm cooldown remaining, s
    //   I1  storm broken flag (BODY-owned)          F1  roll cooldown remaining, s
    //   F3  last frame's action clock, s            F2  shell regen delay (BODY-owned)
    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var self = ctx.Self;
        ref var m = ref self.Scratch;
        float dt = ctx.Dt;

        // Commit edge: the index changing OR the clock running backwards (a finished
        // action re-entered inside one SelectAction never shows an idle frame).
        int   action = self.CurrentActionIndex;
        float clock  = self.CurrentActionTime;
        if (action >= 0 && (action != m.I0 - 1 || clock < m.F3))
        {
            if (action == Storm) m.F0 = StormCooldown;
            if (action == Roll)  m.F1 = RollCooldown;
        }
        m.I0 = action + 1;
        m.F3 = clock;
        if (m.F0 > 0f) m.F0 = MathF.Max(0f, m.F0 - dt);
        if (m.F1 > 0f) m.F1 = MathF.Max(0f, m.F1 - dt);

        var input = new EnemyInput { AimWorld = ctx.Target.Position };
        float dist = ctx.Dist;
        if (dist > AlertRange) return input;

        var   to     = ctx.ToPlayer;
        int   side   = to.X >= 0f ? 1 : -1;
        float rise   = -to.Y;                              // > 0 ⇒ target above
        var   chunks = ctx.Spawner?.Chunks;
        bool  los    = EnemyAim.HasLineOfSight(self.Body.Position, ctx.Target.Position, chunks, 14f);
        int   ahead  = QuillbackProbe.RiseAhead(ctx, side);
        bool  wall   = ahead >= 2;                         // too tall for the hop
        // Blind tracking: with the target out of sight it never waits for it to come
        // back into view. Above, it climbs whatever the target is doing (in sight, it
        // only climbs to one standing still, not under a jump); level or below, it
        // tunnels straight at it from any distance.
        bool  blind  = !los;
        bool  standing = MathF.Abs(ctx.Target.Velocity.Y) < 40f;
        bool  climb  = rise > ClimbRise && (standing || blind) && (MathF.Abs(to.X) < ClimbUnder || wall);
        bool  roofed = QuillbackProbe.Roofed(ctx);
        bool  canBuild = QuillbackPillarAction.CanBuild(ctx);

        int want = -1;
        if (dist >= FarRange && dist <= StormReach && m.F0 <= 0f && los)
            want = Storm;
        else if (dist <= SpinRange || (dist <= FlurryRange && MathF.Abs(to.Y) > MathF.Abs(to.X)))
            want = Spin;
        else if (dist <= FlurryRange)
            want = Flurry;
        else if (climb && !roofed && canBuild)
            want = Pillar;
        // Tunnel at a wall or terrain hiding a level-or-lower target, or through a roof
        // that stops it building. Never at terrain hiding a target above: that's a
        // climb, and it walks on to the wall and builds.
        else if ((climb && roofed) || (rise <= ClimbRise && (wall || blind)))
            want = Dig;
        else if (MathF.Abs(to.X) >= RollMin && MathF.Abs(to.X) <= RollMax && MathF.Abs(to.Y) < 20f
                 && m.F1 <= 0f && los)
            want = Roll;

        if (want >= 0)
        {
            input.WantAttack      = true;
            input.RequestedAction = want;
        }

        // Shuffle on even inside EngageRange when it wants to build but has no floor
        // tile square under its column (perched on an edge or a dug step): standing
        // still there would be stuck for good.
        bool reposition = climb && !roofed && !canBuild;
        if (MathF.Abs(to.X) > EngageRange || reposition)
        {
            input.MoveDir.X = side;
            input.Jump      = ahead == 1;
        }
        return input;
    }
}


// ── Legs ────────────────────────────────────────────────────────────────────
internal static class QuillbackProbe
{
    private const float Under = 2.5f;

    // Under the sole: the pointed foot and the two lower shoulders of the octagon.
    public static bool Anchored(in EnemyContext ctx)
    {
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return false;
        var p = ctx.Self.Body.Position;
        float y = p.Y + QuillbackEnemy.HalfHeight + Under;
        float s = QuillbackEnemy.HalfWidth * 0.6f;
        return TileQuery.IsSolidAt(chunks, p.X, y)
            || TileQuery.IsSolidAt(chunks, p.X - s, y)
            || TileQuery.IsSolidAt(chunks, p.X + s, y);
    }

    // Anchored, or at rest: nothing rests in mid-air under gravity (see WarlordProbe).
    public static bool Grounded(in EnemyContext ctx)
        => Anchored(ctx) || ctx.Self.Body.Velocity.LengthSquared() < 1f;

    // Something solid within two tiles over the head: no room to build-hop.
    public static bool Roofed(in EnemyContext ctx)
    {
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return false;
        var p = ctx.Self.Body.Position;
        float top = p.Y - QuillbackEnemy.HalfHeight;
        return TileQuery.IsSolidAt(chunks, p.X, top - 0.5f * Chunk.TileSize)
            || TileQuery.IsSolidAt(chunks, p.X, top - 1.5f * Chunk.TileSize);
    }

    // How many solid tiles are stacked from the floor up in the column just ahead of
    // the leading edge (0 = open, 1 = a hop, 2+ = a wall).
    public static int RiseAhead(in EnemyContext ctx, int side, int max = 4)
    {
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return 0;
        var   p     = ctx.Self.Body.Position;
        float x     = p.X + side * (QuillbackEnemy.HalfWidth + 3f);
        float sole  = p.Y + QuillbackEnemy.HalfHeight;
        int   rise  = 0;
        for (int k = 1; k <= max; k++)
        {
            if (!TileQuery.IsSolidAt(chunks, x, sole - (k - 0.5f) * Chunk.TileSize)) break;
            rise = k;
        }
        return rise;
    }
}

// A fast scurry — faster than the player walks.
public class QuillbackScurryState : EnemyMovementState
{
    protected virtual float Speed    => 120f;
    protected virtual float Deadzone => 0.1f;

    public override int ActivePriority  => 20;
    public override int PassivePriority => 15;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > Deadzone && QuillbackProbe.Grounded(ctx);
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > Deadzone && QuillbackProbe.Grounded(ctx);

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        ctx.Self.Body.Velocity.X = MathF.Sign(ctx.Input.MoveDir.X) * Speed;
    }
}

// The very short jump: 170 px/s up peaks at ~24 px (v²/2g) — one step, not two.
public class QuillbackHopState : EnemyJumpState
{
    protected override float JumpImpulse     => -170f;
    protected override float HorizontalDrift => 120f;
    protected override float MaxAirTime      => 0.35f;
}

// Off the ground with no action in flight: write nothing, so knockback plays out.
public class QuillbackAirborneState : EnemyMovementState
{
    public override int ActivePriority  => 10;
    public override int PassivePriority => 8;
    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted && !QuillbackProbe.Grounded(ctx);
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => !ctx.Self.IsActionCommitted && !QuillbackProbe.Grounded(ctx);
    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v) => v.TimeInState += ctx.Dt;
}

// Plant the feet while an action is committed — only on the ground and not rising,
// so the pillar's hop and a roll off an edge keep their momentum. Actions run after
// this and overwrite X on the frames they drive.
public class QuillbackHoldState : EnemyMovementState
{
    public override int ActivePriority  => 40;
    public override int PassivePriority => 35;
    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Self.IsActionCommitted;
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v) => ctx.Self.IsActionCommitted;
    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        var body = ctx.Self.Body;
        if (body.Velocity.Y > -20f && QuillbackProbe.Grounded(ctx))
            body.Velocity.X *= 0.6f;
    }
}


// ── Arms: shared ────────────────────────────────────────────────────────────
// Every action shares one Active priority above every Passive, so a committed one
// is never replaced mid-swing; the brain names which one it wants.
public abstract class QuillbackAction : EnemyActionState
{
    public override int ActivePriority  => QuillbackEnemy.ActionActive;
    public override int PassivePriority => 30;

    protected abstract float Windup   { get; }
    protected abstract float Active   { get; }
    protected abstract float Recovery { get; }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    protected static Vector2 UnitTo(in EnemyContext ctx)
    {
        var to = ctx.ToPlayer;
        return to.LengthSquared() > 1f ? Vector2.Normalize(to) : new Vector2(ctx.Facing == 0 ? 1 : ctx.Facing, 0f);
    }

    protected static int FacingOf(Vector2 aim, int fallback)
        => aim.X > 0.001f ? 1 : aim.X < -0.001f ? -1 : (fallback == 0 ? 1 : fallback);

    public static void Outline(TelegraphList t, in BoundingBox r, Color c, float thickness = 1f)
    {
        t.Line(new Vector2(r.Left,  r.Top),    new Vector2(r.Right, r.Top),    c, thickness);
        t.Line(new Vector2(r.Left,  r.Bottom), new Vector2(r.Right, r.Bottom), c, thickness);
        t.Line(new Vector2(r.Left,  r.Top),    new Vector2(r.Left,  r.Bottom), c, thickness);
        t.Line(new Vector2(r.Right, r.Top),    new Vector2(r.Right, r.Bottom), c, thickness);
    }
}


// ── Arms: flurry (three aimed jabs) ─────────────────────────────────────────
// One clock, three strikes Gap apart. Each strike re-aims at the target on its first
// frame (written back into LockedAim, which is snapshotted) and lunges a step along
// it, so the combo follows you round the body — over it, under it, behind it. Three
// HitIds minted at Enter: HitId, +1, +2.
public class QuillbackFlurryAction : QuillbackAction
{
    protected override float Windup   => 0.12f;
    protected override float Active   => 2f * Gap + Strike;
    protected override float Recovery => 0.20f;
    protected virtual  float Gap      => 0.13f;
    protected virtual  float Strike   => 0.06f;

    protected virtual float Inset     => 4f;
    protected virtual float Reach     => 26f;
    protected virtual float Lunge     => 150f;
    protected virtual float Damage    => 0.5f;
    // Knockback along the jab plus a lift; the first two hold you in, the third sends.
    protected virtual float Push      => 170f;
    protected virtual float LastPush  => 400f;
    protected virtual float Lift      => 110f;

    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Spawner != null;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedAim    = UnitTo(ctx);
        v.LockedFacing = FacingOf(v.LockedAim, ctx.Facing);
        v.HitId        = ctx.Spawner.HitIds.Next();
        ctx.Spawner.HitIds.Next();
        ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    // Which strike is live at clock t (-1 between strikes / outside the active span).
    private int StrikeAt(float t, float windup)
    {
        float a = t - windup;
        if (a < 0f) return -1;
        int k = (int)(a / Gap);
        if (k > 2 || a - k * Gap >= Strike) return -1;
        return k;
    }

    public BoundingBox Region(Vector2 pos, Vector2 aim)
    {
        var c = pos + aim * (QuillbackEnemy.HalfWidth * 0.5f + Inset + Reach * 0.5f);
        float h = Reach * 0.5f;
        return new BoundingBox(c.X - h, c.Y - h, c.X + h, c.Y + h);
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float prev = v.TimeInState;
        v.TimeInState += ctx.Dt;
        int k = StrikeAt(v.TimeInState, v.WindupDuration);
        if (k < 0) return;
        var body = ctx.Self.Body;
        if (StrikeAt(prev, v.WindupDuration) != k)
        {
            v.LockedAim    = UnitTo(ctx);
            v.LockedFacing = FacingOf(v.LockedAim, v.LockedFacing);
            body.Velocity.X = v.LockedAim.X * Lunge;
        }
        float push = k == 2 ? LastPush : Push;
        var kb = v.LockedAim * push + new Vector2(0f, -Lift);
        ctx.Hitboxes?.Publish(new Hitbox(
            Region(body.Position, v.LockedAim), v.HitId + k, Damage * ctx.Self.Strength, kb,
            Faction.Enemy, ctx.Self.Id, QuillbackEnemy.Claw,
            targets: HitTargets.EntitiesOnly, origin: body.Position));
    }

    // Windup: the first jab's box, outlined and filling. Strikes: each box solid.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        var r = Region(body.Position, v.LockedAim);
        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            Outline(t, r, QuillbackEnemy.Claw * (0.4f + 0.6f * p));
            t.Rect(new Vector2(r.CenterX, r.CenterY), r.Width * p, QuillbackEnemy.Claw * 0.25f);
            return;
        }
        if (StrikeAt(time, v.WindupDuration) >= 0)
        {
            t.Box(r.Left, r.Top, r.Width, r.Height, QuillbackEnemy.Claw * 0.55f);
            var a = body.Position + v.LockedAim * 6f;
            t.Line(a, a + v.LockedAim * (Reach + 10f), QuillbackEnemy.Quill, 2f);
        }
    }
}


// ── Arms: tail spin (all round) ─────────────────────────────────────────────
// A square of hurt centred on the body: above, below, behind and in front at once.
// Knockback is away from the body, toward whichever side the target is on.
public class QuillbackSpinAction : QuillbackAction
{
    protected override float Windup   => 0.18f;
    protected override float Active   => 0.16f;
    protected override float Recovery => 0.28f;
    public    virtual  float Radius   => 32f;
    protected virtual  float Damage   => 1.0f;
    protected virtual  Vector2 Knockback => new(460f, -280f);

    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Spawner != null;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedAim    = UnitTo(ctx);
        v.LockedFacing = FacingOf(v.LockedAim, ctx.Facing);
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration || t >= v.WindupDuration + v.ActiveDuration) return;
        var p = ctx.Self.Body.Position;
        float side = ctx.ToPlayer.X >= 0f ? 1f : -1f;
        ctx.Hitboxes?.Publish(new Hitbox(
            new BoundingBox(p.X - Radius, p.Y - Radius, p.X + Radius, p.Y + Radius),
            v.HitId, Damage * ctx.Self.Strength, new Vector2(side * Knockback.X, Knockback.Y),
            Faction.Enemy, ctx.Self.Id, QuillbackEnemy.Quill,
            targets: HitTargets.EntitiesOnly, origin: p));
    }

    // Windup: the reach ring tightening in. Active: the tail's sweep as an arc
    // running round the ring.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        var pos = body.Position;
        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            t.Ring(pos, Radius + 10f * (1f - p), QuillbackEnemy.Quill * (0.3f + 0.6f * p), 20, 1f + p);
            return;
        }
        if (time < v.WindupDuration + v.ActiveDuration)
        {
            float p = (time - v.WindupDuration) / MathF.Max(v.ActiveDuration, 1e-3f);
            float a = v.LockedFacing * p * MathHelper.TwoPi;
            t.Ring(pos, Radius, QuillbackEnemy.Quill * 0.35f, 20, 1f);
            t.Arc(pos, Radius - 2f, a, 0.9f, QuillbackEnemy.Quill, 8, 3f);
            t.Line(pos, pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (Radius + 4f), QuillbackEnemy.Claw, 2f);
        }
    }
}


// ── Arms: roll ──────────────────────────────────────────────────────────────
// Curl, then barrel along the floor at Speed for the active span, gravity on (it rolls
// off ledges). The body is the hitbox — padded sideways, its bottom kept just above
// the sole so it never chews the floor it's rolling on — and it breaks soft blocks
// in its way: sand at once, dirt on the second frame, stone stops it.
public class QuillbackRollAction : QuillbackAction
{
    protected override float Windup    => 0.20f;
    protected override float Active    => 0.32f;
    protected override float Recovery  => 0.30f;
    protected virtual  float Speed     => 400f;
    protected virtual  float Damage    => 1.0f;
    protected virtual  float TileDamage => 1.0f;
    protected virtual  Vector2 Knockback => new(420f, -220f);

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Spawner != null && QuillbackProbe.Grounded(ctx);

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.ToPlayer.X >= 0f ? 1 : -1;
        v.LockedAim    = new Vector2(v.LockedFacing, 0f);
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        var body = ctx.Self.Body;
        if (t < v.WindupDuration) { body.Velocity.X *= 0.6f; return; }
        if (t >= v.WindupDuration + v.ActiveDuration) return;

        body.Velocity.X = v.LockedFacing * Speed;
        var p = body.Position;
        float w = QuillbackEnemy.HalfWidth + 3f, h = QuillbackEnemy.HalfHeight;
        ctx.Hitboxes?.Publish(new Hitbox(
            new BoundingBox(p.X - w, p.Y - h - 2f, p.X + w, p.Y + h - 2f),
            v.HitId, TileDamage, new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
            Faction.Enemy, ctx.Self.Id, QuillbackEnemy.Quill,
            targets: HitTargets.All, bodyDamage: Damage * ctx.Self.Strength, origin: p));
    }

    // Windup: the lane it will cover, filling in. Active: speed lines.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        var pos = body.Position;
        int f = v.LockedFacing;
        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            float lane = Speed * Active;
            float h = QuillbackEnemy.HalfHeight * 2f;
            float x0 = f > 0 ? pos.X : pos.X - lane;
            t.Box(x0, pos.Y - QuillbackEnemy.HalfHeight, lane, h, QuillbackEnemy.Claw * (0.06f + 0.14f * p));
            t.Line(pos, pos + new Vector2(f * lane * p, 0f), QuillbackEnemy.Claw * (0.4f + 0.5f * p), 2f);
            return;
        }
        if (time < v.WindupDuration + v.ActiveDuration)
        {
            for (int i = 0; i < 3; i++)
            {
                var a = pos + new Vector2(-f * (QuillbackEnemy.HalfWidth + 4f + i * 6f), -5f + i * 5f);
                t.Line(a, a - new Vector2(f * (14f - i * 3f), 0f), QuillbackEnemy.Quill * (0.7f - i * 0.15f), 2f);
            }
        }
    }
}


// ── Arms: dig ───────────────────────────────────────────────────────────────
// A two-tile bite out of the terrain toward the target, in one of eight directions
// (the aim snapped to 45°, stored un-normalized in LockedAim as (−1|0|1, −1|0|1)).
// Sideways it is the body's height and stops a hair above the sole, so it tunnels
// without digging a pit; down it's the body's width under the feet; diagonal it's a
// two-tile square off the corner. Occluded from the body, so it eats the near layer
// all at once (not occluded — see Update). It also hurts — claws are claws, but only
// through open air.
public class QuillbackDigAction : QuillbackAction
{
    protected override float Windup   => 0.14f;
    protected override float Active   => 0.12f;
    protected override float Recovery => 0.16f;
    protected virtual  float TileDamage => 6f;           // per frame: stone (12) in two
    protected virtual  float Damage     => 0.5f;
    private const float Depth = 2f * Chunk.TileSize;

    public static Vector2 Snap(Vector2 to)
    {
        float a = MathF.Atan2(to.Y, to.X);
        float s = MathF.Round(a / MathHelper.PiOver4) * MathHelper.PiOver4;
        return new Vector2(MathF.Round(MathF.Cos(s)), MathF.Round(MathF.Sin(s)));
    }

    public static BoundingBox Region(Vector2 pos, Vector2 dir)
    {
        float hw = QuillbackEnemy.HalfWidth, hh = QuillbackEnemy.HalfHeight;
        float sx = dir.X, sy = dir.Y;
        if (sx != 0f && sy != 0f)
        {
            var c = pos + new Vector2(sx * (hw + Chunk.TileSize - 2f), sy * (hh + Chunk.TileSize - 2f));
            float h = Chunk.TileSize;
            return new BoundingBox(c.X - h, c.Y - h, c.X + h, c.Y + h);
        }
        if (sx != 0f)
        {
            float x0 = pos.X + sx * (hw - 2f), x1 = x0 + sx * Depth;
            return new BoundingBox(MathF.Min(x0, x1), pos.Y - hh - 4f, MathF.Max(x0, x1), pos.Y + hh - 1.5f);
        }
        float y0 = sy > 0f ? pos.Y + hh - 2f : pos.Y - hh - Depth;
        return new BoundingBox(pos.X - hw + 1.5f, y0, pos.X + hw - 1.5f, y0 + Depth + 2f);
    }

    public static bool HasSolid(ChunkMap chunks, in BoundingBox r)
    {
        if (chunks == null) return false;
        int x0 = (int)MathF.Floor(r.Left / Chunk.TileSize), x1 = (int)MathF.Floor((r.Right  - 0.01f) / Chunk.TileSize);
        int y0 = (int)MathF.Floor(r.Top  / Chunk.TileSize), y1 = (int)MathF.Floor((r.Bottom - 0.01f) / Chunk.TileSize);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (chunks.GetCellState(x, y) == TileState.Solid) return true;
        return false;
    }

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Spawner != null && HasSolid(ctx.Spawner.Chunks, Region(ctx.Self.Body.Position, Snap(ctx.ToPlayer)));

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedAim    = Snap(ctx.ToPlayer);
        v.LockedFacing = FacingOf(v.LockedAim, ctx.Facing);
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration || t >= v.WindupDuration + v.ActiveDuration) return;
        var p = ctx.Self.Body.Position;
        var r = Region(p, v.LockedAim);
        var kb = new Vector2(v.LockedAim.X * 260f, v.LockedAim.Y * 260f - 120f);
        // Terrain: the whole bite, NOT occluded. An occluded bite only reaches cells in
        // view of the body, and one overlapping a cell round a corner never clears it —
        // the precondition keeps seeing solid and it digs at nothing for ever.
        ctx.Hitboxes?.Publish(new Hitbox(
            r, v.HitId, TileDamage, Vector2.Zero,
            Faction.Enemy, ctx.Self.Id, QuillbackEnemy.Claw, targets: HitTargets.TilesOnly));
        // Bodies: occluded, so the claws don't reach through a wall.
        ctx.Hitboxes?.Publish(new Hitbox(
            r, v.HitId, Damage * ctx.Self.Strength, kb,
            Faction.Enemy, ctx.Self.Id, QuillbackEnemy.Claw,
            targets: HitTargets.EntitiesOnly, origin: p));
    }

    // Windup: the bite outlined with claw marks raking across it. Active: filled.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        var r = Region(body.Position, v.LockedAim);
        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            Outline(t, r, QuillbackEnemy.Dirt * (0.4f + 0.6f * p));
            for (int i = 0; i < 3; i++)
            {
                float u = (i + 1) / 4f;
                var a = new Vector2(r.Left + r.Width * u, r.Top);
                t.Line(a, a + new Vector2(-4f, r.Height * p), QuillbackEnemy.Claw * (0.4f + 0.5f * p), 1.5f);
            }
            return;
        }
        if (time < v.WindupDuration + v.ActiveDuration)
            t.Box(r.Left, r.Top, r.Width, r.Height, QuillbackEnemy.Dirt * 0.5f);
    }
}


// ── Arms: pillar (build hop) ────────────────────────────────────────────────
// Climb by building under its own feet. On the windup→active edge it hops straight
// up at HopSpeed (~33 px: two tiles' clearance); every frame of the rise it grows a
// dirt tile (ChunkMap.ForceSprout) in the lowest free cell of its column, above the
// floor it hopped from, that its sole has fully cleared — so one hop stacks up to two.
// It lands on the stack and the action ends on the landing (the clock snaps to the
// recovery, like the Prism dash), or after Active if something goes wrong.
//
// LockedAim is an ABSOLUTE point: (the column's centre x, the floor-top y it hopped
// from), frozen at Enter, round-tripped through EntityData.Aim. The cells it fills
// are derived from that and the terrain, so there is no "placed" count to snapshot.
public class QuillbackPillarAction : QuillbackAction
{
    protected override float Windup   => 0.08f;
    protected override float Active   => 0.70f;
    protected override float Recovery => 0.06f;
    protected virtual  float HopSpeed => 205f;
    public    const    int   MaxStack = 2;
    protected virtual  TileType Material => TileType.Dirt;

    private static int Column(float x) => (int)MathF.Floor(x / Chunk.TileSize);

    // The floor row under the sole, if any, at the body's column.
    private static bool FloorRow(in EnemyContext ctx, out int gtx, out int gty)
    {
        var p = ctx.Self.Body.Position;
        gtx = Column(p.X);
        gty = (int)MathF.Floor((p.Y + QuillbackEnemy.HalfHeight + 2.5f) / Chunk.TileSize);
        var chunks = ctx.Spawner?.Chunks;
        return chunks != null && chunks.GetCellState(gtx, gty) == TileState.Solid;
    }

    // Grounded on a floor tile in its own column, with the column clear above for
    // the body plus one hop.
    public override bool CheckPreConditions(in EnemyContext ctx) => CanBuild(ctx);

    // Shared with the brain, so it never asks for a build that can't start.
    public static bool CanBuild(in EnemyContext ctx)
    {
        if (!QuillbackProbe.Anchored(ctx) || !FloorRow(ctx, out int gtx, out int gty)) return false;
        var chunks = ctx.Spawner.Chunks;
        for (int k = 1; k <= 3; k++)
            if (!EnemyBlockCells.IsFree(chunks, gtx, gty - k)) return false;
        return true;
    }

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        FloorRow(ctx, out int gtx, out int gty);
        v.LockedAim    = new Vector2((gtx + 0.5f) * Chunk.TileSize, gty * Chunk.TileSize);
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float prev = v.TimeInState;
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        var self = ctx.Self;
        var body = self.Body;
        if (t < v.WindupDuration) { body.Velocity.X = 0f; return; }
        float end = v.WindupDuration + v.ActiveDuration;
        if (t >= end) return;

        // Hold the column: steer the centre onto the cell centre.
        body.Velocity.X = (v.LockedAim.X - body.Position.X) * 10f;
        if (prev < v.WindupDuration) { body.Velocity.Y = -HopSpeed; return; }

        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return;
        int gtx = Column(v.LockedAim.X);
        int floor = (int)MathF.Round(v.LockedAim.Y / Chunk.TileSize);
        float sole = body.Position.Y + QuillbackEnemy.HalfHeight;
        for (int k = 1; k <= MaxStack; k++)
        {
            int gty = floor - k;
            if (!EnemyBlockCells.IsFree(chunks, gtx, gty)) continue;   // already grown
            if (sole > gty * Chunk.TileSize - 0.5f) break;             // not clear of it yet
            if (EnemyBlockCells.Overlaps(ctx.Target.Bounds, gtx, gty)) break;
            chunks.ForceSprout(gtx, gty, Material);
            break;                                                     // one a frame
        }

        // Landed (falling, and on something): straight to the recovery.
        if (prev > v.WindupDuration + 0.1f && body.Velocity.Y >= 0f && QuillbackProbe.Anchored(ctx))
            v.TimeInState = end;
    }

    // Windup: the cells it will grow, outlined over its column.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.TimeInState >= v.WindupDuration + v.ActiveDuration) return;
        int gtx = Column(v.LockedAim.X);
        int floor = (int)MathF.Round(v.LockedAim.Y / Chunk.TileSize);
        float p = MathF.Min(1f, v.TimeInState / MathF.Max(v.WindupDuration, 1e-3f));
        for (int k = 1; k <= MaxStack; k++)
            EnemyBlockCells.Marker(t, gtx, floor - k, p * (k == 1 ? 1f : 0.7f), QuillbackEnemy.Dirt);
    }
}


// ── Arms: quill storm (far only; the open shell) ────────────────────────────
// Timeline (t = TimeInState):
//   [0, Windup)               curled, every quill up; the reach ring drawn. OPEN.
//   [Windup, +Active)         a ring of quills flies out from InnerRadius to
//                             MaxRadius. OPEN.
//   [.., +Recovery)           settle. Shut.
//   broken ⇒ [.., +Daze)      hit while open: QuillbackEnemy.OnHit sets Scratch.I1 = 1;
//                             the next Update snaps the clock to the end of the
//                             recovery and sets I1 = 2, and CheckConditions then holds
//                             it through Daze. No quills fly from a broken storm.
// The ring is Quills boxes on a circle, each sized to its share of the circumference
// so the ring has no gaps, all on one HitId (a target is hit once), occluded from the
// body — so a block between you and it is cover. Knockback is outward per quill.
public class QuillbackStormAction : QuillbackAction
{
    public    const   float MaxRadius   = 300f;
    protected const   float InnerRadius = 16f;
    protected const   int   Quills      = 32;
    public    const   float WindupTime  = 0.50f;
    public    const   float ActiveTime  = 0.30f;

    protected override float Windup   => WindupTime;
    protected override float Active   => ActiveTime;
    protected override float Recovery => 0.40f;
    protected virtual  float Daze     => 1.30f;
    protected virtual  float Damage   => 1.0f;
    protected virtual  float Push     => 360f;
    protected virtual  float Lift     => 160f;

    // Open: the window in which a hit is a punish (windup + throw, not yet broken).
    public static bool IsOpen(float t, int brokenFlag) => brokenFlag == 0 && t < WindupTime + ActiveTime;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Spawner != null && QuillbackProbe.Grounded(ctx);

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float settle = v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;
        return v.TimeInState < (ctx.Self.Scratch.I1 == 2 ? settle + Daze : settle);
    }

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedAim    = UnitTo(ctx);
        v.LockedFacing = FacingOf(v.LockedAim, ctx.Facing);
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        ctx.Self.Scratch.I1 = 0;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.Committed = false;
        ctx.Self.Scratch.I1 = 0;
    }

    public float RadiusAt(float t, in EnemyActionVars v)
    {
        float p = MathHelper.Clamp((t - v.WindupDuration) / MathF.Max(v.ActiveDuration, 1e-3f), 0f, 1f);
        return MathHelper.Lerp(InnerRadius, MaxRadius, p);
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        ref var m = ref ctx.Self.Scratch;
        if (m.I1 == 1)
        {
            v.TimeInState = v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;
            m.I1 = 2;
            return;
        }
        if (m.I1 == 2) { ctx.Self.Body.Velocity.X *= 0.8f; return; }

        float t = v.TimeInState;
        if (t < v.WindupDuration || t >= v.WindupDuration + v.ActiveDuration) return;
        var   p = ctx.Self.Body.Position;
        float r = RadiusAt(t, v);
        float h = MathF.Max(8f, r * MathF.Sin(MathF.PI / Quills) * 1.15f);
        for (int i = 0; i < Quills; i++)
        {
            float a   = i * MathHelper.TwoPi / Quills;
            var   dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var   c   = p + dir * r;
            ctx.Hitboxes?.Publish(new Hitbox(
                new BoundingBox(c.X - h, c.Y - h, c.X + h, c.Y + h),
                v.HitId, Damage * ctx.Self.Strength, dir * Push + new Vector2(0f, -Lift),
                Faction.Enemy, ctx.Self.Id, QuillbackEnemy.Quill,
                targets: HitTargets.EntitiesOnly, origin: p));
        }
    }

    // Windup: the reach ring, an inner bristle ring swelling, and a flashing "open"
    // ring on the body. Throw: the quills themselves, radial strokes on the ring.
    // Daze: stars circling over it.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        var pos = body.Position;
        float settle = v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;
        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            t.Ring(pos, MaxRadius, QuillbackEnemy.Quill * (0.10f + 0.25f * p), 48, 1f);
            t.Ring(pos, InnerRadius + 4f + 8f * p, QuillbackEnemy.Quill * (0.4f + 0.6f * p), 16, 1f + p);
            float strobe = 0.5f + 0.5f * MathF.Abs(MathF.Sin(time * 30f));
            t.Ring(pos, QuillbackEnemy.HalfWidth + 2f, QuillbackEnemy.Open * strobe, 16, 2f);
            return;
        }
        if (time < v.WindupDuration + v.ActiveDuration)
        {
            float r = RadiusAt(time, v);
            for (int i = 0; i < Quills; i++)
            {
                float a   = i * MathHelper.TwoPi / Quills;
                var   dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                t.Line(pos + dir * (r - 10f), pos + dir * r, QuillbackEnemy.Quill, 2f);
            }
            t.Ring(pos, QuillbackEnemy.HalfWidth + 2f, QuillbackEnemy.Open, 16, 2f);
            return;
        }
        if (time >= settle)
        {
            for (int i = 0; i < 3; i++)
            {
                float a = time * 6f + i * MathHelper.TwoPi / 3f;
                var at = pos + new Vector2(MathF.Cos(a) * 10f, -QuillbackEnemy.HalfHeight - 6f + MathF.Sin(a) * 3f);
                t.Rect(at, 2.5f, QuillbackEnemy.Open);
            }
        }
    }
}


// ── Body ────────────────────────────────────────────────────────────────────
public sealed class QuillbackEnemy : EnemyEntity
{
    // A low, wide octagon standing on its bottom vertex (the Warden's trick: a pointed
    // foot slides up onto a step edge instead of catching on its corner).
    public const float HalfHeight = 9f;
    public const float HalfWidth  = 12.6f;
    public const float MaxHp      = 4f;

    // The shell.
    public const float ShieldMax    = 4f;
    public const float ShieldRegen  = 3f;     // per second — empty to full in ~1.3 s
    public const float RegenDelay   = 0.8f;   // after a hit the shell held
    public const float BrokenDelay  = 2.2f;   // after a hit that emptied it
    // Knockback divisor bonus while the shell holds (Armor is "mass that only counts
    // for shoves"); bare, it flies like the small thing it is.
    public const float ShellArmor   = 5f;
    // A hit while the storm is open lands at this multiple, straight to HP.
    public const float VulnerableMultiplier = 6f;

    public const int ActionActive = 40;

    public static readonly Color Shell = new(130, 150, 185);
    public static readonly Color Quill = new(225, 230, 240);
    public static readonly Color Claw  = new(255, 150, 70);
    public static readonly Color Dirt  = new(190, 140, 80);
    public static readonly Color Open  = new(255, 70, 70);
    public static readonly Color Flesh = new(205, 120, 95);

    public override EntityKind Kind        => EntityKind.Quillback;
    public override string     DisplayName => "Quillback";

    public float Shield
    {
        get => Energy;
        private set => Energy = MathHelper.Clamp(value, 0f, ShieldMax);
    }

    // The storm is open to a punish right now.
    public bool IsOpen => CurrentActionIndex == QuillbackController.Storm
                       && QuillbackStormAction.IsOpen(CurrentActionTime, Scratch.I1);
    public bool IsDazed => CurrentActionIndex == QuillbackController.Storm && Scratch.I1 == 2;

    private static Polygon CreateBodyPolygon()
    {
        var v = new Vector2[8];
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathHelper.PiOver4;
            v[i] = new Vector2(MathF.Cos(a) * HalfWidth, MathF.Sin(a) * HalfHeight);
        }
        return new Polygon(v);
    }

    private readonly QuillbackFigure _figure = Sprites.Quillback();
    private readonly AnimatedSprite  _sprite = new();
    private QuillbackPose _pose = (QuillbackPose)(-1);
    private int           _poseFacing;

    public QuillbackEnemy(Vector2 pos)
        : base(new PhysicsBody(CreateBodyPolygon(), pos),
               health: MaxHp,
               movement: new List<EnemyMovementState>
               {
                   new EnemyIdleState(),          // 0 — fallback
                   new QuillbackAirborneState(),  // 1
                   new QuillbackScurryState(),    // 2
                   new QuillbackHopState(),       // 3
                   new QuillbackHoldState(),      // 4 — plant while attacking (hop-aware)
                   // No stagger: boss poise. The storm's daze is its only interrupt.
               },
               actions: new List<EnemyActionState>
               {
                   new QuillbackFlurryAction(),   // 0  QuillbackController.Flurry
                   new QuillbackSpinAction(),     // 1  .Spin
                   new QuillbackRollAction(),     // 2  .Roll
                   new QuillbackDigAction(),      // 3  .Dig
                   new QuillbackPillarAction(),   // 4  .Pillar
                   new QuillbackStormAction(),    // 5  .Storm
               },
               controller: new QuillbackController())
    {
        Mass               = 1.5f;
        Body.FrictionScale = 0.12f;
        EnergyMax          = ShieldMax;
        Energy             = ShieldMax;
        EnergyRegen        = 0f;                  // the shell runs its own regen (delayed)
        Color              = Shell;
        Sprite             = _sprite;
    }

    private bool _shellHolding;                   // transient: scoped to one OnHit call
    protected override float KnockbackMass => Mass + (_shellHolding ? ShellArmor : 0f);

    // The shell first; overflow to HP. While the storm is open: no shell, the hit lands
    // VulnerableMultiplier times over, and the storm breaks into the daze. Health is
    // pre-credited with what the shell absorbs so the base hit (flash, hitstop,
    // knockback) runs untouched and never sees a false zero.
    public override Vector2 OnHit(in Hitbox hit, in Hurtbox hurtbox)
    {
        if (IsDead) return base.OnHit(in hit, in hurtbox);
        float dmg = hit.BodyDamage;

        if (IsOpen)
        {
            Scratch.I1 = 1;
            Shield     = 0f;
            Scratch.F2 = BrokenDelay;
            var d = base.OnHit(in hit, in hurtbox);
            Health -= dmg * (VulnerableMultiplier - 1f);
            return d;
        }

        float absorbed = MathF.Min(Shield, dmg);
        _shellHolding  = Shield > 0f;
        Shield        -= absorbed;
        Scratch.F2     = Shield <= 0f ? BrokenDelay : RegenDelay;
        Health        += absorbed;
        var delivered  = base.OnHit(in hit, in hurtbox);
        _shellHolding  = false;
        return delivered;
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (!IsDead)
        {
            if (Scratch.F2 > 0f) Scratch.F2 = MathF.Max(0f, Scratch.F2 - dt);
            else if (Shield < ShieldMax) Shield += ShieldRegen * dt;
        }
        base.Update(dt, player, hitboxes, spawner);
        Color = Shield > 0f ? Shell : Flesh;
    }

    // The shell and HP bars over the body, and a ring on the shell while it holds.
    public override void Telegraph(TelegraphList t)
    {
        base.Telegraph(t);
        if (IsDead) return;
        var p = Body.Position;
        const float W = 30f;
        float y = p.Y - HalfHeight - 14f;
        float sf = Shield / ShieldMax, hf = MathHelper.Clamp(Health / MaxHp, 0f, 1f);
        t.Box(p.X - W * 0.5f, y, W, 3f, Color.Black * 0.5f);
        t.Box(p.X - W * 0.5f, y, W * sf, 3f, Scratch.F2 > 0f && sf < 1f ? Shell : Quill);
        t.Box(p.X - W * 0.5f, y + 4f, W, 2f, Color.Black * 0.5f);
        t.Box(p.X - W * 0.5f, y + 4f, W * hf, 2f, Open);
        if (Shield > 0f && !IsOpen)
            t.Arc(p, HalfWidth + 3f, -MathHelper.PiOver2, 1.3f, Shell * (0.3f + 0.5f * sf), 10, 1.5f);
    }

    // Render-only: pick the figure's pose from the action in flight. Re-derived every
    // rendered frame from snapshotted state, so a rollback needs nothing.
    public override void SyncSprite()
    {
        base.SyncSprite();
        float t = CurrentActionTime;
        ref readonly var v = ref CurrentActionVars;
        bool active = t >= v.WindupDuration && t < v.WindupDuration + v.ActiveDuration;
        var pose = CurrentActionIndex switch
        {
            QuillbackController.Flurry => t < v.WindupDuration ? QuillbackPose.Crouch : QuillbackPose.Strike,
            QuillbackController.Spin   => active ? QuillbackPose.Ball : QuillbackPose.Crouch,
            QuillbackController.Roll   => active ? QuillbackPose.Ball : QuillbackPose.Crouch,
            QuillbackController.Dig    => QuillbackPose.Strike,
            QuillbackController.Pillar => QuillbackPose.Crouch,
            QuillbackController.Storm  => IsDazed ? QuillbackPose.Dazed
                                        : t < v.WindupDuration + v.ActiveDuration ? QuillbackPose.Bristle
                                        : QuillbackPose.Idle,
            _ => MathF.Abs(Body.Velocity.X) > 20f ? QuillbackPose.Run : QuillbackPose.Idle,
        };
        int facing = Facing == 0 ? 1 : Facing;
        if (pose == _pose && facing == _poseFacing) return;
        _pose = pose; _poseFacing = facing;
        _sprite.Play(_figure.Get(pose, facing));
    }
}
