# Animation ↔ Gamestate Matching: Locomotion via Constraint Optimization

Status: **planned, not implemented.** Design for matching animation playback to real
motion. The first slice we build is **cadence** (playback-rate matching for walk/run/
climb on ground). Inverse kinematics (foot-lock, vault-hand-on-corner) is the second
slice — but the key decision in this plan is that **both are the same optimization**
at different degrees of freedom, so the cadence implementation is literally the outer
loop of the IK-era solver with the inner solve stubbed.

Companion docs: [ANIMATION_LAYERING_PLAN.md](ANIMATION_LAYERING_PLAN.md) (movement +
action layering). Animator: [Animation/CharacterAnimator.cs](../Animation/CharacterAnimator.cs).
Sampler: [Animation/AnimationSampler.cs](../Animation/AnimationSampler.cs). Rig:
[Drawing/SkeletonExamples.cs](../Drawing/SkeletonExamples.cs).

---

## 1. The core idea

Match an animation to real motion by treating it as **constraint optimization**, not
a hand-derived playback formula. Label points on the skeleton as *contacts* (a planted
foot, a hand on a vault corner). Each contact is a node that should sit at a known
**world target**. Each frame we choose animation parameters to minimize how far the
labeled nodes land from their targets.

For the first slice the only free parameter is **phase advance `Δφ`** (how far to
advance the clip this frame). Later, the leg/arm joint angles join the parameter set
and the same minimize gains an IK sub-solve. Nothing about the framing changes — only
the dimensionality.

This replaces the earlier "derive a horizontal `clipStride` scalar" idea, which baked
in a single axis and couldn't express vertical constraints (ladder climb) or the
vault. The optimization is axis-agnostic by construction.

---

## 2. Why optimization (and not the analytic formula)

The no-slip invariant for a planted node is "zero world velocity." Written analytically
it needs the node's **clip-space velocity** `p'(φ)` — a derivative estimated off
discrete keyframes, which is noisy and has kinks at every keyframe boundary. It is also
over-determined (a 2D constraint, a 1D knob), forcing a least-squares projection plus
an ad-hoc regularizer near stride reversals.

The optimization formulation sidesteps all of that:

- It only ever **evaluates poses** (which the sampler already does) — no clip
  derivative anywhere in the data path.
- Over-determination is automatic: minimizing `‖·‖²` *is* the least-squares solve.
- The degeneracy at stride reversal stops being a divide-by-zero and becomes a **flat
  loss landscape** — bounded and well-behaved — resolved by a principled momentum prior
  (§4) rather than an injected `ε`.

The one derivative we *do* embrace later is the **IK Jacobian** (forward kinematics
w.r.t. joint angles) — but for 2-bone limbs even that is unnecessary because there's a
closed form (§6). So the solver carries no clip-derivative estimates at all.

---

## 3. Contacts and targets

A **contact** is a labeled node on the skeleton plus a policy for where its world
target comes from. The labeled set changes through the cycle (left foot plants, lifts,
right foot plants).

Two target sources, one primitive:

- **SelfPlant** (foot no-slip): on the frame a node becomes labeled, **capture** its
  current world position; **hold** that point as the target until the label drops.
  This is what pins a walking foot.
- **External** (vault / ledge): the target is a fixed world point supplied by the
  sim/level (the corner being vaulted), active over a time window. Same minimize,
  different target source.

Capture/hold state is **render-only** animator state — it never enters `Simulation`,
so determinism/rollback is unaffected (consistent with the pull-model boundary).
(External targets refine this slightly: the *sim publishes* the anchor point it
already computes — e.g. the vault corner via `PlayerAbilityState` — and the animator
reads it through the sample; all hold/ease state stays animator-side. See
[VAULT_HAND_PIN_PLAN.md](VAULT_HAND_PIN_PLAN.md).)

### Contact node = a bone tip

A contact point is a bone's tip: `WorldOf(bone).TransformPoint(new Vector2(bone.Length, 0))`.
Today the biped's `leg_*_lower` bones use `Length` only as a leaf orientation tick, so
there is no explicit toe/ankle. **Decision (§12.1):** add explicit `foot_l`/`foot_r`
leaf bones so the contact point is unambiguous and gives future IK a clean target.

---

## 4. The loss

For a candidate phase advance `Δφ`, sample the clip at `φ+Δφ`, run forward kinematics,
and sum the squared world-space miss of every active contact, plus a smoothness prior:

```
L(Δφ) = Σᵢ wᵢ · ‖ Cᵢ(φ+Δφ) − tᵢ ‖²   +   λ · (Δφ − Δφ_prev)²

  Cᵢ(φ+Δφ)  world position of contact node i at the candidate phase
            = root · pose(φ+Δφ) forward-kinematics to node i's tip
  tᵢ         contact i's world target (captured SelfPlant point, or External point)
  wᵢ         contact weight (from the label; soft contacts < 1)
  λ          momentum/smoothness prior weight
