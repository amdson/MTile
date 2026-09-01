using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Carried by growing terrain — the multi-block half of the surface-relative
// support work (BACKLOG 5.8). When sprouting mass displaces the body with a
// real horizontal component (a floor sprout AND a wall sprout, a diagonal
// wave), the standing regime is the wrong classification: its station
// friction and hover tracking are keyed to the floor frame and quietly eat
// the horizontal half of the push. This state classifies the ride and runs
// ONE control law for it.
//
// THE RIDE ANCHOR SERVO. Every growing volume near the body defines a smooth
// velocity field along its own motion direction v̂:
//
//     s(lead) = clamp( speed + AnchorKp·(AnchorStandoff − lead), 0, speed )
//
// where `lead` is how far the body sits ahead of the volume's face along v̂.
// At the face (lead ≤ standoff) the field is the volume's full speed; it
// ramps down linearly as the rider pulls ahead (never negative — the mass
// never sucks a rider back in; never above the volume's own speed — catch-up
// is the contact solver's job, which delivers exactly surface speed). The
// per-axis strongest contribution across volumes is the servo target — the
// union IS the crest motion when a stream's cells push in alternating axis
// bursts — and a single acceleration-capped velocity servo tracks it on both
// axes, with the player's steering as an offset. Equilibrium falls out
// naturally: the rider settles at the lead where the field equals the
// crest's true advance. Velocity-match and station-keeping in one term.
//
// Earlier control laws are kept on record because each failure was
// instructive: raw contact aggregation alone drops the ride between scoops
// (contacts blink); latching pushes for a coyote window overshoots a young
// wave (~2 tiles per scoop; whether the crest re-caught the rider was luck);
// leaving Y to the fold lets the rider surf down the wave's forward face (a
// hovering body is never touched by the vertical front under it); summing
// nearby movers manufactures speeds faster than any volume (same-direction
// cells are one front); and a cliff-shaped proximity fade feeding a soft
// X-servo plus a hard Y-clamp plus fold hover was five controllers whose
// composition read as jitter. The servo field replaces all of it; while
// carried, the ambient corrector runs clearance-only (FoldProfile.None) so
// there is exactly one opinion about the trajectory.
//
// The same query is the state's CONTINUITY evidence: any approaching mover
// within the wide probe keeps the classification alive, so a whole ride is
// one carried run (state flapping is poison for animation and for reasoning
// about gamestate). Sim-side and deterministic throughout — the Growing list
// is terrain state, the servo reads only body + vars.
//
// Priorities (MovementPriorities.TerrainCarried*): environmental band — beats
// the free/ground states, yields to stun, the climb assists, and every
// deliberate jump, so jumping out of the wave (inheriting its velocity via
// the jump's source frame) always wins.
public class TerrainCarriedState : MovementState
{
    // Entry: the horizontal component of the live contact push must be real —
    // this is what separates "swept by mass" from "standing on a rising
    // floor" (the plain elevator stays Standing's, tracked by the fold).
    private const float EnterHorizontalPush = 25f;   // px/s
    private const float StayHorizontalPush  = 8f;    // px/s
    // Evidence grace: bridges momentary query gaps (a promotion tick between
    // cascade slices) without letting the state outlive a finished stream.
    private const float EvidenceGraceSeconds = 0.25f;
    // How far past the body the mass query looks. ~2.5 tiles: wide enough
    // that the flow average samples a real neighborhood of the stream (several
    // upcoming cells, not just the one about to scoop the rider), which both
    // smooths the target and strengthens the centering bias's read on where
    // the mass actually is.
    private const float MassProbeReach = Chunk.TileSize * 2.0f;
    // The servo field's shape: desired standing-off distance from a volume's
    // face along its motion, and the ramp slope — each px of extra lead
    // shaves AnchorKp px/s off the target, so a 155 px/s diagonal volume's
    // field reaches zero ~20px ahead (inside the probe) instead of cliffing
    // over half a tile. The standoff keeps the rider visibly proud of the
    // crest rather than skimming the faces.
    private const float AnchorStandoff = 4.5f;   // px
    private const float AnchorKp       = 10f;    // (px/s) per px of lead
    // Flow-average normalization bias, in units of one full-strength
    // contributor's weight. The target is the distance-weighted MEAN of the
    // nearby fields divided by (Σw + bias): deep in a stream the bias is
    // negligible and the target is the mean flow — which IS the crest
    // velocity (the mean of a cascade's alternating lateral and vertical
    // cells is exactly the front's advance rate); at the fringe, where flow
    // is thinning on one side, Σw is small and the bias pulls the target
    // down — a mild centering force easing the rider back into the stream.
    private const float FlowCenterBias = 0.35f;

