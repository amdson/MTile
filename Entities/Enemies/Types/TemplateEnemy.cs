using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  TEMPLATE ENEMY — a complete, minimal bot, written to be edited.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it:   set "Stage": "sandbox" in game_config.json, then
//           dotnet run --project MTile.Desktop
//           (flat ground, one Template, nothing else). Turn on
//           "DebugDrawHitboxes": true to see the damage volume it publishes.
//
// It walks to a preferred distance from the player, holds there, and swings a
// telegraphed melee attack. That is the smallest thing that is genuinely an
// enemy rather than a demo, and it exercises every hook you'd use for a real
// one. Everything is virtual or `init`, so you can subclass instead of editing
// if you'd rather keep the original around.
//
// ── The mental model ────────────────────────────────────────────────────────
//
// An enemy is three separable pieces. Keeping them separate is the whole design;
// almost every confusing bug comes from putting logic in the wrong one.
//
//   Controller ("the brain")   — decides WHAT it wants: which way to go, where
//                                it's pointing, whether attacking is allowed.
//                                Reads the world. Produces an EnemyInput.
//   Movement state ("legs")    — executes the brain's intent on the body.
//                                Reads ctx.Input, not the world.
//   Action state ("arms")      — windup → damage window → recovery, plus the
//                                telegraph that makes it fair.
//
// Movement and actions are two INDEPENDENT state machines running at once, each
// picking one state per frame. They are almost completely isolated: the only
// channel between them is `vars.Committed`, which an action raises and movement
// states read as "an attack is in flight, stop moving."
//
// ── The four gates every state implements ───────────────────────────────────
//
//   CheckPreConditions   "may I START?"     — scanned every frame while inactive
//   CheckConditions      "may I CONTINUE?"  — checked every frame while active
//   PassivePriority      how hard it pushes to start
//   ActivePriority       how hard it resists being replaced
//
// Selection: among candidates whose precondition passes, the highest Passive
// wins — but only if it beats the CURRENT state's Active. So Active > Passive
// (the usual spacing here is +5) means "once I'm running I'm harder to displace
// than I was to start," which is what stops two states trading places every
// frame. Getting that comparison backwards is the classic bug in this codebase;
// see Character/MovementPriorities.cs for the player-side equivalent.
//
// Bands in use on the enemy side, so you can slot a new state sensibly:
//
//   movement    Idle 5/0 · Chase 20/15 · Jump 28/25 · Fly 30/26 · Cling 32/26
//               · Hop 34/30 · Leap 36/30 · AttackHold 40/35 · Stagger 50/45
//   actions     Ranged 28/22 · Melee 30/25 · Lash 32/27 · Spin 32/27
//               · Slam/RailShot/PounceSlam 34/30
//
// ── The rules that will bite you ────────────────────────────────────────────
//
//  1. STATE OBJECTS ARE SHARED. One instance can serve many entities, so a
//     field on the state is shared mutable state and will corrupt across
//     enemies AND break rollback. All per-activation data goes in the `ref vars`
//     struct. The knobs below are fine because they never change after
//     construction.
//
//  2. ONLY vars.TimeInState SURVIVES A ROLLBACK on the movement side. The
//     action side additionally round-trips LockedFacing, LockedAim, HitId,
//     Committed and the three durations. If a movement state needs to remember
//     anything beyond a clock, it currently can't — see BACKLOG 5.15.
//
//  3. Draw GETS ALMOST NOTHING: (SpriteBatch, Texture2D, PhysicsBody, in vars).
//     No context, no facing, no terrain. Anything the telegraph must show has
//     to already be in vars (that's what LockedFacing / LockedAim are for) or
//     reachable off the body (Position, Velocity).
//
//  4. CONTROLLERS MUST BE STATELESS. Config-only. There is nowhere to snapshot
//     per-entity brain state, so no cooldown counters, no memory, no "did I just
//     attack" flags. Express pacing through state durations and gates instead —
//     TemplateAction's Recovery is this bot's cooldown.
//
//  5. NO RANDOMNESS, NO WALL CLOCK, NO MUTABLE STATICS anywhere in here. The
//     sim is replayed frame-for-frame during rollback; anything that isn't a
//     pure function of sim state will desync a netplay match.
//
//  6. `WantAttack` is a plain bool on a struct, so it defaults to FALSE. A brain
//     that forgets to set it produces an enemy that never attacks and looks
//     broken for no visible reason.
//
// ── Making your own from this ───────────────────────────────────────────────
//
//   1. Copy this file. Rename the three classes.
//   2. Add a variant to Entities/EntityKind.cs (snapshot identity — pick a name
//      and don't rename it once saves/replays exist).
//   3. Register the blueprint in EnemyFactory.RegisterBuiltIns (one line).
//   4. Spawn it from a stage in Stage.cs.
//   5. Write a test. Every real bug in the gauntlet trio was found by a headless
//      test, not by playing — see MTile.Tests/Sim/TemplateEnemyTests.cs for the
//      two you almost always want.
//
// Before writing a new state, check whether the stock pool already has it:
// EnemyMovementStates.cs (idle / chase / jump / leap / hop / cling / fly /
// attack-hold / stagger) and EnemyActions.cs + GauntletActions.cs (melee /
// lunge / shockwave / spin / slam / ranged / rail shot / pounce slam / lash /
// pillar / block trail). Composing existing ones in a blueprint needs no code
// at all — see EntityKind.Skirmisher.
// ─────────────────────────────────────────────────────────────────────────────


