using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Carried by growing terrain — the multi-block half of the surface-relative
// support work (BACKLOG 5.8). When sprouting mass pushes the body from more
// than just below (a floor sprout AND a wall sprout, a diagonal wave), the
// standing regime is the wrong classification: its station friction and hover
// tracking are keyed to the floor frame and quietly eat the horizontal half of
// the push, so the player rises but never travels. This state recognizes the
// aggregate push and rides it: gravity is held exactly as Standing's baseline
// would, nothing brakes the carry, and an ensure-at-least along the aggregate
// direction makes the body genuinely travel with the mass.
//
// The aggregate is CONTACT-SCOPED, never a single invented frame: each hard
// collision contact (SurfaceDistance) contributes its surface's push along its
// own normal — the same per-contact model the physics solver resolves with —
// and the vector sum is simply where the mass is taking the body this frame.
// A purely vertical push never enters here (|carry.X| gate): the smooth
// one-sprout elevator stays Standing's, tracked by the fold.
//
// Priorities (MovementPriorities.TerrainCarried*): environmental band — beats
// the free/ground states, yields to stun, the climb assists, and every
// deliberate jump, so jumping out of the wave (inheriting its velocity via the
// jump's source frame) always wins.
public class TerrainCarriedState : MovementState
{
    // Entry: the horizontal component of the aggregate push must be real —
    // this is what separates "swept by mass" from "standing on a rising
    // floor". Continuation runs a lower bar so a fading push hands off
    // smoothly instead of flickering.
    private const float EnterHorizontalPush = 25f;   // px/s
    private const float StayHorizontalPush  = 8f;    // px/s
    // Coyote window on the ride. A growing stream pushes in bursts — each new
    // cell's volume catches the rider for a few frames, then completes, and
    // the NEXT cell arrives a slice later. Dropping the ride the frame
    // contacts blink let the rider bleed speed between slices and fall behind
    // the crest for good (measured: ~3 tiles of a 20-tile stream). While the
    // hold is live the last aggregate keeps driving the ensure-at-least, so
    // the rider stays with the crest and each new slice re-trues the carry.
    // When the stream genuinely ends, the hold expires and the rider exits
    // with the crest's momentum — an honest launch, not a lingering force.
    private const float CarryCoyoteSeconds = 0.25f;
    // How far past the body the nearby-mass query looks, and the slowest mass
    // motion it reports. ~1.5 tiles: the cells that will scoop the rider next
    // are at most a cell away while a stream is alive.
    private const float MassProbeReach     = Chunk.TileSize * 1.5f;
    private const float MinNearbyMassSpeed = 20f;
    // Lead distance over which a sensed volume's TARGET contribution fades to
    // zero. Much tighter than the evidence reach: the station-keeping
    // equilibrium (rider speed == crest advance) should sit within a fraction
    // of a tile of the front — a longer fade leaves enough target speed at
    // long leads to push the rider off the front of the wave.
    private const float LeadFadeReach      = Chunk.TileSize * 0.6f;

    public override int ActivePriority  => MovementPriorities.TerrainCarriedActive;
    public override int PassivePriority => MovementPriorities.TerrainCarriedPassive;

    // The aggregate push velocity from every hard contact whose surface is
    // advancing INTO the body. Each pushing contact contributes its FULL
    // surface velocity — the mass it represents moves as a body, and a rider
    // should travel with it, not just with the component that happens to point
    // along the contact normal (a diagonal volume touched only on its top face
    // would otherwise read as pure vertical, which is exactly how players got
    // carried up and out of diagonal streams). Contacts sharing the same
    // surface velocity are counted ONCE: touching one moving square on two of
    // its faces is one body of mass, not two pushes. Genuinely distinct movers
    // (an up-sprout and a side-sprout) still sum. Static tiles and receding
    // surfaces contribute nothing; FloatingSurfaceDistance (state-owned soft
    // contacts) are excluded — they describe support queries, not pushes.
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

    // The motion of the growing mass NEAR the body, whether or not it is
    // touching: every growing volume within MassProbeReach that is moving
    // TOWARD the body contributes its velocity, deduped per distinct motion
    // (a combined multi-face volume reports identically for each face). This
    // is the state's continuity evidence — contacts blink (a cell pushes for
    // a few frames, completes, reads static), but during a live stream there
    // is ALWAYS a cell growing within a tile of the rider, so the query holds
    // steady from first scoop to stream end. Sim-side and deterministic: the
    // Growing list is part of terrain state.
    internal static Vector2 NearbyMassMotion(EnvironmentContext ctx)
        => NearbyMassMotion(ctx, out _);

