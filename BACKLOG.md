# MTile Backlog

Single consolidated list of outstanding and projected work. Consolidated 2026-08-03 from
`todo.txt`, `Animation/anim_todo.txt`, `Animation/TODO.md`, and `Character/movement_todo.md`
(all four now deleted — this file replaces them), plus open items from `Plans/` and the
deliberately-skipped tests.

Original item text is preserved verbatim in quotes. **Statuses last re-verified against source
2026-08-14** (first consolidated 2026-08-03) — several items had quietly been implemented and
were still sitting in the todo files. Status will drift; re-verify before acting on an old row.

Status key: **OPEN** · **PARTIAL** · **DONE** (kept for the record, delete freely) ·
**UNCLEAR** (couldn't determine from source).

---

## 1. Movement & physics

| # | Item | Status | Evidence / notes |
|---|---|---|---|
| 1.1 | "Pressing right while in ledge grab should pull player over and onto the ledge." | **OPEN** | `Character/Movement/LedgeStates.cs` only reads horizontal input to *exit* the grab (`pressingAway`, line 83). The pull is Up-triggered only. |
| 1.2 | "Add a cooldown to block charge starting up again after placing a block, so that if the player is actively building the block charge state is never visually active." | **SUPERSEDED** | `BlockReadyAction` no longer exists. The paint/place/burst rework replaced charge-on-hold with the `BuildMeters` economy (`ChargePhase Ramping/Peak/Overheld`), so re-state the intent against the new model if it still bothers you in play. |
| 1.3 | "Pressing up while next to a two high ledge should trigger a mini jump into parkour state so that the player smoothly arcs over the corner. The jump should be scaled up a bit if the player is moving into the ledge quickly and is far enough away that the jump won't carry them into a wall" | **PARTIAL** | `ArcJumpState` (`Character/Movement/ClimbStates.cs:327-342`) covers the 2-block band with Up held at speed, but it's a full ballistic arc, not a speed-scaled mini-hop at corner detection. |
| 1.4 | "Make duck under put player at stable height under ledge, so there isn't bobbing up and down." | **PARTIAL** | `FoldDuckReach` (`Character/Movement/MovementConfig.cs:268`) feeds `wallEscapeDown` in `FoldReference.cs:135` and the reference shapes the duck, but no formal stable-height contract is pinned. |
| 1.5 | "Track forces applied by physics contacts at all times, so that by the end of an update physics contacts always know the total force that's been exerted through them." | **DONE** | `Character/Corrector/CorrectorLedger.cs:43-110` — per-channel and per-contact force recording. |
| 1.6 | "Cap forces added by ramps to velocity so that players never accelerate at unrealistic speeds (this is an issue when parkor state moves players upwards on high ledges)." | **DONE** | `ClimbStates.cs:222` caps hop vy at `MaxAssistRiseSpeed`. (The ramp system itself was deleted with the corrector.) |
| 1.7 | "Holding up should trigger player to enter ledge grab and then trigger them to move up out of ledge grab (player shouldn't have to tap up twice). However, it should be easy to tap jump and not move up out of ledge grab." | **DONE** | `LedgeStates.cs:41-42`. |
| 1.8 | "Dropdown from ledge should push the player into ledge grab state unless they're holding down when drop down ends." | **DONE** | `LocomotionStates.cs:294-297` (`DropChainDir` handoff) → `LedgeStates.cs:60` path D. Test: `DropdownTests.cs:170-211`. |
| 1.9 | "Normal movement should never break blocks. Generate a set of tests for when players run into wall, crouch, drop down, etc" | **DONE** | `MTile.Tests/Sim/RunningOverUnderImpactTests.cs`. Mechanism is threshold isolation (player `ImpulseThreshold` 700 vs running impulse ≈250), not an explicit guard. |
| 1.10 | "the player's standing state jitters when they're pushed up by blocks right now. possibly because the moving rectangles disappear momentarily when the tile sprouts convert to solid tiles?" | **DONE** | `MTile.Tests/Sim/StandingJitterTests.cs` pins it. |
| 1.11 | "make tile generation speed a parameter in game config (the rate at which tile sprouts grow)" | **DONE** | `MovementConfig.SproutLifetime` (default 0.1f). |

### Vault / ledge / duck wishlist

| # | Item | Status | Evidence / notes |
|---|---|---|---|
| 1.12 | "activate parkourstate only for one block vaults. for two block, create a similar state that includes an initial hop to provide vertical acceleration" | **DONE** | Split into `ParkourState` (`RequiresRunningEntry = true`, 1-block) and `ArcJumpState` (2-block+), `ClimbStates.cs:299-342`. |
| 1.13 | "make vault require left/right dir to be pressed into direction of vault to activate/continue" | **DONE** | `ClimbManeuverBase.CheckPreConditions`, `ClimbStates.cs:60`. |
| 1.14 | "make player crouch automatically to avoid wall friction when under ledge" | **DONE** | `CrouchedState.CheckPreConditions`, `LocomotionStates.cs:159-170`. |
| 1.15 | "Make ledge grab pull player to a resting height just below lip." | **DONE** | `LedgeStates.cs:283`, gate at `corner.Y - (2*Radius + 4f)`. |
| 1.16 | "Make down arrow when player is overlooking ledge drop player into ledge grab." | **DONE** | Same path as 1.8. |

### Movement contract items (ex-`movement_todo.md`)

All eight were executed by `Plans/MOVEMENT_NIGHT_PLAN.md` and independently re-verified.

| # | Item | Status | Evidence / notes |
|---|---|---|---|
| 1.17 | "a stair-climb test which confirms that the player walking into stairs with slope 45 degrees will walk up smoothly, with continuously chained move-up moves (probably mantlestate)" | **DONE** | `StairClimbTests.cs:44-97`. |
| 1.18 | "a jump into cave test confirming that when the player holds left/right while falling and runs into the mouth of a cave, the corrector assists them in not running into the upper lip of the cave" | **DONE** | `CaveMouthTests.cs:82-139` (both the assist case and the honest-bonk case). |
| 1.19 | "general principle to never allow moves to push player past max-jump speed" | **DONE** | `SpeedInvariantTests.cs:55-109`; `ClimbStates.cs:222,281`. |
| 1.20 | "edit jump so that it can use ledge corners as a pushing off point, for the case of jumping out of a ledge grab or vault" | **DONE** | `JumpStates.cs:29-69` (`TryCornerLaunch`, 24px window). |
| 1.21 | "add tests confirming that moves like vault never significantly push the player's horizontal movement speed past normal running speed" | **DONE** | `SpeedInvariantTests.cs`; soft-clamp at `ClimbStates.cs:256-282`. |
| 1.22 | "edit the 2 block arc to only activate when the player is running in with up arrow held down. when the player is still, and within range of a two block ledge, holding up arrow should trigger ledge grab" | **DONE** | `ClimbStates.cs:338-342` (`ArcJumpRunSpeed` 50 px/s); standing case falls to LedgeGrab on priority. |
| 1.23 | "use the reference trajectory system for ledge pull" | **DONE (prototype)** | `LedgeStates.cs:290-308,353-374` + `ReferenceClips/ledge_pull.json`. `Character/Corrector/ReferencePath.cs` header still says **PROTOTYPE SCOPE** — productionizing is open work. |
| 1.24 | "use reference trajectories for the drop down move" | **DONE (prototype)** | `LocomotionStates.cs:269-282,311-332` + `ReferenceClips/dropdown.json`. Same prototype caveat. |

---

## 2. Animation

| # | Item | Status | Evidence / notes |
|---|---|---|---|
| 2.1 | "Build an additional animation clip for run for the case where they're technically not in contact with the ground. One foot forward, prepared to run on contact. Add machinery for transitioning from this state to a specific phase in the current run clip when the run clip is activated." | **PARTIAL** | *The machinery half is now done and live*: `ClipTimeMode.Hold` + `MatchPose` freeze the run/walk cycle at a pose-matched phase while `GroundGap > PreContactGap` (`MoveDriver.cs:155-161`), pinned by `PreRunAirborneTests.cs`. Still open: the dedicated airborne run clip — none of the 47 biped clips is a run variant. |
| 2.2 | "Work on transitions from running into vaulting / mantling. we should be solving for the initial phase which best matches the pose output in the previous step." | **PARTIAL** | `MatchPose`/`BestMatchingPhase` exist (`CharacterAnimator.cs:421-428,756`) but `ParkourDriver.Select` still returns Clock mode with no `matchPose` for all three climb tags (`MoveDriver.cs:224-230`). **Note the blocker**: `BestMatchingPhase` is only honored in phase modes (`CadencePhase`/`IdleBob`/`Hold`), so Clock-mode Parkour can't use it as written — this needs a mode change, not just a flag. |
| 2.3 | "Visibly show knees going from bent to straight in jump. To a reasonable extent, parametrize jump to keep feet in contact with ground while jump servo active (e.g. add a soft ground-contact constraint)" | **OPEN** | No jump knee parametrization or soft ground-contact constraint found. |
| 2.4 | "Add machinery for tracking clip progression for moves parametrized by position, such as dropdown." | **OPEN** | `ClipTimeMode.Progress` is declared (`MoveDriver.cs:33`) and handled (`CharacterAnimator.cs:745`), but **no driver ever emits it** — the switch arm is the only reference outside the enum. Dropdown is still Clock-driven (`MoveDriver.cs:99`). The one live `MovementProgress` consumer is the ClimbHands overlay/grip pin. |
| 2.5 | "Remove the lean stuff. (or I already did remove it, but make sure i didn't fuck anything up by block commenting it out)" | **PARTIAL** | Verified harmless — all lean code is cleanly commented with no live side effects (`CharacterAnimator.cs:60-61,157,643-649`). Still *commented*, not deleted. Note deleting also means touching the live comments at `:144` and `:609-610,634-635` that explain lean as a post-solve additive layer. |
| 2.6 | "Editor WYSIWYG: editor still samples linearly." Runtime uses C1 Catmull-Rom (`SampleSmooth`); editor scrubbed via the linear path, so in-between poses differed from the game. | **PARTIAL** | The animation editor was fixed — `MTile.Demo/DemoGame.cs:1393` now calls `SampleSmooth`. **The bind editor was not**: `MTile.Demo/BindGame.cs:218,455` still call `SampleAtTime`. |
| 2.7 | Animation solver §11.6 **Phase 4** — horizontal `d.x`/ComOffset (vertical only today), `JointLimits` as a real constraint class (currently just a config knob in `AnimSolverConfig`), local-SDF `NoPenetration` (v1 half-planes only). | **OPEN** | `Plans/ANIMATION_SOLVER_PLAN.md`. |

---

## 3. Combat & content

| # | Item | Status | Evidence / notes |
|---|---|---|---|
| 3.1 | "Add boss template" | **OPEN** | `EnemyBlueprint.cs` gives generic data-driven authoring; no boss-specific state/controller/blueprint. |
| 3.2 | "Build boss optimizer" | **OPEN** | Nothing found. |
| 3.3 | "Test guard state" | **PARTIAL** | `GuardAction`/`GuardRetaliateAction` implemented (`ActionStates.cs:1063-1142`); only test is `GrabTests.Grab_IgnoresGuard()`. No dedicated guard/parry suite. |
| 3.4 | "Fix recurring issue with breaking blocks on contact." | **UNCLEAR** | No detail in the original note and no dedicated fix/test identified. Needs a repro before it's actionable. |
| 3.5 | "Implement roll, ledge, overcrop, ceiling, guard, and dodge, moves." | **PARTIAL** | Guard and ledge exist. Roll, dodge, ceiling and "overcrop" moves do not. |
| 3.6 | "Implement principled quality of life moves, such as ceiling sweep to avoid running into a ceiling corner when jumping up." | **OPEN** | The corrector is the natural home for this now. |
| 3.7 | "Add a grab move (shift click)" | **DONE** | `GrabAction`, `ActionStates.cs:2256-2371`, bound Shift+RMB (which displaced `LobbedAreaAction`). |
| 3.8 | "Test multiplayer" | **PARTIAL** | `RollbackHarnessTests`, `TwoPlayerStepTests`, `InputCodecTests`, `RtcConnectionTests` all exist. What's missing is soak/latency testing under real network conditions. |
| 3.9 | Block-peel grab: playtest tuning pass | **OPEN** | Peel mechanics shipped 2026-08-07 (`BlockGrabAction` peel mode, `Peel*` knobs in `configs/movement_config.json`, `BlockPeelTests`), but every number — kernel σ/rate, spring coeff/power/cap, wear rates, glue floor, material weights — is a first-guess awaiting in-game feel. Hot-reload the JSON while playing; legacy rip is the A/B baseline via `BlockPeelEnabled: false`. |
| 3.10 | Block-peel grab: tension render polish | **OPEN** | Current feedback is the tether-darkening overlay + strain-red shift in `BlockGrabAction.Draw`. The "sticker peel" fantasy wants tethered blocks to visibly strain toward the pull (offset/jitter ∝ force share) — render-only, safe to add anytime. Decide the legacy drag-rip path's fate after the tuning pass. |

### Open design question: hitstun, combos, and disadvantage states

Preserved verbatim from `todo.txt` — unresolved, and the reason the escalation model looks the
way it does:

> In smash ultimate, there's A-moves, air A-moves, tilt attacks, B-moves, rolls, shields, dodges, and grabs. Right now I have slashes in air, slashes on ground/crouched, stabs, block placement, and block placement variants + a bunch of movement options, although the number of options in smash is vastly higher. In this game, stun-states etc are much rarer, so it's much harder to imagine something like a combo. That said, it's definitely good for gameplay if things like disadvantage states are possible. The simplest combo is just iterating slash attacks. Let's say one player gets this going on the other. What should happen? I'd like to give the attacked player some mobility, so that they're not dead in the water, but I'd rather not give them full movement, because then the attack would feel insubstancial. Should I interrupt jumps, for instance? If the player's spamming jump as they're hit, they shouldn't simply escape. But also they shouldn't be powerless, and I dislike locking them out of movestates. In particular, full stun-lock loops should be impossible.
>
> Let's say the player is hit very hard by a stab attack, and they go flying. They probably shouldn't be able to double jump in this state, so maybe I should actually add a stunned / ragdolled state to the game? At least for the duration that they're moving rapidly?

Partly answered since it was written: `TumbleState` (51/26) is the ragdoll-ish launch state, and
`CombatState.BlockedCapabilities` gates moves during disadvantage without hard-locking the FSM.
Diminishing hitstun extensions prevent unbounded stun-locks. The open half is whether the
capability gates are tuned right in practice.

---

## 4. Web / multiplayer

Browser PvP works end to end and is deployed to https://amdson.github.io/mtile/. These are the
items between "works for us" and "send a stranger a link" — see `Plans/INTERNET_READY_PLAN.md`.

| # | Item | Status | Evidence / notes |
|---|---|---|---|
| 4.1 | **TURN relay.** STUN-only today, so symmetric-NAT and CGNAT pairs simply can't connect. | **OPEN** | `mtileRtc.js:117` builds `iceServers: [{urls}]` with no username/credential. `INTERNET_READY_PLAN.md` Phase 2, not started. The single biggest blocker for strangers. |
| 4.2 | **Desync is detected but not surfaced.** | **OPEN** | `RollbackSession.OnDesync` has zero production subscribers — only the declaration (`:65`) and the invoke (`:223`). A desync currently just diverges silently. |
| 4.3 | **Mid-game disconnect freezes instead of reporting.** | **OPEN** | `Index.razor.cs:311` early-returns while `Phase.Playing`, so a peer dropping mid-match leaves the game stuck at the stall cap with no message. |
| 4.4 | **`RunAOTCompilation` is `false` in the csproj.** A plain `dotnet publish -c Release` silently ships the 2.7 fps interpreted build; only `scripts/publish-web.ps1` overrides it. | **OPEN** | `MTile.Web.csproj:15`, with a stale comment ("Defer until Phase 4 perf tuning"). Real footgun — flip it, or make the plain publish fail loudly. |
| 4.5 | **No Firestore-path smoke test.** `pvp_move.py` deliberately drives only the manual copy/paste lobby, so the room-code path has no automated coverage. | **OPEN** | `pvp_move.py:22-25`; `INTERNET_READY_PLAN.md:112` lists it as pending. |
| 4.6 | **STUN/ICE config is hardcoded in three places.** | **OPEN** | `Program.cs`, `Index.razor.cs:43`, `mtileRtc.js:20`. Plan item 6 (centralize into one file) not done. |
| 4.7 | Firestore TTL policy — documented as 1h `expireAt`, but whether the console-side TTL policy was actually created is unverified. | **UNCLEAR** | Worth confirming, else rooms accumulate. |
| 4.8 | No AOT **boot** measurement exists — all boot figures (~11.9 s) are interpreted-build numbers. | **OPEN** | Worth one measurement so the real first-load experience is known. |

Stale docs to fix while you're in there: `INTERNET_READY_PLAN.md` still claims `firebase-config.js`
is a placeholder (it holds live config for project `mtile-937a0` as of `506a3f8`), and `WEB_PVP.md`
describes only the copy/paste lobby — it predates room codes and wasn't updated.

---

## 5. Engineering debt

| # | Item | Status | Evidence / notes |
|---|---|---|---|
| 5.1 | Sim reads animation-layer data — a layering violation against the render-only invariant. | **OPEN** | `Character/Movement/JumpStates.cs:61` (`// TODO remove dependency on Animation layer data`). |
| 5.2 | Distance heuristic in collision resolution should be a line-segment/plane intersection test. | **OPEN** | `Physics/PhysicsWorld.cs:30`. |
| 5.3 | `Game1` render/HUD extraction half-done — ~870 lines still inline (was 1029). | **OPEN** | `Plans/GAME1_REFACTOR_PLAN.md`. |
| 5.4 | Corrector: lever-normalized hinge weighting. | **OPEN** | `Plans/CORRECTOR_CONSOLIDATION_PLAN.md` §6, the one deferred item. |
| 5.5 | Web port never runtime-tested. | **DONE** | Superseded — the browser now runs verified PvP and is deployed. Remaining web work moved to §4. |
| 5.11 | `PlayerCharacter.cs:485,524,535,554` print `[move]`/`[action]` transitions via `System.Console.WriteLine` on the sim hot path — including during rollback re-simulation. | **OPEN** | Almost certainly leftover debug tracing. Delete or gate behind a debug flag. |
| 5.12 | `Character/Corrector/LatticePlanner.cs` (beam-search movement planner) is marked PROTOTYPE / "freeze-frame oracle only — not wired into the live sim"; its only caller is `ZzzLatticeTiming`, an assert-free benchmark self-labelled "TEMP EXPERIMENT: … Delete me". | **OPEN** | Decide: wire it up or delete both. `ZzzLatticeTiming` is also ~35 s of the test suite's ~75 s runtime. |
| 5.13 | Stale comments referencing retired classes in the new build code — `BlockBurstAction`'s header and `BurstReach` cite `BlockReadyAction`/`BlockReadyAction.BuildReach` (`ActionStates.cs:1729,1742`); `BlockGrabAction:2465,2476` likewise, including a mention of `ctx.EruptionMode`, which no longer exists. | **OPEN** | Cheap cleanup. |
| 5.6 | `BotInputSource` is still a seeded-random stub. | **OPEN** | `Net/BotInputSource.cs` (81 lines), `Plans/BOT_AI_PLAN.md` not started. |
| 5.7 | Tangential carry on moving platforms not implemented. | **OPEN** | `MTile.Tests/MovingPlatformTests.cs:13`; `Plans/DYNAMIC_PHYSICS_ROADMAP.md`. |
| 5.8 | Surface-relative descent limiting (sprout-style moving floors) wants surface velocity in the solve. | **OPEN** | Deferred from the corrector work — the "more general solver" pass. |
| 5.9 | `CorrectorCost_VaultHeavyCourse` budget test is marginal in Debug (~0.8–1.2ms vs a 0.5ms ceiling) regardless of changes. | **OPEN** | Needs relaxing or making Release-only. |
| 5.14 | Cosmetic sim-event hooks fired during rollback re-simulation, spraying duplicate particles — `Game1.cs` subscribed to `OnPlayerRespawn`/`OnTileBroken` unguarded while `RollbackSession.cs:108-112` re-runs `Step` over every rolled-back frame. | **FIXED** | Both hooks now emit into `Presentation/PresentationEvents.cs` keyed `(Simulation.Frame, PresentationId)`; `Game1.PresentThisFrame()` drains once per rendered frame. Replay re-emits the same key and is dropped. Tests: `MTile.Tests/Sim/PresentationEventLogTests.cs`. |
| 5.15 | `EnemyEntity` snapshots only `EnemyMovementVars.TimeInState` (`Entities/Enemies/EnemyEntity.cs` `WriteState`/`ReadState`), so any movement state that wants per-activation data beyond a clock silently loses it across a rollback. `SavedGravityScale` survives only because `Exit` is the sole reader and a restore never lands mid-`Exit`. `EnemyEntity._frame` (surfaced as `EnemyContext.Frame`) isn't snapshotted at all. | **OPEN** | Not currently biting — no shipped movement state stores anything else, and nothing reads `ctx.Frame`. It IS a trap: `EnemyHopState` had to keep its whole crouch→launch→land cycle inside one state, and stash its aim nowhere, purely to stay inside the one field that round-trips. Either snapshot the full vars struct (as the action side already does for `LockedFacing`/`LockedAim`) or delete `EnemyContext.Frame`. |
| 5.10 | `MaxEvents` is pinned at 32 — raising to 64 broke qp's GroundFriction post-release braking. QP row-budget crowding is load-bearing. | **OPEN** | Latent fragility, noted not fixed. |

### Deliberately skipped tests

Each encodes a specific missing capability. Un-skip as the capability lands.

| Test | Why skipped |
|---|---|
| `Sim/PlayerImpactByVelocityTests.cs:183,204,212`, `Sim/SandImpactDamageTests.cs:116` | "Impact-break tuning is pathological and pending a rework (the R=12 body impact spread)." |
| `Sim/CorrectorOperationsTests.cs:187` | Needs the StandServo root (consolidation plan §7 baseline-posture); redirect-only ambient is structurally insufficient. |
| `Sim/SimulationTests.cs:450` | "Benchmark not yet passable: crouched reflex-vault band bug (plan step 3) + pit needs at-speed crossing (step 3.5)." |
| `Sim/OneBlockTriggerSweepTests.cs:27` | Assert-free diagnostic sweep, manual only — not a defect. |
| `HoldRight_CourseCorridor` | Regression: vault lost upward exit carry, body traps in pit. |

---

## 6. In-flight experiments

**Redirect audit** — `Character/Corrector/FoldReference.cs` and `AmbientCorrector.cs` carry a `TEMP EXPERIMENT`:
redirect audit counters (`AuditSolves`/`AuditMaskFrames`/`AuditFireFrames`/`AuditMaxZr`/`AuditNetZr`)
plus a second Redirect channel grafted into the ref fold path, gated on `FoldRedirectEnabled`
for hot A/B. **No longer uncommitted** — it landed in the `0eeab5b` "WIP working state" bundle
(2026-08-06), which also carries ~7 corrector test failures. Still unresolved: decide "the redirect
is clean, land it" vs "it eats vx as the qp audit measured, drop it", then strip the counters and
fix those tests.

Related ablation knobs, all hot-reloadable from `configs/movement_config.json`:
`FoldEngine` ("qp" | "ref" | "lattice" | "lm"), `CorrectorVaultEnabled`, `FoldRedirectEnabled`.
The "lattice" engine (`LatticePathPlanner` + `LatticeTracker`, `Plans/LATTICE_PATH_PLANNER.md`) is
the engine `movement_config.json` ships (2026-08-28); phases 0–2 built, jump/wall states on it, the
maneuver states keep their own solves. What remains — engine decision (§4.9), perf gate, playtest
tuning, curvature check, row 11 priority fix, cleanup — is itemized in
`Plans/LATTICE_TODO2_AUDIT.md` §3.

---

## 7. Audio

**Nothing implemented** — there is no audio anywhere in the codebase (no `SoundEffect`/`AudioEngine`
reference). Design is in [Plans/AUDIO_PLAN.md](Plans/AUDIO_PLAN.md): rollback-safe by construction
via level-triggered (predicate-driven) vs edge-triggered (event + `(simFrame, SoundId)` dedup)
sounds, over an `ISoundSource` registry modelled on the animation move-driver registry.

| # | Item | Status | Evidence / notes |
|---|---|---|---|
| 7.1 | Sim frame counter for the single-player path. | **DONE** | `Simulation.Frame` — incremented in `Step` beside `_elapsed`, carried in `SimSnapshot.Frame`, so it rewinds on `Restore`. `PlayerCharacter.Frame` would also have worked but is per-player; a global presentation key shouldn't depend on which player exists. |
| 7.2 | `SimAudioView` read-only façade + `AudioFrame`/`ISoundSource` registry. | **OPEN** | Plan §4. The Phase 0 shape shipped instead: `Audio/GameAudio.cs` is a single policy class with `Present` (edge) + `CollectLevel` (level), over `Audio/AudioMixer.cs`. Generalize to the registry once there are enough sounds to justify it — plan §12 Phase 2, deliberately after web parity. |
| 7.4 | Per-key voice caps + per-frame coalescing. | **PARTIAL** | Implemented in `AudioMixer.Fire` (`MaxVoicesPerKind = 8`, plus a small gain bump per extra hit in a frame so "40 tiles broke" reads louder rather than as 40 voices). **Untuned and unexercised** — needs a real burst/peel with a real clip. |
| 7.5 | Web/KNI audio API parity + `Content.mgcb` assets. | **PARTIAL** | Compile parity verified: `MTile.Web` builds the audio code under KNI, and KNI's content builder produced `wwwroot/Content/Sounds/dev_tone.xnb` from the same mgcb entries. Both toolchains ship `OggImporter` + `SoundEffectProcessor`. **Runtime** behaviour of `Volume`/`Pitch`/`Pan` over WebAudio is still unverified — plan §7 risk 1. |
| 7.6 | Browser audio unlock (click-to-start). | **OPEN** | Plan §7 risk 2. Browsers need a user gesture before any audio plays; `index.html` has a loading div but no click-to-start, so web audio will be silent or throw until one exists. Blocks all web audio, not just polish. |
| 7.7 | Actual clips. | **OPEN** | The infrastructure is clip-agnostic and silent without them. `Plans/AUDIO_ASSET_LIST.md` is the acquisition list; `scripts/build-sfx.ps1` → `scripts/sync-sounds.ps1` is the pipeline. Tier 1 first. |
