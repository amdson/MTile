# Arbitration Cleanup TODO — priorities table + capabilities/suppression

> **Status: IMPLEMENTED** (all three tasks + the Task 2c bug fix). Core + KNI web
> hosts build clean; full suite stable at the 4 known pre-existing failures across 3
> runs. New mechanism files: `Character/MovementCapability.cs`. New test:
> `MTile.Tests/Sim/CoveredJumpStunGateTests.cs`.

Handoff doc. Two contained refactors to the movement-state arbitration system,
agreed as immediate follow-ups to the ledge-pull work (see
`Plans/LEDGE_PULL_INPUT_MATRIX.md` for that context). **Behavior-preserving except
for one deliberate bug fix (Task 2c).** Read CLAUDE.md first — especially the
determinism rules and the dual DesktopGL/KNI build constraint.

## Background (why)

Movement states are selected by arbitration: every frame, any registered state whose
`CheckPreConditions` passes and whose `PassivePriority` exceeds the current state's
`ActivePriority` preempts it (`PlayerCharacter.Update`, the selection loop at
`Character/PlayerCharacter.cs` ~line 349). Three legibility problems have accumulated:

1. **Priority numbers are scattered and drifting.** `Character/MovementPriorities.cs`
   is supposed to be the table, but most states hardcode their numbers inline, and
   the table is already wrong in one place (it says `WallJumpPassive = 40`; the code
   says 45).
2. **Cross-cutting stun gates are sprinkled and forgettable.** Five states each carry
   `if (ctx.Combat?.BlocksJump == true) return false;` in their preconditions —
   and `CoveredJumpState` *forgot* it, which is a live stun-escape bug.
3. **Pairwise "don't preempt me" rules live in the wrong files.** E.g. ParkourState's
   preconditions know about LedgePullState. The state being protected should own the
   rule.

## Task 1 — make `MovementPriorities` the single source of truth

Every movement state's `ActivePriority`/`PassivePriority` override must reference a
named constant in `Character/MovementPriorities.cs`. No numeric literals in the
overrides.

Current inventory (code values are authoritative — **do not change any number**,
only relocate them):

| State | File:line (override) | Active/Passive | Table status |
|---|---|---|---|
| FallingState | `Movement.cs:76` | 0/0 | constant exists |
| StandingState | `Movement.cs:101` | 10/10 | constant exists |
| CrouchedState | `Movement.cs:197` | 15/15 | constant exists |
| StunnedState | `Movement.cs:51` | 25/25 | **add `StunnedActive/Passive = 25`** |
| WallSlidingState | `MovementStates.cs:227` | 20/20 | constant exists |
| JumpingState | `MovementStates.cs:9` | 50/30 | constant exists |
| RunningJumpState | `MovementStates.cs:121` | 55/35 | constant exists |
| WallJumpingState | `MovementStates.cs:324` | 50/45 | **table says 40 — fix table to 45** |
| DoubleJumpingState | `MovementStates.cs:397` | 60/40 | constant exists |
| LedgeGrabState | `MovementStates.cs:802` | 42/42 | constant exists |
| LedgePullState | `MovementStates.cs:962` | 43/43 | constant exists |
| LedgeJumpState | `MovementStates.cs:1069` | 55/44 | **add `LedgeJumpActive = 55, LedgeJumpPassive = 44`** |
| CoveredJumpState | `MovementStates.cs:467` | — | already uses the table ✓ |
| ParkourState | `MovementStates.cs:668` | — | already uses the table (Guided) ✓ |
| DropdownState | `MovementStates.cs:1126` | — | already uses the table ✓ |

Also:

- Move the inline rationale comments (e.g. WallJump's "strictly above DoubleJumping's
  40", LedgeJump's "loses to WallJump 45 so the bail-out wins", DoubleJump's
  registration-order tiebreak note at `MovementStates.cs:404`) to the table, next to
  their constants — the table should read as the complete pairwise spec.
- Add a header comment documenting the active/passive semantics ("passive = strength
  of the bid to take over; active = resistance to being taken over; a candidate wins
  iff `passive > current.Active`") and the bands (free 0–20, stun 25, jump passives
  30–48, holds 42–43, guided passive 45, launch actives 50–60).
- **Scope: movement FSM only.** `ActionStates.cs` priorities are also inline but there
  is no ActionPriorities table yet — out of scope, note it as future work.

Acceptance: `grep -n "Priority =>" Character/Movement*.cs` shows only
`MovementPriorities.*` references for movement states; full test suite unchanged
(see Verification).

## Task 2 — capability mask (replaces sprinkled `BlocksJump` checks)

### 2a. Mechanism

- New `[Flags] public enum MovementCapability { None = 0, Jump = 1 << 0 }` (own file,
  `Character/MovementCapability.cs`). Start with `Jump` only; the point of the enum is
  future status effects (root, silence-like lockouts, cutscene control).
- On `MovementState` (the base, in `Character/Movement.cs`):
  `public virtual MovementCapability RequiredCapabilities => MovementCapability.None;`
- In `PlayerCharacter.Update`'s **selection loop only**: compute the blocked mask once
  per frame —
  `var blocked = ctx.Combat?.BlocksJump == true ? MovementCapability.Jump : MovementCapability.None;`
  — and skip any candidate with `(state.RequiredCapabilities & blocked) != 0`.

