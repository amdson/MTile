# Vault Hand Pin — External contacts + draw-time 2-bone IK

Status: **planned, not implemented.** Design for pinning the character's hand on the
corner being vaulted (`ParkourState`) so the hand stays visually fixed while the
vault clip plays, with entry possible from any walk-cycle point.

Companions: [ANIMATION_LOCOMOTION_PLAN.md](ANIMATION_LOCOMOTION_PLAN.md) (cadence /
contacts / the shared `TwoBoneIk` utility — §12 there resolved that the vault hand
pin is a **movement-layer External contact**, never an action), and
[ANIMATION_LAYERING_PLAN.md](ANIMATION_LAYERING_PLAN.md) (action overlay — independent
of this; an UpperBody slash and a vault never overlap because the pin only runs in
Parkour/LedgePull states).

---

## 1. The problem

Three constraints make this harder than "play a vault animation":

1. **The hand must be *exactly* on the corner** while planted — near-miss reads as
   floating/sliding, which is worse than no pin at all.
2. **The corner is sim data** the animator doesn't have: `ParkourState` re-queries it
   every frame and keeps it in a private `SteeringRamp` (`Character/MovementStates.cs`,
   `_overRamp.Corner` — a world-point tile InnerEdge). Nothing on `PlayerCharacter`'s
   public surface exposes it.
3. **Entry happens from any walk phase**, so the legs/arms arrive in an arbitrary
   pose, and the vault's *duration is physics-driven* (entry-speed dependent) while
   the clip is a fixed-length one-shot — the two clocks never line up exactly.

The principled decomposition: the corner flows **sim → sample, one-way** (the same
pull-model boundary as everything else); the clip *declares* when the hand should be
planted via the existing contact-label system (`ContactSource.External` already
exists, reserved for this); and the pin is *enforced* by closed-form 2-bone IK as a
**render-only post-pass at draw time**. Each piece is independently simple, and the
failure mode of any missing piece is "no pin", never a broken pose.

---

## 2. Architecture overview

```
sim:      ParkourState publishes its over-ramp corner → PlayerAbilityState.VaultAnchor
boundary: PlayerCharacter.TryGetMovementAnchor(out p)  → CharacterAnimSample.AnchorPos
animator: vault clip keyframes carry ContactLabel{Node="arm_r_lower", Source=External}
          Update: evaluate feathered External weight at normalized ClipTime,
                  gate by HasAnchor, ease the effective pin weight, stash anchor
          Draw:   if pinWeight > 0: copy _pose → _renderPose, solve 2-bone IK
                  in rig space, blend by pinWeight, render _renderPose
```

Nothing enters `Simulation.Step`. The only sim change is *publishing* a value the sim
already computes (see §3); all capture/ease/IK state stays animator-side.

---

## 3. Anchor exposure (sim → sample)

- **`PlayerAbilityState`** (`Character/PlayerAbilityState.cs`) gains
  `public bool HasVaultAnchor; public Vector2 VaultAnchor;`. `ParkourState` writes the
  **over-ramp** corner each frame in `Reconcile` and clears on `Exit` — the same
  pattern `LedgeGrabState` uses for `GrabbedCorner`.
  - **Over-ramp only.** The under-ramp (ducking beneath an overhang) publishes
    nothing: a hand pin on a *lower* corner reads wrong. (Known debt: the duck-under
    currently plays the Vault clip too — `SelectClip` matches any `*Parkour*` state —
    so the arm performs its reach with nothing to pin. Acceptable until Duck gets its
    own clip.) If both ramps are ever active, over wins.
  - Snapshot-safe by construction: `Clone()` is `MemberwiseClone` (covers value
    fields); `CopyFrom` gains two lines. **Caveat to comment on the field:**
    `SnapshotRoundTripTests` compares position/velocity/names/hp only — a forgotten
    `CopyFrom` line is invisible to that gate (the only symptom would be one wrong
    render frame after a rollback, since nothing sim-side reads the anchor).
  - This is the first ability field that is **sim-written but only render-read**
    (`GrabbedCorner` is genuinely sim-load-bearing). Document the asymmetry where the
    field is declared.

- **`PlayerCharacter`** gains `public bool TryGetMovementAnchor(out Vector2 p)`:
  - `ParkourState` → `VaultAnchor` (when `HasVaultAnchor`);
  - `LedgeGrabState` / `LedgePullState` → `GrabbedCorner`.
  - **Gate on `CurrentStateName`, NOT `IsLedgeGrabbing`** — `LedgeGrabState.Exit`
    clears that flag *before* `LedgePullState` runs, and `GrabbedCorner` is
    deliberately never cleared (LedgePull re-reads it). String policy on state names
    is the animator's established convention (`SelectClip` does the same).

