using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The per-context channel membership table (CORRECTOR_CONSOLIDATION_PLAN §3.5)
// — the actuation half of the corrector's capability model, in ONE place.
// Capability is expressed by restricting channels (masks + caps), never by
// casework in the solve: a context that can't do something simply has no
// channel that could do it. Driven by movement state + the coast only — never
// by action state (the movement/action firewall).
//
//   Fold states  (Standing/Falling/Crouched)  → BuildFold: the full stack.
//   Maneuvers    (the corrector climb family) → BuildManeuver: redirect disc
//                only — the entry hop injected all the maneuver's energy, the
//                solver may steer that momentum but never add speed.
//   Non-fold ambient assists use the same redirect-only shape via
//                AmbientCorrector's channel setup.
public static class CorrectorChannels
{
    // Fold-stack tuning. LegReach is measured from the pre-fold hover distance
    // (float height + half height − sag) so "floor within leg range" matches
    // the old spring's engagement envelope.
    private const float HoverDist    = 2f * PlayerCharacter.Radius - 2f;
    private const float LegReach     = HoverDist + 20f;  // px — floor within leg range
    private const float WeakTraction = 800f;             // scenario: deliberately underpowered legs-forward
    // Channel authority caps live in MovementConfig (Fold*Force — the hot-
    // reloadable tuning surface); the constants left here are structural.
    private const float CatchFadeBand = 60f;   // px/s — catch authority ramps out across
                                               // MaxGroundEngageVnRel ± this window
    private const float RedirectEpsilon = 1e-6f;         // uniqueness regularizer, not a knob

    // The restricted standing/fold channels, built from the coast — masks and
    // velocity-conditioned caps are frozen per solve (tick 0 uses the body's
    // actual state, so applied perturbations are always truly admissible).
    // Returns the channel count.
    //
    // Anti-autopilot structure: no force channel may push AGAINST the held
    // direction on the solver's behalf. Drive is unilateral along intent while a
    // direction is held (station-keeping at dir 0 is two-sided but friction-
    // weak) — so hard clearance rows can never recruit a slow-down-to-fit, and
    // held momentum is never braked. CornerAssist is lift-only for the same
    // reason. The redirect disc may still shed speed — passivity is its physical
    // semantics (a deflection, like sliding along a wall).
    public static int BuildFold(CorrectorScratch s, int n, int rowCount, int dir, float targetSpeed)
    {
        var cfg = MovementConfig.Current;
        float LegForce = cfg.FoldLegForce, WalkForce = cfg.FoldDriveForce;
        float CornerForce = cfg.FoldCornerForce, TuckForce = cfg.FoldTuckForce;
        float VPushMax = cfg.FoldLegPushFadeSpeed;
        Span<bool> near = stackalloc bool[BallisticPredictor.MaxHorizon];
        for (int k = 0; k < n; k++)
        {
            float dist = s.Samples[k].FloorY - s.Samples[k].Pos.Y;
            near[k] = !float.IsPositiveInfinity(s.Samples[k].FloorY) && dist <= LegReach;
        }
        var ch = s.Problem.Channels;

        // Dev/test capability-probing harness: limited channel sets, hot-swapped
        // via movement_config.json "CorrectorScenario" (see MovementConfig).
        switch (cfg.CorrectorScenario)
        {
            // Redirect disc (all ticks, free) + weak forward traction, nothing
            // else: can momentum-steering alone deliver the body onto a ledge?
            case "redirect-traction":
                for (int k = 0; k < n; k++)
                {
                    s.ChannelMask[0][k] = near[k];
                    s.ChannelMask[1][k] = true;
                }
                ch[0] = new ChannelDef {
                    Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true,
                    Axis = new Vector2(1f, 0f), Cap = WeakTraction, ActiveMask = s.ChannelMask[0] };
                ch[1] = new ChannelDef {
                    Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon, Redirect = true,
                    ActiveMask = s.ChannelMask[1] };
                return 2;

            // Ground actuation only — no air authority at all: does pure leg
            // servo + traction hold hover and refuse everything aerial?
            case "legs-only":
                for (int k = 0; k < n; k++)
                {
                    s.ChannelMask[0][k] = near[k];
                    s.ChannelMask[1][k] = near[k];
                    float sepL = MathF.Max(0f, -s.Samples[k].Vel.Y);
                    s.ChannelCap[0][k] = LegForce * Math.Clamp(1f - sepL / VPushMax, 0f, 1f);
                }
                ch[0] = new ChannelDef {
                    Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
                    Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], ActiveMask = s.ChannelMask[0] };
                ch[1] = new ChannelDef {
                    Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true,
                    Axis = new Vector2(1f, 0f), Cap = WalkForce, ActiveMask = s.ChannelMask[1] };
                return 2;
        }

