# Path-Tracker Unification Plan

Status (2026-10-16, end of day): **phases 1, 2, 4 DONE; phase 3 step 1 DONE**
(the climbs follow their clips on `PathTracker`); phase 3's trigger kept on the
old `ManeuverCorrector` probe by decision (§5.3), so its step-3 deletions
(`ManeuverCorrector`, `BallisticPredictor.PredictGuided`) wait on a trigger
revisit. Phase 5 docs done. BACKLOG 1.25 points here.

Goal: every movement state that steers the body along a trajectory does it the
same way — **a geometric path, followed by the bead / band / progress solve
(`PathTracker`), actuated only by capped, masked channels**. Two corrector
loops (`ManeuverCorrector`, `ReferenceCorrector` + `ReferencePath.TrackForce`)
and the last lattice-engine callers of the ballistic coast
(`BallisticPredictor`) go away.

## 0. Decisions already made (do not re-open)

- **Jumps stay hybrid.** A jump's launch is a velocity write (+ hold force);
  it is discontinuous and does not belong in a planner. The state passes a
  jump profile so the tracker still handles corner plants and clearance on the
  way up. `DoubleJumping` and `WallJumping` already work this way and are
  **out of scope**. `JumpingState` stays lattice-native as it is (its planned
  rise is what the covered-jump bevel escape depends on — lattice scenario
  row 3).
- **`RunningJumpState` must not use `BallisticPredictor`.** It is a ground jump
  with a stronger launch; it joins the hybrid pattern (§3).
- **The `ManeuverCorrector` states and the reference-path states move to
  `PathTracker`.** The logic that worked for `LedgePull` applies generally.
- **`TerrainCarriedState` is last and cautious** (§6): characterize first,
  prefer deleting its ballistic assist over replacing it.
- Standing rules that bind every phase:
  - Maneuvers keep their own states, triggers and priorities. A migration
    changes **how a state steers**, never **whether it exists** — no
    `OnLattice → return false` on any maneuver.
  - No new solver config knobs. Pace and caps come from existing tuning
    (`Lip*Force`, `Fold*Force`, durations); structural constants are fine.
  - No new features on `BallisticPredictor`; `BallisticVy` (closed form) may
    stay as a helper.

## 1. Where things stand

Category per state (from the 2026-10-16 audit), with the target:

| State | Steers via today | Target |
|---|---|---|
| Standing, Crouched, StairClimb, Falling, WallSliding | lattice planner (`FoldProfile.*`) | unchanged |
| Jumping | lattice-native jump (legs launch) | unchanged |
| DoubleJumping, WallJumping | hybrid: impulse + `FoldProfile.Jump` | unchanged |
| **LedgePull** | `PathTracker` + grip channels | **done** — the template |
| **RunningJump** | impulse + hold + **ballistic ambient assist** | hybrid (§3) |
| **Dropdown** | clip → `ReferenceCorrector` deform → `TrackForce` servo | `PathTracker` on the clip (§4) |
| **Parkour, Mantle, ArcJump** | hop + feedforward + `ManeuverCorrector` (guided ballistic coast, `BuildManeuver` channels) | hop + `PathTracker` on the hop arc (§5) |
| **TerrainCarried** | ride servo + **ballistic ambient assist** (clearance-only) | ride servo + nothing, or + `PathTracker` clearance (§6) |
| LedgeGrab, LedgeJump, CoveredJump, Stunned, Tumble | own forces, corrector off | unchanged (out of scope) |

What `PathTracker` already provides (`Character/Corrector/PathTracker.cs`):
free rollout, held-state samples, polyline + beads, band / arc-speed / hover
rows, progress row, terrain clearance rows, solve, tick-0 apply, ledger,
`BuildClipPolyline` (a retargeted `HermiteClip` as a path from the body
onward), `FollowPolyline` (the whole loop for a policy), and the
`IPathTrackPolicy` seam. Policies today: `LatticeTracker`'s private one,
`GripPathPolicy`.

## 2. Lessons from the LedgePull migration (apply to every phase)

Each of these cost a debugging round on the pull. Check them up front.

1. **Hard band rows veto unilateral channels.** If the body can drift to a side
   of the path that no channel can push back from, the solve answers the band
   violation by zeroing whatever channel has a component the wrong way — the
   pull ground up the corner and timed out. Every channel set must be able to
   answer the band in **both** perpendicular directions wherever the path runs
   (the pull needed BRACE and HOLD for exactly this).
2. **Fade caps per axis, not along the path.** A path-speed fade left the lift
   at full authority at a flat crest and launched the body out of reach.
