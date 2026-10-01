using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Bundled fighter brains (Plans/FIGHTER_DESIGN_PLAN.md §5, §16). Each is built once per
// compiled blueprint from its FighterSpec and shared by every entity spawned from it,
// so the rules are the ones EnemyController states, enforced by the type:
//
//   * They extend FighterController, so the only view of the world is FighterSenses.
//     Own state and the coarse target read are free; the exact read (with the tell),
//     terrain probes and cell reads cost energy. An unpaid read is stale.
//   * Config only on the instance — `init` fields copied from the spec at construction.
//     Nothing on `this` is ever written in Decide.
//   * All memory in the BrainScratch handed in by ref (snapshotted). No System.Random,
//     no statics, no wall clock. Time is s.Dt accumulated in Scratch.
//
// These are the reference brains for the three archetype shapes. A competitive brain
// is expected to beat them — see Plans/FIGHTER_PACKAGE_GUIDE.md.

// Shared helpers.
internal static class FighterBrainMath
{
    // The preferred action, if the target sits inside its trigger band; otherwise null
    // ("anything that passes"). RequestedAction is a restriction, not a preference —
    // asking for an out-of-band action would make the fighter attack nothing at all.
    public static int? Request(int preferred, float minRange, float maxRange, float dist)
        => preferred >= 0 && dist >= minRange && dist <= maxRange ? preferred : null;

    public static float Sign(float x) => x >= 0f ? 1f : -1f;

    // The best view the brain can afford this frame: the exact read when it is paid
    // for (or recently was — within `staleOk` frames), else the free coarse one.
    // Spends energy only when the meter can cover the exact read and keep `reserve`
    // for attacks.
    public static EnemyTarget Look(FighterSenses s, float reserve, int staleOk, out bool exact)
    {
        var self = s.Self;
        EnemyTarget t;
        int age;
        if (self.Energy - s.TargetCost >= reserve) t = s.Target(out age);        // buy a fresh view
        else                                       t = s.TargetStale(out age);   // keep the meter for attacks
        if (t.Known && age <= staleOk) { exact = true; return t; }
        exact = false;
        return s.TargetCoarse();
    }

    // Is the target visibly winding up a melee-band strike that will reach us?
    public static bool IncomingStrike(in EnemyTarget t, float dist, float reachGuess)
        => t.Known && t.TellProgress >= 0.35f && t.TellProgress < 1f
           && (t.Tell is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash or ActionKind.Slam
                       or ActionKind.PounceSlam)
           && dist < reachGuess;
}

