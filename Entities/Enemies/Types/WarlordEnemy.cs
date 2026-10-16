using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  WARLORD — a heavy, mobile duelist that fights you inside a cage of its own
//  making.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it:  set "Stage": "warlord" in configs/game_config.json, then
//          dotnet run --project MTile.Desktop
//
// Every other boss in the roster is either rooted (Zeus, Bastion) or has a single
// read (Warden). The Warlord is the first one that *moves* and the first one with a
// repertoire that changes shape depending on where you are:
//
//   CLEAVE  (WarlordCleaveAction)  point-blank. A two-hit combo: a quick forehand
//                                  that chips and shoves, then a heavy overhead that
//                                  launches. The gap between the two hits is the
//                                  window — step back after the first and the second
//                                  whiffs, and the recovery that follows is yours.
//   RAM     (WarlordRamAction)     mid range. A stomp, then a shoulder charge straight
//                                  along the floor. It stops at any wall it hits, so a
//                                  built block in its path is a real answer.
//   LEAP    (WarlordLeapAction)    long range, or you're above it. It crouches, marks
//                                  the exact landing spot, then solves a ballistic
//                                  arc onto it; the landing is a floor-wide shockwave
//                                  that carves foam and sand. Counterplay is to leave
//                                  the ring you were shown — or jump the wave.
//   CAGE    (WarlordCageAction)    the signature. It conjures two foam walls either
//                                  side of you (telegraphed cell by cell), then LEAPS
//                                  INTO THE CAGE. The walls are temporary — foam
//                                  decays on its own — and the slam breaks them anyway;
//                                  the point is that for the next second or two the
//                                  easy sidestep is gone. You can slash through (foam
//                                  is one slash a cell), wall-jump out, or stand your
//                                  ground and punish the landing.
//
// At half health it stalls for a moment (an exhausted opening), then enters phase
// two: shorter cooldowns and a cage every other opening.
//
// ── How the pacing stays deterministic ───────────────────────────────────────
//
// Controllers must be stateless, but a boss needs a memory: "I just raised a cage,
// follow up", "no leap for a second". That memory lives in EnemyEntity.Scratch
// (BrainScratch), which the fighter brains already use and which is snapshotted
// as a flat value — so a rollback rewinds the Warlord's intentions with its body.
// Nothing on the controller instance is ever written.
//
// ── How multi-phase attacks stay inside EnemyActionVars ──────────────────────
//
// The leap has four phases (crouch → flight → slam → recovery) and the slam has to
// start the frame it LANDS, which can be anywhere inside the flight window. There
// is no "landed" flag to snapshot, so the action snaps its own clock forward to the
// slam phase when the landing probe fires (the same trick EnemyGuardAction.Lower
// uses to drop a guard). The clock is the flag, and the clock round-trips.
//
// Built from the TemplateEnemy pattern (brain / legs / arms); see that file for the
// rules every piece here follows.


// ── Brain ───────────────────────────────────────────────────────────────────
public sealed class WarlordController : EnemyController
{
    // Action indices — must match the order WarlordEnemy's ctor registers them.
    public const int Cleave = 0, Ram = 1, Leap = 2, Cage = 3;

    // Horizontal stand-off while nothing is happening. Sits under the cleave's
    // trigger Range (40), which sits under its effective reach (2 + 36 + 6 ≈ 44).
    public float HoldRange        { get; init; } = 26f;
    public float Deadband         { get; init; } = 5f;
    public float AlertRange       { get; init; } = 760f;

    // Which attack fits which geometry (centre-to-centre px; dy is Y-down).
    public float CleaveRange      { get; init; } = 40f;
    public float CleaveVertSlack  { get; init; } = 30f;
    public float RamMin           { get; init; } = 60f;
    public float RamMax           { get; init; } = 170f;
    public float RamVertSlack     { get; init; } = 24f;
    public float LeapMin          { get; init; } = 95f;
    public float LeapAboveDy      { get; init; } = -40f;   // you're this far above me ⇒ leap
    public float CageMin          { get; init; } = 50f;
    public float CageMax          { get; init; } = 230f;
    public float CageVertSlack    { get; init; } = 40f;

    // Pacing. A "big move" is a ram or a leap; the cage comes every CageEvery of them.
    public int   CageEvery        { get; init; } = 3;
    public float BigMoveCooldown  { get; init; } = 1.1f;
    public float CageFollowSeconds{ get; init; } = 2.5f;
    public float RestSeconds      { get; init; } = 1.5f;
    public float Phase2Health     { get; init; } = 0.5f;    // fraction of MaxHealth
    public float Phase2Cooldown   { get; init; } = 0.6f;    // × BigMoveCooldown
    public int   Phase2CageEvery  { get; init; } = 2;

    // BrainScratch layout (see the header):
    //   I0  bit 0: phase (0 normal, 1 enraged); bits 1..: last frame's action index + 1
    //   I1  big moves since the last cage
    //   F0  big-move cooldown remaining, s
    //   F1  exhausted rest remaining, s (phase transition)
    //   F2  cage follow-up window remaining, s (> 0 ⇒ "leap into the cage")
    //   F3  last frame's action clock, s — with the index, the commit edge detector
    private static bool Phase2(in BrainScratch m)     => (m.I0 & 1) != 0;
    private static int  LastAction(in BrainScratch m) => (m.I0 >> 1) - 1;
    private static void Remember(ref BrainScratch m, bool phase2, int action)
        => m.I0 = (phase2 ? 1 : 0) | ((action + 1) << 1);

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var self = ctx.Self;
        ref var m = ref self.Scratch;
        float dt = ctx.Dt;
        bool phase2 = Phase2(m);

