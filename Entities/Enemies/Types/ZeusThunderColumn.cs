using System;
using Microsoft.Xna.Framework;

namespace MTile;

// ── THE THUNDER COLUMN ──────────────────────────────────────────────────────
// A pillar of lightning that falls out of the sky at one x and reaches the
// ground. It is the answer to cover, and every one of its rules follows from
// that job:
//
//   * It does not check line of sight to OPEN. Every other Zeus attack gates its
//     precondition on EnemyAim.HasLineOfSight and stays shut when the statue is
//     blind; this one only checks range, so breaking sight does not buy silence.
//   * It is occluded on the way DOWN, not on the way out. The box carries an
//     `origin` at the top of the column rather than at Zeus, so CombatSystem's
//     reachability trace runs vertically along the falling column. A wall beside
//     you is therefore not cover at all — rock at your shoulder is nothing to a
//     thing coming out of the sky — and a roof only delays it.
//   * Because it publishes HitTargets.All, a ceiling is SPENT rather than held.
//     The tile pass visits cells nearest the origin first and skips unreachable
//     ones, so the column eats a roof strictly top-down and then reaches whoever
//     is under it in the same frame the last layer breaks. Overhead cover buys
//     frames; the only thing that buys safety is not being in the band.
//   * That the origin exists is still what keeps this sane. With no origin every
//     cell of the 3400px shaft took damage at once, which is the version that
//     would have sawn the spire in half every cycle and dropped Zeus's own perch
//     into the sea. Top-down and one layer at a time, it notches terrain instead:
//     ~64.8 damage per cast, so dirt goes instantly, stone in four frames, and
//     hardened rock needs two whole casts for a single tile.
//   * So the counterplay is horizontal. ColumnWindup is over two seconds with the
//     full-height band drawn from the first frame, at final width: there is never
//     a question of WHERE, only of whether you cleared it in time.
//   * It hurts: 54% of escalation per connect, against the heavy bolt's 39%. The
//     attack that beats cover has to be the one you most want to not be hit by,
//     or hiding stays correct.
public class ZeusThunderColumnAction : EnemyActionState
{
    // Long enough to be a decision rather than a reaction. The band is at full
    // width from frame one, so every one of these 132 frames is usable.
    protected virtual float Windup   => 2.20f;
    protected virtual float Active   => 0.30f;
    protected virtual float Recovery => 1.30f;

    // No MaxRange worth the name — the column reaches anywhere on the stage. Walking
    // away is not an answer to it; the answers are the three-tile sidestep and the roof.
    //
    // MinRange is the outer edge of the beams' own bands (bolt 640, strike 640, sweep
    // 620), because this is the attack for a player the rest of the kit cannot deal with.
    // Inside it the beams have the shot and the column stands down: a column landing on
    // top of a bolt is two heavy hits in one window with one telegraph between them, and
    // it makes the close fight a coin flip rather than a read.
    //
    // But see the visibility clause in CheckPreConditions — this is NOT a plain distance
    // gate. Close-and-hidden is precisely the state the column exists for.
    //
    // Note this only bites because ZeusController's coarse WantAttack gate was widened to
    // match (EnemyEntity refuses to select ANY new action while WantAttack is false, so a
    // per-action range wider than that gate is dead code — which is exactly what the old
    // 900 was against an AlertRange of 620). The three beams keep their own bands, so
    // they are still the close-quarters half of the kit; this is the one that follows you.
    protected virtual float MaxRange => float.MaxValue;
    protected virtual float MinRange => 640f;

    // Half a column's width. Three tiles across: wide enough that "roughly out of
    // it" is not good enough, narrow enough that a walk clears it inside the tell.
    protected virtual float HalfWidth => 24f;

