using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace MTile;

public class PlayerCharacter : IHittable
{
    public const float Radius = 9.5f;

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

    // Combat stats. MaxHealth tuned so a Stalker takes ~4 lunges to down the
    // player; Mass divides incoming knockback impulses (heavier = less yeet).
    public float   MaxHealth = 3f;
    public float   Health;
    public float   Mass      = 2.5f;
    // Brief invuln after taking a hit so a multi-frame enemy hitbox (or two
    // overlapping enemies) doesn't shred the player in a single frame. The
    // (HitId, Target) dedupe in CombatSystem already handles single-attack
    // multi-frame, so this is mostly belt-and-suspenders for stacked attackers.
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
    private const float CrushImpulseThreshold = 700f;
    private const float CrushDamagePerImpulse = 0.003f;
    private const int   CrushCooldownFrames   = 6;
    private int _lastCrushFrame = int.MinValue / 2;
    public bool IsAlive => Health > 0f;

    // Player is one big hurtbox covering its body bounds. Future: split into head/body
    // for headshots, or shrink during dodge frames. Suppressed during invuln so
    // CombatSystem doesn't even consider hits during the recovery window.
    public void PublishHurtboxes(HurtboxWorld world)
    {
        if (_hitInvulnRemaining > 0f) return;
        world.Publish(new Hurtbox(Body.Bounds, Faction, Id));
    }

    public void OnHit(in Hitbox hit, in Hurtbox myHurtbox)
    {
        // Guard parry — roadmap §1.5. If GuardActive and the hit lands in the
        // front-cone, absorb completely: no damage, no knockback, no hitstun.
        // Weak in-cone hits additionally charge GuardRetaliate (see CombatState.TryParry).
        if (_abilities.Combat.TryParry(hit.KnockbackImpulse, hit.Damage, _abilities.Facing, _frame))
            return;

        Health -= hit.Damage;
        if (Mass > 0f) Body.Velocity += hit.KnockbackImpulse / Mass;
        _hitInvulnRemaining = HitInvulnDuration;

        // Register the hit for hitstun (every hit) + the stun-threshold check.
        // Uses raw KnockbackImpulse magnitude — independent of player Mass so the
        // attack's "strength" reads consistently across players of different mass.
        _abilities.Combat.OnHitRegistered(_frame, hit.KnockbackImpulse.Length());
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
    }
    
    private readonly PlayerAbilityState _abilities = new();
    private MovementState _currentState;
    // Plain-data per-activation state for the current movement state (see MovementVars).
    // Lives here (not on the flyweight state instances) so it's a single snapshot unit.
    private MovementVars _moveVars;

    private readonly List<MovementState> _stateRegistry = new();

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

    // Player-local block/eruption selection, driven by this player's own input
    // (1-4 keys → block type; P → planner mode). Formerly global planner statics.
    // Read by the eruption actions via EnvironmentContext, and by Simulation's
    // drag-build + the HUD. Defaults mirror the old static defaults.
    private TileType            _activeBlockType = TileType.Dirt;
    private EruptionPlannerMode _eruptionMode    = EruptionPlannerMode.MassBall;
    private bool                _wasPDown;
    // Settable so Simulation can seed the initial selection from GameConfig;
    // thereafter it's driven by this player's own input each frame.
    public TileType            ActiveBlockType { get => _activeBlockType; set => _activeBlockType = value; }
    public EruptionPlannerMode EruptionMode    => _eruptionMode;

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
        Body = new PhysicsBody(Polygon.CreateRegular(Radius, 6), startPosition);
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
        _actionRegistry.Add(new ReadyAction());       // 10/15  — wind-up on LMB press
        _actionRegistry.Add(new RecoveryAction());    // 40/45  — post-attack lockout
        _actionRegistry.Add(new GroundSlash1());      // 30/30
        _actionRegistry.Add(new CrouchSlash());       // 30/32  — crouch-only, no combo
        _actionRegistry.Add(new AirSlash1());         // 30/30
        _actionRegistry.Add(new StabAction());        // 30/30
        _actionRegistry.Add(new PulseAction());       // 30/30  — Circle gesture
        _actionRegistry.Add(new BlockReadyAction());     // 8/10   — RMB drag-build + hold-in-solid charge
        _actionRegistry.Add(new BlockEruptionAction());  // 9/10   — RMB hold-out-of-solid after arming, fires on release
        _actionRegistry.Add(new GroundSlash2());      // 30/50  — combo (Slash2Ready gated)
        _actionRegistry.Add(new GroundSlash3());      // 30/50  — combo
        _actionRegistry.Add(new AirSlash2());         // 30/50  — combo
        _actionRegistry.Add(new AirTurnSlash());      // 30/35  — air backward-click turnaround
        _actionRegistry.Add(new AirSpinStab());       // 30/35  — air backward-swipe stab
        _actionRegistry.Add(new GuardAction());       // 35/40  — Shift held, no L/R, parry posture
        _actionRegistry.Add(new GuardRetaliateAction()); // 30/55 — click during GuardCharged
        _actionRegistry.Add(new EnergyBallAction());     // 40/45 — Shift+LMB tap, preempts Guard briefly
        _actionRegistry.Add(new BeamAction());           // 40/45 — Shift+LMB hold, sustained beam after charge
        _actionRegistry.Add(new GrenadeAction());        // 40/45 — F press, throws sticky grenade
        _actionRegistry.Add(new LobbedAreaAction());     // 40/45 — Shift+RMB charge, ranged eruption on landing
        _currentAction = _actionRegistry[0];

