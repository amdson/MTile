using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Distributes an impact's kinetic energy across the tiles around the contact point, so a
// fast body opens a crater rather than punching a body-width hole.
//
// MODEL. Every solid cell is a node carrying a displacement u. Each node is tied to the
// world by a stiff ground spring (k_g) and to each solid neighbour by a coupling spring
// (k_n). The struck cell is pinned at unit displacement along the impact direction and
// the rest relax around it. A cell breaks when the energy in its own ground spring,
// ½k_g|u|², exceeds the HP it has left.
//
// Setting ∂E/∂u_i = 0 for a free node gives
//
//     u_i = β · Σ_j w_ij u_j / (1 + β · Σ_j w_ij)        β = k_n / k_g
//
// — a damped weighted average of the neighbours, i.e. exactly one Jacobi sweep. So
// relaxation IS the solver here, not an approximation of a different one, and Beta is the
// only shape parameter: it alone sets how far energy travels before it dies out.
//
// WHY IT IS CHEAP. The system is linear and its energy is quadratic and homogeneous, so
// the field for any impact is a scalar multiple of the field for a unit impact. We relax
// once at unit amplitude to get φ and its ground-spring energy E1, then rescale by
// s = √(E_impact / E1). That is exact — there is no outer loop searching for the
// displacement that absorbs the right amount of energy.
//
// AFTER THE FIELD, three rules turn it into damage:
//   * SHARE. The relaxed field is read as a distribution — cell i takes a share |φ_i|² of
//     the impact — normalised over the ground springs alone, since they are where breakage
//     is decided. Including the coupling energy parks most of the impact in springs that
//     can never break anything and then refunds it.
//   * SPILL. A cell can only absorb the HP it has. Excess is spilled once, proportionally,
//     into whatever capacity the neighbourhood has left, so a hard hit widens the crater
//     rather than handing surplus back as forward motion. One pass, so it cannot cascade.
//   * YIELD. Below YieldFraction of a cell's full strength nothing is marked, but the
//     energy is still spent — under yield a material deforms elastically and springs back,
//     it does not hand the impactor its energy back.
//
// THREE THINGS THAT FALL OUT FOR FREE.
//   * Free surfaces amplify. A cell at the surface has fewer solid neighbours, so the
//     1/(1 + β·Σw) denominator is smaller and it moves further for the same neighbour
//     sum. That is real spalling behaviour, and it means a surface slam craters wide
//     while the same energy inside a tunnel stays narrow, with no special-casing.
//   * Craters follow damage. The threshold is HP *remaining*, so a crater preferentially
//     opens along rock that is already chipped.
//   * Work is bounded by construction. Rounds hops of propagation cannot reach past
//     Radius cells, whatever the impact speed.
//
// SHAPE AND SCALING NOTES.
//   * The 8-neighbourhood is deliberate. Propagating on 4 neighbours makes the support an
//     L1 ball, so craters come out as diamonds and read as an engine artifact. Diagonal
//     springs are weighted 1/√2 for their longer rest length.
//   * Radius grows as ln(E), not √E: φ decays geometrically per hop, so the break contour
//     sits where s·φ crosses the threshold, i.e. r ∝ ln(s) ∝ ½ln(E). Doubling the energy
//     adds to the crater radius instead of multiplying it. That saturates gracefully into
//     the Radius cap rather than slamming into it.
//
// DETERMINISM. Jacobi, not Gauss-Seidel: round t+1 is computed entirely from round t, so
// the result does not depend on traversal order and replays identically under rollback.
// The iteration count is fixed and no state outside the scratch buffers is touched.
public static class ImpactSpringField
{
    // How far energy can propagate, in cells, and how many relaxation sweeps to run.
    // Rounds must be at least Radius — a Jacobi sweep advances influence exactly one hop,
    // so fewer would leave the outer ring permanently zero — but it also has to be enough
    // for the field to converge, and convergence gets slower as Beta rises. At Beta = 9,
    // five rounds is nowhere near settled and the crater comes out narrower than a much
    // softer material would give, which is the opposite of what the parameter means.
    public const int Radius = 5;
    public const int Rounds = 20;

    private const int Span = 2 * Radius + 1;
    private const int Cells = Span * Span;
    private const int Centre = Radius * Span + Radius;

    // β = k_n / k_g — the coupling-to-anchor stiffness ratio, and the only crater-shape
    // knob. Higher transmits energy further (wider, shallower); lower keeps it local
    // (narrower, deeper).
    private const float Beta = 9f;

    // Kinetic energy (½·Mass·v², in px/s and the Impact profile's mass units) per unit of
    // tile HP. Calibrated so a single Dirt cell gives way at roughly the speed it used to
    // under the old per-cell impulse cap, leaving the low-speed feel alone; the crater is
    // what the surplus above that now buys.
    private const float HpPerUnitEnergy = 1.0f / 245000f;

