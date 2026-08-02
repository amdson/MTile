using System;
using Microsoft.Xna.Framework;

namespace MTile;

// One predicted tick of the correction-free coast (Plans/BALLISTIC_CORRECTOR_PLAN.md §2).
public struct CoastSample
{
    public Vector2 Pos;       // body position at the END of the predicted tick
    public Vector2 Vel;       // body velocity at the END of the predicted tick
    public bool    Grounded;  // the standing spring was engaged for this tick
    public float   FloorY;    // floor top under the body when Grounded (else +∞)
}

// Forward-simulates the coast — baseline feedforward + gravity, NO corrections —
// H steps from a body state, mirroring PhysicsWorld.StepSwept's exact order for a
// body whose only contact is the state-owned ground FSD:
//
//   probe/classify → feedforward force → v += F·dt → v += g·dt
//   → FSD normal-velocity zeroing (dist < MinDistance)
//   → FSD plane sweep (crossing MinDistance from above) → p += v·dt
//
// Deliberately thin (NOT a second physics engine): tile collision RESPONSE,
// friction bookkeeping, steering ramps, sprouts/moving providers, and the FSM are
// all excluded. Penetration detection is the constraint builder's job — samples
// that fly into terrain are exactly the violations the corrector exists to see.
// Prediction is the only place dynamics are evaluated; the feedforward is the
// purified BaselineFeedforward shared with (mirroring, until step 4+) the live
// states, so drag/modifier changes are respected automatically.
//
// Determinism: plain loops, no statics, no allocation — output goes into the
// caller's CoastSample[] (pooled on PlayerCharacter when wired; see plan §6).
public static class BallisticPredictor
{
    public const int MaxHorizon = 48;   // ≥ any CorrectorHorizon a config will ask for

    // How far below the body a floor may be and still count as SUPPORT — the
    // gravity-hold + envelope-anchor engagement reach (hover rest sits ~20px
    // above the floor top; a ballistic pass-by 40px up must stay ballistic).
    // One constant shared by the live fold states' gravity hold, this coast's
    // grounded classification, and the ambient envelope's anchor band — the
    // three MUST agree or the solver plans against a baseline the live tick
    // doesn't apply (the zero-g floater bug).
    public const float SupportReach = 25f;

    // Inside this floor distance the gravity hold is FULL strength; it fades
    // linearly to zero at SupportReach (≈ the old spring's zero-force length
    // vs its rest length — a body floating above hover must feel gravity).
    public const float HoldFullDist = 2f * PlayerCharacter.Radius;

    // Free-fall launch speed that coasts exactly `rise` px upward — the shared
    // ballistic-envelope primitive (hop sizing, crest caps). Formerly
    // SteeringRamp.BallisticVy; the ramp stack is gone, the math stays.
    public static float BallisticVy(float rise)
        => MathF.Sqrt(2f * Simulation.WorldGravityY * MathF.Max(0f, rise));

