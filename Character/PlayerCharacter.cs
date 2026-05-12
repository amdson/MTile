using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace MTile;

public class PlayerCharacter
{
    public const float Radius = 9.5f;

    public readonly PhysicsBody Body;
    
    private readonly PlayerAbilityState _abilities = new();
    private MovementState _currentState;

    private readonly List<MovementState> _stateRegistry = new();

    private const int HistorySize = 32;
    private readonly MovementState[] _stateHistory = new MovementState[HistorySize];
    private int _historyHead = 0;
    private readonly Func<int, MovementState> _getState;

    public MovementState GetPreviousState(int framesBack)
    {
        if ((uint)framesBack >= HistorySize) return null;
        return _stateHistory[(_historyHead - framesBack + HistorySize) % HistorySize];
    }

    public PlayerCharacter(Vector2 startPosition)
    {
        Body = new PhysicsBody(Polygon.CreateRegular(Radius, 6), startPosition);
        _getState = GetPreviousState;
        
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
        _stateRegistry.Add(new CeilingJumpState());
        _stateRegistry.Add(new ParkourState(1));
        _stateRegistry.Add(new ParkourState(-1));
        _stateRegistry.Add(new LedgeGrabState(1));
        _stateRegistry.Add(new LedgeGrabState(-1));
        _stateRegistry.Add(new LedgePullState(1));
        _stateRegistry.Add(new LedgePullState(-1));
        
        _currentState = _stateRegistry[0]; // falling
    }

    public void Update(Controller controller, ChunkMap chunks, float dt)
    {
        var input = controller.Current;
        var prev  = controller.GetPrevious(1);
        _abilities.JumpJustPressed  = input.Space && !prev.Space;
        _abilities.UpJustPressed    = input.Up    && !prev.Up;
        _abilities.DownJustPressed  = input.Down  && !prev.Down;

        var ctx = new EnvironmentContext
        {
            Input      = input,
            Controller = controller,
            PreviousState = _getState,
            Chunks     = chunks,
            Dt         = dt,
            Body       = Body,
            Intent     = InputIntent.From(controller),
        };

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

        _currentState.Update(ctx, _abilities);

        _historyHead = (_historyHead + 1) % HistorySize;
        _stateHistory[_historyHead] = _currentState;
    }

    public bool IsGrounded => _currentState is StandingState || _currentState is CrouchedState;
    public string CurrentStateName => _currentState?.GetType().Name ?? "None";
    public MovementState CurrentState => _currentState;
}