        // Phase flip: once, at the threshold, with a visible pause.
        if (!phase2 && self.Health <= self.MaxHealth * Phase2Health)
        {
            phase2 = true;
            m.F1   = RestSeconds;
        }

        // Commit edge: SelectAction ran after last frame's Decide, so what it chose is
        // visible now. A start is the index changing OR the clock running backwards —
        // the FSM drops a finished action and re-enters the same one inside a single
        // SelectAction, so a back-to-back repeat never shows an idle frame.
        int   action = self.CurrentActionIndex;
        float clock  = self.CurrentActionTime;
        if (action >= 0 && (action != LastAction(m) || clock < m.F3)) OnActionStarted(action, phase2, ref m);
        Remember(ref m, phase2, action);
        m.F3 = clock;

        if (m.F0 > 0f) m.F0 = MathF.Max(0f, m.F0 - dt);
        if (m.F2 > 0f) m.F2 = MathF.Max(0f, m.F2 - dt);

        var input = new EnemyInput { AimWorld = ctx.Target.Position };

        // Exhausted: stand there and take it.
        if (m.F1 > 0f) { m.F1 = MathF.Max(0f, m.F1 - dt); return input; }
        if (ctx.Dist > AlertRange) return input;

        var   to   = ctx.ToPlayer;
        float adx  = MathF.Abs(to.X);
        float ady  = MathF.Abs(to.Y);
        int   side = to.X >= 0f ? 1 : -1;
        // A target in the air is not somewhere to leap to or build around: it will not
        // be there when the move lands. Wait for it to settle.
        bool settled = MathF.Abs(ctx.Target.Velocity.Y) < 30f;

        // Always press. No backing off — a boss that retreats reads as timid.
        if (adx > HoldRange + Deadband) input.MoveDir.X = side;

        int want = -1;
        if (m.F2 > 0f && ctx.Dist >= 34f)
            want = Leap;                                           // crash the cage
        else if (ctx.Dist < CleaveRange && ady < CleaveVertSlack)
            want = Cleave;
        else if (m.F0 <= 0f)
        {
            int cageEvery = phase2 ? Phase2CageEvery : CageEvery;
            if (settled && m.I1 >= cageEvery && adx >= CageMin && adx <= CageMax && ady < CageVertSlack)
                want = Cage;
            else if (adx >= RamMin && adx <= RamMax && ady < RamVertSlack)
                want = Ram;
            else if (settled && (ctx.Dist >= LeapMin || (to.Y < LeapAboveDy && adx >= 30f)))
                want = Leap;
        }

        if (want >= 0)
        {
            input.WantAttack      = true;
            input.RequestedAction = want;
        }
        return input;
    }

    private void OnActionStarted(int action, bool phase2, ref BrainScratch m)
    {
        float cooldown = BigMoveCooldown * (phase2 ? Phase2Cooldown : 1f);
        switch (action)
        {
            case Cage:
                m.F2 = CageFollowSeconds;
                m.I1 = 0;
                break;
            case Leap:
                m.F2 = 0f;                 // the follow-up is spent
                m.F0 = cooldown;
                m.I1++;
                break;
            case Ram:
                m.F0 = cooldown;
                m.I1++;
                break;
        }
    }
}


// ── Legs ────────────────────────────────────────────────────────────────────
// Anchoring: "a solid tile within AnchorDist of the centre", the same probe the
// hop and cling states use. Radius 15 + 3 px of slack.
//
// Grounded is the looser question the locomotion gates ask: anchored, OR at rest.
// A body this wide can come to rest wedged across a crater its own slam carved,
// held up by the rims at the corners of its polygon while the probe ring under its
// centre finds only air — and a boss that reads that as "airborne" stands there for
// ever. Nothing rests in mid-air under gravity, so "not moving" is support. The
// leap's LANDING keeps the strict probe: at the apex of a vertical hop the body is
// momentarily at rest too, and that is not a landing.
internal static class WarlordProbe
{
    public const float AnchorDist = 18f;
    public const int   Samples    = 16;
    public static bool Anchored(in EnemyContext ctx)
        => SurfaceProbe.IsAnchored(ctx.Self.Body.Position, ctx.Spawner?.Chunks, AnchorDist, Samples);
    public static bool Grounded(in EnemyContext ctx)
        => Anchored(ctx) || ctx.Self.Body.Velocity.LengthSquared() < 1f;
}

// A heavy walk, a little slower than the player, only while grounded.
public class WarlordWalkState : EnemyMovementState
{
    protected virtual float Speed    => 62f;
    protected virtual float Deadzone => 0.1f;

    public override int ActivePriority  => 20;
    public override int PassivePriority => 15;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > Deadzone && WarlordProbe.Grounded(ctx);

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > Deadzone && WarlordProbe.Grounded(ctx);

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        ctx.Self.Body.Velocity.X = MathF.Sign(ctx.Input.MoveDir.X) * Speed;
    }
}