// ── Closer ──────────────────────────────────────────────────────────────────
// Melee brawler (Brick, Sprinter). Walks at the target until EngageRange, jumps when it
// is overhead and a jump was bought, and asks for PreferredAction whenever the target is
// in that action's band. Below RetreatBelowHealth it alternates short retreats with
// longer pushes. Reads the tell when it can afford to and steps back out of a strike it
// sees coming.
//
// Scratch: I0 = mode (0 hunt, 1 retreat, 2 dodge), F0 = seconds in the current mode.
public sealed class FighterCloserBrain : FighterController
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
    public float DodgeSeconds   { get; init; } = 0.3f;
    public float DodgeReach     { get; init; } = 70f;    // a strike this close is worth avoiding
    public float EnergyReserve  { get; init; } = 0f;     // keep this much for attacks

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
            EnergyReserve = s.Actions[s.PreferredAction].EnergyCost;
        }
        CanJump = s.JumpImpulse > 0f;
    }

    protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
    {
        var self   = s.Self;
        var t      = FighterBrainMath.Look(s, EnergyReserve, staleOk: 10, out bool exact);
        var to     = t.Position - self.Position;
        float dist = to.Length();
        var input  = new EnemyInput { AimWorld = t.Position };

        if (dist > AlertRange) { m.I0 = 0; m.F0 = 0f; return input; }   // asleep

        m.F0 += s.Dt;
        bool hurt = self.MaxHealth > 0f && self.Health < RetreatBelowHealth * self.MaxHealth;

        // A strike we can see coming: step out of it for a beat.
        if (m.I0 != 2 && exact && FighterBrainMath.IncomingStrike(in t, dist, DodgeReach) && !self.ActionCommitted)
        { m.I0 = 2; m.F0 = 0f; }

        if (m.I0 == 2)
        {
            if (m.F0 >= DodgeSeconds) { m.I0 = 0; m.F0 = 0f; }
            else
            {
                input.MoveDir.X = -FighterBrainMath.Sign(to.X);
                input.Jump      = CanJump;
                return input;
            }
        }
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
// While the target is hidden it keeps aiming at the last view for MemorySeconds (with
// bought memory that view keeps tracking coarsely; without, it is frozen where the
// target was last seen). It pays for an exact read only when the meter can cover the
// shot it is saving for.
//
// Scratch: I0 = 1 while backing off, F0 = seconds in the current backing-off leg. The
// leg runs at least BackoffSeconds so the fighter does not jitter across StandoffRange.
public sealed class FighterKiterBrain : FighterController
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
    public float EnergyReserve  { get; init; } = 0f;

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
            PreferredMin  = s.Actions[s.PreferredAction].MinRange;
            PreferredMax  = s.Actions[s.PreferredAction].MaxRange;
            EnergyReserve = s.Actions[s.PreferredAction].EnergyCost;
        }
        CanJump = s.JumpImpulse > 0f;
    }

    protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
    {
        var self = s.Self;
        var pos  = self.Position;
        var t    = FighterBrainMath.Look(s, EnergyReserve, staleOk: 10, out _);
        var toBelieved = t.Position - pos;
        float bDist    = toBelieved.Length();
        var input      = new EnemyInput { AimWorld = t.Position };
        if (bDist > AlertRange) { m.I0 = 0; m.F0 = 0f; return input; }

        // A hurt kiter wants more room.
        bool  hurt     = self.MaxHealth > 0f && self.Health < RetreatBelowHealth * self.MaxHealth;
        float standoff = hurt ? StandoffRange * 1.5f : StandoffRange;

        if (m.I0 == 1)
        {
            m.F0 += s.Dt;
            if (m.F0 >= BackoffSeconds && bDist >= standoff) { m.I0 = 0; m.F0 = 0f; }
        }
        else if (bDist < standoff) { m.I0 = 1; m.F0 = 0f; }

        if (m.I0 == 1)                input.MoveDir.X = -FighterBrainMath.Sign(toBelieved.X);
        else if (bDist > EngageRange) input.MoveDir.X =  FighterBrainMath.Sign(toBelieved.X);

        input.Jump            = CanJump && m.I0 == 0 && toBelieved.Y < JumpHeight;
        input.WantAttack      = s.Visible || s.HiddenSeconds < MemorySeconds;
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
// Under a roof the station would be inside the ceiling, so every ProbeEveryFrames the
// brain buys one terrain probe and caps the hover height to the headroom it reports.
//
// Scratch: I0 = phase (0 station, 1 dive), F0 = seconds in phase, I1 = frames until the
// next probe, F1 = headroom in tiles from the last probe (0 = unknown). A dive that has
// not produced a slam in DiveSeconds gives up and re-stations; after a dive the brain
// waits CooldownSeconds on station before the next one.
public sealed class FighterHoverDiveBrain : FighterController
{
    public float EngageRange     { get; init; }
    public float HoverHeight     { get; init; }
    public float AlertRange      { get; init; }
    public int   PreferredAction { get; init; } = -1;

    public float DiveSeconds     { get; init; } = 1.2f;
    public float CooldownSeconds { get; init; } = 1.0f;
    public float StationSlack    { get; init; } = 6f;
    // Flight drains the meter; keep this much so a read never grounds the fighter.
    public float EnergyReserve   { get; init; } = 0.3f;
    public int   ProbeEveryFrames { get; init; } = 30;

    public FighterHoverDiveBrain() {}

    public FighterHoverDiveBrain(FighterSpec s)
    {
        EngageRange     = s.EngageRange;
        HoverHeight     = s.HoverHeight;
        AlertRange      = s.AlertRange;
        PreferredAction = s.PreferredAction;
    }

    protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
    {
        var self   = s.Self;
        var pos    = self.Position;
        var t      = FighterBrainMath.Look(s, EnergyReserve, staleOk: 10, out _);
        var target = t.Position;
        var to     = target - pos;
        var input  = new EnemyInput { AimWorld = target };
        if (to.Length() > AlertRange) { m.I0 = 0; m.F0 = 0f; return input; }   // hover in place

        // Headroom check, rationed: a probe every ProbeEveryFrames when affordable.
        if (m.I1 <= 0)
        {
            if (self.Energy - s.ProbeCost >= EnergyReserve)
            {
                var p = s.Probe(to.X >= 0f ? 1 : -1);
                if (p.Known) m.F1 = p.Headroom;
            }
            m.I1 = ProbeEveryFrames;
        }
        else m.I1--;
        float hover = HoverHeight;
        if (m.F1 > 0f && m.F1 < 4f)
            hover = MathF.Min(HoverHeight, MathF.Max(m.F1 * Chunk.TileSize - self.Radius - 4f, self.Radius + 8f));

        m.F0 += s.Dt;
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

        var station = target + new Vector2(0f, -hover);
        var d       = station - pos;
        if (d.LengthSquared() > StationSlack * StationSlack) input.MoveDir = Vector2.Normalize(d);
        // No attacks on station: the slam is the whole kit and it only makes sense
        // out of a dive.
        return input;
    }
}
