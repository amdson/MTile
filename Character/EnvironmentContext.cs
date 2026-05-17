using System;
using Microsoft.Xna.Framework;

namespace MTile;

public class EnvironmentContext
{
    public PlayerInput Input;
    public Controller Controller;
    public Func<int, MovementState> PreviousState;
    public Func<int, ActionState>   PreviousAction;
    public ChunkMap Chunks;
    public HitboxWorld  Hitboxes;     // offensive — publishers (action FSM, AI) push hitboxes here
    public HurtboxWorld Hurtboxes;    // defensive — read-only for actions, populated each frame by IHittable.PublishHurtboxes
    public IntentBuffer Intents;      // gesture-parsed action intents (Click, Stab, PressEdge); action FSM reads + consumes
    public ConditionState Condition;  // combat condition flags (Slash2Ready, RecoveryActive, …) — lives on PlayerAbilityState
    public int   CurrentFrame;        // monotonic frame counter for intent age + flag expiry
    public float Dt;
    public PhysicsBody Body;
    public InputIntent Intent;
    // Multiplicative scalars on movement knobs (WalkAccel, MaxAirSpeed, GroundFriction, …).
    // Reset to Identity each frame in PlayerCharacter.Update, populated by the current
    // action's ApplyMovementModifiers, then read by movement states at their config sites.
    public MovementModifiers Modifiers;

    private bool _groundSearched;
    private bool _hasGround;
    private FloatingSurfaceDistance _groundContact;

    private bool _crouchGroundSearched;
    private bool _hasCrouchGround;
    private FloatingSurfaceDistance _crouchGroundContact;

    private bool _wallSearched1;
    private bool _hasWall1;
    private FloatingSurfaceDistance _wallContact1;

    private bool _wallSearchedMinus1;
    private bool _hasWallMinus1;
    private FloatingSurfaceDistance _wallContactMinus1;

    private bool _cornerSearched1;
    private bool _hasCorner1;
    private ExposedCorner _corner1;

    private bool _cornerSearchedMinus1;
    private bool _hasCornerMinus1;
    private ExposedCorner _cornerMinus1;

    private bool _lowerCornerSearched1;
    private bool _hasLowerCorner1;
    private ExposedLowerCorner _lowerCorner1;

    private bool _lowerCornerSearchedMinus1;
    private bool _hasLowerCornerMinus1;
    private ExposedLowerCorner _lowerCornerMinus1;

    private bool _ledgeCornerSearched1;
    private bool _hasLedgeCorner1;
    private ExposedCorner _ledgeCorner1;

    private bool _ledgeCornerSearchedMinus1;
    private bool _hasLedgeCornerMinus1;
    private ExposedCorner _ledgeCornerMinus1;

    private bool _ceilingSearched;
    private bool _hasCeiling;
    private FloatingSurfaceDistance _ceilingContact;

    public bool TryGetGround(out FloatingSurfaceDistance ground) =>
        TryGetGroundAt(PlayerCharacter.Radius, ref _groundSearched, ref _hasGround, ref _groundContact, out ground);

    public bool TryGetCrouchGround(out FloatingSurfaceDistance ground) =>
        TryGetGroundAt(0f, ref _crouchGroundSearched, ref _hasCrouchGround, ref _crouchGroundContact, out ground);

    private bool TryGetGroundAt(float floatHeight, ref bool searched, ref bool has, ref FloatingSurfaceDistance contact, out FloatingSurfaceDistance ground)
    {
        if (!searched)
        {
            has = GroundChecker.TryFind(Body, Chunks, PlayerCharacter.Radius, floatHeight, out contact);
            searched = true;
        }
        ground = contact;
        return has;
    }

    public bool TryGetWall(int dir, out FloatingSurfaceDistance wall)
    {
        if (dir == 1)
        {
            if (!_wallSearched1)
            {
                _hasWall1 = WallChecker.TryFind(Body, Chunks, PlayerCharacter.Radius, 0f, 1, out _wallContact1);
                _wallSearched1 = true;
            }
            wall = _wallContact1;
            return _hasWall1;
        }
        else if (dir == -1)
        {
            if (!_wallSearchedMinus1)
            {
                _hasWallMinus1 = WallChecker.TryFind(Body, Chunks, PlayerCharacter.Radius, 0f, -1, out _wallContactMinus1);
                _wallSearchedMinus1 = true;
            }
            wall = _wallContactMinus1;
            return _hasWallMinus1;
        }
        
        wall = null;
        return false;
    }

    public bool TryGetExposedCorner(int dir, out ExposedCorner corner)
    {
        if (dir == 1)
        {
            if (!_cornerSearched1)
            {
                _hasCorner1 = ExposedUpperCornerChecker.TryFind(Body, Chunks, 1, out _corner1);
                _cornerSearched1 = true;
            }
            corner = _corner1;
            return _hasCorner1;
        }
        else if (dir == -1)
        {
            if (!_cornerSearchedMinus1)
            {
                _hasCornerMinus1 = ExposedUpperCornerChecker.TryFind(Body, Chunks, -1, out _cornerMinus1);
                _cornerSearchedMinus1 = true;
            }
            corner = _cornerMinus1;
            return _hasCornerMinus1;
        }
        corner = default;
        return false;
    }
    
    public bool TryGetLedgeCorner(int dir, out ExposedCorner corner)
    {
        if (dir == 1)
        {
            if (!_ledgeCornerSearched1)
            {
                _hasLedgeCorner1 = ExposedUpperCornerChecker.TryFindAboveHead(Body, Chunks, 1, out _ledgeCorner1);
                _ledgeCornerSearched1 = true;
            }
            corner = _ledgeCorner1;
            return _hasLedgeCorner1;
        }
        else if (dir == -1)
        {
            if (!_ledgeCornerSearchedMinus1)
            {
                _hasLedgeCornerMinus1 = ExposedUpperCornerChecker.TryFindAboveHead(Body, Chunks, -1, out _ledgeCornerMinus1);
                _ledgeCornerSearchedMinus1 = true;
            }
            corner = _ledgeCornerMinus1;
            return _hasLedgeCornerMinus1;
        }
        corner = default;
        return false;
    }

    public bool TryGetCeiling(out FloatingSurfaceDistance ceiling)
    {
        if (!_ceilingSearched)
        {
            _hasCeiling = CeilingChecker.TryFind(Body, Chunks, out _ceilingContact);
            _ceilingSearched = true;
        }
        ceiling = _ceilingContact;
        return _hasCeiling;
    }

    public bool TryGetExposedLowerCorner(int dir, out ExposedLowerCorner corner)
    {
        if (dir == 1)
        {
            if (!_lowerCornerSearched1)
            {
                _hasLowerCorner1 = ExposedLowerCornerChecker.TryFind(Body, Chunks, 1, out _lowerCorner1);
                _lowerCornerSearched1 = true;
            }
            corner = _lowerCorner1;
            return _hasLowerCorner1;
        }
        else if (dir == -1)
        {
            if (!_lowerCornerSearchedMinus1)
            {
                _hasLowerCornerMinus1 = ExposedLowerCornerChecker.TryFind(Body, Chunks, -1, out _lowerCornerMinus1);
                _lowerCornerSearchedMinus1 = true;
            }
            corner = _lowerCornerMinus1;
            return _hasLowerCornerMinus1;
        }
        corner = default;
        return false;
    }
}