// Free flight: off the ground with no action in flight, write nothing, so a
// knockback or a bounce plays out instead of being braked by Idle.
public class WarlordAirborneState : EnemyMovementState
{
    public override int ActivePriority  => 10;
    public override int PassivePriority => 8;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted && !WarlordProbe.Grounded(ctx);
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => !ctx.Self.IsActionCommitted && !WarlordProbe.Grounded(ctx);

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v) => v.TimeInState += ctx.Dt;
}

// The attack-hold that knows about the leap. EnemyAttackHoldState brakes X every
// frame an action is committed, which would scrub the leap's horizontal velocity on
// the frames right after takeoff (still within anchor range) and at any apex that
// grazes a ceiling. This one plants the feet only while grounded AND not rising, so
// the crouch and the landing are rooted and the arc is left alone.
public class WarlordHoldState : EnemyMovementState
{
    private const float RisingVy = -20f;

    public override int ActivePriority  => 40;
    public override int PassivePriority => 35;

    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Self.IsActionCommitted;
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v) => ctx.Self.IsActionCommitted;

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        var body = ctx.Self.Body;
        if (body.Velocity.Y > RisingVy && WarlordProbe.Grounded(ctx))
            body.Velocity.X *= 0.6f;
    }
}


// ── Arms: shared bits ───────────────────────────────────────────────────────
internal static class WarlordHits
{
    // A horizontal slab in front of the body: inset..inset+reach along `facing`.
    public static BoundingBox Slab(Vector2 pos, int facing, float inset, float reach, float halfHeight, float yOffset = 0f)
    {
        float cx = pos.X + facing * (inset + reach * 0.5f);
        float cy = pos.Y + yOffset;
        return new BoundingBox(cx - reach * 0.5f, cy - halfHeight, cx + reach * 0.5f, cy + halfHeight);
    }

    public static void Outline(TelegraphList t, in BoundingBox r, Color c, float thickness = 1f)
    {
        t.Line(new Vector2(r.Left,  r.Top),    new Vector2(r.Right, r.Top),    c, thickness);
        t.Line(new Vector2(r.Left,  r.Bottom), new Vector2(r.Right, r.Bottom), c, thickness);
        t.Line(new Vector2(r.Left,  r.Top),    new Vector2(r.Left,  r.Bottom), c, thickness);
        t.Line(new Vector2(r.Right, r.Top),    new Vector2(r.Right, r.Bottom), c, thickness);
    }

    public static readonly Color Ember  = new(255, 120, 40);
    public static readonly Color Blood  = new(230, 40, 50);
    public static readonly Color Bone   = new(240, 225, 200);
    public static readonly Color Cage   = new(120, 220, 255);
}

// ── Arms: cleave (two-hit combo) ────────────────────────────────────────────
// One clock, two strikes. Hit 1 fires at the end of the windup; hit 2 fires Gap
// later and is the launcher. Two HitIds are minted at Enter and the second is
// simply the first + 1 — HitIdAllocator is a counter, so that is exactly what the
// second Next() returned.
public class WarlordCleaveAction : EnemyActionState
{
    protected virtual float   Windup        => 0.42f;
    protected virtual float   Hit1Active    => 0.10f;
    protected virtual float   Gap           => 0.36f;   // hit-1 start → hit-2 start
    protected virtual float   Hit2Active    => 0.12f;
    protected virtual float   Recovery      => 0.75f;

    // Reach ordering (widest → narrowest): effective ≈ 2 + 36 + 6 = 44 > Range 40 >
    // the brain's hold band 21..31.
    protected virtual float   Range         => 40f;
    protected virtual float   VerticalSlack => 30f;

    protected virtual float   Inset1        => 2f;
    protected virtual float   Reach1        => 36f;
    protected virtual float   HalfHeight1   => 14f;
    protected virtual float   Damage1       => 1.0f;
    protected virtual Vector2 Knockback1    => new(240f, -100f);    // under the big-hit line

    protected virtual float   Inset2        => 0f;
    protected virtual float   Reach2        => 44f;
    protected virtual float   HalfHeight2   => 22f;
    protected virtual float   Damage2       => 1.5f;
    protected virtual Vector2 Knockback2    => new(560f, -300f);    // the launcher

    // All four Warlord actions share Active 40 — above every one of their Passives —
    // so once a swing starts nothing else in the kit can steal it mid-windup. The
    // Passives (leap 31 > cage 29 > ram 27 > cleave 25) only order the race to START,
    // and the brain narrows that race to a single request anyway.
    public override int ActivePriority  => WarlordEnemy.ActionActive;
    public override int PassivePriority => 25;

    private float Active => Gap + Hit2Active;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist < Range && MathF.Abs(ctx.ToPlayer.Y) < VerticalSlack;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.HitId        = ctx.Spawner.HitIds.Next();
        ctx.Spawner.HitIds.Next();                    // reserved: hit 2 is HitId + 1
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
        float t  = v.TimeInState - v.WindupDuration;
        if (t < 0f || t >= v.ActiveDuration) return;

