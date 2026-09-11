# Animation scene authoring: moving CoM and editable guides

Status: proposed, 2026-09-09. Planning only; no implementation in this change.

> **2026-09-09 partial landing:** the `edref` → `body_path` piece shipped ahead of the
> rest (`Animation/BodyPath.cs`). The channel was a pure rename — `edref` already held
> the com anchor's scene position — bulk-migrated across the 7 clips that carried it,
> with the editor's precedence (ReferenceArc, else track, else stationary) unchanged
> and a shared sampler (`TrySample`, `TryCycleDisplacement`, no cyclic wrap). Runtime
> placement still consumes only `com`; BodyPath.cs documents the current draw-vs-solve
> com.X asymmetry the rewrite must collapse.

## Intent

Author a stick-figure clip as a small movement scene: the body travels through a
fixed frame, feet meet meaningful surfaces, and the silhouette clears obstacles.
Convert that clip into a rabbit clip with shoulder and pelvis struts while
preserving its movement intent and scene. Let downstream animation code query
that intent, initially for diagnostics and eventually for step placement.

This makes sense: a static pose sequence hides the relationship between body
travel, foot travel, and terrain. A moving scene makes that relationship visible
and gives programmatic authors something concrete to validate. It does not, by
itself, guarantee collision-free motion against a different runtime obstacle.

Keep the editor update modest: extract the scene/placement responsibilities,
add a small dropdown and direct manipulation, and establish a shared data contract.
Keep simulation movement authority with the existing movement/corrector systems.
The same interaction pass should make endpoint attachments and contact annotations
discoverable, and remove the stick figure's helper feet once their data has a
proper endpoint home.

## What exists today

- `MTile.Demo/DemoGame.cs` combines input, clip editing, drawing, persistence,
  reference geometry, and placement in roughly 1,900 lines.
- Keyframe additions already include `com`, a root-local body placement anchor.
  It is an authored reference point, not a mass-weighted anatomical measurement.
- Dragging the CoM marker writes an `edref` scene placement track. It is saved
  with the clip but documented as editor-only. Dragging the rig root instead
  changes `com`, moving the pose relative to the body anchor.
- `ReferenceArc` can own scene placement instead of `edref`. The editor correctly
  chooses one source; it does not add them. Reference arcs use game pixels,
  whereas additions use rig units.
- The floor is implicit. One hard-coded obstacle appears for selected clip types;
  its offset lives in shared `.editor_view.json`, not in the clip.
- `Animation/AnimAdditionSampler.cs` already samples sparse point tracks smoothly.
- Runtime placement in `CharacterAnimator.cs` and foot offsets in
  `ClipStrideTrack.cs` subtract only `com.Y`. The editor subtracts both X and Y.
  Promoting moving scenes must explicitly handle this mismatch.
- The in-progress `scripts/sync-rabbit-derived.sh` retargets biped clips and bakes
  yaw into `rabbit_derived`. Its README makes biped the editable source and keeps
  manually maintained rabbit clips in a separate pool.

## Coordinate and ownership contract

Use three distinct concepts, with explicit labels in the editor:

| Concept | Meaning | Editable by |
|---|---|---|
| Pose anchor `com` | Body anchor expressed in the posed rig's root space | Pose/root drag |
| Body path | Position of that anchor in the fixed clip scene over time | CoM/path drag |
| Scene guides | Fixed floor and obstacle geometry in clip scene space | Guide selection and handles |

Camera pan/zoom changes none of these values. Scene coordinates use rig units,
X right and Y down, at canonical right-facing orientation. Display tile sizes
using the existing game-pixel/rig-scale conversion. Preserve the current scene
origin convention initially, avoiding a forced rewrite of existing clips.

For a rig point `q(t)`, pose anchor `c(t)`, and body path `p(t)`:

```
scenePoint(t) = p(t) + q(t) - c(t)
sceneCoM(t)   = p(t)
```