- **`CharacterAnimSample`** gains `bool HasAnchor; Vector2 AnchorPos` as trailing
  defaulted ctor params (no call-site churn — same trick as `ActionTime`).

Amendment to the locomotion plan's framing: §3 there says capture/hold state "never
enters `Simulation`". Still true — the sim *publishes* a value it already computes;
the animator owns all hold/ease state. The boundary stays one-way.

---

## 4. Contact data (what the clip declares)

- Vault clip keyframes carry `ContactLabel { Node = "arm_r_lower", Source = External,
  Weight }` over the window where the hand is on the corner. The contact point is the
  forearm bone's **tip** (`Length` along local +X) — the same bone-tip convention as
  foot contacts; no new hand bones (decided: add `hand_l/r` leaf bones only if wrist
  orientation ever matters).
- **Evaluation:** reuse the feathered-weight logic (`WeightedContactsAtPhase`) at
  `AnimationSampler.NormalizedTime(doc, ClipTime)` — it already clamps for `Loop=false`
  one-shots. Two required amendments:
  1. **Source filter.** `_weightBuf` currently discards `Source`, and `RefreshContacts`
     hardcodes `SelfPlant` on capture. The External path needs a `Source == External`
     filter and must stay **entirely out of `_contacts`** — that list belongs to the
     cadence φ-solver. (Verified: Vault never enters the cadence path — the solver is
     gated on Walk/WalkBack/Run — so there is no interaction to manage; keeping the
     two evaluations separate preserves that.)
  2. External targets come from `sample.AnchorPos` **every frame** — never
     captured-and-held like SelfPlant. Effective gate =
     `featheredLabelWeight × HasAnchor`, so the pin dies instantly when the movement
     state exits, regardless of where the clip is.
- **Hard authoring rule: the first and last keyframes must carry no hand contact.**
  At clamped t=1 the last keyframe's contacts hold at full weight *forever* while the
  state persists — the anchor gate is the backstop, not the mechanism. (Also note:
  `FeatherWidth` is normalized time, so the engage ramp in *seconds* scales with clip
  `Duration` — a hidden coupling worth remembering when retiming clips.)

---

## 5. The pin: a small regularized solve as a draw-time post-pass

**This is also the pilot of the unified-solver direction.** The long-term intent is
one loss for all animation matching — contacts (feet, hand), pace consistency, and
small perturbations of joint angles and the rig root — solved **bilevel**: the
cadence φ stays on the derivative-free golden-section outer loop (φ is kinky at
keyframes, periodic, and must be forward-bracketed — gradient methods are wrong for
it), while the smooth variables (joint deltas, root offset) get a small **damped
Gauss-Newton** inner solve. The vault is the ideal pilot because a one-shot clip has
*no phase DOF*: the unified solver degenerates to the pure inner problem (2 arm
angles + optionally 2 root-offset components, one contact, priors). If the solver
earns its keep here, locomotion Phase 5 folds foot IK under the cadence outer loop
using the **same inner solver**; if it disappoints, the closed-form fallback (§5.3)
is ~60 lines away and nothing else in this plan changes — anchor plumbing, External
labels, draw-time placement, and the eased weight are all solver-agnostic.

Why a solver instead of closed-form trig: regularized least-squares fixes the three
classic IK brittleness modes *by construction* — no branch/bend-sign flips (the
stay-near-current-pose prior makes output continuous in the target), no hard snapping
at reach limits (infeasible targets degrade into a graceful least-squares compromise,
optionally absorbed by bounded root offset), and temporal coherence via warm starts.
It also removes a whole error class: closed-form requires hand-deriving chain geometry
from rig conventions (the §5.2 findings exist because the first hand-derivation here
was *wrong*); a solver that differentiates the actual FK composition encodes the
conventions automatically.

### 5.1 Why draw time (not Update)

Two reasons, both structural:

- **The true root only exists at draw time.** `Game1` computes the rig root from
  `CurrentSoleY()` of the *eased* pose (`rootY = groundY − sole·scale`) after
  `Update` returns. An Update-side solve would pin against the wrong root.
- **Easing would un-pin the hand.** `_pose.BlendToward(_target, …)` lags the target
  by design; if IK ran before easing, the rendered hand would trail the corner every
  frame. The pin must be the *last* thing that touches the pose.

