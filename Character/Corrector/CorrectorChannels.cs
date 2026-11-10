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
    // Fold-stack tuning. LegReach = the standing ground probe's reach
    // (floatHeight + ProbeSlack below the feet = the same distance above the
    // C-space envelope), so "the legs can act" and "StandingState claims the
    // body" are the same condition — Falling never has live legs (decided
    // 2026-08-24; it was HoverDist + 20 = 42 px, and a falling body could
    // hang at leg reach for 25 px of height where Standing was not yet
    // active).
    // StandingProbeSlack carries BodyHeightTrim: the C-surface tracks the polygon bottom,
    // which the 2026-09-04 shrink raised by 4.8px — the trim keeps the legs' PHYSICAL reach
    // unchanged, and the standing probe now carries it too (it had been left out of it).
    internal const float LegReach    = PlayerCharacter.Radius + GroundChecker.StandingProbeSlack;   // 21.8 px (17 pre-shrink)
    private const float WeakTraction = 800f;             // scenario: deliberately underpowered legs-forward
    // Channel authority caps live in MovementConfig (Fold*Force — the hot-
    // reloadable tuning surface); the constants left here are structural.
    internal const float CatchFadeBand = 60f;   // px/s — catch authority ramps out across
                                               // MaxGroundEngageVnRel ± this window
    private const float RedirectEpsilon = 1e-6f;         // uniqueness regularizer, not a knob

    // The fold channel stack (the lattice tracker's). Masks come from the
    // resource layer and nothing else (Plans/MOVEMENT_RESOURCE_MODEL.md §3.2):
    // `granted` = the resources available (probes) AND used by the state.
    // Within Hands, each channel needs its own surface (a corner to plant on,
    // a face on its side to shove off) — the hand probes' geometry, held in
    // s.CornerPlant / s.ContactPushLeft / s.ContactPushRight. No channel reads
    // the path. Caps stay velocity-conditioned on the held state.
    // Returns the channel count; slots: 0 LegServo, 1 Drive, 2 Redirect,
    // 3 Tuck, then CornerPlant (if enabled) and ContactPush L/R (if enabled).
    // Drive (plan §9): two-way in x with ONE cap (`driveCap`: grip or slip,
    // times the WalkAccel modifier), built as two unilateral sides so each can
    // stop adding at walk speed: a side's cap is at most the room left this
    // tick between the body's speed relative to the floor in its direction
    // and `walk` — the legs cannot add past walk speed — while the other side
    // (braking) keeps the cap. Not conditioned on the path: the axis is x,
    // the mask is the Legs grant.
    public static int BuildFold(CorrectorScratch s, int n, int dir, float supportVy, MovementResource granted,
                                float driveCap, float vRelX, float walk)
    {
        var cfg = MovementConfig.Current;
        float LegForce = cfg.FoldLegForce, WalkForce = cfg.FoldDriveForce;
        float TuckForce = cfg.FoldTuckForce;
        float VPushMax = cfg.FoldLegPushFadeSpeed;
        // The rise ceiling every lifting channel obeys: the fastest rise a
        // DELIBERATE jump can author, support-relative (BACKLOG 5.8): the
        // frame's own rise raises the world-frame ceiling on a rising floor.
        float frameRise = MathF.Max(0f, -supportVy);
        float RiseMax = cfg.MaxAssistRiseSpeed + frameRise;
        bool legs  = granted.Has(MovementResource.Legs);
        bool hands = granted.Has(MovementResource.Hands);
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
                    s.ChannelMask[0][k] = legs;
                    s.ChannelMask[1][k] = true;
                }
                ch[0] = new ChannelDef { Id = CorrectionChannel.Drive,
                    Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true,
                    Axis = new Vector2(1f, 0f), Cap = WeakTraction, ActiveMask = s.ChannelMask[0] };
                ch[1] = new ChannelDef { Id = CorrectionChannel.Redirect,
                    Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon, Redirect = true,
                    ActiveMask = s.ChannelMask[1] };
                return 2;

            // Ground actuation only — no air authority at all: does pure leg
            // servo + traction hold hover and refuse everything aerial?
            case "legs-only":
                for (int k = 0; k < n; k++)
                {
                    s.ChannelMask[0][k] = legs;
                    s.ChannelMask[1][k] = legs;
                    float sepL = MathF.Max(0f, -s.Samples[k].Vel.Y);
                    s.ChannelCap[0][k] = LegForce * Math.Clamp(1f - sepL / VPushMax, 0f, 1f);
                }
                ch[0] = new ChannelDef { Id = CorrectionChannel.LegServo,
                    Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
                    Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], ActiveMask = s.ChannelMask[0] };
                ch[1] = new ChannelDef { Id = CorrectionChannel.Drive,
                    Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true,
                    Axis = new Vector2(1f, 0f), Cap = WalkForce, ActiveMask = s.ChannelMask[1] };
                return 2;
        }

        // "full": the whole stack.
        for (int k = 0; k < n; k++)
        {
            s.ChannelMask[0][k] = legs;                                   // LegServo
            s.ChannelMask[1][k] = legs;                                   // Drive
            s.ChannelMask[2][k] = cfg.FoldRedirectEnabled                 // Redirect: floor or corner
                                  && (legs || (hands && s.CornerPlant[k]));
            s.ChannelMask[3][k] = legs;                                   // Tuck
            s.ChannelMask[4][k] = legs;                                   // Drive, −x side

            // LegServo cap fades on BOTH velocity senses:
            //  - rising: authority tapers to zero at VPushMax;
            //  - descending: the landing-catch authority dies at
            //    MaxGroundEngageVnRel — a plunge past the gate hits the tiles
            //    RAW, which the impact-materials spec is tuned against.
            float sep = MathF.Max(0f, -(s.Samples[k].Vel.Y - supportVy));
            float catchScale = Math.Clamp(
                (cfg.MaxGroundEngageVnRel + CatchFadeBand - s.Samples[k].Vel.Y)
                    / CatchFadeBand, 0f, 1f);
            s.ChannelCap[0][k] = LegForce * Math.Clamp(1f - sep / VPushMax, 0f, 1f) * catchScale;
        }

        ch[0] = new ChannelDef {   // LegServo: strong, up-only
            Id = CorrectionChannel.LegServo,
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], RiseCap = RiseMax,
            ActiveMask = s.ChannelMask[0] };
        ch[1] = new ChannelDef {   // Drive, +x side
            Id = CorrectionChannel.Drive,
            Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(1f, 0f), Cap = DriveSideCap(driveCap, walk - vRelX), ActiveMask = s.ChannelMask[1] };
        ch[2] = new ChannelDef {   // Redirect: Thales disc, a bounded plant-and-deflect.
                                   // Serves hard rows and soft VERTICAL references;
                                   // never the soft x-progress rows. PlantServes: the
                                   // only channel wall-face (PlantOnly) rows may recruit.
            Id = CorrectionChannel.Redirect,
            Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon, Redirect = true,
            Cap = cfg.FoldRedirectForce,
            // The corner force obeys the launch's ceiling: at a corner the disc
            // converts forward speed into rise, and unbounded that outran the
            // legs' own ceiling.
            RiseCap = RiseMax,
            ActiveMask = s.ChannelMask[2], SkipSoftHorizontal = true, PlantServes = true };
        ch[3] = new ChannelDef {   // Tuck: down-only (legs pull down)
            Id = CorrectionChannel.Tuck,
            Lever = LeverKind.Force, Weight = 0.5f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, 1f), Cap = TuckForce, ActiveMask = s.ChannelMask[3] };
        ch[4] = new ChannelDef {   // Drive, −x side (the same cap)
            Id = CorrectionChannel.Drive,
            Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(-1f, 0f), Cap = DriveSideCap(driveCap, walk + vRelX), ActiveMask = s.ChannelMask[4] };
        return AddContactPush(s, n, dir, hands, AddCornerPlant(s, n, hands, 5));
    }

    // A Drive side's cap: the one cap, but never more than adds `room` px/s
    // (the speed left before walk speed in that direction) this tick.
    private static float DriveSideCap(float cap, float room)
        => MathF.Max(0f, MathF.Min(cap, room / Simulation.FixedDt));

    // Contact push: an airborne hand shove off an exposed wall face or lower
    // corner within hand reach (MarkContactPush). Two unilateral lateral
    // channels — each surface can only push the body AWAY from itself, so
    // between two (a 1-wide hole's corners) the solve can only center. The
    // anti-autopilot rule holds: the side that would push AGAINST a held
    // direction is masked, so a body pressed into a wall stays on it (the
    // wall rows otherwise shoved a held wall slide off its face, and it fell
    // at 121 px/s instead of the slide's 80). Neutral, both sides. Lateral only:
    // it never adds rise or brakes a fall. The solve decides when — the
    // wall rows and the path band — never a centering rule. Cap 0 → omitted.
    private const float ContactPushWeight = 0.05f;   // AirLateral's price
    private static int AddContactPush(CorrectorScratch s, int n, int dir, bool hands, int slot)
    {
        float cap = MovementConfig.Current.FoldContactPushForce;
        if (cap <= 0f) return slot;
        var ch = s.Problem.Channels;
        ch[slot] = new ChannelDef {
            Id = CorrectionChannel.ContactPushLeft,
            Lever = LeverKind.Force, Weight = ContactPushWeight, AxisOnly = true, Unilateral = true,
            SkipSoftHorizontal = true,
            Axis = new Vector2(-1f, 0f), Cap = cap, ActiveMask = s.ChannelMask[slot] };
        ch[slot + 1] = new ChannelDef {
            Id = CorrectionChannel.ContactPushRight,
            Lever = LeverKind.Force, Weight = ContactPushWeight, AxisOnly = true, Unilateral = true,
            SkipSoftHorizontal = true,
            Axis = new Vector2(1f, 0f), Cap = cap, ActiveMask = s.ChannelMask[slot + 1] };
        for (int k = 0; k < n; k++)
        {
            s.ChannelMask[slot][k]     = hands && s.ContactPushLeft[k]  && dir <= 0;
            s.ChannelMask[slot + 1][k] = hands && s.ContactPushRight[k] && dir >= 0;
        }
        return slot + 2;
    }

    // Contact-push surface scan: per sample, is an EXPOSED vertical tile face
    // (open on the body's side) within hand reach of the body center? A face
    // on the right lets the body push left, and vice versa. A ceiling tile's
    // side face ends at its lower corner, so a hole's lip counts from below.
    public static void MarkContactPush(ChunkMap chunks, CoastSample[] samples, int n,
                                       bool[] outLeft, bool[] outRight)
    {
        const float R = CornerPlantReach;
        const float ts = Chunk.TileSize, half = ts * 0.5f;
        for (int k = 0; k < n; k++)
        {
            outLeft[k] = outRight[k] = false;
            var pos = samples[k].Pos;
            int gx0 = (int)MathF.Floor((pos.X - R) / ts), gx1 = (int)MathF.Floor((pos.X + R) / ts);
            int gy0 = (int)MathF.Floor((pos.Y - R) / ts), gy1 = (int)MathF.Floor((pos.Y + R) / ts);
            for (int gy = gy0; gy <= gy1; gy++)
            for (int gx = gx0; gx <= gx1; gx++)
            {
                float cx = gx * ts + half, cy = gy * ts + half;
                if (!TileQuery.IsSolidAt(chunks, cx, cy)) continue;
                float y0 = gy * ts, y1 = y0 + ts;
                float dy = pos.Y < y0 ? y0 - pos.Y : pos.Y > y1 ? pos.Y - y1 : 0f;
                float faceR = gx * ts + ts, faceL = gx * ts;
                // Right face, body to its right → shove right.
                if (!outRight[k] && faceR < pos.X && !TileQuery.IsSolidAt(chunks, cx + ts, cy)
                    && (pos.X - faceR) * (pos.X - faceR) + dy * dy <= R * R)
                    outRight[k] = true;
                // Left face, body to its left → shove left.
                if (!outLeft[k] && faceL > pos.X && !TileQuery.IsSolidAt(chunks, cx - ts, cy)
                    && (faceL - pos.X) * (faceL - pos.X) + dy * dy <= R * R)
                    outLeft[k] = true;
            }
        }
    }

    // TEMP EXPERIMENT: an UNRESTRICTED 2D force channel at convex-corner plant
    // ticks (MarkCornerPlants) — no axis, no unilateral rule, no soft-row skip.
    // Deliberately breaks the anti-autopilot structure to see what a corner
    // plant could do with arbitrary authority. Cap 0 → channel omitted.
    private static int AddCornerPlant(CorrectorScratch s, int n, bool hands, int slot)
    {
        float cap = MovementConfig.Current.FoldCornerPlantForce;
        if (cap <= 0f) return slot;
        for (int k = 0; k < n; k++)
            s.ChannelMask[slot][k] = hands && s.CornerPlant[k];
        s.Problem.Channels[slot] = new ChannelDef {
            Id = CorrectionChannel.CornerPlant,
            Lever = LeverKind.Force, Weight = 0.05f,
            Cap = cap, RiseCap = MovementConfig.Current.MaxAssistRiseSpeed,
            ActiveMask = s.ChannelMask[slot] };
        return slot + 1;
    }

    // The maneuver channel set — the same physical semantics as the fold,
    // shaped for a committed arc: near the ground (approach/landing, and the
    // guided coast marks these with its spring-engagement flag) the legs act
    // (LegServo/Tuck/Redirect); in flight, air control only (two-sided lateral
    // — a committed maneuver may pull BACK toward its line, best-effort
    // mid-commitment is the maneuver contract — plus the tiny vertical nudge).
    // No Drive: the maneuver's authored guided feedforward owns x propulsion;
    // the solver corrects around it.
    public static int BuildManeuver(CorrectorScratch s, int n, int rowCount, int dir,
                                    float entrySpeed)
    {
        var cfg = MovementConfig.Current;
        var ch = s.Problem.Channels;
        // Speed-cap principle (movement_todo #5): a committed maneuver may
        // MAINTAIN its entry speed but never add beyond it — forward lateral
        // authority fades to zero as vx-along-dir approaches the cap
        // (velocity-conditioned per-tick caps frozen from the coast, the
        // LegServo idiom). Backward (damping toward the reference) stays
        // unrestricted — slowing an overshoot is always legal.
        float vxCap = MathF.Max(cfg.MaxWalkSpeed, entrySpeed);
        const float VxFadeBand = 25f;
        for (int k = 0; k < n; k++)
        {
            bool near = !float.IsPositiveInfinity(s.Samples[k].FloorY)
                        && s.Samples[k].FloorY - s.Samples[k].Pos.Y <= LegReach;
            s.ChannelMask[0][k] = near;    // LegServo
            // Redirect: near ground + dynamic, or corner-plant (BuildFold's note).
            s.ChannelMask[2][k] = (near || s.CornerPlant[k]) && !s.Samples[k].Grounded;
            s.ChannelMask[3][k] = near;    // Tuck
            s.ChannelMask[4][k] = !near;   // AirLateral forward (fade-capped)
            s.ChannelMask[5][k] = !near;   // AirLateral backward (damping)
            s.ChannelMask[6][k] = !near;   // AirVertical
            float sep = MathF.Max(0f, -s.Samples[k].Vel.Y);
            s.ChannelCap[0][k] = cfg.FoldLegForce * Math.Clamp(1f - sep / cfg.FoldLegPushFadeSpeed, 0f, 1f);
            float vAlong = dir * s.Samples[k].Vel.X;
            s.ChannelCap[4][k] = cfg.FoldAirLateralForce
                * Math.Clamp((vxCap - vAlong) / VxFadeBand, 0f, 1f);
        }
        // CornerAssist: near hard rows (the lip) — lift-only.
        for (int k = 0; k < n; k++) s.ChannelMask[1][k] = false;
        for (int j = 0; j < rowCount; j++)
        {
            if (s.Rows[j].HingeScale < 1f || s.Rows[j].Reference) continue;
            for (int k = Math.Max(0, s.Rows[j].Tick - 2); k <= Math.Min(n - 1, s.Rows[j].Tick + 2); k++)
                s.ChannelMask[1][k] = true;
        }
        // Feature-anchored redirect: same rule as BuildFold — any non-floor
        // hard row (wall face, lip underside) is a plantable anchor for
        // airborne ticks.
        for (int j = 0; j < rowCount; j++)
        {
            if (s.Rows[j].HingeScale < 1f || s.Rows[j].Reference || s.Rows[j].Normal.Y < -0.3f) continue;
            for (int k = Math.Max(0, s.Rows[j].Tick - 2); k <= Math.Min(n - 1, s.Rows[j].Tick + 2); k++)
                if (!s.Samples[k].Grounded) s.ChannelMask[2][k] = true;
        }

        ch[0] = new ChannelDef {   // LegServo
            Id = CorrectionChannel.LegServo,
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], ActiveMask = s.ChannelMask[0] };
        ch[1] = new ChannelDef {   // CornerAssist (lip lift)
            Id = CorrectionChannel.CornerAssist,
            Lever = LeverKind.Force, Weight = 0.5f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), Cap = cfg.FoldCornerForce, ActiveMask = s.ChannelMask[1] };
        ch[2] = new ChannelDef {   // Redirect (plant-and-deflect, near ground)
            Id = CorrectionChannel.Redirect,
            Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon, Redirect = true,
            // Forward-bounded (movement_todo #5): a deflection may keep the
            // speed the maneuver arrived with but never grow forward speed
            // past the cap — that's how the disc was minting 175 px/s vx from
            // hop energy. Fold redirects stay unbounded (redirect-traction is
            // the ambient layer's feature, pinned by its own scenarios).
            ForwardAxis = new Vector2(dir, 0f), ForwardCap = vxCap,
            ActiveMask = s.ChannelMask[2], PlantServes = true };
        ch[3] = new ChannelDef {   // Tuck (near ground)
            Id = CorrectionChannel.Tuck,
            Lever = LeverKind.Force, Weight = 0.5f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, 1f), Cap = cfg.FoldTuckForce, ActiveMask = s.ChannelMask[3] };
        ch[4] = new ChannelDef {   // AirLateral forward: fades out at the vx cap
            Id = CorrectionChannel.AirLateral,
            Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(dir, 0f), CapPerTick = s.ChannelCap[4], ActiveMask = s.ChannelMask[4] };
        ch[5] = new ChannelDef {   // AirLateral backward: unrestricted damping
            Id = CorrectionChannel.AirLateralBack,
            Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(-dir, 0f), Cap = cfg.FoldAirLateralForce, ActiveMask = s.ChannelMask[5] };
        ch[6] = new ChannelDef {   // AirVertical: tiny two-sided nudge in flight
            Id = CorrectionChannel.AirVertical,
            Lever = LeverKind.Force, Weight = 0.2f, AxisOnly = true,
            Axis = new Vector2(0f, 1f), Cap = cfg.FoldAirVerticalForce, ActiveMask = s.ChannelMask[6] };
        int count = AddCornerPlant(s, n, true, 7);
        for (int k = 0; k < n && count > 7; k++) s.ChannelMask[7][k] &= !s.Samples[k].Grounded;
        return count;
    }

    // Hand-plant reach for convex-corner redirect anchors (body center →
    // corner point). Half-height ~10.4 + a forearm; deliberately shorter
    // than LegReach — a plant is a touch, not a stride.
    public const float CornerPlantReach = 26f;

    // Convex-corner plant scan (cave-mouth entry, movement_todo #2): the
    // This scan marks airborne predicted ticks
    // within hand reach of a CONVEX tile corner — a solid tile whose
    // underside, one side, and that diagonal are all open (the lip's
    // lower-left/right corner; never a flat wall face, never a slab run) —
    // as plantable: BuildFold/BuildManeuver expose the Redirect disc there.
    //
    // risingLips (the lattice tracker, 2026-09-30): a RISING tick may plant
    // too, but only on a corner above the body's center — a ceiling lip. A
    // body that meets a corridor mouth too high meets it rising (that is why
    // it is high), and the lip is the one thing it can push down against;
    // the descending-only rule below is about corners underfoot (bump-hops,
    // ledges), which a lip above the head is not. The mirror holds for a
    // DESCENDING tick: only corners at or below the center.
    public static void MarkCornerPlants(ChunkMap chunks, CoastSample[] samples, int n,
                                        bool[] outMask, bool risingLips = false)
    {
        const float R = CornerPlantReach;
        float plunge = MovementConfig.Current.MaxGroundEngageVnRel;
        for (int k = 0; k < n; k++)
        {
            outMask[k] = false;
            // A hand-plant is a DESCENDING maneuver: rising ticks are climb
            // work (LegServo's domain — corridor bump-hops must not shed vx
            // into micro-plants), and plunging ticks can't plant at all
            // (impact honesty — the sand-break spec is tuned against raw
            // plunges; a plant would silently soften terrain damage).
            bool rising = samples[k].Vel.Y <= 0f;
            if (samples[k].Grounded || (rising && !risingLips) || samples[k].Vel.Y > plunge)
                continue;
            var pos = samples[k].Pos;
            const float ts = Chunk.TileSize, half = ts * 0.5f;
            int gx0 = (int)MathF.Floor((pos.X - R) / ts), gx1 = (int)MathF.Floor((pos.X + R) / ts);
            int gy0 = (int)MathF.Floor((pos.Y - R) / ts), gy1 = (int)MathF.Floor((pos.Y + R) / ts);
            for (int gy = gy0; gy <= gy1 && !outMask[k]; gy++)
            for (int gx = gx0; gx <= gx1 && !outMask[k]; gx++)
            {
                float cx = gx * ts + half, cy = gy * ts + half, below = cy + ts;
                if (!TileQuery.IsSolidAt(chunks, cx, cy)) continue;
                if (TileQuery.IsSolidAt(chunks, cx, below)) continue;   // underside must be open
                bool leftOpen = !TileQuery.IsSolidAt(chunks, cx - ts, cy)
                                && !TileQuery.IsSolidAt(chunks, cx - ts, below);
                bool rightOpen = !TileQuery.IsSolidAt(chunks, cx + ts, cy)
                                 && !TileQuery.IsSolidAt(chunks, cx + ts, below);
                float cornerY = gy * ts + ts;
                if (rising && cornerY >= pos.Y) continue;           // rising: lips above the center only
                // Descending: corners at or below the center only — the
                // descending plant is ONTO a corner (underfoot, or a lip met
                // beside the body). A corner above a falling body is one it
                // is moving away from: nothing there to push off, and the
                // disc's only use of it was to cancel the fall — a body
                // drifting under a stalactite's tip with a direction held
                // was held up (vy 85 -> 9 over 12 frames) for as long as the
                // tip stayed in hand reach.
                if (!rising && cornerY < pos.Y) continue;
                if (leftOpen && Vector2.DistanceSquared(pos, new Vector2(gx * ts, cornerY)) <= R * R)
                    outMask[k] = true;
                else if (rightOpen && Vector2.DistanceSquared(pos, new Vector2(gx * ts + ts, cornerY)) <= R * R)
                    outMask[k] = true;
            }
        }
    }

    // The grip channel set — hands on a gripped corner, the ledge family's
    // actuator as the legs are the fold's. Four unilateral channels:
    //   LIFT  up (the pull-up below the lip, the press above it);
    //   PULL  over the lip, along overDir;
    //   HOLD  tension toward the corner — the hands keep hold of it. Below
    //         the lip that is up and in; once the body is above the corner it
    //         is DOWN and in, which is what stops a pull that arrives at the
    //         crest still rising (nothing else can push down there, and the
    //         band would have no actuator to answer it);
    //   BRACE away from the wall — the feet pushing off its face, so only
    //         while the body is still below the lip. Without it nothing could
    //         undo drift INTO the wall (HOLD pulls that way from below): the
    //         body ground up the corner, and the band, with no actuator to
    //         answer it, choked the lift until the pull timed out.
    // Each is capped (LipLiftForce for the two arm-strength channels,
    // LipPushForce for the over-the-lip push) and FADES to zero as the body's
    // speed approaches `pace` along that channel's own axis OR along the path
    // (PaceFade, whichever binds first) — the LegServo / BuildManeuver idiom:
    // the arms cannot push faster than the pace, but never brake speed the
    // body arrived with. The axis term: at a crest the path runs flat, and a
    // path-speed fade alone left the lift at full authority on a body already
    // rising past the lip. The path term: the channels' diagonals (HOLD) add
    // to the axis pushes, and a climb's arc ran at ~1.5× the pace without it.
    // Active while the corner is within arm's reach. Returns the channel count.
    public const float GripReach     = 3f * PlayerCharacter.Radius;
    public const float SpeedFadeBand = 25f;   // px/s — pace-fade window (BuildManeuver's VxFadeBand)

    public static bool InGripReach(Vector2 pos, Vector2 corner)
        => Vector2.DistanceSquared(pos, corner) <= GripReach * GripReach;

    // HOLD's axis from a held position: toward the corner (zero at the corner).
    public static Vector2 GripHoldAxis(Vector2 pos, Vector2 corner)
    {
        var d = corner - pos;
        return d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.Zero;
    }

    public static int BuildGrip(CorrectorScratch s, int n, Vector2 corner, int overDir,
                                Vector2 pathDir, float pace)
    {
        var cfg = MovementConfig.Current;
        // Held state (every tick's sample is the body's current state), so
        // one axis serves the horizon, as the masks and caps do.
        var hold = GripHoldAxis(s.Samples[0].Pos, corner);
        for (int k = 0; k < n; k++)
        {
            bool reach = InGripReach(s.Samples[k].Pos, corner);
            var v = s.Samples[k].Vel;
            s.ChannelMask[0][k] = reach;
            s.ChannelMask[1][k] = reach;
            s.ChannelMask[2][k] = reach && hold != Vector2.Zero;
            s.ChannelMask[3][k] = reach && s.Samples[k].Pos.Y > corner.Y;
            s.ChannelCap[0][k] = cfg.LipLiftForce * PaceFade(pace, v, new Vector2(0f, -1f), pathDir);
            s.ChannelCap[1][k] = cfg.LipPushForce * PaceFade(pace, v, new Vector2(overDir, 0f), pathDir);
            s.ChannelCap[2][k] = cfg.LipLiftForce * PaceFade(pace, v, hold, pathDir);
            s.ChannelCap[3][k] = cfg.LipPushForce * PaceFade(pace, v, new Vector2(-overDir, 0f), pathDir);
        }
        var ch = s.Problem.Channels;
        ch[0] = new ChannelDef {
            Id = CorrectionChannel.GripLift,
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], ActiveMask = s.ChannelMask[0] };
        ch[1] = new ChannelDef {
            Id = CorrectionChannel.GripPull,
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(overDir, 0f), CapPerTick = s.ChannelCap[1], ActiveMask = s.ChannelMask[1] };
        ch[2] = new ChannelDef {
            Id = CorrectionChannel.GripHold,
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = hold, CapPerTick = s.ChannelCap[2], ActiveMask = s.ChannelMask[2] };
        ch[3] = new ChannelDef {
            Id = CorrectionChannel.GripBrace,
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(-overDir, 0f), CapPerTick = s.ChannelCap[3], ActiveMask = s.ChannelMask[3] };
        return 4;
    }

    // The slide channel set — feet on a platform the body is slipping off
    // (DropdownState): LEGS up and TUCK down while the floor is in leg reach
    // (the band's two vertical sides — lesson 1 of the unification plan), and
    // DRIVE along the slide, unilateral. The movers fade at `pace` along their
    // own axis AND along the path (pathDir), whichever binds first
    // (PaceFade) — so the feet never push the body faster than the authored
    // pace and never brake a faster entry. Unfaded, the tuck yanked the body
    // off the edge at twice the old exit speed; faded per axis only, the
    // diagonal slip-off reached the pace on both axes (√2 × pace). Off the
    // floor: nothing — the fold's "no actuators in free air".
    // Caps are the fold's (FoldLegForce with its push fade, FoldTuckForce,
    // FoldDriveForce). Returns the channel count.
    public static int BuildSlide(CorrectorScratch s, int n, int dir, Vector2 pathDir, float pace)
    {
        var cfg = MovementConfig.Current;
        for (int k = 0; k < n; k++)
        {
            bool near = IsNearFloor(s.Samples[k]);
            var v = s.Samples[k].Vel;
            s.ChannelMask[0][k] = near;
            s.ChannelMask[1][k] = near;
            s.ChannelMask[2][k] = near;
            s.ChannelCap[0][k] = cfg.FoldLegForce * Math.Clamp(1f - MathF.Max(0f, -v.Y) / cfg.FoldLegPushFadeSpeed, 0f, 1f);
            s.ChannelCap[1][k] = cfg.FoldTuckForce  * PaceFade(pace, v, new Vector2(0f, 1f), pathDir);
            s.ChannelCap[2][k] = cfg.FoldDriveForce * PaceFade(pace, v, new Vector2(dir, 0f), pathDir);
        }
        var ch = s.Problem.Channels;
        ch[0] = new ChannelDef {
            Id = CorrectionChannel.LegServo,
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], ActiveMask = s.ChannelMask[0] };
        ch[1] = new ChannelDef {
            Id = CorrectionChannel.Tuck,
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, 1f), CapPerTick = s.ChannelCap[1], ActiveMask = s.ChannelMask[1] };
        ch[2] = new ChannelDef {
            Id = CorrectionChannel.Drive,
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(dir, 0f), CapPerTick = s.ChannelCap[2], ActiveMask = s.ChannelMask[2] };
        return 3;
    }

    // The fold's "legs can act" test on a held sample: floor within LegReach.
    public static bool IsNearFloor(in CoastSample sample)
        => !float.IsPositiveInfinity(sample.FloorY) && sample.FloorY - sample.Pos.Y <= LegReach;

    private static float Fade(float pace, float speedAlong)
        => Math.Clamp((pace - speedAlong) / SpeedFadeBand, 0f, 1f);

    // A path actuator's pace fade: no channel pushes the body past the pace in
    // any direction it pushes. It fades (the tightest of):
    //   - on its own axis;
    //   - on each WORLD axis its push has a component along — a diagonal (the
    //     grip's HOLD) must not add x or y speed past the pace the axis-aligned
    //     channels respect (it ran a fast vault to 200 px/s of x past a 150 entry);
    //   - on the PATH, if it pushes along it (axis · pathDir > 0) — per-axis alone
    //     lets a diagonal path run at √2 × pace.
    // A channel pointing across or against the path only answers the band, which
    // adds no path speed: fading it on path speed switched off the climb's HOLD
    // exactly when a vault, moving at pace over the top, needed it to come down
    // onto the step. The axis terms keep a flat crest's lift honest (BuildGrip).
    private static float PaceFade(float pace, Vector2 v, Vector2 axis, Vector2 pathDir)
    {
        float fade = Fade(pace, Vector2.Dot(v, axis));
        if (MathF.Abs(axis.X) > 1e-3f) fade = MathF.Min(fade, Fade(pace, MathF.Sign(axis.X) * v.X));
        if (MathF.Abs(axis.Y) > 1e-3f) fade = MathF.Min(fade, Fade(pace, MathF.Sign(axis.Y) * v.Y));
        if (Vector2.Dot(axis, pathDir) > 0f) fade = MathF.Min(fade, Fade(pace, Vector2.Dot(v, pathDir)));
        return fade;
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
