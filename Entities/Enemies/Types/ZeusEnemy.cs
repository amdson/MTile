using System;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  ZEUS — a rooted statue at the summit of Levels/hill.json that fights entirely
//  with light.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it:  set "Stage": "hill" in configs/game_config.json, then
//          dotnet run --project MTile.Desktop
//
// The design problem is the same one the Bastion has — a thing that never moves
// can only threaten through commitment on the player's side — but Zeus answers
// it with a REPERTOIRE instead of a single shot, so the question the player is
// asking changes every few seconds:
//
//   BOLT   (ZeusBoltAction)   the heavy one. 1.6s of windup with the firing line
//                             drawn from frame one, and the angle FROZEN at the
//                             instant the telegraph appears — so the whole tell
//                             is honest and sidestepping it always works. Bores
//                             a trench through whatever it is pointed at.
//   STORM  (ZeusStrikeAction) the light one. ~0.2s of tell, a flick of a beam,
//                             ~0.15s of recovery, and the schedule re-opens
//                             immediately — so it reads as a *series* of strikes
//                             rather than one attack. Each one is aimed at the
//                             player's CURRENT position plus a small deterministic
//                             angular jitter, which is what stops the series from
//                             being either free (all identical, stand still and
//                             it misses) or unfair (all perfect, standing still
//                             is death).
//   SWEEP  (ZeusSweepAction)  the area one. Locks a centre angle, then rakes a
//                             beam through an arc around it. Its counterplay is
//                             vertical (jump the arc / drop below it), which is
//                             the axis the other two don't ask about.
//
// ── Terrain destruction ─────────────────────────────────────────────────────
//
// None of these mutate ChunkMap directly. The beam publishes a rotated-rect
// hitbox with HitTargets.All and a damage value at or above a material's MaxHP,
// and CombatSystem's tile path does the breaking — the same route
// RailBoltProjectile takes, and the reason enemy code never has to reason about
// break ordering for determinism (IEntitySpawner.Chunks is documented read-only).
//
// What the beam DOES compute itself is its reach: ZeusBeam.Reach ray-marches the
// live terrain read-only and stops once it has accumulated a frame's worth of
// penetration budget. So the box only ever covers as much as the beam can afford
// to eat this frame; next frame those cells are gone and the march carries on
// past them. That is what makes a beam bore a tunnel over its active window
// instead of vaporising the whole hill on its first frame, with no per-activation
// budget riding in EnemyActionVars.
//
// ── Determinism ─────────────────────────────────────────────────────────────
//
// The "random" in the storm's jitter is ZeusBeam.Hash01(frame, entityId) — a pure
// function of sim state, evaluated in Enter, which a rollback replays on the same
// frame with the same id and therefore reproduces exactly. No System.Random
// anywhere; see the rule list at the top of TemplateEnemy.cs.
//
// Pacing is the other thing that has to be stateless: controllers can't hold
// cooldown counters and actions can't remember their last activation. So the
// repertoire is scheduled off a cycle of the absolute sim frame
// (ZeusBeam.CycleFrame) — each action's precondition names the window it is
// allowed to OPEN in, and once open it runs its clock out normally.
// ─────────────────────────────────────────────────────────────────────────────


// ── Shared beam maths ───────────────────────────────────────────────────────
internal static class ZeusBeam
{
    // ── The repertoire schedule ─────────────────────────────────────────────
    // One cycle ≈ 10s. Windows are OPENING windows only — an action that starts
    // inside its window plays its full windup/active/recovery even if that runs
    // past the window's end (and past the cycle boundary).
    //
    //   cf   0.. 40   BOLT   opens        (runs ~3.6s, lands ~frame 220)
    //   cf 240..400   STORM  opens        (~7 strikes at ~0.36s each)
    //   cf 430..470   SWEEP  opens        (runs ~3.0s, lands ~frame 610 → wraps)
    //   cf 520..600   STORM  opens        (a short second flurry)
    public const int CycleFrames = 600;

    public static int CycleFrame(int frame)
    {
        int cf = frame % CycleFrames;
        return cf < 0 ? cf + CycleFrames : cf;      // frame is never negative, but be total
    }

    public static bool InWindow(int frame, int from, int to)
    {
        int cf = CycleFrame(frame);
        return cf >= from && cf < to;
    }

