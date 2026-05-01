using Microsoft.Xna.Framework;

namespace MTile;

public class EnvironmentContext
{
    public PlayerInput Input;
    public ChunkMap Chunks;
    public float Dt;
    public PhysicsBody Body;

    private bool _groundSearched;
    private bool _hasGround;
    private FloatingSurfaceDistance _groundContact;

    private bool _wallSearched1;
    private bool _hasWall1;
    private FloatingSurfaceDistance _wallContact1;

    private bool _wallSearchedMinus1;
    private bool _hasWallMinus1;
    private FloatingSurfaceDistance _wallContactMinus1;

    public bool TryGetGround(out FloatingSurfaceDistance ground)
    {
        if (!_groundSearched)
        {
            _hasGround = GroundChecker.TryFind(Body, Chunks, PlayerCharacter.Radius, PlayerCharacter.Radius, out _groundContact);
            _groundSearched = true;
        }
        ground = _groundContact;
        return _hasGround;
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
}
