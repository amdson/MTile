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
        _stateRegistry.Add(new JumpingState());
        _stateRegistry.Add(new WallSlidingState(1));
        _stateRegistry.Add(new WallSlidingState(-1));
        _stateRegistry.Add(new WallJumpingState(1));
        _stateRegistry.Add(new WallJumpingState(-1));
        
        _currentState = _stateRegistry[0]; // falling
    }

    public void Update(PlayerInput input, ChunkMap chunks, float dt)
    {
        var ctx = new EnvironmentContext
        {
            Input = input,
            Chunks = chunks,
            Dt = dt,
            Body = Body
        };

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

    public bool IsGrounded => _currentState is StandingState;
}
