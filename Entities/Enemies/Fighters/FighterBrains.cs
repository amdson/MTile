using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Bundled fighter brains (Plans/FIGHTER_DESIGN_PLAN.md §5). Each is built once per
// compiled blueprint from its FighterSpec and then shared by every entity spawned from
// it, so the rules are the ones EnemyController states, enforced harder:
//
//   * Config only on the instance — `init` fields copied from the spec's BrainConfig
//     at construction. Nothing on `this` is ever written in Decide.
//   * All memory in ctx.Self.Scratch, which EnemyEntity snapshots as a flat value.
//   * No System.Random, no statics, no wall clock. Time is ctx.Dt accumulated in Scratch.
//
// Targeting goes through ctx.ToPlayer / ctx.Dist / ctx.PlayerVisible / ctx.LastSeenPos
// only — never ctx.Player — so the phase-4 target abstraction can re-point those
// without touching this file. An aim point is Self.Body.Position + ctx.ToPlayer.

// Shared helpers for the three brains below.
internal static class FighterBrainMath
{
    // The preferred action, if the target sits inside its trigger band; otherwise null
    // ("anything that passes"). RequestedAction is a restriction, not a preference —
    // asking for an out-of-band action would make the fighter attack nothing at all.
    public static int? Request(int preferred, float minRange, float maxRange, float dist)
        => preferred >= 0 && dist >= minRange && dist <= maxRange ? preferred : null;

    public static float Sign(float x) => x >= 0f ? 1f : -1f;
}

// ── Closer ──────────────────────────────────────────────────────────────────
// Melee brawler (Brick, Sprinter). Walks at the target until EngageRange, jumps when it
// is overhead and a jump was bought, and asks for PreferredAction whenever the target is
// in that action's band. Below RetreatBelowHealth it alternates short retreats with
// longer pushes — hit and run rather than trading to the death.
//
// Scratch: I0 = mode (0 hunt, 1 retreat), F0 = seconds in the current mode.
public sealed class FighterCloserBrain : EnemyController
{
    public float EngageRange        { get; init; }
    public float AlertRange         { get; init; }
    public float RetreatBelowHealth { get; init; }
    public int   PreferredAction    { get; init; } = -1;
    public float PreferredMin       { get; init; }
    public float PreferredMax       { get; init; }
    public bool  CanJump            { get; init; }

    public float RetreatSeconds { get; init; } = 0.8f;   // one backing-off beat
    public float PushSeconds    { get; init; } = 2.5f;   // min hunting time between beats
    public float JumpHeight     { get; init; } = -20f;   // target this far above ⇒ jump

    public FighterCloserBrain() {}

    public FighterCloserBrain(FighterSpec s)
    {
        EngageRange        = s.EngageRange;
        AlertRange         = s.AlertRange;
        RetreatBelowHealth = s.RetreatBelowHealth;
        PreferredAction    = s.PreferredAction;
        if (s.PreferredAction >= 0 && s.PreferredAction < s.Actions.Count)
        {
            PreferredMin = s.Actions[s.PreferredAction].MinRange;
            PreferredMax = s.Actions[s.PreferredAction].MaxRange;
        }
        CanJump = s.JumpImpulse > 0f;
    }

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        ref var m  = ref ctx.Self.Scratch;
        var self   = ctx.Self;
        var to     = ctx.ToPlayer;
        float dist = ctx.Dist;
        var input  = new EnemyInput { AimWorld = self.Body.Position + to };

        if (dist > AlertRange) { m.I0 = 0; m.F0 = 0f; return input; }   // asleep

        m.F0 += ctx.Dt;
        bool hurt = self.MaxHealth > 0f && self.Health < RetreatBelowHealth * self.MaxHealth;
        if (m.I0 == 0 && hurt && m.F0 >= PushSeconds) { m.I0 = 1; m.F0 = 0f; }
        else if (m.I0 == 1 && m.F0 >= RetreatSeconds) { m.I0 = 0; m.F0 = 0f; }

        if (m.I0 == 1)
        {
            // Back off, still facing the target (aim is independent of MoveDir).
            input.MoveDir.X = -FighterBrainMath.Sign(to.X);
            return input;
        }

        if (dist > EngageRange) input.MoveDir.X = FighterBrainMath.Sign(to.X);
        input.Jump            = CanJump && to.Y < JumpHeight;
        input.WantAttack      = true;
        input.RequestedAction = FighterBrainMath.Request(PreferredAction, PreferredMin, PreferredMax, dist);
        return input;
    }
}

// ── Kiter ───────────────────────────────────────────────────────────────────
// Ranged fighter (Gunner, Builder, Turret). Holds the target between StandoffRange and
// EngageRange: closes when farther, backs off when nearer. A rooted fighter runs the same
// brain — it has no locomotion state to consume the MoveDir, so it simply stands and aims.
//
// With target memory it aims at LastSeenPos while the target is hidden and keeps
// shooting there for MemorySeconds (that is what memory is bought for); without it,
// PlayerVisible is always true and this collapses to "aim at the target".
//
// Scratch: I0 = 1 while backing off, F0 = seconds in the current backing-off leg. The
// leg runs at least BackoffSeconds so the fighter does not jitter across StandoffRange.
public sealed class FighterKiterBrain : EnemyController
{
    public float EngageRange        { get; init; }
    public float StandoffRange      { get; init; }
    public float AlertRange         { get; init; }
    public float RetreatBelowHealth { get; init; }
    public int   PreferredAction    { get; init; } = -1;
    public float PreferredMin       { get; init; }
    public float PreferredMax       { get; init; }
    public bool  CanJump            { get; init; }