3. **Re-derive pace against the timeout.** A constant-pace follower spends
   longer than a clocked clip (it accelerates, and completion sits late in the
   arc). `LedgePullRefDuration` had to drop 0.60 → 0.45 to finish inside
   `MaxLipManeuverTime`. Measure time-to-complete for every migrated move.
4. **Completion and hand-off tests were tuned to the old servo.** The servo
   threw bodies inward at the crest; tests like `PullCompleted`'s 0.7R buffer
   quietly depended on it. Audit every "done" / "re-grab" / "exit" geometry in
   a migrated state.
5. **Test at 60 fps.** Every ledge test ran at 1/30 and none caught the pull
   timing out at the game's 1/60. Each phase adds at least one 60 Hz scenario
   test and runs a parameter sweep (entry speed × height) at both rates.
6. **Anchor authored paths at a canonical pose; let the body join mid-path.**
   `BuildClipPolyline` starts at the body and continues from its projection, so
   a body arriving early, late or fast is never pulled back to the start.

## 3. Phase 1 — RunningJump → hybrid (small)

> **DONE 2026-10-16.** `FoldProfile.Legs` (explicit; `Stand`/`Crouch`/`Stairs`/
> `Jump` on, `Fall` off — identical to the old inferred rule) and
> `FoldProfile.ImpulseJump` (`Jump` with legs off); `RunningJumpState` passes it
> on lattice. Masking the leg servo alone was enough — drive and tuck were left
> permitted (the measured rise matched without touching them, so the "ground
> set" below shrank to the legs). Held running jump: 52.1 px (old ballistic
> assist: 52.0; `RunJumpApexRise` 49.4); with legs permitted it was 85.7 px.
> `RunningJumpLatticeTests` pins it at 60 and 30 Hz.

Today: `RunJumpVelocity` write + `RunJumpHoldForce`, then
`ApplyAmbient(Default, FoldProfile.None)` — which on the lattice engine falls
through to the old ballistic-prediction assist.

Change: pass a jump profile on the lattice engine, as `DoubleJumping` and
`WallJumping` do. **One trap:** those two are airborne, so the tracker has no
leg channels. A running jump starts on the ground, and `FoldProfile.Jump`
would let the legs add a second launch on top of the impulse (leg cap fades
only at `FoldLegPushFadeSpeed` 400) — a super-jump.

