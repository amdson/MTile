# Combat Feel Plan — making the basic hit exchange fun

Goal: make 1v1 back-and-forth hitting feel like a platform fighter (advantage states,
combos, whiff punishes, escalation) **without hard-locking players**. The framing:
advantage comes from *physics and position*, not animation locks. The channels we
already have — `MovementModifiers`, the `MovementCapability` mask, priority bands,
soft constraints/servos — are the levers. Each phase ships playable.

Diagnosis this plan addresses (see file refs inline):

1. Knockback doesn't displace — victim keeps full air/ground control during hitstun
   (only Jump is gated), and `AirControl` brakes any speed above `MaxAirSpeed` back
   to the cap, so even a 450-impulse hit is cancelled within ~4 frames.
2. Post-hit invuln (0.4 s, `PlayerCharacter.cs:42`) outlasts hitstun (8 frames) —
   the victim's hurtbox stops publishing, so follow-ups/strings/juggles are
   mathematically impossible.
3. Flat per-move knockback + flat 8-frame hitstun = no escalation arc, no combo→kill
   spectrum, no reason for slow strong moves to exist.
4. Every attack starts on click-release and recovers in 3–5 frames → mashing is
   optimal; no whiff-punish game.
5. Guard nullifies completely at zero risk and has no counter (no grab) → turtling
   dominates once discovered.
6. Wall-slide / ledge-grab aren't gated by hitstun → any launch is instantly
   cancelled by clinging to terrain; no juggle or edgeguard situations.

---

## Phase 0 — switch the sim to 60 fps  ✅ DONE (2026-06-12)

30 fps was a testing rate. Do this **first** so all combat tuning below happens once,
at the real rate. Spring/servo stability margins (`K·dt² + D·dt < 2`) all improve.

**Implementation note:** durations were converted to seconds and translated to
frames at the **runtime dt** (`SimFrames.FromSeconds(seconds, ctx.Dt)`,
`Character/SimFrames.cs`) rather than baked at load from `FixedDt`. The sim is
rate-independent by construction, and the headless tests — which step their own
`Dt = 1/30` — behave byte-for-byte as before with no script retuning.

- [x] `Simulation.FixedDt` (`Simulation.cs:22`) → `1f / 60f`. `Game1.TargetElapsedTime`
      already derives from it (`Game1.cs:77`).
- [x] Frame-count constants → seconds:
  - [x] `CombatState.cs`: stun 0.6 s, guard-charged 0.8 s; hitstun replaced by the
        Phase 1 impulse scaling below.
  - [x] `ActionStates.cs`: all `SetFor` durations → `SetForSeconds` (combo windows
        1.0 s; recovery 0.1–0.4 s; ranged `RecoverySeconds`).
  - [x] `IntentBuffer.cs`: `JumpBufferSeconds` (0.133 s), read via
        `ctx.JumpBufferFrames`; global cap stays a loose frame constant (120).
  - [x] `InputParser.cs`: `ClickMaxHoldSeconds` (0.2 s), dt passed into `Detect`.
  - [x] `PlayerCharacter.cs`: `CrushCooldownSeconds` (0.2 s).
  - [x] `MovementConfig` audited — already all seconds/px-based.
- [x] Tests: no frame-count rewrites needed (runtime-dt conversion); direct
      `OnHitRegistered` caller in `CoveredJumpStunGateTests` updated.
- [ ] Sanity-pass movement feel at 60 fps in the real game (jump arc, wall slide,
      ledge pull timing) — needs a playtest session.

## Phase 1 — make hits displace  ✅ DONE (2026-06-12)

- [x] **Hitstun movement modifiers.** Applied in `PlayerCharacter.Update` right
      after action modifiers: `WalkAccel/AirAccel ×0.15`, `AirDrag ×0.2`,
      `GroundFriction ×0.3` (constants in `CombatState.Hitstun*Scale`). The
      residual control **is** DI.
  - [x] **Crush exemption:** crush/landing registration passes
        `muteControl: false` + a fixed 0.27 s hitstun override — a hard landing
        gates jump briefly but does NOT mute walking (new
        `CombatState.HitstunMutesControl` flag, snapshotted in `CopyFrom`).