So: at the start of `CharacterAnimator.Draw`, if pin weight > 0, copy `_pose` into a
scratch `_renderPose` (`CopyFrom`, allocation-free), apply the IK there, and render
`_renderPose`. **`_pose` — the easing state — never sees IK.** The pass is a stateless
per-call post-filter: repeated Draws and other hosts are safe; a host that never
Updates sees pin weight 0.

### 5.2 Rig-convention findings (load-bearing for ALL future limb IK)

These came out of designing the closed-form write-back and apply to the locomotion
plan's Phase 5 foot IK too. Under the Gauss-Newton solve they are no longer needed to
*derive* the chain (the Jacobian of the real FK encodes them automatically) — but they
remain essential for choosing the DOF set, sizing the reach annulus, and for the
closed-form fallback:

- **Locals are R·T·S.** `Affine2.FromTRS` rotates the translation: a bone's local
  `Rotation` swings *its own joint position* around the **parent's origin** (stated in
  the `Affine2.cs` comment, easy to forget). Writing `arm_r_upper.Rotation` does NOT
  bend the arm at the shoulder — it orbits the shoulder (bind offset `(7,−2)`) around
  the **chest origin**.
- **Authored keyframes are rotation-only** (`PoseBoneEntry`), so every bone's
  translation sits at bind in every sampled pose. Since `arm_r_lower`'s bind offset
  `(10,0)` is collinear with its +X tip `(10,0)`, **shoulder → elbow → hand-tip are
  always collinear** — there is no independent elbow bend in the data.
- **Therefore the real 2-DOF chain is:**
  `base = chest origin, l1 = |(7,−2)| ≈ 7.28, l2 = 10 + 10 = 20` (rig units), with the
  **shoulder as the virtual elbow**. Same `TwoBoneIk.Solve(root, l1, l2, target,
  bendSign)` shape as the locomotion plan §7.3 — different base and lengths than the
  naive shoulder+elbow chain.
- **Reach is an annulus**, `[l2−l1, l1+l2] ≈ [12.7, 27.3]` rig units → **[7.6, 16.4] px**
  at the game's 0.6 scale. The *minimum* bound is the real hazard: mid-vault the body
  crests over the corner and the chest-to-corner distance shrinks *inside* minimum
  reach, where the clamp hovers the hand off the corner. Author the contact window to
  release before the crest; the eased weight (§5.4) absorbs the rest.

### 5.3 The solve: damped Gauss-Newton over joint deltas (+ optional root offset)

Work in **rig space**: `rigInv = root.Inverse()`. Because `ComputeWorld` composes
`root · (chain of locals)`, `rigInv · WorldOf(bone)` cancels the mirrored root
*exactly* — everything below is facing-independent, and `rigInv` strips the draw
scale, so no manual `_scale` handling exists anywhere in the solve.

**Variables** `x = (δθ_upper, δθ_lower [, δr_x, δr_y])` — deltas on the two arm
locals from their current (eased) values, plus an optional 2D **render-root offset**
("body english": shift the drawn rig a few px relative to the physics body to satisfy
the contact without contorting the arm — the elegant answer when the corner falls
inside minimum reach mid-vault).

**Loss** (all in rig units):

```
L(x) = w_c · ‖ tip(δθ) + δr − P ‖²          contact: forearm tip on the anchor
     + λ_θ · ‖δθ‖²                            stay near the authored/eased pose
     + λ_r · ‖δr‖²                            root offset is a last resort
     + (warm-start: x₀ = last frame's solution → temporal coherence)
```

