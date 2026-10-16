using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Path tracking with the feet on a platform as the only actuator
// (CorrectorChannels.BuildSlide) — DropdownState's slip off an edge along its
// clip. The path is geometry only; the drive's fade speed is the pace, so a
// running entry keeps its speed.
public struct SlidePathPolicy : IPathTrackPolicy
{
    public int   Dir;    // slide direction (toward the drop edge)
    public float Pace;   // px/s the drive pushes to

    public SlidePathPolicy(int dir, float pace) { Dir = dir; Pace = pace; }

    public int AddRows(CorrectorScratch s, int H, float dt, int pass, int rowCount) => rowCount;

    // What the feet could add along t̂ at their (unfaded) caps; nothing off the
    // floor. Legs push up, tuck pulls down, drive pushes along Dir only.
    public bool ProgressAuthority(CorrectorScratch s, Vector2 t, out float capAlong)
    {
        var cfg = MovementConfig.Current;
        capAlong = 0f;
        if (!CorrectorChannels.IsNearFloor(s.Samples[0])) return true;
        capAlong += MathF.Max(0f, -t.Y)      * cfg.FoldLegForce;
        capAlong += MathF.Max(0f,  t.Y)      * cfg.FoldTuckForce;
        capAlong += MathF.Max(0f, t.X * Dir) * cfg.FoldDriveForce;
        return true;
    }

    // The path's direction at the body: its first segment (vertex 0 is the body).
    public int BuildChannels(CorrectorScratch s, int H, int rowCount)
        => CorrectorChannels.BuildSlide(s, H, Dir,
            Vector2.Normalize(s.BeadVerts[1] - s.BeadVerts[0]), Pace);
}