    // Deterministic [0,1) from two integers. Standard integer avalanche — the
    // point is only that it is a pure function with no visible pattern at the
    // cadence the storm fires at.
    public static float Hash01(int a, int b)
    {
        unchecked
        {
            uint h = (uint)a * 73856093u ^ (uint)b * 19349663u ^ 0x9E3779B9u;
            h ^= h >> 15; h *= 0x85EBCA6Bu;
            h ^= h >> 13; h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / 16777216f;
        }
    }

    public static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    // How far the beam gets this frame. Marches the centre line read-only,
    // charging each solid cell its material MaxHP against `penetration`, and
    // stops at the cell that overdraws the budget (that cell is INCLUDED — the
    // hitbox has to cover the wall it visibly just hit, or the beam reads as
    // stopping one tile short of contact).
    //
    // Step is half a tile, so nothing can be skipped over; the march is along the
    // axis only, which under-counts a beam clipping a cell corner at its edge.
    // That errs toward *more* reach, which is the harmless direction.
    public static float Reach(ChunkMap chunks, Vector2 muzzle, Vector2 dir,
                              float maxLength, float penetration)
    {
        if (chunks == null) return maxLength;

        const float Step = Chunk.TileSize * 0.5f;
        float spent = 0f;
        int lastGtx = int.MinValue, lastGty = int.MinValue;

        for (float d = Step; d <= maxLength; d += Step)
        {
            var p   = muzzle + dir * d;
            int gtx = (int)MathF.Floor(p.X / Chunk.TileSize);
            int gty = (int)MathF.Floor(p.Y / Chunk.TileSize);
            if (gtx == lastGtx && gty == lastGty) continue;      // half-tile step revisits cells
            lastGtx = gtx; lastGty = gty;

            if (chunks.GetCellState(gtx, gty) == TileState.Empty) continue;

            spent += TileDamage.MaxHPFor(chunks.GetCellType(gtx, gty));
            if (spent >= penetration) return d;
        }
        return maxLength;
    }

    // Publish the beam as one rotated box from the muzzle out to `length`.
    // HitTargets.All: the tile path chews terrain, the entity path hits bodies.
    // `origin` is the muzzle, so a body tucked behind the lip of a cell just
    // outside the beam's width is still occluded.
    public static void Publish(in EnemyContext ctx, ref EnemyActionVars v,
                               Vector2 muzzle, Vector2 dir, float length,
                               float halfWidth, float damage, float knockback,
                               float hitstun, Color color)
    {
        if (ctx.Hitboxes == null || length < 1f) return;

        var   poly     = Polygon.CreateRectangle(length, halfWidth * 2f);
        var   centre   = muzzle + dir * (length * 0.5f);
        float rotation = MathF.Atan2(dir.Y, dir.X);

        ctx.Hitboxes.Publish(new Hitbox(
            poly.GetBoundingBox(centre, rotation), v.HitId, damage,
            dir * knockback,
            Faction.Enemy, ctx.Self.Id, color,
            targets: HitTargets.All,
            shape: poly, shapePos: centre, shapeRotation: rotation,
            hitstunSecondsOverride: hitstun,
            origin: muzzle));
    }

    public static Vector2 Dir(in EnemyActionVars v)
        => v.LockedAim.LengthSquared() > 1e-4f ? v.LockedAim : new Vector2(v.LockedFacing == 0 ? 1 : v.LockedFacing, 0f);
}


// ── 1. THE BRAIN ────────────────────────────────────────────────────────────
// A statue: no movement intent, ever. It only points and permits.
//
// WantAttack is the coarse "has the player arrived" gate; the per-action
// schedule windows and range bands do the rest. Stateless, like every controller
// must be (see EnemyController's contract).
public sealed class ZeusController : EnemyController
{
    public float AlertRange { get; init; } = 620f;

    public override EnemyInput Decide(in EnemyContext ctx) => new()
    {
        MoveDir    = Vector2.Zero,
        Jump       = false,
        AimWorld   = ctx.Player.Body.Position,
        WantAttack = ctx.Dist <= AlertRange,
    };
}


// ── 2. THE HEAVY BOLT ───────────────────────────────────────────────────────
// The angle is captured in Enter — the same frame the telegraph first appears —
// and never touched again. That is the whole contract with the player: the line
// you can see on frame one of the windup is the line the beam will occupy 96
// frames later, so "step off the line" is always the right answer and always
// works. Tracking the player during the windup would make the telegraph a lie.
public class ZeusBoltAction : EnemyActionState
{
    protected virtual float Windup   => 1.60f;
    protected virtual float Active   => 0.45f;
    protected virtual float Recovery => 1.55f;

