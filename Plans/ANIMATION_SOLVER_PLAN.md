# Generalized Animation Solver — Plan

## 0. One-line goal

Replace the bespoke 1-DOF cadence φ-solver with a **general per-frame nonlinear
least-squares solve** over `{clip times of all active motions, a CoM offset, joint-angle
corrections}`, driven by a uniform pipeline:

```
(action state + movement state + position + environment)
        → motions + constraints
        → solve
        → joint angles + skeleton offset from CoM
```

This is the structure sketched in `Animation/notes.txt`. Everything below clarifies the
ambiguous bits, pins down the math, maps it onto the existing code, and phases the work.

---

## 1. Vocabulary (clarifying the notes)

- **`env`** — all non-state game state the animation layer may read: player body
  position/velocity, facing, grounded, and **local terrain geometry** around the player
  (for penetration/ground constraints). Render-only; the animator already never writes the
  sim, so it may read `ChunkMap` directly. This *extends* today's `CharacterAnimSample`,
  which carries only scalars (no geometry).

- **State** — an `ActionState` or `MovementState` (referred to today by the
  `MovementState`/`Action` strings on the sample). Each contributes motions + constraints.

- **Motion** — a generalization of an animation clip: a function
  `τ ↦ (pose, per-bone weights)`. A clip wrapped with an identity (its "type", e.g.
  `Run`, `Slash`) and persistent solve state (`τ`, `τ̇`). The per-bone weights are how the
  upper-body action overlay (today a masked blend) becomes just another motion.

- **Constraint** — a residual generator: given the current solve variables it emits
  weighted least-squares rows (value + Jacobian). Constraints are produced **without
  knowledge of clip time** and are instead *parametrized by* `τ` where needed (e.g. which
  foot is planted is a function of `τ` via the contact feather — see §4.2).

- **Solve variables** `x` — the augmented vector the solver optimizes each frame:
  - `τ_1 … τ_M` — one clip time per active motion (`M` ≈ 1–3).
  - `d` — the 2D **skeleton offset from the player CoM** (the rig-root placement). This is
    the "skeleton offset from CoM" named as an output in the notes; making it a *solved*
    variable retires the host's `CurrentSoleY` / hard-COM placement hack and lets the body
    rise into a run's flight phase as a constraint outcome rather than a special case.
  - `Δθ_1 … Δθ_N` — per-bone angle **corrections** on top of the blended base pose
    (`N` ≈ rig bone count, ~13). IK lives here: the motion blend is the prior, `Δθ` is the
    minimal correction that satisfies hard constraints.

`x` has ~18 dimensions for the biped — a tiny dense problem.

---

## 2. Data flow (per render frame)

```
1. env        = gather(player, chunks)                       # extend CharacterAnimSample
2. constraints = get_local_constraints(env)                  # ground/wall no-penetration
3. motions = []
   for state in active_states(player):
       motions     += get_motions(state, env)                # clip(s) + per-bone weights + identity
       constraints += get_state_constraints(state, env)      # no-slip, action-progress, …
4. warm-start: match each motion to last frame's by identity;
   carry its (τ, τ̇) as initial guess + continuity prior; fresh motions start cold.
5. base_pose(x) = weighted_blend( m.frame(τ_m) for m in motions ) then + Δθ
6. x* = solve(  Σ_k ‖ r_k(x) ‖²  )                            # Gauss–Newton / LM, warm-started
7. emit (joint angles = base_pose(x*),  root offset = d*);  store (τ*, τ̇*) per motion
8. ease live pose toward emitted pose (keep the existing per-bone smoothing) and draw at
   Body.Position + d*.
```

Step 5's blend is a per-bone shortest-path **angle** average weighted by each motion's
per-bone weight (reusing `BoneTransform.LerpAngle`), matching how the action overlay blends
today — but now it is one mechanism for all motions instead of a special masked path.

---

## 3. The solver core

### 3.1 Pose model

For bone `i`, the **base** angle is the weighted blend of the motions that touch it:

```
θ̄_i(τ) = Σ_m w_{m,i} · clipAngle_{m,i}(τ_m)   /   Σ_m w_{m,i}
θ_i(x)  = θ̄_i(τ) + Δθ_i
```

