using Microsoft.Xna.Framework;

namespace MTile;

// Bundle of combat-condition flags that compose moves. Each flag has an associated
// expire frame; Tick() unsets stale flags so combo windows close cleanly.
//
// Conventions:
//   * The exit of an attack sets the next-stage flag (Slash2Ready, etc.) + the
//     RecoveryActive flag.
//   * The Enter of the chained attack clears the gating flag (Slash2Ready→false)
//     and the RecoveryActive flag (combo move preempts recovery).
//   * Tick is called once per frame at the start of PlayerCharacter.Update so the
//     action FSM sees a consistent snapshot.
public class ConditionState
{
    public bool Slash2Ready;     public int Slash2ExpireFrame;
    public bool Slash3Ready;     public int Slash3ExpireFrame;
    public bool AirSlash2Ready;  public int AirSlash2ExpireFrame;
    public bool RecoveryActive;  public int RecoveryExpireFrame;
    // Late-recovery window for moves that *can* preempt Recovery (Guard etc).
    // Set by Recovery.Enter when the move flagged its tail; unused in V1.
    public bool GuardWindow;     public int GuardWindowExpireFrame;

    // Block eruption hand-off state. Set by BlockReadyAction.Exit when the player
    // sweeps the cursor out of solid (the natural ignition). Consumed by
    // BlockEruptionAction.Enter on the same/next frame. Not time-expired — relies
    // on the action FSM to pick up the armed flag within one frame and consume it.
    public bool    BlockEruptionArmed;
    public float   BlockChargeTime;
    public Vector2 BlockChargeOrigin;

    public void Tick(int currentFrame)
    {
        if (Slash2Ready    && currentFrame >= Slash2ExpireFrame)    Slash2Ready    = false;
        if (Slash3Ready    && currentFrame >= Slash3ExpireFrame)    Slash3Ready    = false;
        if (AirSlash2Ready && currentFrame >= AirSlash2ExpireFrame) AirSlash2Ready = false;
        if (RecoveryActive && currentFrame >= RecoveryExpireFrame)  RecoveryActive = false;
        if (GuardWindow    && currentFrame >= GuardWindowExpireFrame) GuardWindow  = false;
    }

    // Helper for the common "set flag, schedule expiry" pattern used by every attack's Exit.
    public static void SetFor(ref bool flag, ref int expire, int durationFrames, int currentFrame)
    {
        flag = true;
        expire = currentFrame + durationFrames;
    }
}
