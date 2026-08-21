using System;
using Microsoft.Xna.Framework;

namespace MTile;

// TERRAIN-AWARE NO-PENETRATION SOURCING (render-only). Extracts nearby EXPOSED tile faces
// as SolverSurface half-planes for the animation solver's NoPenetrationConstraint, so limb
// tips stay out of arbitrary tile terrain — not just the wall-slide wall.
//
// Method (see the pseudocode in the session/plan): for each collision-relevant TIP of the
// LAST-FRAME pose, scan the tile neighborhood within QueryRadius; every solid cell face
// whose neighbor cell is empty (an exposed face) and that the tip is laterally over emits a
// half-plane {face point, outward normal, margin 0} carrying that tip's bone in BoneMask.
// Coplanar faces from adjacent tiles merge (masks OR together). Frozen for the frame —
// same capture-once lifecycle as pins/contacts, so the solve objective stays smooth and
// the analytic Jacobian exact. One-frame staleness (~a few px of body motion) is well
// inside the query slack.
//
// Margin 0 (vs the wall-slide plane's 1.5 standoff): terrain rows fire only on actual
// penetration, so a foot standing ON the ground (gap = 0) is exactly INACTIVE — behavior
// is unchanged until something would visibly clip. Corners fall out for free: near a
// convex corner the tip is over BOTH adjacent exposed faces (CornerSlop), and two
// half-planes are the correct outer approximation.
//
// GROWING SPROUTS are policed too. A sprout is collision-solid while it grows (ChunkMap
// emits one full-tile volume per supporting face, translating out of that parent) but its
// cell is TileState.Sprouting, never Solid — so the cell scan below is blind to it and a
// limb clips straight through a block that is visibly growing into it. The volumes are
// axis-aligned but NOT grid-aligned, so their faces are emitted from the volume geometry
// itself (the same geometry physics collides with and ChunkRenderer draws), with faces
// backed by rock skipped exactly like a tile's interior faces.
//
// BURIED tips (the tip's own cell is solid) are special-cased: only the shallowest
// exposed face of THAT cell constrains them (nearest exit; near-tied non-opposing faces
// join for corners). Interior faces (solid neighbor) never emit, and other cells' faces
// never claim a buried tip — otherwise a thin wall's two opposing exposed faces cancel
// and trap the limb mid-block instead of releasing it.
public static class TerrainSurfaces
{
    private const float TileSize   = Chunk.TileSize;
    private const float HalfTile   = TileSize * 0.5f;
    public  const float QueryRadius = 20f;  // ~1.25 tiles around a tip
    private const float CornerSlop  = 2f;   // lateral overhang that still counts as "over" a face
    private const float ExitTie     = 2f;   // buried tip: emit near-tied exit faces too (corners)
    private const float CoplanarEps = 0.75f;
    private const float FaceProbe   = 4f;   // px past a sprout face when testing "is there rock behind it"


    // The rig tips the terrain polices: toes, ankles, hands, head (world[i].Translation is
    // the bone's FAR tip under the joint chain). Torso bones are deliberately absent — the
    // body proper is the physics engine's job; this keeps rows scarce and avoids a plane
    // near the hip bending the whole spine.
    private static readonly string[] TipNames =
        { "foot_l", "foot_r", "leg_l_lower", "leg_r_lower", "arm_l_lower", "arm_r_lower", "head" };

    // A tip within this clearance of a plane can plausibly engage within one solve —
    // reported via `near` so the animator's off-locomotion static solve only runs when
    // there is real work (idle feet hovering 15px over dormant ground planes don't count).
    public  const float EngageBand = 6f;