// ── 1. THE BRAIN ────────────────────────────────────────────────────────────
// Tactics live here, and ONLY here. The states below don't know what a "player"
// is; swap this for a flee brain or a patrol brain and the same legs and arms
// keep working. That's the point of the split.
public sealed class TemplateController : EnemyController
{
    // Stand-off distance it tries to hold, centre-to-centre.
    //
    // Match this to where the attack's damage actually LANDS, not to the
    // action's trigger Range — those are different numbers and conflating them
    // is how you get a bot that dutifully walks up, swings on cue, and never
    // touches anything. TemplateAction's hitbox spans 8..42px in front of the
    // body, so anything past ~48 centre-to-centre (42 of reach plus the player's
    // ~6px half-width) is a guaranteed whiff. See the ⚠ note on
    // TemplateAction.Range — getting this ordering wrong is worse than a whiff.
    public float PreferredRange { get; init; } = 30f;
    // Slack around PreferredRange. Without a deadband the bot oscillates
    // forward/back every frame at exactly the preferred distance. Widen it and
    // the hold band widens too — keep the FAR edge inside the attack's reach.
    public float Deadband       { get; init; } = 6f;
    // Beyond this it won't attack at all, regardless of what the action's own
    // Range says. Useful for "doesn't notice you until you're close."
    public float AlertRange     { get; init; } = 220f;

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var   to   = ctx.ToPlayer;                 // player position − mine
        float dist = to.Length();
        int   side = to.X >= 0f ? 1 : -1;          // +1 = player is to my right

        // Approach when too far, back off when too close, hold in the deadband.
        Vector2 move = Vector2.Zero;
        if      (dist > PreferredRange + Deadband) move.X =  side;
        else if (dist < PreferredRange - Deadband) move.X = -side;

        return new EnemyInput
        {
            MoveDir    = move,
            Jump       = false,                     // no jump state registered below
            // Drives facing (EnemyEntity derives it from this whenever no action
            // is committed) and is what actions read to aim. Kept separate from
            // MoveDir on purpose: a retreating bot still faces its target.
            AimWorld   = ctx.Player.Body.Position,
            WantAttack = dist <= AlertRange,        // see rule 6 above
        };
    }
}


// ── 2. THE LEGS ─────────────────────────────────────────────────────────────
// Deliberately dumb: it reads ctx.Input.MoveDir and writes velocity. Resist the
// urge to look at ctx.Player here — the moment a movement state makes tactical
// decisions, swapping the brain stops working and the two start fighting.
public class TemplateMoveState : EnemyMovementState
{
    protected virtual float Speed    => 65f;
    // Below this the brain's request counts as "no horizontal intent". Stops a
    // near-zero MoveDir from producing a permanent twitch.
    protected virtual float Deadzone => 0.1f;

    public override int ActivePriority  => 20;   // same band as the stock EnemyChaseState
    public override int PassivePriority => 15;

    // Both gates are the same here, which is the common case. They differ when
    // starting and continuing have different costs — e.g. EnemyHopState will
    // only START when it's not mid-attack, but CONTINUES regardless, because
    // bailing out of a hop halfway would leave the body in mid-air.
    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > Deadzone;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > Deadzone;

    // Enter/Exit are optional; override them for one-shot effects (an impulse, a
    // gravity change). If you change something on the entity in Enter you MUST
    // undo it in Exit — EnemyClingMoveState zeroing GravityScale is the example,
    // and forgetting the restore is how enemies end up floating.

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        // Tick the clock yourself. Nothing does it for you, and Draw and every
        // phase check downstream read it.
        v.TimeInState += ctx.Dt;

        // Assign rather than accumulate: this is a walk, not a force. Vertical
        // velocity is left alone so gravity still works.
        ctx.Self.Body.Velocity.X = MathF.Sign(ctx.Input.MoveDir.X) * Speed;
    }
}


