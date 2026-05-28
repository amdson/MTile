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
                    {
                        body.Velocity -= vnRel * sc.Normal;
                        sc.LastImpulse += -vnRel * sc.Normal;
                        float mag = MathF.Abs(vnRel);
                        if (mag > body.LastImpulseMagnitude) body.LastImpulseMagnitude = mag;
                    }
                }

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
        const int maxIterations = 12;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            var bounds = body.Polygon.GetBoundingBox(nextPos).Union(body.Polygon.GetBoundingBox(body.Position));

            // Resolve the SINGLE deepest-penetrating shape this iteration, then re-evaluate.
            // The old loop pushed out of every overlapping shape in tile-iteration (left→
            // right) order, so the resolution order — and thus the final pop-out direction
            // when several contacts disagree — depended on which side a contact sat. A body
            // wedged by three surfaces (floor + side wall + a sprout growing into it) could
            // then squeeze DOWN through the floor on one mirror side but slide clear on the
            // other. Always tackling the deepest overlap first makes push-out order-
            // independent (and symmetric), so the body pops out the shallowest face.
            bool found = false;
            float bestDepth = 0f;
            Vector2 bestMtv = Vector2.Zero;
            Vector2 bestShapeVel = Vector2.Zero;

            foreach (var shape in WorldQuery.SolidShapesInRect(chunks, bounds))
            {
                var hit = Collision.Check(body.Polygon, nextPos, 0f, shape.Polygon, shape.Position, 0f);
                if (!hit.Intersects) continue;
                if (!found || hit.Depth > bestDepth)
                {
                    found = true;
                    bestDepth = hit.Depth;
                    bestMtv = hit.MTV;
                    bestShapeVel = shape.Velocity;
                }
            }

            if (!found) break;

            var normal = Vector2.Normalize(bestMtv);
            nextPos += bestMtv + normal * Epsilon;

            // Zero the *relative* normal velocity so a body landing on a moving
            // surface emerges moving with it along the contact normal
            // For static tiles shape.Velocity is zero and this collapses to absolute.
            float vnRel = Vector2.Dot(body.Velocity - bestShapeVel, normal);
            Vector2 frictionDv = Vector2.Zero;
            if (vnRel < 0f)
            {
                body.Velocity -= vnRel * normal;
                float mag = MathF.Abs(vnRel);
                if (mag > body.LastImpulseMagnitude) body.LastImpulseMagnitude = mag;
                frictionDv = ApplyFrictionAtImpact(body, normal, bestShapeVel, ComputeFrictionForNormal(body, normal), savedForce, dt);
                if (body.Impact != null
                    && body.Impact.BounceRestitution > 0f
                    && MathF.Abs(vnRel) >= body.Impact.BounceImpulseThreshold)
                {
                    // Carry-zero already applied above (relative normal V = 0). Stack
                    // the rebound on top: net Δv = -(1+e)·vnRel·normal, i.e. body
                    // emerges moving away from the surface at e·|vnRel|.
                    body.Velocity -= vnRel * body.Impact.BounceRestitution * normal;
                }
                // Damage is no longer fired here — see ApplyContactDamage, which
                // walks body.Constraints once per StepSwept after all impulses
                // have been delivered to their per-contact LastImpulse fields.
            }

            var sd = UpdateSurfaceConstraint(body, nextPos, normal, bestShapeVel);
            // Accumulate impulse delivered TO the body through this contact: the
            // normal-resolve component (-vnRel*normal — sign matches "force on
            // body") plus any tangential friction delta.
            if (vnRel < 0f) sd.LastImpulse += -vnRel * normal;
            sd.LastImpulse += frictionDv;
        }
        return nextPos;
    }

    public static void StepSwept(
        IReadOnlyList<PhysicsBody> bodies,
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
            // Reset per-step impulse accumulator. Resolve* paths max this against
            // each collision's |vnRel|; the largest one is what callers (e.g.
            // PlayerCharacter.Update) read post-step for crush-damage dispatch.
            body.LastImpulseMagnitude = 0f;
            // Zero per-contact LastImpulse so accumulation below reflects only this
            // step. Both Maintained (solver-owned) and soft (state-owned) contacts
            // get reset — readers expect "impulse during the most recent StepSwept".
            foreach (var c in body.Constraints) c.LastImpulse = Vector2.Zero;

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
                    {
                        body.Velocity -= vnRel * sc.Normal;
                        sc.LastImpulse += -vnRel * sc.Normal;
                        float mag = MathF.Abs(vnRel);
                        if (mag > body.LastImpulseMagnitude) body.LastImpulseMagnitude = mag;
                    }
                }
                // See the matching note in Step(): friction is gated on contact
                // presence, not on the impulse condition, so soft spring contacts
                // still brake tangential motion in steady state.
                ApplyContactFriction(body, sc, savedForce, dt);
            }

            // Steering ramps: rotate velocity onto the shallowest trajectory that clears the corner
            // (derived from the polygon each step). Position is still resolved by the real tile sweep below.
            SteeringRamp.ApplyRedirect(body, dt);

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
            FloatingSurfaceDistance hitFsd = null;

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
                hitFriction = ComputeFrictionForNormal(body, swept.Normal);
                anyHit = true;
                hitFromFloating = false;
                hitFsd = null;
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
                hitFsd = fsd;
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

            // Carry-zero with absorption cap: the body would ideally lose
            // |vnRel| of inbound normal velocity (full stop along normal). But
            // the impacted tile face can only absorb so much impulse before it
            // breaks; beyond that the body keeps the surplus as continued
            // inbound velocity, which the NEXT sweep iteration sees against
            // the layer behind. Per cell, absorption capacity = impulse
            // required to (a) cross body.Impact.ImpulseThreshold and (b) drive
            // remaining HP to zero given DamagePerUnitImpulse. Summed across
            // the cells in the impact strip, that's the cap on impulse
            // delivered through this contact. If idealImpulse ≤ totalCap the
            // body absorbs fully (carry-zero, same as before); if it exceeds
            // the cap the cells break and the body plows on.
            float vnRel = Vector2.Dot(body.Velocity - hitSurfaceVel, hitNormal);
            Vector2 frictionDv = Vector2.Zero;
            float dvNormalMag = 0f;
            bool brokeThrough = false;
            if (vnRel < 0f)
            {
                float vnAbs = MathF.Abs(vnRel);
                float dvCapMag = vnAbs;

                if (!hitFromFloating && body.Impact != null)
                {
                    ComputeImpactCells(body, pos, hitNormal, chunks, _impactCellsScratch);
                    int n = _impactCellsScratch.Count;
                    if (n > 0)
                    {
                        float totalCapImpulse = 0f;
                        float dmgRate = MathF.Max(body.Impact.DamagePerUnitImpulse, 1e-6f);
                        foreach (var (gtx, gty) in _impactCellsScratch)
                        {
                            var type = chunks.GetCellType(gtx, gty);
                            float hpRemaining = MathF.Max(0f, TileDamage.MaxHPFor(type) - chunks.Damage.Get(gtx, gty));
                            totalCapImpulse += body.Impact.ImpulseThreshold + hpRemaining / dmgRate;
                        }
                        float capDvMag = totalCapImpulse / MathF.Max(body.Impact.Mass, 1e-6f);
                        if (vnAbs > capDvMag)
                        {
                            dvCapMag = capDvMag;
                            brokeThrough = true;
                        }

                        // Distribute the actually-delivered impulse across the
                        // cells and chip/break them. On a break-through pass
                        // each cell receives exactly its cap, so the accumulator's
                        // over-portion equals hpRemaining/dmgRate and the cell
                        // breaks. Below-cap passes chip per the old formula.
                        float deliveredImpulse = dvCapMag * body.Impact.Mass;
                        float perCell = deliveredImpulse / n;
                        foreach (var (gtx, gty) in _impactCellsScratch)
                        {
                            float over = chunks.Impact.AccrueAndConsume(gtx, gty, perCell, body.Impact.ImpulseThreshold);
                            if (over > 0f) chunks.DamageCell(gtx, gty, over * body.Impact.DamagePerUnitImpulse);
                        }
                    }
                }

                dvNormalMag = dvCapMag;
                body.Velocity += dvCapMag * hitNormal;
                if (dvCapMag > body.LastImpulseMagnitude) body.LastImpulseMagnitude = dvCapMag;

                frictionDv = ApplyFrictionAtImpact(body, hitNormal, hitSurfaceVel, hitFriction, savedForce, dt);

                // Bounce only on hard-stop (no break-through). A break-through
                // body is already carrying inbound momentum into the empty
                // space behind the broken cells — restitution on top would
                // double-count the redirect.
                if (!brokeThrough
                    && body.Impact != null
                    && body.Impact.BounceRestitution > 0f
                    && vnAbs >= body.Impact.BounceImpulseThreshold)
                {
                    body.Velocity -= vnRel * body.Impact.BounceRestitution * hitNormal;
                }
            }

            float dn2 = Vector2.Dot(displacement, hitNormal);
            if (dn2 < 0f)
            {
                if (brokeThrough)
                {
                    // Body retains (vnRel + dvCapMag) of inbound normal velocity
                    // (still negative — going INTO surface). Match that fraction
                    // in the displacement so the next iteration's sweep starts
                    // from a consistent position/velocity pair.
                    float retainRatio = (vnRel + dvNormalMag) / vnRel;  // ∈ (0, 1]
                    displacement -= dn2 * (1f - retainRatio) * hitNormal;
                }
                else
                {
                    displacement -= dn2 * hitNormal;
                }
            }

            // The contact this impulse is delivered through: tile-hit creates/refreshes
            // a solver-owned SurfaceDistance; FSD-hit already has the state-owned
            // contact. LastImpulse reflects the impulse actually delivered (clipped
            // by the absorption cap), not the would-be impulse.
            SurfaceContact contact = hitFromFloating
                ? hitFsd
                : UpdateSurfaceConstraint(body, pos, hitNormal, hitSurfaceVel);
            if (contact != null)
            {
                if (vnRel < 0f) contact.LastImpulse += dvNormalMag * hitNormal;
                contact.LastImpulse += frictionDv;
            }
        }

        return pos + displacement;
    }

    // Scratch list reused across ResolveChunkCollisionsSwept iterations; the
    // method is static so a static buffer keeps allocations off the hot path.
    // Not thread-safe — Step/StepSwept are single-threaded by design.
    private static readonly List<(int gtx, int gty)> _impactCellsScratch = new(4);

    // Cells touching the impact face along -normal. Mirrors the old
    // ApplyContactDamage strip query: inset along the face by 2 px so a hex
    // body's AABB-graze on adjacent columns doesn't count (mirrors
    // GroundChecker.HorizontalInset).
    private static void ComputeImpactCells(PhysicsBody body, Vector2 pos, Vector2 normal, ChunkMap chunks, List<(int gtx, int gty)> cells)
    {
        cells.Clear();
        const float probe = 1f;
        const float inset = 2f;
        var bounds = body.Polygon.GetBoundingBox(pos);
        BoundingBox strip;
        if (MathF.Abs(normal.Y) >= MathF.Abs(normal.X))
        {
            var face = bounds.InsetHorizontal(inset);
            strip = normal.Y < 0 ? face.StripBelow(probe) : face.StripAbove(probe);
        }
        else
        {
            var face = bounds.InsetVertical(inset);
            strip = normal.X < 0 ? face.StripRight(probe) : face.StripLeft(probe);
        }
        foreach (var shape in WorldQuery.SolidShapesInRect(chunks, strip))
        {
            int gtx = (int)MathF.Floor(shape.WorldCenterX / Chunk.TileSize);
            int gty = (int)MathF.Floor(shape.WorldCenterY / Chunk.TileSize);
            cells.Add((gtx, gty));
        }
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

    // Returns the SurfaceDistance that represents this collision contact — either the
    // existing constraint refreshed in place, or a freshly-added one. Callers use the
    // return value to accumulate per-step impulse onto the same contact instance.
    private static SurfaceDistance UpdateSurfaceConstraint(PhysicsBody body, Vector2 resolvedPos, Vector2 normal, Vector2 surfaceVelocity)
    {
        // Floor-pointing normals (within ~45° of straight up) get the ground-friction
        // default; walls and ceilings stay frictionless so wall-slides and head-bumps
        // don't pick up a spurious tangential coupling. Scaled per body so enemies
        // can be made slippery (slide visibly when slashed).
        float friction = (normal.Y < -0.7f ? MovementConfig.Current.GroundFriction : 0f) * body.FrictionScale;

        foreach (var c in body.Constraints)
        {
            if (c is SurfaceDistance sd && Vector2.Dot(sd.Normal, normal) > 0.9f)
            {
                sd.Position = resolvedPos;
                sd.Normal = normal;
                sd.MinDistance = Epsilon;
                sd.SurfaceVelocity = surfaceVelocity;
                sd.Friction = friction;
                return sd;
            }
        }
        // Maintained: these solver-owned resting contacts persist across frames and
        // are the only constraints a snapshot needs to capture (see PhysicsContact).
        var fresh = new SurfaceDistance(resolvedPos, normal, Epsilon)
        {
            SurfaceVelocity = surfaceVelocity,
            Friction = friction,
            Maintained = true,
        };
        body.Constraints.Add(fresh);
        return fresh;
    }

    // Apply friction at a single contact, capping the body's *relative* tangential
    // velocity reduction per step at Friction · dt. 
    private static void ApplyContactFriction(PhysicsBody body, SurfaceContact sc, Vector2 savedForce, float dt)
    {
        Vector2 dv = ApplyFrictionAtImpact(body, sc.Normal, sc.SurfaceVelocity, sc.Friction, savedForce, dt);
        sc.LastImpulse += dv;
    }

    // Same logic, callable at sweep-intercept sites where the caller has the raw
    // normal/surface-velocity/friction rather than a SurfaceContact instance.
    // Returns the velocity delta applied to the body (the tangential impulse
    // delivered through this surface, with sign matching "force on body"), so
    // callers can accumulate it onto the matching contact's LastImpulse.
    private static Vector2 ApplyFrictionAtImpact(PhysicsBody body, Vector2 normal, Vector2 surfaceVelocity, float friction, Vector2 savedForce, float dt)
    {
        if (friction <= 0f) return Vector2.Zero;

        Vector2 vRel = body.Velocity - surfaceVelocity;
        Vector2 vTangent = vRel - Vector2.Dot(vRel, normal) * normal;
        float vTangentMag = vTangent.Length();
        if (vTangentMag < 0.001f) return Vector2.Zero;

        Vector2 tangentDir = vTangent / vTangentMag;
        // If the state pushed along this tangent, leave friction off. Threshold is
        // generous (~1 N/kg) so micro-noise in AppliedForce doesn't keep friction
        // suppressed when the state is effectively idle.
        if (MathF.Abs(Vector2.Dot(savedForce, tangentDir)) > 1f) return Vector2.Zero;

        float deltaMag = MathF.Min(vTangentMag, friction * dt);
        Vector2 dv = -tangentDir * deltaMag;
        body.Velocity += dv;
        return dv;
    }

    // Floor-pointing normals (within ~45° of straight up) get the configured
    // ground-friction coefficient; walls and ceilings stay frictionless so
    // wall-slides and head-bumps don't pick up a spurious tangential coupling.
    // Scaled by the body's per-body FrictionScale (enemies use < 1 to be slippery).
    private static float ComputeFrictionForNormal(PhysicsBody body, Vector2 normal)
        => (normal.Y < -0.7f ? MovementConfig.Current.GroundFriction : 0f) * body.FrictionScale;

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
