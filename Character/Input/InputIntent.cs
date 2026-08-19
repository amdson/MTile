namespace MTile;

// Per-frame, buffered read of player input. "Held" requires sustained press over HeldFrames frames,
// so taps don't trigger states that should require deliberate input (e.g. ParkourState toward a wall).
public struct InputIntent
{
    public int  HeldHorizontal;     // -1, 0, 1 — sustained over HeldFrames consecutive frames
    public int  CurrentHorizontal;  // -1, 0, 1 — current frame only
    public bool JumpHeld;
    public bool JumpJustPressed;
    public bool DownHeld;
    public bool DownJustPressed;
    public bool UpHeld;
    public bool UpJustPressed;

    public const int HeldFrames = 3;

    public static InputIntent From(Controller ctrl)
    {
        var cur  = ctrl.Current;
        var prev = ctrl.GetPrevious(1);

        int curH = (cur.Right ? 1 : 0) - (cur.Left ? 1 : 0);
        int heldH = curH;
        if (curH != 0)
        {
            for (int i = 1; i < HeldFrames; i++)
            {
                var p = ctrl.GetPrevious(i);
                int ph = (p.Right ? 1 : 0) - (p.Left ? 1 : 0);
                if (ph != curH) { heldH = 0; break; }
            }
        }

        return new InputIntent
        {
            HeldHorizontal    = heldH,
            CurrentHorizontal = curH,
            JumpHeld          = cur.Space,
            JumpJustPressed   = cur.Space && !prev.Space,
            DownHeld          = cur.Down,
            DownJustPressed   = cur.Down && !prev.Down,
            UpHeld            = cur.Up,
            UpJustPressed     = cur.Up && !prev.Up,
        };
    }
}
