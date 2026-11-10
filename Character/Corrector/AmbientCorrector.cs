using System;
using Microsoft.Xna.Framework;

namespace MTile;

// How the active movement state participates in the stand fold — the reference-
// shaping half of the per-state membership table (CORRECTOR_CONSOLIDATION_PLAN
// §3.5; the actuation half is CorrectorChannels). Published fresh every frame.
// Fold = false (the default) means the state owns its own forces — the ambient
// layer does nothing.
public struct FoldProfile
{
    public bool  Fold;          // support/locomotion delegated to the ambient solve
    public float HoverOffset;   // hover clearance above the C-obstacle top surface (px)
    public float MaxSpeed;      // progress-target speed (px/s, pre-modifier)
    // Lattice engine: the state's price per px CLIMBED on the planned path
    // (drops are free). The planner's goal trades it against progress, so
    // this is what decides what a state will and won't climb — a crouch's
    // is high enough that no ledge is ever worth it (LATTICE_SCENARIOS.md).
    public float RiseCost;
    // Lattice engine: hover cost on (the fold states ride a surface) or off
    // (jump states: an airborne seed must not be dragged to the floor).
    public bool  Hover;
    // Lattice engine: the intent points UP as well as along dir — u becomes
    // (dir, −1)^ — while a jump is held. The launch is then the tracker
    // spending the legs along a rising path; nothing fires an impulse.
    public bool  Rising;
    public static FoldProfile None => default;

    // Falling on the lattice engine: no hover — the plan is a level line at
    // the body's height (obstacle avoidance only) and the engine has no
    // actuators in free air (LatticeTracker masks the air channels; the legs
    // are out of reach: LegReach is the standing probe, so wherever they
    // could act StandingState owns the body instead). Air control stays the
    // state's baseline. MaxSpeed is the one along-path speed (the walk's).
    public static FoldProfile Fall => new()
    {
        Fold = true, Hover = false, Rising = false,
        HoverOffset  = MovementConfig.Current.FoldHoverOffset,
        MaxSpeed     = MovementConfig.Current.MaxWalkSpeed,
        RiseCost     = MovementConfig.Current.FoldRiseCost,
    };

    // Jump states on the lattice engine (plan §7.3): hover off, rise free,
    // u up-and-along-intent while the button is held; the along-path speed
    // is the intent's (walk along, the rise ceiling up — LatticeTracker).
    public static FoldProfile Jump => new()
    {
        Fold = true, Hover = false, Rising = true,
        HoverOffset = 0f,
        MaxSpeed = MovementConfig.Current.MaxWalkSpeed, RiseCost = 0f,
    };


    // Standing/Falling: hover ≈ the old spring equilibrium; 1-high ledges ramp
    // the reference (16 ≤ 20), 2-high walls never do (32 > 20).
    public static FoldProfile Stand => new()
    {
        Fold = true,
        HoverOffset  = MovementConfig.Current.FoldHoverOffset,
        MaxSpeed     = MovementConfig.Current.MaxWalkSpeed,
        RiseCost     = MovementConfig.Current.FoldRiseCost,
        Hover        = true,
    };

    // StairClimbState: Standing's profile with the climb priced FREE. On a regular flight
    // the rise IS the objective, so the planner routes straight up the corner line at the
    // hover offset instead of weighing each riser against progress (measured 2026-09-21 on
    // the 10-step 45° fixture, fold alone: RiseCost 16 rides 12 px above the corner line
    // with 190 px/s vertical spikes; RiseCost 0 rides 18 px, a steady 70 px/s glide).
    public static FoldProfile Stairs
    {
        get { var p = Stand; p.RiseCost = 0f; return p; }
    }

    // Crouched: same fold, lower reference — the crouch IS reference shaping,
    // not a new mechanism. Hover 0 = center resting on the C-obstacle surface
    // (≈ the old crouch spring equilibrium at float height 0); climb reach
    // covers surface roughness only (a crouch never mounts ledges); crawl speed.
    public static FoldProfile Crouch => new()
    {
        Fold = true,
        HoverOffset  = MovementConfig.Current.CrouchHoverOffset,
        MaxSpeed     = MovementConfig.Current.CrouchMaxWalkSpeed,
        RiseCost     = MovementConfig.Current.CrouchRiseCost,
        Hover        = true,
    };
}

