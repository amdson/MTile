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

Recommendation (original): ship the trust-region clamp first. **UPDATE — SHIPPED THE C1
SAMPLING instead** (`AnimationSampler.SampleSmooth` + `SampleAngularVelocity`, non-uniform
Catmull-Rom; see §5 Phase-5 follow-up): ω is now continuous, so the analytic Jacobian is exact
across keyframe boundaries and no trust-region clamp is needed. The runtime samples this
spline everywhere; only the **editor** still samples linearly (a WYSIWYG gap, not a solver one).

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

   **Follow-up finding (parity test retired).** Per-frame logging of both paths showed
   they agree exactly for the first cycle, then diverge ONLY at the once-per-stride
   contact handoff, where the objective is **bimodal**: a global min at small Δφ (foot
   stays planted, cost ≈ 0.006) and a spurious local min at large Δφ (cost ≈ 0.27, 45×
   worse — the "next foot plants at its target" alias, mostly continuity-penalty). The LM
   path (seed search + local refine) finds the **global** min; **golden-section, assuming
   unimodality, lands in the wrong basin** and takes a ~0.18 catch-up jump every stride.
   So the "parity gap" (solver advanced ~0.80–0.83× golden) was **golden over-advancing
   via a unimodality violation, not the solver under-advancing** — the general solver is
   the *better* minimizer of the stated objective. The brittle `0.85–1.15` parity test
   (which treated golden as ground truth, and won't apply once more variables are added
   anyway) was **removed**. Open question it surfaces: the NoSlip objective only pins the
   *planted* foot, so cadence rate vs body speed is under-constrained — a body-speed /
   stride-length coupling term may be wanted later (relates to §4.4 ComOffset).
2. **Add `Δθ` + PosePrior. — DONE.** `SolvePhaseStepLm` now solves over the full vector
   `x = [Δφ, Δθ_0 … Δθ_N]`: the clip is sampled at φ+Δφ, each bone's rotation gets its Δθ
   before FK, and the residuals add one Tikhonov row `√PosePrior · Δθ_i` per bone. The
   solved corrections are applied onto the authored base pose in `Update` (the IK channel,
   wired through but inert). Δφ alone still carries the cadence, so the only thing on most
   Δθ is the prior. **Well-posedness proof** (`Solver_AngleCorrections_StayNegligible`):
   bones with no contact in their subtree (chest/head/arms) have zero slip-coupling → the
   normal equations decouple them (JᵀJ diagonal = λ) → they collapse to **exactly 0**
   (< 1e-4, λ-independently); leg/foot bones carry only a small bounded IK trim of the
   residual slip (peak ~0.05 rad on walk, well inside the ±`AngleCorrLimit` box). Constants:
   `PosePrior = 25`, `AngleCorrLimit = 0.6`. **Note (tier system, §9.5 open):** the contact
   no-slip weight is only 1 while `PosePrior = 25` — the "hard" pin is not yet hard relative
   to the priors. It doesn't bite now (Δφ handles cadence), but a real hard constraint that
   *requires* Δθ (NoPenetration, FixedPoint) will need a proper weight tier so it can
   override the prior. Decide the named tiers before Phase 5/6.