    // Predicts `steps` ticks from (pos, vel), writing samples[0 .. steps-1].
    // Returns the number of samples written (== steps; truncation at deep
    // violations is the constraint builder's call, not the predictor's).
    //
    // inputDirX ∈ {-1, 0, +1} and inputDown are the frozen input for the horizon.
    // startGrounded = is the CURRENT movement state a grounded one (Standing/
    // Crouched)? It seeds the entry-vs-continuation asymmetry of the ground
    // classification: entry additionally requires the rise-speed gate
    // (StandingState.IsStandingGround); continuation is the plain probe.
    //
    // tickDv (optional): per-tick velocity-lever corrections z_k applied after
    // gravity, mirroring PredictGuided — used for the ambient mode's corrected
    // ("solved") rollout capture.
    public static int Predict(
        PhysicsBody body, ChunkMap chunks,
        int inputDirX, bool inputDown, bool startGrounded,
        in MovementModifiers modifiers, Vector2 gravity,
        float dt, int steps, CoastSample[] samples, Vector2[] tickDv = null)
    {
        var cfg = MovementConfig.Current;
        steps = Math.Min(steps, samples.Length);

        Vector2 pos = body.Position;
        Vector2 vel = body.Velocity;
        var polygon = body.Polygon;
        float floatHeight = PlayerCharacter.Radius;

        // The stand fold: no standing spring, no FSD zeroing/sweep in this coast
        // (mirroring the live StandingState) — vertical support is the solver's
        // job via soft hover rows. FloorY is reported whenever a floor is within
        // probe reach so the ambient corrector can synthesize hover rows.
        for (int k = 0; k < steps; k++)
        {
            var bounds = polygon.GetBoundingBox(pos);
            bool haveFloor = TryProbeFloor(chunks, bounds, floatHeight, out float floorY);
            // Supported ≠ merely "probe sees a floor": the probe reaches ~40px
            // down (float height + slack), but support engages only within
            // SupportReach — a ballistic pass-by above that is honest free
            // flight — and only below the FSD-era engagement speed gate: a
            // plunge past MaxGroundEngageVnRel lands via the raw swept-impact
            // path, never the hold (mirrors the live TryGetGround gate).
            bool grounded = haveFloor && floorY - pos.Y <= SupportReach
                && vel.Y <= cfg.MaxGroundEngageVnRel
                && -vel.Y <= cfg.SpringMaxRiseSpeed;

            var force = Vector2.Zero;
            if (grounded)
            {
                // No ground drive in the coast — x-locomotion is requested from
                // the solver via progress rows. Vertically, the stand baseline
                // HOLDS AGAINST GRAVITY (mirrored by live FoldBaseline),
                // fading across [HoldFullDist, SupportReach]: sustained support
                // is feedforward, not a correction — channels act relative to
                // the hold (LegServo = push beyond it, Tuck = release/press
                // below it). At no input, ground FRICTION is also baseline
                // (mirrored live): the fold body never physically touches the
                // floor, so SurfaceContact.Friction can't brake it — this term
                // is that friction, re-expressed as feedforward.
                float holdScale = Math.Clamp(
                    (SupportReach - (floorY - pos.Y)) / (SupportReach - HoldFullDist), 0f, 1f);
                force.Y -= gravity.Y * holdScale;
                if (inputDirX == 0 && dt > 0f)
                {
                    float frictionCap = cfg.GroundFriction * modifiers.GroundFriction;
                    force.X = Math.Clamp(-vel.X / dt, -frictionCap, frictionCap) * holdScale;
                }
            }
            else
            {
                force.X = BaselineFeedforward.Air(
                    inputDirX, vel.X,
                    cfg.AirAccel    * modifiers.AirAccel,
                    cfg.MaxAirSpeed * modifiers.MaxAirSpeed,
                    cfg.AirDrag     * modifiers.AirDrag,
                    modifiers.PreserveExternalVelocity, dt);
                if (inputDown)
                    force.Y += cfg.FastFallForce;
            }

            // Gravity-scale modifier as a counter-force (PlayerCharacter.Update's shape).
            if (modifiers.GravityScale != 1f)
                force += gravity * (modifiers.GravityScale - 1f);

            vel += force * dt;
            vel += gravity * dt;
            if (tickDv != null) vel += tickDv[k];
            pos += (pos + vel * dt) - pos;   // StepSwept's displacement rounding

            samples[k].Pos      = pos;
            samples[k].Vel      = vel;
            samples[k].Grounded = grounded;
            samples[k].FloorY   = haveFloor ? floorY : float.PositiveInfinity;
        }

        return steps;
    }

