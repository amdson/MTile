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

    public Faction Faction => MTile.Faction.Player;

    // Player is one big hurtbox covering its body bounds. Future: split into head/body
    // for headshots, or shrink during dodge frames.
    public void PublishHurtboxes(HurtboxWorld world)
        => world.Publish(new Hurtbox(Body.Bounds, MTile.Faction.Player, this));

    // V1 stub. HP, stun, knockback live here once the player can actually take damage.
    public void OnHit(in Hitbox hit, in Hurtbox myHurtbox) { }
    
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
        Sprite = Sprites.Player(Radius);
        _getState  = GetPreviousState;
        _getAction = GetPreviousAction;

        // Order in the registry only matters as a tiebreaker between equal-passive
        // candidates; preconditions + ConditionState gates do the real selection work.
        // Listed roughly low-to-high priority for readability.
        _actionRegistry.Add(new NullAction());        // 0/0
        _actionRegistry.Add(new ReadyAction());       // 10/15  — wind-up on LMB press
        _actionRegistry.Add(new RecoveryAction());    // 40/45  — post-attack lockout
        _actionRegistry.Add(new GroundSlash1());      // 30/30
        _actionRegistry.Add(new AirSlash1());         // 30/30
        _actionRegistry.Add(new StabAction());        // 30/30
        _actionRegistry.Add(new PulseAction());       // 30/30  — Circle gesture
        _actionRegistry.Add(new BlockReadyAction());  // 8/10   — RMB in solid (below ReadyAction)
        _actionRegistry.Add(new BlockEruptionAction()); // 9/10 — armed handoff from BlockReady
        _actionRegistry.Add(new GroundSlash2());      // 30/50  — combo (Slash2Ready gated)
        _actionRegistry.Add(new GroundSlash3());      // 30/50  — combo
        _actionRegistry.Add(new AirSlash2());         // 30/50  — combo
        _currentAction = _actionRegistry[0];

        _stateRegistry.Add(new FallingState());
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

    public void Update(Controller controller, ChunkMap chunks, HitboxWorld hitboxes, HurtboxWorld hurtboxes, float dt)
    {
        _frame++;

        var input = controller.Current;
        var prev  = controller.GetPrevious(1);
        _abilities.JumpJustPressed  = input.Space && !prev.Space;
        _abilities.UpJustPressed    = input.Up    && !prev.Up;
        _abilities.DownJustPressed  = input.Down  && !prev.Down;

        // Expire combo / recovery flags whose window closed since last frame.
        _abilities.Condition.Tick(_frame);

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
            Intents        = _intents,
            Condition      = _abilities.Condition,
            CurrentFrame   = _frame,
            Dt             = dt,
            Body           = Body,
            Intent         = InputIntent.From(controller),
            Modifiers      = MovementModifiers.Identity,
        };

        // Facing tracks the last non-zero horizontal input so standstill actions
        // (slash from a stop) still have a direction. Movement code doesn't read this.
        if (ctx.Intent.CurrentHorizontal != 0) _abilities.Facing = ctx.Intent.CurrentHorizontal;

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
}
