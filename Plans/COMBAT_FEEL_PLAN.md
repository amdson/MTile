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

## Phase 0 — switch the sim to 60 fps

30 fps was a testing rate. Do this **first** so all combat tuning below happens once,
at the real rate. Spring/servo stability margins (`K·dt² + D·dt < 2`) all improve.

- [ ] `Simulation.FixedDt` (`Simulation.cs:22`) → `1f / 60f`. `Game1.TargetElapsedTime`
      already derives from it (`Game1.cs:77`).
- [ ] Audit every **frame-count** constant (seconds-based durations like action
      `Duration` and `MaxJumpHoldTime` are rate-independent and need nothing).
      Prefer converting these to seconds-at-load (`frames = (int)(seconds / FixedDt)`)
      so this never bites again; otherwise double them:
  - [ ] `CombatState.cs`: `InitialHitstunFrames` 8, `ExtensionHitstunFrames` 4,
        `StunFrames` 18, `GuardChargedFrames` 24 (these get retuned in Phase 1 anyway).
  - [ ] `ActionStates.cs`: all `ConditionState.SetFor(...)` durations — combo windows
        (30) and recovery (3–12) at lines ~297–353, 411–438, 485, 647, 972, 1023,
        1576, 1797, 1908, 2044.
  - [ ] `IntentBuffer.cs`: global lifetime 60, `JumpBufferFrames` 4.
  - [ ] `InputParser.cs`: `ClickMaxHoldFrames` 6.
  - [ ] `PlayerCharacter.cs`: `CrushCooldownFrames` 6.
  - [ ] Grep for any other `Frames` constants / literal frame counts in checkers and
        movement states.
- [ ] Update `MTile.Tests` that count frames (`CombatHitstunTests`,
      `CoveredJumpStunGateTests`, ledge tests, snapshot round-trip) — ideally express
      expectations via the same seconds→frames conversion.
- [ ] Sanity-pass movement feel after the switch (jump arc, wall slide, ledge pull
      timing) before touching combat numbers.

## Phase 1 — make hits displace (biggest feel change, mostly tuning)

- [ ] **Hitstun movement modifiers.** While `HitstunActive`, apply modifiers in the
      same slot actions use (`ApplyMovementModifiers` ordering in
      `PlayerCharacter.Update`): `AirAccel ×~0.15`, `WalkAccel ×~0.15`,
      `AirDrag ×~0.2`, `GroundFriction ×~0.3`. The residual control **is** DI —
      tune the accel scalar to taste rather than adding a separate DI system.
- [ ] **Don't let the speed cap eat knockback.** `AirControl.Apply`
      (`Character/AirControl.cs:37-39`) brakes vx above `MaxAirSpeed` back to the cap
      even with input held. During hitstun either raise `MaxAirSpeed` modifier ≫ 1 or
      (cleaner) add a flag/overload so the over-cap brake is skipped and input can
      only *add* speed up to the cap, never bleed externally-applied velocity.
      Same check for ground friction vs. horizontal knockback while standing.
- [ ] **Impulse-scaled hitstun.** Replace constant `InitialHitstunFrames` with
      `hitstunFrames = impulse / K` (`CombatState.OnHitRegistered`). At 60 fps start
      with K ≈ 12: Slash1 (200) → ~16f (0.27 s), Slash3/Stab (380) → ~32f (0.53 s),
      Pulse (450) → ~37f. Keep the diminishing-extension rule for follow-up hits
      (extension = scaled value × ~0.5) as the anti-infinite.
  - [ ] Decide whether `StunActive` survives as a separate tier or collapses into
        "long hitstun + Tumble" (Phase 4). Interim: keep the 350 threshold as-is.