Apply camera transforms only after this calculation. Runtime placement relative
to the actual simulated body `B(t)` is:

```
worldPoint(t) = B(t) + facingAndScale(q(t) - c(t))
```

The authored path is an input to planning; adding it to `B(t)` again would double
count movement. Runtime terrain remains the authority for actual support.

## Proposed document additions

Prefer a typed optional `Motion` descriptor and optional `Scene` on
`AnimationDocument`, with a shared sampler in `Animation/`. Exact C# names can
be settled during implementation; their semantics should follow this plan.

- `Motion.Source`: `InPlace`, `Track`, or `ReferenceArc`. Exactly one owner.
- For `Track`, use a reserved scene-position point channel, provisionally
  `body_path`, on existing keyframes. Explicitly document its scene coordinates
  despite the additions container's usual root-local convention; require no parent.
  Reuse keyframe timing and interpolation rather than introducing a second timeline.
- For `ReferenceArc`, retain the existing arc name and adapt its sampling and
  game-pixel units to the shared scene contract. Do not silently fall back to a
  stale track if the requested arc is missing; surface the problem.
- `Scene.Guides`: stable ID, kind, position/size, optional label, and visibility/
  lock flags. First version supports a horizontal ground line and axis-aligned
  rectangles. Ground needs only a Y coordinate; rectangles need positive width
  and height. An explicitly empty list means no guides.
- Scene guides are reference data, not spawned runtime colliders. Rectangle tops
  can describe candidate support and rectangle interiors describe clearance.
  Contacts continue to describe which limb is planted and when.

Expose `SampleBodyPosition(phase)`, `DeltaBodyPosition(from, to)`, total authored
displacement, and the placement source. Missing intent must remain distinguishable
from an explicitly stationary clip. A derivative, if needed, must specify whether
it is per normalized phase or per second (`Duration` supplies the conversion).

Preserve scene and motion data in save/load, cloning, key insertion/deletion,
retiming, retargeting, and yaw baking. Scene data stays fixed when retargeting
between the current rigs at their common scale. If future rigs change units,
require an explicit conversion rather than inferring one from limb lengths.

## Editor interaction

Add a compact **Scene** dropdown in the header:

- Add ground / Add block: select the tool, then click to place; drag to size a block.
- Select guides: click a guide, drag to move, use edge/corner handles to resize.
- Duplicate / Delete selected guide; Hide / Lock; optional tile-grid snapping.
- Motion source: In place / Authored path / Reference arc.
- Show path, pose ghosts, contact marks, and physics body outline.

Show selected guide coordinates and dimensions in rig units with a tile-size hint.
Ground uses a draggable height handle. Use deterministic picking priority: header
UI, active handles, then the current edit mode's objects. Delete must act on the
selected guide only in guide mode, preserving existing keyframe deletion behavior.
Escape cancels placement or restores the pre-drag value. A committed edit marks
the clip dirty; guide visibility and locking do not change geometry semantics.

CoM drag moves the body along its scene path at the active keyframe. Root drag
continues to adjust the pose relative to that anchor. Label the modes so these
similar-looking gestures are understandable. Editing a sampled time requires
inserting/selecting a key explicitly. A linked arc remains owned by the arc editor;
offer an explicit “Bake arc to editable path” operation with a sampling-error check.

Default to a fixed scene camera while playing so displacement is visible. Offer
“Frame scene/path” and an optional follow view. Floor and blocks never follow the
body. Path dots, ghosts, and rendered poses must use the same placement sampler.

## Endpoint menus, elements, and contact annotations

### Interaction proposal

Click an endpoint to select it and show a small dropdown affordance beside it;
right-click opens the same menu directly. Preserve ordinary dragging for posing:
opening the menu must not require changing the existing drag gesture. At shared
joints, show the selected target by name and highlight the corresponding segment;
offer a short target chooser when several endpoints overlap.

Suggested menu, with friendly labels rather than serializer field names:

