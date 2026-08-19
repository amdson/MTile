using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// MVP action-FSM states. One concrete action: a melee swing with the standard
// Windup → Active → Recovery triad. Telegraph is purely a Draw concern, read
// off TimeInState / WindupDuration in EnemyActionVars — the sim never declares
// a separate telegraph descriptor. See Plans/ENEMY_CAPABILITY_FRAMEWORK.md §3.

// Forward melee swing. Subclasses tune Windup / Active / Recovery / Range /
// Damage / Knockback via virtual properties.
public class EnemyMeleeAction : EnemyActionState
{
    protected virtual float   Windup            => 0.45f;
    protected virtual float   Active            => 0.12f;
    protected virtual float   Recovery          => 0.40f;
    protected virtual float   Range             => 32f;
    protected virtual float   VerticalSlack     => 24f;
    protected virtual float   HitboxReach       => 22f;
    protected virtual float   HitboxHalfHeight  => 12f;
    protected virtual float   Damage            => 1.0f;
    // Knockback impulse — Entity.OnHit divides by target mass, so a player at
    // Mass 2.5 gains (X/2.5, Y/2.5) px/s. Tuned to actually launch the player
    // (~180 px/s horizontal + ~70 up) rather than nudge them.
    protected virtual Vector2 Knockback         => new(460f, -180f);
    protected virtual Color   TelegraphColor    => Color.Red;
    protected virtual Color   StrikeColor       => Color.OrangeRed;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 25;

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
            targets: HitTargets.EntitiesOnly));
    }

    // Telegraph IS the Draw — read everything off vars, just like the player's
    // slash dot or stab tip. Windup: a growing dot offset toward facing, color
    // ramping toward TelegraphColor. Strike flash: a faint slab where the
    // hitbox sits. Recovery: nothing — the body sprite alone reads the lockout.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        float t = v.TimeInState;
        if (v.WindupDuration > 0f && t < v.WindupDuration)
        {
            float p = t / v.WindupDuration;       // 0 → 1 across windup
            int   sz = 2 + (int)(p * 4f);
            float off = 8f + p * 14f;
            var pos = body.Position + new Vector2(v.LockedFacing * off, 0f);
            var color = Color.Lerp(new Color(TelegraphColor, 100), TelegraphColor, p);
            sb.Draw(pixel, new Rectangle((int)pos.X - sz / 2, (int)pos.Y - sz / 2, sz, sz), color);
        }
        else if (t < v.WindupDuration + v.ActiveDuration)
        {
            float halfReach = HitboxReach * 0.5f;
            var c = body.Position + new Vector2(v.LockedFacing * (8f + halfReach), 0f);
            sb.Draw(pixel, new Rectangle(
                (int)(c.X - halfReach), (int)(c.Y - HitboxHalfHeight),
                (int)HitboxReach, (int)(HitboxHalfHeight * 2f)),
                StrikeColor * 0.55f);
        }
    }
}

// Forward lunge. Active window overrides Body.Velocity.X so the brute glides
// into the player; the hitbox is published on the body itself so contact during
// the dash damages on touch (à la StalkerEnemy.Lunge). Mid-range trigger
// disjoint from EnemyMeleeAction so the two don't fight for the same situation.
public class EnemyLungeAction : EnemyActionState
{
    protected virtual float Windup       => 0.35f;
    protected virtual float Active       => 0.25f;
    protected virtual float Recovery     => 0.45f;
    protected virtual float MinRange     => 36f;     // disjoint with EnemyMeleeAction.Range (32)
    protected virtual float MaxRange     => 90f;
    protected virtual float VertSlack    => 24f;
    protected virtual float LungeSpeed   => 260f;
    protected virtual float HitHalfWidth => 12f;
    protected virtual float HitHalfHeight=> 12f;
    protected virtual float Damage       => 0.9f;
    // Heavier than the melee swing — a lunge is a committed dash, so the launch
    // reads as a body-check (~210 px/s horizontal + ~95 up against player Mass 2.5).
    protected virtual Vector2 Knockback  => new(540f, -240f);