    // The column is a piece of sky, so it is anchored to the world above the
    // caster rather than to the caster's muzzle: it starts SkyAbove px over Zeus's
    // head and runs down far enough to pass the ground plane of a stage as tall as
    // the spire (150 tiles ≈ 2400px). Overshooting below the ground costs nothing —
    // there is nothing down there to hit.
    protected virtual float SkyAbove    => 260f;
    protected virtual float ColumnDrop  => 3400f;

    protected virtual float Damage    => 3.6f;
    // Straight down — it is a falling column, and being driven into the floor is
    // what the hit should read as. Kept well under the bolt's eviction threshold in
    // spirit: enough to cost the player their footing on the face, not enough to
    // fling them off the tower and silently end the encounter.
    protected virtual float Knockback => 260f;
    protected virtual float Hitstun   => 0.35f;

    // Aim scatter for a column fired blind. Far tighter than ZeusController's search
    // spread, and deliberately so. That spread exists to stop a fast, repeating beam
    // from being either free (always the same spot) or unfair (always exact); a column
    // the player watches for 2.2 seconds and walks out of needs neither protection.
    // What it needs is to actually ARRIVE near the player — scattered by the
    // controller's ±190px, a three-tile column would miss a hidden player almost every
    // time, and hiding would be free after all.
    protected virtual float BlindJitter => 40f;

    protected virtual Color ChargeColor => new(180, 160, 255);
    protected virtual Color BoltColor   => new(245, 240, 255);

    // Above everything else Zeus has. Its window is narrow and it is the cycle's
    // heavy beat, so once it opens nothing displaces it — and it may cut the tail
    // of a bolt's recovery short to start on time, which is only ever downtime.
    public override int ActivePriority  => 46;
    public override int PassivePriority => 42;

    // The one opening window nothing shares. Sits after the bolt has landed and
    // before the storm's flurry starts, so the cycle reads bolt → column → storm →
    // sweep → storm.
    protected virtual int WindowFrom => 150;
    protected virtual int WindowTo   => 190;

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (!ZeusBeam.InWindow(ctx.Frame, WindowFrom, WindowTo)) return false;
        if (ctx.Dist > MaxRange) return false;

        // Stand down only when the rest of the set can ACTUALLY take the shot — close
        // AND visible. Both halves matter, and a plain `ctx.Dist < MinRange` would be a
        // bug rather than a simplification: every beam gates on EnemyAim.HasLineOfSight,
        // so a player who is near but behind cover leaves them all shut, and a
        // distance-only MinRange would hand that player total safety by standing the one
        // unoccluded attack down as well. Near-and-hidden is the exact state this action
        // was written for.
        //
        // Note there is still no HasLineOfSight call of its own here. Visibility is read
        // to decide whether someone ELSE has the shot, never as a condition on taking it:
        // when the column does fire, it fires blind.
        if (ctx.Dist < MinRange && ctx.PlayerVisible) return false;
        return true;
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;

        // Frozen here, like the bolt's angle: the band the player sees on the first
        // frame of the windup is the band that falls. Only the x matters to the hitbox,
        // but the whole point is stashed — LockedAim is the one Vector2 the snapshot
        // round-trips (EntityData.Aim), and keeping the y costs nothing while giving
        // the telegraph's ground marker somewhere meaningful to sit.
        //
        // Aimed at the memory itself rather than at the controller's search point (see
        // BlindJitter). When the player IS visible the two are the same thing, which is
        // what Input.AimWorld already carries.
        v.LockedAim = ctx.PlayerVisible
            ? ctx.Input.AimWorld
            : ctx.LastSeenPos + new Vector2(
                (ZeusBeam.Hash01(ctx.Frame, ctx.Self.Id.Index * 3 + 1) - 0.5f) * 2f * BlindJitter, 0f);
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