```

- **Axis-agnostic:** it is just `‖worldPos − target‖²`. Horizontal walk, vertical
  climb, diagonal vault ramp are the same equation; the clip's contact geometry decides
  the axis, never the solver.
- **Multi-contact:** more squared terms; still one scalar `Δφ`. Two planted feet or
  hand+foot fall out as the natural least-squares over all active contacts.
- **The prior is load-bearing, not a hack:** where contacts don't determine cadence
  (stride reversal; or, post-IK, any reachable phase) the first term is flat and
  `λ·(Δφ−Δφ_prev)²` picks "keep going at the previous rate." It also suppresses
  frame-to-frame jitter.

Body motion enters through `Cᵢ` (the node's world pos includes the new body position
in `root`), so cadence is driven by real motion without a separate "ground distance"
computation.

---

## 5. The solver — outer loop (build this first)

A **1-D, derivative-free, forward-bracketed** minimize of `L(Δφ)`.

- **Method:** golden-section search (Brent later only if profiling demands). ~30 lines,
  hand-rolled, pure `MathF` — no external dependency (see §11).
- **Forward-bracketed is non-negotiable:** `L` is multimodal over a cyclic clip (a foot
  crosses the same world point twice per cycle). A global minimize would teleport or
  reverse the phase. Search `Δφ ∈ [0, Δφ_max]` only, with `Δφ_max` set by a max
  plausible cadence per frame.
- **Cost:** a handful of bones × ~10–20 evals, serial, allocation-free. Negligible for
  our character counts.

### 5.1 Event-aware substepping (the "multi-frame" case)

A single large frame step can advance phase far enough to **cross a contact event** — a
foot lifts and the other plants *within* the step. You cannot solve that span with one
fixed target set. So advance phase in segments bounded by the next contact-change
keyframe:

```
remaining = desiredTotalStep            // bound by velocity / Δφ_max
while remaining > 0:
    segEnd   = phase to next contact-change keyframe (or remaining, whichever first)
    Δφ_seg   = SolvePhaseStep(clip, φ, root, min(remaining, segEnd))   // golden-section
    apply Δφ_seg; φ = Wrap01(φ + Δφ_seg)
    on crossing a contact change: release lifted contacts, capture newly-planted ones
    remaining -= Δφ_seg