    // Point blank is the reward for climbing the hill: inside MinRange the bolt
    // won't open, so a player on the summit is fighting a statue that can only
    // storm and sweep at them.
    protected virtual float MinRange     => 80f;
    protected virtual float MaxRange     => 640f;
    protected virtual float MuzzleOffset => 18f;
    protected virtual float MaxLength    => 30f * Chunk.TileSize;   // 480px
    protected virtual float HalfWidth    => 13f;                    // ~1.6 tiles across
    // TileMaxHP-units eaten per frame. Stone is 2.0, so ~1.5 stone cells of depth
    // per frame → the full 27 active frames trench roughly the whole MaxLength.
    protected virtual float Penetration  => 3.0f;
    // At/above Stone's MaxHP so a cell inside the box clears outright rather than
    // being left half-chewed. Doubles as the percent contribution on a body hit.
    protected virtual float Damage       => 2.6f;
    // Deliberately well under RailBoltProjectile's 950. A hit here is supposed to
    // cost the player their POSITION on the slope, not their whole approach:
    // knock them far enough and the hill's own shoulder occludes them, which
    // silently ends the fight until they climb back. 520 / player Mass 2.5 ≈
    // 210 px/s — a shove down the slope, not an eviction from the encounter.
    protected virtual float Knockback    => 120f;
    protected virtual float Hitstun      => 0.30f;

    protected virtual Color ChargeColor => new(120, 150, 255);
    protected virtual Color BeamColor   => new(215, 235, 255);

    // Above the sweep, which is above the storm — so a bolt already in flight is
    // never displaced, and the schedule alone decides what opens.
    public override int ActivePriority  => 38;
    public override int PassivePriority => 34;

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (!ZeusBeam.InWindow(ctx.Frame, 0, 40)) return false;
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
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration) return;
        if (t >= v.WindupDuration + v.ActiveDuration) return;

        var dir    = ZeusBeam.Dir(in v);
        var muzzle = ctx.Self.Body.Position + dir * MuzzleOffset;
        float len  = ZeusBeam.Reach(ctx.Spawner?.Chunks, muzzle, dir, MaxLength, Penetration);
        ZeusBeam.Publish(in ctx, ref v, muzzle, dir, len, HalfWidth, Damage, Knockback, Hitstun, BeamColor);
    }

    // Windup: the full swathe as a faint wash (so the player can see the WIDTH
    // they have to clear, not just a hairline), a core that sharpens, and a
    // charge orb at the muzzle that swells and then strobes over the last fifth.
    // Active: the beam itself, drawn out to the reach the burn actually got, with
    // a cap at the front so the stopping point is legible.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var   dir      = ZeusBeam.Dir(in v);
        float rotation = MathF.Atan2(dir.Y, dir.X);
        var   muzzle   = body.Position + dir * MuzzleOffset;
        float time     = v.TimeInState;

        if (time < v.WindupDuration && v.WindupDuration > 0f)
        {
            float p   = time / v.WindupDuration;
            var   mid = muzzle + dir * (MaxLength * 0.5f);
            var   col = Color.Lerp(ChargeColor, BeamColor, p);

            t.RotatedRect(mid, new Vector2(MaxLength, HalfWidth * 2f), rotation, col * (0.08f + 0.16f * p));
            t.RotatedRect(mid, new Vector2(MaxLength, 1f + 3f * p),    rotation, col * (0.30f + 0.60f * p));

            // Converging chevrons — a legible clock on an otherwise static line.
            var perp = new Vector2(-dir.Y, dir.X);
            for (int i = 0; i < 5; i++)
            {
                float phase = 1f - ((p * 1.7f + i * 0.2f) % 1f);
                var   c     = muzzle + dir * (50f + phase * 240f);
                var   tint  = col * (0.2f + 0.8f * (1f - phase));
                t.Rect(c + perp * (HalfWidth + 3f), 2f, tint);
                t.Rect(c - perp * (HalfWidth + 3f), 2f, tint);
            }

            int core = 5 + (int)(p * 10f);
            if (p > 0.80f && (int)(time * 30f) % 2 == 0) core += 6;
            t.Rect(muzzle, core, Color.Lerp(BeamColor, Color.White, p) * (0.55f + 0.45f * p));
            t.Ring(body.Position, 6f + p * 14f, col * (0.25f + 0.5f * p), 12, 1.5f);
            return;
        }

        if (time >= v.WindupDuration + v.ActiveDuration) return;   // recovery draws nothing

        float ap  = (time - v.WindupDuration) / MathF.Max(v.ActiveDuration, 1e-4f);
        float len = MathF.Max(24f, MaxLength);
        var   c2  = muzzle + dir * (len * 0.5f);
        t.RotatedRect(c2, new Vector2(len, HalfWidth * 2f), rotation, BeamColor * (0.85f - 0.25f * ap));
        t.RotatedRect(c2, new Vector2(len, 5f),             rotation, Color.White * 0.9f);
        t.Rect(muzzle, 14, Color.White * 0.9f);
    }
}