    public float BackoffSeconds { get; init; } = 0.4f;
    public float MemorySeconds  { get; init; } = 1.0f;
    public float JumpHeight     { get; init; } = -20f;

    public FighterKiterBrain() {}

    public FighterKiterBrain(FighterSpec s)
    {
        EngageRange        = s.EngageRange;
        StandoffRange      = s.StandoffRange;
        AlertRange         = s.AlertRange;
        RetreatBelowHealth = s.RetreatBelowHealth;
        PreferredAction    = s.PreferredAction;
        if (s.PreferredAction >= 0 && s.PreferredAction < s.Actions.Count)
        {
            PreferredMin = s.Actions[s.PreferredAction].MinRange;
            PreferredMax = s.Actions[s.PreferredAction].MaxRange;
        }
        CanJump = s.JumpImpulse > 0f;
    }

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        ref var m  = ref ctx.Self.Scratch;
        var self   = ctx.Self;
        var pos    = self.Body.Position;
        var to     = ctx.ToPlayer;
        float dist = ctx.Dist;

        // Where it believes the target is: live while visible, remembered otherwise.
        var believed = ctx.PlayerVisible ? pos + to : ctx.LastSeenPos;
        var input    = new EnemyInput { AimWorld = believed };
        if (dist > AlertRange) { m.I0 = 0; m.F0 = 0f; return input; }

        var toBelieved = believed - pos;
        float bDist    = toBelieved.Length();

        // A hurt kiter wants more room.
        bool  hurt     = self.MaxHealth > 0f && self.Health < RetreatBelowHealth * self.MaxHealth;
        float standoff = hurt ? StandoffRange * 1.5f : StandoffRange;

        if (m.I0 == 1)
        {
            m.F0 += ctx.Dt;
            if (m.F0 >= BackoffSeconds && bDist >= standoff) { m.I0 = 0; m.F0 = 0f; }
        }
        else if (bDist < standoff) { m.I0 = 1; m.F0 = 0f; }

        if (m.I0 == 1)               input.MoveDir.X = -FighterBrainMath.Sign(toBelieved.X);
        else if (bDist > EngageRange) input.MoveDir.X =  FighterBrainMath.Sign(toBelieved.X);

        input.Jump            = CanJump && m.I0 == 0 && toBelieved.Y < JumpHeight;
        input.WantAttack      = ctx.PlayerVisible || ctx.LastSeenAge < MemorySeconds;
        input.RequestedAction = FighterBrainMath.Request(PreferredAction, PreferredMin, PreferredMax, bDist);
        return input;
    }
}

// ── Hover-and-dive ──────────────────────────────────────────────────────────
// Flyer. Holds a station HoverHeight above the target; once it is within EngageRange
// horizontally it dives straight down and asks for PreferredAction (its slam), whose
// fall-speed gate the dive itself satisfies. The slam commits, which drops the fly
// state for its duration — the fighter falls onto the target and climbs back after.
//
// Scratch: I0 = phase (0 station, 1 dive), F0 = seconds in phase. A dive that has not
// produced a slam in DiveSeconds gives up and re-stations; after a dive the brain waits
// CooldownSeconds on station before the next one.
public sealed class FighterHoverDiveBrain : EnemyController
{
    public float EngageRange     { get; init; }
    public float HoverHeight     { get; init; }
    public float AlertRange      { get; init; }
    public int   PreferredAction { get; init; } = -1;

    public float DiveSeconds     { get; init; } = 1.2f;
    public float CooldownSeconds { get; init; } = 1.0f;
    public float StationSlack    { get; init; } = 6f;

    public FighterHoverDiveBrain() {}

    public FighterHoverDiveBrain(FighterSpec s)
    {
        EngageRange     = s.EngageRange;
        HoverHeight     = s.HoverHeight;
        AlertRange      = s.AlertRange;
        PreferredAction = s.PreferredAction;
    }

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        ref var m  = ref ctx.Self.Scratch;
        var pos    = ctx.Self.Body.Position;
        var to     = ctx.ToPlayer;
        var target = pos + to;
        var input  = new EnemyInput { AimWorld = target };
        if (ctx.Dist > AlertRange) { m.I0 = 0; m.F0 = 0f; return input; }   // hover in place

        m.F0 += ctx.Dt;
        bool above = to.Y > 20f;                       // Y-down: the target is below us
        bool over  = MathF.Abs(to.X) < EngageRange;

        if (m.I0 == 0 && above && over && m.F0 >= CooldownSeconds) { m.I0 = 1; m.F0 = 0f; }
        else if (m.I0 == 1 && (m.F0 >= DiveSeconds || !above)) { m.I0 = 0; m.F0 = 0f; }

        if (m.I0 == 1)
        {
            input.MoveDir         = new Vector2(0f, 1f);
            input.WantAttack      = true;
            input.RequestedAction = PreferredAction >= 0 ? PreferredAction : null;
            return input;
        }

        var station = target + new Vector2(0f, -HoverHeight);
        var d       = station - pos;
        if (d.LengthSquared() > StationSlack * StationSlack) input.MoveDir = Vector2.Normalize(d);
        // No attacks on station: the slam is the whole kit and it only makes sense
        // out of a dive.
        return input;
    }
}