```

Bonus: within a segment the contact set is constant, so the loss is smooth (no
foot-switch kink) → each golden-section solve sees a unimodal piece. Correctness and
conditioning from the same mechanism.

### 5.2 Contact swap handling (foot lift/plant)

The hard moment is the **plant swap**: at some phase, foot A lifts and foot B plants.
The decisions below trade a little exactness for a lot of simplicity — and lose almost
no fidelity at 30 fps.

**Feather the contact weight — make the swap a crossover, not an instant.** Each
contact's `Weight` ramps rather than toggling: A's `1→0` as it lifts, B's `0→1` as it
plants, with a short overlap window where both are partially weighted. The least-squares
loss then blends both constraints automatically:

```
L(Δφ) = w_A·‖C_A − t_A‖² + w_B·‖C_B − t_B‖² + λ(Δφ − Δφ_prev)²
```

This dissolves the "what is the new constraint's start point?" question: **capture B's
target the moment its weight first goes nonzero**, while B's influence on the solve is
still near zero. A slightly-wrong capture barely affects the solve; by the time `w_B`
ramps to 1, B has been pinned for several frames and settled. The error is absorbed in
the low-weight region instead of popping.

**Bound `Δφ_max` below one stance window.** Wanted anyway (stops skipping a whole step
per frame; keeps the golden-section bracket on a smooth piece). It also guarantees a
frame crosses **at most one swap**, so the bookkeeping never compounds. At 30 fps a
frame advances only ~10–13% of a stance even at a sprint, so a frame rarely straddles a
swap and, when it does, by a small margin.

**Lazy end-of-frame capture — no exact-instant substep.** Per frame:

1. Solve `Δφ ∈ [0, Δφ_max]` with weights evaluated across the candidate span (a frame
   straddling the swap automatically sees the blended A+B loss — no special case).
2. Advance `φ`; store `Δφ` as `Δφ_prev`.
3. Reconcile contacts at the new `φ`: a contact whose weight just left zero gets its
   target captured **now**, one FK eval under the current (end-of-frame) body root — no
   within-frame body interpolation. A contact whose weight hit zero is dropped.

**Explicitly skipped:** interpolating body position to the exact swap instant and
re-solving the post-swap remainder within the same frame. The sub-frame exactness it
buys is below perceptual threshold given `Δφ`/frame ≪ stance window. (If a freshly-
planted foot ever needs one notch more precision, capture it at the swap *keyframe*
`φ_swap` instead of end-of-frame `φ` — one extra pose sample — but feathering should
make this unnecessary.)

**Continuity:** the momentum prior `λ(Δφ − Δφ_prev)²` carries the phase rate smoothly
through the swap, so cadence doesn't lurch when the active constraint changes. **IK era:**
drive each leg's IK target weight by the same contact weight — the lifting leg ramps
back to the clip/FK pose while the planting leg ramps onto its captured point, so the
limb transitions FK→IK with no snap.

---

## 6. The solver — inner loop (IK era; stub for now)

When joint angles join the parameter set, the structure is **bilevel**, not one big
Jacobian — because `Δφ` and joint angles are different *kinds* of variable:

| variable | nature | method |
|---|---|---|
| `Δφ` | kinky (keyframe boundaries), periodic, forward-bounded | derivative-free golden-section (outer) |
| joint angles `θ` | smooth, analytic FK | closed form / Gauss-Newton (inner) |

```
outer:  φ  ← golden-section on the POST-IK residual
inner:  θ  ← given φ, solve joints to hit contact targets
```

- **Inner is a small damped Gauss-Newton, piloted by the vault hand pin** (revised —
  see [VAULT_HAND_PIN_PLAN.md](VAULT_HAND_PIN_PLAN.md) §5.3, which pilots the shared
  `LimbSolver` on a problem with *no phase DOF*). Rationale for preferring it over the
  earlier closed-form law-of-cosines plan: the stay-near-pose prior kills branch/bend
  flips and reach-limit snapping by construction, warm starts give temporal coherence,
  and differentiating the *actual* FK encodes the rig's R·T·S pivot conventions
  automatically (hand-deriving chain geometry is where the errors live — see that
  plan's §5.2). Cost: 2–4 warm-started iterations on a ≤4-DOF dense system per
  evaluation instead of O(1) — still trivially cheap inside the bilevel loop, and §11's
  hand-rolled-GN clause already blessed it. **Closed-form remains the documented
  fallback** (same plan, §5.3 tail) if the pilot disappoints. The bilevel structure
  itself is unchanged and non-negotiable: Δφ stays on the derivative-free outer loop —
  one unified loss, two nested solvers matched to variable type, never one flat solver.
- **The objective shifts when IK lands — internalize this.** Once IK can pin the foot
  for any nearby `φ`, the contact term goes to ~0 across a *range* of `φ`, so contacts
  **stop determining cadence**. `φ` is then pinned by (a) a **reach-feasibility barrier**
  (soft penalty as the planted leg nears full extension — don't let the body outrun the
  locked foot) and (b) the **momentum prior**. IK does not make the φ-solve harder; it
  empties the contact term and hands φ's job to the barrier + prior. The residual left
  after IK is exactly the slip IK couldn't absorb (over-extension), which is what the
  barrier is protecting against.
- **Multi-state interpolation** (blending two clips) adds a weight `α`, but `α` is
  scheduled by the transition (a crossfade timer), so it's a **parameter** of the loss,
  not a solved DOF. Dimensionality stays low.

For the first slice, the inner solve is the identity (no IK): `Cᵢ` is read straight
from the sampled pose. Adding IK later is a localized change inside the loss evaluation.

---

## 7. Data model (sketches)

### 7.1 Contact label on a keyframe — additive, back-compatible

```csharp
// Animation/ContactLabel.cs (new)

// Where a contact's world target comes from.
public enum ContactSource { SelfPlant, External }

