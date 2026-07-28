# Movement night plan

**Campaign decisions (user-confirmed at kickoff, 2026-07-28):**
- M1 vertical bound: GLOBAL — |vy| ≤ |RunJumpVelocity| (120, fastest authored
  launch) + small tolerance. One bound for everything, correctors included.
- M1 horizontal bound: MaxWalkSpeed + 10% = 110 px/s sustained.
- M3 ArcJump run gate: |vx| ≥ 50 px/s (half walk speed) AND Up held; below
  that + in range + Up → LedgeGrab.

Source: `Character/movement_todo.md` (8 items, 2026-07-28). Ordered so global
invariants land first (they constrain everything after), behavior contracts
next, trigger/arbitration edits third, and the reference-trajectory migrations
— the riskiest, most novel work — last, where a stall costs the least.

Verification per milestone: `dotnet test MTile.Tests/MTile.Tests.csproj`
(424/0 + new tests green), `dotnet msbuild MTile.Web/MTile.Web.csproj
-t:Compile` (KNI parity), commit per milestone on `corrector_testing`.
Where a milestone changes feel (M3, M4), also run the bench corridor/vault
numbers and record them in the commit message — bumpy corridor baseline is
currently 96.6 px/s, corridor stage 79.4.

---

## M1 — Speed-cap invariants (todo 3 + 5) `[x] done`

**Principle: no move may push the body past what a deliberate jump/run could
produce.** Vertical: no state's launch or corrector channel output may exceed
max-jump rise speed (`JumpVelocity`-derived). Horizontal: vault/climb
correctors must not push vx significantly past `MaxRunSpeed`.

- Audit every launch write and corrector channel cap for where the invariant
  can be violated today (the vault-rocket class of bug — `b5c417b` fixed three
  instances; this milestone makes the *class* impossible, not just the known
  instances). Prefer enforcing at the channel/launch source (per-channel caps,
  launch gates) over a blanket velocity clamp — a clamp would mask new bugs
  instead of failing tests.
- New test file `MTile.Tests/Sim/SpeedInvariantTests.cs`: sweep the maneuver
  registry across the existing courses (vault course, bumpy corridor, step
  fixtures) asserting max |vy| ≤ jump-rise bound and vx ≤ run bound + small
  tolerance, with input scripts that historically rocketed (hold jump around
  vaults, re-trigger at lips).
- Keep exceptions explicit and listed in the test (e.g. external launches:
  eruptions, knockback — those are combat, not moves).

## M2 — Contract tests: stairs + cave mouth (todo 1 + 2)

Two new behavior contracts, written as tests first; fix whatever they expose.

- **Stair climb** (`StairClimbTests`): 45° staircase (1-block rise / 1-block
  run), player walks in holding Right → chained climb-family moves carry them
  up smoothly. Assert: monotonic-ish ascent, no stall (avg speed floor), no
  head bonks, and state trace shows chained climbs (Mantle re-trigger each
  step) — not jump spam or fold-only scraping.