    public override int ActivePriority  => 30;
    public override int PassivePriority => 24;

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
            targets: HitTargets.EntitiesOnly));
    }

    // Layered telegraph — readable across the 0.35s windup. Phases:
    //   0 → 0.50  danger bar forms along the lunge path
    //   0.50→0.80 bar pulses + side ticks pop out at the tip (suggesting hit zone)
    //   0.80→1.00 anticipation recoil flash behind the body
    // Active: forward streak + 3 fanning speed lines. Recovery: nothing —
    // body sprite alone reads the post-strike lockout (same as the player slash).
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        const int BarMaxLen     = 64;
        const float HitZoneTickStart = 0.50f;
        const float RecoilStart      = 0.80f;
        var hot = Color.MediumPurple;

        float t = v.TimeInState;
        if (v.WindupDuration > 0f && t < v.WindupDuration)
        {
            float p = t / v.WindupDuration;

            // Phase 1: danger bar — extends + brightens across full windup. Pulse
            // (fast sin) layers in starting at HitZoneTickStart so the late
            // half reads as "imminent" rather than steady.
            int barLen = (int)(p * BarMaxLen);
            var origin = body.Position + new Vector2(v.LockedFacing * 12f, 0f);
            var start  = v.LockedFacing > 0 ? origin : origin + new Vector2(-barLen, 0f);
            float pulse = p < HitZoneTickStart ? 1f
                : 0.55f + 0.45f * MathF.Abs(MathF.Sin(t * 32f));
            sb.Draw(pixel,
                new Rectangle((int)start.X, (int)start.Y - 1, barLen, 2),
                hot * ((0.25f + 0.55f * p) * pulse));

            // Phase 2: hit-zone tick marks at the tip — vertical ticks framing
            // the impact band so the player reads "this is where it hits".
            if (p > HitZoneTickStart)
            {
                float tickP = (p - HitZoneTickStart) / (1f - HitZoneTickStart);
                int tickH = 4 + (int)(tickP * 4f);
                float tipX = v.LockedFacing > 0 ? origin.X + barLen : origin.X - barLen;
                sb.Draw(pixel, new Rectangle((int)tipX - 1, (int)origin.Y - tickH - 2, 2, tickH), hot * tickP);
                sb.Draw(pixel, new Rectangle((int)tipX - 1, (int)origin.Y + 2,         2, tickH), hot * tickP);
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
                sb.Draw(pixel,
                    new Rectangle((int)back.X - sz / 2, (int)back.Y - sz / 2, sz, sz),
                    hot * (0.5f + 0.5f * anticip));
            }
        }
        else if (t < v.WindupDuration + v.ActiveDuration)
        {
            // Active: main streak through the body PLUS three fanning speed
            // lines behind so the dash reads as motion rather than a teleport.
            var p = body.Position;
            sb.Draw(pixel,
                new Rectangle((int)(p.X - 14), (int)(p.Y - 1), 28, 2), hot * 0.75f);
            for (int i = 0; i < 3; i++)
            {
                int yOff   = -7 + i * 7;   // -7, 0, +7 vertical spread
                int len    = 12 - i * 3;
                int lineX  = (int)(p.X - v.LockedFacing * (10 + i * 5));
                int startX = v.LockedFacing > 0 ? lineX - len : lineX;
                sb.Draw(pixel,
                    new Rectangle(startX, (int)(p.Y + yOff), len, 1),
                    hot * (0.55f - i * 0.12f));
            }
        }
    }
}