3. **Add `d` + ComOffset. — DONE (vertical; horizontal deferred).** The solve gained a
   vertical root offset **δ** (`x = [Δφ, δ, Δθ…]`): a HARD per-contact ground row
   `√w·(footY + δ − target.Y)` holds the planted foot at the height it planted (so the
   body bobs to keep it down instead of the foot sinking through the pose), and a SOFT
   `√ComWeightY·δ` row pulls δ→0 so a no-contact **flight** frame settles back to the com
   baseline (both feet free to leave the ground — §4.4's "soft com lets NoSlip win
   vertically"). The solver-path root bakes in the com baseline (`rootY = BodyY −
   com.Y·scale`) so capture/solve/draw share one frame; the host (`AttackGlowSystem.
   RigRoot`, com branch) adds the solved `CharacterAnimator.VerticalOffset` on top. Golden
   path and flight frames → δ = 0 → exactly today's com anchor (zero-risk). Constants
   `ComWeightY = 0.05` (≪ contact weight 1), `VertOffsetLimit = 24px`. Test
   `Solver_VerticalOffset_EngagesInStance_ReleasesInFlight`: over a run, δ both engages in
   stance and releases to exactly 0 in flight, bounded well inside the box. To see it live:
   `"AnimSolver": true` in `game_config.json` (the MTile.Demo editor plays raw clips, no δ).
   **Deferred:** (a) the horizontal `d.x` — left out to avoid a Δφ/d.x cadence redundancy
   (a free d.x could absorb horizontal no-slip and stall the leg cycle); add it with a
   strong horizontal ComOffset when there's a reason (lean, non-locomotion). (b) Removing
   the `CurrentSoleY` sole-hack for *no-com* clips needs an absolute ground line
   (`BodyY + 2·Radius`) in the solve — i.e. env/`groundY` on the sample (Phase 6 territory);
   δ is only wired through the com branch for now. **Caveat:** the run's small *visual*
   foot clearance is a clip/rig limit (no heel-tuck DOF), not a placement bug — δ changes
   the bob, not the authored foot heights; revisit via IK (Δθ) or re-authoring.
4. **Multi-motion blend. — DONE.** The pose is now composed from ordered MOTIONS: motion 0
   is the full-body base (locomotion/idle/one-shot + Δθ), and overlays are painted on top
   by per-bone opacity via the one reusable primitive `PaintMotionLayer(acc, layer,
   weight[])`. The action overlay became "just another motion with per-bone weights" — its
   opacity is `Region mask × eased ActionWeight` — retiring the hard-coded masked-lerp
   special case. The action **clip time is pinned** (τ = ActionTime/Duration, sampled at
   deterministic sim time), per the §9 Q1 lean toward pinned-not-DOF, so no new solve
   variable. Behavior-preserving refactor: all of `ActionOverlayTests` (incl. the
   bit-identical-lower-body guard) and the solver/cadence suites stay green. Base is kept
   as the accumulator (motion 0 implicit, no per-frame copy); the multi-motion generality
   is scaffolded but only exercised once there's a 2nd overlay or a locomotion-clip
   cross-fade (walk↔run transitions — future, not in this phase's scope).
4.5. **Solve the POST-BLEND skeleton. — DONE.** The cadence solve now optimizes the
   *composed* pose (base + Δθ + action overlay), not the bare locomotion clip. The overlay
   is resolved BEFORE the solve (new Update "step 1.5": bind clip, sample `_actionPose` at
   pinned τ, ease `ActionWeight`, fill `_overlayWeight`), then composed inside the forward
   pass via one shared primitive `ComposeOverlays(pose)` — called from the contact-target
   capture (`RefreshContacts`), the LM residual, and the golden `Loss`, so capture and
   measurement live in the *same* composed frame (else a constant offset). The forward pass
   itself is factored into `BuildSolvePose(x)` (sample φ+Δφ → +Δθ → compose → FK), the single
   substrate the residual and the coming analytic Jacobian share. **Behavior-preserving today**
   because every overlay is UpperBody-masked (`w=0` on the feet) so the composed feet ≡ base
   feet — all of `ActionOverlayTests` + the solver suites stay green. It becomes load-bearing
   the moment an overlay moves a constrained bone (a kick): the planted foot then stays pinned
   as the overlay blends in. **Why linearity matters:** overlay pose + weights are constant
   w.r.t. the solve vars, so the blend is a per-bone linear pre-multiply `(1−w_i)` on the base
   layer — exactly the `w_{m,i}` factor §3.3 already carries in `∂p/∂τ_m`. (No test yet
   exercises a leg-affecting overlay — none exists; add one with the first such clip.)
4.6. **Overlay STACK (1 base + N overlays). — Phase A DONE (mechanism); Phase B DONE
   (first movement overlay: parkour hands).** Co-occurring actions (parkour hands + a slash) need more than one
   overlay, but there is only one Action FSM, so the single `_boundAction*` overlay generalized
   to an ordered `OverlaySlot[]` (cap 3): **slot 0 is PRIVILEGED** — the Action-FSM overlay that
   still drives `ActionWeight`/`OverlayActive`/the knife pose (so the glow + the 6
   `ActionOverlayTests` + the upper-body stiffness ramp are bit-identical) — and slots 1+ are
   movement-sourced. Composition is **absolute blend + disjoint masks** (the chosen strategy):
   `ComposeOverlays` paints active slots foreground-last (slot 0 wins a shared bone); disjoint
   masks compose trivially, a shared bone resolves by paint order × weight (design the
   contending bones — e.g. the two arms — to be separable; let weights handle the rest). The key
   property that keeps the analytic Jacobian intact: sequential lerps leave the base layer's
   coefficient as the per-bone **product** `Π(1−w)` over the slots masking it, cached each frame
   in `_baseBlend[j]` — order-independent, so `CadenceJacobian` just reads it instead of the old
   scalar `1−_overlayWeight[j]`. With disjoint masks at most one factor is <1 per bone, reducing
   to the single-overlay case. Each slot eases its own weight and samples its own pose at its own
   τ; slots are matched to requests by key so an in-progress fade stays put. **Phase A** (this
   refactor) is behavior-preserving — `ResolveMovementOverlays` returns nothing yet, so only slot
   0 is ever active; full suite + KNI build green. **Phase B** (when parkour clips exist): fill
   `ResolveMovementOverlays` — animation-side policy, name-convention lookup against
   `_actionClips`, τ derived from MOVEMENT DATA (vault progress is input-driven — body-vs-corner,
   not a clock), carried via a proposed generic `float MovementProgress ∈ [0,1]` the guided state
   fills from its own geometry. Parkour then exports two clips: a Lower/FullBody locomotion base
   (φ-driven, cadence-synced) + an UpperBody hands overlay (slot 1, τ = MovementProgress).
   INVARIANT for the constraint phases: a pinned/constrained bone (the parkour planted hand →
   FixedPoint, Phase 6) must be owned by exactly ONE slot, else its `(1−w)` is ambiguous —
   naturally satisfied since a co-occurring slash uses the OTHER arm.
   **Phase B (parkour, DONE):** `ResolveMovementOverlays` binds a `VaultHands` UpperBody clip by
   name when `MovementState` contains "Parkour", τ = `s.MovementProgress`. The τ is SPATIAL, not
   a clock: `MovementState.AnimationProgress` (new virtual, default 0) is overridden by
   `ParkourState` to the body's projected travel from the entry point toward the live ledge
   corner (`_overRamp.Corner`) — input-driven, derived from deterministic body/world data each
   `Update`, render-read-only (the sim never branches on it). `CharacterAnimSample` gained
   `MovementProgress` (= `CurrentState.AnimationProgress`). Clips: `SkeletonStates/vaulthands.json`
   (UpperBody reach→push) + a rewritten `vault.json` legs base (the old one was a corrupted
   capture). Both are FIRST-DRAFT poses authored blind — tune in the editor. NOTE the editor still
   samples linearly (Animation/TODO.md), and the legs base plays by ClipTime while the hands play
   by spatial progress (the two timelines are intentionally independent).
5. **Analytic Jacobian (replace finite differences). — DONE.** `LeastSquaresSolver.Minimize`
   gained an optional `JacobianFn(x, jac, stride)`; when supplied it fills `_jac` (after the
   solver zeroes the active m×n block) instead of the per-variable FD loop, which stays as the
   fallback/oracle. The cadence solve passes `CadenceJacobian`, the closed form of §3.3. **The
   key simplification** (verified in the data): rotation is the ONLY authored channel
   (`PoseData` — translation/scale are rig-bind constants) and the overlay blend is a per-bone
   linear premultiply, so the whole composed pose is driven by per-bone rotations — every
   contact-row column is a rotation lever arm. `CadenceJacobian` reuses `BuildSolvePose(x)`'s
   world buffer for joint origins and, per ancestor joint `j` of a contact bone: `∂tip/∂Δθ_j =
   (1−w_j)·L_j` and `∂tip/∂Δφ = Σ_j (1−w_j)·ω_j·L_j`, where `L_j = ∂tip/∂θ_j` is the EXACT 2D
   lever `A_p·J·A_p⁻¹·(tip − o_p)` (`Lever()`; `A_p`/`o_p` = the joint's parent world linear
   part/origin — exact under the facing-flip reflection and any bind shear, reduces to
   `perp(tip − o)` for a pure rotation) and `ω_j` is the base clip's per-bone angular velocity
   from the new `AnimationSampler.SampleAngularVelocity` (keyframe-interval `WrapAngle(B−A)/span`).
   `∂/∂δ` and the three priors are constant columns. **Validation** (`Solver_AnalyticJacobian_
   MatchesFiniteDifference`, walk/run × both facings): analytic vs central-difference agree to
   the float32 oracle floor (~0.1% relative; the oracle needs an origin-shift to dodge
   catastrophic cancellation in `tip − target` far from the world origin, and a relative metric
   since a real structural error would be O(10–100%)). The well-posedness and vertical-offset
   tests still pass with analytic `J` driving LM. **Keyframe-kink (§3.5):** `ω` is piecewise-
   constant and jumps at keyframe boundaries; for now LM's accept/reject damping + the Δφ seed
   search absorb cross-boundary steps (as FD did) — the explicit trust-region clamp / C1
   sampling is deferred until cadence feel demands it (the validation simply skips the Δφ column
   at a boundary). FD stays in as the permanent cross-check oracle.

   **Follow-up — keyframe kink FIXED via C1 sampling (the §3.5 "C1 sampling" option, done).**
   Rather than the trust-region clamp, the rotation channel is now interpolated with a cubic
   **Hermite/Catmull-Rom** spline (`AnimationSampler.SampleSmooth` + the matching exact
   derivative `SampleAngularVelocity`), tangents = the AVERAGE of the adjacent secant slopes
   (non-uniform Catmull-Rom). ω is now continuous, so the analytic Jacobian is exact across
   boundaries and the cadence has no per-keyframe velocity step. Boundary policy keyed on
   `IsCyclic` (cached: `Loop` AND first==last keyframe pose — locomotion duplicates the seam;
   action clips default `Loop=true` but start≠end so read as one-shots): a **cyclic** clip
   WRAPS tangents across the seam (velocity continuous through φ:1→0 — the one-shot zero-clamp
   in `SampleAngularVelocity` is gated off for cyclic, else the seam read 0); a **one-shot**
   ZEROS its end tangents (ease in/out, continuous with the held clamp). Only the rotation
   channel splines (translation/scale are bind constants); four scratch poses for the keyframe
   quad. Visual effect: smoother in-betweens for ALL clips, keyframes unchanged (Catmull-Rom
   can mildly overshoot a sharply-keyed bone). The FD-vs-analytic test now passes WITHOUT a
   boundary skip in spirit — it keeps a per-column FD step (small h for Δφ through the
   high-curvature spline, larger h for δ/Δθ to clear cancellation) and still skips only where
   the ±h step straddles a boundary, since there acceleration jumps (C1, not C2) make the FD
   *oracle* unreliable though the analytic stays exact. NOTE: the editor still samples linearly
   (`SampleNormalized` unchanged) — a WYSIWYG gap to close if it matters.
6. **FixedPoint** (External contacts) for ledge/vault hand pins. — **DONE** (§11.6 Phase 2).
7. **Env + NoPenetration.** — **v1 DONE** (§11.6 Phase 3): HALF-PLANE no-penetration from
   already-resolved surfaces on the sample (`SolverSurface`), wall-slide wall wired. STILL TO DO:
   the general version — carry local tiles, build the per-frame local SDF, no-penetration vs
   arbitrary terrain. Heaviest piece; last.
8. **JointLimits**, polish. (C1 Catmull-Rom sampling already shipped — §5 follow-up; not pending.)

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
   system and keep the torso glued to the authored blend? — **RESOLVED: all bones** (the
   Tikhonov prior + JᵀJ decoupling already pin untouched bones to 0; see §2 Phase 2, §11.1).
3. **Keyframe kink**: trust-region clamp (no data change) vs C1 Catmull-Rom sampling. —
   **RESOLVED: C1 sampling, done** (§5 follow-up).
4. **rigCoM definition**: authored `"com"` point vs computed bone-mass centroid? — **RESOLVED:
   authored `"com"` point** (§4.4; every clip now carries one — see project COM-anchor notes).
5. **Constraint weights**: a small named tier system (hard ≫ soft ≫ prior) — agree the tiers
   up front so tuning is legible. — **RESOLVED: named tiers in §11.4.**

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

---

## 11. Composite-constraint refactor — concrete plan (decisions locked 2026-06-24)

The "big change": finish the solver by (a) making the **Δθ** and **d** channels *load-bearing*
under real constraints, and (b) replacing the two monolithic `CadenceResiduals` /
`CadenceJacobian` methods with a **composable constraint library**. (a) is the two extra
variable-sets — "deviation of the skeleton from the com point" (= `d`) and "deviation of the
skeleton angles from the cadence pose" (= `Δθ`); (b) is the "composite of sub-functions +
better gradient machinery". Both channels already EXIST in `x` with exact analytic Jacobian
columns — today they're just inert (only the Tikhonov prior touches Δθ; δ is the only live `d`
component). This phase gives them constraints that demand them and a library to express those.

### 11.1 Variable layout
`x = [Δφ, d.y, Δθ_0 … Δθ_{N-1}]` — unchanged from today (δ ≡ d.y). **d.x is DEFERRED**
(decision): the fixed-point / no-penetration goals are met by Δθ + the vertical δ, and a free
d.x reintroduces the d.x↔Δφ no-slip redundancy (§3 Phase 3) that stalls the leg cycle. Add d.x
+ a strong horizontal ComOffset only when a whole-body horizontal shift is needed (lean,
wall push-off).

### 11.2 The one gradient primitive (the "better gradient machinery")
Factor the ancestor-walk currently inlined in `CadenceJacobian` into a reusable routine — the
sensitivity of a world point `p` on bone `b` to the solve vars:
```
PointJacobian(bone b, world point p) :
    ∂p/∂Δθ_j = baseBlend_j · Lever(j, p)         (each ancestor j of b; 0 otherwise)
    ∂p/∂Δφ   = Σ_j baseBlend_j · ω_j · Lever(j, p)
    ∂p/∂d.y  = (0, 1)
```
Every geometric constraint is a function of world points, so its Jacobian factors by the chain
rule as `(∂r/∂p) · PointJacobian(b, p)`: NoSlip = the x-row; FixedPoint = both rows;
NoPenetration = `−n·(…)` (half-plane normal) / `−∇sdf·(…)`; ComOffset = `∂rigCoM/∂x`. No
constraint re-derives FK gradients. `Lever()` + `SampleAngularVelocity` (ω) stay as the shared
kernels they already are.

### 11.3 Constraint API (decision: IConstraint instances)
```
interface IConstraint {
    int  PrepareRows(...);                         // stable count for THIS solve (frozen before Minimize)
    void Residuals(in SolveCtx, Span<float> r);
    void Jacobian (in SolveCtx, Span<float> jac, int stride, int rowBase);
}
```
Preallocated, mutated per frame (same lifecycle as today's `_contacts` → zero per-frame alloc,
KNI/WASM-clean). Orchestrator per solve: one shared `BuildSolvePose(x)` → expose the world
buffer + `baseBlend` + `ω` via a `SolveCtx` → walk the constraint list to assemble the m×n
system. `LeastSquaresSolver` is untouched. Inequality constraints (NoPenetration) freeze their
*active* row set in `PrepareRows` (before `Minimize`) to honor the stable-row-count contract.
Today's `CadenceResiduals`/`CadenceJacobian` become the assembly loop over the list.

### 11.4 Weight tiers (resolves §9.5)
Named tiers, rescaled so a hard pin beats the priors (today INVERTED: NoSlip=1 < PosePrior=25,
fine while Δφ carries cadence but fatal once a hard constraint needs Δθ):
`HARD ~1e3 (FixedPoint, active NoPenetration) ≫ CONTACT ~1 (NoSlip, ground) ≫ SOFT ~0.05
(ComOffset) ≫ PRIOR ~λ (Tikhonov, JointLimits)`.

### 11.5 NoPenetration v1 (decision: half-planes from known contacts)
v1 emits one-sided HALF-PLANE residuals from surfaces the movement layer ALREADY resolved
(wall-slide wall normal+pos via `EnvironmentContext`/`WallSlidingState`; the ground line). The
only plumbing is passing those few already-computed surfaces onto `CharacterAnimSample` (a
handful of scalars/vectors) — NOT the `ChunkMap`/local-SDF geometry the general version needs.
For a few sample points `q` along each limb segment:
`r = √w · max(0, margin − n·(q − p0))`, active rows frozen per solve, Jacobian `−√w · n ·
PointJacobian(b, q)`. The full local-SDF version (§4.5, arbitrary terrain) is a later phase.

### 11.6 Phase order (each behind `AnimSolver`, A/B-able)
1. **Refactor — DONE.** Monolith → `IConstraint` blocks + the shared `PointJacobianColumns`
   primitive, all in the partial `CharacterAnimator.Constraints.cs`. Behavior-identical; FD-vs-
   analytic + vertical-offset tests green.
2. **Tiers + FixedPoint — DONE.** `ExternalPin` on the sample → `FixedPointConstraint` (both-axis
   HARD pin); Δθ is now load-bearing (the pinned hand reaches via limb bend). **Two findings that
   reshaped the weights** (all weights are EMPIRICAL — read `SolveScaleReport`, tune, don't derive):
   (a) a single 2D pin under-determines the 3-bone limb chain, so a UNIFORM Tikhonov lets the
   redundant **proximal** joint (chest) drift to the box at any prior size → fix = **per-bone prior**
   (`_isCore`: stiff torso λ≈60, loose limbs λ≈4) so the arm does the IK and the torso stays put;
   (b) the 2-link arm still has a near-singular **flat DOF** that re-solves to a new spot each frame
   → fix = a **temporal Δθ-smoothness prior** `√λ·(Δθ−Δθ_prev)` (`ThetaSmoothnessConstraint`). All
   solver weights/limits moved to **`AnimSolverConfig`** ← `anim_solver_config.json` (hot-reloaded;
   render-only so no determinism gate). Test `FixedPointSolverTests`: reach, **torso-stable**, arm
   engaged + bounded. NOTE the solve still only runs on the locomotion+contact path — Phase 3 must
   broaden the trigger so pins engage off-locomotion (wall slide).
3. **NoPenetration v1 (half-planes) — DONE.** Two pieces: (i) the **solve trigger broadened**
   off the locomotion+contact path — a new `SolveStaticPose` runs whenever a non-cadence frame
   has external pins OR surfaces to satisfy (Δφ LOCKED to [0,0], only δ + Δθ move), so the
   load-bearing constraints engage on any clip (wall slide). Behaviour-preserving: normal
   locomotion already has `_haveCorr` set by the cadence solve, and plain flight/idle has no
   pins/surfaces, so the static path is skipped there. (ii) `NoPenetrationConstraint` — one
   one-sided HALF-PLANE row per (surface × bone tip), `√TierNoPen·max(0, margin − n·(q − p0))`,
   pushing any limb tip that crosses a surface back out along `n` (q.Y rides δ). Surfaces ride
   the new `SolverSurface` on the sample (`{Point, Normal, Margin}`); `CharacterAnimSample.From`
   derives the wall-slide half-plane from public `Position`/`Facing`/`Radius` (a render-only
   read — no `ChunkMap`/SDF, per §11.5). INACTIVE rows emit 0 residual AND 0 Jacobian, so the
   row count is stable across a Minimize without a separate active-set pass — only WHICH rows
   are nonzero changes. **Key test subtlety:** the `max(0,·)` knee makes the FD oracle mute for
   any tip within ~one step of `gap == margin` (exactly the Δφ keyframe-boundary situation), so
   `MaxJacobianError` now skips no-pen rows with `|margin − gap| < 0.5px`; away from the knee the
   analytic Jacobian `−√w·n·PointJacobian(b,q)` matches FD to the float floor (`jacErr ≈ 0`).
   Test `NoPenetrationSolverTests`: off-locomotion solve runs, the wall pushes the hand ~2px out
   of the solid via a bounded arm Δθ, and the new block's Jacobian is exact. `TierNoPen` is in
   `anim_solver_config.json` (defaults to the hard tier, 10).
4. **(later)** d.x + horizontal ComOffset · local-SDF NoPenetration (arbitrary terrain, §4.5) ·
   JointLimits · wire NoPenetration surfaces for ground/ledge beyond the wall-slide wall.

Per-constraint **finite-difference Jacobian check** is the headline test for each new block
(template: `Solver_AnalyticJacobian_MatchesFiniteDifference`) — the single highest-value test
for a hand-rolled solver.
