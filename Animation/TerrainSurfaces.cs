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
// BURIED tips (the tip's own cell is solid) are special-cased: only the shallowest
// exposed face of THAT cell constrains them (nearest exit; near-tied non-opposing faces
// join for corners). Interior faces (solid neighbor) never emit, and other cells' faces
// never claim a buried tip — otherwise a thin wall's two opposing exposed faces cancel
// and trap the limb mid-block instead of releasing it.
public static class TerrainSurfaces
{
    private const float TileSize   = 16f;
    private const float HalfTile   = 8f;
    public  const float QueryRadius = 20f;  // ~1.25 tiles around a tip
    private const float CornerSlop  = 2f;   // lateral overhang that still counts as "over" a face
    private const float ExitTie     = 2f;   // buried tip: emit near-tied exit faces too (corners)
    private const float CoplanarEps = 0.75f;

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
