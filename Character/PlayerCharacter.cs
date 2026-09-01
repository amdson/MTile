using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace MTile;

public class PlayerCharacter : IHittable
{
    // CORE GAMEPLAY CONSTANT — do not change without an explicit decision from the project
    // owner. 12 is the original size (restored 2026-07-18 after an unnoticed 9.5 detour in
    // c7e110a): standing head height ≈ R·(1+2·sin60°) ≈ 32.8px, deliberately JUST over two
    // tiles so 2-high corridors force a crouch. Ripple surfaces if this ever moves again:
    // animation COM stamps in SkeletonStates/*.json, checker bands, corridor/mantle config,
    // test fixture geometry.
    public const float Radius = 12f;

    // Body silhouette: a regular hexagon squeezed to half width (twice as tall as
    // wide). The slim profile is a core gameplay attribute — it threads 1-tile
    // gaps and reads as a nimble runner; collision, the C-obstacle template, and
    // the corrector's clearance geometry all derive from this one polygon.
    public const float BodyWidthScale = 0.5f;

    public static Polygon CreateBodyPolygon()
    {
        var verts = Polygon.CreateRegular(Radius, 6).GetVertices(Vector2.Zero);
        for (int i = 0; i < verts.Length; i++) verts[i].X *= BodyWidthScale;
        return new Polygon(verts);
    }

    // Standing head height above the floor: float height (= Radius) + the hexagon body's
    // full vertical extent (2 · R·sin60°). Used by auto-crouch to decide whether standing
    // fits under a ceiling.
    public static readonly float StandingHeight = Radius + 2f * Radius * MathF.Sin(MathF.PI / 3f);

    // Stable identity for snapshot/restore (IHittable.Id). Assigned by
    // Simulation from its deterministic id counter, shared with entities.
    public EntityId Id { get; set; }

    public readonly PhysicsBody Body;
    // Owned visual. Game1 syncs Position each frame and calls Update + Draw.
    // Null in headless test contexts where rendering isn't needed.
    public readonly AnimatedSprite Sprite;

    // Global gravity force, set by Game1 (defaults match Game1.Gravity = (0, 600)).
    // Read by Update to apply MovementModifiers.GravityScale as a counter-force on
    // the body's AppliedForce — same trick Entity.PreStep uses, just owned by the
    // player instead of the entity factory.
    public Vector2 Gravity = new(0f, 600f);

    // Combat faction. Default Player; settable so a second PlayerCharacter spawned
    // for two-player combat (Game1.AddSecondaryPlayer / SimRunner.RunMulti) can be
    // re-tagged Enemy/Neutral and become a valid target through CombatSystem's
    // self-damage filter. Real solo play never touches this — the default stands.
    public Faction Faction { get; set; } = MTile.Faction.Player1;

    // Combat stats. Mass divides incoming knockback impulses (heavier = less yeet).
    //
    // MaxHealth is the pool a hit now comes straight out of (see OnHit): a stock
    // slash is 0.5, so 10 clean connects, a S1→S2→S3 string is 1.5 and a Stalker
    // lunge is 1.0. It was 3 under the escalation model, where direct hits cost no
    // HP at all and the pool only ever drained to crush impacts — 3 was sized for a
    // channel that fired a handful of times per life. A pool that every hit bites
    // has to be deeper to leave room for a fight, hence 5.
    public float   MaxHealth = 5f;
    public float   Health;
    public float   Mass      = 2.5f;
    // Spawn protection only (COMBAT_FEEL_PLAN Phase 1). The old post-HIT invuln is
    // gone — it outlasted hitstun, which made follow-up hits (strings, juggles)
    // mathematically impossible. Single-attack multi-frame is already handled by
    // the (HitId, Target) dedupe in CombatSystem; stacked-attacker burst damage is
    // now a legitimate combo. Respawn still grants this window so a fresh spawn
    // isn't hit on frame one.
    private const float HitInvulnDuration = 0.4f;
    private float _hitInvulnRemaining;

    // Crush-damage tuning. Reads PhysicsBody.LastImpulseMagnitude (max |vnRel|
    // absorbed by collision resolution last step). Below the threshold normal
    // landings and casual wall-bumps are free; above it the excess scales into
    // HP damage and also routes through Combat.OnHitRegistered so hitstun / stun
    // gates kick in (a hard fall briefly locks jump even though no attack hit).
    // Separate from _hitInvulnRemaining: a slash-then-thrown-into-wall combo
    // should land both the slash damage AND the crush damage, not one or the
    // other. _lastCrushFrame is the cross-event cooldown that prevents the same
    // wall-slam being charged twice.
    //
    // Threshold sized so the player's own jumps + sand impacts don't self-damage:
    //   * Held single jump lands at vy ≈ 260-270 (measured)
    //   * Held running jump (RunJumpVelocity -120) lands ~290
    //   * Held jump + double jump compounds to ~340-370 in worst case
    //   * Sand impact: PhysicsWorld now caps the body's per-hit Δv at the tile
    //     face's absorption capacity. For sand that's
    //     (ImpulseThreshold + MaxHp/DamagePerUnitImpulse)/Mass per cell —
    //     290 px/s on one cell, 580 on two (worst case for the hex body),
    //     regardless of incoming speed. Threshold of 700 means hitting any
    //     amount of sand never reaches the crush gate.
    // Plunges onto stone (cap 1040 per cell, no break-through ⇒ full carry-zero
    // at vnAbs ≈ 849 from terminal velocity) still trigger crush; 2-cell-dirt
    // plunges likewise. Was 400 — pre-absorption-cap that was sized for self-
    // jumps only, before sand impacts could legally exceed it.
    // Armored hits are damped, not no-sold: the body still visibly reacts so a
    // tanked hit reads as armor, not a whiff (HIT_AIRLOCK_PLAN open q. 3 —
    // knockback scaling chosen over the binary no-sell).
    private const float ArmorKnockbackScale = 0.3f;