        var self = ctx.Self;
        var pos  = self.Body.Position;
        if (t < Hit1Active)
        {
            ctx.Hitboxes?.Publish(new Hitbox(
                WarlordHits.Slab(pos, v.LockedFacing, Inset1, Reach1, HalfHeight1), v.HitId,
                Damage1 * self.Strength, new Vector2(v.LockedFacing * Knockback1.X, Knockback1.Y),
                Faction.Enemy, self.Id, WarlordHits.Ember,
                targets: HitTargets.EntitiesOnly, origin: pos));
        }
        else if (t >= Gap)
        {
            ctx.Hitboxes?.Publish(new Hitbox(
                WarlordHits.Slab(pos, v.LockedFacing, Inset2, Reach2, HalfHeight2, -4f), v.HitId + 1,
                Damage2 * self.Strength, new Vector2(v.LockedFacing * Knockback2.X, Knockback2.Y),
                Faction.Enemy, self.Id, WarlordHits.Blood,
                targets: HitTargets.EntitiesOnly, origin: pos));
        }
    }

    // Windup: the forehand slab outlined and filling from the body outward. Between
    // the hits: the overhead slab outlines while an arm-mark swings up over the
    // head — that is the "second one is coming" read. Each strike: a solid slab.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var pos = body.Position;
        int f   = v.LockedFacing;
        float time = v.TimeInState;

        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            var r = WarlordHits.Slab(pos, f, Inset1, Reach1, HalfHeight1);
            WarlordHits.Outline(t, r, WarlordHits.Ember * (0.4f + 0.6f * p));
            float w = r.Width * p;
            float x = f > 0 ? r.Left : r.Right - w;
            t.Box(x, r.Top, w, r.Height, WarlordHits.Ember * (0.12f + 0.3f * p));
            return;
        }

        float a = time - v.WindupDuration;
        if (a < Hit1Active)
        {
            t.Box(WarlordHits.Slab(pos, f, Inset1, Reach1, HalfHeight1).Left, pos.Y - HalfHeight1,
                  Reach1, HalfHeight1 * 2f, WarlordHits.Ember * 0.7f);
        }
        else if (a < Gap)
        {
            float p = (a - Hit1Active) / MathF.Max(Gap - Hit1Active, 1e-3f);
            var r = WarlordHits.Slab(pos, f, Inset2, Reach2, HalfHeight2, -4f);
            WarlordHits.Outline(t, r, WarlordHits.Blood * (0.35f + 0.65f * p));
            // The arm: a bar that swings from behind the body up over the head.
            float ang = MathF.PI * (f > 0 ? 1f - p : p);   // from behind → overhead → forward
            var tip = pos + new Vector2(MathF.Cos(ang) * 26f, -MathF.Abs(MathF.Sin(ang)) * 26f - 4f);
            t.Line(pos, tip, WarlordHits.Blood * (0.5f + 0.5f * p), 2f + 2f * p);
            t.Rect(tip, 3f + 3f * p, Color.Lerp(WarlordHits.Blood, Color.White, p));
        }
        else if (a < v.ActiveDuration)
        {
            var r = WarlordHits.Slab(pos, f, Inset2, Reach2, HalfHeight2, -4f);
            t.Box(r.Left, r.Top, r.Width, r.Height, WarlordHits.Blood * 0.7f);
        }
    }
}

// ── Arms: ram ───────────────────────────────────────────────────────────────
// A stomp, then a shoulder charge: Speed px/s along the locked facing for Active
// seconds, body-anchored hitbox, EntitiesOnly so a wall stops it dead. The long
// recovery is what a whiff costs.
public class WarlordRamAction : EnemyActionState
{
    protected virtual float   Windup     => 0.45f;
    protected virtual float   Active     => 0.32f;
    protected virtual float   Recovery   => 0.70f;
    protected virtual float   Speed      => 400f;        // × 0.32 s ≈ 128 px
    protected virtual float   HalfWidth  => 16f;
    protected virtual float   HalfHeight => 16f;
    protected virtual float   Damage     => 1.2f;
    protected virtual Vector2 Knockback  => new(480f, -200f);

    public override int ActivePriority  => WarlordEnemy.ActionActive;
    public override int PassivePriority => 27;

    // Grounded-ish. The brain owns the range band.
    public override bool CheckPreConditions(in EnemyContext ctx)
        => MathF.Abs(ctx.Self.Body.Velocity.Y) < 30f && WarlordProbe.Grounded(ctx);

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
        float prev = v.TimeInState;
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState, end = v.WindupDuration + v.ActiveDuration;
        var body = ctx.Self.Body;

