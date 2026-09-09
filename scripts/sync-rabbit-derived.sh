#!/bin/sh
# Regenerate SkeletonStates/rabbit_derived/ — the machine-made rabbit clip pool,
# a pure function of SkeletonStates/biped/ (see rabbit_derived/README.md).
#
#   scripts/sync-rabbit-derived.sh          # rebuild the whole derived pool
#
# Pipeline per clip: probe retarget biped -> biped_rabbit rig (inserts the
# shoulder/pelvis struts, direction-preserving re-authoring, verified), then
# bakeyaw (pseudo-3D strut stretch from the leg/arm scissor). Re-runnable;
# every run overwrites the whole directory's clips from the current biped pool.
# DO NOT HAND-EDIT clips in rabbit_derived/ — edit the biped clip and resync,
# or move the clip to the hand-authored SkeletonStates/biped_rabbit/ pool.
set -e
cd "$(dirname "$0")/.."

dotnet build MTile.Probe --nologo -v q
P=MTile.Probe/bin/Debug/net8.0/MTile.Probe.dll

dotnet "$P" retarget biped_rabbit --out rabbit_derived
dotnet "$P" --rig biped_rabbit --dir rabbit_derived bakeyaw
echo "sync-rabbit-derived: done (SkeletonStates/rabbit_derived/)"
