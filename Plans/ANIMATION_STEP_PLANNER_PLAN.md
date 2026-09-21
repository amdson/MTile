# Clip-driven step planning

Status: proposed design, not implemented. September 2026.
Reviewed against the solver code 2026-09-05; amendments from that pass are folded in below
(corrector-sourced prediction, the joint-limits dependency on ANIMATION_SOLVER_PLAN §11.6
Phase 4, retiring the cadence workarounds as an explicit deliverable, body-shrink geometry).
Concrete build plan: [ANIMATION_STEP_PLANNER_IMPL.md](ANIMATION_STEP_PLANNER_IMPL.md)
(modules, integration points, phases P0–P4, tests).

## Objective and scope

Make semi-procedural walking and stair traversal more intentional without adding
terrain-specific choreography rules or making clip authoring substantially harder.

Treat the movement controller as fixed. All planning is render-only: it reads the
body's actual movement and terrain, but never changes physics, hover height, input,
or simulation state. Preserve the existing clips, animation layering, and numerical
pose solver where practical.

The central change is to represent a **step** explicitly. The authored clip supplies
timing, preferred placement, and style; a planner chooses feasible terrain support;
the pose solver makes bounded adjustments to realize that plan.

This cannot guarantee convincing planted locomotion along every physics trajectory.
If the body passes too close to a block for a plausible leg pose, the animation must
degrade deliberately rather than folding arbitrarily to preserve contact.

## Current system and its limitations

- `Animation/ContactLabel.cs` defines `Node`, `Weight`, and `Source`. Keyframes carry
  contact labels, interpreted as intervals with feathered transitions.
- `CharacterAnimator.RefreshContacts` captures a newly active foot from the sampled
  pose, then `SnapToSupport` adjusts its height toward a nearby support plane.
  This creates a target when contact begins, not during the preceding swing.
- Although `ContactSource.External` exists in the schema, this refresh path creates
  `SelfPlant` contacts. A new enum value alone is not a working planner.
- Planted-contact residuals participate in a combined solve over phase, root offsets,
  and joint corrections. Contact timing and cadence can therefore fight each other.
- Existing clip labels include compensations for feathering and cadence stalls;
  they are not necessarily literal anatomical touchdown/liftoff events.
- `TerrainSurfaces` extracts local collision planes around the previous emitted pose.
  Those planes lack the finite support bounds and persistent identity needed to select
  future footholds.
- Clip changes currently clear contacts, potentially losing a valid world-space plant.

Relevant code: `Animation/AnimationDocument.cs`, `Animation/CharacterAnimator.cs`,
`Animation/CharacterAnimator.Constraints.cs`, `Animation/TerrainSurfaces.cs`, and
`MTile.Probe/Program.cs`.

## Authoring contract

**Animate a representative gait and mark when each foot should be planted.**

Do not require authors to specify world-space destinations, stair dimensions, terrain
classes, procedural swing curves, or per-step solver weights.

Proposed opt-in annotation, retaining existing defaults:

```json
{ "Node": "foot_l", "Source": "PlannedSupport" }
```

*(Shipped as proposed, then removed 2026-09-20 once the library was being re-authored: the
runtime plans every contact span of a locomotion clip when terrain is present, so no
per-point opt-in exists. The rest of this section describes intent that still holds.)*

The exact name is provisional. Its meaning is: this interval requests terrain support
for this node; the runtime chooses the support point. It is not a promise that support
exists, nor a command to capture wherever the foot currently happens to be.

Absence of that foot's label denotes swing/no requested support. Consecutive labeled
intervals form a stance. Keep `Weight` optional and defaulted; authors should not need
to tune it for ordinary walking. Rig-specific limb chains and limits belong in rig
metadata or shared configuration, not repeated in each clip.

Retain legacy `SelfPlant` behavior for non-migrated clips. Explicitly review contact
timing when opting a clip in: old early-release workarounds must not silently become
the new intended liftoff schedule. Authoring tools should eventually support marking
either foot independently, including double support; the current single-contact CLI
operation is not a sufficient long-term interface.

## Extract intention from the clip

Compile opted-in labels and sampled motion into a cached, per-foot stride track at
clip load time. This is derived runtime data, not another hand-maintained asset.

Extract:

