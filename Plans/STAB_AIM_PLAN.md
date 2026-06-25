# Aimed-action animation (stab direction) — Plan

## 0. Goal

The stab is parametrized by player input: `StabAction` captures `_stabDir` (a world aim vector,
steerable mid-attack within `MaxTotalSteer`). The authored clip (`stab.json`) only depicts a
**horizontal** stab. We want the *rendered* pose to re-aim along the actual `_stabDir`, so a stab
up-and-to-the-right looks like it thrusts up-and-to-the-right — without re-authoring the clip per
angle and without losing the clip's windup→thrust→settle dynamics.

This is the first **aimed action**. The mechanism is general (any overlay action with an input
direction — slashes toward the mouse, etc.), but stab is the pilot.

## 1. The idea (user's, refined)

> Treat the clip as the reference for a horizontal stab. Track the left-hand→right-hand vector and
> its angle over the clip. When the clip plays, constrain that vector's direction (relative to the
> actual stab direction) to equal what it was (relative to horizontal) at the same time in the
> reference. If we're lucky that's enough to read as a convincing re-aim.

Two refinements that make it well-posed:

- **Signed angle, not cosine similarity.** Cosine similarity is sign-symmetric (`cos α = cos −α`),
  so it can't tell up-aim from down-aim. Express the goal as a *rotation*: rotate the reference aim
  vector by the stab's deviation angle, and constrain the live aim **parallel** to the result. The
  residual is the 2-D cross product `v̂ × û*` (zero iff parallel, signed) — see §2.
- **The reference is the unperturbed pose, computed live.** The composed overlay pose at the
  current τ *with Δθ = 0* is exactly the authored horizontal reference. Its aim vector `a_ref(τ)`
  is read once per frame at solve start; no stored reference clip / precomputed table is needed.

So conceptually: **rotate this frame's authored aim by the stab's deviation-from-horizontal, and
let the solver bend the arm (Δθ) to hit that aim** — preserving the within-clip thrust dynamics
because the rotation is applied to the *current* reference, whatever phase of the stab it's in.

## 2. The math