    // `massNearby` is the EVIDENCE output — true when any approaching mover is
    // within the wide probe at all — while the returned vector is the
    // lead-weighted servo TARGET. They must stay separate: a rider half a
    // tile ahead of the front has a near-zero target (station-keeping brake)
    // but is still very much riding a live stream.
    internal static Vector2 NearbyMassMotion(EnvironmentContext ctx, out bool massNearby)
    {
        massNearby = false;
        var body = ctx.Body;
        var b = body.Polygon.GetBoundingBox(body.Position);
        var probe = new BoundingBox(b.Left - MassProbeReach, b.Top - MassProbeReach,
                                    b.Right + MassProbeReach, b.Bottom + MassProbeReach);
        const float half = Chunk.TileSize * 0.5f;
        // Per-axis STRONGEST weighted contribution, not a sum: several nearby
        // cells moving the same way are one advancing front, and summing them
        // manufactured a target faster than any volume actually moves.
        Vector2 best = Vector2.Zero;
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
                // DISTANCE-KEEPING: weight by how far the body has pulled
                // ahead of this volume along its motion direction — full
                // strength at touch, zero at MassProbeReach. Feeding the raw
                // volume speed into the ride shot the rider off the front (a
                // volume moves faster than the crest it belongs to advances);
                // with the fade, the servo target drops as the rider leads
                // and rises as the mass closes in, so the rider settles at
                // exactly the gap where their speed matches the crest's real
                // advance. Velocity-match and station-keeping in one term.
                float lead = Vector2.Dot(body.Position - c, vel / speed)
                             - half - PlayerCharacter.Radius;
                float w = Math.Clamp(1f - lead / LeadFadeReach, 0f, 1f);
                var contrib = vel * w;
                if (MathF.Abs(contrib.X) > MathF.Abs(best.X)) best.X = contrib.X;
                if (MathF.Abs(contrib.Y) > MathF.Abs(best.Y)) best.Y = contrib.Y;
            }
        }
        return best;
    }

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => MathF.Abs(AggregateCarry(ctx.Body).X) > EnterHorizontalPush;

    // Once riding, the state persists on CONTINUOUS evidence — an actual push,
    // or moving mass still growing nearby — with the coyote hold only bridging
    // true gaps. This is what keeps the classification stable for the whole
    // ride (state flapping is poison for animation and for reasoning about
    // gamestate); the vertical-only entry gate above still keeps the plain
    // elevator in Standing.
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (MathF.Abs(AggregateCarry(ctx.Body).X) > StayHorizontalPush) return true;
        NearbyMassMotion(ctx, out bool massNearby);
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

        // The ride is a PER-AXIS envelope of the recent pushes, each axis with
        // its own coyote window. A stream's cells push in alternating bursts —
        // a lateral cell (110, 0) this slice, a vertical (0, −110) the next, a
        // diagonal both — and a single last-push memory collapses the ride
        // onto whichever axis pushed most recently (measured: the rider skims
        // horizontally off the front of a diagonal wave and falls). The union
        // of the axes IS the crest motion; each fades independently when its
        // pushes stop.
        // Target, BOTH axes: a live push wins (ground truth); otherwise the
        // lead-weighted nearby-mass motion, DIRECTLY — no latch, no timed
        // decay. The weighting already encodes station-keeping (full speed at
        // touch, zero at LeadFadeReach of lead), so braking engages the
        // moment the mass stops pushing or leading. Two earlier versions
        // failed instructively: latching the live push for a coyote window
        // overshot the young wave by ~2 tiles per scoop (luck decided the
        // rest), and leaving Y to the fold alone let the rider surf DOWN the
        // wave's forward face — falling while still pushed sideways, a
        // wipeout over the nose — because a hovering body is never touched
        // by the vertical front rising under it. The same lead-weighting
        // that paces X makes Y safe: a sensed vertical cell contributes only
        // when it is genuinely at the body.
        var live = AggregateCarry(ctx.Body);
        var near = NearbyMassMotion(ctx, out bool massNearby);
        vars.CarryVelocity.X = MathF.Abs(live.X) > 1f ? live.X : near.X;
        vars.CarryVelocity.Y = MathF.Abs(live.Y) > 1f ? live.Y : near.Y;
        vars.CarryHoldX = massNearby || MathF.Abs(live.X) > 1f
            ? CarryCoyoteSeconds : vars.CarryHoldX - ctx.Dt;
        var carry = vars.CarryVelocity;

        // Vertical: Standing's own baseline — gravity held while the support
        // (static floor or the rising volume itself, via the surface-relative
        // gate) is within reach, faded across the same band.
        var force = new Vector2(0f, StandingState.FoldBaseline(ctx).Y);

        // Horizontal: STATION FRICTION IN THE CARRY FRAME — a two-sided servo
        // toward carry.X at ground-friction authority, plus the player's own
        // steering offset. This is the pacing mechanism: a scoop imparts the
        // volume's full speed (via the contact carry-zero), which is FASTER
        // than the stream's crest advances (the cascade spends a slice per
        // cell) — a frictionless rider coasts off the front of the wave onto
        // bare ground (measured). Relaxing toward the decaying envelope
        // between pushes keeps the rider in the next cell's catch zone, while
        // a live push is never fought (the servo target IS the push).
        var cfg = MovementConfig.Current;
        var m = ctx.Modifiers;
        int dir = ctx.Intent.CurrentHorizontal;
        float targetX = carry.X + dir * cfg.MaxAirSpeed * m.MaxAirSpeed;
        float capX = cfg.GroundFriction * m.GroundFriction;
        force.X = Math.Clamp((targetX - ctx.Body.Velocity.X) / ctx.Dt, -capX, capX);

        ctx.Body.AppliedForce = force;

        // Vertical envelope, sign-aware ensure: the fold owns hover and lift;
        // this only guarantees a held vertical push isn't nibbled away by
        // gravity between penetrating frames. Never subtracts — a jump out
        // keeps its launch.
        ref var v = ref ctx.Body.Velocity;
        if (carry.Y < 0f && v.Y > carry.Y) v.Y = carry.Y;
        if (carry.Y > 0f && v.Y < carry.Y) v.Y = carry.Y;

        // Keep STANDING'S FOLD while carried. The vertical half of a ride was
        // never contact-driven: the fold hovers the body ~FoldHoverOffset above
        // the surface — more than a tile — so a cell growing beneath a hovering
        // body never actually touches it. The smooth vertical carry comes from
        // the fold solve tracking the rising floor envelope (which sees growing
        // volumes). Dropping to FoldProfile.None here silently killed that and
        // left the rider skimming horizontally off the front of a diagonal
        // wave. The fold's x-progress rows only engage with held input, so the
        // envelope does not fight the horizontal carry at neutral.
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default, FoldProfile.Stand, startGrounded: true);
    }
}
