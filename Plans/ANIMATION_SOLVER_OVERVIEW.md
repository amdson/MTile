# Animation solver — how it works (current state)

A plain-language map of the runtime animation system for re-orientation. History and
rationale live in `ANIMATION_SOLVER_PLAN.md`; this is *what ships today*. Render-only:
nothing here feeds back into the sim.

## One sentence

Every render frame, `CharacterAnimator` composes the authored clips into a target pose,
then runs **one box-bounded Levenberg–Marquardt least-squares solve** whose variables
nudge that pose so feet don't slip, pinned hands reach their targets, limbs stay out of
walls, and everything eases smoothly — conflicts are resolved by **weights**, never by
special-case code paths.

## The frame pipeline (CharacterAnimator.Update)

1. **Sample** — `CharacterAnimSample.From(player, dt)`: a read-only snapshot (position,
   velocity, facing, grounded, `AnimTag`, action name/time, pins, surfaces, grip, aim).
   The one-way boundary; movement code never knows the animator exists.
2. **Select clip** — `SelectClip` keys on `sample.Tag` + grounded/velocity:
   Vault/Crouch/WallSlide by tag, Jump/Fall by Vy, Run/Walk/WalkBack/Idle by speed vs
   facing. Phase-driven clips (Walk/WalkBack/Run/Idle) play off the wrapped phase φ;
   one-shots play off clip time.
3. **Resolve overlays** — the `OverlaySlot[]` stack (cap 3). Slot 0 = the Action FSM's
   overlay (a slash), slots 1+ = movement overlays (VaultHands, driven by *spatial*
   progress, not a clock). Each slot has per-bone weight = region mask (with graded
   `OffRegionWeight`) × eased weight. `_baseBlend[i] = Π(1−w)` is what the base layer
   keeps per bone — the Jacobian needs it.
4. **Resolve constraints' inputs** — pins (vault grip hand → ledge corner, gated by a
   progress window), surfaces (wall-slide half-plane), aim (stab direction), and the
   feathered **contacts** from the clip's keyframe labels (below). Targets are captured
   once and held — the solve never chases a moving target within a frame.
5. **Solve** — LM minimizes the weighted residual stack over
   `x = [Δφ, δ(=d.y), d.x, Δθ₀…Δθ_N]` (indices `IdxPhi/IdxDy/IdxDx/IdxTheta0`):
   - **Δφ** — cadence: how much the locomotion phase advances *this frame*.
   - **δ, d.x** — a small solved root offset (vertical bob + fore-aft sway).
   - **Δθᵢ** — per-bone angle corrections applied to the **composed** pose
     (post-overlay, so a pin can bend an overlay-owned arm).
   Off locomotion (wall slide, standing stab) a **static** variant runs with Δφ locked
   to 0 whenever there are pins/surfaces/aim to satisfy.
6. **Emit** — the solved pose IS the rendered pose (`_pose.CopyFrom(_target)` — there is
   no post-hoc ease; smoothing lives *inside* the solve, see ThetaSmoothness). Lean and
   land-squash are eased scalars applied after. Root placement for drawing =
   body position − com·scale + (d.x, δ) (`AttackGlowSystem.RigRoot` + offsets).

## The constraint blocks (ordered; each = rows in one big least-squares stack)

