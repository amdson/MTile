using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace MTile;

public class PlayerCharacter
{
    public const float Radius = 12f;

    public readonly PhysicsBody Body;
    
    private readonly PlayerAbilityState _abilities = new();
    private MovementState _currentState;

    private readonly List<MovementState> _stateRegistry = new();

    public PlayerCharacter(Vector2 startPosition)
    {
        Body = new PhysicsBody(Polygon.CreateRegular(Radius, 6), startPosition);
        
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
        
        _currentState = _stateRegistry[0]; // falling
    }

    private PlayerInput _lastInput;

    public void Update(PlayerInput input, ChunkMap chunks, float dt)
    {
        bool jumpJustPressed = (input.Space && !_lastInput.Space) || (input.Up && !_lastInput.Up);
        _abilities.JumpJustPressed = jumpJustPressed;
        _lastInput = input;

        var ctx = new EnvironmentContext
        {
            Input = input,
            Chunks = chunks,
            Dt = dt,
            Body = Body
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
    }

    public bool IsGrounded => _currentState is StandingState || _currentState is CrouchedState;
}
