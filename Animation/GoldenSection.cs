using System;

namespace MTile;

// Derivative-free 1-D minimizer over a bracket [lo, hi]. Golden-section search:
// no derivatives (so it tolerates the kinks an animation clip has at keyframe
// boundaries) and no allocation of its own. Assumes the objective is unimodal on
// the bracket — which the locomotion solver guarantees by keeping each solve inside
// one contact-segment (see Plans/ANIMATION_LOCOMOTION_PLAN.md §5).
//
// This is the outer loop of the cadence solver: it picks the phase advance Δφ that
// minimizes contact slip. WASM-safe by construction (pure MathF, no dependency).
public static class GoldenSection
{
    private const float InvPhi = 0.6180339887f;   // 1/φ — golden-section probe ratio

    // Returns the argmin of f on [lo, hi]. Stops when the bracket narrows below tol
    // or after maxIters evaluations of the interior probes (two probes per iter, but
    // one is reused each step). The caller's f closure is the only allocation.
    public static float Minimize(Func<float, float> f, float lo, float hi,
                                 float tol = 1e-3f, int maxIters = 24)
    {
        if (hi < lo) (lo, hi) = (hi, lo);

        float a = lo, b = hi;
        float c = b - (b - a) * InvPhi;   // lower interior probe
        float d = a + (b - a) * InvPhi;   // upper interior probe
        float fc = f(c), fd = f(d);

        for (int i = 0; i < maxIters && (b - a) > tol; i++)
        {
            if (fc < fd)
            {
                b = d; d = c; fd = fc;
                c = b - (b - a) * InvPhi;
                fc = f(c);
            }
            else
            {
                a = c; c = d; fc = fd;
                d = a + (b - a) * InvPhi;
                fd = f(d);
            }
        }
        return 0.5f * (a + b);
    }
}