// Wide-area concussive shockwave. Low damage, big radius, big radial knockback —
// the gameplay role is "swat the player out of the air" when they jump in over
// the brute. Long windup (~0.85s) makes it a clear "get away" telegraph rather
// than an instant punish.
//
// Trigger gating is what keeps this from stealing every melee opportunity: the
// player must be at least slightly ABOVE the brute (ToPlayer.Y < -4), so on
// flat ground the brute still prefers melee/lunge. Once the player jumps over
// or lands on top, shockwave's higher passive priority preempts melee.
//
// Knockback direction is computed at publish time as "outward from the brute"
// (sign of player-X relative to brute-X for horizontal, fixed launch for
// vertical). This deliberately ignores LockedFacing so a shockwave that lands
// behind the brute still pushes the target away.
public class EnemyShockwaveAction : EnemyActionState
{
    protected virtual float Windup       => 0.85f;
    protected virtual float Active       => 0.18f;
    protected virtual float Recovery     => 0.55f;
    protected virtual float TriggerRange => 70f;
    protected virtual float VertSlack    => 48f;
    protected virtual float HitRadius    => 56f;
    protected virtual float Damage       => 0.3f;
    // High radial knockback — outward × 700 / 2.5 = 280 px/s on the player,
    // matched with a hefty pop upward so the launch reads as concussive.
    protected virtual float KnockOutward => 700f;
    protected virtual float KnockUp      => -350f;

    public override int ActivePriority  => 32;
    public override int PassivePriority => 26;   // above Melee (25) — wins when player is above

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist < TriggerRange
        && ctx.ToPlayer.Y < -4f
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

        // Radial hitbox centered on the brute. Direction-of-knockback is sampled
        // per-frame from current player position so a target on either side gets
        // pushed outward rather than toward LockedFacing.
        var c = ctx.Self.Body.Position;
        var region = new BoundingBox(
            c.X - HitRadius, c.Y - HitRadius,
            c.X + HitRadius, c.Y + HitRadius);
        float dx = ctx.Player.Body.Position.X - c.X;
        int outSign = dx >= 0f ? 1 : -1;
        ctx.Hitboxes?.Publish(new Hitbox(
            region, v.HitId, Damage,
            new Vector2(outSign * KnockOutward, KnockUp),
            Faction.Enemy, ctx.Self.Id, Color.Gold,
            targets: HitTargets.EntitiesOnly));
    }

    // Telegraph: expanding ring on the ground throughout windup (0→full radius),
    // a "tells where the burst will reach" marker. At 70%+ the ring pulses and
    // a bright core lights up underneath the brute. Active: bright filled disc
    // outline + 8 radial shock-lines fanning outward.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        var hot = Color.Gold;
        float t = v.TimeInState;
        var c0  = body.Position;

        if (v.WindupDuration > 0f && t < v.WindupDuration)
        {
            float p = t / v.WindupDuration;
            int   r = (int)(HitRadius * p);

            // Ground ring — 16 samples around the perimeter so the radius reads
            // clearly even at small windup fractions.
            float pulse = p < 0.70f ? 1f
                : 0.55f + 0.45f * MathF.Abs(MathF.Sin(t * 36f));
            var ringColor = Color.Lerp(new Color(hot, 60), hot, p) * pulse;
            for (int i = 0; i < 16; i++)
            {
                float a = i * MathHelper.TwoPi / 16f;
                var pos = c0 + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
                sb.Draw(pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2), ringColor);
            }

            // Late-windup core glow under the brute — "the burst is loaded."
            if (p > 0.70f)
            {
                float cp = (p - 0.70f) / 0.30f;
                int sz = 4 + (int)(cp * 6f);
                sb.Draw(pixel,
                    new Rectangle((int)c0.X - sz / 2, (int)c0.Y - sz / 2, sz, sz),
                    Color.White * (0.4f + 0.6f * cp));
            }
        }
        else if (t < v.WindupDuration + v.ActiveDuration)
        {
            // Active: bright disc outline + 8 shock-lines fanning out.
            float ap = (t - v.WindupDuration) / v.ActiveDuration;     // 0→1
            int r = (int)HitRadius;
            for (int i = 0; i < 24; i++)
            {
                float a = i * MathHelper.TwoPi / 24f;
                var pos = c0 + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
                sb.Draw(pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2), hot * (1f - ap * 0.4f));
            }
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathHelper.TwoPi / 8f;
                int lineLen = (int)(HitRadius * 0.6f * (1f + ap * 0.4f));
                float dx = MathF.Cos(a), dy = MathF.Sin(a);
                for (int s = 6; s < lineLen; s += 3)
                {
                    var pos = c0 + new Vector2(dx, dy) * s;
                    sb.Draw(pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2), hot * (0.8f - ap * 0.5f));
                }
            }
        }
    }
}