⚠ **The mask gates candidate *entry* only — never the current state's continuation.**
Today `BlocksJump` appears only in `CheckPreConditions`, so a player hit mid-jump
finishes the jump (see StunnedState's comment, `Movement.cs:34-37`). Applying the mask
to `CheckConditions` or to the current state would change that. Don't.

### 2b. Conversions (behavior-preserving)

Declare `RequiredCapabilities => MovementCapability.Jump` and delete the inline
`ctx.Combat?.BlocksJump` check in:

- JumpingState (`MovementStates.cs:25`)
- RunningJumpState (`:132`)
- WallJumpingState (`:335`)
- DoubleJumpingState (`:406`)
- LedgeJumpState (`:1074`)

### 2c. The deliberate behavior change

`CoveredJumpState` has **no** `BlocksJump` gate today — a stunned player under an
overhang holding Space+direction can covered-jump out of stun. Give it
`RequiredCapabilities => Jump`. This is a bug fix; add a regression test mirroring
`MTile.Tests/Sim/CombatHitstunTests.cs` (stab-stunned player under a 2-tile ceiling
holding Space+Right must stay put until stun expires).

## Task 3 — suppression hook (relocates the `PreviousState` gates)

### 3a. Mechanism

- On `MovementState`:
  `public virtual bool Suppresses(MovementState candidate, EnvironmentContext ctx) => false;`
- In the selection loop, before the priority comparison:
  ```csharp
  var owner = ctx.PreviousState(0);
  ...
  if (owner != null && owner.Suppresses(state, ctx)) continue;
  ```

⚠ **Call it on `ctx.PreviousState(0)`, NOT on `_currentState`.** This is load-bearing.
The existing gates use `PreviousState(0)`, which still points at the pull for one
frame *after* the pull's conditions fail (the exit-fallback to Falling happens before
selection, but history is recorded at end of frame). Example that breaks otherwise:
release Up + inward held (matrix row H) — the pull dies, the same-frame selection must
still suppress Parkour (passive 45) or it beats the re-grab (passive 42) and the
release behavior regresses. `MTile.Tests/Sim/LedgePullExitTests.cs` pins this.

### 3b. Conversions

Move these two gates into `LedgePullState.Suppresses` and delete them at their
current sites:

1. **Parkour gate** — `ParkourState.CheckPreConditions` (`MovementStates.cs` ~:680):
   `if (ctx.PreviousState(0) is LedgePullState) return false;`
   → `LedgePullState.Suppresses`: `candidate is ParkourState` → true.
2. **WallJump inward gate** — `WallJumpingState.CheckPreConditions` (~:340): during a
   pull, only an *away* press may wall-jump (inward queues for LedgeJump — matrix
   row K). → `LedgePullState.Suppresses`: candidate is `WallJumpingState` and the
   input is NOT pressing away from this pull's wall → true. You'll need to expose
   `WallDir` on `WallJumpingState` (a `public int WallDir => _wallDir;` property,
   same as `LedgePullState.WallDir`), and express "pressing away" relative to the
   pull's own `_wallDir` exactly as the current gate does
   (`_wallDir == 1 ? ctx.Input.Left : ctx.Input.Right`).

### 3c. Explicit non-conversions

These look similar but are **entry rules, not suppressions — leave them alone**:

- `LedgeGrabState.CheckPreConditions` path C (`PreviousState(0) is LedgePullState …`)
  — re-grab after an abandoned pull.
- `LedgeJumpState.CheckPreConditions` (`PreviousState(0) is LedgePullState …`) — the
  jump can only launch from a pull.

## Verification

```bash
dotnet build MTile.Core.csproj
dotnet build MTile.Web/MTile.Web.csproj        # KNI — same sources, different framework; must stay green
dotnet test MTile.Tests/MTile.Tests.csproj
```

- **Known pre-existing failures (not yours, do not chase):**
  `SimulationTests.HoldRight_CourseCorridor_RunsThrough`,
  `SproutPushTests.SproutGrowsLeft_AgainstRightInput_BlocksAdvance`,
  `CharacterAnimatorTests.Cadence_RateScalesWithSpeed`,
  `CharacterAnimatorTests.RealWalkJson_AdvancesPhase_WhenWalkingForward`.
  Everything else must pass — especially `LedgePullExitTests` (pins the suppression
  semantics), `LedgeGrabFlickerTests`, `CombatHitstunTests`,
  `JumpFromCompressedTests`.
- The suite used to be flaky from a static scratch-buffer race in `PhysicsWorld`
  (fixed via `[ThreadStatic]` on `_impactCellsScratch`). If you see "collection was
  modified" or rotating impact-test failures, something reintroduced shared mutable
  statics — see the determinism rules in CLAUDE.md.
- Snapshot/rollback: these changes add **no mutable state** (capabilities and
  suppressions are pure declarations), so no snapshot changes are needed. Don't add
  any fields that would need capturing.
- Game is file-locked while running (`MTile.exe`) — use `dotnet test --no-build` if a
  build's copy step fails.

## Out of scope / future (do not do now)

- Action FSM priority table (`ActionStates.cs` inline numbers).
- Generated arbitration matrix: a test harness that enumerates, per current state ×
  canonical input vector, which state wins selection, emitted as a doc + pinning
  test. This is the long-term answer to "the transition graph lives nowhere".
- Additional capabilities (e.g. `Dash`, `Attack`-side gating on the action FSM).