    // Extract half-planes around `anim`'s last-emitted pose into `dest` (caller-owned
    // scratch, reused every frame). Returns the count written. Call BEFORE anim.Update
    // for the frame — the tips are read from the pose drawn last frame, matching the
    // root the player saw (RigRoot: com anchor + solved offsets).
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
        Span<Vector2> facesP = stackalloc Vector2[4];
        Span<Vector2> facesN = stackalloc Vector2[4];
        Span<float>   facesD = stackalloc float[4];
        foreach (string name in TipNames)
        {
            int b = anim.Skeleton.IndexOf(name);
            if (b < 0) continue;
            Vector2 q = world[b].Translation;

            // BURIED tip (its own cell is solid): the limb must be pulled to the NEAREST
            // EXIT of that cell — emit only the shallowest exposed face (plus near-tied,
            // non-opposing faces for corners). Faces of OTHER cells never constrain a
            // buried tip, and interior faces (solid neighbor) never emit at all: two
            // opposing exposed faces of a thin wall would otherwise cancel and TRAP the
            // limb mid-block (the stuck-limb bug). A cell with no exposed face = deep
            // interior → un-policed (the physics keeps the body out; the clip will move on).
            int tgx = (int)MathF.Floor(q.X / TileSize), tgy = (int)MathF.Floor(q.Y / TileSize);
            if (chunks.GetCellState(tgx, tgy) == TileState.Solid)
            {
                int nf = ExposedFaces(chunks, tgx, tgy, q, facesP, facesN, facesD);
                if (nf == 0) continue;
                float minD = float.MaxValue;
                int best = -1;
                for (int i = 0; i < nf; i++)
                    if (MathF.Abs(facesD[i]) < minD) { minD = MathF.Abs(facesD[i]); best = i; }
                near = true;
                Emit(dest, ref count, facesP[best], facesN[best], b);
                for (int i = 0; i < nf; i++)
                    if (i != best && MathF.Abs(facesD[i]) <= minD + ExitTie &&
                        Vector2.Dot(facesN[i], facesN[best]) > -0.5f)
                        Emit(dest, ref count, facesP[i], facesN[i], b);
                continue;
            }

            // BURIED IN A GROWING SPROUT: the cell is Sprouting, not Solid, so the branch
            // above can't see it and TryEmit rejects behind-the-plane tips — a limb inside
            // a growing block would otherwise be left completely un-policed.
            int ns = SproutExits(chunks, q, facesP, facesN);
            if (ns > 0)
            {
                near = true;
                for (int i = 0; i < ns; i++) Emit(dest, ref count, facesP[i], facesN[i], b);
                continue;
            }

            int gx0 = (int)MathF.Floor((q.X - QueryRadius) / TileSize);
            int gx1 = (int)MathF.Floor((q.X + QueryRadius) / TileSize);
            int gy0 = (int)MathF.Floor((q.Y - QueryRadius) / TileSize);
            int gy1 = (int)MathF.Floor((q.Y + QueryRadius) / TileSize);
            for (int gy = gy0; gy <= gy1; gy++)
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    if (chunks.GetCellState(gx, gy) != TileState.Solid) continue;
                    float x0 = gx * TileSize, y0 = gy * TileSize;   // y-down: y0 = TOP edge
                    // Each exposed face: p on the face line, n = outward unit normal.
                    if (chunks.GetCellState(gx, gy - 1) != TileState.Solid)   // top face (up = -y)
                        TryEmit(dest, ref count, ref near, q, new Vector2(x0 + HalfTile, y0), new Vector2(0f, -1f), b);
                    if (chunks.GetCellState(gx, gy + 1) != TileState.Solid)   // bottom face
                        TryEmit(dest, ref count, ref near, q, new Vector2(x0 + HalfTile, y0 + TileSize), new Vector2(0f, 1f), b);
                    if (chunks.GetCellState(gx - 1, gy) != TileState.Solid)   // left face
                        TryEmit(dest, ref count, ref near, q, new Vector2(x0, y0 + HalfTile), new Vector2(-1f, 0f), b);
                    if (chunks.GetCellState(gx + 1, gy) != TileState.Solid)   // right face
                        TryEmit(dest, ref count, ref near, q, new Vector2(x0 + TileSize, y0 + HalfTile), new Vector2(1f, 0f), b);
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
                    TryEmitVolume(chunks, dest, ref count, ref near, q, new Vector2(c.X, c.Y - HalfTile), new Vector2(0f, -1f), b);
                    TryEmitVolume(chunks, dest, ref count, ref near, q, new Vector2(c.X, c.Y + HalfTile), new Vector2(0f,  1f), b);
                    TryEmitVolume(chunks, dest, ref count, ref near, q, new Vector2(c.X - HalfTile, c.Y), new Vector2(-1f, 0f), b);
                    TryEmitVolume(chunks, dest, ref count, ref near, q, new Vector2(c.X + HalfTile, c.Y), new Vector2( 1f, 0f), b);
                }
            }
        }
        return count;
    }

    // Exposed faces of ONE cell with their signed clearances to q (negative = q behind the
    // face, i.e. inside). Interior faces (solid neighbor) are never included.
    private static int ExposedFaces(ChunkMap chunks, int gx, int gy, Vector2 q,
                                    Span<Vector2> p, Span<Vector2> n, Span<float> d)
    {
        float x0 = gx * TileSize, y0 = gy * TileSize;
        int c = 0;
        if (chunks.GetCellState(gx, gy - 1) != TileState.Solid)
        { p[c] = new Vector2(x0 + HalfTile, y0);            n[c] = new Vector2(0f, -1f); d[c] = Dot(n[c], q, p[c]); c++; }
        if (chunks.GetCellState(gx, gy + 1) != TileState.Solid)
        { p[c] = new Vector2(x0 + HalfTile, y0 + TileSize); n[c] = new Vector2(0f, 1f);  d[c] = Dot(n[c], q, p[c]); c++; }
        if (chunks.GetCellState(gx - 1, gy) != TileState.Solid)
        { p[c] = new Vector2(x0, y0 + HalfTile);            n[c] = new Vector2(-1f, 0f); d[c] = Dot(n[c], q, p[c]); c++; }
        if (chunks.GetCellState(gx + 1, gy) != TileState.Solid)
        { p[c] = new Vector2(x0 + TileSize, y0 + HalfTile); n[c] = new Vector2(1f, 0f);  d[c] = Dot(n[c], q, p[c]); c++; }
        return c;
    }

    // Exits from the UNION of growing-sprout volumes containing q, shallowest first, then
    // any near-tied non-opposing exit (corners) — the moving-volume analogue of the
    // buried-in-tile branch. Returns 0 when q is inside no volume (the common case).
    private static int SproutExits(ChunkMap chunks, Vector2 q, Span<Vector2> p, Span<Vector2> n)
    {
        float dUp = 0f, dDn = 0f, dLf = 0f, dRt = 0f;   // depth to clear, per exit direction
        float fUp = 0f, fDn = 0f, fLf = 0f, fRt = 0f;   // the face line that exit clears
        bool inside = false;
        var growing = chunks.ActiveSprouts;
        for (int i = 0; i < growing.Count; i++)
        {
            var s = growing[i];
            for (int k = 0; k < TileSproutNode.FaceOrder.Length; k++)
            {
                var face = TileSproutNode.FaceOrder[k];
                if ((s.Faces & face) == 0) continue;
                var c = s.VolumeCenter(face);
                float l = c.X - HalfTile, r = c.X + HalfTile, t = c.Y - HalfTile, bo = c.Y + HalfTile;
                if (q.X <= l || q.X >= r || q.Y <= t || q.Y >= bo) continue;
                inside = true;
                // A multi-face sprout emits overlapping volumes on purpose, so an exit has
                // to clear ALL of the ones q is in: each direction takes the deepest.
                if (q.Y - t  > dUp) { dUp = q.Y - t;  fUp = t;  }
                if (bo - q.Y > dDn) { dDn = bo - q.Y; fDn = bo; }
                if (q.X - l  > dLf) { dLf = q.X - l;  fLf = l;  }
                if (r - q.X  > dRt) { dRt = r - q.X;  fRt = r;  }
            }
        }
        if (!inside) return 0;

        Span<float>   d  = stackalloc float[4]   { dUp, dDn, dLf, dRt };
        Span<Vector2> pp = stackalloc Vector2[4] { new(q.X, fUp), new(q.X, fDn), new(fLf, q.Y), new(fRt, q.Y) };
        Span<Vector2> nn = stackalloc Vector2[4] { new(0f, -1f), new(0f, 1f), new(-1f, 0f), new(1f, 0f) };

        // An exit into rock is no exit — and the trailing face of a growing volume sits in
        // the Solid parent it is pushing out of, which is usually the shallowest one.
        int best = -1;
        for (int i = 0; i < 4; i++)
        {
            if (BlockedDeep(chunks, pp[i], nn[i])) continue;
            if (best < 0 || d[i] < d[best]) best = i;
        }
        if (best < 0) return 0;   // boxed in on every side: leave it to the physics

        int c2 = 0;
        p[c2] = pp[best]; n[c2] = nn[best]; c2++;
        for (int i = 0; i < 4; i++)
            if (i != best && d[i] <= d[best] + ExitTie && Vector2.Dot(nn[i], nn[best]) > -0.5f
                && !BlockedDeep(chunks, pp[i], nn[i]))
            { p[c2] = pp[i]; n[c2] = nn[i]; c2++; }
        return c2;
    }

    private static void TryEmitVolume(ChunkMap chunks, SolverSurface[] dest, ref int count,
                                      ref bool near, Vector2 q, Vector2 p, Vector2 n, int bone)
    {
        if (Blocked(chunks, p, n)) return;
        TryEmit(dest, ref count, ref near, q, p, n, bone);
    }

    // Is the space just past this face solid rock? Probed a few px along the outward normal
    // because sprout volumes are not grid-aligned — a cell test on the face line itself is
    // ambiguous whenever the face happens to land on a cell boundary.
    private static bool Blocked(ChunkMap chunks, Vector2 p, Vector2 n)
    {
        Vector2 o = p + n * FaceProbe;
        return chunks.GetCellState((int)MathF.Floor(o.X / TileSize),
                                   (int)MathF.Floor(o.Y / TileSize)) == TileState.Solid;
    }

    // As Blocked, but a NEIGHBOURING SPROUT VOLUME counts as blocking too — the physics
    // point-solidity predicate verbatim. Only the buried branch pays for it (it is O(growing
    // volumes) per probe): picking an exit that merely lands in the next volume of a growing
    // wall would shove the limb sideways through the wall instead of out of it. The free-tip
    // face scan stays on the cheap tile-only test — a tip on the free side of a
    // sprout-backed face is, by construction, one the buried branch already declined.
    private static bool BlockedDeep(ChunkMap chunks, Vector2 p, Vector2 n)
    {
        Vector2 o = p + n * FaceProbe;
        return ((ISolidShapeProvider)chunks).IsSolidAt(o.X, o.Y);
    }

    private static float Dot(Vector2 n, Vector2 q, Vector2 p) => n.X * (q.X - p.X) + n.Y * (q.Y - p.Y);

    private static void TryEmit(SolverSurface[] dest, ref int count, ref bool near,
                                Vector2 q, Vector2 p, Vector2 n, int bone)
    {
        // Relevance in the face's frame: d = signed clearance off the plane (positive =
        // free side), s = lateral offset along the face. The tip must be near the plane
        // AND actually over this face's span (else a distant coplanar face would claim it).
        // d < 0 (tip behind the face) is NOT handled here: real penetration means the
        // tip's own cell is solid and the buried branch already picked its nearest exit;
        // a behind-the-plane tip in an EMPTY cell is corner-slop leakage from a
        // neighboring column and would push the limb the wrong way.
        float dxq = q.X - p.X, dyq = q.Y - p.Y;
        float d = n.X * dxq + n.Y * dyq;
        if (d > QueryRadius || d < 0f) return;
        float s = -n.Y * dxq + n.X * dyq;               // perp(n)·(q-p)
        if (MathF.Abs(s) > HalfTile + CornerSlop) return;
        if (d < EngageBand) near = true;                // margin 0 + band: could fire this frame
        Emit(dest, ref count, p, n, bone);
    }

    private static void Emit(SolverSurface[] dest, ref int count, Vector2 p, Vector2 n, int bone)
    {
        // Merge with an existing coplanar terrain plane (same normal, same line): masks OR.
        float planeOff = n.X * p.X + n.Y * p.Y;
        for (int i = 0; i < count; i++)
        {
            var e = dest[i];
            if (e.Normal.X == n.X && e.Normal.Y == n.Y &&
                MathF.Abs((e.Normal.X * e.Point.X + e.Normal.Y * e.Point.Y) - planeOff) < CoplanarEps)
            {
                dest[i] = new SolverSurface(e.Point, e.Normal, e.Margin, e.BoneMask | (1 << bone));
                return;
            }
        }
        if (count < dest.Length)
            dest[count++] = new SolverSurface(p, n, 0f, 1 << bone);
        // Buffer full: silently drop — MaxSurfaces bounds the solve anyway; acceptable
        // for a render-only guard.
    }
}
