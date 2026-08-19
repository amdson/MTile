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
    // Dropdown → LedgeGrab chain offer (one frame): set by DropdownState.Exit when
    // Down is released with the slide committed (±1 = wall side of the lip to
    // grab, corner already in GrabbedCorner), 0 otherwise — Exit always rewrites
    // it so a stale offer can't survive. Consumed by LedgeGrabState.Enter.
    public int  DropChainDir;

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

    // Defensive combat state — hitstun / stun / hit-history. Sibling of Condition;
    // populated by PlayerCharacter.OnHit, read by jump preconditions etc.
    public CombatState Combat = new();

    // Block-placement economy (reservoir / working pool / eruption charge). Sibling of
    // Condition; read and spent by the placement actions.
    public BuildMeters Meters = new();

    // Snapshot/restore (roadmap goal 4 §E). Deep-copies the value fields plus the
    // two nested state objects, so the result is a standalone POCO with no aliasing.
    public PlayerAbilityState Clone()
    {
        var c = (PlayerAbilityState)MemberwiseClone();   // copies value fields
        c.Condition = Condition.Clone();
        c.Combat    = Combat.Clone();
        c.Meters    = Meters.Clone();
        return c;
    }

    // Apply a previously-cloned snapshot back onto the live instance in place (so
    // references held elsewhere — EnvironmentContext wiring — stay valid).
    public void CopyFrom(PlayerAbilityState o)
    {
        TimeInState     = o.TimeInState;
        HasDoubleJumped = o.HasDoubleJumped;
        JumpJustPressed = o.JumpJustPressed;
        UpJustPressed   = o.UpJustPressed;
        DownJustPressed = o.DownJustPressed;
        IsLedgeGrabbing = o.IsLedgeGrabbing;
        GrabWallDir     = o.GrabWallDir;
        GrabbedCorner   = o.GrabbedCorner;
        DropChainDir    = o.DropChainDir;
        Facing          = o.Facing;
        SlashInterrupted = o.SlashInterrupted;
        Condition.CopyFrom(o.Condition);
        Combat.CopyFrom(o.Combat);
        Meters.CopyFrom(o.Meters);
    }
}