| Action | Result |
|---|---|
| Add element → Knife / Custom element | Attach a clip-local element to this endpoint |
| Add point → Contact point / Named marker | Create a named point attached to this endpoint |
| Contact → No slip / Planned support / External pin / Clear | Set the selected point's contact behavior for the chosen time range |
| Attached items → select item | Inspect, rename, reposition, or remove an existing item |

Selecting Contact on a bare endpoint creates or reuses its default contact point.
The user should not have to perform two setup operations for a simple foot plant.
Keep optional offsets collapsed until needed. Show attached items as small badges
and highlight them on hover; selecting a badge opens its properties. The Scene
header can offer an Elements/Points list as an alternative for crowded poses.

An endpoint identifies **where** something attaches. A point gives that location a
stable semantic name. A contact annotation specifies **when and how** it is used.
“Contact point” therefore defines a location; “No slip” enables behavior there.
Avoid presenting them as equivalent free-form tags.

### No slip and contact timing

The existing code uses `ContactLabel` with `SelfPlant`, `PlannedSupport`, and
`External`; no literal `NoSlip` or `ContactPoints` types were found in the inspected
C# source. Use “No slip” as the UI name for `SelfPlant`: capture a world point on
activation and hold it during the contact. Describe planned support as requesting
a terrain target, and external pin as requiring a target supplied by gameplay.
Do not allow contradictory source modes on the same point at the same time.

Show the edit scope directly in the menu/inspector: default to “This key → next
key,” matching the existing keyframe contact interval convention. Allow a selected
timeline range and whole-clip scope. Display colored contact bars beneath the
timeline, with point name, source, and weight (default 1). A range edit inserts
boundary keys as needed, preserves sampled pose/path there, and restores the prior
contact state outside the range. Defer draggable range handles if they make the
initial UI pass too large; explicit start/end fields are sufficient.

Removing a contact clears its interval, not the point or endpoint. Removing a
point must show its dependent annotations; deleting a knife must remove its owned
attachment/animation data without deleting the arm or unrelated markers. Treat
each such operation as one cancelable edit with a clear effect preview.

### Knife and other elements

“Add knife” should create the necessary clip-local orientation bone and render
attachment as one operation using the existing `ExtraBones` and `Attachments`
mechanisms. Pick an existing compatible knife asset/preset; if none exists, show
the asset selection explicitly rather than creating an invisible attachment.
Preview the grip at the selected endpoint, then allow drag-to-aim and size/rotation
adjustment. Properties include label, parent endpoint, asset, and active interval;
trail settings can remain an advanced section. A custom element uses the same flow.

Keep elements clip-local by default. Shared rig editing remains a separate explicit
operation. Real articulated or orientation bones remain useful for a weapon;
contact metadata alone should never require creating one. Stable element ownership
must cover generated bone names and attachment records, including the runtime's
union of extra bones across clips, where duplicate names currently deduplicate.

### Endpoint data contract

Add a small shared endpoint reference/resolver before removing helper bones.
Provisional shape: `EndpointRef { Bone, End }`, with precisely defined start/end
semantics, and a named point record containing a stable ID, endpoint reference,
role (for example left/right support), and optional local offset. Zero offset means
the exact endpoint. Store shared anatomical points with the rig and clip-specific
points with the clip; avoid copying anatomical definitions into every keyframe.
Contact intervals reference point IDs. Display labels can change without breaking
references. Retargeting maps anatomical roles/endpoint references explicitly.

Implementation must verify the resolver against the rendered segment geometry.
`SkeletonPose.ComputeWorld` composes node transforms; current contact consumers
use node translation even though the contact comment calls this the bone tip.
Do not assume that adding `Length` to a node transform, or merely renaming a node,
selects the endpoint the user sees. Use one resolver for picking, rendering,
contacts, IK, probe output, and attachment placement. An endpoint offset must use
the appropriate bone orientation, facing, and stretch consistently.