**D1 — resolved 2026-10-16: permit channels explicitly through the profile.**
A state already chooses its channels through its `FoldProfile`, but leg
permission is inferred today (`LatticeTracker` masks legs iff
`!fold.Hover && !fold.Rising`). Make it an explicit `FoldProfile` field
covering the ground set — legs, drive, tuck (all need feet on the floor; the
redirect plant is already off once the body is ballistic). On for `Stand`,
`Crouch`, `Stairs`, `Jump` (JumpingState's launch *is* the legs); off for
`Fall`. `RunningJump` passes `Jump` with the ground set off: its impulse is the
launch. Replaces an inferred rule with a stated one — no new mechanism, no knob.
(`Fall` is not a substitute: it plans a level line, and near the takeoff floor
its tuck would pull the rise down.)

Done when: `RunningJump` never reaches `BallisticPredictor` on lattice; apex
height matches the other engines within a few px; `SpeedInvariantTests`,
`SproutLiftJumpTests` (3 known red — confirm unchanged), the lattice scenario
rows and a 60 Hz running-jump test pass.

## 4. Phase 2 — Dropdown → PathTracker (medium-small)

> **DONE 2026-10-16.** `PathTracker.BuildClipPolyline` on the dropdown clip +
> `SlidePathPolicy` over a new `CorrectorChannels.BuildSlide` (legs up, tuck
> down, drive along — a small explicit set rather than `BuildFold`'s masks,
> which carry corner/plant/air machinery a slide never uses). Two findings:
> (1) **fade on the axis AND along the path** (`PaceFade`, the tighter of the
> two) — per-axis alone let the diagonal slip-off reach the pace on both axes;
> (2) **re-derive the duration** (lesson 3 again): only the clip's on-platform
> stretch plays, so the average-speed pace at 0.30 s yanked the body off at
> twice the old exit speed; `DropdownRefDuration` 0.30 → 0.45 matches the old
> slip-off (exit vy 70–170 px/s at 60 Hz). Both fallbacks deleted
> (`UseReferenceClips` gone — LedgePull's too), with `ReferenceCorrector`,
> `ReferencePath.TrackForce`, `GuidedSpringK`/`GuidedDamping` and the
> never-read `Guided*` fields, `MovementVars.RefActive`/`RefProgress`.
> `GuidedMaxForce` stays (the `ref` engine reads it). `DropdownPathTests` sweeps
> 8 starts at 60 Hz. Not yet applied: `PaceFade` to `BuildGrip` (the pull works;
> change it only with a pull playtest).

Today: `ReferenceClipRegistry.Dropdown` clip → `ReferenceCorrector` deforms the
clock-sampled arc → `ReferencePath.TrackForce` servos it (free force capped at
`GuidedMaxForce` 10 000). Bespoke fallback (clips off) uses `ManeuverCorrector`.

Change: the LedgePull recipe.
- Path: `PathTracker.BuildClipPolyline` on the dropdown clip (entry/gate as
  set in `Enter` today).
- Channels: a `DropdownPathPolicy`. While over the platform the body has legs:
  the fold's ground set (legs up, tuck down, drive along the slide) — reuse
  `CorrectorChannels.BuildFold` masks rather than inventing. Off the edge:
  nothing (the state already exits to Falling the instant the floor is gone).
  Check lesson 1: the slide is horizontal-ish, so legs + tuck cover both band
  sides vertically; drive is one-sided along the path, which is fine (the band
  is perpendicular).
- Pace: drive cap fades at `arc length / DropdownRefDuration` (per axis).
- Bespoke fallback (`UseReferenceClips = false`): **delete it** — not wanted
  (decided 2026-10-16). The dropdown always follows its clip; its
  `ManeuverCorrector` call goes with it.

Then delete: `ReferenceCorrector`, `ReferencePath.TrackForce` (both are
dropdown-only after this), `ReferenceCorrectorTests`, `GuidedMaxForce` /
`GuidedSpringK` / `GuidedDamping` if nothing else reads them.

Done when: `DropdownTests`, `ReferencePathTests` (dropdown cases),
the dropdown → ledge-grab chain (`DropChainDir`, BACKLOG 1.8) pass; a 60 Hz
sweep over slide speed × platform-edge offset lands every case.

## 5. Phase 3 — the climb family → PathTracker (large)

`ParkourState`, `MantleState`, `ArcJumpState` (`ClimbManeuverBase`,
`Character/Movement/ClimbStates.cs`) — the automatic step climbs: the running
1-block vault, the slow/flush 1-block mantle, and the 2-block arc hop (Up held).
They fire on their own when the corridor scan sees a step ahead. The
biggest phase: two consumers of the old loop (per-tick correction **and**
trigger-by-feasibility), and no authored path to reuse.

What stays: the states, their rise bands, entry-speed gates, corridor scan,
priorities, the **hop velocity write** (`HopVy` — this is a jump; hybrid by
decision), cancel-on-release, `MaxLipManeuverTime`, the completion test.

What goes: the per-tick feedforward (entry-speed drive, ballistic envelope
brake, crest push) and `ManeuverCorrector.Apply`/`Run`.

### 5.1 The path

**D2 — resolved 2026-10-16: option (a), the existing clips.** The authored arcs already exist:
`ReferenceClips/parkour.json`, `mantle.json`, `arcjump.json` (same
`HermiteClip` format, same `--ref` editor). The sim does not load them today —
`ReferenceClipRegistry` registers only `ledge_pull` and `dropdown` — they feed
the animation side (`MTile.Probe mapcom`). Options:
- **(a, recommended)** Use them: register the three clips, retarget entry→gate
  (`MantleCorner` / `MantleTargetY` give the gate), `BuildClipPolyline` — the
  LedgePull recipe exactly. Sim and animation then share one arc. The hop stays
  a velocity write (hybrid) but can be read off the clip's entry tangent instead
  of `HopVy`'s separate sizing. Check in the sweep: a fixed shape per climb
  cannot flatten with entry speed, so a very fast entry is bent onto the
  authored arc — measure the band corrections at the top of each band's speed
  range.
- (b) The hop's own ballistic arc, closed form (`p₀ + v₀t + ½gt²` to the target
  height, then a crest segment over the lip). Follows entry speed exactly, but
  a second source of shape the animation does not share.

### 5.2 Channels

A `ManeuverPathPolicy` over the existing `CorrectorChannels.BuildManeuver`
set (legs/tuck/redirect near ground, fade-capped forward air lateral +
unrestricted back, tiny air vertical, corner assist, corner plant). Keeping the
climbs' own actuators makes this a solve swap, not a feel change. Verify
lesson 1 against it: forward air lateral is unilateral but the back channel
answers the other side; vertical in flight is only the tiny `AirVertical` —
**measure whether the band can be met at the crest**. If not, the grip
channels (`BuildGrip`, hands on `MantleCorner`) are the physical answer at the
lip, not a bigger air force.

Pace: the forward air lateral already fades at `max(MaxWalkSpeed, entry
speed)` — keep. No arc-speed row.

### 5.3 Trigger-by-feasibility

Today `CheckPreConditions` runs the full-arc `ManeuverCorrector.Run` on a probe
body (128 iterations) and asks `Delivers`: does the corrected guided coast
reach the gate band past the lip before any unavoided impact? Plus a second,
uncorrected look for flush-against-the-step starts.

**D3 — resolved 2026-10-16: check the reference itself, with wiggle room.**
A climb may fire iff its **reference arc** (the retargeted clip from D2,
anchored at the post-hop entry pose — the path the tracker will follow) reaches
the gate past the lip without running the body into terrain *by more than the
tracker can bend around*. No dynamics, no `BallisticPredictor`, no probe solve:

- Sample the full retargeted clip (as `BuildClipPolyline` does), sweep the body
  polygon along the samples with `ClearanceConstraintBuilder` (the margin the
  path tracker uses, `LatticePathPlanner.Clearance`), and walk the samples in
  order up to the gate.
- **Wiggle room:** a sample's penetration only refuses the climb if it is deeper
  than a graze allowance — a structural constant in `ClimbStates.cs`, not a
  config knob. Shallower contacts are what the per-tick solve exists to steer
  around (today's "a feasible scrape vs. a jam" distinction, kept as a depth
  instead of a solve).
- **Flush starts:** contacts already present at the entry sample are ignored —
  a body standing against the step it climbs is in contact with that face, not
  blocked by it (the reason for today's second, uncorrected look).
- **Sizing the allowance (measured, not guessed):** run the old trigger and the
  new one over the climb sweep (entry speed × rise height × approach distance,
  each band, both rates). Pick the smallest allowance where the new trigger
  accepts **at least every case the old one accepted**; then check the
  newly-accepted cases actually deliver (none time out at
  `MaxLipManeuverTime` or end below the gate). If a value can't satisfy both,
  report the conflicting cases rather than picking one.

The question is no longer "would the corrected motion deliver" but "is the
authored arc a sane route here" — the per-tick tracker owns the rest, and a
climb it can't finish still ends through cancel-on-release / the timeout as
today.

> **Measured 2026-10-16 — D3 as written does not hold; decision needed.** With
> the per-tick swap landed (step 1, `8e364c8`), both triggers were logged side
> by side over the movement + corrector suites and the climb sweep (1000
> decisions; old trigger in charge). Two findings:
> 1. The old trigger accepted reference arcs penetrating up to **7.8 px** — the
>    authored parkour arc grazes its OWN lip corner from some entries (the
>    per-tick solve steers it off), and refused many arcs penetrating nothing
>    (median 1.3 px, min 0), almost all 1-tile Parkour at ~100 px/s.
> 2. Swapped in as the trigger: allowance ≥ 2 px keeps the suites green but 13
>    climbs time out (old: 1); 1 px matches the old deliveries but refuses a
>    plain 1-tile vault (`CorrectorAnchorTests.TriggerFeasibility_SlabOverApproach`).
>    The timeouts are TIMING failures — vaults started too close to the face
>    at speed; their arcs are clear, so no geometric allowance can see them.
>    Contacts with the climbed step's own lip can be excluded (like flush
>    starts), but that only fixes (1).
> Options: keep the old probe solve as the trigger (ManeuverCorrector stays,
> trigger-only); the dynamic check (tracker rollout on the probe — original
> D3 (a)); or the geometric sweep plus an explicit closed-form timing refusal
> (hop reaches lip height before the face arrives).
> **Decided 2026-10-16: keep the old probe solve as the trigger for now.**
> `ManeuverCorrector` stays (trigger-only); step 3's deletions wait on a
> revisit of the trigger.

### 5.4 Order inside the phase

1. Per-tick correction only: replace feedforward + `ManeuverCorrector.Apply`
   with the reference path (D2) + `ManeuverPathPolicy`; leave the trigger on the old loop.
   Gate: climb tests + per-engine sweep (memory: maneuvers run on every engine;
   compare lattice / qp / ref).
2. Trigger: implement D3 (reference sweep + allowance, sized by the old-vs-new
   sweep); delete `Delivers` and the probe `Run`.
3. Delete `ManeuverCorrector`, `BallisticPredictor.PredictGuided`,
   `BaselineFeedforward` if unreferenced, `CorrectorHorizon` if unreferenced,
   the render-only `ReferenceTrajectory` capture's `PredictGuided` call (replace
   with the reference polyline).