        if (t >= v.WindupDuration && t < end)
        {
            body.Velocity.X = v.LockedFacing * Speed;
            var p = body.Position;
            ctx.Hitboxes?.Publish(new Hitbox(
                new BoundingBox(p.X - HalfWidth, p.Y - HalfHeight, p.X + HalfWidth, p.Y + HalfHeight),
                v.HitId, Damage * ctx.Self.Strength,
                new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
                Faction.Enemy, ctx.Self.Id, WarlordHits.Ember,
                targets: HitTargets.EntitiesOnly, origin: p));
        }
        else if (prev < end && t >= end)
        {
            body.Velocity.X = v.LockedFacing * Speed * 0.25f;   // skid into the recovery
        }
    }

    // Windup: a lane along the floor the full charge length, with a stomp ring that
    // tightens under the body. Active: speed lines.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var pos = body.Position;
        int f   = v.LockedFacing;
        float time = v.TimeInState;
        float lane = Speed * Active;

        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            float len = lane * (0.3f + 0.7f * p);
            float x0  = f > 0 ? pos.X + 10f : pos.X - 10f - len;
            float pulse = p < 0.6f ? 1f : 0.6f + 0.4f * MathF.Abs(MathF.Sin(time * 34f));
            t.Box(x0, pos.Y + HalfHeight - 3f, len, 3f, WarlordHits.Ember * ((0.3f + 0.6f * p) * pulse));
            t.Box(x0, pos.Y - HalfHeight, len, HalfHeight * 2f, WarlordHits.Ember * (0.06f + 0.14f * p));
            float r = 22f - 12f * p;
            t.Ring(pos + new Vector2(0f, HalfHeight - 2f), r, WarlordHits.Ember * (0.3f + 0.7f * p), 12, 1f + p);
            return;
        }
        if (time < v.WindupDuration + v.ActiveDuration)
        {
            for (int i = 0; i < 4; i++)
            {
                float len = 18f - i * 4f;
                float x   = pos.X - f * (12f + i * 7f);
                t.Box(f > 0 ? x - len : x, pos.Y - 9f + i * 6f, len, 2f, WarlordHits.Ember * (0.7f - i * 0.15f));
            }
            t.Rect(pos + new Vector2(f * 14f, 0f), new Vector2(6f, HalfHeight * 2f), WarlordHits.Bone * 0.5f);
        }
    }
}

// ── Arms: leap + shockwave slam ─────────────────────────────────────────────
// Timeline (t = TimeInState):
//   [0, Windup)                      crouch; the landing ring is drawn at LockedAim
//   the frame t crosses Windup       launch: v = Δ/T − ½gT toward LockedAim
//   [Windup, Windup + MaxAir)        flight; once past MinAir, the anchor probe
//                                    firing SNAPS the clock to the slam phase
//   [slamStart, slamStart + Slam)    shockwave: a floor-wide slab either side of the
//                                    body, one tile-carving frame first
//   then Recovery
// LockedAim is the ABSOLUTE landing point (not a direction) so the telegraph can
// draw it and a snapshot mid-flight restores the same destination.
public class WarlordLeapAction : EnemyActionState
{
    protected virtual float   Windup         => 0.50f;
    protected virtual float   MaxAir         => 1.60f;
    protected virtual float   MinAir         => 0.15f;
    protected virtual float   Slam           => 0.15f;
    protected virtual float   Recovery       => 0.65f;

    protected virtual float   FlightTime     => 0.60f;
    protected virtual float   MaxRun         => 280f;
    protected virtual float   MaxRise        => 160f;
    protected virtual float   MaxDrop        => 280f;
    protected virtual float   MinRise        => 230f;
    protected virtual float   MaxLaunchSpeed => 680f;
    // Air drag shortens every arc; aim a hair past the target to land on it.
    protected virtual float   Overshoot      => 1.08f;

    protected virtual float   ShockHalfWidth => 48f;
    protected virtual float   ShockUp        => 6f;      // above the body centre
    protected virtual float   ShockDown      => 24f;     // below the body centre
    protected virtual float   Damage         => 1.5f;
    protected virtual Vector2 Knockback      => new(520f, -300f);
    // Carved out of every tile the wave crosses on the landing frame — OUTWARD of the
    // body only (see TileSlabs): the wave radiates, it does not dig under its own
    // feet, or a boss landing on dirt would sink a cell per slam. Breaks foam and
    // sand outright (0.5), chips dirt (2.0), does nothing to stone.
    protected virtual float   TileDamage     => 1.0f;
    protected virtual float   TileInset      => WarlordEnemy.Radius + 4f;

    public override int ActivePriority  => WarlordEnemy.ActionActive;
    public override int PassivePriority => 31;

    private float SlamStart => Windup + MaxAir;
    private float Active    => MaxAir + Slam;

