#!/bin/sh
# Regenerate the rabbit/badger clip pool (SkeletonStates/biped_rabbit/, the one the
# game loads for AnimationRig biped_rabbit) from SkeletonStates/biped/.
#
#   scripts/sync-rabbit-derived.sh                  # rebuild biped_rabbit/ in place
#   scripts/sync-rabbit-derived.sh rabbit_derived   # write a side pool instead
#
# Clips named in KEEP below are hand-authored for the rabbit and are NOT overwritten.
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

# Hand-authored rabbit clips that have deliberately diverged from biped: kept out of
# the overwrite (saved aside, restored after the pool is regenerated).
KEEP="stairs stepup"
OUT="${1:-biped_rabbit}"
TMP=$(mktemp -d)
for c in $KEEP; do
  [ -f "SkeletonStates/$OUT/$c.json" ] && cp "SkeletonStates/$OUT/$c.json" "$TMP/"
done

dotnet "$P" retarget biped_rabbit --out "$OUT" | grep -v maxDirDev
# Strut width: the ORDER law (bakeyaw --turn) on every clip — the side whose hand/foot
# leads is the front side, so the struts mirror whenever the biped clip has its _l limb
# ahead. Without it that limb attaches on the far socket and crosses the whole body.
# See BakeYaw in MTile.Probe/Program.cs.
dotnet "$P" --rig biped_rabbit --dir "$OUT" bakeyaw --turn > /dev/null
for c in $KEEP; do
  [ -f "$TMP/$c.json" ] && cp "$TMP/$c.json" "SkeletonStates/$OUT/"
done
rm -rf "$TMP"
# The kept clips do NOT get the order law: their feet are hand-planted against a step, and
# changing hip width moves the feet (it turned two stairs/step-up tests red when tried).
# Two clips with one Type both bind to the same action and one silently shadows the
# other (a leftover airspinstab stub typed AirWhipKick hid the real kick on this rig).
DUP=$(grep -h '"Type"' "SkeletonStates/$OUT"/*.json | sort | uniq -d)
if [ -n "$DUP" ]; then
  echo "sync-rabbit-derived: DUPLICATE clip Type in SkeletonStates/$OUT — one shadows the other:" >&2
  echo "$DUP" >&2
  exit 1
fi
echo "sync-rabbit-derived: done (SkeletonStates/$OUT/, kept by hand: $KEEP)"
