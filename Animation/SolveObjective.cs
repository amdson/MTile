using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The solve's FORWARD PASS (workplan chunk 1.5): build the composed, corrected world pose for a
// candidate x = [Δφ, δ, d.x, Δθ…] of the frozen problem into `e`, and (for the Jacobian) the
// Δφ-channel velocities at the same phase. Pure functions of (problem, x) → PoseEval; the only
// state they touch is the eval they were handed. One place, so the residual and the analytic
// Jacobian evaluate the SAME skeleton. Order mirrors CharacterAnimator.Update's draw exactly:
// sample the base clip at φ+Δφ, paint the action overlay on top (the linear blend), then add
// the per-bone Δθ.
public static class SolveForward
{
    public static void Run(SolveProblem p, ReadOnlySpan<float> x, PoseEval e)
    {
        float t = p.TimeAt(p.Phi + x[SolveProblem.IdxPhi]);
        AnimationSampler.SampleSmooth(p.Clip, t, e.KfA, e.KfB, e.KfC, e.KfD, e.Pose);
        p.Overlays.Compose(e.Pose);                                               // overlay first (linear)
        int bones = p.Skeleton.Count;
        // Δθ is applied onto the COMPOSED pose, not the base — so the IK correction survives an
        // overlay that fully owns a bone (a vault hand owned by the ClimbHands overlay). Pre-compose
        // Δθ would be overwritten by the overlay paint's lerp and the pin couldn't bend that limb.
        for (int i = 0; i < bones; i++) e.Pose.Local[i].Rotation += x[SolveProblem.IdxTheta0 + i];   // post-compose IK
        // The root rides the candidate phase: the draw root samples the anchor at the phase the
        // pose is drawn at (runtime plan §3), so the solve must too. ∂root/∂φ is e.RootVel.
        e.T    = t;
        e.Root = RootAt(p, t);
        e.Pose.ComputeWorld(e.Root);
    }

    // The rig root for this solve at normalized time t — the draw's placement contract
    // (BodyPath.RootOffset: body − facingAndScale(c(t))), minus the solved d the rows add
    // residual-side. A clip with no anchor hangs the root at the bare body position.
    public static Affine2 RootAt(SolveProblem p, float t)
    {
        Vector2 pos = p.Body;
        if (BodyPath.TrySampleAnchor(p.Clip, t, out var c, out _)) pos += BodyPath.RootOffset(c, p.Dir, p.Scale);
        return Affine2.FromTRS(pos, 0f, new Vector2(p.Dir * p.Scale, p.Scale));
    }

    // The Δφ channel's velocities at normalized time t, read by PointJacobianColumns: the BASE
    // clip's per-bone ω_j and local-translation ṫ_j (animated Stretch), and the root's drift
    // ∂root/∂φ = RootOffset(ċ) (the anchor re-sampled at the candidate phase). Sampled once
    // per Jacobian so every block shares them.
    public static void Velocities(SolveProblem p, float t, PoseEval e)
    {
        AnimationSampler.SampleAngularVelocity(p.Clip, t, e.KfA, e.KfB, e.KfC, e.KfD,
                                               e.AngVel.AsSpan(0, p.Skeleton.Count), e.TransVel);
        e.RootVel = BodyPath.TrySampleAnchor(p.Clip, t, out _, out var dc)
            ? BodyPath.RootOffset(dc, p.Dir, p.Scale) : Vector2.Zero;
    }
}