    // Grounded, not point-blank, and the landing spot has a floor somewhere under it —
    // a leap onto thin air over a pit is a leap out of the fight.
    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist >= 24f && MathF.Abs(ctx.Self.Body.Velocity.Y) < 40f && WarlordProbe.Grounded(ctx)
        && FloorUnder(ctx.Spawner?.Chunks, ClampTarget(ctx.Self.Body.Position, ctx.Input.AimWorld));

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        v.LockedAim    = ClampTarget(ctx.Self.Body.Position, ctx.Input.AimWorld);
        PopulateDurations(ref v);
    }

    // A solid cell within FloorSearchTiles below the point (no terrain ⇒ assume yes).
    protected virtual int FloorSearchTiles => 12;
    private bool FloorUnder(ChunkMap chunks, Vector2 at)
    {
        if (chunks == null) return true;
        for (int k = 0; k <= FloorSearchTiles; k++)
            if (TileQuery.IsSolidAt(chunks, at.X, at.Y + k * Chunk.TileSize)) return true;
        return false;
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    private Vector2 ClampTarget(Vector2 from, Vector2 aim)
    {
        var d = (aim - from) * Overshoot;
        if (MathF.Abs(d.X) > MaxRun) d.X = MathF.Sign(d.X) * MaxRun;
        d.Y = MathHelper.Clamp(d.Y, -MaxRise, MaxDrop);
        return from + d;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float prev = v.TimeInState;
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        var self = ctx.Self;
        var body = self.Body;

        if (t < v.WindupDuration) { body.Velocity.X *= 0.7f; return; }

        if (prev < v.WindupDuration) { Launch(body, v.LockedAim, self.GravityScale); return; }

        float slamStart = SlamStart;
        if (t < slamStart)
        {
            // Flight. Landing (past the grace window, not still rising — brushing a
            // wall on the way up is not a landing) jumps the clock to the slam — half
            // a frame short of it, so next frame's `prev < slamStart <= t` is the one
            // tile-carving frame.
            if (t >= v.WindupDuration + MinAir && body.Velocity.Y > -10f && WarlordProbe.Anchored(ctx))
                v.TimeInState = slamStart - 0.5f * ctx.Dt;
            return;
        }

        if (t < slamStart + Slam)
        {
            body.Velocity.X = 0f;                             // planted
            var p = body.Position;
            var region = new BoundingBox(p.X - ShockHalfWidth, p.Y - ShockUp, p.X + ShockHalfWidth, p.Y + ShockDown);
            float side = ctx.ToPlayer.X >= 0f ? 1f : -1f;      // away from the body, live
            ctx.Hitboxes?.Publish(new Hitbox(
                region, v.HitId, Damage * self.Strength,
                new Vector2(side * Knockback.X, Knockback.Y),
                Faction.Enemy, self.Id, WarlordHits.Blood,
                targets: HitTargets.EntitiesOnly));
            if (prev < slamStart)
            {
                var (left, right) = TileSlabs(p);
                ctx.Hitboxes?.Publish(new Hitbox(left,  v.HitId, TileDamage, Vector2.Zero,
                    Faction.Enemy, self.Id, WarlordHits.Bone, targets: HitTargets.TilesOnly));
                ctx.Hitboxes?.Publish(new Hitbox(right, v.HitId, TileDamage, Vector2.Zero,
                    Faction.Enemy, self.Id, WarlordHits.Bone, targets: HitTargets.TilesOnly));
            }
        }
    }

    // The two outward halves of the wave, starting TileInset either side of the body.
    private (BoundingBox left, BoundingBox right) TileSlabs(Vector2 p) => (
        new BoundingBox(p.X - ShockHalfWidth, p.Y - ShockUp, p.X - TileInset, p.Y + ShockDown),
        new BoundingBox(p.X + TileInset, p.Y - ShockUp, p.X + ShockHalfWidth, p.Y + ShockDown));

    // p(T) = p0 + v·T + ½·g·T²  ⇒  v = Δ/T − ½·g·T. Same solve as EnemyHopState.
    private void Launch(PhysicsBody body, Vector2 target, float gravityScale)
    {
        var   delta = target - body.Position;
        float g     = Simulation.WorldGravityY * gravityScale;
        float T     = MathF.Max(FlightTime, 1e-3f);
        var   launch = new Vector2(delta.X / T, delta.Y / T - 0.5f * g * T);
        if (launch.Y > -MinRise) launch.Y = -MinRise;
        float speed = launch.Length();
        if (speed > MaxLaunchSpeed) launch *= MaxLaunchSpeed / speed;
        body.Velocity = launch;
    }

    // Crouch: a landing ring at the destination that tightens and brightens, a
    // dotted arc from the body to it, and a compression bar under the feet. Flight:
    // the ring keeps pulsing so the destination stays readable. Slam: the wave.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var pos  = body.Position;
        var dest = v.LockedAim;
        float time = v.TimeInState;
        float slamStart = SlamStart;

        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            t.Ring(dest, ShockHalfWidth, WarlordHits.Blood * 0.18f, 20);
            t.Ring(dest, ShockHalfWidth * (1f - 0.7f * p), WarlordHits.Blood * (0.3f + 0.6f * p), 20, 1f + p);
            t.Rect(dest, 3f + 3f * p, Color.Lerp(WarlordHits.Blood, Color.White, p));
            // Dotted arc, sampled on the real ballistic (drag ignored).
            float g = Simulation.WorldGravityY, T = FlightTime;
            var d = dest - pos;
            var v0 = new Vector2(d.X / T, d.Y / T - 0.5f * g * T);
            if (v0.Y > -MinRise) v0.Y = -MinRise;
            for (int i = 1; i <= 8; i++)
            {
                float s = T * i / 8f;
                var q = pos + v0 * s + new Vector2(0f, 0.5f * g * s * s);
                t.Rect(q, 2f, WarlordHits.Ember * (0.25f + 0.5f * p));
            }
            int w = 10 + (int)(p * 20f);
            t.Box(pos.X - w / 2f, pos.Y + 14f, w, 2f, Color.Lerp(new Color(WarlordHits.Ember, 90), WarlordHits.Ember, p));
            return;
        }

        if (time < slamStart)
        {
            float pulse = 0.5f + 0.5f * MathF.Abs(MathF.Sin(time * 18f));
            t.Ring(dest, ShockHalfWidth * (0.6f + 0.4f * pulse), WarlordHits.Blood * (0.25f + 0.45f * pulse), 20, 1.5f);
            t.Rect(dest, 4f, WarlordHits.Blood);
            // Danger aura under the falling body, like the pouncer's.
            if (body.Velocity.Y > 60f)
                t.Box(pos.X - 10f, pos.Y + 6f, 20f, 10f, WarlordHits.Blood * 0.35f);
            return;
        }

        if (time < slamStart + Slam)
        {
            float p = (time - slamStart) / MathF.Max(Slam, 1e-3f);
            float reach = ShockHalfWidth * (0.3f + 0.7f * p);
            t.Box(pos.X - reach, pos.Y - ShockUp, reach * 2f, ShockUp + ShockDown, WarlordHits.Blood * (0.6f - 0.3f * p));
            t.Ring(pos + new Vector2(0f, 8f), reach, Color.Lerp(WarlordHits.Blood, Color.White, p), 16, 2f);
        }
    }
}

