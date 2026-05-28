using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Over:  body must pass ABOVE the corner (a low step / ledge).
// Under: body must pass BELOW the corner (an overcrop / low ceiling).
public enum SteeringSense { Over, Under }

// A phantom guard surface stamped on an exposed corner. It does NOT push the body and does NOT
// resolve position — every step it derives, from the body polygon vs. the corner, the shallowest
// trajectory that still clears the corner ("string from the corner": the binding vertex is the one
// whose ray to the corner is steepest), then rotates the body's velocity onto that trajectory with
// the magnitude preserved. Positional safety stays with the real polygon-vs-tile collision.
// See Character/STEERING_RAMP_IMPL.md.
public sealed class SteeringRamp : PhysicsContact
{
    public Vector2 Corner;        // ExposedCorner.InnerEdge / ExposedLowerCorner.InnerEdge
    public SteeringSense Sense;
    public int ForwardDir;        // ±1: the x-direction the body travels past the corner

    // Recomputed each step from the polygon (also useful for debug draw):
    public float   ThetaStar;     // [0, π/2]: the binding angle off "forward". 0 ⇒ inert this step.
    public Vector2 SurfaceDir;    // d*, unit: the shallowest clearing trajectory (the implicit ramp tangent)
    public Vector2 BannedDir;     // b, unit: d* rotated 90° toward the solid
    public float   Weight;        // [0,1]: Smoothstep(0, ThetaBand, ThetaStar)

    // Optional ceiling on |velocity| after the redirect — the owner state sets this so the climb
    // force can't blow the speed up through the rescale. Default = no cap.
    public float   MaxSpeed = float.PositiveInfinity;

    // Optional ceiling on the magnitude of the *upward* (negative-Y) component the
    // redirect may produce. Caps just the vy after rotation, on top of MaxSpeed:
    // a steep redirect on a tall ledge converts horizontal speed into a fast
    // vertical kick that the magnitude cap still allows, sending the player
    // ballistic. Default = no cap. Y-down convention: upward = negative Y, so
    // the clamp is body.Velocity.Y >= -MaxRedirectVy.
    public float   MaxRedirectVy = float.PositiveInfinity;

    // Optional ceiling on the magnitude of the impulse the ramp may deliver
    // per step (= MaxForce · dt). With MaxForce finite the ramp behaves like
    // a soft constraint: it rotates velocity *up to* this much, and anything
    // beyond stays in body.Velocity — so external forces stronger than the
    // ramp (knockback, gravity overrun) keep velocity into the underlying
    // tile and the swept resolver picks it up as a real impact.
    // Default = no cap (current behavior — infinite stiffness).
    public float   MaxForce      = float.PositiveInfinity;

    // Active-drive mode: owner state (e.g. ParkourState) sets HasTarget=true
    // and writes the velocity it wants the body to reach this step. In
    // ResolveVelocity the ramp computes dv = TargetVelocity - vBefore
    // (clipped at MaxForce·dt) instead of doing geometry-based velocity
    // rotation. This is how movement states route force *through the ramp*
    // rather than via body.AppliedForce, so the contact's LastImpulse
    // accurately reflects what the ramp delivered — see ParkourState.Update.
    //
    // Default HasTarget=false ⇒ legacy passive-rotation behavior. Owner
    // states clear/refresh HasTarget every frame so a stale target from a
    // previous activation doesn't persist.
    public bool    HasTarget;
    public Vector2 TargetVelocity;

    private const float ThetaBand      = 0.15f;   // radians: width of the Weight fade near ThetaStar = 0
    private const float BindEpsilon    = 1e-3f;   // a vertex binds iff rf > eps && rc > eps
    private const float SpeedEpsilon   = 1e-2f;   // below this speed there is nothing to redistribute
    private const float CombineLambda  = 0.1f;    // multi-ramp regularizer, as a fraction of max ramp weight
    private const float Clearance      = 1.5f;    // small graze-safety nudge toward the open side of the corner
    private const float OverVertLift   = PlayerCharacter.Radius;  // Over only: also lift the binding vertex a body-radius
                                                  // onto the clear side ⇒ the vault delivers the body at standing float
                                                  // height (= body-half-height + float-height = 2·Radius above the step),
                                                  // not crouched-low. (For Under there is no "float" below a ceiling —
                                                  // the body just needs to not intersect the slab — so it gets only the
                                                  // graze nudge: the ramp stays inert whenever the body actually fits.)
    public  const float WeightEpsilon  = 1e-3f;   // ramps weaker than this are treated as inert