// The COMPOSITE OBJECTIVE (§11.3) over the problem's block list: one shared forward pass, then
// each block emits its rows in list order. These two are what the LM core is handed (through
// the animator's closures over its problem + eval), and the reusable point-Jacobian primitive
// every geometric block's Jacobian is built from.
public static class SolveObjective
{
    public static int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        SolveForward.Run(p, x, e);    // e.Pose world is now the composed, corrected pose at φ+Δφ
        int row = 0;
        foreach (var c in p.Blocks) row += c.Residuals(p, e, x, r.Slice(row));
        return row;
    }

    public static void Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride)
    {
        SolveForward.Run(p, x, e);
        SolveForward.Velocities(p, p.TimeAt(p.Phi + x[SolveProblem.IdxPhi]), e);
        int row = 0;
        foreach (var c in p.Blocks) row += c.Jacobian(p, e, x, jac, stride, row);
    }

    // The one reusable gradient primitive (§11.2): the sensitivity of a world point `pt` on
    // bone `bone` to the solve variables, written into colX[v]/colY[v] (x/y component of
    // ∂pt/∂x_v). Every geometric block's Jacobian is (∂r/∂pt)·this. Covers the FK-driven channels:
    //   ∂p/∂Δθ_j = Lever(j, p)                       for each ancestor j of b
    //   ∂p/∂Δφ   = Σ_j baseBlend_j · (ω_j · Lever(j, p) + A_j · ṫ_j)  +  ∂root/∂φ
    // where ṫ_j is the clip's local-translation velocity (animated Stretch — the rabbit's
    // struts) and A_j world[j]'s linear frame: under R·T·S with S ≡ 1 (PoseData.Apply never
    // scales), t_j is applied in exactly that frame and carries the whole subtree rigidly.
    // ∂root/∂φ (e.RootVel) is the pose anchor drifting with phase — the solve root is
    // re-anchored at every candidate phase (SolveForward.RootAt) — and moves every point equally.
    // Δθ is applied to the COMPOSED pose (SolveForward.Run), so its lever is UNATTENUATED — an
    // overlay-owned bone (a vault hand) still bends under a pin. Δφ moves the BASE clip, which the
    // overlay attenuates per bone, so it keeps the baseBlend_j = Π(1−w) factor. (No overlay ⇒
    // baseBlend_j = 1 ⇒ the two channels coincide, so locomotion is unchanged.)
    // δ (IdxDy) does NOT move p in the current model — it is a residual-side vertical shift
    // (tip.Y + δ), so colX/Y[IdxDy] stay 0 and the blocks that use δ add that column
    // themselves. Requires e's world buffer + velocities current (SolveForward.Run +
    // Velocities, done once by Jacobian above). Caller supplies the column spans.
    public static void PointJacobianColumns(SolveProblem p, PoseEval e, int bone, Vector2 pt,
                                            Span<float> colX, Span<float> colY)
    {
        colX.Clear(); colY.Clear();
        float dphiX = 0f, dphiY = 0f;
        var bones = p.Skeleton.Bones;
        for (int j = bone; j >= 0; j = bones[j].Parent)
        {
            int par = bones[j].Parent;
            // R·T·S: θ_j is the OUTERMOST factor of L_j (world[j] = world[par]·R(θ_j)·T·S), so it
            // pivots about world[par]'s origin (the PARENT's joint) and acts in the parent's
            // linear frame A_p.
            Affine2 wp = par < 0 ? e.Root : e.Pose.WorldOf(par);           // A_p: parent linear frame
            Vector2 lev = Lever(wp, wp.Translation, pt);                  // ∂p/∂θ_j (exact; facing flip + scale)
            float blend = p.BaseBlend[j];   // Π(1−w) over the active overlay slots masking j
            colX[SolveProblem.IdxTheta0 + j] = lev.X;                     // ∂p/∂Δθ_j (post-compose → unattenuated)
            colY[SolveProblem.IdxTheta0 + j] = lev.Y;
            dphiX += blend * e.AngVel[j] * lev.X;                         // ∂p/∂Δφ (base clip, overlay-attenuated)
            dphiY += blend * e.AngVel[j] * lev.Y;
            Vector2 tv = e.TransVel[j];                                   // + the stretch channel A_j·ṫ_j
            if (tv.X != 0f || tv.Y != 0f)
            {
                Affine2 wj = e.Pose.WorldOf(j);
                dphiX += blend * (wj.M11 * tv.X + wj.M12 * tv.Y);
                dphiY += blend * (wj.M21 * tv.X + wj.M22 * tv.Y);
            }
        }
        // The root term is unattenuated: the anchor is the base clip's (TryComReference reads it).
        colX[SolveProblem.IdxPhi] = dphiX + e.RootVel.X; colY[SolveProblem.IdxPhi] = dphiY + e.RootVel.Y;
    }

    // The 2D rotation lever arm ∂p/∂θ for a joint whose rotation acts in the linear frame `wp`
    // (its parent's world transform) and pivots about `pivot` (under R·T·S, the parent's joint =
    // wp.Translation), evaluated at world point `p`. Exactly A·J·A⁻¹·(p − pivot) where A is wp's linear part
    // and J the 90° rotation — correct under the facing-flip reflection and any scale/squash
    // (reduces to the bare perp(p − pivot) when A is a pure rotation). Returns 0 if wp is singular.
    public static Vector2 Lever(in Affine2 wp, Vector2 pivot, Vector2 p)
    {
        float dx = p.X - pivot.X, dy = p.Y - pivot.Y;
        float det = wp.M11 * wp.M22 - wp.M12 * wp.M21;
        if (MathF.Abs(det) < 1e-12f) return Vector2.Zero;
        float inv = 1f / det;
        float wx = ( wp.M22 * dx - wp.M12 * dy) * inv;     // A⁻¹·(p − o)
        float wy = (-wp.M21 * dx + wp.M11 * dy) * inv;
        float jx = -wy, jy = wx;                            // J·(…)
        return new Vector2(wp.M11 * jx + wp.M12 * jy, wp.M21 * jx + wp.M22 * jy);   // A·(…)
    }
}