- Touchdown and liftoff phases from stance interval boundaries.
- Preferred foot placement relative to the body at touchdown and liftoff.
- The authored swing trajectory between those events, including lift and progression.
- The original pose as the posture/style reference for final correction.

Use the same placement convention as the live renderer: named `com` anchor, rig
scale, facing, and body-to-root transform must agree. `com` here is an authored
placement anchor, not a measured physical center of mass. Do not extract offsets from
raw FK and assume they are already body-relative.

Handle cyclic seams, flight intervals, and persistent stance explicitly. Validate
missing nodes, invalid event ordering, and zero-duration swings. Persistent stance
(such as idle) needs support maintenance, not an invented next stride. Rebuild the
cache after clip edits/hot reload.

For swing reconstruction, retain motion relative to its authored endpoints rather
than replacing it with a generic parabola. Account for the authored body-relative
frame when deriving that representation. Coincident endpoints need a defined
fallback so normalization never divides by a zero stride length.

## Runtime planning

Proposed pipeline:

```text
clip + contact labels -> cached stride tracks
                                  |
actual body motion + terrain -> per-foot step planner
                                  |
                       stance and swing targets
                                  |
authored/composed pose ------> bounded pose correction -> rendered pose
```

Maintain per-foot state: current stance support, swing start, selected landing
support, expected touchdown, and planning validity. Only plan the next landing;
multi-step search is outside the initial scope.

### Predict and choose support