// Bulk block placement — summons a vertical pillar of solid tiles in front of
// the enemy. Single-frame submission on the windup→active transition; the
// chunk sprout system then animates the pillar growing upward over a few
// hundred ms (each cell waits for its parent below to finalize, exactly the
// same mechanism that drives the player's eruption).
//
// Tactical role: a "wall the player out" move. No direct damage — the threat
// is spatial. The player can still slash through the pillar (it's stone, 2x
// max HP), but doing so commits them while the brute repositions.
public class EnemyPillarAction : EnemyActionState
{
    protected virtual float Windup        => 0.65f;
    protected virtual float Active        => 0.10f;
    protected virtual float Recovery      => 0.55f;
    protected virtual float MinRange      => 60f;
    protected virtual float MaxRange      => 220f;
    // Lateral offset in tiles from the enemy body to the pillar column. 3 →
    // ~48 px in front, far enough that the pillar doesn't sprout under the
    // enemy itself.
    protected virtual int   ColumnOffset  => 3;
    // Pillar height in tiles. 4 → 64 px, roughly twice a brute's diameter —
    // reads as "a wall," not a stub.
    protected virtual int   PillarHeight  => 4;
    protected virtual TileType TileType   => TileType.Stone;

    public override int ActivePriority  => 28;
    public override int PassivePriority => 21;       // below Ranged (22) — long-range zoning is the fallback

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

        // Single-frame fire on the windup→active boundary — at a fixed
        // timestep this condition is true on exactly one frame per cast, so
        // the snapshot doesn't need a "fired" flag (chunk state captures the
        // resulting tiles, and the threshold is re-derived from TimeInState
        // on restore).
        if (prevT < v.WindupDuration && v.TimeInState >= v.WindupDuration)
        {
            var chunks = ctx.Spawner.Chunks;
            if (chunks == null) return;
            var (baseTx, baseTy) = PillarBase(ctx.Self.Body.Position, v.LockedFacing);
            for (int i = 0; i < PillarHeight; i++)
            {
                // Bottom cell sprouts from the existing floor below; subsequent
                // cells start Pending and promote as each parent below
                // finalizes, producing the upward-growing visual for free.
                chunks.TryRequestTile(baseTx, baseTy - i, TileType);
            }
        }
    }

    // Telegraph: candle markers at the future cells, growing in height +
    // alpha across the windup. Bottom-up phase ordering mirrors the sprout
    // commit order so the player can pre-read which rung lands first.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.WindupDuration <= 0f) return;
        float t = v.TimeInState;
        if (t >= v.WindupDuration + v.ActiveDuration) return;

        bool inWindup = t < v.WindupDuration;
        float p = inWindup ? t / v.WindupDuration : 1f;

        var (baseTx, baseTy) = PillarBase(body.Position, v.LockedFacing);
        var hot = Color.SandyBrown;
        const int TS = Chunk.TileSize;

        for (int i = 0; i < PillarHeight; i++)
        {
            float phase = MathHelper.Clamp(p * PillarHeight - i, 0f, 1f);
            int cellX = baseTx * TS;
            int cellY = (baseTy - i) * TS;
            int barH  = 4 + (int)(phase * (TS - 4));
            sb.Draw(pixel,
                new Rectangle(cellX + TS / 2 - 2, cellY + TS - barH, 4, barH),
                hot * (0.35f + 0.55f * phase));
            if (phase >= 0.95f)
            {
                sb.Draw(pixel, new Rectangle(cellX,          cellY,            TS, 1), hot);
                sb.Draw(pixel, new Rectangle(cellX,          cellY + TS - 1,   TS, 1), hot);
                sb.Draw(pixel, new Rectangle(cellX,          cellY,            1,  TS), hot);
                sb.Draw(pixel, new Rectangle(cellX + TS - 1, cellY,            1,  TS), hot);
            }
        }
    }

    private (int tx, int ty) PillarBase(Vector2 pos, int facing)
    {
        int bodyTx = (int)MathF.Floor(pos.X / Chunk.TileSize);
        int bodyTy = (int)MathF.Floor(pos.Y / Chunk.TileSize);
        // Base cell sits in front of the enemy at its body row — adjacent to
        // the floor below, so it can sprout immediately on grounded enemies.
        return (bodyTx + facing * ColumnOffset, bodyTy);
    }
}

