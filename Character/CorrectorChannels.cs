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
//   Fold states  (Standing/Falling/Crouched)  → BuildFold.
//   Maneuvers    (the corrector climb family) → BuildManeuver.
//   Non-fold ambient assists keep a redirect-only shape (near-ground grazes)
//                via AmbientCorrector's channel setup.
//
// PHYSICAL SEMANTICS (uniform across contexts): actuation depends on what the
// body can push against.
//   Near the ground (floor within LegReach): LEGS — LegServo (strong push
//     up), Tuck (pull down onto the floor), Redirect (the Thales disc: a
//     plant-and-deflect, only meaningful with ground to push against),
//     Drive/CornerAssist.
//   In flight: AIR CONTROL only — lateral authority plus a TINY vertical
//     nudge. No redirect in flight: momentum cannot be deflected against
//     nothing.
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
    // direction on the solver's behalf. Drive and AirLateral are unilateral
    // along intent while a direction is held — so hard clearance rows can never
    // recruit a slow-down-to-fit, and held momentum is never braked.
    // CornerAssist is lift-only for the same reason. The redirect disc may
    // still shed speed — passivity is its physical semantics (a deflection off
    // planted feet) — and exists only near the ground.
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
            // Redirect: near the ground AND in dynamic motion — the disc is a
            // plant-and-deflect: it needs ground to push against (no redirect
            // in flight) but must not serve steady SUPPORTED ticks (rotating a
            // supported walk's speed into rise is the leg servo's job done
            // wrong — it eats vx to climb).
            s.ChannelMask[3][k] = near[k] && !s.Samples[k].Grounded;
            s.ChannelMask[4][k] = near[k];    // Tuck: legs pulling the body down
                                              // onto the floor (ducks, drop-below-
                                              // hover) — a ground verb too
            s.ChannelMask[5][k] = dir != 0 && !near[k];  // AirLateral
            s.ChannelMask[6][k] = !near[k];   // AirVertical

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
        ch[3] = new ChannelDef {   // Redirect: Thales disc, NEAR-GROUND, free.
                                   // Serves hard rows and soft VERTICAL references
                                   // (ducks, catches); never the soft x-progress
                                   // rows (a passive air-brake along intent).
            Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon, Redirect = true,
            ActiveMask = s.ChannelMask[3], SkipSoftHorizontal = true };
        ch[4] = new ChannelDef {   // Tuck: down-only, near ground (legs pull down)
            Lever = LeverKind.Force, Weight = 0.5f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, 1f), Cap = TuckForce, ActiveMask = s.ChannelMask[4] };
        ch[5] = new ChannelDef {   // AirLateral: flight steering along intent
            Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true, Unilateral = true,
            Axis = intent, Cap = cfg.FoldAirLateralForce, ActiveMask = s.ChannelMask[5] };
        ch[6] = new ChannelDef {   // AirVertical: tiny two-sided nudge in flight
            Lever = LeverKind.Force, Weight = 0.2f, AxisOnly = true,
            Axis = new Vector2(0f, 1f), Cap = cfg.FoldAirVerticalForce, ActiveMask = s.ChannelMask[6],
            SkipSoftHorizontal = true };
        return 7;
    }

    // The maneuver channel set — the same physical semantics as the fold,
    // shaped for a committed arc: near the ground (approach/landing, and the
    // guided coast marks these with its spring-engagement flag) the legs act
    // (LegServo/Tuck/Redirect); in flight, air control only (two-sided lateral
    // — a committed maneuver may pull BACK toward its line, best-effort
    // mid-commitment is the maneuver contract — plus the tiny vertical nudge).
    // No Drive: the maneuver's authored guided feedforward owns x propulsion;
    // the solver corrects around it.
    public static int BuildManeuver(CorrectorScratch s, int n, int rowCount, int dir)
    {
        var cfg = MovementConfig.Current;
        var ch = s.Problem.Channels;
        for (int k = 0; k < n; k++)
        {
            bool near = !float.IsPositiveInfinity(s.Samples[k].FloorY)
                        && s.Samples[k].FloorY - s.Samples[k].Pos.Y <= LegReach;
            s.ChannelMask[0][k] = near;    // LegServo
            // Redirect: near ground + dynamic (see BuildFold's mask note).
            s.ChannelMask[2][k] = near && !s.Samples[k].Grounded;
            s.ChannelMask[3][k] = near;    // Tuck
            s.ChannelMask[4][k] = !near;   // AirLateral
            s.ChannelMask[5][k] = !near;   // AirVertical
            float sep = MathF.Max(0f, -s.Samples[k].Vel.Y);
            s.ChannelCap[0][k] = cfg.FoldLegForce * Math.Clamp(1f - sep / cfg.FoldLegPushFadeSpeed, 0f, 1f);
        }
        // CornerAssist: near hard rows (the lip) — lift-only.
        for (int k = 0; k < n; k++) s.ChannelMask[1][k] = false;
        for (int j = 0; j < rowCount; j++)
        {
            if (s.Rows[j].HingeScale < 1f) continue;
            for (int k = Math.Max(0, s.Rows[j].Tick - 2); k <= Math.Min(n - 1, s.Rows[j].Tick + 2); k++)
                s.ChannelMask[1][k] = true;
        }

        ch[0] = new ChannelDef {   // LegServo
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], ActiveMask = s.ChannelMask[0] };
        ch[1] = new ChannelDef {   // CornerAssist (lip lift)
            Lever = LeverKind.Force, Weight = 0.5f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), Cap = cfg.FoldCornerForce, ActiveMask = s.ChannelMask[1] };
        ch[2] = new ChannelDef {   // Redirect (plant-and-deflect, near ground)
            Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon, Redirect = true,
            ActiveMask = s.ChannelMask[2] };
        ch[3] = new ChannelDef {   // Tuck (near ground)
            Lever = LeverKind.Force, Weight = 0.5f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, 1f), Cap = cfg.FoldTuckForce, ActiveMask = s.ChannelMask[3] };
        ch[4] = new ChannelDef {   // AirLateral: two-sided mid-commitment steering
            Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true,
            Axis = new Vector2(1f, 0f), Cap = cfg.FoldAirLateralForce, ActiveMask = s.ChannelMask[4] };
        ch[5] = new ChannelDef {   // AirVertical: tiny two-sided nudge in flight
            Lever = LeverKind.Force, Weight = 0.2f, AxisOnly = true,
            Axis = new Vector2(0f, 1f), Cap = cfg.FoldAirVerticalForce, ActiveMask = s.ChannelMask[5] };
        return 6;
    }

    // Cross-frame Δ-anchor leak shared by every corrector loop: continuity
    // without DC memory (a full-strength anchor lets a force channel sustain
    // thrust with no remaining demand).
    public const float AnchorLeak = 0.7f;

    // Per-tick total δv of the solved plan (velocity-update z is a δv; force z
    // contributes z·dt), summed across channels into s.TickDv.
    public static void ComputeTickDv(CorrectorScratch s, CorrectionProblem p, int n, float dt)
    {
        for (int k = 0; k < n; k++) s.TickDv[k] = Vector2.Zero;
        for (int c = 0; c < p.ChannelCount; c++)
        {
            bool vu = p.Channels[c].Lever == LeverKind.VelocityUpdate;
            for (int k = 0; k < n; k++)
                s.TickDv[k] += vu ? s.Z[c * n + k] : s.Z[c * n + k] * dt;
        }
    }
}