**Solver:** hand-rolled damped Gauss-Newton (Levenberg damping), ≤4 DOF dense —
~60 lines, fixed-size arrays, allocation-free, pure `MathF`, WASM-safe (the same
discipline as `GoldenSection`; locomotion plan §11 pre-approved exactly this). The
Jacobian comes from the **actual FK composition** — analytically (each column is the
perpendicular of tip-minus-pivot, with pivots per the R·T·S findings in §5.2: upper's
pivot is the *chest origin*, lower's is the shoulder) or by central differences on
the 2–4 DOF (8 FK evals; either is cheap). 2–4 iterations per frame, warm-started.
Apply with pin weight `w`: `Rotation += w·δθ`, render root shifted by `w·δr·scale` —
exact pin at `w=1` (when feasible), identity at `w=0`.

**The three tuning rules that make it behave** (these ARE the design — get them
wrong and the solver "cheats"):

1. **Exactness:** `w_c ≫ λ_θ` (start ~1000 : 1) and iterate to convergence — with
   DOF ≥ constraints the feasible-case residual goes to ~0, matching the "near-miss
   reads as floating" requirement. A pin that looks spongy means the priors are too
   strong, not that the approach failed.
2. **The root offset will cheat if allowed:** it's a global knob that reduces every
   contact residual at once, so unbounded least squares slides the body instead of
   bending the arm — and a drawn body drifting from the physics body breaks hitbox
   readability. Hard-clamp `|δr|` (a few px) AND give it the strongest prior
   (`λ_r ≫ λ_θ`). The prior ratios form an explicit explanation hierarchy:
   joints first, root only when joints can't reach.
3. **Debugging inverts:** when trig IK looks wrong you debug geometry; when a solver
   looks wrong you debug a loss landscape. The editor residual readout (per-frame
   `‖tip − P‖` + the solved `δθ/δr` values) is the primary debugging tool — build it
   in the same phase as the solver, not later.

Reach: the annulus (§5.2) stops being a clamp special-case — an out-of-reach target
just leaves a least-squares residual, softened by `δr` up to its bound, and the
authored release window plus the eased weight (§5.4) cover the crest as before.

Known approximation: `LandSquash` puts non-uniform scale on the hip (an ancestor),
which perturbs the FK the Jacobian linearizes — GN just converges against the actual
FK, so unlike the closed-form recipe this isn't even approximate; no caveat needed.

**Closed-form fallback** (if the pilot disappoints — kept because it was carefully
derived and is exact): law of cosines about `B` = chest origin with measured
`l1 = |S0−B|`, `l2 = |T0−S0|`, `d = |P−B|` clamped to the annulus, fixed bend sign →
solved shoulder `S*`; then `δ1 = wrap(atan2(S*−B) − atan2(S0−B))`, rotate `T0` about
`B` by `δ1` → `T1`, `δ2 = wrap(atan2(P−S*) − atan2(T1−S*))`; apply `w·δ1`, `w·δ2`.
Under R·T·S these pivot exactly at `B` and `S*`. No root offset, hard reach clamp,
and the LandSquash ellipticity caveat applies (sub-pixel in practice).

### 5.4 The eased pin weight (why feathering alone is not enough)

The state exit is **physics-timed** (speed-dependent), so it will not line up with the
clip's authored release keyframe. Without extra handling, `HasAnchor` dropping would
snap the arm from pinned to eased-pose in one frame.

Fix: the animator keeps a render-side **eased pin weight** — target =
`featheredLabelWeight × HasAnchor`, asymmetric ease rates (fast engage / ~3-frame
release, same shape as the action overlay's `ActionEaseIn/Out`). Evaluated and stashed
in `Update` (which has the sample), consumed in `Draw`. Engage smoothness comes from
the authored feather; release smoothness comes from the ease.

### 5.5 Two required fixes that ride along

- **`CurrentSoleY` must exclude the pinned chain.** A vault pose reaching downward can
  let the *arm* define the sole, lifting the whole rig (body visibly floats while the
  rendered hand is pinned elsewhere). This cannot be fixed post-IK — the IK needs the
  root and the root needs the sole (circular). Principled break: a position-constrained
  limb has no business defining ground rest — skip the External-pinned chain's bones in
  the sole scan.
- **Plant-foot debug markers must draw from the rendered pose.** They currently read
  `_pose`'s world buffer assuming the renderer just refreshed it; once Draw renders
  `_renderPose`, `_pose`'s last world pass is `CurrentSoleY`'s *identity-root* one — the
  markers would silently draw at rig-space coordinates. (Latent today: Vault clears
  `_contacts` so no markers show; fix it when touching Draw.)

---

## 6. Entry from any walk phase, and the two-clocks problem

- **Legs/pose discontinuity at entry: rely on the existing `BlendToward` ease**
  (20/s ≈ a 0.15 s implicit crossfade) — decided; no new crossfade machinery. The
  vault is a full-body one-shot; the pinned hand plus the fast guided body motion
  carry the eye through the blend. Explicit per-transition crossfades stay a future
  extension (listed in the layering plan); phase-aligned entry (picking a clip start
  matching the current leg pose) was considered and rejected as disproportionate.
- **Clip time vs. sim progress:** the clip plays on animator `ClipTime` (starts when
  the clip switches), while the vault's duration varies with entry speed — so the clip
  may clamp early or run long. Accepted: the anchor gate bounds the damage (pin dies
  with the state), and `Loop=false` holds the final pose harmlessly. Exposing movement
  time-in-state (or a vault progress scalar) to drive the clip is the upgrade path if
  the mismatch ever reads badly; it stays unexposed for now.

---

## 7. Edge cases

1. **Anchor jumps** — `ParkourState.Reconcile` re-queries per frame and chains across
   staircase corners; the anchor can teleport one tile at weight ≈ 1. Also
   Parkour → LedgePull keeps the same clip (no `ClipTime` reset) while the anchor
   source switches instantly. Rule: if the stashed anchor moves more than a few px in
   one frame, drop the eased pin weight to 0 and re-engage — release/re-plant, never
   drag the hand.
2. **Facing flip mid-vault** — releasing the held direction exits `ParkourState`
   within a frame, killing the anchor; at most one frame of mirrored-target solve,
   bounded by the reach clamp and the eased release. Self-resolving.
3. **LedgeGrab hang** — plays the Fall clip, which has no External labels → gate is 0
   → graceful no-op. A future Hang clip lights the pin up *just by adding the label* —
   a deliberate property of putting the window in clip data.
4. **No clip labels at all** (current state of the authored vault clip) — gate is 0,
   everything renders exactly as today. Nothing regresses before labels are authored.

---

## 8. Implementation phases (when picked up)

Gate for every phase touching shared code: compiles under both `MTile.Core`
(DesktopGL) and `MTile.Web` (KNI/WASM). All render-side; the sim change is publish-only.

1. **Anchor plumbing** — `PlayerAbilityState` fields (+`CopyFrom`), `ParkourState`
   publish/clear, `PlayerCharacter.TryGetMovementAnchor`, sample fields. Verify:
   builds; snapshot tests green; a debug disc at the anchor during a vault tracks the
   corner.
2. **External evaluation + eased pin weight** — Source-filtered feathered weights at
   normalized ClipTime, anchor gating, anchor-jump release, weight ease in Update.
   Verify: headless test drives a synthetic vault clip + scripted anchor and asserts
   the weight envelope (engage ramp, full, release on anchor loss).
3. **Draw-time solver pass** — new `Animation/LimbSolver.cs` (damped Gauss-Newton,
   ≤4 DOF, §5.3), `_renderPose` copy, root-offset clamp, `CurrentSoleY` exclusion,
   marker fix, and the residual/`δθ`/`δr` debug readout (rule 3 — same phase, not
   later). Verify: headless — solve a known geometry and assert the forearm tip lands
   on the target at w=1 (within tolerance after ≤4 iterations), poses untouched at
   w=0, output continuous under a moving target (no branch flips), and `δr` stays at
   ~0 while the target is reachable by joints alone; in-game — hand visually locked
   on the corner through a vault at multiple entry speeds. **This phase is the
   unified-solver pilot** (§5 preamble): its outcome decides whether locomotion
   Phase 5 adopts `LimbSolver` as its inner solve or falls back to closed-form.
4. **Editor + seed** — author/cycle `ContactSource` on labels in `MTile.Demo`
   (M+click currently toggles SelfPlant only), distinct color for External marks,
   re-label the vault seed (contact-free first/last keyframes per §4). Verify: labels
   round-trip JSON; pin reads correctly with the seed clip.
5. **LedgePull reuse** — `TryGetMovementAnchor` already returns `GrabbedCorner`; just
   confirm the shared Vault clip's labels behave through grab → pull → exit (the
   anchor-jump rule covers the source switch).

---

## 9. Open decisions

1. Eased pin weight rates (engage/release) — tune by eye, start near the action
   overlay's 25/8 per second.
2. Anchor-jump threshold (px/frame) for release/re-engage — start ~4 px.
3. Whether the pinned-chain exclusion in `CurrentSoleY` should be "arms always" or
   "currently-pinned bones only" — start with pinned-only (minimal behavior change).
4. Solver weights (§5.3 rules): `w_c : λ_θ` ratio (start 1000:1), `λ_r` (start
   ≥ 10× λ_θ), root-offset clamp (start ±3 px), Levenberg damping seed, iteration
   cap (start 4). All tune-by-eye against the residual readout.
5. Jacobian: analytic (perpendicular-of-lever columns per the §5.2 pivots) vs
   central differences (8 FK evals, convention-proof) — start with differences,
   switch to analytic only if profiling ever cares.
6. Whether the root offset ships in the pilot at all, or lands as a follow-up once
   the joints-only solve is proven (joints-only is a strict subset — 2 DOF, no
   clamp logic). Leaning: joints-only first, `δr` second.