| Block | Rows | Drives | Meaning |
|---|---|---|---|
| PlantedContacts | 2/contact | Δφ, δ | H: planted foot must not slip (the cadence engine). V: hold the foot at plant height (body bobs). |
| FixedPoint | 2/pin | Δθ | Hard both-axis pin: hand on ledge corner. |
| NoPenetration | 1/(surface×bone) | Δθ, δ | One-sided half-plane: `max(0, margin − n·(q−p))` pushes limb tips out of solids. Surfaces = the wall-slide wall (margin 1.5, all bones) + TERRAIN: nearby exposed tile faces auto-extracted per frame (`TerrainSurfaces`, margin 0, bone-masked to the tips they were found near; a planted foot is exempt from its own support plane — the contact V-row owns that). |
| ActionAim | 1 | Δθ | Signed ANGLE between live hand-pair vector and the rotated authored reference (angle, not cross — cross has a spurious antiparallel minimum). |
| PlaybackContinuity | 1 | Δφ | Momentum prior on the phase step. |
| ComOffset | 2 | δ, d.x | Soft pull of the root offset toward baseline (flight settles; d.x can't silently absorb cadence). |
| PosePrior | N | Δθ | Tikhonov toward 0 — stiff core (hip/chest/head λ=60), loose limbs (λ=4): the arm does the IK, the torso stays put. |
| ThetaSmoothness | N | Δθ | **The ease, in-solve**: final angle pulled toward last frame's *emitted* angle, λ derived per frame from Stiffness(20)/UpperBodyStiffness(90) + dt. Deviation-space, so clip playback itself is never charged. |

**Weights** are dimensionless (px rows ÷ rig reach ≈ 21.6px, so radian and px rows
compare directly — 1 rad of joint ≈ 1 reach of tip). Tiers in `anim_solver_config.json`
(hot-reloaded): HARD ≈ 4700 (pins/no-pen) ≫ CONTACT ≈ 470 ≫ SOFT (com ties) ≫ priors.
Tune from `SolveScaleReport()` numbers, not first principles.

## Contacts & cadence (where walking feel comes from)

- Keyframes carry **contact labels** (`foot_l` SelfPlant). At phase φ a contact holds
  full weight until `nextKeyframe.Time − FeatherWidth(0.12)`, then crossfades out — a
  foot swap is a smooth crossover. Consecutive labeled keyframes extend a plant;
  a close NO-contact keyframe is the intentional **drop/arm marker** pattern.
- The contact's world **target is captured once** when it appears. A stale target at
  the foot's motion reversal used to deadlock Δφ→0; a time-based release
  (`ContactReleaseTime` 0.1s, engages when the contact starts fading) resolves it.
- **Known residual behavior**: a once-per-stride Δφ hop (~0.1–0.2) at the contact
  handoff; the Δθ smoothness bridges it (pose doesn't teleport). d.x reduced it.
- The solver can only be as smooth as the clip: the cadence tracks the *authored* foot
  sweep, so a stance that sweeps faster than body speed forces dφ to crawl, then swing
  sprints to catch up. See "clip data contract" below.

## Sampling & the analytic Jacobian

- Clips are sampled **C1** (Catmull-Rom on the rotation channel, `SampleSmooth`) with a
  matching exact derivative (`SampleAngularVelocity`) — same spline, so the analytic
  Jacobian is exact across keyframe boundaries. **Cyclic clips** (Loop + first==last
  pose) wrap tangents across the seam; anything else gets one-shot clamped ends.
- Rotation is the only authored channel, so every geometric row's Jacobian is a chain
  of 2D **lever arms** (`Lever`: exact under facing flip/scale) × `_baseBlend` (Δφ
  column) or unattenuated (Δθ columns — they correct the FINAL pose, post-compose),
  plus ω from SampleAngularVelocity for ∂/∂Δφ.
- The FD oracle (`MaxJacobianError`, Diagnostics partial) validates it in tests,
  shifted to the origin (float32 cancellation) and skipping genuinely non-smooth spots
  (keyframe-boundary straddles, no-pen knee band).

## Clip data contract (what authoring must respect — now guarded)

1. **Looping locomotion clips close their cycle one of two ways.** *Open tail*
   (default): the last keyframe ends BEFORE t=1 — the cycle period is exactly 1 and
   the sampler interpolates the wrap segment [lastTime, firstTime+1] back to the
   first keyframe (pose + velocity continuous through the seam, no duplicate needed).
   *Endpoint seam*: the last keyframe sits AT t=1 and must be an exact copy of the
   first — a drifted duplicate silently degrades the seam to one-shot semantics: pose
   pop + cadence stall every stride (the run.json incident). Guards (endpoint style
   only): red warning in the MTile.Demo header, `SEAM MISMATCH` flag in the probe
   digest, `SeamGuardTests` scans every shipped clip; `OpenTailLoopTests` covers the
   wrap semantics. Action clips are exempt from open-tail wrapping (they keep reading
   as one-shots even with Loop=true).
2. **No zero-width keyframe intervals** (duplicate times NaN'd the spline once;
   the sampler now degrades them to a step, but don't author them).
3. **Keep angle-vs-phase slopes sane**: ≥ ~8 rad/phase reads as a single-frame limb
   snap at locomotion rates (walk's max ≈ 4). The digest flags `STEEP interval`s.
4. **One dominant planted contact per keyframe** near handoffs; drop/arm markers keep
   their times + labels but should sit ON the motion path, not clone a neighbor pose
   (clones = mid-swing freeze frames).
5. Stance's phase span should roughly match sweep-distance ÷ body-speed at nominal
   cadence, or dφ will seesaw (crawl in stance, sprint in swing).

## Tooling

- `dotnet MTile.Probe/bin/Debug/net8.0/MTile.Probe.dll digest <clip>` — semantic digest
  + FLAGS (seam, steep, recurvatum). The fast edit→verify loop (~1s, no rebuild).
- `MTile.Tests/Animation/ZzzRunContinuity.cs` (temp harness) — per-frame phase /
  limb-delta / root traces to `.probe/run_continuity_*.md`, plus a jerk decomposition
  (clip content vs φ dynamics vs Δθ). Delete once run feels right.
- `SolveScaleReport()` — column norms/gradients for weight tuning. `MaxJacobianError()`
  — FD-vs-analytic. Both in `CharacterAnimator.Diagnostics.cs`.
- Headline tests: `AnimSolverTests` (Jacobian parity, well-posedness, vertical offset),
  `FixedPointSolverTests`, `NoPenetrationSolverTests`, `VaultGripSolverTests`,
  `ActionAimSolverTests`, `SmoothingTests`, `SeamGuardTests`.

## Key files

| File | Role |
|---|---|
| `Animation/CharacterAnimator.cs` | Update pipeline, clip binding, contacts, overlay stack, solve orchestration |
| `Animation/CharacterAnimator.Constraints.cs` | The constraint blocks + `PointJacobianColumns`/`Lever` |
| `Animation/CharacterAnimator.Diagnostics.cs` | FD oracle, SolveScaleReport, debug hooks |
| `Animation/LeastSquaresSolver.cs` | Allocation-free box-bounded LM (portable MathF) |
| `Animation/AnimationSampler.cs` | C1 sampling + angular velocity + IsCyclic + SeamMismatch guard |
| `Animation/AnimSolverConfig.cs` + `anim_solver_config.json` | All weights/limits, hot-reloaded |
| `Animation/MotionProbe.cs` / `MTile.Probe/` | Digest/report/diff tooling + FLAGS |
| `Animation/CharacterAnimSample.cs` | The sim→animation boundary (sample, pins, surfaces, AnimTag) |
| `Animation/TerrainSurfaces.cs` | Terrain no-pen sourcing: exposed tile faces → bone-masked half-planes |

## Invariants to remember

- **Render-only, always.** The sim never reads anything the animator produced.
- **One solve per frame; weights arbitrate.** Every constraint evaluates the final
  composed Δθ-corrected pose. No two-stage splits, no live w(φ+Δφ) inside the solve —
  both were tried and rejected (see memory / PLAN §2).
- The animator keys on `AnimTag`, never on state-name strings.
- Clips author ONLY rotations; lengths/translations come from the shared rig.