// Precise block placement — lays a series of N blocks one at a time across
// the active window, forming a horizontal trail extending from in front of
// the enemy. "Precise" means the placement positions are scripted (exact
// (gtx, gty) by index and facing) and the inter-placement interval is fixed;
// nothing is randomized.
//
// Each placement uses TryRequestTile, which silently skips cells with no
// solid/sprouting neighbour — on uneven terrain the trail just has a gap
// there, which is acceptable and avoids needing a separate feasibility scan.
//
// Tactical role: zoning that the player has to actively traverse. Default
// TileType is Dirt (cheap-ish), so a couple of slashes clear a gap; the
// point isn't to wall the player out, it's to deny the cleanest approach
// line during the brute's recovery.
public class EnemyBlockTrailAction : EnemyActionState
{
    protected virtual float    Windup       => 0.45f;
    protected virtual float    Active       => 0.80f;
    protected virtual float    Recovery     => 0.35f;
    protected virtual float    MinRange     => 50f;
    protected virtual float    MaxRange     => 200f;
    // Block count laid across the active window. Per-block placement
    // threshold is at WindupDuration + i * (Active / NumBlocks).
    protected virtual int      NumBlocks    => 5;
    protected virtual int      StartOffset  => 2;
    protected virtual TileType TileType     => TileType.Dirt;

    public override int ActivePriority  => 28;
    public override int PassivePriority => 21;

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
        var chunks = ctx.Spawner.Chunks;
        if (chunks == null) return;

