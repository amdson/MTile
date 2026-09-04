# Avalanche Riding v2 — Test-First Plan

Supersedes `AVALANCHE_RIDING_PLAN.md` (v1). v1's diagnosis survives; most of its
machinery doesn't. This version starts from an instrumented test harness, fixes the
one demonstrated terrain bug with the minimum lineage state, and treats **block
creation order** as a first-class tuning lever — the final avalanche shapes are good
and must be preserved, but the *order* cells grow in is negotiable if that's what
makes the front rideable at arbitrary angles.

## Spirit, restated as invariants

The fiction: the player enters a moving body of earth, rides its front, steers within
its frame, and jumps out with its momentum.

1. **Shape is sacred, order is not.** The set of cells an avalanche ultimately
   commits stays exactly what it is today. Growth *timing and order* within that set
   may be reordered freely to make the front coherent.
2. **A ride is one run.** One avalanche, one `TerrainCarried` run — no state flapping
   at cell boundaries.
3. **The frame must read true at any angle.** Waves are launched at arbitrary angles
   and narrow as mass depletes. A no-input rider tracks the front; their velocity
   points along the wave, not along whichever axis-aligned cell touched them last.
4. **Momentum survives the jump**, including during a between-cell evidence gap.
5. Mere proximity never grabs the player; untagged building, bursts, and enemy
   pillars keep today's symmetric-shell behavior; everything snapshots and replays.

## Carried over from v1

- The diagnosis: backward growth is **far-side static ignition** — a pending cell
  near pre-existing terrain promotes off the wrong side (`ChunkMap.TickSprouts`
  pass 2 + `SolidFaces`).
- The static-root dot test (solid terrain may parent a wave cell only when it is
  not ahead of it along the sweep) — kept as *optional polish only*; Part 3's
  schedule gate makes it unnecessary for correctness.
- Rejecting `TileMassField.MassAt` derivatives as a flow source (accumulation
  signal, not motion).
- Wave identity: a ride locks to one wave; a crossing wave requires a fresh contact.
- Explicit carrier-velocity handoff into jump entry (grace-window jumps must not
  depend on contact timing).
- Ride persistent state lives in `MovementVars`.

## Dropped from v1, and why

- **Five-field `AvalancheStamp`.** `MassBall` has no gravity and scalar drag only
  (`Entities/MassBall.cs:79`): a wave's direction is a per-wave *constant*. Per-cell
  `Direction`/`Speed`/`AlongSweep` stamps store what one per-wave record plus a dot
  product recomputes.
- **`(DepositStep, AlongSweep)` lexicographic order + ε semantics.** Machinery for a
  curving ball that doesn't exist, solving a sprout-vs-sprout backward-growth mode
  nobody has demonstrated. If the ordering tests below fail with only the static-root
  fix in place, revisit — with the failing test as the spec.
- **`AvalancheHistory` map.** Promotion is driven by the finalizing node, which is
  still in hand with its tag inside `TickSprouts` pass 2. Check it there.
- **Per-`(cell, WaveId)` mass buckets.** Splitting buckets changes how mass pools to
  `Threshold` — overlapping strokes would build less terrain per unit mass. That's a
  mass-economy change, not bookkeeping. One winning tag per bucket.
- **`lerp(macro, local, confidence)` flow blend.** Wrong limit: evidence down must
  mean claimed flow down, never "full idealized ball velocity." Macro direction may
  *rotate* the local estimate; it never props up its magnitude. The existing
  `FlowCenterBias` fade is correct — keep it.
- **The position-anchor servo spec (v1 §5).** Deferred as a contingent hypothesis
  (see "Contingent" below), not built. `TerrainCarriedState`'s class comment is a
  graveyard of position-flavored controllers; we don't re-enter that on speculation.

---

## Part 1 — The harness (build this first)

`MTile.Tests/Sim/AvalancheRideTests.cs` + `AvalancheOrderingTests.cs`. Register the
term `"Avalanche"` in `scripts/test-group.py` — ride tests under `movement`,
ordering under `terrain` — or the classes silently run only in the full sweep.

### Scenario shape

Flat floor (ascii terrain, `SimTerrain.FromAscii`), player standing at rest near the
eruption origin, in the wave's path. A wave launches over their position at angle θ.
Loop style: `SproutLiftJumpTests.Run` (manual frame loop with constraint sampling),
extended with a `HeadlessEntityWorld` so a real `MassBall` can fly, or a scripted
depositor (below) when the test wants the wave decoupled from ball tuning.

Two wave drivers, both worth having:

- **`ScriptedWave`** — a test helper that walks a ray at angle θ with the ball's
  drag-matched speed decay and calls `chunks.Mass.Deposit(...)` each frame with a
  minted wave id. This is exactly what `MassBall.ProjectileUpdate` does, minus the
  entity, so the angle sweep is a pure function of (θ, speed, mass budget).
