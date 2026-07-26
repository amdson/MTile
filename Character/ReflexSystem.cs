using System;
using Microsoft.Xna.Framework;

namespace MTile;

// What the active movement state allows the ambient reflex layer to do this frame.
// Published fresh every frame on the MovementModifiers pattern — a per-frame channel,
// not a lifecycle. Default = both assists on (plain run/jump/fall get reflexes);
// states that manage their own ramps (CoveredJump, Dropdown) or servo the body
// against fixed contacts (Mantle, the ledge family) publish Off so the ambient
// layer never fights an owned maneuver or duplicates a corner.
public struct RampPolicy
{
    public bool Over;    // corner-vault assist (floor rises)
    public bool Under;   // head-tuck assist (ceiling lips)

    public static RampPolicy Default => new() { Over = true, Under = true };
    public static RampPolicy Off     => default;
}

// Ambient reflex layer (Plans/CORRIDOR_MANEUVER_PLAN.md piece 3): AUTOMATIC corner ramps.
// A per-frame reconciliation system, deliberately not an FSM — no modes, no transitions,
// no hysteresis. Every frame it diffs the body's ambient SteeringRamps against what the
// corridor probe + the active state's RampPolicy call for, and stamps/refreshes/removes.
//
// Activation conditions (the whole contract — everything else is the ramp's own geometry):
//   A ramp exists this frame iff ALL of:
//     1. A horizontal direction is HELD (Intent.CurrentHorizontal ≠ 0) and the scan runs
//        in that direction — assists are input-gated; releasing the stick removes them,
//        the same cancel semantics every maneuver has. Works grounded AND airborne
//        (jumping into a ledge corner finally gets the same graze assist as running).
//     2. The active state's RampPolicy enables that sense (see RampPolicy).
//     3. Over: the corridor ahead records a first floor RISE with Delta ≤ AmbientRampMaxRise
//        (the 1-block band — taller is an honest wall, and the probe truncates TallRise
//        before recording anyway). Under: it records a ceiling LIP whose bottom is below
//        the body's head line (the slab would clip; passability is already guaranteed —
//        a pinched column truncates the corridor before its lip is recorded).
//     4. The corner is within AmbientRampRange px of the body's leading face.
//   The STEEPNESS condition is not here: it is the ramp's own binary Engaged gate
//   (θ* < ~72°, recomputed from the live polygon every physics step). A body flush
//   against a step gets no assist — that case belongs to MantleState. This keeps the
//   stamping conditions generous and the authority decision purely geometric.
//
// Energy honesty: ambient ramps are PASSIVE (HasTarget = false) — they only rotate the
// velocity the body already has, magnitude preserved, up to MaxForce. Climbing trades
// speed for height like a real ramp: fast enough carries over the corner, too slow
// stalls and falls back flush — where the mantle's precondition takes the case. The
// ballistic crest cap (√(2g·remaining rise)) keeps a fast vault from popping ballistic.
//
// Snapshot/rollback: nothing here is snapshot state. SteeringRamps are soft contacts
// (never captured — see BodyState); a restore drops them from Body.Constraints and the
// next Reconcile re-stamps from the restored pose. The instance refs below are pure
// per-frame caches, every field rewritten before physics reads them.
public sealed class ReflexSystem
{
    private SteeringRamp _over;
    private SteeringRamp _under;

    public void Reconcile(EnvironmentContext ctx, RampPolicy policy)
    {
        var cfg = MovementConfig.Current;
        int dir = ctx.Intent.CurrentHorizontal;

        bool wantOver = false, wantUnder = false;
        Vector2 overCorner = default, underCorner = default;
        float overVyCap = 0f;

        if (cfg.AmbientRampsEnabled && dir != 0 && (policy.Over || policy.Under))
        {
            var corridor = ctx.GetCorridor(dir);
            var bounds   = ctx.Body.Bounds;
            float face   = bounds.Side(dir);

            if (policy.Over && corridor.TryFirstRise(out var rise)
                && rise.Delta <= cfg.AmbientRampMaxRise
                && dir * (rise.Pos.X - face) <= cfg.AmbientRampRange)
            {
                wantOver   = true;
                overCorner = rise.Pos;
                // Ballistic crest cap: never let the redirect carry more upward speed than
                // free-fall needs to coast from here to standing float height on the new
                // floor — the vault crests at ~zero vy and settles instead of popping.
                float needH = ctx.Body.Position.Y - corridor.LandingGateY(rise.Column);
                overVyCap = MathF.Min(cfg.AmbientRampMaxVy, SteeringRamp.BallisticVy(needH));
            }

            if (policy.Under && corridor.TryFirstCeilingLip(out var lip)
                && lip.Pos.Y > bounds.Top
                && dir * (lip.Pos.X - face) <= cfg.AmbientRampRange)
            {
                wantUnder   = true;
                underCorner = lip.Pos;
            }
        }

        _over  = Ensure(ctx, _over,  wantOver,  SteeringSense.Over,  dir, overCorner,  overVyCap, cfg);
        _under = Ensure(ctx, _under, wantUnder, SteeringSense.Under, dir, underCorner, cfg.AmbientRampMaxVy, cfg);
    }

    // Stamp/refresh/remove one ambient ramp slot. The Contains check (tiny list) makes this
    // self-healing after a rollback restore drops the soft contacts from Body.Constraints.
    private static SteeringRamp Ensure(EnvironmentContext ctx, SteeringRamp ramp, bool want,
        SteeringSense sense, int dir, Vector2 corner, float vyCap, MovementConfig cfg)
    {
        if (!want)
        {
            if (ramp != null) ctx.Body.Constraints.Remove(ramp);
            return null;
        }
        ramp ??= new SteeringRamp { Sense = sense };
        ramp.ForwardDir    = dir;
        ramp.Corner        = corner;
        ramp.MaxForce      = cfg.AmbientRampMaxForce;
        ramp.MaxRedirectVy = vyCap;
        ramp.MaxSpeed      = float.PositiveInfinity;
        ramp.HasTarget     = false;
        if (!ctx.Body.Constraints.Contains(ramp)) ctx.Body.Constraints.Add(ramp);
        return ramp;
    }
}