        // Fire any thresholds crossed this frame. With dt < Active / NumBlocks
        // this is at most one per frame; the loop still handles wider dt
        // gracefully so the trail stays intact under engine pauses.
        var (rowTy, originTx) = TrailBase(ctx.Self.Body.Position, v.LockedFacing);
        float interval = v.ActiveDuration / NumBlocks;
        for (int i = 0; i < NumBlocks; i++)
        {
            float threshold = v.WindupDuration + i * interval;
            if (prevT < threshold && v.TimeInState >= threshold)
            {
                int tx = originTx + v.LockedFacing * i;
                chunks.TryRequestTile(tx, rowTy, TileType);
            }
        }
    }

    // Telegraph: pre-placement ghost outlines at every future cell during
    // windup, with the imminent cell pulsing. As each threshold passes, that
    // cell's ghost stops drawing (the chunk renderer owns the real tile).
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.WindupDuration <= 0f && v.ActiveDuration <= 0f) return;
        float t = v.TimeInState;
        if (t >= v.WindupDuration + v.ActiveDuration) return;

        var (rowTy, originTx) = TrailBase(body.Position, v.LockedFacing);
        var hot = Color.Khaki;
        const int TS = Chunk.TileSize;

        float interval = v.ActiveDuration / NumBlocks;
        int nextIdx    = NextPendingIndex(t, in v);
        for (int i = 0; i < NumBlocks; i++)
        {
            float threshold = v.WindupDuration + i * interval;
            if (t >= threshold) continue;        // tile is real now

            int tx    = originTx + v.LockedFacing * i;
            int cellX = tx * TS;
            int cellY = rowTy * TS;

            float baseAlpha = t < v.WindupDuration
                ? 0.25f + 0.45f * (t / v.WindupDuration) * (1f - 0.12f * i)
                : 0.45f;
            float pulse = (i == nextIdx)
                ? 0.55f + 0.45f * MathF.Abs(MathF.Sin(t * 26f))
                : 1f;
            var col = hot * (baseAlpha * pulse);
            sb.Draw(pixel, new Rectangle(cellX,          cellY,            TS, 1), col);
            sb.Draw(pixel, new Rectangle(cellX,          cellY + TS - 1,   TS, 1), col);
            sb.Draw(pixel, new Rectangle(cellX,          cellY,            1,  TS), col);
            sb.Draw(pixel, new Rectangle(cellX + TS - 1, cellY,            1,  TS), col);
        }
    }

    private int NextPendingIndex(float t, in EnemyActionVars v)
    {
        float interval = v.ActiveDuration / NumBlocks;
        for (int i = 0; i < NumBlocks; i++)
        {
            float threshold = v.WindupDuration + i * interval;
            if (t < threshold) return i;
        }
        return -1;
    }

    private (int rowTy, int originTx) TrailBase(Vector2 pos, int facing)
    {
        int bodyTx = (int)MathF.Floor(pos.X / Chunk.TileSize);
        int bodyTy = (int)MathF.Floor(pos.Y / Chunk.TileSize);
        return (bodyTy, bodyTx + facing * StartOffset);
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
public class EnemySlamAction : EnemyActionState
{
    protected virtual float Windup        => 0.20f;
    protected virtual float Active        => 0.16f;
    protected virtual float Recovery      => 0.40f;
    protected virtual float MinFallSpeed  => 80f;
    protected virtual float MaxHorizDist  => 50f;
    protected virtual float HitHalfWidth  => 18f;
    protected virtual float HitHalfHeight => 16f;
    protected virtual float HitOffsetY    => 16f;       // hitbox sits below the body
    protected virtual float Damage        => 1.6f;
    protected virtual Vector2 Knockback   => new(650f, -200f);

    public override int ActivePriority  => 34;
    public override int PassivePriority => 30;          // wins vs melee/lunge when airborne

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
            targets: HitTargets.EntitiesOnly));
    }

    // Telegraph: short windup, so the visual is dense — overhead chevron forming
    // above the brute (anticipation "raised arms"), plus a downward target mark
    // that snaps into place under the body. Active: full vertical strike streak.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        var hot = Color.Crimson;
        float t = v.TimeInState;
        var c0  = body.Position;

        if (v.WindupDuration > 0f && t < v.WindupDuration)
        {
            float p = t / v.WindupDuration;

            // Overhead chevron — two angled bars meeting above the body, growing
            // outward across the windup.
            int len = 4 + (int)(p * 10f);
            for (int s = 0; s < len; s++)
            {
                var l = c0 + new Vector2(-s,     -16 - s);
                var r = c0 + new Vector2( s,     -16 - s);
                sb.Draw(pixel, new Rectangle((int)l.X, (int)l.Y, 2, 2), hot * (0.4f + 0.6f * p));
                sb.Draw(pixel, new Rectangle((int)r.X, (int)r.Y, 2, 2), hot * (0.4f + 0.6f * p));
            }

            // Downward target indicator under the body — appears at p > 0.4,
            // grows brighter as fire-time approaches.
            if (p > 0.4f)
            {
                float tp = (p - 0.4f) / 0.6f;
                int mark = 6 + (int)(tp * 6f);
                var t0 = c0 + new Vector2(0f, HitOffsetY);
                sb.Draw(pixel, new Rectangle((int)t0.X - mark / 2, (int)t0.Y - 1, mark, 2), hot * tp);
                sb.Draw(pixel, new Rectangle((int)t0.X - 1, (int)t0.Y - mark / 2, 2, mark), hot * tp);
            }
        }
        else if (t < v.WindupDuration + v.ActiveDuration)
        {
            // Active: thick downward streak from body to hitbox center + impact slab.
            sb.Draw(pixel,
                new Rectangle((int)c0.X - 2, (int)c0.Y, 4, (int)HitOffsetY),
                hot * 0.8f);
            var hc = c0 + new Vector2(0f, HitOffsetY);
            sb.Draw(pixel,
                new Rectangle((int)(hc.X - HitHalfWidth), (int)(hc.Y - HitHalfHeight),
                              (int)(HitHalfWidth * 2f), (int)(HitHalfHeight * 2f)),
                hot * 0.55f);
        }
    }
}