    private const float CrushImpulseThreshold = 700f;
    private const float CrushDamagePerImpulse = 0.003f;
    private const float CrushCooldownSeconds  = 0.2f;   // ≈ the original 6 frames at 30 fps
    // Sprout crush (a growing block wedged us against terrain, so physics destroyed it).
    // Flat HP per destroyed cell — one cell costs the same as taking a stock slash,
    // punishing enough that being walled in is a real threat, survivable enough that a
    // single bad sprout is not a death sentence.
    private const float SproutCrushDamage = 0.5f;
    // Impulse magnitude reported to OnHitRegistered for hitstun/stun scaling. A squeeze
    // has no real |vnRel| to report, so this stands in for "how hard that felt" — just
    // over CrushImpulseThreshold, so it reads as a solid hit and nothing more.
    private const float SproutCrushImpulseFeel = 750f;
    private int _lastCrushFrame = int.MinValue / 2;
    // The last frame this player lost HP from ANY source — a landed hit or a crush.
    // Anchors the regen delay below. Separate from _lastCrushFrame, which is the
    // crush path's own re-bill cooldown and must not be pushed forward by an
    // ordinary hit.
    private int _lastDamageFrame = int.MinValue / 2;

    // Out-of-combat HP regen. Deliberately a slow trickle behind a long delay, not
    // the fast pool it was: under the escalation model direct hits cost no HP, so a
    // brisk 0.8/s refill only ever undid a crush landing, and the lasting pressure
    // lived in the (never-regenerating) percent meter. Now that every hit bites HP,
    // that same rate would erase a clean slash in well under a second and hand the
    // damage model straight back its irrelevance. At 0.15/s behind a 3 s delay,
    // disengaging for ten seconds buys back one slash — enough that a fight you walk
    // away from isn't permanent, not enough to out-heal one you're still in.
    private const float HealthRegenPerSecond = 0.15f;
    private const float RegenDelaySeconds    = 3.0f;
    // The dt this player is being stepped at, captured at the top of Update.
    // OnHit fires from CombatSystem.Apply (after all updates, same frame), so it
    // reads the current frame's value. Not snapshotted — rewritten every Update.
    private float _dt = Simulation.FixedDt;
    public bool IsAlive => Health > 0f;

    // Player is one big hurtbox covering its body bounds. Future: split into head/body
    // for headshots, or shrink during dodge frames. Suppressed during invuln so
    // CombatSystem doesn't even consider hits during the recovery window.
    public void PublishHurtboxes(HurtboxWorld world)
    {
        if (_hitInvulnRemaining > 0f) return;
        world.Publish(new Hurtbox(Body.Bounds, Faction, Id));
    }

    public Vector2 OnHit(in Hitbox hit, in Hurtbox myHurtbox)
    {
        // Tech i-frames (Phase 4): a freshly-teched player no-ops incoming hits for
        // a short window so the tech recovery isn't immediately re-punished.
        // Early-outs still return the authored impulse so a stab that pogoes off a
        // parrying/invulnerable player recoils the attacker exactly as before.
        if (_abilities.Combat.IsInvulnerable(_frame)) return hit.KnockbackImpulse;

        // Guard — timing-based (CombatState.ResolveGuard). A front-cone hit arriving
        // inside the window right after the stance came up is absorbed completely: no
        // damage, no knockback, no hitstun, and a weak one also charges GuardRetaliate.
        // Anything later leaks through — harder the longer the button has been held —
        // and breaks the guard on the way in. `guard` scales the percent and the
        // knockback below; it is (1, 1) when the guard wasn't involved.
        var guard = _abilities.Combat.ResolveGuard(hit.KnockbackImpulse, hit.BodyDamage,
                                                   _abilities.Facing, _frame, _dt);
        if (guard.Absorbed) return hit.KnockbackImpulse;

        // Struggle / grab-break (COMBAT_FEEL_PLAN Phase 6). A grabbed victim's exempt
        // slash erodes THIS player's grab strength instead of dealing knockback/percent/
        // hitstun — so struggling wears the hold down without ever stunning the grabber
        // (a stun would let the victim trade out of every grab, which broke balance).
        // GrabAction.CheckConditions drops the hold once GrabStrength hits 0.
        if (hit.GrabStrengthDamage > 0f)
        {
            _abilities.Combat.ErodeGrab(hit.GrabStrengthDamage);
            return hit.KnockbackImpulse;
        }

        // A landed hit costs HP, directly and immediately — the same rule Entity.OnHit
        // has always applied, so a player and a creature read the same hitbox the same
        // way. BodyDamage is Damage for almost every attack; the handful that have to
        // carve stone to do their job declare a separate, smaller body number (see
        // Hitbox.BodyDamage).
        //
        // This replaced the escalation model (COMBAT_FEEL_PLAN Phase 5), in which a
        // direct hit chipped no HP at all: it raised a monotonic percent, the percent
        // scaled knockback, and HP only came off when the resulting launch slammed you
        // into terrain. The indirection cost more than it bought — a clean hit read as
        // nothing happening, and the knockback multiplier (2.5× at 100%) made the whole
        // game feel like it was made of beach balls. Crush damage survives as its own
        // channel below; it just isn't the only way to lose HP any more.
        float hpLoss = hit.BodyDamage * guard.DamageScale;
        Health -= hpLoss;
        _abilities.Combat.AddDamage(hpLoss);
        _lastDamageFrame = _frame;
        var res = HitResolver.Resolve(in hit, Mass, Body.Velocity);

        // Superarmor (Plans/HIT_AIRLOCK_PLAN.md §4): the live action may tank
        // hits below its armor threshold. Percent still accrued above (armor
        // takes the damage), attacker recoil unchanged, but knockback is scaled
        // way down and the hit never REGISTERS — no hitstun, no stun, and
        // therefore no flinch eviction (RecoveryAction keys off LastHitFrame).
        float armor = _currentAction?.ArmorProfile(in _actionVars) ?? 0f;
        if (armor > 0f && res.Strength < armor)
        {
            Body.Velocity += res.TargetDeltaV * ArmorKnockbackScale * guard.KnockbackScale;
            return res.Impulse;
        }

        // Scaled here rather than through HitResolver's `scale` because that also feeds
        // Collision mode's MinLaunch floor, which would hand back most of the knockback
        // a leaky guard just ate.
        Body.Velocity += res.TargetDeltaV * guard.KnockbackScale;

        // For render-only cosmetics (directional knockback cue, weapon flash) that
        // want more than LastHitImpulse's magnitude. Falls back to the hit's launch
        // axis when the resolved knockback was ~zero (e.g. a heavy target barely
        // budged) so the cue still has a direction to draw.
        _abilities.Combat.LastHitDir = res.TargetDeltaV.LengthSquared() > 1e-4f
            ? Vector2.Normalize(res.TargetDeltaV)
            : hit.StrikeDir;

        // Register the hit for hitstun (every hit) + the stun-threshold check.
        // HitResult.Strength is the impulse magnitude (Impulse mode) or the closing
        // speed (Collision mode) — pre-mass either way, so strength reads consistently
        // across masses. Hold-slashes still carry an explicit HitstunSecondsOverride.
        // Strength rides the same knockback share: it IS the knockback magnitude
        // (pre-mass), so leaving it whole while halving the actual velocity change
        // would stun the victim as if nothing had been blocked.
        _abilities.Combat.OnHitRegistered(_frame, res.Strength * guard.KnockbackScale, _dt,
                                          hit.HitstunSecondsOverride);
        return res.Impulse;
    }