**Aim vector.** `v(x) = p_R(x) − p_L(x)`, the world vector from the left hand (`arm_l_lower` tip)
to the right hand (`arm_r_lower` tip). (See §8 Q1 — alternative aim vectors if L→R doesn't read.)

**Deviation rotation.** Let `f = (facing, 0)` be horizontal-forward in world, and `d = _stabDir`
(unit, world). `R` is the 2-D rotation taking `f → d` (i.e. by the signed angle `θ_d` between
them). Computed directly from `f`, `d` (cos = f·d, sin = f×d) — no atan2 needed.

**Target.** At solve start (Δθ = 0) read the reference aim `a_ref = v(0)` from the composed overlay
pose, then `u* = R · a_ref` (rotate the reference aim by the stab deviation), normalized to `û*`.
Freeze `û*` for the frame.

**Residual (one row).** `r = √w · ( v̂(x) × û* )` where `v̂ = v/|v|` and `×` is the scalar 2-D cross
`a.x·b.y − a.y·b.x`. `r = 0 ⇔ v ∥ û*`. Bounded stab angle + warm start avoid the antiparallel
degeneracy (you can't stab backwards).

**Jacobian (analytic, reuses the primitive).** `∂v/∂x = PointJacobianColumns(R) −
PointJacobianColumns(L)` (the two hands' world-point sensitivities, already our one primitive).
Then for the cross product against the *fixed* `û*`, differentiating `v̂ × û*`:
- Cheap, robust variant — drop the `1/|v|` normalization and constrain the *unnormalized* cross
  `v × û*` (same zero set; magnitude scales by `|v|`, which is ~constant over a small Δθ): then
  `∂r/∂x_k = √w · ( ∂v.x/∂x_k · û*.y − ∂v.y/∂x_k · û*.x )` — a direct linear combination of the
  primitive's `colX`/`colY`. This is the recommended v1 (one row, exact, trivial).
- If the `|v|` variation matters, carry the full `∂v̂` quotient-rule term; not expected to.

δ (the body bob) doesn't rotate `v` (it shifts both hands equally → cancels in `p_R − p_L`), so the
aim row has a zero δ column. Δφ: stab is not phase-driven, so Δφ is locked on this path anyway.

Because Δθ is applied **post-compose** (the vault-grip change), the lever columns for the
overlay-owned stab arm are unattenuated — the solver can actually bend it. This feature is only
possible because that already landed.

## 3. Mapping onto existing code

| Piece | Where |
|---|---|
| New constraint `ActionAimConstraint` (1 row, the cross residual + Jacobian) | `Animation/CharacterAnimator.Constraints.cs`, in the `_constraints` list (HARD-ish tier, after FixedPoint) |
| Reference aim `a_ref` capture (post-compose, Δθ=0) + `û*` freeze | once per solve, alongside `BuildSolvePose`/contact capture |
| `PointJacobianColumns(R) − PointJacobianColumns(L)` | already the shared primitive |
| Stab direction `_stabDir` → animation | new render-only `ActionState.TryAnimationAim(out Vector2 dir)`, overridden by `StabAction`; rides `CharacterAnimSample.{HasAim, AimDir}` (geometry only; set in `From`) |
| Aim bone pair (`arm_l_lower`, `arm_r_lower`) + which actions aim | animation policy in the animator (like `ResolveMovementPins`), keyed off the action name |
| **Solve must run during a standing stab** | broaden the off-locomotion trigger (§4) |
| Weight/limits | `AnimSolverConfig` (`TierAim`, maybe an aim-ramp), hot-reloadable |

## 4. The trigger (important)

Today the solve runs on (a) locomotion + contacts, or (b) the off-locomotion static path when
pins/surfaces exist (Phase 3). A **standing** stab is `Idle` (or air `Jump/Fall`) + the stab
overlay — no contacts, no pins, no surfaces — so **no solve runs and Δθ stays 0**. So we must
extend the static-solve guard to also fire when an action aim is active:

```
if (_useSolver && !_haveCorr && hasClip && (_pins.Count > 0 || _surfaces.Count > 0 || _aimActive))
    SolveStaticPose(...);
```

This generalizes the trigger from "a geometric pin/surface to satisfy" to "any active non-prior
constraint." (A *walking* stab already solves via the cadence path; the aim constraint just adds
its row there — the contact rows keep the feet planted while the arm re-aims.)

## 5. Plumbing detail

1. `ActionState`: `public virtual bool TryAnimationAim(out Vector2 dir) { dir = default; return false; }`
   — render-only, same contract as the movement `TryAnimationGrip`. `StabAction` overrides it to
   return `_stabDir` while in (or near) the thrust window.
2. `CharacterAnimSample`: add `bool HasAim; Vector2 AimDir;`, set in `From` from
   `p.CurrentAction?.TryAnimationAim(...)`. Geometry only — the animator owns which bones/when.
3. Animator (step 1.7, beside `ResolveMovementPins`): resolve the aim — if the current action aims
   and the clip is the stab overlay, set `_aimActive`, capture `_aimDir`, and the constraint reads
   the `arm_l_lower`/`arm_r_lower` pair. Naming/window policy lives here.
4. Constraint engagement window: ramp the aim weight with the action's thrust phase (0 during deep
   windup/recovery, full during the active window) so the re-aim doesn't fight the wind-up pose.
   The reference-relative target already preserves windup *shape*; the ramp just decides *how hard*
   we enforce aim per phase. (Cheapest v1: always-on during the overlay; add the ramp if needed.)

## 6. Weights

`TierAim` in `AnimSolverConfig` (start near the hard/contact tier so the arm clearly re-aims, but
below the stiff-core `CorePosePrior` so the torso stays mostly put and the *arm* does the aiming).
Empirical, read off `SolveScaleReport` (extend it to print the aim residual / angle error). The
existing per-bone pose prior (stiff torso, loose limbs) + the temporal Δθ-smoothness prior already
do the right things here: arm leads, torso steadies, mid-attack steering re-aims without jitter.

## 7. Phases

1. **Plumb the aim signal.** `TryAnimationAim` on `ActionState`/`StabAction`, `{HasAim, AimDir}` on
   the sample + `From`, animator resolves `_aimActive`/`_aimDir`. No solver change yet; assert the
   signal reaches the animator (test).
2. **`ActionAimConstraint` + trigger.** Add the 1-row cross-product constraint (unnormalized v1) and
   the `_aimActive` trigger branch. Headline test: **FD-vs-analytic** on the aim row
   (`MaxJacobianError`) across several stab angles, plus an aim-reaches test (live `v` parallel to
   `û*` within tolerance) on a standing stab — exercising the solve-during-stab trigger.
3. **Tune + feel.** Set `TierAim`, decide the engagement window/ramp, eyeball in the editor /
   in-game. Confirm: up/down/diagonal stabs read correctly, mid-attack steering re-aims smoothly
   (θ-smoothness), the off-hand stays plausible, the knife/glow tracks the new aim.
4. **(Generalize, later.)** Reuse for slashes (aim toward the mouse `ComputeSlashDir`) — same
   constraint, different aim source + bone pair.

## 8. Open questions / decisions

1. **Aim vector = L→R hands?** The user's pick; holistic (both arms + torso share the turn). Risk:
   it's a *proxy* for the knife/hitbox direction, so the visible knife may not point *exactly* at
   `_stabDir`. Alternatives if it doesn't read: (a) the **knife axis** (`arm_r_lower`→knife tip) —
   ties the visual directly to the hitbox, only the stab arm moves; (b) `chest`→right-hand. Decision
   leans: start with **L→R** (honor the idea, looks holistic); keep knife-axis as the fidelity
   fallback. Could even run both (L→R for body turn at a soft tier, knife-axis hard) later.
2. **Direct-rotation baseline?** A non-solver hack — add `θ_d` to the stab arm's shoulder rotation —
   is far cheaper and might already "read." Worth a 10-minute spike *before* the constraint to see
   how much the solver actually buys (fidelity, off-hand, limits, smoothness). If the hack looks
   fine, the constraint is gold-plating; if it looks stiff/clips, the constraint earns its keep.
3. **Engagement window** — always-on during the overlay, vs ramped to the thrust phase (§5.4).
4. **Whose angle is "horizontal"?** `f = (facing, 0)` in world. Confirm `_stabDir` is world-space
   and consistent with the facing-flipped draw root (it should be — both are world).

## 9. Risks

- **Degenerate antiparallel** (cross = 0 when `v` points opposite `û*`): mitigated by the bounded
  stab cone (no backward stab) + warm start near aligned. Add a tiny dot-of-aim guard only if seen.
- **Stiffness / unnatural off-hand.** Re-aiming purely via bounded Δθ may look rigid. Mitigations:
  give the chest a little freedom during stab (lower its prior while aiming), tune `TierAim`, or
  switch to the knife-axis aim so only the stab arm carries it.
- **Knife/hitbox mismatch** if L→R is the aim proxy (Q1) — the glow welds to the knife bone, so a
  large proxy error would show. Knife-axis aim removes this.
- **Trigger scope creep.** Broadening the static-solve trigger to "aim active" is small, but keep it
  to *active non-prior constraints* so idle/flight with nothing to do still skips the solve.

## 10. Tests

- **FD-vs-analytic** for the aim row (`Solver_AnalyticJacobian_MatchesFiniteDifference` template),
  across stab angles — the headline test for the new block.
- **Aim-reaches**: standing stab with `AimDir` at several angles → live `arm_l_lower→arm_r_lower`
  ends up parallel to `û*` within tolerance, via a bounded arm Δθ, torso steady (chest Δθ small) —
  and crucially the **solve runs** (exercises the aim trigger on a non-locomotion clip).
- **Steering smoothness**: sweep `AimDir` over frames (mimicking mid-attack steer) → the aim
  follows without per-frame jitter (the θ-smoothness prior).
- Regression: with no aim (HasAim=false) the stab overlay is byte-identical to today (no solve).

## 11. Prerequisites already in place

- **Post-compose Δθ** (vault grip) — lets the solver bend the overlay-owned stab arm. *Required.*
- **Off-locomotion static solve** (Phase 3) — the path a standing stab's solve runs on; just needs
  the `_aimActive` trigger condition added.
- **`PointJacobianColumns`** — the shared world-point gradient the aim Jacobian is built from.
- **Per-bone pose prior + temporal Δθ-smoothness** — make the arm lead, the torso steady, and the
  mid-attack re-aim jitter-free, for free.