- **Real `MassBall`** spawned through the entity world, for a few end-to-end tests
  pinning that the real thing matches the scripted one.

### The angle sweep (the test the mechanic is designed against)

`[Theory]` over θ ∈ {20°, 30°, 45°, 60°, 75°, 90°} × speed {slow, fast} × budget
{small, large}. Small budgets are the narrowing case — the front thins and dies
mid-ride, which is where riders currently fall out.

Per run, compute from the trace + per-frame terrain sampling:

| Metric | Definition | Spirit clause |
|---|---|---|
| **Catch rate** | did the wave's passage over the player start a carried run at all | "hook into the flow" |
| **Transport ratio** | (player displacement · dir) / (front displacement · dir) over the ride window; front = max along-sweep over the wave's growing volume centers | "base to crest with no input" |
| **Frame alignment** | mean angle between player velocity and wave direction while carried (frames with speed above a floor) | "not pushed in the wrong frame" |
| **Dropout** | player more than k tiles behind the front while the wave is still growing, or carried run ends before the wave does | "falls out" |
| **Continuity** | number of distinct `TerrainCarried` runs (state-transition count) — target 1 | one ride, one run |
| **Jerk** | per-frame Δv bound, the `SproutLift_…_SmoothlyOneTile` Δy-cap pattern generalized to both axes | tile jitter must not dominate |

Then the steering, jump, and negative cases:

- **Relative steering**: hold-right during a 60° ride shifts position along the wave
  frame while retaining most of the transport ratio.
- **Jump inheritance**: flat-ground jump vs live-contact ride jump vs jump during an
  evidence-gap frame (find one from the trace) — the latter two agree on carrier
  momentum. Y-down signs: the carrier's upward velocity is negative; asserting
  magnitudes, not signs, has burned tests before.
- **No proximity theft**: wave passing one tile beside a standing player never
  enters carried.
- **End of wave**: budget exhausts mid-ride → decays into falling/standing with no
  stale carry force (bound post-wave `AppliedForce`).
- **Rollback mid-ride**: snapshot during a ride with carry vars live, advance,
  restore, replay to identical trace + terrain checksum. v1's rollback test covered
  terrain only; the new `MovementVars` fields are the likelier desync.

### Ordering tests (`AvalancheOrderingTests`)

- **Back-ignition repro, written first, red against today's code**: wave toward a
  wall/platform; record each wave cell's first-growth frame; assert the projection
  of growth-start events on the sweep axis never runs ahead of the ball's recorded
  passage — no far-end ignition while the front is still mid-field. (Asserting "no
  backward promotion faces" is too strong: a wall-adjacent cell growing out of the
  wall *on schedule* is legal — the bug is the reverse *race*, not local backward
  volumes.) This is the spec for Parts 2–3.
- Manual/untagged requests keep symmetric shell semantics (pin current behavior).
- **Shape invariance**: for each reordering lever in Part 3, the final committed
  cell set equals the lever-off run's, cell for cell. This is invariant #1 as a test.

### Two-stage pinning

Land the harness with **diagnostic output and loose assertions** (report the metric
table per run, assert only structural facts: catch, single run, shape invariance,
rollback). Pin numeric thresholds — transport ratio floors, alignment ceilings, jerk
caps — only after the feel pass settles, per the standing rule about not retuning
tests mid-tuning. The metrics exist from day one so tuning is measured, not vibes;
they become law once the vibes are signed off.

---

## Part 2 — Minimal provenance

New sim state, in full:

- `WaveId` (an `EntityId`) and `RequestFrame` (sim frame at first request) on
  `TileSproutNode`; `WaveId` on each mass bucket entry — single winning tag, first
  contribution wins; `None` = ordinary building.
- A small per-wave table `WaveId → Direction` (unit vector — the ball never turns,
  so one constant per wave), used by the ride's direction conditioning (Part 4).
  Pruned when a wave has no live ball, buckets, or nodes. In `TerrainSnapshot` +
  checksum.

`MassBall` registers its direction once and passes its id through
`Mass.Deposit → Spill → TryRequestTile`; the request stamps the node with the
current frame. `BreakCell` face-clearing, foam, bursts, `ForceSprout`: untouched.

No deposit counters, no order relation, no history map — and no static-root dot
test in the mandatory path: Part 3's schedule gate subsumes the back-ignition fix.

## Part 3 — Growth order: replay the deposition history

The user's call: final shapes are right, but the front's *creation order* must
change for riding to work at shallow angles. Today the cascade is an isotropic
shell — every supported ghost promotes the tick its neighbor lands — so an oblique
wave's front is a staircase firing in shell order, and a far wall can ignite a
reverse front early.