    // Maneuver-mode coast: the baseline is the MANEUVER's own feedforward — a
    // guided horizontal drive toward dir·targetSpeed (the vault's entry-speed
    // preservation) instead of the free run/air input drive. Vertical dynamics are
    // unchanged: gravity, plus the standing spring whenever the per-sample ground
    // probe engages (so the landing settles in prediction, not as a violation).
    //
    // tickDv (optional): per-tick velocity-lever corrections z_k from a prior
    // solver pass, applied after gravity like the live redirect would be — this is
    // the outer sequential-convexification pass's "re-rollout WITH the planned
    // corrections applied".
    public static int PredictGuided(
        PhysicsBody body, ChunkMap chunks,
        int dir, float targetSpeed, bool startGrounded,
        Vector2 gravity, float dt, int steps,
        CoastSample[] samples, Vector2[] tickDv = null)
    {
        var cfg = MovementConfig.Current;
        steps = Math.Min(steps, samples.Length);

        Vector2 pos = body.Position;
        Vector2 vel = body.Velocity;
        var polygon = body.Polygon;
        bool prevGrounded = startGrounded;

        float halfHeight  = PlayerCharacter.Radius;
        float floatHeight = PlayerCharacter.Radius;
        float minDistance = halfHeight + floatHeight;
        var   groundNormal = new Vector2(0f, -1f);

        for (int k = 0; k < steps; k++)
        {
            var bounds = polygon.GetBoundingBox(pos);
            bool haveFloor = TryProbeFloor(chunks, bounds, floatHeight, out float floorY);
            bool grounded = haveFloor
                && MathF.Abs(vel.Y) <= cfg.MaxGroundEngageVnRel
                && (prevGrounded || -vel.Y <= cfg.SpringMaxRiseSpeed);

            var groundPos = new Vector2(pos.X, floorY);

            // Guided drive: two-sided servo toward dir·targetSpeed, force-capped
            // at WalkAccel — the same SoftClampVelocity the guided states use.
            var force = Vector2.Zero;
            force.X = AirControl.SoftClampVelocity(vel.X, dir * targetSpeed, cfg.WalkAccel, dt);
            if (grounded)
            {
                var spring = BaselineFeedforward.Ground(
                    pos, vel, groundPos, groundNormal, Vector2.Zero, minDistance,
                    inputX: 0f, walkAccel: 0f, maxWalkSpeed: 0f,
                    preserveExternalVelocity: false,
                    cfg.SpringK, cfg.SpringDamping, cfg.SpringMaxRiseSpeed, dt);
                force.Y += spring.Y;
            }

            vel += force * dt;
            vel += gravity * dt;
            if (tickDv != null) vel += tickDv[k];

            if (grounded)
            {
                float dist = Vector2.Dot(pos - groundPos, groundNormal);
                if (dist < minDistance)
                {
                    float vnRel = Vector2.Dot(vel, groundNormal);
                    if (vnRel < 0f) vel -= vnRel * groundNormal;
                }
                var disp = (pos + vel * dt) - pos;
                float dn = Vector2.Dot(disp, groundNormal);
                if (dn < 0f && dist >= minDistance)
                {
                    float t = (minDistance - dist) / dn;
                    if (t >= 0f && t <= 1f)
                    {
                        pos += disp * t + groundNormal * PhysicsWorld.Epsilon;
                        disp *= 1f - t;
                        float vnRel = Vector2.Dot(vel, groundNormal);
                        if (vnRel < 0f) vel -= vnRel * groundNormal;
                        float dn2 = Vector2.Dot(disp, groundNormal);
                        if (dn2 < 0f) disp -= dn2 * groundNormal;
                    }
                }
                pos += disp;
            }
            else
            {
                pos += (pos + vel * dt) - pos;
            }

            samples[k].Pos      = pos;
            samples[k].Vel      = vel;
            samples[k].Grounded = grounded;
            // FloorY is informational (channel masks key "near ground" off it —
            // a lip crossing is near the step top even while unsupported);
            // Grounded alone drives the dynamics above.
            samples[k].FloorY   = haveFloor ? floorY : float.PositiveInfinity;
            prevGrounded = grounded;
        }

        return steps;
    }

    // Static-tile floor probe under the body — mirror of GroundChecker.TryFind's
    // tile branch (strip below the horizontally-inset bounds, highest tile top
    // that starts within the strip). Sprouts and external shape providers are
    // deliberately out of scope: the coast is predicted against static terrain.
    private static bool TryProbeFloor(ChunkMap chunks, BoundingBox bounds, float floatHeight, out float floorY)
    {
        const float HorizontalInset = 2f;   // = GroundChecker.HorizontalInset
        var probe = bounds.InsetHorizontal(HorizontalInset).StripBelow(floatHeight + GroundChecker.ProbeSlack);

        floorY = float.MaxValue;
        int ts = Chunk.TileSize;
        int colMin = (int)MathF.Floor(probe.Left / ts);
        int colMax = (int)MathF.Floor(probe.Right / ts);
        int rowMin = (int)MathF.Floor(probe.Top / ts);
        int rowMax = (int)MathF.Floor(probe.Bottom / ts);
        // Inclusive floor bounds on all four edges — the exact tile set
        // TileQuery.SolidTilesInRect enumerates for this rect (edge-flush
        // tiles included), which is what GroundChecker's fluent probe sees.

        for (int gtx = colMin; gtx <= colMax; gtx++)
        for (int gty = rowMin; gty <= rowMax; gty++)
        {
            if (!TileQuery.IsSolidAt(chunks, gtx * ts + ts * 0.5f, gty * ts + ts * 0.5f)) continue;
            float top = gty * ts;
            if (top < probe.Top - 1f) continue;   // wall rising through the strip, not a floor
            if (top < floorY) floorY = top;
        }
        return floorY != float.MaxValue;
    }
}
