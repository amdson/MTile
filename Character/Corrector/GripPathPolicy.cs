using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Path tracking with the hands on a gripped corner (CorrectorChannels.BuildGrip)
// as the only actuator — for the ledge family's guided moves (LedgePull's
// clip). The path is geometry only; the pace is the grip caps' fade speed,
// so a body that arrives moving keeps its speed along the path.
public struct GripPathPolicy : IPathTrackPolicy
{
    public Vector2 Corner;   // the gripped corner (abilities.GrabbedCorner)
    public int     OverDir;  // the direction over the lip (the wall side)
    public float   Pace;     // px/s the arms can pull to

    public GripPathPolicy(Vector2 corner, int overDir, float pace)
    {
        Corner = corner; OverDir = overDir; Pace = pace;
    }

    public int AddRows(CorrectorScratch s, int H, float dt, int pass, int rowCount) => rowCount;

    // What the arms could add along t̂ at their (unfaded) caps; zero out of
    // reach. Every grip channel is unilateral, so only the part of its axis
    // that points along t̂ counts.
    public bool ProgressAuthority(CorrectorScratch s, Vector2 t, out float capAlong)
    {
        var cfg = MovementConfig.Current;
        capAlong = 0f;
        var pos = s.Samples[0].Pos;
        if (!CorrectorChannels.InGripReach(pos, Corner)) return true;
        capAlong += MathF.Max(0f, -t.Y)          * cfg.LipLiftForce;
        capAlong += MathF.Max(0f, t.X * OverDir) * cfg.LipPushForce;
        capAlong += MathF.Max(0f, Vector2.Dot(t, CorrectorChannels.GripHoldAxis(pos, Corner))) * cfg.LipLiftForce;
        return true;
    }

    public int BuildChannels(CorrectorScratch s, int H, int rowCount)
        => CorrectorChannels.BuildGrip(s, H, Corner, OverDir, Pace);
}