The lever: the deposit cascade already visits cells in the order the wave actually
swept them — the ball moves, deposits at its cell, spill radiates a bounded halo.
That recorded order IS the front, at every angle, curved tails included. So replay
it:

> A wave-tagged ghost, even fully supported, does not promote before
> `RequestFrame + SurfaceLagFrames`.

`SurfaceLagFrames` is the one knob: how long after the ball's passage the eruption
surfaces. Design notes:

- **Temporal "not-before" gate, not ordinal.** Promoting strictly in stamp order can
  deadlock on an early-stamped cell that never gains support (spill into
  unreachable air gets pruned); a time gate cannot. Growth is min(schedule,
  support): where `SproutLifetime` is the binding constraint the front grows at its
  own pace — but nothing *ignites* out of recorded order, and early ignition is
  what destroys both direction correctness and the oblique sweep.
- **Subsumes back-ignition.** Far cells carry late stamps, so a far wall cannot
  start a reverse race; it can only parent its adjacent cells on schedule, when the
  real front is arriving anyway. Residue is cosmetic — a wall-adjacent cell's
  volume slides out of the wall (locally backward, on schedule), which should read
  as mass condensing against the wall. If the harness or the feel pass disagrees,
  add v1's static-root dot test as polish then.
- One frame's spill halo shares a stamp (≤ 8 cells) — sub-tile front coherence,
  fine.
- Shape invariance holds: promotion timing never feeds back into deposits
  (`MassBall` ignores tiles). The wave takes longer wall-clock to finish; the
  committed cell set is identical.

Fallback if the scheduled sweep still reads wrong at some angle: direction-biased
spill order (visit the four neighbors ordered by `dot(faceOffset, waveDir)` instead
of fixed N/E/S/W — deterministic, per-wave-constant permutation), which refines
ordering *within* a frame's halo. Reach for it only when the sweep table names the
failing metric.

Do **not** vary `Lifetime` per cell to shape the front — volume velocity is
`TileSize/Lifetime`, so that lever silently retunes every contact, carry, and crush
speed in the game.

## Part 4 — Ride changes (minimal, on the existing servo)

`TerrainCarriedState`'s velocity-field servo stays the control law. Three changes:

1. **Wave lock.** `CarryWaveId` in `MovementVars`. Set on entry from the pushing
   contact's node (needs a contact → node id path; the ground/contact query already
   knows the volume). While locked, `RideTarget` sums only that wave's volumes plus
   untagged movers it's touching. Cleared on exit. Never switches during grace.
2. **Direction conditioning, not magnitude blending.** Compute flow magnitude
   exactly as today (including the `FlowCenterBias` fringe fade). When the sample
   set thins, rotate the flow direction toward the wave's constant direction; never
   scale magnitude toward ball speed. Fringe behavior stays a fading centering
   brake — no flying carpet, no suction.
3. **Jump handoff.** Carried state writes the current flow velocity to a
   `JumpSourceVelocity` in `MovementVars` every frame evidence is fresh; jump entry
   from carried-or-grace consumes it (Y from carrier + jump impulse, X left as body
   velocity — it already contains carrier + steering; adding carrier X again would
   double-count). Cleared on consume and on carried-exit-to-anything-else.

Entry gate: **unchanged for now** (horizontal-push threshold), but the sweep's
catch-rate row is the judge. If shallow angles catch unreliably — entry timing
depending on whether a vertical or horizontal face touches first is luck, not
design — the fix is an intent-shaped gate (any wave contact while airborne or
holding into the flow; pure-vertical-while-standing-still stays the elevator), not
a lower threshold. That's a decision point, deliberately not specced here.

Constants move to `MovementConfig`/JSON for hot reload when tuning starts, not
before.

## Contingent — the positional anchor (v1 §5)

Not built in v2. The discriminating criterion, measured by the sweep after Parts
2–4 land: **no-input transport ratio persistently below ~0.7 at mid angles while
flow samples are good and jerk is in bounds** — i.e. the rider is provably being
successively pushed rather than inhabiting the frame, and it isn't a flow-quality
bug. Only then design an anchor, and design it surface-relative (leading-face frame
+ standoff, like the existing `AnchorStandoff` logic), with explicit handling of
anchor-set membership changes at finalize/branch events — a raw position servo on a
weighted average of a discretely-changing set is the documented failure mode.

## Order of work

1. Harness + back-ignition red test + shape-invariance scaffold (Part 1). Runs
   against today's code; the metric table is the baseline.
2. Provenance (Part 2) + the schedule gate (Part 3) → back-ignition test green,
   sweep re-run, diff, tune `SurfaceLagFrames`.
3. Wave lock + direction conditioning + jump handoff (Part 4, items 1–3).
4. Feel pass in-game; then pin the numeric thresholds and move constants to config.

Each step ends with a sweep-table diff. A step that doesn't move the metric it
targets gets reverted, not stacked on.