    // Called by Game1 on Health <= 0 to reset to a clean starting state. Cheaper
    // than a full re-init of the FSMs — the next Update will re-evaluate state
    // from the new position and arrive at Falling → Standing naturally.
    public void Respawn(Vector2 position)
    {
        Body.Position = position;
        Body.Velocity = Vector2.Zero;
        Health        = MaxHealth;
        _hitInvulnRemaining = HitInvulnDuration;
        // A KO ends the life, so the running damage tally starts over with it — and
        // the regen delay clears too, or a fresh spawn would be locked out of regen
        // by the hit that killed the last one.
        _abilities.Combat.DamageTaken = 0f;
        _lastDamageFrame = int.MinValue / 2;
    }
    
    private readonly PlayerAbilityState _abilities = new();
    // Read-only view for the HUD (build meters, condition flags). Sim code reaches
    // _abilities directly; this exists so rendering doesn't need a back door.
    public PlayerAbilityState Abilities => _abilities;
    private MovementState _currentState;
    // Plain-data per-activation state for the current movement state (see MovementVars).
    // Lives here (not on the flyweight state instances) so it's a single snapshot unit.
    private MovementVars _moveVars;

    private readonly List<MovementState> _stateRegistry = new();

    // Long-lived per-direction corridor scratch for EnvironmentContext.GetCorridor — pure
    // derived data, fully rewritten by every scan (never snapshot state); pooled here only
    // so the per-frame reflex probe doesn't allocate.
    private readonly Corridor _corridorScratch1      = new(1);
    private readonly Corridor _corridorScratchMinus1 = new(-1);

    // Pooled corrector scratch (BALLISTIC_CORRECTOR_PLAN): predict/rows/solve buffers
    // for the corrector-driven maneuver states. Derived data only — never snapshotted.
    private readonly CorrectorScratch _correctorScratch = new();
    // Render-side access to the corrector's captured trajectories (reference /
    // ballistic / solved). The host sets CaptureTrajectories from its draw flags
    // and reads the buffers after stepping; sim logic never reads them.
    public CorrectorScratch CorrectorDebug => _correctorScratch;
    // What the applied corrector solve exerted THIS step, by channel and by
    // contact tile (CorrectorLedger). Same-step derived data: sim consumers
    // (block-breaking reactions) must read it inside the step that wrote it;
    // never snapshot state.
    public CorrectorLedger ForceLedger => _correctorScratch.Ledger;

    private const int HistorySize = 32;
    private readonly MovementState[] _stateHistory = new MovementState[HistorySize];
    private int _historyHead = 0;
    private readonly Func<int, MovementState> _getState;

    private readonly List<ActionState> _actionRegistry = new();
    private ActionState _currentAction;
    // Plain-data per-activation state for the action FSM — action-side analogue of
    // _moveVars. Passed by ref into the current action's lifecycle, by `in` into its
    // read-only hooks (modifiers/forces/draw). See ActionVars.
    private ActionVars _actionVars;
    private readonly ActionState[] _actionHistory = new ActionState[HistorySize];
    private int _actionHistoryHead = 0;
    private readonly Func<int, ActionState> _getAction;

    // Input-parser + intent buffer: edge-triggered gesture detection feeds an
    // intent queue the action FSM reads from. Replaces the old inline release-detection
    // in SlashAction.
    private readonly InputParser _inputParser = new();
    private readonly IntentBuffer _intents    = new();
    // Monotonic frame counter — used for intent age + ConditionState flag expiry.
    // Distinct from _historyHead (which mods to HistorySize).
    private int _frame;
    public int Frame => _frame;

    // Deterministic HitId source. Defaults to a private allocator (sufficient for
    // solo play / single-player tests); Simulation overrides this with one shared
    // across all players + entities so cross-source ids never collide.
    public HitIdAllocator HitIds { get; set; } = new();

