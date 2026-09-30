using System;
using Microsoft.Xna.Framework;

namespace MTile;

// TERRAIN-AWARE NO-PENETRATION SOURCING (render-only). Builds the FACE LIST the animation
// solver's NoPenetrationConstraint folds into one potential per limb tip.
//
// For each collision-relevant TIP of the LAST-FRAME pose, scan the tile neighborhood within
// QueryRadius; every EXPOSED solid face (its neighbor cell is not Solid) whose segment lies
// within QueryRadius of the tip AND that FACES THE BODY — the physics body's centre is on the
// face's free side — is emitted as a finite SolverSurface segment carrying that tip's bone in
// BoneMask. Faces pointing away from the body are dropped: they are the far side of a block
// from where the body stands, and a limb must never be pushed out through them (it is pulled
// back through a body-facing face instead). Coplanar touching faces merge into one run
// (masks OR together). Each run end is marked CONCAVE (the solid carries on past it — the
// next cell along the face is solid) or convex, which is what lets the constraint tell
// "inside the solid past the face's end" from "in the air around the corner".
//
// Margin 0: rows fire only on actual penetration, so a foot standing ON the ground (gap = 0)
// is exactly INACTIVE. Frozen for the frame — same capture-once lifecycle as pins/contacts,
// so the solve objective stays smooth. One-frame staleness (~a few px of body motion) is well
// inside the query slack.
//
// GROWING SPROUTS are policed too. A sprout is collision-solid while it grows (ChunkMap
// emits one full-tile volume per supporting face, translating out of that parent) but its
// cell is TileState.Sprouting, never Solid — so the cell scan is blind to it. The volumes are
// axis-aligned but NOT grid-aligned, so their faces are emitted from the volume geometry
// itself (the same geometry physics collides with and ChunkRenderer draws), with faces
// backed by solid (rock or a neighbouring volume) skipped exactly like a tile's interior faces.
public static class TerrainSurfaces
{
    private const float TileSize   = Chunk.TileSize;
    private const float HalfTile   = TileSize * 0.5f;
    public  const float QueryRadius = 20f;  // ~1.25 tiles around a tip
    private const float SpanSlop    = 2f;   // lateral overhang that still counts as "over" a face (near flag)
    private const float CoplanarEps = 0.75f;
    private const float TouchEps    = 0.5f; // coplanar segments this close end-to-end merge
    private const float FaceProbe   = 4f;   // px past a sprout face when testing "is there solid behind it"


    // The rig tips the terrain polices: toes, ankles, hands, head (world[i].Translation is
    // the bone's FAR tip under the joint chain). Torso bones are deliberately absent — the
    // body proper is the physics engine's job; this keeps rows scarce and avoids a face
    // near the hip bending the whole spine.
    // The support points (EndpointResolver, role "support") are added per rig: on the stick
    // figure they ARE the lower-leg ends; on a rig with real feet they are the foot tips.
    private static readonly string[] TipNames =
        { "leg_l_lower", "leg_r_lower", "arm_l_lower", "arm_r_lower", "head" };

    // A tip within this clearance of a face can plausibly engage within one solve —
    // reported via `near` so the animator's off-locomotion static solve only runs when
    // there is real work (idle feet hovering 15px over dormant ground faces don't count).
    public  const float EngageBand = 6f;

