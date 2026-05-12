using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Base class for path-followed movement states (Parkour, LedgePull, LedgeDrop, CoveredJump).
//
// The subclass plans a Hermite path from (current pos, current vel) to a goal point computed from
// the obstacle (corners) at Enter. Each Update, the body's projection onto the path advances
// progress, and a PD controller drives the body toward a slightly-ahead point on the path.
//
// Phantom corner constraints (the floating ramps) are preserved as a safety net against
// geometric wedging when the PD controller momentarily slips, but they no longer carry the
// locomotion logic.
public abstract class GuidedState : MovementState
{
    protected GuidedPath Path;
    protected float ProgressT;

    // Read-only accessors for debug/visualization. Path is null when the state
    // is between Enter/Exit or when planning failed.
    public GuidedPath ActivePath => Path;
    public float      CurrentProgressT => ProgressT;
    private readonly List<FloatingSurfaceDistance> _safety = new();

    // Stall watchdog: if progress fails to advance for this many consecutive
    // frames, abort the state. Body is geometrically pinned (e.g. against a
    // wall with no momentum) and the PD controller can't make headway.
    private const int   StallFrameLimit  = 8;
    private const float StallProgressEps = 1e-3f;
    private float _lastProgressT;
    private int   _stalledFrames;

    // Subclass plans the path + any safety constraints. Returning false aborts the state.
    protected abstract bool TryPlan(EnvironmentContext ctx, PlayerAbilityState abilities,
                                    out GuidedPath path,
                                    out List<FloatingSurfaceDistance> safetyConstraints);

    // Called each frame; if it returns false the state exits cleanly (e.g. player released input).
    protected abstract bool IntentHeld(EnvironmentContext ctx, PlayerAbilityState abilities);

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!TryPlan(ctx, abilities, out Path, out var safety))
        {
            Path = null;
            return;
        }
        _safety.Clear();
        if (safety != null)
        {
            foreach (var c in safety)
            {
                _safety.Add(c);
                ctx.Body.Constraints.Add(c);
            }
        }
        ProgressT      = 0f;
        _lastProgressT = 0f;
        _stalledFrames = 0;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        foreach (var c in _safety) ctx.Body.Constraints.Remove(c);
        _safety.Clear();
        Path = null;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (Path == null) return false;
        if (Path.IsComplete(ProgressT)) return false;
        if (_stalledFrames >= StallFrameLimit) return false;
        return IntentHeld(ctx, abilities);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (Path == null) { ctx.Body.AppliedForce = Vector2.Zero; return; }

        var cfg = MovementConfig.Current;

        ProgressT = Path.ProjectOnto(ctx.Body.Position);

        if (ProgressT - _lastProgressT < StallProgressEps) _stalledFrames++;
        else                                               _stalledFrames = 0;
        _lastProgressT = ProgressT;

        // Drop safety constraints once the body has crossed past their plane.
        // They exist to prevent wedging during the corner approach; on the far side
        // they only fight the PD controller (clamping velocity along the ramp surface
        // when the goal lies past the corner).
        for (int i = _safety.Count - 1; i >= 0; i--)
        {
            var fsd  = _safety[i];
            float d  = Vector2.Dot(ctx.Body.Position - fsd.Position, fsd.Normal);
            if (d < 0f)
            {
                ctx.Body.Constraints.Remove(fsd);
                _safety.RemoveAt(i);
            }
        }

        float lookT = MathF.Min(1f, ProgressT + cfg.GuidedLookahead);
        Vector2 targetPos = Path.Sample(lookT);
        Vector2 targetVel = Path.SampleVelocity(lookT);

        Vector2 force = cfg.GuidedSpringK * (targetPos - ctx.Body.Position)
                      + cfg.GuidedDamping * (targetVel - ctx.Body.Velocity);
        // Feedforward gravity cancellation so the path can be tracked exactly under gravity
        force.Y -= cfg.GuidedGravityCancel;

        // Bound force magnitude
        float mag = force.Length();
        if (mag > cfg.GuidedMaxForce && mag > 0f)
            force *= cfg.GuidedMaxForce / mag;

        ctx.Body.AppliedForce = force;
    }
}
