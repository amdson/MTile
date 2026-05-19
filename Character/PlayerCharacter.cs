using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace MTile;

public class PlayerCharacter : IHittable
{
    public const float Radius = 9.5f;

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
    public Faction Faction { get; set; } = MTile.Faction.Player;

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
    // Threshold sized so the player's own jumps don't self-damage:
    //   * Held single jump lands at vy ≈ 260-270 (measured)
    //   * Held running jump (RunJumpVelocity -120) lands ~290
    //   * Held jump + double jump compounds to ~340-370 in worst case
    // 400 puts all self-jumps comfortably free while keeping plunges from
    // height (≥ 9-10 tiles) painful. Was 200 — caught every normal jump
    // landing, dealing ~0.21 HP per jump and triggering hitstun that blocked
    // the next jump (the "fails to jump occasionally" + "damage every jump"
    // pair the bug report linked).
    private const float CrushImpulseThreshold = 400f;
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
        world.Publish(new Hurtbox(Body.Bounds, Faction, this));
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

        // Register the hit for hitstun (every hit) and stash impulse/direction
        // for the stun-threshold check. Uses the raw KnockbackImpulse magnitude —
        // independent of player Mass so the attack's "strength" reads consistently
        // across players of different mass.
        var dir = hit.KnockbackImpulse;
        if (dir.LengthSquared() > 1e-4f) dir.Normalize();
        _abilities.Combat.OnHitRegistered(_frame, hit.KnockbackImpulse.Length(), dir);
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

    private readonly List<MovementState> _stateRegistry = new();

    private const int HistorySize = 32;
    private readonly MovementState[] _stateHistory = new MovementState[HistorySize];
    private int _historyHead = 0;
    private readonly Func<int, MovementState> _getState;

    private readonly List<ActionState> _actionRegistry = new();
    private ActionState _currentAction;
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
    // Written by Game1 each tick *before* Update — records the frame on which
    // Game1.HandleBuildInput last actually placed a tile. Plumbed into
    // EnvironmentContext so BlockReadyAction can cancel an in-flight charge while
    // the player is actively building. Default is a far-past value so a fresh
    // PlayerCharacter (tests, secondary players) reads as "never built."
    public int LastTilePlacedFrame { get; set; } = int.MinValue / 2;

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
        Body.Impact = new ImpactDamage {
            Mass                 = 2.5f,
            ImpulseThreshold     = 700f,
            DamagePerUnitImpulse = 0.04f,
        };
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
        _actionRegistry.Add(new BlockReadyAction());     // 8/10   — RMB hold-in-solid charge
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
            _abilities.Combat.OnHitRegistered(_frame, Body.LastImpulseMagnitude, Vector2.Zero);
            _lastCrushFrame = _frame;
        }

        var input = controller.Current;
        var prev  = controller.GetPrevious(1);
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
            Intents        = _intents,
            Condition      = _abilities.Condition,
            Combat         = _abilities.Combat,
            CurrentFrame   = _frame,
            LastTilePlacedFrame = LastTilePlacedFrame,
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

        if (IsGrounded || ctx.TryGetWall(1, out _) || ctx.TryGetWall(-1, out _))
        {
            _abilities.HasDoubleJumped = false;
        }

        if (!_currentState.CheckConditions(ctx, _abilities))
        {
            _currentState.Exit(ctx, _abilities);
            _currentState = _stateRegistry.First(s => s is FallingState);
            _currentState.Enter(ctx, _abilities);
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
            _currentState.Exit(ctx, _abilities);
            _currentState = bestChoice;
            _currentState.Enter(ctx, _abilities);
        }

        // Action FSM selection moved BEFORE Movement.Update so the freshly-selected
        // action's modifiers are in effect when movement reads physics knobs this
        // same frame. Action.Update still runs after Movement.Update (below).
        if (!_currentAction.CheckConditions(ctx, _abilities))
        {
            _currentAction.Exit(ctx, _abilities);
            _currentAction = _actionRegistry.First(a => a is NullAction);
            _currentAction.Enter(ctx, _abilities);
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
            _currentAction.Exit(ctx, _abilities);
            _currentAction = bestAction;
            _currentAction.Enter(ctx, _abilities);
        }

        // The current action declares its modifier scalars for this frame, then
        // movement reads them through ctx.Modifiers.
        _currentAction.ApplyMovementModifiers(ref ctx.Modifiers);

        _currentState.Update(ctx, _abilities);

        // Action gets to augment the body's force AFTER movement has written it but
        // BEFORE Action.Update — keeps Update free for FSM logic, lets the physics
        // augmentation live in its own dedicated hook.
        _currentAction.ApplyActionForces(ctx);

        // Apply gravity-scale modifier as a counter-force, identical in shape to
        // Entity.PreStep. With GravityScale = 1 this is a no-op; with 0.3 the body
        // experiences only 30% of gravity → floaty mid-air feel during charge.
        if (ctx.Modifiers.GravityScale != 1f)
            Body.AppliedForce += Gravity * (ctx.Modifiers.GravityScale - 1f);

        _currentAction.Update(ctx, _abilities);

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
    public string CurrentActionName => _currentAction?.GetType().Name ?? "None";
    // Read-only exposure of intent-direction for debug overlays. Movement code reads
    // from _abilities directly; this exists so Game1 can render a facing indicator.
    public int Facing => _abilities.Facing;
    // Defensive combat state — exposed for tests / HUD / debug overlays that
    // want to read HitstunActive, StunActive, LastHitImpulse, etc.
    public CombatState Combat => _abilities.Combat;
}