    // Extract body-facing face segments around `anim`'s last-emitted pose into `dest`
    // (caller-owned scratch, reused every frame). Returns the count written. Call BEFORE
    // anim.Update for the frame — the tips are read from the pose drawn last frame, matching
    // the root the player saw (RigRoot: com anchor + solved offsets). `bodyPos` is the physics
    // body's centre: it decides which faces face the body.
    public static int Extract(ChunkMap chunks, CharacterAnimator anim, Vector2 bodyPos,
                              int facing, float scale, SolverSurface[] dest, out bool near)
    {
        near = false;
        if (chunks == null || anim == null || dest == null || dest.Length == 0) return 0;

        var rootPos = AttackGlowSystem.RigRoot(bodyPos, facing, anim, scale);
        int dir = facing == 0 ? 1 : facing;
        var world = anim.Pose.ComputeWorld(
            Affine2.FromTRS(rootPos, 0f, new Vector2(dir * scale, scale)));

        int count = 0;
        int tipCount = TipNames.Length, supportCount = 0;
        Span<int> tips = stackalloc int[TipNames.Length + 4];
        for (int i = 0; i < TipNames.Length; i++) tips[i] = anim.Skeleton.IndexOf(TipNames[i]);
        foreach (var sp in EndpointResolver.WithRole(anim.Skeleton, "support"))
        {
            int sb = anim.Skeleton.IndexOf(sp.Bone);
            if (sb < 0 || tipCount + supportCount >= tips.Length) continue;
            bool dup = false; for (int i = 0; i < tipCount; i++) if (tips[i] == sb) dup = true;
            if (!dup) tips[tipCount + supportCount++] = sb;
        }
        for (int ti = 0; ti < tipCount + supportCount; ti++)
        {
            int b = tips[ti];
            if (b < 0) continue;
            Vector2 q = world[b].Translation;

            int gx0 = (int)MathF.Floor((q.X - QueryRadius) / TileSize);
            int gx1 = (int)MathF.Floor((q.X + QueryRadius) / TileSize);
            int gy0 = (int)MathF.Floor((q.Y - QueryRadius) / TileSize);
            int gy1 = (int)MathF.Floor((q.Y + QueryRadius) / TileSize);
            for (int gy = gy0; gy <= gy1; gy++)
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    if (!Solid(chunks, gx, gy)) continue;
                    float cx = gx * TileSize + HalfTile, cy = gy * TileSize + HalfTile;
                    // Each exposed face: centre on the face line, n = outward unit normal.
                    for (int f = 0; f < 4; f++)
                    {
                        int nx = FaceNx[f], ny = FaceNy[f];
                        if (Solid(chunks, gx + nx, gy + ny)) continue;          // interior face
                        // Tangent t = (−n.Y, n.X); a run end is concave when the cell along
                        // ±t is solid (the solid continues past the face's end).
                        int tx = -ny, ty = nx;
                        float extHi = Solid(chunks, gx + tx, gy + ty) ? QueryRadius : 0f;
                        float extLo = Solid(chunks, gx - tx, gy - ty) ? QueryRadius : 0f;
                        Consider(dest, ref count, ref near, q, bodyPos,
                                 new Vector2(cx + nx * HalfTile, cy + ny * HalfTile), new Vector2(nx, ny),
                                 extLo, extHi, b);
                    }
                }

            // Same pass over the growing sprout volumes near this tip. Cheap: Growing is
            // empty on most frames and holds a handful of nodes on a build frame.
            var growing = chunks.ActiveSprouts;
            for (int i = 0; i < growing.Count; i++)
            {
                var s = growing[i];
                for (int k = 0; k < TileSproutNode.FaceOrder.Length; k++)
                {
                    var face = TileSproutNode.FaceOrder[k];
                    if ((s.Faces & face) == 0) continue;
                    var c = s.VolumeCenter(face);
                    if (MathF.Abs(q.X - c.X) > HalfTile + QueryRadius) continue;
                    if (MathF.Abs(q.Y - c.Y) > HalfTile + QueryRadius) continue;
                    for (int f = 0; f < 4; f++)
                    {
                        var n = new Vector2(FaceNx[f], FaceNy[f]);
                        var p = new Vector2(c.X, c.Y) + n * HalfTile;
                        if (SolidPast(chunks, p + n * FaceProbe)) continue;     // backed by solid
                        var t = new Vector2(-n.Y, n.X);
                        float extHi = SolidPast(chunks, p + t * (HalfTile + FaceProbe) - n * FaceProbe) ? QueryRadius : 0f;
                        float extLo = SolidPast(chunks, p - t * (HalfTile + FaceProbe) - n * FaceProbe) ? QueryRadius : 0f;
                        Consider(dest, ref count, ref near, q, bodyPos, p, n, extLo, extHi, b);
                    }
                }
            }
        }
        return count;
    }

    // Face order: top, bottom, left, right (y-down: top's outward normal is −y).
    private static readonly int[] FaceNx = { 0, 0, -1, 1 };
    private static readonly int[] FaceNy = { -1, 1, 0, 0 };

    private static bool Solid(ChunkMap chunks, int gx, int gy)
        => chunks.GetCellState(gx, gy) == TileState.Solid;

    // The physics point-solidity predicate verbatim (tiles AND growing sprout volumes) —
    // sprout volumes are not grid-aligned, so their neighbours are probed by point.
    private static bool SolidPast(ChunkMap chunks, Vector2 o)
        => ((ISolidShapeProvider)chunks).IsSolidAt(o.X, o.Y);

    // Keep face (centre p, normal n, half-length HalfTile) for tip q's bone when it faces the
    // body and its segment is within QueryRadius of q.
    private static void Consider(SolverSurface[] dest, ref int count, ref bool near,
                                 Vector2 q, Vector2 body, Vector2 p, Vector2 n,
                                 float extLo, float extHi, int bone)
    {
        if (n.X * (body.X - p.X) + n.Y * (body.Y - p.Y) <= 0f) return;   // faces away from the body
        var t = new Vector2(-n.Y, n.X);
        Vector2 u = q - p;
        float dn  = n.X * u.X + n.Y * u.Y;                 // signed clearance (+ = free side)
        float lat = t.X * u.X + t.Y * u.Y;
        float tc  = Math.Clamp(lat, -HalfTile, HalfTile);
        if ((u - t * tc).Length() > QueryRadius) return;
        if (dn < EngageBand && lat >= -HalfTile - extLo - SpanSlop && lat <= HalfTile + extHi + SpanSlop)
            near = true;                                   // on, near, or behind it: could fire this frame
        Emit(dest, ref count, p, n, extLo, extHi, bone);
    }

    private static void Emit(SolverSurface[] dest, ref int count, Vector2 p, Vector2 n,
                             float extLo, float extHi, int bone)
    {
        // Merge with an existing coplanar face whose span touches or overlaps this one.
        var t = new Vector2(-n.Y, n.X);
        float planeOff = n.X * p.X + n.Y * p.Y;
        float along = t.X * p.X + t.Y * p.Y;
        float lo = along - HalfTile, hi = along + HalfTile;
        for (int i = 0; i < count; i++)
        {
            var e = dest[i];
            if (e.Normal.X != n.X || e.Normal.Y != n.Y) continue;
            if (MathF.Abs((e.Normal.X * e.Point.X + e.Normal.Y * e.Point.Y) - planeOff) >= CoplanarEps) continue;
            float eAlong = t.X * e.Point.X + t.Y * e.Point.Y;
            float eLo = eAlong - e.HalfLength, eHi = eAlong + e.HalfLength;
            if (lo > eHi + TouchEps || hi < eLo - TouchEps) continue;
            float mLo = MathF.Min(lo, eLo), mHi = MathF.Max(hi, eHi);
            // Each end of the run keeps the concavity of whichever piece supplies it.
            float xLo = lo < eLo - TouchEps ? extLo : eLo < lo - TouchEps ? e.ExtLo : MathF.Max(extLo, e.ExtLo);
            float xHi = hi > eHi + TouchEps ? extHi : eHi > hi + TouchEps ? e.ExtHi : MathF.Max(extHi, e.ExtHi);
            float mid = 0.5f * (mLo + mHi);
            dest[i] = new SolverSurface(n * planeOff + t * mid, n, e.Margin, e.BoneMask | (1 << bone),
                                        0.5f * (mHi - mLo), xLo, xHi);
            return;
        }
        if (count < dest.Length)
            dest[count++] = new SolverSurface(p, n, 0f, 1 << bone, HalfTile, extLo, extHi);
        // Buffer full: silently drop — MaxSurfaces bounds the solve anyway; acceptable
        // for a render-only guard.
    }
}