- [x] **Don't let the speed cap eat knockback.**
      `MovementModifiers.PreserveExternalVelocity` (set during muting hitstun):
      `AirControl.Apply` skips the over-cap brake, and Standing/Crouched skip
      their walk-overspeed correction. Input can still add speed up to the cap;
      external velocity decays via (muted) drag/friction only.
- [x] **Impulse-scaled hitstun.** `seconds = impulse × 0.00135`, clamped to
      [0.10 s, 0.70 s] (`CombatState.OnHitRegistered`); Stab/S3 (380) → ~0.51 s,
      Pulse (450) → ~0.61 s. Follow-up extension ×0.5 keeps the anti-infinite.
  - [ ] Decide whether `StunActive` survives as a separate tier or collapses into
        "long hitstun + Tumble" (Phase 4). Interim: 350 threshold kept as-is.
- [x] **Removed victim post-hit invulnerability** for ordinary hits — `OnHit` no
      longer sets `_hitInvulnRemaining`; the field + hurtbox suppression remain
      solely as **respawn protection**. `HitId` dedupe covers single-attack
      multi-frame; crush cooldown kept.
  - [ ] If degenerate rapid-rehit cases appear (e.g. Pulse's 12 segments), fix at
        the source with per-attack dedupe windows, not blanket invuln.
- [x] Tests: `CombatFeelTests.Hitstun_KnockbackDisplaces_DespiteHoldingAgainstIt`
      (stab knockback carries ≥ 8 px against held counter-input).

**Playtest checkpoint:** hits should now visibly move people, light strings should be
possible, juggles half-work (wall-cling still rescues — that's Phase 4).

## Phase 2 — hit effects beyond knockback  ✅ DONE (2026-06-12)

Generalize "a hit = one impulse" so moves can shape victim motion. Two tiers:

- [x] **Tier 1: hold-tuned knockback.** Implemented as *reduced outward* impulse
      rather than inward-aimed — the field (Tier 2) does the pulling, so the
      impulse only needs to stop launching people out of range.
  - [x] GroundSlash1 → 60 (was 200), GroundSlash2 → 80 (was 260); both carry
        `HitstunSecondsOverride = 0.30 s` so the tiny impulse still stuns.
  - [x] GroundSlash3 keeps its 380 outward launch (the payoff hit; also crosses
        the 350 stun threshold).
- [x] **Tier 2 (real system): holding fields — stateless, broadcast like hitboxes.**
      A hold is NOT a persistent effect written onto the victim. It is a value-typed,
      single-frame `ForceField` the attack re-broadcasts every frame it is active,
      mirroring the `Hitbox`/`HitboxWorld` model (`World/Hitbox.cs:34-36`): the field
      world is cleared each frame, publishers re-broadcast, and anyone inside the
      region is pulled *this frame*. No cross-frame identity, no expiry bookkeeping,
      **nothing new to snapshot** — after a restore, fields regenerate from
      already-snapshotted FSM/`ActionVars` state exactly like hitboxes do.
  - [x] `ForceField` struct + `ForceFieldWorld` + `ForceFieldSystem.Apply`
        (`World/ForceField.cs`): saturated velocity-servo toward `Focus`,
        `clamp((targetVel − vel)/dt, ±MaxAccel)`, approach speed capped by
        `dist/dt` so it lands on the focus. Applied in `Simulation.Step` (and
        `SimRunner.RunMulti`) after all updates, **before** the physics step;
        targets = the hurtbox set, owner-faction filtered, publisher excluded.
  - [x] `Hitbox.HitstunSecondsOverride` (seconds, < 0 ⇒ impulse-derived).
  - [x] GroundSlash1/2 broadcast the field for the whole slash
        (`SlashLikeAction.PublishHoldField`, focus at `0.7 × ArcRadius` along
        SlashDir, target speed 160 px/s, accel cap 4000 px/s²);
        `RecoveryAction.Update` publishes a 0.6× continuation while
        `Slash2Ready/Slash3Ready` is open, reusing the surviving `vars.SlashDir`.
        S3 broadcasts none — it's the launch payoff.
  - [ ] Decide in playtest whether fields affect non-hitstunned players (suction in
        neutral) or gate the applied force on `HitstunActive` — **currently
        ungated** (anyone in region is pulled); both options stay stateless.
  - [ ] Later consumers: Pulse = outward radial field over its active window
        (instead of one 450 impulse); Stab = directional field dragging victims
        along the lunge; eruption knock-up fields (Phase 7).
  - [ ] Debug overlay for fields in Game1 (mirror DrawHitbox).
- [ ] Determinism: fields read sim state only and live in no snapshot; add a
      restore-mid-slash case to `SnapshotRoundTripTests` verifying the field
      rebuilds identically on the replayed frame.
- [x] Sim test: `CombatFeelTests.HoldField_Slash1_KeepsVictimInRange_DespiteWalkingAway`
      (held through slash + recovery, escapes cleanly after). Full S1→S2→S3
      max-range-opener chain test still to add:
  - [ ] Scripted S1→S2→S3 vs. a victim holding away — assert all three connect
        from a max-range opener.

## Phase 3 — commitment spectrum (startup, whiff lag, on-hit cancels)  ✅ DONE (2026-06-13)

- [x] **Attacker hit-confirm channel.** `CombatSystem.PeekHits(hitId)` — a per-`HitId`
      entity-connection count, sibling of `PeekRecoil`, populated as hits land and
      read the next frame. `SlashLikeAction.Update` polls it and latches
      `ActionVars.AttackConnected`. **Determinism:** the tally is a frame-N→N+1
      inbox, so it's now snapshotted (`CombatSystem.Capture/RestoreHitConfirm`,
      `SimSnapshot.HitConfirm`) — a restore taken between the hit's `Apply` and the
      attacker's read would otherwise drop the confirm and diverge on replay
      (`RollbackHarnessTests` catches this). `_recoilByHitId` is the same shape but
      currently always empty (RecoilScale = 0), so it's left un-snapshotted with a note.
- [~] **Whiff-punish via hit-confirm — wired but DISABLED.** `connected` is threaded
      to every opener's `OnExitSetFlags(..., bool connected)` and the latch is fully
      tracked, but it does NOT currently gate the chain — openers set their follow-up
      flag (`Slash2Ready`/`Slash3Ready`/`AirSlash2Ready`) unconditionally, so a whiff
      still chains (combat timing unchanged for now, per user). To enable whiff-punish,
      wrap each follow-up set in `if (connected)` (one line per opener; see
      `GroundSlash1.OnExitSetFlags`).
- [x] **Stab → heavy.** Knockback ×3 (380 → 1140; ~456 px/s launch, well over the 350
      stun threshold ⇒ launch + stun ⇒ TumbleState in the air). Startup 0.12 → 0.18 s
      (~11f) so the launcher is committal/whiff-punishable. S1/AS1 stay the fast pokes.
  - [ ] Remaining: a dedicated charged-slash heavy variant (Pulse already 0.15 s).
- [x] Sim test: `CommitmentSpectrumTests` — `HitConnects_ConfirmLatches_AndChains`
      (in range → AttackConnected latches, victim damaged, chains to S2) and
      `Whiff_NoConfirm_ButStillChains` (out of range → AttackConnected stays false &
      victim HP unchanged, but S2 still fires since chaining is ungated).

## Phase 4 — tumble, tech, and gating defensive movement  ✅ DONE (2026-06-13)

- [x] **Extended `MovementCapability`** with `WallCling` + `LedgeGrab`, declared on
      `WallSlidingState` (WallCling), `LedgeGrabState`/`LedgePullState` (LedgeGrab).
      The combat disadvantage window blocks them via `CombatState.BlockedCapabilities`
      (hitstun OR stun ⇒ Jump | WallCling | LedgeGrab), consumed by the selection loop
      (replaces the old `BlocksJump ? Jump : None`). A launch past terrain can no
      longer be cancelled by clinging — this is the juggle/edgeguard enabler. (Attack
      stays gated separately via `CombatState.BlocksAttack`; no movement-cap flag.)
- [x] **TumbleState** (`Character/Movement.cs`) in the launch band (Active 51, Passive
      26): entered when airborne + `StunActive` (the 350-impulse stun threshold).
      Muted air-control (DI only), forces `PreserveExternalVelocity` so the launch
      isn't braked in the stun tail. `StunnedState` is now grounded-only — an airborne
      heavy hit goes to Tumble; a grounded stun that gets knocked airborne flips to it.
  - [x] **Tech.** A buffered Jump while a surface is within the tech probe (the last
        few frames before landing) ends the launch early (`CombatState.Tech` clears
        hitstun + stun), grants brief i-frames (`InvulnExpireFrame`, checked in
        `OnHit`), and pops the body up. Outside the window it just rides the tumble
        down. Tunables: TechProbeSlack 60px, TechInvulnSeconds 0.25, TechBounceVy 260.
- [x] Sim tests: `TumbleTechTests` — hitstun blocks WallCling (vs. a no-hit control
      that wall-slides); a heavy airborne hit tumbles and never wall-clings; a tech
      near the ground produces an invuln upward bounce that clears the launch, while a
      jump pressed high (outside the window) does not tech.

## Phase 5 — escalation arc (percent + fast-regen HP, terrain as the damage source)  ✅ DONE (2026-06-13)

Model chosen (per user): a **monotonic percent** that scales the knockback applied to
you (Smash-style, never decays in a life), paired with **fast-regenerating HP**.
Direct hits do NOT chip HP — they raise percent + apply scaled knockback. **HP damage
comes only from being slammed into terrain** (the existing crush path, which scales
with impact hardness). No blast zones — KO is HP→0 from hard splats at high percent.

- [x] **Percent + scaled knockback.** `CombatState.DamagePercent` (monotonic,
      snapshotted). `OnHit` does `AddPercent(hit.Damage)` then applies
      `KnockbackImpulse × KnockbackScale` where `KnockbackScale = 1 + pct ×
      KnockbackPerPercent` (0.015 ⇒ ~2.5× at 100%). The percent-scaled magnitude also
      feeds `OnHitRegistered`, so high-% hits stun longer and cross the Tumble
      threshold. `PercentPerDamage = 15` (a slash's 0.5 damage ⇒ +7.5%). Direct
      `Health -= hit.Damage` removed for players (tile damage path unchanged).
- [x] **Fast-regen HP.** `PlayerCharacter.Update` regenerates HP
      (`HealthRegenPerSecond = 0.8`) once clear of impacts for `RegenDelaySeconds`
      (1.0; anchored on `_lastCrushFrame`, already snapshotted). Percent does NOT regen.
- [x] **Impact = HP source.** The crush path (`CrushImpulseThreshold` 700,
      `CrushDamagePerImpulse` 0.003) is unchanged and is now the *only* HP-loss path —
      high-% knockback into terrain exceeds the threshold and chips HP.
  - [ ] Remaining: make crush **material-aware** (stone hurts more than sand via tile
        MaxHP) — currently uniform. The plan's wall-splat retune.
- [x] **Respawn.** `Respawn` resets `DamagePercent = 0` (the only thing that clears
      it) and refills HP. HUD shows the percent next to the HP bar (under
      `DebugDrawHealthBars`).
- [x] Sim tests: `EscalationTests` — percent rises monotonically; knockback scales
      with percent (≥2.5× at 200%); direct hits don't chip HP and HP regens; respawn
      zeroes percent. Existing HP-based "did it connect" assertions repointed to
      `DamagePercent` (`CombatHitstunTests`, `TwoPlayerStepTests`, `CommitmentSpectrumTests`).
  - [ ] Playtest the loop: combo to build %, then a launcher (stab) into terrain to KO.

## Phase 6 — grab (RPS triangle's new corner)  ✅ DONE (2026-06-13) · guard rebalance deferred

Input: **Shift + RMB** (per user). `LobbedAreaAction` deactivated to free the binding
(its registration line in `PlayerCharacter` is commented out — re-add to restore).

- [x] **Grab action** (`GrabAction`). The Phase 2 hold-field turned up: while held it
      broadcasts a strong short-range `ForceField` in front of the grabber (PullSpeed
      320, PullAccel 9000 — overpowers walking). Stateless; the grab persists only
      while the action keeps broadcasting. **Ignores guard for free** — a field never
      goes through the OnHit/parry path. Priority 46 (above LobbedArea/Guard, below
      GuardRetaliate). A whiffed grab still runs hold→throw→0.3 s recovery, so a read
      grab is punishable (attack beats grab).
  - [ ] Deferred: mash-to-escape (needs the victim's input to shorten the grabber's
        action — cross-player plumbing). For now escape is via struggle or the hold cap.
- [x] **Grabbed signal.** `CombatState.GrabbedActive` (+ `GrabbedExpireFrame`), set by
      the grab field each frame via `ForceFieldSystem.Apply`'s new `onGrabHeld`
      callback → `MarkGrabbed(victim.Frame)`; cleared by `Tick` a couple frames after
      the field stops (mirrors hitstun). Folds into `BlocksAttack` and
      `BlockedCapabilities` (grabbed ⇒ no normal attack, no jump/cling/grab).
      Snapshotted (bool + int in `CopyFrom`); rollback-clean (`RollbackHarnessTests`).
- [x] **Struggle attack** (`GrabbedSlash`). Weak short slash, **exempt** from the
      `BlocksAttack` gate (its precondition requires `GrabbedActive` and skips the gate
      the normal slashes obey). Fixed 0.20 s hitstun so it reliably stuns the grabber
      regardless of percent. **Grab-break falls out for free:** `GrabAction`'s
      `CheckConditions` require `!HitstunActive`, so a connecting struggle → grabber
      hitstun → grab exits → field dies → victim's `GrabbedActive` lapses.
  - [ ] Deferred: a separate weak `GrabbedStab` variant (slash covers the mechanic).
- [x] **Throw.** On RMB release (or the 1.2 s hold cap) the grab enters a brief
      (0.12 s) high-speed directional field (ThrowSpeed 520, `IsThrow`) that flings the
      held victim along the aim. The throw field **stuns** the victim on contact
      (`onThrown` → `CombatState.RegisterThrown`, a 450-impulse → stun + Tumble) so
      they exit *committed* — control-muted, able to tech, and bouncing hard off
      terrain (the player Impact profile already rebounds at 0.35 above a 300 impulse)
      — instead of keeping full control out of the throw. Into terrain at high percent
      that's the Phase 5 KO.
- [ ] **Guard rebalance — DEFERRED (needs redesign vs. the Phase 5 percent model).**
      "Chip damage / guard stamina" no longer maps, since direct hits don't touch HP
      anymore — guard should instead bleed percent/knockback through, and the
      just-frame parry on Shift *press* is a separate task. The existing full-parry
      `GuardAction` already supplies "guard beats attack," so the RPS triangle closes:
      grab beats guard (ignores it), attack beats grab (whiff lag / struggle), guard
      beats attack (parry).
- [x] Sim tests: `GrabTests` — grab flags GrabbedActive; grab ignores guard; a
      grabbed victim's struggle slash stuns the grabber and breaks the grab (freeing
      them); releasing the grab flings the victim (Vx > 200).


## Tuning principles

- Soft everything: forces/servos/springs, never velocity writes or position locks;
  entry gates (capabilities), never forced state exits.
- Express new durations in **seconds**, convert to frames at load.
- Hitstun scalars, hold spring constants, knockback scaling `k`, tech window →
  all belong in config (movement_config.json / game_config.json) for hot-reload
  tuning.
- Every phase: update sim tests in `MTile.Tests/Sim/` (scripted-input scenarios are
  the regression net) and keep `SnapshotRoundTripTests` green when adding state
  (ActiveHold, tumble vars, guard stamina, damage accumulator).


TODO later
- [ ] **Tech.** Buffered Jump intent (existing `IntentBuffer` + `Refresh` pattern)
      within ~6 frames (100 ms at 60 fps) before wall/ground impact while in
      Tumble → convert the splat into a servo'd bounce (`JumpServo` — no velocity
      snap) + brief hurtbox invuln (~10 frames) + clear hitstun. No tech → small
      bounce + a short grounded knockdown window (stun-band state) before normal
      states resume.
- [ ] Interaction with crush damage: tech *prevents* wall-splat crush damage
      (Phase 5 makes that damage matter) — that's the risk/reward.