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
            body.Velocity += body.AppliedForce * dt;
            body.AppliedForce = Vector2.Zero;
            body.Velocity += gravity * dt;

            foreach (var c in body.Constraints)
            {
                if (c is not SurfaceContact sc) continue;
                float dist = Vector2.Dot(body.Position - sc.Position, sc.Normal);
                if (dist < sc.MinDistance)
                {
                    float vn = Vector2.Dot(body.Velocity, sc.Normal);
                    if (vn < 0f)
                        body.Velocity -= vn * sc.Normal;
                }
            }

            var nextPos = body.Position + body.Velocity * dt;
            nextPos = ResolveChunkCollisions(body, chunks, nextPos);
            body.Position = nextPos;

            var bodyBounds = body.Polygon.GetBounds(body.Position);
            body.Constraints.RemoveAll(c =>
            {
                if (c is not SurfaceDistance sd) return false;
                if (Vector2.Dot(body.Position - sd.Position, sd.Normal) > 2f * Epsilon) return true;
                return !WorldHasSurface(chunks, bodyBounds, sd.Normal);
            });
        }
    }

    private static Vector2 ResolveChunkCollisions(PhysicsBody body, ChunkMap chunks, Vector2 nextPos)
    {
        const int chunkPixelSize = Chunk.Size * Chunk.TileSize;
        const int maxIterations = 8;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            var bounds = body.Polygon.GetBounds(nextPos);
            bool anyHit = false;

            int cxMin = (int)Math.Floor((float)bounds.Left / chunkPixelSize);
            int cxMax = (int)Math.Floor((float)bounds.Right / chunkPixelSize);
            int cyMin = (int)Math.Floor((float)bounds.Top / chunkPixelSize);
            int cyMax = (int)Math.Floor((float)bounds.Bottom / chunkPixelSize);

            for (int cx = cxMin; cx <= cxMax; cx++)
            for (int cy = cyMin; cy <= cyMax; cy++)
            {
                var chunkPos = new Point(cx, cy);
                if (!chunks.TryGet(chunkPos, out var chunk)) continue;

                float chunkOriginX = cx * chunkPixelSize;
                float chunkOriginY = cy * chunkPixelSize;

                int txMin = Math.Max(0, (int)Math.Floor((bounds.Left - chunkOriginX) / Chunk.TileSize));
                int txMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((bounds.Right - chunkOriginX) / Chunk.TileSize));
                int tyMin = Math.Max(0, (int)Math.Floor((bounds.Top - chunkOriginY) / Chunk.TileSize));
                int tyMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((bounds.Bottom - chunkOriginY) / Chunk.TileSize));

                if (txMin > txMax || tyMin > tyMax) continue;

                for (int tx = txMin; tx <= txMax; tx++)
                for (int ty = tyMin; ty <= tyMax; ty++)
                {
                    if (!chunk.Tiles[tx, ty].IsSolid) continue;

                    var tilePos = TileWorld.TileCenter(chunkPos, new Point(tx, ty));
                    var hit = Collision.Check(body.Polygon, nextPos, 0f, TileWorld.TileShape, tilePos, 0f);
                    if (!hit.Intersects) continue;

                    anyHit = true;
                    var normal = Vector2.Normalize(hit.MTV);
                    nextPos += hit.MTV + normal * Epsilon;
                    bounds = body.Polygon.GetBounds(nextPos);

                    float vn = Vector2.Dot(body.Velocity, normal);
                    if (vn < 0f)
                        body.Velocity -= vn * normal;

                    UpdateSurfaceConstraint(body, nextPos, normal);
                }
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
            body.Velocity += body.AppliedForce * dt;
            body.AppliedForce = Vector2.Zero;
            body.Velocity += gravity * dt;

            foreach (var c in body.Constraints)
            {
                if (c is not SurfaceContact sc) continue;
                float dist = Vector2.Dot(body.Position - sc.Position, sc.Normal);
                if (dist < sc.MinDistance)
                {
                    float vn = Vector2.Dot(body.Velocity, sc.Normal);
                    if (vn < 0f)
                        body.Velocity -= vn * sc.Normal;
                }
            }

            body.Position = ResolveChunkCollisionsSwept(body, chunks, body.Position, body.Position + body.Velocity * dt);

            var bodyBounds = body.Polygon.GetBounds(body.Position);
            body.Constraints.RemoveAll(c =>
            {
                if (c is not SurfaceDistance sd) return false;
                if (Vector2.Dot(body.Position - sd.Position, sd.Normal) > 2f * Epsilon) return true;
                return !WorldHasSurface(chunks, bodyBounds, sd.Normal);
            });
        }
    }

    private static Vector2 ResolveChunkCollisionsSwept(PhysicsBody body, ChunkMap chunks, Vector2 pos, Vector2 targetPos)
    {
        const int chunkPixelSize = Chunk.Size * Chunk.TileSize;
        const int maxBounces = 4;

        var displacement = targetPos - pos;

        for (int bounce = 0; bounce < maxBounces; bounce++)
        {
            if (displacement.LengthSquared() < 0.001f) break;

            var sweptBounds = GetSweptBounds(body.Polygon, pos, displacement);
            bool anyHit = false;
            float minT = 1f;
            Vector2 hitNormal = Vector2.Zero;
            bool hitFromFloating = false;

            int cxMin = (int)Math.Floor((float)sweptBounds.Left / chunkPixelSize);
            int cxMax = (int)Math.Floor((float)sweptBounds.Right / chunkPixelSize);
            int cyMin = (int)Math.Floor((float)sweptBounds.Top / chunkPixelSize);
            int cyMax = (int)Math.Floor((float)sweptBounds.Bottom / chunkPixelSize);

            for (int cx = cxMin; cx <= cxMax; cx++)
            for (int cy = cyMin; cy <= cyMax; cy++)
            {
                var chunkPos = new Point(cx, cy);
                if (!chunks.TryGet(chunkPos, out var chunk)) continue;

                float chunkOriginX = cx * chunkPixelSize;
                float chunkOriginY = cy * chunkPixelSize;

                int txMin = Math.Max(0, (int)Math.Floor((sweptBounds.Left - chunkOriginX) / Chunk.TileSize));
                int txMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((sweptBounds.Right - chunkOriginX) / Chunk.TileSize));
                int tyMin = Math.Max(0, (int)Math.Floor((sweptBounds.Top - chunkOriginY) / Chunk.TileSize));
                int tyMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((sweptBounds.Bottom - chunkOriginY) / Chunk.TileSize));

                if (txMin > txMax || tyMin > tyMax) continue;

                for (int tx = txMin; tx <= txMax; tx++)
                for (int ty = tyMin; ty <= tyMax; ty++)
                {
                    if (!chunk.Tiles[tx, ty].IsSolid) continue;

                    var tilePos = TileWorld.TileCenter(chunkPos, new Point(tx, ty));
                    var swept = Collision.Swept(body.Polygon, pos, 0f, displacement, TileWorld.TileShape, tilePos, 0f);
                    if (!swept.Hit || swept.T > minT) continue;

                    minT = swept.T;
                    hitNormal = swept.Normal;
                    anyHit = true;
                    hitFromFloating = false;
                }
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
                anyHit = true;
                hitFromFloating = true;
            }

            if (!anyHit) break;

            // Already overlapping at start of step: fall back to discrete push-out at target position.
            if (hitNormal == Vector2.Zero)
            {
                pos = ResolveChunkCollisions(body, chunks, pos + displacement);
                displacement = Vector2.Zero;
                break;
            }

            pos += displacement * minT + hitNormal * Epsilon;
            displacement *= 1f - minT;

            float vn = Vector2.Dot(body.Velocity, hitNormal);
            if (vn < 0f) body.Velocity -= vn * hitNormal;

            float dn2 = Vector2.Dot(displacement, hitNormal);
            if (dn2 < 0f) displacement -= dn2 * hitNormal;

            if (!hitFromFloating)
                UpdateSurfaceConstraint(body, pos, hitNormal);
        }

        return pos + displacement;
    }

    private static Rectangle GetSweptBounds(Polygon polygon, Vector2 pos, Vector2 displacement)
    {
        var b0 = polygon.GetBounds(pos);
        var b1 = polygon.GetBounds(pos + displacement);
        int left   = Math.Min(b0.Left,   b1.Left);
        int top    = Math.Min(b0.Top,    b1.Top);
        int right  = Math.Max(b0.Right,  b1.Right);
        int bottom = Math.Max(b0.Bottom, b1.Bottom);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    private static void UpdateSurfaceConstraint(PhysicsBody body, Vector2 resolvedPos, Vector2 normal)
    {
        foreach (var c in body.Constraints)
        {
            if (c is SurfaceDistance sd && Vector2.Dot(sd.Normal, normal) > 0.9f)
            {
                sd.Position = resolvedPos;
                sd.Normal = normal;
                sd.MinDistance = Epsilon;
                return;
            }
        }
        body.Constraints.Add(new SurfaceDistance(resolvedPos, normal, Epsilon));
    }

    private static bool WorldHasSurface(ChunkMap chunks, Rectangle bodyBounds, Vector2 normal)
    {
        const int chunkPixelSize = Chunk.Size * Chunk.TileSize;
        const int probe = 2;

        // Thin strip just beyond the body's face in the -normal direction.
        Rectangle strip;
        if (MathF.Abs(normal.Y) >= MathF.Abs(normal.X))
            strip = normal.Y < 0
                ? new Rectangle(bodyBounds.Left, bodyBounds.Bottom, bodyBounds.Width, probe)
                : new Rectangle(bodyBounds.Left, bodyBounds.Top - probe, bodyBounds.Width, probe);
        else
            strip = normal.X < 0
                ? new Rectangle(bodyBounds.Right, bodyBounds.Top, probe, bodyBounds.Height)
                : new Rectangle(bodyBounds.Left - probe, bodyBounds.Top, probe, bodyBounds.Height);

        int cxMin = (int)Math.Floor((float)strip.Left / chunkPixelSize);
        int cxMax = (int)Math.Floor((float)strip.Right / chunkPixelSize);
        int cyMin = (int)Math.Floor((float)strip.Top / chunkPixelSize);
        int cyMax = (int)Math.Floor((float)strip.Bottom / chunkPixelSize);

        for (int cx = cxMin; cx <= cxMax; cx++)
        for (int cy = cyMin; cy <= cyMax; cy++)
        {
            var chunkPos = new Point(cx, cy);
            if (!chunks.TryGet(chunkPos, out var chunk)) continue;

            float chunkOriginX = cx * chunkPixelSize;
            float chunkOriginY = cy * chunkPixelSize;

            int txMin = Math.Max(0, (int)Math.Floor((strip.Left - chunkOriginX) / Chunk.TileSize));
            int txMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((strip.Right - chunkOriginX) / Chunk.TileSize));
            int tyMin = Math.Max(0, (int)Math.Floor((strip.Top - chunkOriginY) / Chunk.TileSize));
            int tyMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((strip.Bottom - chunkOriginY) / Chunk.TileSize));

            if (txMin > txMax || tyMin > tyMax) continue;

            for (int tx = txMin; tx <= txMax; tx++)
            for (int ty = tyMin; ty <= tyMax; ty++)
                if (chunk.Tiles[tx, ty].IsSolid) return true;
        }

        return false;
    }
}
