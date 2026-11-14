# rabbit_derived — optional side pool

`scripts/sync-rabbit-derived.sh` now regenerates the live pool,
`SkeletonStates/biped_rabbit/`, directly from `SkeletonStates/biped/` (probe
`retarget` + `bakeyaw`), keeping only the clips named in the script's `KEEP`
list (stairs, stepup) hand-authored.

Pass a directory name to write a throwaway side pool here instead, for
comparison: `scripts/sync-rabbit-derived.sh rabbit_derived`. Nothing in the game
loads this directory.