    public override int ActivePriority  => MovementPriorities.TerrainCarriedActive;
    public override int PassivePriority => MovementPriorities.TerrainCarriedPassive;

    // The aggregate push velocity from every hard contact whose surface is
    // advancing INTO the body — the ENTRY evidence (an actual push starts a
    // ride; mass merely growing nearby does not). Each pushing contact
    // contributes its FULL surface velocity (the mass moves as a body, and a
    // rider travels with it, not just with the normal component); contacts
    // sharing a surface velocity are counted once (one moving square touched
    // on two faces is one body of mass); genuinely distinct movers sum.
    // Static tiles and receding surfaces contribute nothing;
    // FloatingSurfaceDistance soft contacts are excluded — they describe
    // support queries, not pushes.
    internal static Vector2 AggregateCarry(PhysicsBody body)
    {
        Vector2 carry = Vector2.Zero;
        Span<int> seen = stackalloc int[8];
        int seenCount = 0;
        var cons = body.Constraints;
        for (int i = 0; i < cons.Count; i++)
        {
            if (cons[i] is FloatingSurfaceDistance || cons[i] is not SurfaceDistance sd) continue;
            float vn = Vector2.Dot(sd.SurfaceVelocity, sd.Normal);
            if (vn <= 1f) continue;
            bool dup = false;
            for (int j = 0; j < seenCount; j++)
                if (cons[seen[j]] is SurfaceDistance prev
                    && (prev.SurfaceVelocity - sd.SurfaceVelocity).LengthSquared() < 1f)
                { dup = true; break; }
            if (dup) continue;
            if (seenCount < seen.Length) seen[seenCount++] = i;
            carry += sd.SurfaceVelocity;
        }
        return carry;
    }