    // Below this share of a cell's remaining strength, an impact does nothing at all.
    // Real materials deform elastically under light load and spring back; only past the
    // yield point is the deformation permanent. Without a yield point every graze leaves
    // a mark and terrain quietly erodes under repeated footfalls and small landings — the
    // job ImpulseThreshold used to do in the old per-cell impulse model.
    private const float YieldFraction = 0.25f;

    // Ring of 8 neighbours. Diagonals are √2 further apart, so their springs are weaker
    // by the same factor.
    private static readonly int[] NeighbourDx = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] NeighbourDy = { -1, -1, -1, 0, 0, 1, 1, 1 };
    private static readonly float[] NeighbourW =
    {
        0.70710678f, 1f, 0.70710678f,
        1f,              1f,
        0.70710678f, 1f, 0.70710678f,
    };

    // Per-thread scratch: the game steps on one thread, but the test runner steps
    // independent sims in parallel. Matches PhysicsWorld's _impactCellsScratch.
    [ThreadStatic] private static Vector2[] _phi;
    [ThreadStatic] private static Vector2[] _next;
    [ThreadStatic] private static float[] _hp;
    [ThreadStatic] private static float[] _yield;
    [ThreadStatic] private static bool[] _solid;
    [ThreadStatic] private static float[] _energy;

    public readonly struct Result
    {
        // Tile HP actually consumed — the energy the terrain took out of the body,
        // expressed in HP units. Convert back with EnergyForHp.
        public readonly float HpConsumed;
        public readonly int CellsBroken;
        // Whether every cell the body was about to move into gave way. The body only
        // carries on through the gap if this is true; cells that broke off to the side
        // widen the crater but do not open a path.
        public readonly bool PathCleared;

        public Result(float hpConsumed, int cellsBroken, bool pathCleared)
        {
            HpConsumed = hpConsumed;
            CellsBroken = cellsBroken;
            PathCleared = pathCleared;
        }
    }

    public static float HpForEnergy(float energy) => energy * HpPerUnitEnergy;
    public static float EnergyForHp(float hp) => hp / HpPerUnitEnergy;

    // Relax the field around (gtx0, gty0), then chip or break everything the scaled field
    // reaches. `direction` is the unit vector the body is travelling along, `energyHp` its
    // available kinetic energy expressed in HP units, and `pathCells` the cells directly
    // in front of it (the contact silhouette) whose fate decides whether it breaks through.
    public static Result Apply(
        ChunkMap chunks, int gtx0, int gty0, Vector2 direction, float energyHp,
        System.Collections.Generic.List<(int gtx, int gty)> pathCells)
    {
        if (energyHp <= 0f) return new Result(0f, 0, false);

        var phi = _phi ??= new Vector2[Cells];
        var next = _next ??= new Vector2[Cells];
        var hp = _hp ??= new float[Cells];
        var yield = _yield ??= new float[Cells];
        var solid = _solid ??= new bool[Cells];
        var energy = _energy ??= new float[Cells];

        // ---- sample the neighbourhood -------------------------------------------------
        bool anySolid = false;
        for (int ly = 0; ly < Span; ly++)
        {
            for (int lx = 0; lx < Span; lx++)
            {
                int i = ly * Span + lx;
                int gtx = gtx0 + lx - Radius;
                int gty = gty0 + ly - Radius;
                bool isSolid = chunks.GetCellState(gtx, gty) == TileState.Solid;
                solid[i] = isSolid;
                phi[i] = Vector2.Zero;
                next[i] = Vector2.Zero;
                if (!isSolid) { hp[i] = 0f; yield[i] = 0f; continue; }
                anySolid = true;
                var type = chunks.GetCellType(gtx, gty);
                float maxHp = TileDamage.MaxHPFor(type);
                hp[i] = MathF.Max(0f, maxHp - chunks.Damage.Get(gtx, gty));
                // Yield is a property of the material, so it keys off full strength, not
                // what is left. Scaling it to remaining HP instead would shrink the
                // threshold as a tile weakens, letting ever-smaller taps finish off
                // anything already scratched — death by a thousand cuts.
                yield[i] = YieldFraction * maxHp;
            }
        }
        if (!anySolid || !solid[Centre]) return new Result(0f, 0, false);

        // ---- relax at unit amplitude --------------------------------------------------
        // The struck cell is a Dirichlet boundary pinned along the impact direction; every
        // other solid cell relaxes around it. Cells beyond the window are treated as solid
        // and held at zero, which absorbs at the edge instead of reflecting off it.
        phi[Centre] = direction;
        for (int round = 0; round < Rounds; round++)
        {
            for (int ly = 0; ly < Span; ly++)
            {
                for (int lx = 0; lx < Span; lx++)
                {
                    int i = ly * Span + lx;
                    if (!solid[i]) { next[i] = Vector2.Zero; continue; }
                    if (i == Centre) { next[i] = direction; continue; }

                    Vector2 num = Vector2.Zero;
                    float wsum = 0f;
                    for (int k = 0; k < 8; k++)
                    {
                        int nx = lx + NeighbourDx[k];
                        int ny = ly + NeighbourDy[k];
                        float w = NeighbourW[k];
                        if (nx < 0 || nx >= Span || ny < 0 || ny >= Span)
                        {
                            // Outside the window: assume solid, displacement zero.
                            wsum += w;
                            continue;
                        }
                        int j = ny * Span + nx;
                        if (!solid[j]) continue;      // empty neighbour ⇒ no spring ⇒ free surface
                        wsum += w;
                        num += w * phi[j];
                    }
                    next[i] = wsum <= 0f ? Vector2.Zero : (Beta * num) / (1f + Beta * wsum);
                }
            }
            (phi, next) = (next, phi);
        }

        // ---- normalisation ------------------------------------------------------------
        // Sum the ground-spring energy at unit amplitude. Only the ground springs matter
        // here: they are where breakage is decided, so they are the reservoir the impact
        // is shared into. The coupling springs have already done their job by shaping the
        // relaxed field, and folding their stored energy into the budget would park most
        // of the impact in springs that can never break anything and then refund it —
        // which showed up as craters that stayed small however hard you hit AND a body
        // that barely decelerated.
        float groundE = 0f;
        for (int ly = 0; ly < Span; ly++)
        {
            for (int lx = 0; lx < Span; lx++)
            {
                int i = ly * Span + lx;
                if (!solid[i]) continue;
                groundE += 0.5f * phi[i].LengthSquared();
            }
        }
        // Read the relaxed field as a distribution: cell i takes a share |phi_i|^2 of the
        // impact, and the shares sum to exactly the energy delivered.
        float e1 = groundE;
        if (e1 <= 1e-9f) return new Result(0f, 0, false);

        // Exact rescale: energy is quadratic in the field, so s² = E / E1.
        float s2 = energyHp / e1;


        // ---- share out, then spill the surplus -----------------------------------------
        // A cell can only absorb the HP it actually has. Without somewhere for the excess
        // to go it is handed straight back to the body, which at high speed means the
        // crater saturates this window and every surplus joule turns into forward motion
        // — the body drills instead of cratering. Energy that a cell cannot take should
        // go into the material around it, so spill it once, proportionally, into whatever
        // capacity the neighbourhood has left. One pass: bounded, order-independent, and
        // it cannot cascade.
        float surplus = 0f, capacity = 0f;
        for (int i = 0; i < Cells; i++)
        {
            if (!solid[i]) { energy[i] = 0f; continue; }
            energy[i] = 0.5f * s2 * phi[i].LengthSquared();
            float over = energy[i] - hp[i];
            if (over > 0f) surplus += over;
            else capacity += -over;
        }
        if (surplus > 0f && capacity > 0f)
        {
            float share = MathF.Min(1f, surplus / capacity);
            for (int i = 0; i < Cells; i++)
            {
                if (!solid[i]) continue;
                float room = hp[i] - energy[i];
                if (room > 0f) energy[i] += room * share;
            }
        }

        // ---- chip and break -----------------------------------------------------------
        float consumed = 0f;
        int broken = 0;
        for (int ly = 0; ly < Span; ly++)
        {
            for (int lx = 0; lx < Span; lx++)
            {
                int i = ly * Span + lx;
                if (!solid[i] || energy[i] <= 0f) continue;

                // The body is charged for what the rock took, whether or not the rock
                // kept a mark: energy under the yield point still goes into elastic waves
                // and heat, it just does not leave permanent damage. Refunding it instead
                // — which is what skipping the charge for sub-yield cells amounts to —
                // hands most of a spread-out impact straight back as forward motion, and
                // the body bores on through soft rock instead of stopping in its crater.
                // Only what is left once the whole neighbourhood has taken its fill
                // carries the body into the space behind.
                consumed += MathF.Min(energy[i], hp[i]);
                if (energy[i] < yield[i]) continue;   // absorbed, but elastic — no scar

                int gtx = gtx0 + lx - Radius;
                int gty = gty0 + ly - Radius;
                if (chunks.DamageCell(gtx, gty, energy[i])) broken++;
            }
        }

        // ---- did the body's path open? ------------------------------------------------
        bool pathCleared = pathCells != null && pathCells.Count > 0;
        if (pathCleared)
        {
            foreach (var (gtx, gty) in pathCells)
            {
                if (chunks.GetCellState(gtx, gty) == TileState.Solid) { pathCleared = false; break; }
            }
        }

        return new Result(consumed, broken, pathCleared);
    }
}