// ── 3. THE STORM ────────────────────────────────────────────────────────────
// One activation is ONE strike. The series is an emergent property of a very
// short cycle (~0.36s end to end) inside a long opening window, so the pacing
// lives entirely in the durations — which is the only place a stateless action
// can put it.
//
// The tell is deliberately near the floor of what is reactable: ~11 frames. The
// counterplay is not "dodge this strike", it's "keep moving through the flurry",
// and the jitter is what makes that true. Aim is taken LIVE at Enter (unlike the
// bolt) because a 0.18s lock is functionally the same as no lock — the honesty
// the bolt buys with a frozen angle isn't worth paying for at this timescale.
public class ZeusStrikeAction : EnemyActionState
{
    protected virtual float Windup   => 0.18f;
    protected virtual float Active   => 0.06f;
    protected virtual float Recovery => 0.12f;

    protected virtual float MaxRange     => 640f;
    protected virtual float MuzzleOffset => 16f;
    protected virtual float MaxLength    => 30f * Chunk.TileSize;
    protected virtual float HalfWidth    => 5f;
    // A strike scars the hill rather than tunnelling it — one dirt cell of depth
    // per frame, and stone (2.0) stops it in one.
    protected virtual float Penetration  => 1.0f;
    // Below Stone's MaxHP on purpose: the flurry cannot dig, it only chews the
    // dirt crust. Also a light percent contribution on a body.
    protected virtual float Damage       => 0.9f;
    protected virtual float Knockback    => 150f;
    protected virtual float Hitstun      => 0.12f;

    // Angular spread around the player, in radians (~±7°). Big enough that
    // standing still is not a guaranteed hit and not a guaranteed miss; small
    // enough that the flurry still reads as aimed at YOU.
    protected virtual float JitterRadians => 0.13f;

    protected virtual Color TellColor  => new(255, 240, 160);
    protected virtual Color StrikeColor => new(255, 250, 220);

    public override int ActivePriority  => 30;
    public override int PassivePriority => 25;

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (!ZeusBeam.InWindow(ctx.Frame, 240, 400) && !ZeusBeam.InWindow(ctx.Frame, 520, 600))
            return false;
        if (ctx.Dist > MaxRange) return false;
        return EnemyAim.HasLineOfSight(ctx.Self.Body.Position, ctx.Input.AimWorld,
                                       ctx.Spawner?.Chunks, MuzzleOffset);
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;

        // Aim at where the player is right now, then knock it off by a
        // deterministic amount keyed on (frame, entity id) — so two Zeus statues
        // in the same room jitter differently, and a rollback replay of this
        // frame reproduces this exact angle.
        var aim    = EnemyAim.AimAt(ctx.Input.AimWorld - ctx.Self.Body.Position, v.LockedFacing);
        float jit  = (ZeusBeam.Hash01(ctx.Frame, ctx.Self.Id.Index) - 0.5f) * 2f * JitterRadians;
        v.LockedAim = ZeusBeam.Rotate(aim, jit);

        v.HitId     = ctx.Spawner.HitIds.Next();
        v.Committed = true;
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