World transforms come from the existing forward pass (`SkeletonPose.ComputeWorld`) under the
root `FromTRS(Body.Position + d, 0, (facing·scale, scale))`.

### 3.2 Residuals

Each constraint contributes rows `r_k(x)` (see §4). Total objective `Σ_k ‖r_k‖²`.

### 3.3 Jacobian (fully analytic — this is why 2D is the right place to do this)

A world point `p` on the chain, and its sensitivity to each variable block:

- **Angle correction `Δθ_j`** (and, transitively, anything that moves bone `j`'s angle):
  `∂p/∂θ_j = s · perp±(p − o_j)` — the lever arm from joint `j`'s world origin `o_j` to `p`,
  rotated 90°, scaled by the global `scale`, sign set by the facing flip. Zero if `p` is not
  in bone `j`'s subtree.
- **Clip time `τ_m`**: `∂p/∂τ_m = Σ_i (∂p/∂θ_i) · w_{m,i} · clipAngle′_{m,i}(τ_m)`, where
  `clipAngle′ = WrapAngle(B_i − A_i)/span` is the clip's per-bone angular velocity in the
  current keyframe interval.
- **Root offset `d`**: `∂p/∂d = I` (a pure translation of every world point).

`JᵀJ` is ~18×18, dense, symmetric — assembled and solved per frame with no allocation.

### 3.4 Method

**Levenberg–Marquardt** (Gauss–Newton with adaptive damping):
`(JᵀJ + μ·diag) Δx = −Jᵀr`, solved by a small in-place **LDLᵀ**. 1–3 iterations per frame,
warm-started from last frame → typically 1 is enough. Pure `MathF`, zero per-frame heap.

### 3.5 The keyframe-kink caveat

Clips are only **C0** across keyframes, so `clipAngle′` (hence `∂p/∂τ`) is piecewise-constant
and **jumps at keyframe boundaries**. That discontinuity is exactly why the current code went
derivative-free (`GoldenSection`). Two acceptable fixes, pick one early:
- **Trust-region clamp**: clamp each `τ` step so it cannot cross a keyframe boundary in one
  iteration (re-linearize on the far side next iteration). Cheap, keeps the data C0.
- **C1 sampling**: interpolate keyframe angles with Catmull–Rom instead of linear, so
  `clipAngle′` is continuous. Cleaner gradients; mild change to the sampler, no re-authoring.

Recommendation: ship with the trust-region clamp (no data change), add C1 sampling later if
the cadence still feels notchy.

---

## 4. Constraint library

Each is an `IConstraint` emitting `(residual rows, Jacobian rows, weight)`. Build the
priority/units so a "hard" pin out-weighs a "soft" prior by ~10²–10³.

### 4.1 PlaybackContinuity (replaces today's momentum prior)
`r = √w · (τ_m − τ_prev,m − τ̇_prev,m·Δt)` per motion. Keeps cadence smooth; generalizes
`PhaseStepPrior`. Keep the forward-only intent as a clamp `τ̇ ≥ 0` for locomotion.

### 4.2 NoSlip (planted feet — generalizes the current cadence loss)
For each contact node, captured world target `T` at plant, feathered weight `α(τ_m)`:
`r = √(w·α(τ_m)) · ( foot_x(x) − T_x )` — **horizontal only** (the fix we just landed: the
foot's vertical arc is intrinsic and must not be fought). Depends on `τ` through both the
foot position and `α`; include both in the Jacobian column for `τ_m`.

### 4.3 FixedPoint (External contacts)
A node pinned to a fixed world point over a window (e.g. a hand on a ledge corner during a
vault). `r = √w · ( node(x) − T )`, both axes.

### 4.4 ComOffset
Tie the rig CoM (the `"com"` addition point, or a bone-mass centroid) to the player CoM
(`Body.Position`): `r = √w · ( rigCoM(x) − Body.Position )`. Mostly satisfied by `d`. Keep it
**soft** so a stance foot's NoSlip can win vertically → the body rises in flight instead of
being nailed to the CoM line.

### 4.5 NoPenetration (limbs vs ground/walls) — the env-dependent one
Sample a few points along each limb segment; for each, query a **local signed-distance field**
built once per frame from the tiles near the player. One-sided residual
`r = √w · max(0, margin − sdf(q))` pushing out along `∇sdf`. Only emits rows where active
(inequality via activation), so it costs nothing in open space. Most complex — later phase.

### 4.6 JointLimits / PosePrior (regularizers, keep the system well-posed)
- Limits: one-sided `r = √w · max(0, θ_i − hi, lo − θ_i)`.
- Prior: `r = √λ · Δθ_i` — Tikhonov term keeping corrections minimal (stay near the authored
  blend) and `JᵀJ` non-singular when constraints under-determine the pose.

---

## 5. Solver library (cross-platform — desktop DesktopGL **and** browser KNI/Blazor WASM)

Hard rule: the same source compiles under both runtimes, so **pure managed C#, zero native
deps**. No P/Invoke. This rules out the usual heavyweights — **Ceres, NLopt, OR-Tools, and
Math.NET's MKL/OpenBLAS providers** all break the web build (and you wouldn't catch it from
`MTile.Core` — only the KNI re-glob would fail).

**Recommendation: hand-roll the LM loop.** The problem is ~18 dense variables with a handful
of residual rows — a few hundred lines, no dependency, identical on both targets, and
allocation-free (which a library would not be). Building blocks:
- `System.Numerics` (`Vector2`, `Matrix3x2`) for primitives — fine in WASM (SIMD only kicks in
  under AOT, but scalar fallback is correct).
- A small in-place symmetric solver (LDLᵀ / Cholesky) for `(JᵀJ + μI)Δx = −Jᵀr`.
- The 2D analytic Jacobian from §3.3 — no autodiff, no finite differences needed.

**If a library is ever wanted** (e.g. for an offline tool, not the per-frame path):
**Math.NET Numerics** is the only one to consider — its core is pure managed and runs under
Blazor WASM (just never reference `MathNet.Numerics.MKL`). Caveat: it allocates, so it is
wrong for the zero-GC hot loop; use it only for setup/offline or pool aggressively.

**WASM perf gotchas:**
- Turn on **AOT** for the web build (`<RunAOTCompilation>true</RunAOTCompilation>`). A
  per-frame numeric solver under the default IL interpreter is ~10–20× slower; AOT closes
  most of the gap. Single biggest lever for solver-heavy code in Blazor.
- Don't *rely* on SIMD speedups (only present under AOT); rely on scalar correctness.

**Free lever:** the animator is **render-only and never feeds the sim**, so — unlike every
other solver in this codebase — it has **no determinism obligation**. Floats, variable
iteration counts, early-outs, and frame-rate-dependent warm starts are all fine.

---

## 6. Mapping onto existing code

| Today | Becomes |
|---|---|
| `CharacterAnimator.Update` (monolithic) | thin **orchestrator**: gather env → build motions+constraints → call solver → ease+draw |
| `GoldenSection` 1-D Δφ solve | retired; subsumed by the LM solve (keep the file until parity is proven) |
| `SolvePhaseStep` + contact loss | `NoSlipConstraint` (§4.2) |
| feathered `_contacts` / `RefreshContacts` | contact capture feeding NoSlip/FixedPoint targets |
| masked action-overlay blend | a `Motion` with per-bone weights (§1) |
| host `RigRoot` / `CurrentSoleY` / `"com"` anchor | `ComOffset` constraint + solved `d` (§4.4) |
| `CharacterAnimSample` (scalars) | `AnimEnv` = sample **+ local terrain geometry** |
| `AnimationSampler` / `AnimAdditionSampler` | reused as-is for `Motion.frame(τ)` and `"com"` |
| `SkeletonPose` / `Affine2` / `BoneTransform` | reused; the FK whose Jacobian we differentiate |

New types: `IMotion`/`Motion`, `MotionState` (persistent τ, τ̇, identity), `IConstraint` +
implementations, `AnimEnv`, `PoseSolver` (variable layout, residual+Jacobian assembly, LM).

---

## 7. Incremental phases (each shippable & testable)

1. **Solver skeleton, parity with today. — DONE.** `LeastSquaresSolver` (a general,
   allocation-free, box-bounded Levenberg–Marquardt with a Cholesky inner solve, pure
   `MathF`) drives the cadence over the single Δφ variable with the SAME objective
   (`NoSlip` horizontal + `PlaybackContinuity`). Gated by `GameConfig.AnimSolver`
   (default off) and `CharacterAnimator(useSolver:)`. Tests: `AnimSolverTests` (LM core
   on known problems + cadence parity vs golden-section on real `walk`/`run`, both
   directions). **Finding:** the cadence objective is **non-convex in Δφ** — a planted
   foot's horizontal track is non-monotonic over a stance arc, so the gradient at Δφ=0
   can point into the lower box wall while the true minimum is further inside the
   bracket. A purely local LM stalls there; golden-section doesn't because it searches
   the whole bracket. Phase 1 globalizes the 1-D solve with a cheap coarse seed search
   (keeping the Δφ_prev momentum warm-start as a candidate) before LM refinement. **This
   confirms the §3.5 concern is real and shapes the N-D design**: the full solver can't
   grid-search, so it must lean hard on temporal warm-starting (previous frame) plus the
   `PlaybackContinuity`/pose-prior regularizers to stay in a good basin, with a globalizer
   only for cold starts / large state changes.
2. **Add `Δθ` + PosePrior.** Introduce angle corrections and the Tikhonov prior. Verify the
   solve is a no-op (corrections ≈ 0) when no hard constraint pushes — proves well-posedness.
3. **Add `d` + ComOffset.** Move vertical placement into the solve; delete the host
   `CurrentSoleY` path. Confirm flight still reads (NoSlip vs soft ComOffset trade-off).
4. **Multi-motion blend.** Fold the action overlay into a weighted `Motion`; retire the
   masked-blend special case. Add the action **clip-time-tracks-progress** constraint.
5. **FixedPoint** (External contacts) for ledge/vault hand pins.
6. **Env + NoPenetration.** Extend the sample to carry local tiles, build the per-frame local
   SDF, add the limb no-penetration constraint. Heaviest phase; do last.
7. **JointLimits**, polish, C1 sampling if needed.

---

## 8. Testing

Mirror `MTile.Tests/Animation/CharacterAnimatorTests` — headless, deterministic scenarios:
- **Per-constraint unit tests**: each `IConstraint` residual + a **finite-difference Jacobian
  check** (analytic vs numeric within tol) — the single most valuable test for a hand-rolled
  solver.
- **Cadence parity**: walk/run advance both directions (existing tests must still pass).
- **NoSlip**: planted foot world-x variance under steady walk stays ~0.
- **Flight**: run has frames with both feet above ground once `d` is solved.
- **Convergence/perf**: residual norm drops monotonically; ≤3 iters/frame; zero allocation
  (assert via a GC-alloc probe in the hot loop).

---

## 9. Open questions / decisions to confirm

1. **Action clip time**: free variable with a strong "match action progress" constraint
   (uniform, per the notes) vs simply *pinned* (`τ = ActionTime/Duration`, not a DOF)? Pinned
   is simpler and exact; constrained is more uniform. Leaning pinned for v1.
2. **`Δθ` scope**: all bones, or only an IK-relevant subset (legs/feet/arms) to shrink the
   system and keep the torso glued to the authored blend?
3. **Keyframe kink**: trust-region clamp (no data change) vs C1 Catmull-Rom sampling. Leaning
   clamp first.
4. **rigCoM definition**: authored `"com"` point vs computed bone-mass centroid?
5. **Constraint weights**: a small named tier system (hard ≫ soft ≫ prior) — agree the tiers
   up front so tuning is legible.

---

## 10. Risks

- **Weight tuning** is where this kind of solver lives or dies; the named-tier system and
  per-constraint tests are the mitigation.
- **NoPenetration / local SDF** is real complexity and env-coupled; isolated to the last
  phase so the rest ships without it.
- **Kinks** can cause `τ` jitter; the clamp/C1 decision and the PlaybackContinuity term guard
  against it.
- **Scope/refactor size**: the phases are ordered so each is independently shippable behind a
  flag and A/B-comparable to the current animator, so we never have a long red period.
```