- [ ] **Remove victim post-hit invulnerability.** `HitInvulnDuration`
      (`PlayerCharacter.cs:42`, used at :80, :94, :110) → delete the hurtbox
      suppression for ordinary hits. Per-attack dedupe via `HitId`
      (`World/CombatSystem.cs`) already prevents one swing hitting twice.
      **Keep** the separate crush cooldown (`PlayerCharacter.cs:50`,
      `CrushCooldownFrames`) — that one prevents physics-contact jank, not combos.
  - [ ] Update `EcsComponents.HitInvulnRemaining` + snapshot fields accordingly.
  - [ ] If degenerate rapid-rehit cases appear (e.g. Pulse's 12 segments), fix at the
        source with per-attack dedupe windows, not blanket invuln.
- [ ] Update `CombatHitstunTests`; add a test that knockback displacement over the
      hitstun window is within X% of the ballistic (no-input) trajectory while the
      victim holds toward the attacker.

**Playtest checkpoint:** hits should now visibly move people, light strings should be
possible, juggles half-work (wall-cling still rescues — that's Phase 4).

## Phase 2 — hit effects beyond knockback (holds, pulls, sustained push)

Generalize "a hit = one impulse" so moves can shape victim motion. Two tiers:

- [ ] **Tier 1 (cheap): directional set-knockback.** The attacker computes
      `KnockbackImpulse` per hit already — nothing says it must point away.
  - [ ] GroundSlash1/2: aim a *small* impulse inward/upward toward the arc center in
        front of the attacker (e.g. magnitude ~60–80 toward `attackerPos +
        facing·arcRadius·0.5`), so victims are nudged INTO the next slice instead of
        out of range. With Phase 1's control-mute + low drag this may be enough.
  - [ ] GroundSlash3 keeps/raises its big outward launch (the payoff hit).
  - [ ] Hitstun from these inward hits still applies (impulse-scaled — so give S1/S2
        an explicit hitstun-frames override rather than deriving from the now-small
        impulse; see Tier 2 payload).
- [ ] **Tier 2 (real system): holding fields — stateless, broadcast like hitboxes.**
      A hold is NOT a persistent effect written onto the victim. It is a value-typed,
      single-frame `ForceField` the attack re-broadcasts every frame it is active,
      mirroring the `Hitbox`/`HitboxWorld` model (`World/Hitbox.cs:34-36`): the field
      world is cleared each frame, publishers re-broadcast, and anyone inside the
      region is pulled *this frame*. No cross-frame identity, no expiry bookkeeping,
      **nothing new to snapshot** — after a restore, fields regenerate from
      already-snapshotted FSM/`ActionVars` state exactly like hitboxes do.
  - [ ] `ForceField` struct: `Region` (AABB, optional polygon narrow-phase like
        Hitbox.Shape), `Owner` faction + `Source` (skip self), `Focus` (world point),
        `TargetSpeed`, `MaxAccel`, `Targets`. Force shape = saturated velocity-servo
        toward Focus: `clamp((targetVel − vel)/dt, ±MaxAccel)` — the existing
        `AirControl.SoftClampVelocity` vocabulary. Force-capped means escape is
        always a physics question; overlapping fields compose by summation.
  - [ ] `ForceFieldWorld` (clear → publish → apply), applied during force
        accumulation **before** the physics step so the pull acts the same frame —
        i.e. alongside `ApplyActionForces`, not in the post-step combat phase.
        Applies to any hurtbox-publishing body in region (players + entities),
        owner-faction filtered. Per-frame force ⇒ no dedupe needed.
  - [ ] Explicit `HitstunFramesOverride` on `Hitbox` (separate, small): weak
        multi-hits can carry real hitstun independent of their now-small impulse.
  - [ ] Wire GroundSlash1/2 to broadcast a field over the arc region focused at the
        next slice's center, during active + recovery frames (`RecoveryAction` is a
        live state and can publish a weaker continuation so the S1→S2 gap holds).
        S3 broadcasts none — it's the launch payoff.
  - [ ] Decide in playtest whether fields affect non-hitstunned players (suction in
        neutral) or gate the applied force on `HitstunActive` — both stay stateless,
        the gate is just a read.
  - [ ] Later consumers: Pulse = outward radial field over its active window
        (instead of one 450 impulse); Stab = directional field dragging victims
        along the lunge; eruption knock-up fields (Phase 7).
- [ ] Determinism: fields read sim state only and live in no snapshot; add a
      restore-mid-slash case to `SnapshotRoundTripTests` verifying the field
      rebuilds identically on the replayed frame.
- [ ] Sim test: scripted S1→S2→S3 vs. a victim holding away — assert all three
      connect from a max-range opener.

## Phase 3 — commitment spectrum (startup, whiff lag, on-hit cancels)

- [ ] **Attacker hit-confirm channel.** Mirror the `PeekRecoil` inbox in
      `CombatSystem`: per-`HitId` entity-hit count readable the next frame
      (`PeekHits(hitId)` or an `OnDealtHit` callback to the Source entity).
- [ ] **On-hit cancels / whiff lag.** Combo flags are currently set unconditionally
      in `Exit` (`ActionStates.cs:297` etc.). Change slashes to:
      - hit confirmed → set `Slash2Ready`/`Slash3Ready` (cancel window) + short
        recovery (current values);
      - whiff → no combo flag (or much shorter window) + **longer** recovery
        (~2–3× hit recovery; at 60 fps think 10–20 frames on heavies).
      Movement stays free during recovery — commitment lives in attack
      availability only, which `RecoveryActive` + passive-priority preemption
      already models.
- [ ] **Speed/power spectrum.** Add real startup to 2–3 heavies (charged slash
      variant, Pulse already has 0.15 s; Stab's 0.12 s pre-active is a good template)
      and make their knockback/hitstun clearly worth it. Keep S1/AS1 as the fast
      weak pokes. Target at 60 fps: pokes ~2–4f startup / weak, heavies ~12–20f
      startup / launch + stun tier.
- [ ] Sim test: whiffed S1 cannot chain into S2; hit S1 can.

## Phase 4 — tumble, tech, and gating defensive movement

- [ ] **Extend `MovementCapability`** (`Character/MovementCapability.cs`): add
      `WallCling`, `LedgeGrab` (and optionally `Attack`) flags. Declare them on
      `WallSlidingState`, `LedgeGrabState`/`LedgePullState`. Block them while
      `HitstunActive` (today only `Jump` is blocked) — this single change creates
      juggles and edgeguards from existing knockback.
- [ ] **TumbleState** in the launch band (Active ~50, Passive ~25, per
      `MovementPriorities` table): entered when hit impulse ≥ threshold (reuse the
      350 stun threshold). Muted control (like `StunnedState`), blocks
      WallCling/LedgeGrab/Attack capabilities, exits on ground contact or tech.
      Replaces/absorbs `StunnedState` for airborne heavy hits.
- [ ] **Tech.** Buffered Jump intent (existing `IntentBuffer` + `Refresh` pattern)
      within ~6 frames (100 ms at 60 fps) before wall/ground impact while in
      Tumble → convert the splat into a servo'd bounce (`JumpServo` — no velocity
      snap) + brief hurtbox invuln (~10 frames) + clear hitstun. No tech → small
      bounce + a short grounded knockdown window (stun-band state) before normal
      states resume.
- [ ] Interaction with crush damage: tech *prevents* wall-splat crush damage
      (Phase 5 makes that damage matter) — that's the risk/reward.
- [ ] Sim tests: launched player cannot wall-cling mid-tumble; tech input inside
      window bounces with invuln; outside window eats crush + knockdown.

## Phase 5 — escalation arc (damage-scaled knockback, terrain as blast zone)

- [ ] **Damage-scaled knockback.** Accumulate damage taken (keep or replace the
      3-HP model — decide here) and scale applied knockback:
      `appliedImpulse = baseImpulse × (1 + damageTaken × k)`. Early hits combo
      (low knockback, hitstun chains via Phase 1 scaling), late hits launch across
      the arena.
- [ ] **Wall-splat as the closer.** Crush damage (`CrushImpulseThreshold` 700,
      `PlayerCharacter.cs:45-72`) already converts impact impulse to damage — retune
      thresholds so high-percent launches into stone hurt badly, sand/dirt less
      (material-aware via tile MaxHP). KO = crush-out or falling into carved-out
      pits; players literally build/destroy their own blast zones.
- [ ] Revisit `MaxHealth`/respawn flow to fit whichever model wins (HP vs.
      percent-hybrid).
- [ ] Playtest for the Smash loop: combos at low damage → kill threats at high.

## Phase 6 — grab + guard rebalance (complete the RPS triangle)

- [ ] **Grab action.** Close-range action (input TBD — e.g. click while guarding, or
      dedicated key). Implementation is the Phase 2 field system turned up: while the
      grab action is active it broadcasts a strong short-range holding field focused
      just in front of the attacker (still stateless — the "grab" persists only
      because the action state persists and keeps broadcasting). Victim capabilities
      masked while in-field + hitstunned; mash (intent count) ends the grab action
      early, which kills the field automatically. **Ignores guard/parry.**
- [ ] **Grabbed-state signal (tiny, snapshotted).** Victim-side actions need to know
      "I am grabbed *right now*", but fields are stateless and cleared each frame.
      Write `LastGrabbedFrame` (int) on the victim's `CombatState` during grab-field
      application — same pattern as `LastHitFrame` — and derive
      `IsGrabbed = currentFrame − LastGrabbedFrame <= 1`. One int in
      `Clone`/`CopyFrom`; everything else stays field-derived.
- [ ] **Struggle attacks: weak slash + weak stab usable while grabbed.** New
      low-passive-priority variants (`GrabbedSlash`, `GrabbedStab`) whose
      preconditions require `IsGrabbed` and which are **exempt** from the
      grabbed/stunned attack gate (the GuardAction can-fire-during-stun precedent —
      they're the only attacks that skip `BlocksAttack`/capability masking).
  - [ ] Weak: small damage (~0.25), short range (the grabber is held-adjacent by the
        field focus, so they connect), modest impulse — just enough to register
        hitstun on the grabber.
  - [ ] **Grab-break falls out for free:** the grab action's `CheckConditions`
        require `!Combat.HitstunActive` (you can't hold someone while in hitstun).
        Struggle attack lands → grabber enters hitstun → grab action exits → field
        dies. No explicit release channel.
  - [ ] Give struggle attacks a few frames of startup — that startup is the
        grabber's guaranteed window to throw. Tune startup vs. throw timing so a
        prompt throw always wins and a greedy/slow grab always gets hit: this is
        the pressure that keeps grabs quick.
  - [ ] Mash-to-escape (intent count) stays as the no-risk slow option; struggle
        attack is the faster option that also deals chip — but it commits the
        victim, so a predicted struggle can be thrown on reaction.
- [ ] **Throws.** Release with a directed impulse; terrain synergy is the identity:
      throw into stone = crush bonus, throw down into sand = bury, throw off your
      carved ledge = edgeguard.
- [ ] **Guard rebalance** (`CombatState.TryParry`, `GuardAction`):
  - [ ] Blocked hits still apply pushback to the guarder (spacing game) and chip
        damage or decaying guard "stamina" — sustained pressure breaks through.
  - [ ] Zero-damage parry becomes a tight just-frame on Shift *press* (~6 frames at
        60 fps); held guard merely reduces. `GuardRetaliate` stays as the
        hard-read reward.
- [ ] Sim tests: grab beats guard; attack beats grab attempt (grabber hit during
      grab startup); guard beats attack; victim struggle-slash breaks an idle grab
      before max duration; a prompt throw completes before struggle startup lands.

## Phase 7 — terrain-combat identity (backlog, pull items forward as they fit)

- [ ] Material-aware knockback outcomes: splat on stone (crush + tech), stick/sink
      in foam and sand (bury state, mash out) — player-built foam becomes a combo
      extender / trap.
- [ ] Eruption-as-launcher: erupting blocks under an opponent applies launch
      impulse — constructed anti-air.
- [ ] Breaking the floor under a cornered opponent as a kill confirm.
- [ ] Debris/shrapnel from slashed tiles as weak projectiles (ties slash → ranged
      poke without a new weapon).

---

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