    // The sim's CombatSystem, used by actions to read per-frame recoil tallies
    // populated in CombatSystem.Apply (Newton's-third-law back-impulse on hits).
    // Null in headless tests that don't drive combat; ApplyActionForces hooks
    // guard accordingly.
    public CombatSystem CombatSystem { get; set; }

    // Player-local block selection, driven by this player's own input (1-4 keys).
    // Formerly a global static. Read by the placement actions via EnvironmentContext and
    // by the HUD. The P planner-mode toggle that used to live alongside it is gone —
    // placement is ball-based only now, so there is nothing to switch between.
    private TileType _activeBlockType = TileType.Dirt;
    // Settable so Simulation can seed the initial selection from GameConfig;
    // thereafter it's driven by this player's own input each frame. Non-placeable
    // materials are refused here rather than at each placement verb: build, paint,
    // mass deposit and the eruption ball all read this one field, so this is the single
    // choke point that keeps a GameConfig typo ("StartingBlockType": "Hardened") from
    // handing the player bedrock. Snapshot restore writes the field directly, as it must.
    public TileType ActiveBlockType
    {
        get => _activeBlockType;
        set { if (TileTypes.IsPlaceable(value)) _activeBlockType = value; }
    }

    public MovementState GetPreviousState(int framesBack)
    {
        if ((uint)framesBack >= HistorySize) return null;
        return _stateHistory[(_historyHead - framesBack + HistorySize) % HistorySize];
    }

    public ActionState GetPreviousAction(int framesBack)
    {
        if ((uint)framesBack >= HistorySize) return null;
        return _actionHistory[(_actionHistoryHead - framesBack + HistorySize) % HistorySize];
    }

    public PlayerCharacter(Vector2 startPosition)
    {
        Body = new PhysicsBody(CreateBodyPolygon(), startPosition);
        // Landing impact damage. PhysicsWorld dispatches this whenever a body
        // hits a surface (chunk OR floating-surface constraint) with vnRel < 0
        // and Impact != null. Tuning rationale:
        //   - Threshold 700: a 5-block fall (v ≈ 310 px/s) reaches impulse 775
        //     and just barely chips tiles; a 1-2 block jump (v ≈ 150-200) sits
        //     well under, so normal play doesn't damage terrain.
        //   - Mass 2.5 matches the combat-knockback Mass, so the player's
        //     "weight" reads consistently between knockback and impact.
        //   - DamagePerUnitImpulse 0.04: a 10-block plunge (v ≈ 440) does
        //     ~16 dmg spread across 2-3 cells under the body → ~5 each, which
        //     breaks Sand (max HP ~1) and cracks Dirt. Diving from very tall
        //     heights cracks Stone.
        // Slamming horizontally into walls at high speed also chips them
        // (running max ~100 px/s stays safe; bouncing > 280 px/s starts chipping).
        // Tuning lives in impact_profiles.json under the "player" key —
        // see Physics/ImpactProfiles.cs for defaults + load semantics.
        Body.Impact = ImpactProfiles.Build(ImpactProfiles.Player);
        Sprite = Sprites.Player(Radius);
        Health = MaxHealth;
        _getState  = GetPreviousState;
        _getAction = GetPreviousAction;

        // Order in the registry only matters as a tiebreaker between equal-passive
        // candidates; preconditions + ConditionState gates do the real selection work.
        // Listed roughly low-to-high priority for readability.
        _actionRegistry.Add(new NullAction());        // 0/0
        _actionRegistry.Add(new RecoveryAction());    // 10/45  — countdown + wind-up airlock (absorbed ReadyAction)
        _actionRegistry.Add(new GroundSlash1());      // 30/30
        _actionRegistry.Add(new CrouchSlash());       // 30/32  — crouch-only, no combo
        _actionRegistry.Add(new AirSlash1());         // 30/30
        _actionRegistry.Add(new StabAction());        // 30/30
        _actionRegistry.Add(new PulseAction());       // 30/30  — Circle gesture
        _actionRegistry.Add(new BurstAction());       // 30/30  — RMB during a Ready wind-up
        _actionRegistry.Add(new BlockPaintAction());     // 8/10   — plain RMB: paint outside solid, charge inside;
                                                         //          fast release with banked charge erupts (Exit)
        _actionRegistry.Add(new BlockPlaceAction());     // 8/10   — Shift+RMB single-block placement
        _actionRegistry.Add(new BlockBurstAction());     // 30/30  — LMB while RMB paints over dead air → foam puff
        _actionRegistry.Add(new GroundSlash2());      // 30/50  — combo (Slash2Ready gated)
        _actionRegistry.Add(new GroundSlash3());      // 30/50  — combo
        _actionRegistry.Add(new AirSlash2());         // 30/50  — combo
        _actionRegistry.Add(new AirTurnSlash());      // 30/35  — air backward-click turnaround
        _actionRegistry.Add(new DownAirSlash());      // 30/52  — air click in the bottom sextant → pogo chop
        _actionRegistry.Add(new AirSpinStab());       // 30/35  — air backward-swipe stab
        _actionRegistry.Add(new GuardAction());       // 35/40  — Shift held, no L/R, parry posture
        _actionRegistry.Add(new GuardRetaliateAction()); // 30/55 — click during GuardCharged
        _actionRegistry.Add(new BeamAction());           // 40/45 — Shift+LMB hold, sustained beam after charge
        // LobbedAreaAction (Shift+RMB charge) deactivated in COMBAT_FEEL_PLAN Phase 6
        // when Grab took that binding. Grab has since moved to Shift+LMB, so Shift+RMB
        // is free again — re-add the line to restore the ranged eruption. (Its
        // projectile is still live: BlockGrabAction throws one.)
        _actionRegistry.Add(new BlockGrabAction());      // 46/46 — Shift+LMB on terrain → drag-rip → orb → throw
        _actionRegistry.Add(new GrabAction());           // 48/48 — Shift+LMB hold on a victim → grab → throw
        _actionRegistry.Add(new GrabbedSlash());         // 36/36 — struggle (exempt while grabbed)
        _currentAction = _actionRegistry[0];

        _stateRegistry.Add(new FallingState());
        _stateRegistry.Add(new StunnedState());
        _stateRegistry.Add(new TumbleState());
        _stateRegistry.Add(new StandingState());
        _stateRegistry.Add(new CrouchedState());
        _stateRegistry.Add(new TerrainCarriedState());
        _stateRegistry.Add(new JumpingState());
        _stateRegistry.Add(new RunningJumpState());
        _stateRegistry.Add(new DoubleJumpingState());
        _stateRegistry.Add(new WallSlidingState(1));
        _stateRegistry.Add(new WallSlidingState(-1));
        _stateRegistry.Add(new WallJumpingState(1));
        _stateRegistry.Add(new WallJumpingState(-1));
        _stateRegistry.Add(new CoveredJumpState());
        // Climb family (ClimbStates.cs): Parkour = at-speed 1-block vault,
        // ArcJump = 2-block band, Mantle = slow/flush 1-block. Gated per frame by
        // MovementConfig.CorrectorClimbEnabled (hot-reloadable A/B), so
        // registration is unconditional.
        _stateRegistry.Add(new ParkourState(1));
        _stateRegistry.Add(new ParkourState(-1));
        _stateRegistry.Add(new ArcJumpState(1));
        _stateRegistry.Add(new ArcJumpState(-1));
        _stateRegistry.Add(new MantleState(1));
        _stateRegistry.Add(new MantleState(-1));
        _stateRegistry.Add(new DropdownState());
        _stateRegistry.Add(new LedgeGrabState(1));
        _stateRegistry.Add(new LedgeGrabState(-1));
        _stateRegistry.Add(new LedgePullState(1));
        _stateRegistry.Add(new LedgePullState(-1));
        _stateRegistry.Add(new LedgeJumpState(1));
        _stateRegistry.Add(new LedgeJumpState(-1));

        _currentState = _stateRegistry[0]; // falling
    }