    // Derive ThetaStar / SurfaceDir / BannedDir / Weight from the body's world-space polygon vertices.
    public void Recompute(Vector2[] worldVertices)
    {
        // Forward axis (toward the corner along travel) and clearance axis (the way the body must move to clear it).
        Vector2 fwd = new Vector2(ForwardDir, 0f);
        Vector2 clr = Sense == SteeringSense.Over ? new Vector2(0f, -1f) : new Vector2(0f, 1f);

        // Aim the binding-vertex trajectory a hair to the player side of the real corner (graze
        // safety), plus — for Over — a body-radius onto the clear side so the body crests at standing
        // float height rather than just barely over the lip.
        float vc = Sense == SteeringSense.Over ? OverVertLift : Clearance;
        Vector2 c = Corner + (-ForwardDir * Clearance) * fwd + vc * clr;

        float thetaMax = 0f;
        foreach (var v in worldVertices)
        {
            Vector2 r = c - v;
            float rf = Vector2.Dot(r, fwd);   // forward extent (vertex → corner); >0 ⇒ vertex hasn't passed the corner
            float rc = Vector2.Dot(r, clr);   // clearance extent; >0 ⇒ vertex is still on the blocked side
            if (rf <= BindEpsilon || rc <= BindEpsilon) continue;
            float theta = MathF.Atan2(rc, rf);   // in (0, π/2)
            if (theta > thetaMax) thetaMax = theta;
        }

        ThetaStar = thetaMax;
        float ct = MathF.Cos(ThetaStar), st = MathF.Sin(ThetaStar);
        SurfaceDir = ct * fwd + st * clr;        // unit
        BannedDir  = st * fwd - ct * clr;        // unit: d* rotated 90° toward the solid interior (fwd & anti-clr)
        Weight     = Smoothstep(0f, ThetaBand, ThetaStar);
    }

    // PhysicsWorld hook: recompute every steering ramp on the body from the current polygon, drop the
    // inert ones, and rotate velocity to respect the rest. No-op (and no allocation) if there are none.
    // `dt` is the simulation step duration, used to convert each ramp's MaxForce into a per-step Δv cap.
    public static void ApplyRedirect(PhysicsBody body, float dt)
    {
        bool any = false;
        foreach (var c in body.Constraints) if (c is SteeringRamp) { any = true; break; }
        if (!any) return;

        var verts = body.Polygon.GetVertices(body.Position);
        List<SteeringRamp> active = null;
        foreach (var c in body.Constraints)
        {
            if (c is not SteeringRamp ramp) continue;
            ramp.Recompute(verts);
            if (ramp.Weight <= WeightEpsilon) continue;
            (active ??= new List<SteeringRamp>()).Add(ramp);
        }
        if (active != null) ResolveVelocity(body, active, dt);
    }