        // "full": the whole stack.
        for (int k = 0; k < n; k++)
        {
            s.ChannelMask[0][k] = near[k];    // LegServo
            s.ChannelMask[1][k] = dir != 0 && near[k];   // Drive — no x channel at
                                              // station (friction is baseline; a
                                              // two-sided x channel would let hard
                                              // rows recruit a dodge-brake)
            // Redirect: ballistic ticks only. Keyed to the coast's SUPPORTED
            // classification (gravity-hold reach), NOT the wider leg reach —
            // low flight through a duck-under corridor is exactly where the
            // disc earns its keep.
            s.ChannelMask[3][k] = !s.Samples[k].Grounded;
            s.ChannelMask[4][k] = true;       // Tuck — grounded too: with the gravity
                                              // hold in the baseline, "release the
                                              // floor" (ducks, drop-below-hover) is
                                              // a down-channel job on ground ticks

            // LegServo cap fades on BOTH velocity senses:
            //  - rising: authority tapers to zero at VPushMax — the servo can
            //    push, but never past it;
            //  - descending: the landing-catch authority dies at
            //    MaxGroundEngageVnRel, mirroring the old FSD engagement gate.
            //    Legs cushion an ordinary landing entirely; a plunge past the
            //    gate hits the tiles RAW — which is what the whole
            //    impact-materials spec (bounce/break by drop height) is tuned
            //    against. Softening plunges would silently retune terrain
            //    damage as a side effect of locomotion.
            float sep = MathF.Max(0f, -s.Samples[k].Vel.Y);
            float catchScale = Math.Clamp(
                (cfg.MaxGroundEngageVnRel + CatchFadeBand - s.Samples[k].Vel.Y)
                    / CatchFadeBand, 0f, 1f);
            s.ChannelCap[0][k] = LegForce * Math.Clamp(1f - sep / VPushMax, 0f, 1f) * catchScale;
        }
        // CornerAssist: active near obstacle features — ticks within ±2 of any
        // HARD row (proxy for "close to the corner").
        for (int k = 0; k < n; k++) s.ChannelMask[2][k] = false;
        for (int j = 0; j < rowCount; j++)
        {
            if (s.Rows[j].HingeScale < 1f) continue;
            for (int k = Math.Max(0, s.Rows[j].Tick - 2); k <= Math.Min(n - 1, s.Rows[j].Tick + 2); k++)
                s.ChannelMask[2][k] = true;
        }

        var intent = dir == 0 ? new Vector2(1f, 0f) : new Vector2(dir, 0f);
        ch[0] = new ChannelDef {   // LegServo: strong, up-only, near ground
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], ActiveMask = s.ChannelMask[0] };
        ch[1] = new ChannelDef {   // Drive: along intent only, capped, near ground
            Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true, Unilateral = dir != 0,
            Axis = intent, Cap = WalkForce, ActiveMask = s.ChannelMask[1] };
        ch[2] = new ChannelDef {   // CornerAssist: weak LIFT near features (never x)
            Lever = LeverKind.Force, Weight = 0.5f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), Cap = CornerForce, ActiveMask = s.ChannelMask[2],
            SkipSoftHorizontal = true };
        ch[3] = new ChannelDef {   // Redirect: Thales disc, ballistic ticks, free.
                                   // Serves hard rows and soft VERTICAL references
                                   // (ducks, catches); never the soft x-progress
                                   // rows (a passive air-brake along intent).
            Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon, Redirect = true,
            ActiveMask = s.ChannelMask[3], SkipSoftHorizontal = true };
        ch[4] = new ChannelDef {   // Tuck: down-only fast-fall, all ticks
            Lever = LeverKind.Force, Weight = 0.5f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, 1f), Cap = TuckForce, ActiveMask = s.ChannelMask[4] };
        return 5;
    }

    // The maneuver channel set: the redirect disc over the whole horizon,
    // nothing else. Writes Channels[0] and returns the channel count (1).
    public static int BuildManeuver(CorrectionProblem p, int n)
    {
        p.Channels[0] = new ChannelDef
        {
            Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon,
            Redirect = true, ActiveFrom = 0, ActiveTo = n,
        };
        return 1;
    }
}