    // Corrector stress harness (the "corridor" stage): strip the movement registry
    // down to the two free states, so everything between them — bump hops, head
    // tucks, unsticking — must come from the ambient corrector's applied
    // corrections. No jump, no crouch, no climb family, no ledges. Standing's
    // Update runs the ambient layer (Default policy + Stand fold), so it stays
    // on. Call from a Stage.Populate before the first Step.
    public void RestrictToFallAndStand()
    {
        _stateRegistry.Clear();
        _stateRegistry.Add(new FallingState());
        _stateRegistry.Add(new StandingState());
        _currentState = _stateRegistry[0]; // falling
    }

    public void Update(Controller controller, ChunkMap chunks, HitboxWorld hitboxes, HurtboxWorld hurtboxes, float dt, IEntitySpawner spawner = null, ForceFieldWorld forceFields = null)
    {
        _frame++;
        _dt = dt;
        if (_hitInvulnRemaining > 0f) _hitInvulnRemaining -= dt;

        // Crush damage: turn the previous step's largest |vnRel| into HP loss
        // when it crosses CrushImpulseThreshold. Reads PhysicsBody.LastImpulse
        // Magnitude (written by PhysicsWorld.StepSwept). Routes through
        // OnHitRegistered so a hard fall / wall slam also lights up Hitstun and
        // (if hard enough) Stun — "I just slammed down, give me a sec."
        if (Body.LastImpulseMagnitude > CrushImpulseThreshold
            && _frame - _lastCrushFrame >= SimFrames.FromSeconds(CrushCooldownSeconds, dt))
        {
            float excess = Body.LastImpulseMagnitude - CrushImpulseThreshold;
            float crushHp = excess * CrushDamagePerImpulse;
            Health -= crushHp;
            _abilities.Combat.AddDamage(crushHp);
            _lastDamageFrame = _frame;
            // Short fixed hitstun (the old 8-frames-at-30fps feel) and NO control
            // mute: a hard landing briefly gates jump ("give me a sec") but doesn't
            // turn walking to mush — that treatment is for combat hits.
            _abilities.Combat.OnHitRegistered(_frame, Body.LastImpulseMagnitude, dt,
                hitstunSecondsOverride: 0.27f, muteControl: false);
            _lastCrushFrame = _frame;
        }

        // Sprout crush: a block grew into us and the depenetration solver could not push
        // us clear, so PhysicsWorld destroyed it (see CrushOverlappingSprouts). Flat cost
        // per event rather than impulse-scaled — the trigger is binary ("physics gave
        // up"), and a slow squeeze carries almost no |vnRel|, which is exactly why the
        // impulse gate above never fires for it. Shares _lastCrushFrame, so a squeeze and
        // a slam can't both bill you inside the cooldown, and regen stays paused after.
        else if (Body.SproutCrushCount > 0
            && _frame - _lastCrushFrame >= SimFrames.FromSeconds(CrushCooldownSeconds, dt))
        {
            float sproutHp = SproutCrushDamage * Body.SproutCrushCount;
            Health -= sproutHp;
            _abilities.Combat.AddDamage(sproutHp);
            _lastDamageFrame = _frame;
            _abilities.Combat.OnHitRegistered(_frame, SproutCrushImpulseFeel, dt,
                hitstunSecondsOverride: 0.27f, muteControl: false);
            _lastCrushFrame = _frame;
        }

        // Out-of-combat regen: HP trickles back once you've been clear of damage —
        // hits included, not just crushes — for RegenDelaySeconds. Combat.DamageTaken
        // does NOT regen; it's the running tally of the whole life.
        if (Health < MaxHealth
            && _frame - _lastDamageFrame >= SimFrames.FromSeconds(RegenDelaySeconds, dt))
            Health = MathF.Min(MaxHealth, Health + HealthRegenPerSecond * dt);

        var input = controller.Current;
        var prev  = controller.GetPrevious(1);

        // Block-picker selection from this player's own input. Number keys are
        // level-triggered (re-assign harmlessly). Formerly interpreted in
        // Game1/Simulation against a global static — now player-local and
        // rollback-deterministic. (The P planner toggle that lived here is gone.)
        if (input.Num1) _activeBlockType = TileType.Stone;
        if (input.Num2) _activeBlockType = TileType.Dirt;
        if (input.Num3) _activeBlockType = TileType.Sand;
        if (input.Num4) _activeBlockType = TileType.Foam;

        _abilities.JumpJustPressed  = input.Space && !prev.Space;
        _abilities.UpJustPressed    = input.Up    && !prev.Up;
        _abilities.DownJustPressed  = input.Down  && !prev.Down;

        // Expire combo / recovery flags whose window closed since last frame.
        _abilities.Condition.Tick(_frame);
        // Expire hitstun / stun whose window closed.
        _abilities.Combat.Tick(_frame, guardHeld: input.Shift);

        // Hitstop (Plans/HIT_FEEL_PLAN.md phase 1): freeze the CURRENT ACTION's
        // progression for a few frames after a landed combat hit — no new hitboxes,
        // no ApplyActionForces recoil/lunge, guarded at those two call sites below.
        // Deliberately NOT a blanket skip of this method:
        //   - Movement state selection/Update (incl. TumbleState's tech-window check,
        //     TumbleTechTests) must keep running every frame regardless — movement
        //     must not read action state (CLAUDE.md), so it can't be affected by an
        //     action-side freeze anyway, and gating it here broke tech resolution
        //     under sustained hits.
        //   - Action FSM SELECTION (incl. RecoveryAction's flinch eviction of a
        //     mid-swing attack, HitEvictionTests) must also keep running every frame,
        //     hitstop or not, or a fresh hit could never interrupt what it just hit —
        //     only the chosen action's Update/ApplyActionForces freeze, not selection.
        // Physics integration (gravity/terrain collision) is untouched either way.
        bool hitstopFrozen = _abilities.Combat.HitstopActive;

        // Edge-detect input gestures and enqueue intents. Done BEFORE the FSMs so
        // freshly-released clicks are visible to action preconditions this frame.
        // Gestures are measured relative to the body so camera follow (player motion)
        // doesn't read as cursor motion.
        _inputParser.Detect(controller, _intents, _frame, dt, Body.Position);

        // Per-tick corrector trajectory captures expire every frame — the overlay
        // only ever shows what THIS timestep computed (see CorrectorScratch).
        _correctorScratch.BeginFrame();

        var ctx = new EnvironmentContext
        {
            Input          = input,
            Controller     = controller,
            PreviousState  = _getState,
            PreviousAction = _getAction,
            Chunks         = chunks,
            Hitboxes       = hitboxes,
            Hurtboxes      = hurtboxes,
            ForceFields    = forceFields,
            Spawner        = spawner,
            Faction        = Faction,
            SelfId         = Id,
            HitIds         = HitIds,
            CombatSystem   = CombatSystem,
            ActiveBlockType = _activeBlockType,
            Intents        = _intents,
            ActionRegistry = _actionRegistry,
            CurrentActionVars = _actionVars,
            Condition      = _abilities.Condition,
            Combat         = _abilities.Combat,
            CurrentFrame   = _frame,
            Dt             = dt,
            Body           = Body,
            Mass           = Mass,
            Intent         = InputIntent.From(controller),
            Modifiers      = MovementModifiers.Identity,
            CorridorScratch1      = _corridorScratch1,
            CorridorScratchMinus1 = _corridorScratchMinus1,
            Corrector             = _correctorScratch,
            Gravity               = Gravity,
        };

        // Facing tracks the last non-zero horizontal input so standstill actions
        // (slash from a stop) still have a direction. Movement code doesn't read this.
        // Roadmap §1.6: facing is sticky in air — only ground-state input writes here.
        // Air-direction changes route through AirTurnSlash / AirSpinStab, which flip
        // Facing themselves on Enter.
        if (IsGrounded && ctx.Intent.CurrentHorizontal != 0) _abilities.Facing = ctx.Intent.CurrentHorizontal;

        if (IsGrounded)
        {
            _abilities.HasDoubleJumped = false;
        }

        if (!_currentState.CheckConditions(ctx, _abilities, ref _moveVars))
        {
            _currentState.Exit(ctx, _abilities, ref _moveVars);
            _currentState = _stateRegistry.First(s => s is FallingState);
            if (SimTrace.Enabled) SimTrace.Write($"[move] -> {_currentState.GetType().Name}");
            _currentState.Enter(ctx, _abilities, ref _moveVars);
        }

        MovementState bestChoice = null;
        int highestPriority = int.MinValue;

        // Cross-cutting capability lock-out (combat hitstun/stun blocks Jump +
        // WallCling + LedgeGrab — see CombatState.BlockedCapabilities). Computed
        // once; candidate states declaring a blocked capability are skipped. Gates
        // entry only — the current state's continuation is governed by its
        // CheckConditions.
        var blockedCaps = ctx.Combat?.BlockedCapabilities ?? MovementCapability.None;

        // The state active at the end of last frame owns one-frame suppression rights —
        // it still points at a maneuver (e.g. LedgePull) for the frame after that maneuver
        // exits to Falling, which is exactly when a bystander would otherwise steal control.
        var owner = ctx.PreviousState(0);

        foreach (var state in _stateRegistry)
        {
            if (state == _currentState) continue;
            if ((state.RequiredCapabilities & blockedCaps) != 0) continue;
            if (owner != null && owner.Suppresses(state, ctx)) continue;

            if (state.CheckPreConditions(ctx, _abilities))
            {
                if (state.PassivePriority > highestPriority)
                {
                    highestPriority = state.PassivePriority;
                    bestChoice = state;
                }
            }
        }

        if (bestChoice != null && highestPriority > _currentState.ActivePriority)
        {
            _currentState.Exit(ctx, _abilities, ref _moveVars);
            _currentState = bestChoice;
            if (SimTrace.Enabled) SimTrace.Write($"[move] -> {_currentState.GetType().Name}");
            _currentState.Enter(ctx, _abilities, ref _moveVars);
        }

        // Action FSM selection moved BEFORE Movement.Update so the freshly-selected
        // action's modifiers are in effect when movement reads physics knobs this
        // same frame. Action.Update still runs after Movement.Update (below).
        if (!_currentAction.CheckConditions(ctx, _abilities, ref _actionVars))
        {
            _currentAction.Exit(ctx, _abilities, ref _actionVars);
            _currentAction = _actionRegistry.First(a => a is NullAction);
            if (SimTrace.Enabled) SimTrace.Write($"[action] -> {_currentAction.GetType().Name}");
            _currentAction.Enter(ctx, _abilities, ref _actionVars);
        }

        // Refresh AFTER the natural-exit handling so RecoveryAction's entry cases
        // see the true live incumbent (Null on an exit frame, not the ghost that
        // PreviousAction(0) still reports) and its current-phase vars.
        ctx.CurrentAction     = _currentAction;
        ctx.CurrentActionVars = _actionVars;

        ActionState bestAction = null;
        int bestActionPriority = int.MinValue;
        foreach (var action in _actionRegistry)
        {
            if (action == _currentAction) continue;
            if (action.CheckPreConditions(ctx, _abilities) && action.PassivePriority > bestActionPriority)
            {
                bestActionPriority = action.PassivePriority;
                bestAction = action;
            }
        }
        if (bestAction != null && bestActionPriority > _currentAction.ActivePriority)
        {
            _currentAction.Exit(ctx, _abilities, ref _actionVars);
            _currentAction = bestAction;
            if (SimTrace.Enabled) SimTrace.Write($"[action] -> {_currentAction.GetType().Name}");
            _currentAction.Enter(ctx, _abilities, ref _actionVars);
        }

        // The current action declares its modifier scalars for this frame, then
        // movement reads them through ctx.Modifiers.
        _currentAction.ApplyMovementModifiers(ref ctx.Modifiers, in _actionVars);

        // Hitstun mutes self-control AFTER action modifiers so a hit always wins
        // the stack (COMBAT_FEEL_PLAN Phase 1): knockback displaces instead of
        // being steered away or clipped by the speed caps. The residual accel is
        // the victim's DI; PreserveExternalVelocity stops AirControl / the ground
        // states from braking over-cap velocity back down.
        if (_abilities.Combat.HitstunActive && _abilities.Combat.HitstunMutesControl)
        {
            ctx.Modifiers.WalkAccel      *= CombatState.HitstunAccelScale;
            ctx.Modifiers.AirAccel       *= CombatState.HitstunAccelScale;
            ctx.Modifiers.AirDrag        *= CombatState.HitstunDragScale;
            ctx.Modifiers.GroundFriction *= CombatState.HitstunFrictionScale;
            ctx.Modifiers.PreserveExternalVelocity = true;
        }

        // The ambient corrector (Character/AmbientCorrector.cs) runs INSIDE the
        // state's Update — every state ends its Update with an ApplyAmbient call
        // (see MovementState.ApplyAmbient), so the active forces (redirect,
        // fold support) are the state's own, not a shell pass over it.
        _currentState.Update(ctx, _abilities, ref _moveVars);

        // Action gets to augment the body's force AFTER movement has written it but
        // BEFORE Action.Update — keeps Update free for FSM logic, lets the physics
        // augmentation live in its own dedicated hook. Hitstop freezes this hook (no
        // recoil/lunge assist while frozen) and the action's own Update below (no
        // hitbox progression) — see the hitstopFrozen comment above for why nothing
        // else is gated.
        if (!hitstopFrozen) _currentAction.ApplyActionForces(ctx, in _actionVars);

        // Apply gravity-scale modifier as a counter-force, identical in shape to
        // Entity.PreStep. With GravityScale = 1 this is a no-op; with 0.3 the body
        // experiences only 30% of gravity → floaty mid-air feel during charge.
        if (ctx.Modifiers.GravityScale != 1f)
            Body.AppliedForce += Gravity * (ctx.Modifiers.GravityScale - 1f);

        if (!hitstopFrozen) _currentAction.Update(ctx, _abilities, ref _actionVars);

        // Block-economy upkeep, once per frame per player and AFTER the action ran, so a
        // placement action has had its chance to request charging. Runs unconditionally —
        // the reservoir has to regenerate whether or not a build action is live.
        //
        // RMB goes in raw so the meter can retire a charge the moment the button comes
        // up, however the stroke ended — including a preempt, which never runs the paint
        // action's Exit again (see BuildMeters.Step).
        _abilities.Meters.Step(dt, ctx.Input.RightClick);

        // Attacker-side hitstop (symmetric hitlag, 2026-09-01): if this player's live
        // attack connected on the frame just resolved, freeze the attacker for the same
        // window CombatSystem granted the victim. Read AFTER Update/ApplyActionForces so
        // this frame's recoil/pogo has already been applied — the freeze then starts
        // next frame via hitstopFrozen, one frame behind the victim's, and pauses the
        // action clock (TimeInState), which also pauses the overlay clip: the crunch.
        if (CombatSystem != null)
        {
            float stop = CombatSystem.PeekHitstop(_actionVars.HitId);
            if (stop > 0f) _abilities.Combat.ApplyHitstop(_frame, stop, dt);
        }

        _historyHead = (_historyHead + 1) % HistorySize;
        _stateHistory[_historyHead] = _currentState;

        _actionHistoryHead = (_actionHistoryHead + 1) % HistorySize;
        _actionHistory[_actionHistoryHead] = _currentAction;

        // Drop consumed + aged-out intents so the buffer stays small. Pruning here
        // (rather than at the top) lets a newly-issued intent be Peeked + Consumed
        // in the same frame it was emitted.
        _intents.Prune(_frame, ctx.JumpBufferFrames);
    }