    // The ride's servo target (see the class comment), plus the evidence
    // output: `massNearby` is true when ANY approaching mover is inside the
    // probe at all — a rider half a tile ahead of the front has a near-zero
    // target (station-keeping brake) but is still very much riding a live
    // stream, so evidence and target must stay separate values.
    internal static Vector2 RideTarget(EnvironmentContext ctx, out bool massNearby)
    {
        massNearby = false;
        var body = ctx.Body;
        var b = body.Polygon.GetBoundingBox(body.Position);
        var probe = new BoundingBox(b.Left - MassProbeReach, b.Top - MassProbeReach,
                                    b.Right + MassProbeReach, b.Bottom + MassProbeReach);
        const float half = Chunk.TileSize * 0.5f;
        // Distance-weighted MASS FLOW average (see FlowCenterBias): each
        // volume's station-keeping field, weighted by radial proximity and
        // averaged. The mean of the front cells' motions is the crest's true
        // advance rate — the quantity every earlier pacing mechanism
        // approximated indirectly — and averaging is continuous in the cell
        // positions, so hand-offs between cells never step the target the
        // way an argmax selection could.
        Vector2 flow = Vector2.Zero;
        float wSum = 0f;
        Span<Vector2> seen = stackalloc Vector2[8];
        int seenCount = 0;
        var growing = ctx.Chunks.Graph.Growing;
        for (int i = 0; i < growing.Count; i++)
        {
            var sp = growing[i];
            foreach (var face in TileSproutNode.FaceOrder)
            {
                if ((sp.Faces & face) == 0) continue;
                var c = sp.VolumeCenter(face);
                if (c.X + half <= probe.Left || c.X - half >= probe.Right) continue;
                if (c.Y + half <= probe.Top  || c.Y - half >= probe.Bottom) continue;
                var vel = sp.VolumeVelocity(face);
                float speed = vel.Length();
                if (speed < 1f) continue;
                if (Vector2.Dot(vel, body.Position - c) <= 0f) continue;   // moving away
                bool dup = false;
                for (int j = 0; j < seenCount; j++)
                    if ((seen[j] - vel).LengthSquared() < 1f) { dup = true; break; }
                if (dup) continue;
                if (seenCount < seen.Length) seen[seenCount++] = vel;
                massNearby = true;

                var dirV = vel / speed;
                float lead = Vector2.Dot(body.Position - c, dirV)
                             - half - PlayerCharacter.Radius;
                float s = Math.Clamp(speed + AnchorKp * (AnchorStandoff - lead), 0f, speed);
                float dist = MathF.Max(0f, Vector2.Distance(body.Position, c)
                                           - half - PlayerCharacter.Radius);
                float w = Math.Clamp(1f - dist / MassProbeReach, 0.05f, 1f);
                flow += dirV * (s * w);
                wSum += w;
            }
        }
        return wSum > 0f ? flow / (wSum + FlowCenterBias) : Vector2.Zero;
    }

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => MathF.Abs(AggregateCarry(ctx.Body).X) > EnterHorizontalPush;

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (MathF.Abs(AggregateCarry(ctx.Body).X) > StayHorizontalPush) return true;
        RideTarget(ctx, out bool massNearby);
        return massNearby || vars.CarryHoldX > 0f;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState   = 0f;
        vars.CarryVelocity = Vector2.Zero;
        vars.CarryHoldX    = 0f;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.CarryVelocity = Vector2.Zero;
        vars.CarryHoldX    = 0f;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState += ctx.Dt;

        var target = RideTarget(ctx, out bool massNearby);
        vars.CarryVelocity = target;
        vars.CarryHoldX = massNearby || MathF.Abs(AggregateCarry(ctx.Body).X) > 1f
            ? EvidenceGraceSeconds : vars.CarryHoldX - ctx.Dt;

        // Gravity hold (feedforward, the same baseline Standing runs) so the
        // servo shapes RELATIVE motion instead of fighting gravity at dt
        // leverage. FoldBaseline is surface-relative, so it holds over the
        // rising volume itself.
        var force = new Vector2(0f, StandingState.FoldBaseline(ctx).Y);

        // The one servo: acceleration-capped velocity tracking on both axes.
        // X always runs — with a zero target it IS station friction, the
        // pacing brake that keeps the rider in the next cell's catch zone.
        // Y engages only while some volume actually projects a vertical
        // field; otherwise vertical dynamics belong to gravity, the hold,
        // and the contacts (servoing vy toward zero would cancel honest
        // falls and jumps).
        var cfg = MovementConfig.Current;
        var m = ctx.Modifiers;
        int dir = ctx.Intent.CurrentHorizontal;
        float cap = cfg.GroundFriction * m.GroundFriction;
        float targetX = target.X + dir * cfg.MaxAirSpeed * m.MaxAirSpeed;
        force.X += Math.Clamp((targetX - ctx.Body.Velocity.X) / ctx.Dt, -cap, cap);
        if (MathF.Abs(target.Y) > 1f)
            force.Y += Math.Clamp((target.Y - ctx.Body.Velocity.Y) / ctx.Dt, -cap, cap);

        ctx.Body.AppliedForce = force;

        // Clearance protection only — hard rows against terrain ahead, no
        // fold hover: the ride servo is the single opinion about where the
        // body goes while carried.
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default, FoldProfile.None);
    }
}
