using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Rigid moving-least-squares point deformation (Schaefer/McPhail/Warren, SIGGRAPH 2006,
// §2.3): given control handles at rest positions p_i and their moved positions q_i, maps
// every baked vertex v with the rotation+translation that best matches the nearby handle
// motion — the "as-rigid-as-possible" image deformation behind sprite skinning.
//
// Everything that depends only on the REST configuration (weights, weighted centroid p*,
// the per-handle 2x2 matrices A_i) is precomputed in Bake; Deform is a small per-vertex
// accumulate + one normalize, allocation-free. Render-only; never touches the sim.
//
//   f_r(v) = |v - p*| · f/|f| + q*        f = Σ_i q̂_i A_i   (q̂_i = q_i - q*, row vector)
//   A_i    = w_i (p̂_i ; -p̂_i⊥)(v - p* ; -(v - p*)⊥)ᵀ        ⊥: (x, y) → (-y, x)
public sealed class MlsDeformer
{
    // Per-vertex handle entry, flattened: which handle, its rest weight, and the
    // precomputed A_i (row-major 2x2). Entries for vertex v live in
    // [_entryStart[v], _entryStart[v + 1]).
    private readonly int[]   _entryStart;
    private readonly short[] _entryHandle;
    private readonly float[] _entryW;
    private readonly float[] _entryA;      // 4 floats per entry: a11 a12 a21 a22

    private readonly float[] _invSumW;     // per vertex: 1 / Σ w_i (rest weights are fixed)
    private readonly float[] _dist;        // per vertex: |v - p*|
    private readonly float[] _restDx, _restDy;   // per vertex: v - p* (degenerate-rotation fallback)

    public readonly int VertexCount;
    public readonly int HandleCount;

    // Regularizer added to squared distance in the weight kernel: keeps a vertex sitting
    // exactly on a handle finite while still letting that handle dominate (~1e4 x a
    // handle 1 rig-unit away). Rig-unit² — the biped is ~40 units tall.
    private const float WeightEps = 1e-4f;

    private MlsDeformer(int vertexCount, int handleCount, int[] entryStart,
                        short[] entryHandle, float[] entryW, float[] entryA,
                        float[] invSumW, float[] dist, float[] restDx, float[] restDy)
    {
        VertexCount = vertexCount; HandleCount = handleCount;
        _entryStart = entryStart; _entryHandle = entryHandle; _entryW = entryW; _entryA = entryA;
        _invSumW = invSumW; _dist = dist; _restDx = restDx; _restDy = restDy;
    }

    // Precompute the deformer for a fixed set of rest vertices against rest handles.
    // maxHandlesPerVertex prunes each vertex to its strongest-weighted handles — distant
    // handles contribute ~nothing and pruning bounds the per-frame cost. `alpha` is the
    // falloff exponent of the paper's weight kernel w = 1/d^2α: 1 = broad/soft influence,
    // 2 = tight/local (limbs stop grabbing pixels that belong to other limbs, at the
    // cost of slightly harder transitions between influence regions).
    public static MlsDeformer Bake(ReadOnlySpan<Vector2> restVerts, ReadOnlySpan<Vector2> restHandles,
                                   int maxHandlesPerVertex = 8, float alpha = 1f)
    {
        int nv = restVerts.Length, nh = restHandles.Length;
        if (nh < 2) throw new ArgumentException("MLS needs at least 2 handles.", nameof(restHandles));
        if (nh > short.MaxValue) throw new ArgumentException("Too many handles.", nameof(restHandles));
        int k = Math.Min(maxHandlesPerVertex, nh);

        var entryStart  = new int[nv + 1];
        var entryHandle = new short[nv * k];
        var entryW      = new float[nv * k];
        var entryA      = new float[nv * k * 4];
        var invSumW     = new float[nv];
        var dist        = new float[nv];
        var restDx      = new float[nv];
        var restDy      = new float[nv];

        // Scratch for one vertex's weights + selection.
        var w      = new float[nh];
        var picked = new short[k];

        for (int v = 0; v < nv; v++)
        {
            Vector2 pos = restVerts[v];
            for (int i = 0; i < nh; i++)
            {
                float dx = restHandles[i].X - pos.X, dy = restHandles[i].Y - pos.Y;
                float d2 = dx * dx + dy * dy + WeightEps;
                w[i] = alpha == 1f ? 1f / d2 : 1f / MathF.Pow(d2, alpha);
            }

            // Top-k by weight (selection by repeated max — k and nh are tiny).
            int count = 0;
            for (int pick = 0; pick < k; pick++)
            {
                int best = -1; float bw = 0f;
                for (int i = 0; i < nh; i++)
                {
                    if (w[i] <= bw) continue;
                    bool taken = false;
                    for (int j = 0; j < count; j++) if (picked[j] == i) { taken = true; break; }
                    if (!taken) { best = i; bw = w[i]; }
                }
                if (best < 0) break;
                picked[count++] = (short)best;
            }

            float sumW = 0f; Vector2 pStar = Vector2.Zero;
            for (int j = 0; j < count; j++)
            {
                float wi = w[picked[j]];
                sumW  += wi;
                pStar += restHandles[picked[j]] * wi;
            }
            pStar /= sumW;

            Vector2 d = pos - pStar;
            int e0 = entryStart[v];
            for (int j = 0; j < count; j++)
            {
                int i = picked[j];
                float wi = w[i];
                Vector2 ph = restHandles[i] - pStar;
                // A_i = w_i (p̂ ; -p̂⊥)(d ; -d⊥)ᵀ with ⊥: (x,y) → (-y,x). Rows of the left
                // matrix dotted with COLUMNS d and -d⊥ of the right (it's a transpose).
                float dPx = -d.Y, dPy = d.X;      // d⊥
                float pPx = -ph.Y, pPy = ph.X;    // p̂⊥
                int a = (e0 + j) * 4;
                entryA[a + 0] = wi * (ph.X * d.X + ph.Y * d.Y);       //  p̂ · d
                entryA[a + 1] = wi * -(ph.X * dPx + ph.Y * dPy);      //  p̂ · (-d⊥)
                entryA[a + 2] = wi * -(pPx * d.X + pPy * d.Y);        // -p̂⊥ · d
                entryA[a + 3] = wi * (pPx * dPx + pPy * dPy);         // -p̂⊥ · (-d⊥)
                entryHandle[e0 + j] = (short)i;
                entryW[e0 + j] = wi;
            }
            entryStart[v + 1] = e0 + count;
            invSumW[v] = 1f / sumW;
            dist[v]    = d.Length();
            restDx[v]  = d.X; restDy[v] = d.Y;
        }

        return new MlsDeformer(nv, nh, entryStart, entryHandle, entryW, entryA,
                               invSumW, dist, restDx, restDy);
    }

    // Evaluate the deformation for the current handle positions. result[v] is the moved
    // vertex; result may be sized larger than VertexCount (extra entries untouched).
    public void Deform(ReadOnlySpan<Vector2> posedHandles, Span<Vector2> result)
    {
        if (posedHandles.Length != HandleCount)
            throw new ArgumentException($"Expected {HandleCount} handles, got {posedHandles.Length}.");
        if (result.Length < VertexCount)
            throw new ArgumentException("Result span too small.");

        for (int v = 0; v < VertexCount; v++)
        {
            int e0 = _entryStart[v], e1 = _entryStart[v + 1];

            float qsx = 0f, qsy = 0f;
            for (int e = e0; e < e1; e++)
            {
                Vector2 q = posedHandles[_entryHandle[e]];
                qsx += _entryW[e] * q.X;
                qsy += _entryW[e] * q.Y;
            }
            qsx *= _invSumW[v]; qsy *= _invSumW[v];    // q*

            float fx = 0f, fy = 0f;
            for (int e = e0; e < e1; e++)
            {
                Vector2 q = posedHandles[_entryHandle[e]];
                float qhx = q.X - qsx, qhy = q.Y - qsy;   // q̂ (row vector)
                int a = e * 4;
                fx += qhx * _entryA[a + 0] + qhy * _entryA[a + 2];
                fy += qhx * _entryA[a + 1] + qhy * _entryA[a + 3];
            }

            float len = MathF.Sqrt(fx * fx + fy * fy);
            if (len > 1e-9f)
            {
                float s = _dist[v] / len;
                result[v] = new Vector2(qsx + fx * s, qsy + fy * s);
            }
            else
            {
                // Degenerate (all local handles coincident, or v at p*): rotation is
                // unobservable — fall back to translating the rest offset.
                result[v] = new Vector2(qsx + _restDx[v], qsy + _restDy[v]);
            }
        }
    }
}