Legacy `ContactLabel.Node` must continue resolving to its exact current location.
New point references can be an optional field during migration; reject ambiguous
records containing conflicting old/new targets. Likewise, existing bone-bound
attachments keep their current transforms until explicitly converted.

## Remove the stick figure's helper feet

Recommended result: biped legs end at their visible lower-leg endpoints, with
named left/right support points there. No small foot segment is needed to carry
contact tags, support identity, or other metadata. Keep any actual rabbit foot
geometry that serves its silhouette or articulation; this change targets the
stick figure's workaround, not every rig's anatomy.

This requires more than deleting two JSON records. `Skeletons/biped.json` currently
contains short, nonzero `foot_l` and `foot_r` bones. `MotionProbe`, probe `addcom`,
and `TerrainSurfaces` include foot-name assumptions; IK, stride compilation,
clip contacts, extra-bone parents, and sprite bindings also need a dependency
audit. Removing them can move support positions and change inferred floor/CoM.

Proposed migration sequence:

1. Add endpoint-backed points and let contact/planner consumers resolve them while
   existing clips keep their legacy node targets. Query left/right support roles
   in utilities instead of assuming a bone called `foot_l` or `foot_r` exists.
2. Produce a dry-run reference report for the biped rig and clips: poses, contacts,
   points/vectors, element parents, bindings, and retarget mappings that use the
   helper feet. Report unresolved references rather than silently dropping them.
3. Convert a representative biped clip. Measure the old helper-node trajectory
   versus the intended lower-leg endpoint at keys and intermediate samples. If
   they differ, show the delta: snapping to the true endpoint is a deliberate
   authoring change, while a legacy offset may preserve a prior point temporarily.
   Do not promise exact preservation if the old helper has independently animated
   rotation/stretch; those cases need explicit baking or reauthoring.
4. Migrate biped clips and remove helper-foot pose entries and rig bones only after
   their references resolve. Recheck contact height, floor alignment, `com`, chain
   reach, and stance/swing behavior. Do not globally restamp CoM to hide a mismatch.
5. Update retarget mappings so biped support roles land on the intended rabbit
   support endpoints/points. Regenerate the derived pool from the migrated source;
   do not patch generated clips by hand. Audit manually maintained rabbit clips
   separately and preserve their behavior through legacy references where needed.

Success means an author can click a bare leg endpoint, choose **No slip**, and
see a planted interval without adding geometry; or click a hand endpoint, choose
**Add knife**, and immediately see/edit the attached element. Both operations
survive save/reopen, cloning, and stick-to-rabbit conversion.

## Moving loops and interpolation

Treat pose looping separately from scene travel. A walking cycle can repeat its
pose while advancing by `D = p(1) - p(0)` every cycle:

```
pExtended(n + phase) = n * D + p(phase)
```

Sample the final endpoint explicitly; do not modulo phase 1 to 0 before extracting
displacement. A one-shot clamps. Replaying one scene can reset visibly, while an
optional continuous preview accumulates displacement. Continuous loops need
matching endpoint velocities; validate the seam before presenting them as smooth.

Use the existing sparse cubic sampler initially, with its endpoint behavior
documented. Check clearance between keys because cubic interpolation can overshoot.
Do not assume `Loop=true` proves a clip is a locomotion cycle: the current biped
`stepup` carries that flag and needs an intentional review for the pilot.

## Downstream step-placement use

First expose and inspect intent without changing gameplay. Then add an explicit
opt-in consumer for one maneuver, rather than changing every clip at once.

At touchdown phase `td`, a prospective foot target can be formed from:

```
predictedBody(td) + facingAndScale(qFoot(td) - c(td))
```

Authored `p(td) - p(now)` supplies a preferred body displacement when appropriate.
Map it into the maneuver's actual frame, entry position, scale, and phase mapping;
use the existing movement prediction when authoritative. For reference-driven
maneuvers, avoid applying the reference arc a second time.