// Very-close-range 360° spin. Triggers when the player is right on top of the
// brute (Dist < 22) — covering the awkward case where melee's forward hitbox
// would whiff because the player is too close to be "ahead of" the brute. Wide
// circular hitbox so it catches the player wherever they ended up.
//
// Moderate damage, low radial knockback — the spin is meant to PRESSURE rather
// than punt, so the player doesn't get a free reset.
public class EnemySpinAction : EnemyActionState
{
    protected virtual float Windup       => 0.30f;
    protected virtual float Active       => 0.35f;
    protected virtual float Recovery     => 0.35f;
    protected virtual float TriggerRange => 22f;
    protected virtual float VertSlack    => 24f;
    protected virtual float HitRadius    => 26f;
    protected virtual float Damage       => 0.8f;
    protected virtual float KnockOutward => 280f;     // light push so the player stays in range
    protected virtual float KnockUp      => -120f;

    public override int ActivePriority  => 32;
    public override int PassivePriority => 27;        // above Melee (25) — wins at point-blank

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist < TriggerRange && MathF.Abs(ctx.ToPlayer.Y) < VertSlack;

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

        var c = ctx.Self.Body.Position;
        var region = new BoundingBox(
            c.X - HitRadius, c.Y - HitRadius,
            c.X + HitRadius, c.Y + HitRadius);
        float dx = ctx.Player.Body.Position.X - c.X;
        int outSign = dx >= 0f ? 1 : -1;
        ctx.Hitboxes?.Publish(new Hitbox(
            region, v.HitId, Damage,
            new Vector2(outSign * KnockOutward, KnockUp),
            Faction.Enemy, ctx.Self.Id, Color.Aquamarine,
            targets: HitTargets.EntitiesOnly));
    }

    // Telegraph: 3 orbiting dots spinning around the brute. Spin rate ramps up
    // through windup; on active, the dots merge into a continuous ring + motion
    // streaks reading as a whirling sweep.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        var hot = Color.Aquamarine;
        float t = v.TimeInState;
        var c0  = body.Position;

        if (v.WindupDuration > 0f && t < v.WindupDuration)
        {
            float p = t / v.WindupDuration;
            float spin = t * (8f + p * 24f);          // accelerates
            float r    = 12f + p * 8f;                // pulls outward as fists extend
            var col = Color.Lerp(new Color(hot, 80), hot, p);
            for (int i = 0; i < 3; i++)
            {
                float a = spin + i * MathHelper.TwoPi / 3f;
                var pos = c0 + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
                int sz = 3 + (int)(p * 2f);
                sb.Draw(pixel, new Rectangle((int)pos.X - sz / 2, (int)pos.Y - sz / 2, sz, sz), col);
            }
        }
        else if (t < v.WindupDuration + v.ActiveDuration)
        {
            // Active: 16-sample continuous ring at HitRadius + faster trailing
            // streaks behind each "fist" to read as motion blur.
            float ap = (t - v.WindupDuration) / v.ActiveDuration;
            float spin = t * 48f;
            for (int i = 0; i < 16; i++)
            {
                float a = i * MathHelper.TwoPi / 16f;
                var pos = c0 + new Vector2(MathF.Cos(a), MathF.Sin(a)) * HitRadius;
                sb.Draw(pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2), hot * 0.6f);
            }
            for (int i = 0; i < 3; i++)
            {
                float a = spin + i * MathHelper.TwoPi / 3f;
                for (int s = 0; s < 5; s++)
                {
                    float aa = a - s * 0.18f;
                    var pos = c0 + new Vector2(MathF.Cos(aa), MathF.Sin(aa)) * HitRadius;
                    sb.Draw(pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2),
                        hot * ((1f - s * 0.18f) * (1f - ap * 0.3f)));
                }
            }
        }
    }
}