Done when: `ClimbArbitrationTests`, `MantleStateTests`, `ArcJumpStateTests`,
`DeliberateClimbTests`, `VaultJumpAndLipReproTests`, `SpeedInvariantTests`,
`StairClimbTests`, `CorrectorAnchorTests` / `CorrectorOperationsTests` (several
known red — compare against the baseline, not against green) pass; a 60 Hz
sweep over entry speed × rise height in each band delivers everywhere the old
loop did; no climb times out at `MaxLipManeuverTime`.

## 6. Phase 4 — TerrainCarried (cautious)

> **DONE 2026-10-16 at step 2.** The assist acted on 50 of 1633 carried frames
> (mean 11 vs the servo's ~790); `AmbientPolicy.Off` changed no test — the one
> trace difference is the drop after a ride ends (~100 px/s landing, was ~70).

Today: the ride-anchor servo is the whole control law; the ambient corrector
runs "clearance-only" (`AmbientPolicy.Default`, `FoldProfile.None`), which on
lattice means the old ballistic-prediction assist.

Order, stopping as soon as a step is good enough:
1. **Characterize.** Record what the assist contributes during rides (the
   ledger's per-channel force, `CorrectorLedger`) across `SproutLiftJumpTests`,
   `SproutPushTests`, `SproutCrushTests`, `StandingJitterTests` and a scripted
   avalanche ride. Pin current behavior with a trace-comparison test first.
2. **If the assist is near-inert: switch to `AmbientPolicy.Off`.** Deletes the
   dependency outright; one opinion about the trajectory, as the state's own
   header asks for.
3. **If it does real work:** a `PathTracker` clearance mode — no polyline, no
   band, no progress; terrain rows plus the redirect / contact-push channels.
   Needs `PathTracker.Solve` to accept "no path, no hover line" (today it
   assumes one or the other) — small.
4. **Full lattice ride (optional, later):** a `FoldProfile.Ride` with the
   support frame moving (the tracker already reads `supportVy`). Only if 2–3
   leave a visible problem.

Done when: the sprout/ride tests are unchanged against the baseline
(`SproutLiftJumpTests` has 3 known reds), and `TerrainCarried` never reaches
`BallisticPredictor` on lattice.

## 7. Phase 5 — cleanup

- Delete whatever §3–§6 left unreferenced (list in each phase).
- `BallisticPredictor` itself stays while the `qp` / `ref` / `lm` engines exist
  (their ambient path uses it). Retiring those engines is a separate decision.
- Docs: `CODEBASE_OVERVIEW.md` corrector section; the stale twelfth-pass note in
  `Plans/LATTICE_PATH_PLANNER.md` ("no state runs the qp coast path on it any
  more" — false for RunningJump/TerrainCarried until this lands);
  `Movement.cs`'s "three ways a state uses the solve" header becomes two
  (ambient fold, path tracker).

## 8. Out of scope

- Jump launches (velocity writes stay; §0).
- `LedgeGrab` (a static hold), `LedgeJump` (its own corner launch — fine under
  "jumps are hybrid"), `CoveredJump` (own contacts), `Stunned` / `Tumble`.
- Retiring the non-lattice engines.

## 9. Verification, every phase

- Groups: `movement` + `corrector` (`scripts/test-group.py`). Baseline on
  2026-10-16: **37 failing** across the two groups, none ledge-related —
  diff the failure list, don't count reds.
- New 60 Hz scenario test + a both-rates sweep per migrated state (lesson 5).
- Per-engine comparison for anything the climbs or dropdown touch.
- Perf: `MTile.Bench --corrector` before/after phases 3 and 4.
- Playtest checkpoint after each phase before starting the next (the pull
  needed two rounds of live feedback that no test predicted).

## 10. Size

Estimated in agent-session time (the LedgePull rework, the `PathTracker`
extraction and this plan took ~3 h together), excluding playtest rounds:

| Phase | Size | Main risk |
|---|---|---|
| 1 RunningJump | ~30–60 min | double launch if leg permission isn't explicit |
| 2 Dropdown | ~1 h | chain-to-grab hand-off geometry (lesson 4) |
| 3 Climbs | ~3–4 h | sizing the D3 allowance so the trigger is neither stricter nor looser than today; crest band answerability (lesson 1) |
| 4 TerrainCarried | ~30 min if step 2 suffices; ~2 h if step 3 | regressions in rides no test covers |
| 5 Cleanup | ~30 min | — |

All decisions are settled (2026-10-16): D1 explicit ground-channel permission,
D2 the existing climb clips, D3 reference sweep with a measured graze allowance,
both `UseReferenceClips` fallbacks deleted (done in phase 2).