    public bool IsGrounded => _currentState is StandingState || _currentState is CrouchedState;
    public string CurrentStateName => _currentState?.GetType().Name ?? "None";
    public MovementState CurrentState => _currentState;
    public ActionState CurrentAction => _currentAction;
    // Per-activation action vars, exposed read-only so the renderer can pass them
    // into CurrentAction.Draw (which now reads its sim state from ActionVars).
    public ActionVars CurrentActionVars => _actionVars;
    public string CurrentActionName => _currentAction?.GetType().Name ?? "None";
    // Read-only exposure of intent-direction for debug overlays. Movement code reads
    // from _abilities directly; this exists so Game1 can render a facing indicator.
    public int Facing => _abilities.Facing;
    // Defensive combat state — exposed for tests / HUD / debug overlays that
    // want to read HitstunActive, StunActive, LastHitImpulse, etc.
    public CombatState Combat => _abilities.Combat;

    // ── Snapshot/restore (Plans/ECS_MIGRATION_PLAN.md, Phases 5-6) ───────────────
    // Serializable player state lives in the World's PlayerData + BodyStateComp stores
    // keyed by this player's Id; these methods sync it to/from the live object at
    // snapshot boundaries. The two FSMs become registry indices; per-activation data is
    // the value-struct blobs; helper objects deep-copy their state. Render-only fields
    // (Sprite) are excluded. The Controller is captured at the sim level, not here.
    public void CaptureState(World world)
    {
        ref var d = ref world.Get<PlayerData>(Id);
        d.Health             = Health;
        d.HitInvulnRemaining = _hitInvulnRemaining;
        d.LastCrushFrame     = _lastCrushFrame;
        d.LastDamageFrame    = _lastDamageFrame;
        d.Frame              = _frame;
        d.StateIndex         = _stateRegistry.IndexOf(_currentState);
        d.ActionIndex        = _actionRegistry.IndexOf(_currentAction);
        d.StateHistory       = MapStateRing(_stateHistory);
        d.ActionHistory      = MapActionRing(_actionHistory);
        d.HistoryHead        = _historyHead;
        d.ActionHistoryHead  = _actionHistoryHead;
        d.MoveVars           = _moveVars;
        d.ActionVars         = _actionVars;
        d.Abilities          = _abilities.Clone();
        d.Parser             = _inputParser.Capture();
        d.Intents            = _intents.Capture();
        d.ActiveBlockType    = _activeBlockType;
        world.Get<BodyStateComp>(Id).State = BodyState.Capture(Body);
    }

