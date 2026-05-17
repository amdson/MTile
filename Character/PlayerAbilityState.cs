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

    // -1 or +1. Last non-zero Intent.CurrentHorizontal; persisted across standstills
    // so a standstill slash still has a side to swing toward. Refreshed by
    // PlayerCharacter.Update each frame before the action FSM runs.
    public int  Facing = 1;
    // Reserved for future use; currently unset by any state.
    public bool SlashInterrupted;

    // Combat condition flags — combo readiness, recovery, guard window. Lives here
    // so action states read/write through the same well-known struct, the same way
    // movement states use PlayerAbilityState for HasDoubleJumped etc.
    public ConditionState Condition = new();
}