        _stateRegistry.Add(new FallingState());
        _stateRegistry.Add(new StunnedState());
        _stateRegistry.Add(new StandingState());
        _stateRegistry.Add(new CrouchedState());
        _stateRegistry.Add(new JumpingState());
        _stateRegistry.Add(new RunningJumpState());
        _stateRegistry.Add(new DoubleJumpingState());
        _stateRegistry.Add(new WallSlidingState(1));
        _stateRegistry.Add(new WallSlidingState(-1));
        _stateRegistry.Add(new WallJumpingState(1));
        _stateRegistry.Add(new WallJumpingState(-1));
        _stateRegistry.Add(new CoveredJumpState());
        _stateRegistry.Add(new ParkourState(1));
        _stateRegistry.Add(new ParkourState(-1));
        _stateRegistry.Add(new DropdownState());
        _stateRegistry.Add(new LedgeGrabState(1));
        _stateRegistry.Add(new LedgeGrabState(-1));
        _stateRegistry.Add(new LedgePullState(1));
        _stateRegistry.Add(new LedgePullState(-1));
        
        _currentState = _stateRegistry[0]; // falling
    }

    public void Update(Controller controller, ChunkMap chunks, HitboxWorld hitboxes, HurtboxWorld hurtboxes, float dt, IEntitySpawner spawner = null)
    {
        _frame++;
        if (_hitInvulnRemaining > 0f) _hitInvulnRemaining -= dt;

        // Crush damage: turn the previous step's largest |vnRel| into HP loss
        // when it crosses CrushImpulseThreshold. Reads PhysicsBody.LastImpulse
        // Magnitude (written by PhysicsWorld.StepSwept). Routes through
        // OnHitRegistered so a hard fall / wall slam also lights up Hitstun and
        // (if hard enough) Stun — "I just slammed down, give me a sec."
        if (Body.LastImpulseMagnitude > CrushImpulseThreshold
            && _frame - _lastCrushFrame >= CrushCooldownFrames)
        {
            float excess = Body.LastImpulseMagnitude - CrushImpulseThreshold;
            Health -= excess * CrushDamagePerImpulse;
            _abilities.Combat.OnHitRegistered(_frame, Body.LastImpulseMagnitude);
            _lastCrushFrame = _frame;
        }

        var input = controller.Current;
        var prev  = controller.GetPrevious(1);

        // Block-picker + planner-mode selection from this player's own input.
        // Number keys are level-triggered (re-assign harmlessly); P is edge-detected
        // so a held key toggles once. Formerly interpreted in Game1/Simulation against
        // global planner statics — now player-local and rollback-deterministic.
        if (input.Num1) _activeBlockType = TileType.Stone;
        if (input.Num2) _activeBlockType = TileType.Dirt;
        if (input.Num3) _activeBlockType = TileType.Sand;
        if (input.Num4) _activeBlockType = TileType.Foam;
        if (input.P && !_wasPDown)
            _eruptionMode = _eruptionMode == EruptionPlannerMode.PriorityField
                ? EruptionPlannerMode.MassBall
                : EruptionPlannerMode.PriorityField;
        _wasPDown = input.P;

        _abilities.JumpJustPressed  = input.Space && !prev.Space;
        _abilities.UpJustPressed    = input.Up    && !prev.Up;
        _abilities.DownJustPressed  = input.Down  && !prev.Down;

        // Expire combo / recovery flags whose window closed since last frame.
        _abilities.Condition.Tick(_frame);
        // Expire hitstun / stun whose window closed.
        _abilities.Combat.Tick(_frame);

        // Edge-detect input gestures and enqueue intents. Done BEFORE the FSMs so
        // freshly-released clicks are visible to action preconditions this frame.
        _inputParser.Detect(controller, _intents, _frame);

        var ctx = new EnvironmentContext
        {
            Input          = input,
            Controller     = controller,
            PreviousState  = _getState,
            PreviousAction = _getAction,
            Chunks         = chunks,
            Hitboxes       = hitboxes,
            Hurtboxes      = hurtboxes,
            Spawner        = spawner,
            Faction        = Faction,
            SelfId         = Id,
            HitIds         = HitIds,
            CombatSystem   = CombatSystem,
            EruptionMode   = _eruptionMode,
            ActiveBlockType = _activeBlockType,
            Intents        = _intents,
            Condition      = _abilities.Condition,
            Combat         = _abilities.Combat,
            CurrentFrame   = _frame,
            Dt             = dt,
            Body           = Body,
            Intent         = InputIntent.From(controller),
            Modifiers      = MovementModifiers.Identity,
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
            _currentState.Enter(ctx, _abilities, ref _moveVars);
        }

        MovementState bestChoice = null;
        int highestPriority = int.MinValue;

        foreach (var state in _stateRegistry)
        {
            if (state == _currentState) continue;

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
            _currentState.Enter(ctx, _abilities, ref _moveVars);
        }

        // Action FSM selection moved BEFORE Movement.Update so the freshly-selected
        // action's modifiers are in effect when movement reads physics knobs this
        // same frame. Action.Update still runs after Movement.Update (below).
        if (!_currentAction.CheckConditions(ctx, _abilities, ref _actionVars))
        {
            _currentAction.Exit(ctx, _abilities, ref _actionVars);
            _currentAction = _actionRegistry.First(a => a is NullAction);
            _currentAction.Enter(ctx, _abilities, ref _actionVars);
        }

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
            _currentAction.Enter(ctx, _abilities, ref _actionVars);
        }

        // The current action declares its modifier scalars for this frame, then
        // movement reads them through ctx.Modifiers.
        _currentAction.ApplyMovementModifiers(ref ctx.Modifiers, in _actionVars);

        _currentState.Update(ctx, _abilities, ref _moveVars);

        // Action gets to augment the body's force AFTER movement has written it but
        // BEFORE Action.Update — keeps Update free for FSM logic, lets the physics
        // augmentation live in its own dedicated hook.
        _currentAction.ApplyActionForces(ctx, in _actionVars);

        // Apply gravity-scale modifier as a counter-force, identical in shape to
        // Entity.PreStep. With GravityScale = 1 this is a no-op; with 0.3 the body
        // experiences only 30% of gravity → floaty mid-air feel during charge.
        if (ctx.Modifiers.GravityScale != 1f)
            Body.AppliedForce += Gravity * (ctx.Modifiers.GravityScale - 1f);

        _currentAction.Update(ctx, _abilities, ref _actionVars);

        _historyHead = (_historyHead + 1) % HistorySize;
        _stateHistory[_historyHead] = _currentState;

        _actionHistoryHead = (_actionHistoryHead + 1) % HistorySize;
        _actionHistory[_actionHistoryHead] = _currentAction;

        // Drop consumed + aged-out intents so the buffer stays small. Pruning here
        // (rather than at the top) lets a newly-issued intent be Peeked + Consumed
        // in the same frame it was emitted.
        _intents.Prune(_frame);
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

    // ── Snapshot/restore (roadmap goal 4 §A) ────────────────────────────────────
    // Capture the full per-player simulation state into a flat PlayerSnapshot. The
    // two FSMs become registry indices; the per-activation data is the value-struct
    // blobs; helper objects deep-copy their state. Render-only fields (Sprite) are
    // excluded. The Controller is captured at the sim level, not here.
    public PlayerSnapshot CaptureState()
    {
        return new PlayerSnapshot
        {
            Id                  = Id,
            Body                = BodyState.Capture(Body),
            Health              = Health,
            HitInvulnRemaining  = _hitInvulnRemaining,
            LastCrushFrame      = _lastCrushFrame,
            Frame               = _frame,
            StateIndex          = _stateRegistry.IndexOf(_currentState),
            ActionIndex         = _actionRegistry.IndexOf(_currentAction),
            StateHistory        = MapStateRing(_stateHistory),
            ActionHistory       = MapActionRing(_actionHistory),
            HistoryHead         = _historyHead,
            ActionHistoryHead   = _actionHistoryHead,
            MoveVars            = _moveVars,
            ActionVars          = _actionVars,
            Abilities           = _abilities.Clone(),
            Parser              = _inputParser.Capture(),
            Intents             = _intents.Capture(),
            Eruption            = EruptionAction.CaptureGesture(),
            ActiveBlockType     = _activeBlockType,
            EruptionMode        = _eruptionMode,
            WasPDown            = _wasPDown,
        };
    }

    public void RestoreState(in PlayerSnapshot s)
    {
        Id                  = s.Id;
        s.Body.RestoreInto(Body);
        Health              = s.Health;
        _hitInvulnRemaining = s.HitInvulnRemaining;
        _lastCrushFrame     = s.LastCrushFrame;
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
        EruptionAction.RestoreGesture(s.Eruption);

        _activeBlockType = s.ActiveBlockType;
        _eruptionMode    = s.EruptionMode;
        _wasPDown        = s.WasPDown;

        // The restored body keeps only its Maintained (hard) contacts; the soft
        // contacts are gone, so every movement state's transient contact-ref cache is
        // now stale. Null them all so the active state's idempotent Ensure… rebuilds
        // its contact next Update from the restored pose (see ResetTransient).
        foreach (var st in _stateRegistry) st.ResetTransient();
    }

    // The BlockEruptionAction flyweight in this player's registry — owner of the one
    // reference-type per-activation buffer (_pen/_samples) that needs a deep copy.
    private BlockEruptionAction EruptionAction
        => _actionRegistry.OfType<BlockEruptionAction>().First();

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