// One contact annotation on a keyframe: a named node that should be pinned at this
// keyframe's instant. Absent/empty list on a keyframe = airborne (no contacts).
public sealed class ContactLabel
{
    public string        Node   { get; set; }                    // bone name; point = its tip
    public float         Weight { get; set; } = 1f;              // wᵢ in the loss, [0,1]
    public ContactSource Source { get; set; } = ContactSource.SelfPlant;
}
```

```csharp
// Animation/AnimationDocument.cs — extend AnimationKeyframe (rides existing JSON I/O)
public sealed class AnimationKeyframe
{
    public float                Time     { get; set; }
    public List<PoseBoneEntry>  Bones    { get; set; } = new();
    public List<ContactLabel>   Contacts { get; set; }   // null = no contacts this frame
}
```

`System.Text.Json` picks up `Contacts` automatically; old files (no `Contacts`) load
as airborne-everywhere and fall back to the legacy velocity-based phase advance.

### 7.2 Active contact target — animator runtime state (render-only)

```csharp
// held inside CharacterAnimator
private struct ActiveContact
{
    public int           Bone;     // resolved bone index of the contact node
    public Vector2       Target;   // world point to pin to
    public float         Weight;
    public ContactSource Source;
}
private readonly List<ActiveContact> _contacts = new();   // rebuilt as labels change
private float _prevPhaseStep;                              // Δφ_prev for the momentum prior
```

Capture/hold logic: when a label appears at the current phase, resolve its world
position once and store as `Target` (SelfPlant) or take the supplied point (External);
keep it until the label drops at a later keyframe.

### 7.3 Solvers (hand-rolled, WASM-safe)

```csharp
// Animation/GoldenSection.cs (new) — 1-D derivative-free minimize on [lo, hi].
public static class GoldenSection
{
    // Minimizes f on [lo, hi] to tolerance tol or maxIters. Allocation-free.
    public static float Minimize(Func<float, float> f, float lo, float hi,
                                 float tol = 1e-3f, int maxIters = 24);
}
```

```csharp
// Animation/LimbSolver.cs (new, IK era) — damped Gauss-Newton over a limb's local
// rotation deltas (≤4 DOF incl. optional root offset), minimizing contact residual
// + stay-near-pose priors. Hand-rolled, fixed-size, allocation-free, WASM-safe.
// Piloted (and specified in detail) by VAULT_HAND_PIN_PLAN.md §5.3; shared by the
// vault pin and this plan's Phase 5 foot IK. The closed-form law-of-cosines
// TwoBoneIk sketched in earlier revisions survives as the documented fallback there.
public static class LimbSolver { /* see VAULT_HAND_PIN_PLAN.md §5.3 */ }
```

### 7.4 The per-frame phase solve (ties it together)

```csharp
// inside CharacterAnimator — replaces the PhasePerPixel line for locomotion clips
float SolvePhaseStep(AnimationDocument clip, float phi, in Affine2 root, float maxStep)
{
    float Loss(float dphi)
    {
        AnimationSampler.SampleNormalized(clip, Wrap01(phi + dphi), _kfA, _kfB, _scratch);
        _scratch.ComputeWorld(root);
        // (IK era: solve inner joints here, then read Cᵢ from the IK'd pose.)
        float e = 0f;
        foreach (var c in _contacts)
        {
            Vector2 tip = _scratch.WorldOf(c.Bone)
                                  .TransformPoint(new Vector2(_skeleton.Bones[c.Bone].Length, 0f));
            e += c.Weight * (tip - c.Target).LengthSquared();
        }
        float d = dphi - _prevPhaseStep;
        return e + Lambda * d * d;
    }
    return GoldenSection.Minimize(Loss, 0f, maxStep);
}
```

---

## 8. Runtime integration (CharacterAnimator)

Change is localized to the phase-advance step in `Update`:

1. Resolve `root` for the current frame (already done for `Draw`; hoist it so the solve
   and the render share it).
2. Refresh `_contacts` from the active clip's labels at the current phase: release
   contacts whose label dropped, capture newly-appeared ones (§7.2).
3. Event-aware substep loop (§5.1) calling `SolvePhaseStep`; store the last `Δφ` into
   `_prevPhaseStep`.
4. Sample the clip **by phase** for locomotion (`SampleNormalized(clip, _state.Phase,…)`),
   keeping one-shot clips (Jump/Fall/Vault/Crouch) sampled by `ClipTime` as today. The
   `Loop` flag is a good selector (looped → phase-driven, one-shot → time-driven), or an
   explicit `bool PhaseDriven` (§12.3).
5. Directional lean (§3b in the animator) and landing squash (§3c) still apply on top of
   the sampled base pose — unchanged.

Unchanged invariants: read-only `CharacterAnimSample`, render-only, no feedback into the
sim. None of this lives in `Simulation.Step`.

Fallback: clips with no contact labels (or `HasContacts == false`) keep the existing
`speed * dt * PhasePerPixel` advance, so nothing regresses before labels are authored.

---

## 9. Editor support (MTile.Demo)

- **Label toggle:** select a node, press a key to toggle its `ContactLabel` on the
  active keyframe; cycle `Source` (SelfPlant/External) with a modifier.
- **Render planted nodes** distinctly (filled red disc) so the stance schedule is
  visible while scrubbing.
- **Slip readout:** with a synthetic constant body velocity, drive the solver and draw
  the planted node's world track across a cycle — a near-stationary point means good
  no-slip. Lets authors tune contact timing by eye.
- Saving round-trips `Contacts` through the existing `AnimationStore.Save`.

---

## 10. Edge cases

1. **Standstill / sub-threshold speed:** keep the `WalkSpeedThreshold` blend to Idle;
   freeze on a planted-foot phase.
2. **Flight phase (run):** a span with zero contacts is legal — `_contacts` empties, the
   loss is just the prior, cadence coasts. No divide-by-anything.
3. **Slopes:** targets are world points and the loss is axis-agnostic, so a foot planted
   on a slope is handled with no special case once the contact point snaps to terrain
   (a downward query — stub now, fill later).
4. **Reach feasibility:** harmless pre-IK (no lock to over-extend). Becomes the dominant
   φ-driver post-IK via the reach barrier (§6).
5. **IK solution flips (IK era):** fix the knee/elbow bend side by convention and
   warm-start from the previous frame so the post-IK residual stays smooth in `φ`; the
   `λ` prior smooths any remaining jitter.

---

## 11. WASM / dependency constraints

The shared source compiles twice — under DesktopGL (`MTile.Core`) and KNI/Blazor WASM
(`MTile.Web`). The solver lives in shared `Animation/` code, so it must run in WASM.

- **No external solver library.** Native-backed options (NLopt wrappers, MKL paths) use
  P/Invoke → unavailable in Blazor WASM. Math.NET Numerics is the only pure-managed
  option that would run, but it's oversized for a 1-D search + closed-form IK, adds
  download weight (the web csproj is size-conscious, `RunAOTCompilation=false`), and can
  fight trimming. Accord.NET / SolverFoundation are effectively dead.
- **Hand-roll both pieces** (`GoldenSection`, `TwoBoneIk`): pure `MathF`/`Vector2`,
  allocation-free, single-threaded (Blazor WASM is single-threaded by default), WASM-safe
  by construction — the same discipline already used for `Affine2`.
- Revisit a real solver library **only** for long chains or a coupled full-body solve —
  and even then prefer a ~60-line hand-rolled dense Gauss-Newton/LM for a per-frame
  render-side loop.

---

## 12. Open decisions

**Resolved — action/movement layering:** action clips are **constraint-free, fixed-rate
overlays**. They carry no contact labels, never enter the cadence φ-solve, and add no
phase DOF; the action layer just reads its own `ActionVars.TimeInState`, samples, and
blends onto the movement pose per a per-bone mask (see
[ANIMATION_LAYERING_PLAN.md](ANIMATION_LAYERING_PLAN.md)). **All contacts and IK live on
the movement layer only.** The lone positional action-like invariant — the vault hand on
the corner — is not an action: vault is a *movement* state (`ParkourState`), so that pin,
if ever wanted, is an External contact on the movement layer's contact set, not action-
side. This removes the two-clock coupling entirely; don't re-litigate it.

1. **Add explicit `foot_l`/`foot_r` bones** to the biped (recommended) vs. reuse the
   lower-leg joint as the ankle.
2. **`λ` (momentum prior weight)** and **`Δφ_max` (max phase step / cadence clamp)** —
   tune by eye in the editor.
3. **Phase vs time sampling selector:** reuse the `Loop` flag, or add explicit
   `bool PhaseDriven` to `AnimationDocument`.
4. **Initial labeling:** hand-label the `walk` seed in `DemoGame.BuildSeeds` so there's a
   working contact example day one, vs. editor-only.
5. **Golden-section tolerance / iteration cap** — start at `tol 1e-3`, `maxIters 24`.

---

## 13. Implementation phases

Each phase is independently shippable and ends with a concrete verification. A recurring
gate applies to **every** phase touching shared code: it must compile under **both**
`MTile.Core` (DesktopGL) and `MTile.Web` (KNI/Blazor WASM). Determinism is not a gate —
all of this is render-only and never enters `Simulation.Step`.

### Phase 0 — Rig + data scaffolding (no behavior change)
- **Do:** add `foot_l`/`foot_r` leaf bones to `SkeletonExamples.Biped` (§12.1); add the
  `ContactLabel` type + `ContactSource` enum (§7.1); add the `Contacts` field to
  `AnimationKeyframe`.
- **Stub/defer:** nothing reads `Contacts` yet.
- **Verify:** both builds green; existing `SkeletonStates/*.json` still load (no
  `Contacts` → treated as airborne); the game and demo run visually identical to today.

### Phase 1 — Golden-section minimizer + headless test (pure math)
- **Do:** implement `GoldenSection.Minimize` (§7.3), allocation-free.
- **Stub/defer:** no game wiring.
- **Verify:** a `MTile.Tests` unit test builds a 2-keyframe clip with one labeled
  contact + a synthetic constant body velocity, runs the solve loop, and asserts the
  planted node's world position stays within tolerance across the cycle. Fits the
  headless `MTile.Tests/Sim` style (no rendering).

### Phase 2 — Cadence wiring, single contact (no swap yet)
- **Do:** add the `ActiveContact` capture/hold state (§7.2), hoist `root`, implement
  `SolvePhaseStep` (§7.4), and switch **locomotion** clips to phase-driven sampling
  (one-shots stay `ClipTime`-driven). Resolve §12.3 (reuse `Loop`, or add `PhaseDriven`).
  Hand-label the `walk` seed in `DemoGame.BuildSeeds` with one planted foot per half-
  cycle (hard switch tolerated here).
- **Stub/defer:** swap feathering, substepping, IK. Legacy `PhasePerPixel` fallback stays
  for unlabeled clips.
- **Verify in-game:** walk pace tracks body speed across a range; foot slip is visibly
  reduced vs. the old constant. A small pop at the foot swap is acceptable for now.

### Phase 3 — Swap handling (feather + bound + lazy capture)
- **Do:** implement §5.1 substep loop + §5.2 feathered-weight crossover, the `Δφ_max`
  bound, lazy end-of-frame capture, and the `λ` momentum prior. Re-label `walk` (and add
  `walkback`) with weights that ramp across keyframes. Tune `λ`, `Δφ_max` (§12.2).
- **Stub/defer:** IK.
- **Verify in-game:** foot-plant transitions are smooth (no pop at the swap); cadence
  stays continuous through swaps and across speed changes; standstill blends to Idle
  cleanly (§10.1).

### Phase 4 — Editor support (authoring)
- **Do:** §9 — contact label toggle, `Source` cycle, planted-node rendering, slip readout
  in `MTile.Demo`.
- **Note:** can be pulled before Phase 3 if hand-labeling seeds in `BuildSeeds` gets
  painful — it's a pure authoring aid, no runtime dependency.
- **Verify:** can author/inspect/save contacts visually; the slip readout reflects label
  timing changes.

### Phase 5 — IK era (separate slice; only after cadence is proven)
- **Do:** adopt `LimbSolver` (§7.3 — damped GN, piloted and proven by the vault hand
  pin first; closed-form fallback if that pilot disappointed), plug the inner solve into
  the loss evaluation (§6), add the reach-feasibility barrier, switch SelfPlant feet to
  hard pins, drive IK target weight by contact weight (§5.2). The **External**
  vault-corner contact is designed separately in
  [VAULT_HAND_PIN_PLAN.md](VAULT_HAND_PIN_PLAN.md) — External contacts **bypass the
  φ-solve entirely** (the vault clip is a one-shot with no phase DOF); their solve is a
  draw-time post-pass, a different call site from the bilevel cadence loss here.
  `LimbSolver` is the shared utility both consume. NOTE before implementing either: the
  rig-convention findings in that plan's §5.2 (R·T·S pivot semantics, rotation-only
  keyframes → the chain base is the *parent* origin, not the first joint) apply to this
  phase's leg IK too — the GN Jacobian encodes them automatically, the fallback doesn't.
- **Verify in-game:** no measurable slip (hard pin); legs handle slopes and don't over-
  extend (barrier); vault hand reaches the corner; no limb snap at swaps (weight ramp).
