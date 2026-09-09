# rabbit_derived — GENERATED, do not hand-edit

Every clip here is a pure function of `SkeletonStates/biped/`, produced by
`scripts/sync-rabbit-derived.sh`: probe `retarget` onto the **biped_rabbit** rig
(inserts the shoulder/pelvis struts, re-authors rotations, verifies world
directions are preserved) followed by `bakeyaw` (pseudo-3D strut stretch from
the leg/arm scissor — see `Plans/ANIMATION_STRETCH_AND_REFERENCE.md`).

- **Edit the biped clip and resync** — a resync overwrites every clip in this
  directory. Hand edits here are lost on the next sync.
- The hand-authored rabbit pool stays `SkeletonStates/biped_rabbit/` (clips that
  have deliberately diverged from biped: the re-authored stepup, the ground
  combo presentation pass). Promote a derived clip there when it needs manual
  work; from then on it is maintained by hand.
- Inspect with the probe: `dotnet MTile.Probe/bin/Debug/net8.0/MTile.Probe.dll
  --rig biped_rabbit --dir rabbit_derived digest <clip>`.
