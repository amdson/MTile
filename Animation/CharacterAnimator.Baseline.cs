using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Read-only accessors for the animation baseline harness (MTile.Bench --anim-baseline,
// Plans/ANIMATION_RUNTIME_SIMPLIFICATION_PLAN.md §1). Nothing here changes behavior; every
// member reads state the last Update left behind. Solver counters are only meaningful when
// SolvedThisFrame is true — a fast-path frame leaves the previous solve's numbers in place.
public sealed partial class CharacterAnimator
{
    // A cadence (locomotion, planted-contact) solve ran this frame, as opposed to the static
    // off-locomotion solve — the static path clears _contacts before solving, the cadence path
    // only solves with at least one contact.
    public bool BaselineCadenceSolved => _haveCorr && _contacts.Count > 0;

    // This frame's clip plays off the cadence phase (CadencePhase time mode).
    public bool BaselineCadenceMode => _timeMode == ClipTimeMode.CadencePhase;

    public bool BaselineAimActive => _aimActive;
    public int  BaselineRejectedTrials => _ls.LastRejectedTrials;
    public int  BaselineVars => IdxTheta0 + _skeleton.Count;

    public int BaselineContactCount => _contacts.Count;
    public (int Bone, Vector2 Target, float Weight, ContactSource Source) BaselineContact(int i)
        => (_contacts[i].Bone, _contacts[i].Target, _contacts[i].Weight, _contacts[i].Source);

    // Largest |Δθ| the solver applied this frame (0 on a fast-path frame).
    public float BaselineMaxAbsDTheta
    {
        get
        {
            if (!_haveCorr) return 0f;
            float m = 0f;
            for (int i = 0; i < _skeleton.Count; i++)
                m = MathF.Max(m, MathF.Abs(_solveVars[IdxTheta0 + i]));
            return m;
        }
    }
}
