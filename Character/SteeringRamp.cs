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
    public static void ApplyRedirect(PhysicsBody body)
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
        if (active != null) ResolveVelocity(body, active);
    }

    // Rotate body.Velocity (magnitude preserved, modulo each ramp's MaxSpeed cap) onto the
    // trajectory that best respects the active ramps.
    public static void ResolveVelocity(PhysicsBody body, List<SteeringRamp> ramps)
    {
        float cap = float.PositiveInfinity;
        foreach (var r in ramps) if (r.MaxSpeed < cap) cap = r.MaxSpeed;
        float s = MathF.Min(body.Velocity.Length(), cap);
        if (s < SpeedEpsilon) return;

        if (ramps.Count == 1)
        {
            var ramp = ramps[0];
            Vector2 v = body.Velocity;
            float into = MathF.Max(0f, Vector2.Dot(v, ramp.BannedDir));
            Vector2 vRem = v - ramp.Weight * into * ramp.BannedDir;
            float remLen = vRem.Length();
            body.Velocity = remLen > SpeedEpsilon ? vRem * (s / remLen) : s * ramp.SurfaceDir;
            return;
        }

        // Two or more: û* = argmin over unit û of [ Σ wᵢ·max(0, û·bᵢ)  +  λ·(1 − û·v̂) ].
        // (Phase 1: 64-sample scan, no Newton refine — one-block-step ParkourState never reaches this path.)
        float maxW = 0f;
        foreach (var r in ramps) if (r.Weight > maxW) maxW = r.Weight;
        float lambda = CombineLambda * maxW;
        Vector2 vHat = Vector2.Normalize(body.Velocity);
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
        body.Velocity = new Vector2(MathF.Cos(bestAngle), MathF.Sin(bestAngle)) * s;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0) return x >= edge1 ? 1f : 0f;
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