1. Estimate time until authored touchdown from the nominal gait rate.
2. Predict body placement over that short horizon using observed movement. Start
   with a conservative velocity-based estimate, continuously corrected from actual
   motion; do not claim to know future player input or replay/mutate the simulation.
   **Prefer the corrector's own planned path over raw extrapolation where available**:
   the lattice engine already solves a short-horizon path nearly every tick
   (`LatticeTracker`'s solved path, exposed via `CorrectorDebug`). Reading it is a
   legal render-side sim read (same direction as reading body position), and velocity
   extrapolation is at its worst exactly where placement matters most — step-ups,
   vault entries, landings — because the corrector reshapes the trajectory there.
   Keep the velocity estimate as the fallback when no path applies (maneuver states
   that own their trajectory, hitstun, carried states). Deliberately do NOT build on
   `BallisticPredictor` for this: it is excluded as a prediction source for the
   planner (owner's call, 2026-09-05).
3. Transform the authored touchdown offset through that predicted body placement.
   This gives a preferred destination, not a constraint.
4. Query nearby exposed support segments and select a feasible point on one of them.

Generic feasibility checks should include finite tread bounds, space for the foot,
leg extension **and compression**, and swing clearance. Check the anticipated support
interval as well as the touchdown instant where practical: a foot reachable at landing
may become implausible immediately afterward as the body advances.

Derive extension/compression limits from rig dimensions (rig metadata), not constants
calibrated to a particular silhouette. The 2026-09-04 body shrink makes this concrete:
standing ground clearance grew to ~14.4px, so a 1-high block can pass under the physics
body while the feet must still visibly clear it, and planted-leg extension at hover is
slightly longer than the old silhouette assumed. Swing-clearance and extension checks
earn their keep sooner than this plan originally anticipated.

Among feasible candidates, prefer proximity to authored placement, modest pose
deformation, and continuity with the previous selection. Use hysteresis to avoid
switching treads every frame. Allow smooth target updates during swing, with reduced
late-swing changes unless the existing target becomes invalid.

These are shared rules about support and anatomy, not special cases such as
"if a staircase is exactly one block high, place the left foot here."

### Reconstruct swing

Reconstruct the authored swing between the actual takeoff and planned landing points.
Preserve its timing and characteristic shape; add only the clearance needed to pass
intervening geometry. Endpoint reachability alone is insufficient: sample/check the
path and resulting limb clearance. A toe clearing a corner does not prove the shin
or knee clears it.

Do not promise that arbitrary obstacles can be cleared by increasing arc height.
If a swing cannot fit within pose limits, reject or abandon that landing plan.

### Maintain and release stance

At touchdown, establish contact only with valid support and an acceptably close foot;
do not instantly pin a distant target simply because phase crossed an event. Keep the
support point fixed during stance, subject to terrain validity and pose feasibility.

Terrain destruction, excessive reach/compression, or a large motion discontinuity may
invalidate a contact. Release it deliberately; never preserve an anchor on missing
terrain. Initially support static tiles. Supporting moving geometry later requires
provider identity and a support-local anchor, not merely a frozen world point.

## Terrain query boundary

Add a small read-only support query for finite exposed segments near predicted landing
locations. Return bounds, normal, and support identity sufficient for revalidation.
Keep this separate from the existing local no-penetration plane extraction, though
they may share geometry helpers.

Start with static tile tops. Growing terrain and moving platforms need explicit
support policies before being enabled; do not silently treat changing geometry as
permanent support. The first slice should report unsupported cases clearly.

## Integration with the existing solver

The 2026-09-05 code review confirmed the solver is structurally ready for most of this.
The composite objective is already `ISolveConstraint` blocks with a frozen-rows-per-solve
contract and one shared point-Jacobian primitive (`PointJacobianColumns`), so the swing
residual is two rows per foot on existing machinery, with its target frozen per solve
like everything else. Swing targets not inheriting `SkipPair` collision exemptions maps
directly onto how those exemptions are keyed to planted contacts today. On the terrain
side, `TerrainSurfaces` already enumerates exposed faces per tile (including sprout
volumes and buried-tip exits), so the finite-tread query is an extension of that
enumeration, not new geometry code. The risk in this plan is concentrated almost
entirely in the cadence-ownership migration below.

Reuse the numerical solver, but distinguish two different requests:

- **Stance:** remain at a fixed support point (no-slip/ground contact).
- **Swing:** follow a moving trajectory target while preserving the authored pose.

A swing target must not create a planted contact, drive no-slip cadence, or inherit
contact-specific collision exemptions. Planned stance can reuse much of the existing
contact residual machinery. Swing needs its own target residual and appropriate
priority relative to collision and pose limits.

The planner owns contact lifecycle and nominal progression. Permit only bounded
solver timing correction, with an explicit rule for crossing events; do not let
preserving an old plant stall liftoff indefinitely. Freeze discrete support choices
and lifecycle state during each numerical solve. Reconcile accepted phase advancement
with events afterward, including frames that cross more than one event.

This cadence interface is a substantive part of the change, not something solved by
adding a target field. Double support must not revive opposing-foot cadence deadlocks.

Be clear-eyed that this is an **inversion of who drives Δφ**, replacing machinery, not
layering over it. Today planted contacts drive phase, and a whole archaeology of patches
in `RefreshContacts` exists because of that coupling: the time-fade release floor (the
foot-swap deadlock), the engage ramp (the landing jerk), weights frozen inside the solve
(the self-deleting-constraint free-run), the momentum prior and phase accel box. Each was
tuned against φ-driven weights; under a planner-owned touchdown schedule some become dead
weight and some become actively wrong. Treat "which patches die" as an explicit
deliverable of the step-3 slice — otherwise the work becomes debugging interference
between the old release logic and the new lifecycle.

The pose-feasibility side of this section — leg reach/compression limits, "preserve the
intended knee bend," bounded correction under anatomical limits — presupposes joint-limit
machinery that is still an **open item in `ANIMATION_SOLVER_PLAN.md` §11.6 Phase 4**.
Step 3 builds reach/compression limits anyway; these are the same piece of work. Merge
the two roadmaps and build limit handling once, not twice.

Keep root correction bounded around the unchanged physics body. Apply anatomical
limits and preserve the intended knee bend; do not let a strong contact weight buy
arbitrary folding. An infeasible target should produce a diagnostic and fallback,
not an ever-larger correction.

For transitions, preserve compatible planted supports by foot identity instead of
unconditionally clearing them. Choose one owner for foot intent when layering clips;
upper-body overlays should not independently create a second locomotion schedule.
The final composed pose still needs constraint evaluation.

## Fallback policy with fixed movement

The planner cannot guarantee all of: exact body tracking, perfect plants, no terrain
intersection, and anatomically plausible poses for every supplied body path.

Prefer, in order:

1. A nearby feasible landing with modest deviation from the clip.
2. Bounded visual correction while maintaining a valid contact.
3. An explicit missed/released contact or limited slip, preserving plausible joints.

Never fabricate mid-air support or indefinitely pin a foot to an impossible target.
Falling back to authored airborne motion does not create a physical hop or alter the
movement controller. Report infeasibility so remaining movement/animation mismatches
stay visible rather than disappearing into solver tuning.

## Implementation sequence

1. **Extract intent without changing playback.** Add opt-in metadata and stride-track
   compilation. Expose touchdown/liftoff and body-relative trajectories in probe output.
   Validate extraction against existing raw clips and placement conventions.
2. **Plan without deforming.** Add static support queries and per-foot target selection.
   Wire the lattice-path prediction source (with the velocity fallback) here, where
   its accuracy can be judged against the overlays before any pose depends on it. Overlay
   preferred targets, selected treads, predicted touchdown, and rejection reasons on live
   animation strips. Check target stability before involving IK.
3. **Integrate one gait path.** Migrate `walk` and `stepup`; add separate swing residuals,
   planned stance contacts, and bounded cadence ownership. Keep legacy clips unchanged.
   Add reach/compression limits and explicit contact-failure handling in this slice —
   built once, jointly with the §11.6 Phase 4 joint-limit item, not as a parallel
   implementation. Deliver an explicit disposition list for the existing cadence patches
   (time-fade release floor, engage ramp, frozen weights, momentum prior/accel box):
   which survive under planner-owned lifecycle, which retire, with the behavioral test
   that justifies each call.
4. **Transitions and broader coverage.** Preserve supports across walk/step transitions;
   test stop/start, reversal, flight, and overlays. Expand to other gaits only after the
   vertical slice works. Handle dynamic supports separately.
5. **Simplify authoring.** Extend the probe/editor to mark physical contact intervals
   directly and diagnose malformed tracks. Remove migrated cadence workarounds only
   after behavioral tests demonstrate they are unnecessary.

Provisional implementation units: `ClipStrideTrack` (derived data), `StepPlanner`
(render-only per-character state), and a finite support-query helper. These names
are suggestions, not a requirement for a broader animation-framework rewrite.

## Verification and observability

Use both raw stick-figure clips and in-game rabbit strips; a clean raw clip alone
does not verify body placement, planning, layering, or terrain correction.

Tests should cover:

- Track extraction at loop seams, repeated contacts, flight, and persistent stance.
- Body/root/`com`/scale/facing agreement during extraction and runtime reconstruction.
- Correct finite-tread selection, stable targets, and invalidation after tile removal.
- Flat ground, single steps, repeated stairs, descent, stopping, reversal, and abrupt
  body motion; identical recorded motion when comparing planner variants.
- Phase advancement without deadlocks, including double support and crossed events.
- Bounded stance slip, touchdown error, swing penetration, joint compression/extension,
  root offsets, and discontinuities during clip transitions.
- Legacy clips remaining on the old path, and no animation writes into simulation.

Add strip/debug overlays for actual body position, visual root, nominal foot target,
selected support, swing path, contact state, and feasibility failures. Establish
numerical tolerances from rig scale and baseline captures rather than inventing
universal pixel thresholds in this planning document.

Success is a repeatable improvement in foot placement and plausible posture with
simple contact authoring—not merely zero solver residual or a staircase-specific pass.

## Precedents and reading

- [Rune Skovbo Johansen, Automated Semi-Procedural Animation for Character Locomotion
  (2009)](https://runevision.com/thesis/): foundational reference for extracting locomotion
  structure and foot trajectories from authored motion.
- [Valve, Character Locomotion in Half-Life: Alyx
  (SIGGRAPH 2021)](https://media.steampowered.com/apps/valve/2021/Half-Life_Alyx_Locomotion_Slides.pdf):
  production precedent for stride-relative foot motion, predicted footsteps, runtime
  retargeting, and contact-aware transitions. Start with slides 10–34 and 45–55.
- [Ubisoft, Fitting the World: A Biomechanical Approach to Foot IK
  (GDC 2016)](https://www.gdcvault.com/play/1023316/contactUs): relevant discussion of
  reactive IK artifacts and predictive adjustment.

The architecture above is a proposed adaptation for MTile, not a verbatim implementation
of any one reference. Motion matching, machine learning, full physical character
simulation, and movement-controller changes are outside this plan.