// The ambient layer: what a movement state hands the solver at the end of its
// Update. A state that passes a fold profile (Fold = true) delegates support
// and locomotion to the lattice tracker (LatticeTracker: the lattice path DP
// gives the trajectory's shape, the tracker picks this tick's channel forces
// from its first step). A state that passes FoldProfile.None owns its own
// forces and gets nothing.
public static class AmbientCorrector
{
    // Highest (min-y) C-obstacle top surface at horizontal position x, among
    // solid standable tiles (open above) whose surface lies within the vertical
    // band [bandMinY, bandMaxY]. The bevel facets ramp this smoothly over lips.
    internal static float FloorEnvelope(ChunkMap chunks, CObstacleTemplate t, float x,
                                       float bandMinY, float bandMaxY, out bool found)
    {
        int ts = Chunk.TileSize;
        float half = ts * 0.5f;
        found = false;
        float env = float.MaxValue;
        int cMin = (int)MathF.Floor((x - t.XExtent) / ts), cMax = (int)MathF.Floor((x + t.XExtent) / ts);
        int rMin = (int)MathF.Floor((bandMinY - t.Reach) / ts);
        int rMax = (int)MathF.Floor((bandMaxY + t.Reach) / ts);
        for (int gtx = cMin; gtx <= cMax; gtx++)
        for (int gty = rMin; gty <= rMax; gty++)
        {
            float cx = gtx * ts + half, cy = gty * ts + half;
            if (!TileQuery.IsSolidAt(chunks, cx, cy)) continue;
            if (TileQuery.IsSolidAt(chunks, cx, cy - ts)) continue;   // not standable
            float ry = t.TopSurfaceRy(x - cx, out bool valid);
            if (!valid) continue;
            float yTop = cy + ry;
            if (yTop < bandMinY || yTop > bandMaxY) continue;
            if (yTop < env) { env = yTop; found = true; }
        }

        // Growing sprout volumes are floor too (BACKLOG 5.8): the support a body
        // is actually riding while a block grows under it. Each face volume is a
        // tile-sized box centred on the lerped position (the same shape
        // GroundChecker and the physics contacts see), so the same beveled
        // C-obstacle math applies — and the envelope rises smoothly with the
        // growth instead of snapping a full tile when the sprout completes.
        // Without this the fold's floor model and the probe's disagree for the
        // whole growth window, and the solve pulls DOWN against the lift.
        // Zero-cost when nothing is growing (the common case).
        var growing = chunks.Graph.Growing;
        for (int i = 0; i < growing.Count; i++)
        {
            var sp = growing[i];
            foreach (var face in TileSproutNode.FaceOrder)
            {
                if ((sp.Faces & face) == 0) continue;
                var c = sp.VolumeCenter(face);
                if (MathF.Abs(x - c.X) > t.XExtent + half) continue;
                if (TileQuery.IsSolidAt(chunks, c.X, c.Y - ts)) continue;   // not standable
                float ry = t.TopSurfaceRy(x - c.X, out bool valid);
                if (!valid) continue;
                float yTop = c.Y + ry;
                if (yTop < bandMinY || yTop > bandMaxY) continue;
                if (yTop < env) { env = yTop; found = true; }
            }
        }
        return env;
    }

    // Runs at the END of the movement state's Update (its own forces are final),
    // before the physics step reads AppliedForce. Every state calls this once
    // per Update via MovementState.ApplyAmbient; stateless by design (cross-frame
    // state lives in MovementVars, scratch in ctx.Corrector).
    // `uses` = the state's MovementState.Uses: the tracker grants the
    // channels of (available ∧ uses).
    public static void Apply(EnvironmentContext ctx, in FoldProfile fold, MovementResource uses, ref MovementVars vars)
    {
        var s = ctx.Corrector;
        if (s == null || !MovementConfig.Current.AmbientCorrectorEnabled || !fold.Fold)
        {
            vars.AmbientPrevDv = Vector2.Zero;
            vars.AmbientChannelPrev = default;
            return;
        }
        LatticeTracker.Apply(ctx, s, fold, uses, ctx.Intent.CurrentHorizontal, ref vars);
    }
}