    public void RestoreState(World world)
    {
        var s = world.Get<PlayerData>(Id);
        Health              = s.Health;
        _hitInvulnRemaining = s.HitInvulnRemaining;
        _lastCrushFrame     = s.LastCrushFrame;
        _lastDamageFrame    = s.LastDamageFrame;
        _frame              = s.Frame;

        _currentState  = _stateRegistry[s.StateIndex];
        _currentAction = _actionRegistry[s.ActionIndex];
        UnmapStateRing(s.StateHistory, _stateHistory);
        UnmapActionRing(s.ActionHistory, _actionHistory);
        _historyHead       = s.HistoryHead;
        _actionHistoryHead = s.ActionHistoryHead;

        _moveVars   = s.MoveVars;
        _actionVars = s.ActionVars;
        _abilities.CopyFrom(s.Abilities);
        _inputParser.Restore(s.Parser);
        _intents.Restore(s.Intents);

        _activeBlockType = s.ActiveBlockType;

        world.Get<BodyStateComp>(Id).State.RestoreInto(Body);

        // The restored body keeps only its Maintained (hard) contacts; the soft
        // contacts are gone, so every movement state's transient contact-ref cache is
        // now stale. Null them all so the active state's idempotent Ensure… rebuilds
        // its contact next Update from the restored pose (see ResetTransient).
        foreach (var st in _stateRegistry) st.ResetTransient();
    }

    private int[] MapStateRing(MovementState[] ring)
    {
        var idx = new int[ring.Length];
        for (int i = 0; i < ring.Length; i++) idx[i] = ring[i] == null ? -1 : _stateRegistry.IndexOf(ring[i]);
        return idx;
    }

    private int[] MapActionRing(ActionState[] ring)
    {
        var idx = new int[ring.Length];
        for (int i = 0; i < ring.Length; i++) idx[i] = ring[i] == null ? -1 : _actionRegistry.IndexOf(ring[i]);
        return idx;
    }

    private void UnmapStateRing(int[] idx, MovementState[] ring)
    {
        for (int i = 0; i < ring.Length; i++) ring[i] = (idx == null || idx[i] < 0) ? null : _stateRegistry[idx[i]];
    }

    private void UnmapActionRing(int[] idx, ActionState[] ring)
    {
        for (int i = 0; i < ring.Length; i++) ring[i] = (idx == null || idx[i] < 0) ? null : _actionRegistry[idx[i]];
    }
}
