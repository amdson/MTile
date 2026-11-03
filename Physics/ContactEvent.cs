using Microsoft.Xna.Framework;

namespace MTile;

public enum ContactOutcome : byte { Stop, Bounce, BreakThrough }

// One terrain impact the sweep resolved, recorded on PhysicsBody.ContactEvents. Facts in
// physics' own terms only — no consumer's vocabulary — so anything (the launch
// reconciler, audio, particles) can read the same list without physics knowing who
// listens. Add a field only when a consumer needs it.
public readonly struct ContactEvent
{
    public readonly Vector2        Normal;          // surface normal, pointing out at the body
    public readonly Vector2        SurfaceVelocity;
    public readonly float          VnBefore;        // relative normal speed in, ≤ 0
    public readonly ContactOutcome Outcome;
    public readonly float          Restitution;     // the e applied; 0 unless Outcome == Bounce

    public ContactEvent(Vector2 normal, Vector2 surfaceVelocity, float vnBefore,
                        ContactOutcome outcome, float restitution)
    {
        Normal          = normal;
        SurfaceVelocity = surfaceVelocity;
        VnBefore        = vnBefore;
        Outcome         = outcome;
        Restitution     = restitution;
    }
}