// ── Arms: cage ──────────────────────────────────────────────────────────────
// Two foam walls either side of where you STOOD at Enter (LockedAim is that point),
// Thickness tiles thick, Height tiles tall, each rising from the first solid cell
// under that column. Every cell is telegraphed across the windup. Placement goes
// through ChunkMap.ForceSprout (no support needed, same path as a burst) on the
// windup→active transition; the active window then re-stamps each cell's foam decay
// timer to WallLifetime once it has solidified, which is what makes the cage shorter-
// lived than player-built foam. Cells that would grow into a body are skipped.
public class WarlordCageAction : EnemyActionState
{
    protected virtual float    Windup       => 0.80f;
    protected virtual float    Active       => 0.25f;    // > MovementConfig.SproutLifetime
    protected virtual float    Recovery     => 0.30f;
    protected virtual int      HalfGap      => 4;        // tiles from the target column to the inner face
    protected virtual int      Thickness    => 2;
    protected virtual int      Height       => 6;        // 66 px: twice the player's standing height
    protected virtual int      GroundSearch => 4;        // rows below the feet to look for a floor
    protected virtual float    WallLifetime => 3.2f;
    protected virtual TileType Material     => TileType.Foam;

    public override int ActivePriority  => WarlordEnemy.ActionActive;
    public override int PassivePriority => 29;

    // The brain owns the range band; this only asks "is there a floor to build on".
    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Spawner?.Chunks != null && FloorRow(ctx.Spawner.Chunks, ctx.Target.Bounds, out _);

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.Committed    = true;
        // Absolute: the target's column and the FLOOR row under its feet, as a world
        // point (that cell's centre), so the walls are placed where you WERE and the
        // telegraph can lay the cage out from the anchor alone.
        var chunks = ctx.Spawner?.Chunks;
        var b = ctx.Target.Bounds;
        int col = (int)MathF.Floor(ctx.Target.Position.X / Chunk.TileSize);
        int floor = chunks != null && FloorRow(chunks, b, out int ground) ? ground
                  : (int)MathF.Floor((b.Bottom - 1f) / Chunk.TileSize) + 1;
        v.LockedAim = EnemyBlockCells.Center(col, floor);
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    // First Solid row at or below the target's feet, within GroundSearch rows.
    private bool FloorRow(ChunkMap chunks, in BoundingBox b, out int ground)
    {
        int col  = (int)MathF.Floor(b.CenterX / Chunk.TileSize);
        int feet = (int)MathF.Floor((b.Bottom - 1f) / Chunk.TileSize);
        for (int gy = feet; gy <= feet + GroundSearch; gy++)
            if (chunks.GetCellState(col, gy) == TileState.Solid) { ground = gy; return true; }
        ground = 0;
        return false;
    }

    // The planned cells, in a fixed order (left wall then right, column-major,
    // bottom-up), each resolved against the live terrain: a column's floor is the
    // first solid cell at or below the anchor row, searched GroundSearch rows down.
    // With no terrain (the telegraph) the anchor row itself is taken as the floor.
    private int Cells(ChunkMap chunks, Vector2 anchor, Span<(int gtx, int gty)> into)
    {
        var (ac, ar) = EnemyBlockCells.CellOf(anchor);
        int n = 0;
        for (int side = -1; side <= 1; side += 2)
        for (int k = 0; k < Thickness; k++)
        {
            int gtx = ac + side * (HalfGap + k);
            int ground = -1;
            for (int gy = ar; gy <= ar + GroundSearch + 1; gy++)
                if (chunks == null || chunks.GetCellState(gtx, gy) == TileState.Solid) { ground = gy; break; }
            if (ground < 0) continue;
            for (int h = 1; h <= Height && n < into.Length; h++)
                into[n++] = (gtx, ground - h);
        }
        return n;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float prev = v.TimeInState;
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return;
        if (t < v.WindupDuration || t >= v.WindupDuration + v.ActiveDuration) return;

        Span<(int gtx, int gty)> cells = stackalloc (int, int)[2 * 2 * 8];
        int n = Cells(chunks, v.LockedAim, cells);

        if (prev < v.WindupDuration)
        {
            for (int i = 0; i < n; i++)
            {
                var (gtx, gty) = cells[i];
                if (EnemyBlockCells.Overlaps(ctx.Self.Body, gtx, gty)) continue;
                if (EnemyBlockCells.Overlaps(ctx.Target.Bounds, gtx, gty)) continue;
                chunks.ForceSprout(gtx, gty, Material);      // null ⇒ occupied: skip
            }
            return;
        }

        // Re-stamp the decay timer on every cell that has solidified into our material.
        for (int i = 0; i < n; i++)
        {
            var (gtx, gty) = cells[i];
            if (chunks.GetCellState(gtx, gty) == TileState.Solid && chunks.GetCellType(gtx, gty) == Material)
                chunks.Foam.Register(gtx, gty, WallLifetime);
        }
    }