        var dir    = ZeusBeam.Dir(in v);
        var muzzle = ctx.Self.Body.Position + dir * MuzzleOffset;
        float len  = ZeusBeam.Reach(ctx.Spawner?.Chunks, muzzle, dir, MaxLength, Penetration);
        ZeusBeam.Publish(in ctx, ref v, muzzle, dir, len, HalfWidth, Damage, Knockback, Hitstun, StrikeColor);
    }

    // A hairline that brightens across 11 frames, then the strike as a solid
    // sliver. Thin on purpose — a wide tell at this speed would read as the bolt
    // and teach the wrong dodge.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var   dir      = ZeusBeam.Dir(in v);
        float rotation = MathF.Atan2(dir.Y, dir.X);
        var   muzzle   = body.Position + dir * MuzzleOffset;
        float time     = v.TimeInState;

        if (time < v.WindupDuration && v.WindupDuration > 0f)
        {
            float p   = time / v.WindupDuration;
            var   mid = muzzle + dir * (MaxLength * 0.5f);
            t.RotatedRect(mid, new Vector2(MaxLength, 1f), rotation, TellColor * (0.25f + 0.55f * p));
            t.Rect(muzzle, 3 + (int)(p * 5f), TellColor * (0.4f + 0.6f * p));
            return;
        }

        if (time >= v.WindupDuration + v.ActiveDuration) return;

        var mid2 = muzzle + dir * (MaxLength * 0.5f);
        t.RotatedRect(mid2, new Vector2(MaxLength, HalfWidth * 2f), rotation, StrikeColor * 0.8f);
        t.RotatedRect(mid2, new Vector2(MaxLength, 2f),             rotation, Color.White * 0.95f);
        t.Rect(muzzle, 9, Color.White * 0.8f);
    }
}


// ── 4. THE SWEEP ────────────────────────────────────────────────────────────
// A beam that rakes. The centre angle is frozen at Enter like the bolt's, but
// the beam itself walks from -Arc/2 to +Arc/2 across the active window as a pure
// function of TimeInState — so it needs no extra vars, and a snapshot restore
// lands the beam exactly where it was.
//
// The windup draws the whole arc, both edges plus the centre line, because the
// question this attack asks is "where is the arc's edge", not "where is the
// line". Sweeping into the hill scoops a fan out of the slope.
public class ZeusSweepAction : EnemyActionState
{
    protected virtual float Windup   => 0.90f;
    protected virtual float Active   => 0.85f;
    protected virtual float Recovery => 1.25f;

    protected virtual float MinRange     => 60f;
    protected virtual float MaxRange     => 620f;
    protected virtual float MuzzleOffset => 18f;
    protected virtual float MaxLength    => 28f * Chunk.TileSize;
    protected virtual float HalfWidth    => 9f;
    protected virtual float Penetration  => 2.0f;
    protected virtual float Damage       => 2.1f;      // ≥ Stone MaxHP: the rake clears cells
    protected virtual float Knockback    => 50f;
    protected virtual float Hitstun      => 0.20f;

    // Total swept angle, centred on the locked aim (~±26°).
    protected virtual float Arc => 0.92f;

    // Fractions of Arc the windup draws: both edges and the centre.
    private static readonly float[] ArcEdges = { -0.5f, 0f, 0.5f };

    protected virtual Color ChargeColor => new(170, 130, 255);
    protected virtual Color BeamColor   => new(235, 200, 255);

    public override int ActivePriority  => 36;
    public override int PassivePriority => 32;

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (!ZeusBeam.InWindow(ctx.Frame, 430, 470)) return false;
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

    // Sweep progress → the beam's direction this instant. Sweeping from the
    // player's side toward the far side (sign of LockedFacing) means the rake
    // starts near where they are and drives them outward rather than herding
    // them into the middle of the arc.
    private Vector2 SweptDir(in EnemyActionVars v)
    {
        float ap = MathHelper.Clamp((v.TimeInState - v.WindupDuration) / MathF.Max(v.ActiveDuration, 1e-4f), 0f, 1f);
        float sign = v.LockedFacing >= 0 ? 1f : -1f;
        return ZeusBeam.Rotate(ZeusBeam.Dir(in v), sign * (ap - 0.5f) * Arc);
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration) return;
        if (t >= v.WindupDuration + v.ActiveDuration) return;