`ClipStrideTrack` should retain body-relative offsets and swing shape; the scene
path is a separate source of displacement. Update full X/Y anchor handling
consistently with runtime rendering before enabling it for a clip. Preserve the
legacy convention for unconverted clips if nonzero legacy `com.X` would change them.
Terrain queries and reach/clearance checks still decide whether a preferred target
is usable. Missing intent uses the existing planner behavior.

Guide-relative contact positions can later support obstacle adaptation, but stable
guide-to-runtime-surface binding and automatic trajectory fitting are outside this
editor update. A floor guide alone does not encode a planted foot; contact labels do.

## Refactor boundaries

Extract focused components from `DemoGame`, keeping it as the MonoGame host:

1. Scene placement and sampling: shared math, with arc loading outside pure sampling.
2. Scene guide editing: selection, hit tests, drag state, dropdown, and drawing.
3. Scene preview: grid, path, ghosts, and body outline using the shared transforms.
4. Endpoint inspection: shared selection/menu plumbing for attached elements and
   contact points, backed by a resolver in the shared animation/rig layer.

Keep existing pose and timeline machinery unless an integration requires a local
change. Avoid a general widget framework, wholesale editor rewrite, or full undo
system in this pass. New drag operations should support cancellation cleanly.

## Compatibility and rollout

1. **Extract without changing behavior.** Capture representative current clips and
   inspect placement, picking, save, and playback after the extraction.
2. **Add persistent guides.** Missing `Scene` retains the legacy floor/block preview;
   explicit `Scene` replaces it, including an empty scene. Materialize legacy guides
   on the first scene edit. Shared view offsets may seed that explicit conversion,
   but ordinary saves must not stamp one user's view settings into every clip.
3. **Add explicit motion intent.** Old clips keep legacy preview precedence
   (`ReferenceArc`, otherwise `edref`, otherwise stationary) without automatic runtime
   opt-in. Offer migration from `edref` to `body_path`, preserving sampled placement
   and removing conflicting old placement data only in the converted clip.
4. **Carry data through the pipeline.** Verify retarget and yaw bake preserve guides,
   path samples, contact timing, and anchor meaning. Keep generated files generated.
5. **Pilot one step-up scene.** Author approach, lift, passage over the edge, and
   landing with a moving CoM and fixed block. Check both biped and derived rabbit;
   different limb widths can require different clearance even with identical intent.
6. **Expose intent to runtime, then opt in.** Add diagnostics first. Only enable a
   step-placement change after X/Y anchor parity and real-terrain mapping are verified.

Run the endpoint work as a bounded companion sequence: resolver and menu first,
knife/contact editing next, then helper-foot migration. Complete it before the
final regenerated step-up pilot so that pilot exercises the intended authoring
workflow. This migration reaches shared consumers and deserves its own reviewable
change rather than being hidden inside the editor refactor.

New movement clips should explicitly choose a moving path or deliberate in-place
authoring. Idle and overlays remain valid stationary clips. Do not bulk migrate
the existing library as part of the pilot.

## Validation and completion criteria

- Save/reopen and clone preserve independently editable guides and motion metadata;
  old clips retain their previous placement and gameplay.
- Pan/zoom never edits scene data. Moving the body never moves the floor/block.
  Guide selection, resizing, deletion, and canceled drags behave predictably.
- Editor, probe, and runtime diagnostic sampling agree at endpoints and between keys,
  including nonzero `com.X`, facing left, scale conversion, and loop displacement.
- A step-up clip visibly travels upward and forward; marked stance feet remain on
  their intended surfaces and swing feet clear the edge throughout sampled motion.
  Show body-polygon and limb clearance separately. Sampled checks are diagnostics,
  not a continuous collision proof or a promise about sprite thickness.
- Retarget/bake preserves scene intent; inspect the rabbit result against the same
  obstacle. Source clips remain the reproducible input.