    // Top of the column in world space, and its centre — pure functions of the
    // caster and the locked target so a snapshot restore rebuilds them exactly.
    private float ColumnTop(PhysicsBody body) => body.Position.Y - SkyAbove;
    private Vector2 ColumnCentre(PhysicsBody body, in EnemyActionVars v)
        => new(v.LockedAim.X, ColumnTop(body) + ColumnDrop * 0.5f);

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration) return;
        if (t >= v.WindupDuration + v.ActiveDuration) return;
        if (ctx.Hitboxes == null) return;

        var poly   = Polygon.CreateRectangle(HalfWidth * 2f, ColumnDrop);
        var centre = ColumnCentre(ctx.Self.Body, in v);

        // EntitiesOnly (no terrain damage — see the class comment), but WITH an origin,
        // and the origin is the one at the top of the column rather than at Zeus. That is
        // what makes the reachability trace run DOWNWARD along the column instead of
        // outward from the statue, which is the whole difference between "a wall beside
        // you is cover" (it isn't) and "a roof over you is cover" (it is).
        ctx.Hitboxes.Publish(new Hitbox(
            poly.GetBoundingBox(centre), v.HitId, Damage,
            new Vector2(0f, Knockback),
            Faction.Enemy, ctx.Self.Id, BoltColor,
            targets: HitTargets.All,
            shape: poly, shapePos: centre,
            hitstunSecondsOverride: Hitstun,
            origin: new Vector2(v.LockedAim.X, ColumnTop(ctx.Self.Body))));
    }

    // The tell. The band is drawn at FULL width for the whole windup — this attack
    // asks "are you standing in it", so anything that grows into its final shape
    // would be lying for the first half of the answer. What escalates instead is
    // brightness, the strobe, and a bolt-head that falls down the column as a clock.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        float top  = ColumnTop(body);
        var   mid  = ColumnCentre(body, in v);
        float x    = v.LockedAim.X;

        if (time < v.WindupDuration && v.WindupDuration > 0f)
        {
            float p   = time / v.WindupDuration;
            var   col = Color.Lerp(ChargeColor, BoltColor, p);

            // The band itself, and its two edges — the edges are what the player
            // actually reads to decide which way to walk.
            t.Rect(mid, new Vector2(HalfWidth * 2f, ColumnDrop), col * (0.10f + 0.20f * p));
            t.Rect(new Vector2(x - HalfWidth, mid.Y), new Vector2(2f, ColumnDrop), col * (0.45f + 0.45f * p));
            t.Rect(new Vector2(x + HalfWidth, mid.Y), new Vector2(2f, ColumnDrop), col * (0.45f + 0.45f * p));

            // A charge falling down the column, once per second, so the windup has a
            // readable clock instead of being a static stripe that suddenly fires.
            float phase = (p * 2.2f) % 1f;
            var   head  = new Vector2(x, top + phase * ColumnDrop * 0.35f);
            t.Disc(head, 5f + 7f * (1f - phase), col * (0.35f + 0.65f * (1f - phase)));

            // Ground marker: a ring on the aim point that tightens as the clock runs
            // out, plus a strobe over the last fifth.
            t.Ring(new Vector2(x, v.LockedAim.Y), 34f - 22f * p, col * (0.4f + 0.6f * p), 16, 2f);
            if (p > 0.80f && (int)(time * 30f) % 2 == 0)
                t.Rect(mid, new Vector2(HalfWidth * 2f, ColumnDrop), Color.White * 0.22f);

            // And the statue winding up, so the source is never ambiguous.
            t.Ring(body.Position, 8f + p * 20f, col * (0.3f + 0.5f * p), 14, 2f);
            return;
        }

        if (time >= v.WindupDuration + v.ActiveDuration) return;   // recovery draws nothing

        float ap = (time - v.WindupDuration) / MathF.Max(v.ActiveDuration, 1e-4f);
        t.Rect(mid, new Vector2(HalfWidth * 2f, ColumnDrop), BoltColor * (0.9f - 0.3f * ap));
        t.Rect(mid, new Vector2(10f, ColumnDrop),            Color.White * 0.95f);
        t.Disc(new Vector2(x, v.LockedAim.Y), 26f * (1f - ap), Color.White * 0.7f);
    }
}