// ── 3. THE ARMS ─────────────────────────────────────────────────────────────
// The canonical attack shape: Windup → Active → Recovery, one continuous clock.
// The telegraph is drawn from that same clock, which is what guarantees the tell
// and the damage window can never drift apart — there is no separate telegraph
// object to keep in sync.
//
// Reading the phases:
//   Windup    the tell. Long enough to react to, and the only thing that makes
//             a hard-hitting attack fair.
//   Active    the damage window. Short. A hitbox is published every frame of it,
//             but CombatSystem dedupes on HitId so a target is hit once.
//   Recovery  the punish window, and this bot's only cooldown. Nothing else
//             prevents it attacking again immediately.
public class TemplateAction : EnemyActionState
{
    protected virtual float   Windup        => 0.50f;
    protected virtual float   Active        => 0.12f;
    protected virtual float   Recovery      => 0.45f;

    // Trigger gate: "the target is close enough that I start swinging."
    //
    // ⚠ THE ONE INVARIANT THAT ISN'T OBVIOUS. Range must be strictly INSIDE the
    // attack's effective reach, or the enemy DEADLOCKS. Set Range wider than the
    // hitbox and the bot walks in until it trips the trigger, commits, whiffs —
    // and then, because EnemyEntity runs SelectAction BEFORE SelectMovement, it
    // re-commits on the very frame recovery ends, before the movement FSM ever
    // gets a turn. It pins at the trigger boundary and swings at thin air
    // forever. Measured, not hypothetical: at Range 56 against a 42px reach this
    // exact bot froze at 54.7px and landed nothing in 420 frames.
    //
    // The safe ordering, widest to narrowest:
    //
    //     effective reach   >   Range   >   controller hold band
    //     (8 + Reach + target    (42)       (PreferredRange ± Deadband
    //      half-width ≈ 48)                  = 30 ± 6 → 24..36)
    //
    // Keep Range disjoint from any other action on the same enemy too, or two
    // actions fight over the same situation and priority silently decides it.
    protected virtual float   Range         => 42f;
    protected virtual float   VerticalSlack => 28f;   // don't swing at someone overhead

    // Hitbox geometry, in px. The volume spans 8..(8+Reach) in front of the
    // body centre — the 8 is a small inset so a swing doesn't connect with
    // something standing behind the shoulder. Effective centre-to-centre reach
    // is therefore (8 + Reach) plus the target's half-width; see
    // TemplateController.PreferredRange, which has to agree with this.
    protected virtual float   Reach         => 34f;
    protected virtual float   HalfHeight    => 12f;

    // Damage is a PERCENT contribution against a player (it feeds the monotonic
    // DamagePercent that scales knockback — HP is only lost to crush impacts),
    // and raw HP against an entity. Both from this one field.
    protected virtual float   Damage        => 1.0f;

    // Knockback impulse; the target's Mass divides it (player Mass is 2.5, so
    // this lands as ~168 px/s sideways and ~64 up). Negative Y is up.
    protected virtual Vector2 Knockback     => new(420f, -160f);

    protected virtual Color   TelegraphColor => Color.Magenta;
    protected virtual Color   StrikeColor    => Color.HotPink;

    public override int ActivePriority  => 30;   // same band as the stock EnemyMeleeAction
    public override int PassivePriority => 25;

    // "May I start?" — the action FSM's resting state is "no action at all",
    // so unlike movement this can simply be false most of the time.
    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist < Range && MathF.Abs(ctx.ToPlayer.Y) < VerticalSlack;

    // "May I continue?" — run the full clock out. Note it does NOT re-check
    // Range: an attack that cancelled the moment the player stepped back would
    // have no punish window, and whiffing is supposed to cost something.
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        // Lock the direction NOW so the swing goes where the telegraph pointed.
        // Aiming live during the windup means the tell lies, and the attack
        // becomes impossible to sidestep.
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;

        // Dedupe key for this one swing. CombatSystem tracks (HitId, target), so
        // a multi-frame active window damages each target exactly once. Mint a
        // fresh one per activation — reusing an id makes the second swing whiff
        // silently against anyone the first one touched.
        v.HitId = ctx.Spawner.HitIds.Next();

        // The cross-FSM channel: movement states read this as "hold still".
        v.Committed = true;

        PopulateDurations(ref v);

        // For an attack that must strike along an arbitrary 2D axis (off a wall,
        // off a ceiling), set v.LockedAim to a unit vector here instead of
        // relying on LockedFacing — see EnemyLashAction in GauntletActions.cs.
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    // Copies the durations into vars so Draw and the phase maths never reach
    // back into this object. Also called on snapshot restore, which is why the
    // numbers must come from here and not be inlined in Enter.
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

        if (t < v.WindupDuration) return;                                   // winding up
        if (t >= v.WindupDuration + v.ActiveDuration) return;               // recovering