- Endpoint selection and badges identify the correct target at overlapping joints.
  Knife insertion/removal preserves unrelated rig data. Contact range edits preserve
  behavior outside their range and reject conflicting source modes.
- Foot removal leaves no dangling references. Endpoint positions agree across editor,
  probe, IK, and runtime, including rotated/stretched limbs and facing left. Compare
  legacy support trajectories and report intentional migration differences.
- Tests should target the shared coordinate contract, serialization, legacy fallback,
  endpoint/loop behavior, and pipeline preservation. Use manual editor checks for
  interaction; run planner regressions when the runtime opt-in is implemented.

The first useful delivery ends with an editable, persistent moving step-up scene,
endpoint-based contact/element editing, a stick rig without helper feet, a matching
derived rabbit preview, and a shared motion query. Automatic obstacle
adaptation and broad gameplay changes can follow as a separate, reviewable step.

## Implementation decisions log (chunk 3, 2026-09-11)

Choices made while building the editor track without the owner in the loop; each is
reversible and listed so they can be reviewed in one place.

- **Document shape.** `AnimationDocument.Scene` (`ClipScene { Guides }`) and
  `AnimationDocument.Motion` (`MotionSource?`: `InPlace | Track | ReferenceArc`), both
  optional and omitted from JSON when absent. `Track` is the existing `body_path` channel;
  no separate `Motion` object was added since the source is the only field it needed.
- **Legacy precedence** when `Motion` is null: ReferenceArc → `body_path` → stationary, with
  `ClipMotion.HasIntent` false only for the last case (missing intent ≠ declared in-place).
  A declared arc that cannot be resolved reports `ArcMissing` and samples zero; it never
  falls back to a stale track.
- **Com-marker drag on a legacy clip** still seeds a `body_path` track and leaves `Motion`
  null (the clip stays legacy-compatible); on a clip declared `InPlace` the drag is refused
  with a console hint. Declaring a source is a menu action, never a side effect of a drag.
- **Bake arc to path** clears `ReferenceArc` after writing the track and declares `Track`,
  so the clip has exactly one owner afterwards. The sampling-error check is printed, not
  gated: the author decides whether to add keys.
- **Guide picking priority**: corners, then edges / the ground line, then a block body.
  Locked guides are selectable but not editable; hidden guides are neither.
- **Legacy materialization** copies exactly the preview the editor was showing (floor at
  2·Radius/scale under the anchor; the one-tile block for Parkour/Mantle/ArcJump/LedgePull at
  its persisted view offset). Non-com clips with an explicit scene use the scene-anchor frame.
- **The playhead's body-radius ring** now draws for any moving source (arc or track), not
  only for arcs.
- **Escape** is layered: menu → guide cancel/tool exit → quit. **Delete** acts on the
  selected guide only in guide mode.
- **Verification**: this VM has no display or fonts, so the editor could not be run; the
  extraction was verified by compiling the editor sources and by a parity test
  (`AnimSceneTests.RootAt_MatchesTheLegacyEditorPlacement`) that checks the shared
  `ClipMotion` query against the old placement formulas on the shipped clips. The
  interaction itself needs the manual editor check the plan already calls for.

## Implementation decisions log (chunk 4, 2026-09-11)

- **Endpoint data**: `BoneEnd { Start, End }`, `NamedPoint { Id, Bone, End, Role, Ox, Oy, Label }`
  (`Animation/Endpoints.cs`). Rig-level points live in `Skeletons/<rig>.json` `Points`
  (`Skeleton.Points`, carried through composition); clip-level points in
  `AnimationDocument.Points`. `ContactLabel.Point` references a point id; `Node` stays as the
  legacy reference and resolves to that bone's End. A label with both that disagree is
  rejected by the resolver. Contact consumers pin bone tips only: a point that is not an
  exact End is ignored by the solver (reported through `BoneOf(..., out exactTip)`).
