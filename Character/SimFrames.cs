using System;

namespace MTile;

// Seconds → frame-count conversion for the FSMs' frame-keyed timers (combo windows,
// recovery, hitstun, input-gesture thresholds). Durations are AUTHORED in seconds so
// gameplay feel is independent of the simulation rate; each conversion site passes
// the dt it is actually being stepped at (ctx.Dt / the host's fixed dt), so headless
// tests running at a different rate than Simulation.FixedDt behave identically in
// real time. Rounded to nearest, floored at 1 so no window can vanish entirely.
public static class SimFrames
{
    public static int FromSeconds(float seconds, float dt)
    {
        if (dt <= 0f) return 1;
        return Math.Max(1, (int)MathF.Round(seconds / dt));
    }
}
