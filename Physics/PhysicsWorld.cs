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
        const int maxIterations = 12;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            var bounds = body.Polygon.GetBoundingBox(nextPos);

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
            var resolvedBounds = body.Polygon.GetBoundingBox(nextPos);

            // Zero the *relative* normal velocity so a body landing on a moving
            // surface emerges moving with it along the contact normal — the carry.
            // For static tiles shape.Velocity is zero and this collapses to absolute.
            float vnRel = Vector2.Dot(body.Velocity - bestShapeVel, normal);
            bool brokeThrough = false;
            Vector2 frictionDv = Vector2.Zero;
            if (vnRel < 0f)
            {
                body.Velocity -= vnRel * normal;
                float mag = MathF.Abs(vnRel);
                if (mag > body.LastImpulseMagnitude) body.LastImpulseMagnitude = mag;
                frictionDv = ApplyFrictionAtImpact(body, normal, bestShapeVel, ComputeFrictionForNormal(body, normal), savedForce, dt);
                brokeThrough = TryApplyImpactDamage(body, resolvedBounds, normal, vnRel, chunks);
                if (brokeThrough && body.Impact != null)
                {
                    body.Velocity += vnRel * normal;
                    body.Velocity -= vnRel * (1f - body.Impact.NormalRetainOnBreak) * normal;
                }
            }

            if (!brokeThrough)
            {
                var sd = UpdateSurfaceConstraint(body, nextPos, normal, bestShapeVel);
                // Accumulate impulse delivered TO the body through this contact: the
                // normal-resolve component (-vnRel*normal — sign matches "force on
                // body") plus any tangential friction delta. Skipped on break-through
                // because no persistent contact represents the now-broken surface.
                if (vnRel < 0f) sd.LastImpulse += -vnRel * normal;
                sd.LastImpulse += frictionDv;
            }
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

            // Zero the *relative* normal velocity so a body landing on a moving surface
            // emerges moving with it along the contact normal — the carry. Pair it
            // with friction so the same impulse that locks normal motion also brakes
            // tangential motion (Coulomb-coupled).
            float vnRel = Vector2.Dot(body.Velocity - hitSurfaceVel, hitNormal);
            bool brokeThrough = false;
            Vector2 frictionDv = Vector2.Zero;
            if (vnRel < 0f)
            {
                // Provisionally zero the normal velocity (carry preserved). If we
                // then find this was a break-through, undo the full zero and
                // replace with the bleed-off.
                body.Velocity -= vnRel * hitNormal;
                float mag = MathF.Abs(vnRel);
                if (mag > body.LastImpulseMagnitude) body.LastImpulseMagnitude = mag;
                frictionDv = ApplyFrictionAtImpact(body, hitNormal, hitSurfaceVel, hitFriction, savedForce, dt);
                // Impact damage fires on BOTH chunk-collision and FSD-collision
                // events — the latter is how the player lands (StandingState's
                // ground FSD takes the impulse). The probe strip below the body
                // finds the actual tile cells to damage; FSDs are virtual planes
                // but the body's bounds at impact still line up with real cells.
                // Players who shouldn't break tiles just don't set body.Impact.
                brokeThrough = TryApplyImpactDamage(body, body.Polygon.GetBoundingBox(pos), hitNormal, vnRel, chunks);
                if (brokeThrough && body.Impact != null)
                {
                    // Restore the velocity we zeroed, then bleed by the configured
                    // retain factor. Net effect: body keeps NormalRetainOnBreak of
                    // its pre-impact inward velocity, surface friction still
                    // applied (Coulomb-coupled), tile is gone. Next frame's sweep
                    // continues into the now-empty space — one block per frame
                    // penetration (per the user's no-substep directive).
                    body.Velocity += vnRel * hitNormal;                              // undo carry-zero
                    body.Velocity -= vnRel * (1f - body.Impact.NormalRetainOnBreak) * hitNormal;
                }
            }

            float dn2 = Vector2.Dot(displacement, hitNormal);
            if (dn2 < 0f) displacement -= dn2 * hitNormal;

            // The contact this impulse is delivered through: tile-hit creates/refreshes
            // a solver-owned SurfaceDistance; FSD-hit already has the state-owned
            // contact. Either way, accumulate the normal + friction deltas onto it so
            // callers see consistent per-contact totals.
            SurfaceContact contact = null;
            if (!hitFromFloating && !brokeThrough)
                contact = UpdateSurfaceConstraint(body, pos, hitNormal, hitSurfaceVel);
            else if (hitFromFloating)
                contact = hitFsd;
            if (contact != null)
            {
                if (vnRel < 0f) contact.LastImpulse += -vnRel * hitNormal;
                contact.LastImpulse += frictionDv;
            }
            // Break-through: end the sweep here, body pauses at the block
            // boundary for this frame, next frame continues into the opening.
            if (brokeThrough) break;
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
    // velocity reduction per step at Friction · dt. If the state has applied a
    // tangential force this frame (walking, running, etc.), we skip — the state is
    // actively driving the body, and the contact's job is to not fight that.
    // Without a tangential applied force the friction handles both braking-when-
    // grounded and the tangential carry from moving surfaces.
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

    // Probe a thin slab along the impact face for every tile pressed against it,
    // then split the impulse-derived damage equally among them. This is what makes
    // a body landing on the boundary between two tiles damage *both* rather than
    // crediting whichever the collision solver iterated to first. The strip
    // direction mirrors TryFindContactSurface: `normal` points from surface to
    // body, so the impact face is on the -normal side.
    // Returns true iff impulse ≥ Impact.BreakThreshold AND at least one tile cell
    // actually broke this call. The resolver uses this signal to decide whether
    // to bleed normal velocity (break-through) vs zero it (chip / no-damage).
    private static bool TryApplyImpactDamage(PhysicsBody body, BoundingBox bounds, Vector2 normal, float vnRel, ChunkMap chunks)
    {
        if (body.Impact == null) return false;
        float impulse = body.Impact.Mass * MathF.Abs(vnRel);
        if (impulse <= 0f) return false;

        const float probe = 1f;
        BoundingBox strip;
        if (MathF.Abs(normal.Y) >= MathF.Abs(normal.X))
            strip = normal.Y < 0 ? bounds.StripBelow(probe) : bounds.StripAbove(probe);
        else
            strip = normal.X < 0 ? bounds.StripRight(probe) : bounds.StripLeft(probe);

        // Local list (not static): PhysicsWorld is a static class but xUnit runs
        // test classes in parallel, so a static scratch list races across
        // concurrent StepSwept calls and trips the List enumerator's version
        // check mid-foreach. The per-call alloc is one ~4-element List on the
        // rare frames a body actually impacts terrain — cheap.
        var impactCells = new List<(int gtx, int gty)>(4);
        foreach (var shape in WorldQuery.SolidShapesInRect(chunks, strip))
        {
            int gtx = (int)MathF.Floor(shape.WorldCenterX / Chunk.TileSize);
            int gty = (int)MathF.Floor(shape.WorldCenterY / Chunk.TileSize);
            impactCells.Add((gtx, gty));
        }
        if (impactCells.Count == 0) return false;

        // Each cell sees its share of the impulse routed through the
        // accumulator. The cell-level threshold + decay handles spring-padded
        // landings: even if a single frame's impulse is below threshold, the
        // accumulator integrates the spring's bleed-out and fires damage once
        // the running total crosses. AccrueAndConsume returns the over-
        // threshold portion already net of the threshold, so we just multiply
        // by the body's damage coefficient.
        bool anyBroken = false;
        float perCellImpulse = impulse / impactCells.Count;
        foreach (var (gtx, gty) in impactCells)
        {
            float over = chunks.Impact.AccrueAndConsume(gtx, gty, perCellImpulse, body.Impact.ImpulseThreshold);
            if (over > 0f && chunks.DamageCell(gtx, gty, over * body.Impact.DamagePerUnitImpulse))
                anyBroken = true;
        }
        // Break-through report: the contact face has opened AND the body had
        // enough momentum to count as plowing through (vs a soft chip-and-stop).
        return anyBroken && impulse >= body.Impact.BreakThreshold;
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
