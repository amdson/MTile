using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public static class PhysicsWorld
{
    public const float Epsilon = 0.5f;

    public static void Step(
        List<PhysicsBody> bodies,
        ChunkMap chunks,
        float dt,
        Vector2 gravity)
    {
        foreach (var body in bodies)
        {
            // Saved before reset so the friction step can tell whether the state
            // applied a tangential force this frame (walking / running / etc.).
            // If so, friction stays out of the way; otherwise it brakes / carries.
            Vector2 savedForce = body.AppliedForce;
            body.Velocity += body.AppliedForce * dt;
            body.AppliedForce = Vector2.Zero;
            body.Velocity += gravity * dt;

            foreach (var c in body.Constraints)
            {
                if (c is not SurfaceContact sc) continue;
                // TODO replace dist heuristic with check for line-segment intersection between body and plane
                float dist = Vector2.Dot(body.Position - sc.Position, sc.Normal);
                if (dist < sc.MinDistance)
                {
                    // Zero the *relative* normal velocity (body − surface) so a body
                    // resting on a moving surface inherits its motion. For a static
                    // surface SurfaceVelocity is zero and this collapses to the old form.
                    float vnRel = Vector2.Dot(body.Velocity - sc.SurfaceVelocity, sc.Normal);
                    if (vnRel < 0f)
                        body.Velocity -= vnRel * sc.Normal;
                }
                // Friction is tangential coupling that exists whenever the surface
                // is engaged with the body — and the contact's presence in
                // body.Constraints is the engagement signal (owning states add on
                // Enter, remove on Exit). It can't be coupled to the normal-impulse
                // gate above because a body hovering on a soft spring contact
                // (StandingState's FSD) has its normal motion managed by the
                // state's spring force, not by the constraint loop — so the impulse
                // gate never fires in steady state and a coupled friction call
                // would never brake horizontal motion.
                ApplyContactFriction(body, sc, savedForce, dt);
            }

            var nextPos = body.Position + body.Velocity * dt;
            nextPos = ResolveChunkCollisions(body, chunks, nextPos, savedForce, dt);
            body.Position = nextPos;

            var bodyBounds = body.Polygon.GetBounds(body.Position);
            body.Constraints.RemoveAll(c =>
            {
                if (c is not SurfaceDistance sd) return false;
                if (Vector2.Dot(body.Position - sd.Position, sd.Normal) > 2f * Epsilon) return true;
                // Query-driven refresh: re-probe each frame so a moving source surface's
                // current velocity overwrites the snapshot stamped at collision time.
                if (!TryFindContactSurface(chunks, bodyBounds, sd.Normal, out var surfaceVel)) return true;
                sd.SurfaceVelocity = surfaceVel;
                return false;
            });
        }
    }

    private static Vector2 ResolveChunkCollisions(PhysicsBody body, ChunkMap chunks, Vector2 nextPos, Vector2 savedForce, float dt)
    {
        const int maxIterations = 8;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            var bounds = body.Polygon.GetBoundingBox(nextPos);
            bool anyHit = false;

            foreach (var shape in WorldQuery.SolidShapesInRect(chunks, bounds))
            {
                var hit = Collision.Check(body.Polygon, nextPos, 0f, shape.Polygon, shape.Position, 0f);
                if (!hit.Intersects) continue;

                anyHit = true;
                var normal = Vector2.Normalize(hit.MTV);
                nextPos += hit.MTV + normal * Epsilon;
                bounds = body.Polygon.GetBoundingBox(nextPos);

                // Zero the *relative* normal velocity so a body landing on a moving
                // surface emerges moving with it along the contact normal — the carry.
                // For static tiles shape.Velocity is zero and this collapses to absolute.
                float vnRel = Vector2.Dot(body.Velocity - shape.Velocity, normal);
                if (vnRel < 0f)
                {
                    body.Velocity -= vnRel * normal;
                    ApplyFrictionAtImpact(body, normal, shape.Velocity, ComputeFrictionForNormal(normal), savedForce, dt);
                    TryApplyImpactDamage(body, bounds, normal, vnRel, chunks);
                }

                UpdateSurfaceConstraint(body, nextPos, normal, shape.Velocity);
            }

            if (!anyHit) break;
        }
        return nextPos;
    }

    public static void StepSwept(
        List<PhysicsBody> bodies,
        ChunkMap chunks,
        float dt,
        Vector2 gravity)
    {
        foreach (var body in bodies)
        {
            Vector2 savedForce = body.AppliedForce;
            body.Velocity += body.AppliedForce * dt;
            body.AppliedForce = Vector2.Zero;
            body.Velocity += gravity * dt;

            foreach (var c in body.Constraints)
            {
                if (c is not SurfaceContact sc) continue;
                float dist = Vector2.Dot(body.Position - sc.Position, sc.Normal);
                if (dist < sc.MinDistance)
                {
                    // Zero the *relative* normal velocity (body − surface) so a body
                    // resting on a moving surface inherits its motion. For a static
                    // surface SurfaceVelocity is zero and this collapses to the old form.
                    float vnRel = Vector2.Dot(body.Velocity - sc.SurfaceVelocity, sc.Normal);
                    if (vnRel < 0f)
                        body.Velocity -= vnRel * sc.Normal;
                }
                // See the matching note in Step(): friction is gated on contact
                // presence, not on the impulse condition, so soft spring contacts
                // still brake tangential motion in steady state.
                ApplyContactFriction(body, sc, savedForce, dt);
            }

            // Steering ramps: rotate velocity onto the shallowest trajectory that clears the corner
            // (derived from the polygon each step). Position is still resolved by the real tile sweep below.
            SteeringRamp.ApplyRedirect(body);

            // Depenetration pre-pass. The swept solver only ejects by Epsilon per bounce
            // when the body starts overlapping a shape — fine for static tiles the body
            // never enters, but a TileSprout that flipped to Solid mid-overlap can leave
            // the body wedged by several px, and 4 bounces × 0.5 px isn't enough. Discrete
            // ResolveChunkCollisions applies the full MTV per iter (cap 8), so it cleanly
            // ejects from any starting overlap before the sweep begins.
            body.Position = ResolveChunkCollisions(body, chunks, body.Position, savedForce, dt);

            body.Position = ResolveChunkCollisionsSwept(body, chunks, body.Position, body.Position + body.Velocity * dt, savedForce, dt);

            var bodyBounds = body.Polygon.GetBounds(body.Position);
            body.Constraints.RemoveAll(c =>
            {
                if (c is not SurfaceDistance sd) return false;
                if (Vector2.Dot(body.Position - sd.Position, sd.Normal) > 2f * Epsilon) return true;
                // Query-driven refresh: re-probe each frame so a moving source surface's
                // current velocity overwrites the snapshot stamped at collision time.
                if (!TryFindContactSurface(chunks, bodyBounds, sd.Normal, out var surfaceVel)) return true;
                sd.SurfaceVelocity = surfaceVel;
                return false;
            });
        }
    }

    private static Vector2 ResolveChunkCollisionsSwept(PhysicsBody body, ChunkMap chunks, Vector2 pos, Vector2 targetPos, Vector2 savedForce, float dt)
    {
        const int maxBounces = 4;

        var displacement = targetPos - pos;

        for (int bounce = 0; bounce < maxBounces; bounce++)
        {
            if (displacement.LengthSquared() < 0.001f) break;

            var sweptBounds = GetSweptBounds(body.Polygon, pos, displacement);
            bool anyHit = false;
            float minT = 1f;
            Vector2 hitNormal = Vector2.Zero;
            Vector2 hitSurfaceVel = Vector2.Zero;
            float hitFriction = 0f;
            bool hitFromFloating = false;

            // All solid shapes — tiles, sprouts, external dynamic providers — go
            // through WorldQuery so the sweep sees one unified surface set. For
            // static tiles shape.Velocity is zero; the relative-frame carry math
            // below collapses to the absolute case.
            foreach (var shape in WorldQuery.SolidShapesInRect(chunks, sweptBounds))
            {
                var swept = Collision.Swept(body.Polygon, pos, 0f, displacement, shape.Polygon, shape.Position, 0f);
                if (!swept.Hit || swept.T > minT) continue;

                minT = swept.T;
                hitNormal = swept.Normal;
                hitSurfaceVel = shape.Velocity;
                hitFriction = ComputeFrictionForNormal(swept.Normal);
                anyHit = true;
                hitFromFloating = false;
            }

            // Treat each FloatingSurfaceDistance as a plane the body sweeps against.
            foreach (var c in body.Constraints)
            {
                if (c is not FloatingSurfaceDistance fsd) continue;
                float dn = Vector2.Dot(displacement, fsd.Normal);
                if (dn >= 0f) continue;
                float distNow = Vector2.Dot(pos - fsd.Position, fsd.Normal);
                float t = (fsd.MinDistance - distNow) / dn;
                if (t < 0f || t > minT) continue;
                minT = t;
                hitNormal = fsd.Normal;
                hitSurfaceVel = fsd.SurfaceVelocity;
                hitFriction = fsd.Friction;
                anyHit = true;
                hitFromFloating = true;
            }

            if (!anyHit) break;

            // Already overlapping at start of step: fall back to discrete push-out at target position.
            if (hitNormal == Vector2.Zero)
            {
                pos = ResolveChunkCollisions(body, chunks, pos + displacement, savedForce, dt);
                displacement = Vector2.Zero;
                break;
            }

            pos += displacement * minT + hitNormal * Epsilon;
            displacement *= 1f - minT;

            // Zero the *relative* normal velocity so a body landing on a moving surface
            // emerges moving with it along the contact normal — the carry. Pair it
            // with friction so the same impulse that locks normal motion also brakes
            // tangential motion (Coulomb-coupled).
            float vnRel = Vector2.Dot(body.Velocity - hitSurfaceVel, hitNormal);
            if (vnRel < 0f)
            {
                body.Velocity -= vnRel * hitNormal;
                ApplyFrictionAtImpact(body, hitNormal, hitSurfaceVel, hitFriction, savedForce, dt);
                if (!hitFromFloating)
                    TryApplyImpactDamage(body, body.Polygon.GetBoundingBox(pos), hitNormal, vnRel, chunks);
            }

            float dn2 = Vector2.Dot(displacement, hitNormal);
            if (dn2 < 0f) displacement -= dn2 * hitNormal;

            if (!hitFromFloating)
                UpdateSurfaceConstraint(body, pos, hitNormal, hitSurfaceVel);
        }

        return pos + displacement;
    }

    private static BoundingBox GetSweptBounds(Polygon polygon, Vector2 pos, Vector2 displacement)
    {
        var b0 = polygon.GetBoundingBox(pos);
        var b1 = polygon.GetBoundingBox(pos + displacement);
        return new BoundingBox(
            MathF.Min(b0.Left,   b1.Left),
            MathF.Min(b0.Top,    b1.Top),
            MathF.Max(b0.Right,  b1.Right),
            MathF.Max(b0.Bottom, b1.Bottom));
    }

    private static void UpdateSurfaceConstraint(PhysicsBody body, Vector2 resolvedPos, Vector2 normal, Vector2 surfaceVelocity)
    {
        // Floor-pointing normals (within ~45° of straight up) get the ground-friction
        // default; walls and ceilings stay frictionless so wall-slides and head-bumps
        // don't pick up a spurious tangential coupling.
        float friction = normal.Y < -0.7f ? MovementConfig.Current.GroundFriction : 0f;

        foreach (var c in body.Constraints)
        {
            if (c is SurfaceDistance sd && Vector2.Dot(sd.Normal, normal) > 0.9f)
            {
                sd.Position = resolvedPos;
                sd.Normal = normal;
                sd.MinDistance = Epsilon;
                sd.SurfaceVelocity = surfaceVelocity;
                sd.Friction = friction;
                return;
            }
        }
        body.Constraints.Add(new SurfaceDistance(resolvedPos, normal, Epsilon)
        {
            SurfaceVelocity = surfaceVelocity,
            Friction = friction,
        });
    }

    // Apply friction at a single contact, capping the body's *relative* tangential
    // velocity reduction per step at Friction · dt. If the state has applied a
    // tangential force this frame (walking, running, etc.), we skip — the state is
    // actively driving the body, and the contact's job is to not fight that.
    // Without a tangential applied force the friction handles both braking-when-
    // grounded and the tangential carry from moving surfaces.
    private static void ApplyContactFriction(PhysicsBody body, SurfaceContact sc, Vector2 savedForce, float dt)
        => ApplyFrictionAtImpact(body, sc.Normal, sc.SurfaceVelocity, sc.Friction, savedForce, dt);

    // Same logic, callable at sweep-intercept sites where the caller has the raw
    // normal/surface-velocity/friction rather than a SurfaceContact instance.
    private static void ApplyFrictionAtImpact(PhysicsBody body, Vector2 normal, Vector2 surfaceVelocity, float friction, Vector2 savedForce, float dt)
    {
        if (friction <= 0f) return;

        Vector2 vRel = body.Velocity - surfaceVelocity;
        Vector2 vTangent = vRel - Vector2.Dot(vRel, normal) * normal;
        float vTangentMag = vTangent.Length();
        if (vTangentMag < 0.001f) return;

        Vector2 tangentDir = vTangent / vTangentMag;
        // If the state pushed along this tangent, leave friction off. Threshold is
        // generous (~1 N/kg) so micro-noise in AppliedForce doesn't keep friction
        // suppressed when the state is effectively idle.
        if (MathF.Abs(Vector2.Dot(savedForce, tangentDir)) > 1f) return;

        float deltaMag = MathF.Min(vTangentMag, friction * dt);
        body.Velocity -= tangentDir * deltaMag;
    }

    // Floor-pointing normals (within ~45° of straight up) get the configured
    // ground-friction coefficient; walls and ceilings stay frictionless so
    // wall-slides and head-bumps don't pick up a spurious tangential coupling.
    private static float ComputeFrictionForNormal(Vector2 normal)
        => normal.Y < -0.7f ? MovementConfig.Current.GroundFriction : 0f;

    // Probe a thin slab along the impact face for every tile pressed against it,
    // then split the impulse-derived damage equally among them. This is what makes
    // a body landing on the boundary between two tiles damage *both* rather than
    // crediting whichever the collision solver iterated to first. The strip
    // direction mirrors TryFindContactSurface: `normal` points from surface to
    // body, so the impact face is on the -normal side.
    private static readonly List<(int gtx, int gty)> _impactCells = new(4);
    private static void TryApplyImpactDamage(PhysicsBody body, BoundingBox bounds, Vector2 normal, float vnRel, ChunkMap chunks)
    {
        if (body.Impact == null) return;
        float impulse = body.Impact.Mass * MathF.Abs(vnRel);
        float over    = impulse - body.Impact.ImpulseThreshold;
        if (over <= 0f) return;

        const float probe = 1f;
        BoundingBox strip;
        if (MathF.Abs(normal.Y) >= MathF.Abs(normal.X))
            strip = normal.Y < 0 ? bounds.StripBelow(probe) : bounds.StripAbove(probe);
        else
            strip = normal.X < 0 ? bounds.StripRight(probe) : bounds.StripLeft(probe);

        _impactCells.Clear();
        foreach (var shape in WorldQuery.SolidShapesInRect(chunks, strip))
        {
            int gtx = (int)MathF.Floor(shape.WorldCenterX / Chunk.TileSize);
            int gty = (int)MathF.Floor(shape.WorldCenterY / Chunk.TileSize);
            _impactCells.Add((gtx, gty));
        }
        if (_impactCells.Count == 0) return;

        float per = over * body.Impact.DamagePerUnitImpulse / _impactCells.Count;
        foreach (var (gtx, gty) in _impactCells) chunks.DamageCell(gtx, gty, per);
    }

    // Probe the strip just beyond the body's face along -normal for any solid shape.
    // Returns the first shape's velocity if one is found (zero for tiles), or false
    // if the strip is empty. Used both to prune constraints whose source surface is
    // gone and to refresh kept constraints' SurfaceVelocity each frame — without
    // that refresh, a sinusoidal platform's constraint clamps the body to its
    // collision-time velocity even as the platform decelerates, sending the body
    // ballistic on direction reversals.
    private static bool TryFindContactSurface(ChunkMap chunks, Rectangle bodyBounds, Vector2 normal, out Vector2 surfaceVelocity)
    {
        const float probe = 2f;

        BoundingBox strip;
        if (MathF.Abs(normal.Y) >= MathF.Abs(normal.X))
            strip = normal.Y < 0
                ? new BoundingBox(bodyBounds.Left, bodyBounds.Bottom,         bodyBounds.Right, bodyBounds.Bottom + probe)
                : new BoundingBox(bodyBounds.Left, bodyBounds.Top - probe,    bodyBounds.Right, bodyBounds.Top);
        else
            strip = normal.X < 0
                ? new BoundingBox(bodyBounds.Right,         bodyBounds.Top, bodyBounds.Right + probe, bodyBounds.Bottom)
                : new BoundingBox(bodyBounds.Left - probe,  bodyBounds.Top, bodyBounds.Left,          bodyBounds.Bottom);

        foreach (var shape in WorldQuery.SolidShapesInRect(chunks, strip))
        {
            surfaceVelocity = shape.Velocity;
            return true;
        }
        surfaceVelocity = Vector2.Zero;
        return false;
    }

}