        // Active window. Note this runs AFTER the movement state's Update, so
        // writing Body.Velocity here beats whatever movement wrote — that's how
        // a lunge or a dash-attack is built (see EnemyLungeAction).
        float half   = Reach * 0.5f;
        var   centre = ctx.Self.Body.Position + new Vector2(v.LockedFacing * (8f + half), 0f);
        var   region = new BoundingBox(centre.X - half,  centre.Y - HalfHeight,
                                       centre.X + half,  centre.Y + HalfHeight);

        ctx.Hitboxes?.Publish(new Hitbox(
            region, v.HitId, Damage,
            new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
            Faction.Enemy, ctx.Self.Id, StrikeColor,
            // EntitiesOnly = doesn't chew terrain. Use HitTargets.All to make an
            // attack destructive; then Damage is also tile HP, and anything ≥ 2.0
            // one-shots Stone. RailBoltProjectile is the worked example.
            targets: HitTargets.EntitiesOnly,
            // Origin = terrain occlusion: this swing can't reach through a wall.
            origin: ctx.Self.Body.Position));
    }

    // The telegraph. This is pure rendering — deleting it changes nothing about
    // the sim, and it is the single highest-leverage thing you can spend effort
    // on. A hard attack with a clear tell reads as fair; the same attack without
    // one reads as broken.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        float t = v.TimeInState;

        if (v.WindupDuration > 0f && t < v.WindupDuration)
        {
            // Windup: a dot that grows and slides out toward the strike point,
            // so both "something is coming" and "when" are readable.
            float p   = t / v.WindupDuration;                  // 0 → 1
            int   sz  = 2 + (int)(p * 5f);
            float off = 8f + p * (Reach + 4f);
            var   pos = body.Position + new Vector2(v.LockedFacing * off, 0f);
            sb.Draw(pixel, new Rectangle((int)pos.X - sz / 2, (int)pos.Y - sz / 2, sz, sz),
                    Color.Lerp(new Color(TelegraphColor, 90), TelegraphColor, p));
        }
        else if (t < v.WindupDuration + v.ActiveDuration)
        {
            // Strike: a slab exactly where the hitbox is.
            float half = Reach * 0.5f;
            var   c    = body.Position + new Vector2(v.LockedFacing * (8f + half), 0f);
            sb.Draw(pixel, new Rectangle((int)(c.X - half), (int)(c.Y - HalfHeight),
                                         (int)Reach, (int)(HalfHeight * 2f)),
                    StrikeColor * 0.55f);
        }
        // Recovery draws nothing — the body sitting still already reads as
        // "open". Add something here if you want the punish window signposted.
    }
}


// ── 4. THE WIRING ───────────────────────────────────────────────────────────
// Body, looks, brain, and the two state lists. No subclass of EnemyEntity is
// needed — BlueprintEnemy reads all of this.
public static class TemplateEnemy
{
    public static EnemyBlueprint Blueprint => new()
    {
        // Snapshot identity. Must be unique per blueprint and must not be
        // renamed once saves or replays exist.
        Kind          = EntityKind.Template,

        // ── body ──
        Radius        = 11f,
        Sides         = 6,
        Health        = 3f,
        // Divides incoming knockback. ~1 is shovable; 40 is effectively bolted
        // down (see EntityKind.Bastion).
        Mass          = 1.2f,
        GravityScale  = 1f,     // 0 = floats
        FrictionScale = 0.12f,

        // ── looks ──
        Color         = new Color(190, 60, 190),
        // Takes the radius, returns a Sprite. Drawing/Sprites.cs has the
        // existing ones and shows how a new pose is assembled.
        Sprite        = Sprites.Brute,

        Controller    = new TemplateController(),

        // ── the two registries ──
        // NEW LIST PER SPAWN (that's why these are factories, not lists).
        //
        // Index 0 is the movement fallback: when the current state's
        // CheckConditions fails, the FSM drops here, so it must always be
        // selectable. EnemyIdleState is the conventional choice.
        //
        // Order is snapshot identity — a state's index is what gets saved. Add
        // to the END of these lists; reordering them invalidates existing saves
        // and replays.
        Movement = () => new()
        {
            new EnemyIdleState(),        // 0 — fallback, always valid
            new TemplateMoveState(),
            // Roots the body while an attack is committed, so the swing reads as
            // planted. Leave it OUT if your attack depends on the body's own
            // momentum or if a locomotion state owns gravity — at priority 40 it
            // preempts both. (Why the Pouncer and Latcher don't have it.)
            new EnemyAttackHoldState(),
            // Lets knockback actually play out instead of being overwritten by
            // the next frame's movement. Omit it for something that shouldn't
            // flinch.
            new EnemyStaggerState(),
        },
        Actions = () => new()
        {
            new TemplateAction(),
        },
    };
}