// Long-range projectile attack. Spawns an EnergyBallProjectile aimed at the
// player's position captured at windup START so a moving target can sidestep.
// The spawn fires exactly once on the windup→active transition; no extra
// snapshot field is needed because the transition is computable from
// TimeInState + Dt at a fixed timestep.
public class EnemyRangedAction : EnemyActionState
{
    protected virtual float Windup        => 0.60f;
    protected virtual float Active        => 0.08f;
    protected virtual float Recovery      => 0.50f;
    protected virtual float MinRange      => 90f;    // disjoint with EnemyLungeAction.MaxRange
    protected virtual float MaxRange      => 360f;
    protected virtual float ProjectileSpeed => 500f;
    protected virtual float MuzzleOffset  => 14f;

    public override int ActivePriority  => 28;
    public override int PassivePriority => 22;

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
                muzzle, dir, v.HitId, Faction.Enemy));
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
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        const float CoreStart    = 0.70f;
        const float FlashSeconds = 0.18f;
        var hot = Color.LightCyan;

        float t = v.TimeInState;

        // Post-fire muzzle flash — bright square at the body, fading out across
        // FlashSeconds. Reads as the "bang" frame of the shot.
        if (t >= v.WindupDuration && t < v.WindupDuration + FlashSeconds)
        {
            float fp = (t - v.WindupDuration) / FlashSeconds;
            float fa = 1f - fp;
            int sz  = 14 - (int)(fp * 8f);
            var c   = body.Position;
            sb.Draw(pixel,
                new Rectangle((int)c.X - sz / 2, (int)c.Y - sz / 2, sz, sz),
                hot * fa);
            sb.Draw(pixel,
                new Rectangle((int)c.X - sz, (int)c.Y - 1, sz * 2, 2),
                hot * (fa * 0.6f));
            return;
        }

        if (v.WindupDuration <= 0f || t >= v.WindupDuration) return;

        float p = t / v.WindupDuration;
        var ringColor = Color.Lerp(new Color(hot, 60), hot, p);
        var c0 = body.Position;

        // Outer charging ring — 12 samples, radius grows 4 → 12 px across windup.
        int outerR = 4 + (int)(p * 8f);
        for (int i = 0; i < 12; i++)
        {
            float a   = i * MathHelper.TwoPi / 12f;
            var pos   = c0 + new Vector2(MathF.Cos(a), MathF.Sin(a)) * outerR;
            sb.Draw(pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2), ringColor);
        }

        // 6 inner particles orbiting and converging — start at radius 18, pulled
        // to radius 4 at fire-time. Angular position rotates as a function of t
        // so the swirl reads as energy gathering rather than a static pattern.
        if (p > 0.2f)
        {
            float gather = (p - 0.2f) / 0.8f;       // 0 → 1 across the late windup
            float orbitR = 18f * (1f - gather) + 4f * gather;
            float spin   = t * 12f;
            var particleColor = hot * (0.5f + 0.5f * gather);
            for (int i = 0; i < 6; i++)
            {
                float a = i * MathHelper.TwoPi / 6f + spin;
                var pos = c0 + new Vector2(MathF.Cos(a), MathF.Sin(a)) * orbitR;
                sb.Draw(pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2), particleColor);
            }
        }

        // Late-stage core: pulsing bright spot at the muzzle, signals "fire is
        // imminent." Half-sine envelope so the alpha crescendos toward fire.
        if (p > CoreStart)
        {
            float cp     = (p - CoreStart) / (1f - CoreStart);
            float pulse  = MathF.Sin(cp * MathF.PI);
            int   coreSz = 3 + (int)(pulse * 5f);
            sb.Draw(pixel,
                new Rectangle((int)c0.X - coreSz / 2, (int)c0.Y - coreSz / 2, coreSz, coreSz),
                Color.White * (0.4f + 0.6f * pulse));
        }
    }
}