    // Rotate body.Velocity (magnitude preserved, modulo each ramp's MaxSpeed cap) onto the
    // trajectory that best respects the active ramps. With MaxForce finite, the rotation
    // is clipped so the per-step Δv magnitude never exceeds MaxForce·dt — the unredirected
    // remainder stays in body.Velocity and the swept-tile resolver catches it as a real
    // impact (so knockback into a corner plows through rather than getting magic-redirected).
    public static void ResolveVelocity(PhysicsBody body, List<SteeringRamp> ramps, float dt)
    {
        float cap = float.PositiveInfinity;
        float vyCap = float.PositiveInfinity;
        float forceCap = float.PositiveInfinity;
        foreach (var r in ramps)
        {
            if (r.MaxSpeed      < cap)      cap      = r.MaxSpeed;
            if (r.MaxRedirectVy < vyCap)    vyCap    = r.MaxRedirectVy;
            if (r.MaxForce      < forceCap) forceCap = r.MaxForce;
        }
        Vector2 vBefore = body.Velocity;

        // Target-drive mode (owner has set HasTarget on at least one ramp):
        // vIdeal = the average of all set targets. The common ParkourState
        // case sets the same target on every engaged ramp, so the average
        // collapses to that target. Mixed (some have target, some don't)
        // averages only the ones that do — keeps things composable.
        // Legacy mode (no targets): geometry-aware velocity rotation as
        // before (single-ramp: project off banned direction & rescale,
        // multi-ramp: 64-sample angle scan).
        Vector2 combinedTarget = Vector2.Zero;
        int targetCount = 0;
        foreach (var r in ramps)
        {
            if (r.HasTarget) { combinedTarget += r.TargetVelocity; targetCount++; }
        }
        bool useTargetMode = targetCount > 0;

        Vector2 vIdeal;
        if (useTargetMode)
        {
            vIdeal = combinedTarget / targetCount;
        }
        else
        {
            float s = MathF.Min(vBefore.Length(), cap);
            if (s < SpeedEpsilon) return;
            if (ramps.Count == 1)
            {
                var ramp = ramps[0];
                float into = MathF.Max(0f, Vector2.Dot(vBefore, ramp.BannedDir));
                Vector2 vRem = vBefore - ramp.Weight * into * ramp.BannedDir;
                float remLen = vRem.Length();
                vIdeal = remLen > SpeedEpsilon ? vRem * (s / remLen) : s * ramp.SurfaceDir;
            }
            else
            {
                // û* = argmin over unit û of [ Σ wᵢ·max(0, û·bᵢ)  +  λ·(1 − û·v̂) ].
                // (Phase 1: 64-sample scan, no Newton refine — one-block-step ParkourState never reaches this path.)
                float maxW = 0f;
                foreach (var r in ramps) if (r.Weight > maxW) maxW = r.Weight;
                float lambda = CombineLambda * maxW;
                Vector2 vHat = Vector2.Normalize(vBefore);
                float baseAngle = MathF.Atan2(vHat.Y, vHat.X);

                const int Samples = 64;
                float bestAngle = baseAngle;
                float bestCost = float.MaxValue;
                for (int i = 0; i < Samples; i++)
                {
                    float ang = baseAngle + (MathHelper.TwoPi * i) / Samples;
                    Vector2 u = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                    float cost = lambda * (1f - Vector2.Dot(u, vHat));
                    foreach (var r in ramps) cost += r.Weight * MathF.Max(0f, Vector2.Dot(u, r.BannedDir));
                    if (cost < bestCost) { bestCost = cost; bestAngle = ang; }
                }
                vIdeal = new Vector2(MathF.Cos(bestAngle), MathF.Sin(bestAngle)) * s;
            }
        }

        Vector2 dv = vIdeal - vBefore;
        if (forceCap < float.PositiveInfinity && dt > 0f)
        {
            float maxDv = forceCap * dt;
            float dvLen = dv.Length();
            if (dvLen > maxDv) dv = dv * (maxDv / dvLen);
        }
        body.Velocity = vBefore + dv;

        // Vertical kick cap (y-down ⇒ upward = negative Y). Applied after the
        // force cap so it remains a hard ceiling on vy regardless of MaxForce.
        if (body.Velocity.Y < -vyCap) body.Velocity = new Vector2(body.Velocity.X, -vyCap);

        // Per-contact impulse accounting. Use the *actual* dv (post-clip,
        // post-vyCap) so downstream readers see what the body really
        // received from each ramp.
        Vector2 actualDv = body.Velocity - vBefore;
        if (ramps.Count == 1)
        {
            ramps[0].LastImpulse += actualDv;
        }
        else
        {
            float wSum = 0f;
            foreach (var r in ramps) wSum += r.Weight;
            if (wSum > 0f)
                foreach (var r in ramps) r.LastImpulse += actualDv * (r.Weight / wSum);
        }
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0) return x >= edge1 ? 1f : 0f;
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
