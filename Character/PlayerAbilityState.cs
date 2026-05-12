using Microsoft.Xna.Framework;

namespace MTile;

public class PlayerAbilityState
{
    public float TimeInState;
    public bool HasDoubleJumped;
    public bool JumpJustPressed;
    public bool UpJustPressed;
    public bool DownJustPressed;
    public bool IsLedgeGrabbing;
    public int  GrabWallDir;
    public Vector2 GrabbedCorner;
}
