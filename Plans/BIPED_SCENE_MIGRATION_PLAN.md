# Biped clip scene migration

Proposed process, based on the working tree on 2026-09-16. No clips changed.

The infrastructure is already built: `ClipMotion` resolves motion, `BodyPath`
defines placement, the editor supports scene guides/path editing, and the probe
provides `bakepath`, `liftswing`, and `scenecheck`. This is chiefly an authoring
and validation pass, with a few tooling gaps to close first.

Of 47 clips in `SkeletonStates/biped`, only `stepup` has explicit `Motion`,
`Scene`, and `body_path`. Five already follow reference arcs: `parkour`, `mantle`,
`arcjump`, `ledgepull`, and `dropdown`. All 47 have a `com` anchor. Missing explicit
motion does not mean missing runtime support: `walk` already uses `PlannedSupport`;
`run` and `crouchwalk` still use `SelfPlant`.

1. **Classify each clip and declare its intent.** Record one motion owner:
   `Track` for authored travel, `ReferenceArc` for guided maneuvers, or `InPlace`
   for stationary/body-relative poses and overlays. Add explicit scene guides
   (an empty scene is valid). Preserve the placement contract:
   `root = body_path - com`, in rig units, +Y down. Keep overlays attached to their
   movement clip; they should not introduce independent body travel. Remove or
   reconcile inactive path data after comparing the old preview.

2. **Close the validation gaps before expanding the pilot.** In
   `Animation/ClipSceneBake.cs`, make `scenecheck` resolve reference arcs instead
   of passing a null arc provider; report missing arcs/tracks and unsupported or
   absent contacts explicitly. Handle persistent contacts rather than skipping
   them, and guard baking when no useful stance transitions exist. Add limb/body
   clearance diagnostics, or require visual inspection until those exist: today's
   checker only measures stance-point drift and swing-point penetration.
   Review `GaitTiming.CycleDisplacement`, which reads `body_path` directly without
   honoring `Motion`, so editor and runtime cannot select different motion owners.

3. **Migrate the ground gaits first.** Start with `walk`, then `run`, `crouchwalk`,
   and `walkback`; retain `stepup` as the stair reference. Correct touchdown/liftoff
   labels using the rig's `support_l`/`support_r` points (`walkback` has none).
   Preview `probe bakepath <clip> --flat --dry` for level gaits; use the 2-D bake
   for stairs. Ensure loops have an explicit t=1 endpoint and the intended cycle
   displacement, including negative travel for backpedaling. Bake, then adjust
   poses, `com`, and swing clearance with IK/`liftswing`; check between keys and
   across the loop seam. Baking derives a candidate from existing foot sweeps;
   it does not repair inconsistent double support or produce ballistic flight.
   Review planner opt-in separately from scene migration; changing `Motion`
   does not convert `SelfPlant` contacts to `PlannedSupport`.

4. **Author obstacle and airborne scenes.** Make the five existing arc choices
   explicit, add floor/ledge/block guides, and pose against them. Check clip/arc
   duration alignment, takeoff, landing, and hand support; do not infer a vault
   from foot sweeps. Use the editor's arc-to-path bake only when an independently
   editable path is intended. For jumps, falls, and wall moves, choose a
   representative movement trajectory or explicitly retain a body-relative pose.
   Review maneuver-driver grip targets separately: scene guides are not runtime
   colliders or automatic bindings to real ledges.

5. **Validate and propagate in small batches.** Compare editor ghosts and probe
   reports before/after; set per-clip drift/clearance limits and check loop seams.
   Extend `AnimStepUpSceneTests` and `AnimSceneTests` for migrated behavior; run
   `scripts/test-group.py animation` and relevant animation-bench scenarios
   (flat travel, stairs, parkour, both facings, transitions, and overlays).
   Regenerate `rabbit_derived` with `scripts/sync-rabbit-derived.sh`, after
   reconciling existing local edits there; separately review hand-authored
   `biped_rabbit` clips and validate retargeted contacts/clearance.

Done means each clip has deliberate motion/scene intent and passes the checks
appropriate to its role. Physically plausible authored scenes still need runtime
terrain adaptation: simulation/correctors own body motion, and the animation
planner/IK adapts support. Matching arbitrary obstacle geometry remains separate
work; a successful bake alone is not a physical-validity guarantee.