- **Support points**: `support_l` / `support_r` with role `support`. On the biped they are
  the lower legs' ends; on the rabbit they are its foot tips, because the rabbit keeps its
  foot bones (its skin binds to them) — retargeting maps by point id, so a biped clip's
  `support_l` label lands on the rabbit's own point without a mapping table.
- **Helper-feet removal (biped only)**: the dry-run report (`probe feetreport`) showed every
  foot tip exactly 0.246 rig units (0.15 px at game scale) from the lower-leg end in all 47
  clips, with 161 posed foot entries animated off bind but nothing else hung from the feet
  (no additions, attachments or extra bones). That is under the one-pixel gate, so the
  migration ran to completion: `probe dropfeet --write` relabeled 36 contacts to the
  support points and dropped 392 foot pose entries; the two foot bones were removed from
  `Skeletons/biped.json`; the two biped sprite bindings lost their stale foot entries (the
  skin skips unknown bones anyway); `rabbit_derived` was regenerated. The hand-authored
  rabbit pool only had its labels moved (`dropfeet --labels-only`).
- **Code that assumed a foot bone** now asks the rig: `TerrainSurfaces` adds the support
  points' bones to the policed tips; `MotionProbe.SupportBone` picks the toe per side
  (support point, else `foot_*` where it exists, else the lower leg); `addcom` uses it.
  `PoseIk.DefaultChain` is unchanged (the chain simply ends at the lower leg).
- **Editor endpoint menu**: click a joint to select its End and show a small `v` beside it;
  right-click opens the menu directly. Items: cycle target at a shared joint, Add knife (one
  operation: clip-local orientation bone + attachment, the groundslash1 shape), Add custom
  element (naming prompt), Add contact point (creates a clip point only when neither the rig
  nor the clip names the endpoint), Add named marker, Contact No slip / Planned support /
  External pin / Clear, and the scope toggle (this key → next key, or the whole clip; an
  interpolated playhead first samples a key). Attached items list the effects and clip
  points here; Delete removes a selected clip point together with its dependent labels
  (reported). M+click still toggles No slip, writing a point label when one exists.
  Timeline: colored contact bars per label over its keyframe interval (green no-slip, blue
  planned, orange external), keyed at the track's right edge. Range handles were deferred,
  as the plan allows.
- **Not done**: the sampling-error gate on `Bake arc` is a printed report; a Knife asset
  chooser (the only asset is `knife`); helper-foot removal on the rabbit rig.

## Implementation decisions log (chunk 3.5, 2026-09-11)

- **Solver**: `PoseIk.DragSession` (`Animation/PoseIk.cs`) — one `LeastSquaresSolver` and
  its arrays per drag; rows = 2 tip rows + n prior-A rows (0.2 rig units/rad toward the
  drag-start rotations) + n prior-B rows (0.05 toward the previous frame, which also warm
  starts). 40 LM iterations per frame on n ≤ 4 variables. Only rotations move; a
  keyframe's Stretch is preserved. Prior weights are public fields, left at values that
  barely resist reach (lever arms are 10–20 units/rad) but keep the return-home drift
  under 0.03 rad in the test.
- **Fold guard**: a one-sided bound on `*_lower` bones whose drag-start angle exceeds
  0.05 rad keeps the sign; a near-straight limb is free to fold either way. To be replaced
  by the rig-level joint-limit vocabulary when runtime §8 defines it.
- **Editor**: header box "IK drag: on/off" (`_ikToggle`, header-first picking); in mode a
  joint press on a non-root bone opens a session over `PoseIk.DefaultChain`; the drag
  branch steps the session in root-local rig units and captures the pose into the active
  keyframe (same dirty/save path as a rotate edit); Escape restores; release drops the
  session. RESIZE/STRETCH and com drags are untouched. Verified by compilation and the
  headless `AnimDragIkTests` only (no display on the build VM).
- **Not done**: the torso-extension modifier and graded up-chain weights (evaluate feel
  first); a distinct hover color for chain nodes.