    // Windup: every planned cell outlined, lighting bottom-up as the windup fills, with
    // a pair of bright "bars" rising from the ground to the planned wall height. A
    // mark at the anchor says "this is where you were".
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.TimeInState >= v.WindupDuration) return;
        const float ts = Chunk.TileSize;
        float p = v.TimeInState / MathF.Max(v.WindupDuration, 1e-3f);
        var (ac, ar) = EnemyBlockCells.CellOf(v.LockedAim);

        Span<(int gtx, int gty)> cells = stackalloc (int, int)[2 * 2 * 8];
        int n = Cells(null, v.LockedAim, cells);     // terrain-free layout: the anchor row is the floor
        for (int i = 0; i < n; i++)
        {
            var (gtx, gty) = cells[i];
            int h = ar - gty;                           // 1 = bottom cell
            bool lit = p * Height >= h - 1;
            t.Rect(EnemyBlockCells.Center(gtx, gty), new Vector2(ts - 2f, ts - 2f),
                   WarlordHits.Cage * (lit ? 0.18f + 0.35f * p : 0.08f));
        }
        // Inner faces: a bright bar rising from the floor to the planned wall height.
        for (int side = -1; side <= 1; side += 2)
        {
            float x = (ac + side * HalfGap + (side > 0 ? 0f : 1f)) * ts;
            t.Box(x - 1f, ar * ts - Height * ts * p, 2f, Height * ts * p, WarlordHits.Cage * (0.5f + 0.5f * p));
        }
        t.Ring(v.LockedAim - new Vector2(0f, ts), 6f + 4f * MathF.Abs(MathF.Sin(v.TimeInState * 12f)), WarlordHits.Cage * 0.6f, 10);
        // The caster: a charge over the head.
        t.Disc(body.Position + new Vector2(0f, -20f), 2f + 3f * p, Color.Lerp(new Color(WarlordHits.Cage, 80), WarlordHits.Cage, p));
    }
}


// ── Body ────────────────────────────────────────────────────────────────────
public sealed class WarlordEnemy : EnemyEntity
{
    public const float Radius    = 15f;
    public const float MaxHp     = 10f;
    // Shared Active priority of the four actions: above all of their Passives (25..31),
    // so a committed attack is never replaced by another one mid-swing.
    public const int   ActionActive = 40;

    private static readonly Color CalmColor  = new(150, 60, 50);
    private static readonly Color RageColor  = new(255, 70, 40);
    private static readonly Color RestColor  = new(110, 110, 130);

    public override EntityKind Kind        => EntityKind.Warlord;
    public override string     DisplayName => "Warlord";

    public bool Enraged   => (Scratch.I0 & 1) != 0;
    public bool Exhausted => Scratch.F1 > 0f;

    public WarlordEnemy(Vector2 pos)
        : base(new PhysicsBody(Polygon.CreateRegular(Radius, 8), pos),
               health: MaxHp,
               movement: new List<EnemyMovementState>
               {
                   new EnemyIdleState(),          // 0 — fallback
                   new WarlordAirborneState(),    // 1
                   new WarlordWalkState(),        // 2
                   new WarlordHoldState(),        // 3 — plant while attacking (leap-aware)
                   // No stagger: the boss has poise. Hitstop still lands on big hits.
               },
               actions: new List<EnemyActionState>
               {
                   new WarlordCleaveAction(),     // 0  WarlordController.Cleave
                   new WarlordRamAction(),        // 1  WarlordController.Ram
                   new WarlordLeapAction(),       // 2  WarlordController.Leap
                   new WarlordCageAction(),       // 3  WarlordController.Cage
               },
               controller: new WarlordController())
    {
        Mass               = 5f;        // a stock slash barely rocks it
        Armor              = 2f;
        Body.FrictionScale = 0.12f;
        Color              = CalmColor;
        Sprite             = Sprites.Warlord(Radius);
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        base.Update(dt, player, hitboxes, spawner);
        Color = Exhausted ? RestColor : Enraged ? RageColor : CalmColor;
    }

    public override void Telegraph(TelegraphList t)
    {
        base.Telegraph(t);
        if (IsDead) return;
        if (Exhausted)
        {
            // Open: a slow, sagging ring.
            float s = 0.5f + 0.5f * MathF.Sin(Scratch.F1 * 6f);
            t.Ring(Body.Position, Radius + 4f + 2f * s, RestColor * 0.5f, 16);
        }
        else if (Enraged)
        {
            t.Ring(Body.Position, Radius + 3f, RageColor * 0.35f, 16);
        }
    }
}