        var dir    = SweptDir(in v);
        var muzzle = ctx.Self.Body.Position + dir * MuzzleOffset;
        float len  = ZeusBeam.Reach(ctx.Spawner?.Chunks, muzzle, dir, MaxLength, Penetration);
        // A fresh HitId per frame: the beam is somewhere new every frame, so the
        // usual "dedupe a multi-frame window down to one hit" would instead mean
        // the rake could only ever land once no matter how long it stayed on you.
        // Hitstun is what keeps that from being a per-frame damage faucet.
        v.HitId = ctx.Spawner.HitIds.Next();
        ZeusBeam.Publish(in ctx, ref v, muzzle, dir, len, HalfWidth, Damage, Knockback, Hitstun, BeamColor);
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var   centre = ZeusBeam.Dir(in v);
        var   muzzle = body.Position + centre * MuzzleOffset;
        float time   = v.TimeInState;

        if (time < v.WindupDuration && v.WindupDuration > 0f)
        {
            float p    = time / v.WindupDuration;
            float sign = v.LockedFacing >= 0 ? 1f : -1f;
            var   col  = Color.Lerp(ChargeColor, BeamColor, p);

            // Both edges plus the centre — the arc, not the line, is the threat.
            foreach (float k in ArcEdges)
            {
                var d = ZeusBeam.Rotate(centre, sign * k * Arc);
                t.Ray(muzzle, MathF.Atan2(d.Y, d.X), MaxLength,
                      1f + p * 2f, col * (0.20f + 0.55f * p));
            }
            // A leading marker riding the arc from start to end, so the DIRECTION
            // of the rake is readable before it starts.
            var lead = ZeusBeam.Rotate(centre, sign * (((p * 1.4f) % 1f) - 0.5f) * Arc);
            t.Rect(muzzle + lead * (MaxLength * 0.55f), 4 + (int)(p * 4f), col);

            t.Ring(body.Position, 8f + p * 12f, col * (0.3f + 0.5f * p), 12, 1.5f);
            t.Rect(muzzle, 4 + (int)(p * 8f), Color.Lerp(BeamColor, Color.White, p));
            return;
        }

        if (time >= v.WindupDuration + v.ActiveDuration) return;

        var   dir      = SweptDir(in v);
        float rotation = MathF.Atan2(dir.Y, dir.X);
        var   m        = body.Position + dir * MuzzleOffset;
        var   c        = m + dir * (MaxLength * 0.5f);
        t.RotatedRect(c, new Vector2(MaxLength, HalfWidth * 2f), rotation, BeamColor * 0.8f);
        t.RotatedRect(c, new Vector2(MaxLength, 3f),             rotation, Color.White * 0.9f);
        t.Rect(m, 11, Color.White * 0.85f);
    }
}


// ── 5. THE WIRING ───────────────────────────────────────────────────────────
public static class ZeusEnemy
{
    public static EnemyBlueprint Blueprint => new()
    {
        Kind = EntityKind.Zeus,

        // ── body ──
        // Big, and Mass 80 makes it genuinely immovable: Entity.OnHit divides the
        // knockback impulse by mass, so nothing the player has shifts a statue.
        // No EnemyStaggerState either — hitting Zeus never interrupts a charge,
        // so the counterplay to a telegraph is always positional.
        Radius        = 16f,
        Sides         = 6,
        Health        = 14f,
        Mass          = 80f,
        // Rooted — the one non-obvious knob, and the literal one: the statue's position
        // is level geometry, not simulation output. Zeus's own beams excavate the spire,
        // and aiming downslope means the ground it stands on is the first thing a beam
        // passes through, so under gravity it promptly digs its perch out from under
        // itself and tumbles into its own crater, ending the encounter without anyone
        // touching it. Weightlessness alone fixed only that one mover; Mass 80 damped a
        // second (knockback) without stopping it, and left the residue drifting, since
        // nothing brings a weightless body back to rest. Rooted stops all of them at
        // once — velocity is zeroed every frame and the depenetration solver is out of
        // the loop, so the summit can erode out from under the statue, the player can
        // build a block into it, and it stays exactly where PopulateHill put it.
        //
        // Which is also the fiction. It is a statue.
        GravityScale  = 0f,
        Rooted        = true,
        FrictionScale = 0.95f,

        Color  = new Color(205, 200, 175),
        Sprite = Sprites.Zeus,

        Controller = new ZeusController { AlertRange = 620f },

        Movement = () => new()
        {
            new EnemyIdleState(),        // 0 — fallback, and the whole kit
        },
        Actions = () => new()
        {
            // Order is snapshot identity — append only.
            new ZeusBoltAction(),
            new ZeusStrikeAction(),
            new ZeusSweepAction(),
        },
    };
}