- **Jump into cave** (`CaveMouthTests`): body falling with Left/Right held,
  on course to graze the wall face just above a tunnel mouth; the corrector
  trims them slightly DOWN so they duck under the lip and enter seamlessly.
  Channel expectations by phase (confirmed with user): far out, AirVertical's
  down side supplies the tiny trim (300 px/s², integrates over the approach);
  once the tunnel floor is within LegReach through the opening, the near mask
  flips while airborne — `near && !Grounded` is Redirect's plant-and-deflect
  case (its cleanest intended showcase; Tuck also live). The decision comes
  from hard clearance rows against the wall-above-mouth on the predicted
  coast — verify the corridor/ceiling probes register that face on approach.
  TWO cases: near-miss → enters clean, vx retained, no upward bonk on the lip
  row; aimed well above the mouth → honest bonk (tiny air authority must NOT
  save it — the assist smooths near-misses, it doesn't steer into caves).
- **Corner-anchored redirect** (user request): the lower corner of the
  tunnel's top lip should ALSO expose a Redirect channel — a plantable
  surface within reach is a push-off anchor, floors aren't special. Gate:
  CONVEX corner within reach only (never flat overhangs — a deflection
  channel under a slab would corrupt the hover ride), keep
  SkipSoftHorizontal. This is the same capability M3's jump-off-ledge-corner
  item needs (corner contacts as actuation anchors) — build the corner
  detection once, share it between the two.
- Fixes stay within existing channel semantics (no new channels; no redirect
  in flight — near-ground only, per the standing design rule).

## M3 — Trigger & arbitration edits (todo 4 + 6)

- **Jump off ledge corners** (todo 4): extend the jump family's contact
  context so a ledge corner counts as a push-off point — covers jumping out
  of a ledge grab and out of a vault (post-`b5c417b`, jumps already *win* the
  race; this gives the jump a legal surface so the steal produces a real
  launch instead of failing preconditions mid-arc). Likely touch: the jump
  preconditions' ground/wall probe accepting a corner contact within reach.
  Share the corner-contact detection built in M2 (corner-anchored redirect) —
  same "plantable corner within reach" primitive, two consumers.
  Test: jump press during LedgeGrab and mid-vault at a corner → jump fires
  with a full-sized arc.
- **ArcJump requires running + Up; Up-when-still = ledge grab** (todo 6): the
  2-block arc becomes deliberate — precondition requires |vx| above a
  threshold (genuinely running in) AND Up held. When stationary within range
  of a 2-block ledge, Up triggers LedgeGrab instead. Check the priority table
  (`MovementPriorities.cs`) — LedgeGrab Passive 42 vs climb 29 already orders
  this correctly once ArcJump's precondition refuses the still case. Tests:
  both sides (running+Up → ArcJump; still+Up → LedgeGrab; running without
  Up → no arc).
- Re-run `ClimbArbitrationTests` + `VaultJumpAndLipReproTests` — this
  milestone touches the exact ground they pin.

## M4 — Reference trajectories: ledge pull, then dropdown (todo 7 + 8)

Migrate the two remaining hand-tuned kinematic moves onto the Hermite
reference-clip system (`b1d4486`, editor via `--ref`).

- **Ledge pull** (todo 7) first: author a reference clip for the pull-up path
  (grab pose → crest → standing on top), drive LedgePullState from the
  reference trajectory instead of its bespoke path math. Preserve existing
  timings/heights (pin with a before/after trace test), keep the LedgeJump
  height-gate handoff working.
- **Dropdown** (todo 8) second, same recipe: reference clip for the slip-off
  arc; keep the hold-Down trigger and priority behavior identical.
- Both are state-internal migrations — arbitration, priorities, and triggers
  must not change. Suite green after each, committed separately. If the
  reference-clip system is missing a needed feature (e.g. speed-parameterized
  time warp), note it in this plan and stop rather than hack around it.

---

## Ground rules for the run

- Branch: `corrector_testing`. Commit per milestone; push after each commit.
- Determinism rules apply throughout (no static mutable sim state; snapshot
  coverage for any new `MovementVars` fields — mirror `FoldDownAnchorY`'s
  pattern).
- Channel semantics are settled: no redirect in flight; air = lateral + tiny
  vertical only. Don't reopen.
- If a milestone dead-ends, write findings under its section here, commit the
  partial work behind a green suite, and move to the next milestone.

## Campaign log

- M1 — speed-cap invariants: DONE. Bound derivation corrected mid-milestone:
  "max-jump speed" = what a HELD jump achieves (launch + hold-force window,
  ≈208), not the launch constant (a bare 120 impulse only reaches 12px; the
  1-block apex hop needs ~170). `MovementConfig.MaxAssistRiseSpeed` derives
  it. Sources fixed: HopVy lip-term capped at the assist bound; maneuver
  AirLateral split fwd(fade-capped at max(EntrySpeed, MaxWalkSpeed))/back
  (unrestricted damping); Redirect disc got a ForwardCap (post-projection
  forward clamp, provably stays inside the Thales disc — fold redirects
  unbounded, traction is the ambient layer's feature); the REAL 175 px/s
  violator was the crest push (`VaultPushForce` unconditional +500) → now
  SoftClampVelocity toward EntrySpeed. SpeedInvariantTests: growth-attribution
  sweep (rise growth past cap only in deliberate-launch states; vx growth
  past 110 only in authored-burst states), vault-course jump-held rocket
  script pinned permanently. Suite 429/0, KNI clean.
