using Microsoft.Xna.Framework;

namespace MTile;

// BODY PATH — the com anchor's authored position in CLIP SCENE space
// (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md: `p(t)`; the channel formerly saved as the
// editor-only "edref" track). One reserved per-keyframe Point addition, sampled by the
// standard sparse-track sampler; rig units, +x forward at canonical right-facing, +y
// down, origin = the clip's scene anchor (the editor's fixed scenery frame).
//
// The full placement model (editor math today, the runtime contract after its rewrite):
//     sceneCoM(t)   = p(t)                       — this channel
//     scenePoint(t) = p(t) + q(t) − c(t)         — q: rig point, c: the "com" addition
// i.e. the com marker has a scene position, and the skeleton root hangs off it by the
// pose-local com anchor. Exactly ONE source owns p(t): a ReferenceArc when the clip
// names one, else this track, else the clip is stationary (missing intent — callers
// must distinguish that from an authored zero path).
//
// RUNTIME PLACEMENT (one convention, 2026-09-10 — workplan chunk 2): gameplay placement
// ignores p(t) (the sim body B is the authority; adding p would double-count travel) and
// hangs the rig off B by the pose anchor c(t), BOTH axes, facing/scale applied:
//     rigRoot(t)   = B − facingAndScale(c(t))            — RootOffset below
//     worldPoint   = B + facingAndScale(q(t) − c(t))
// Every consumer routes through RootOffset/TrySampleAnchor: the draw root
// (AttackGlowSystem.RigRoot), the cadence + static solve root (CharacterAnimator.SolveRootAt,
// re-anchored at each candidate phase), and ClipStrideTrack's body-relative offsets. The
// editor's com-anchored placement (root = anchor − com·scale) is the same math.
public static class BodyPath
{
    public const string ChannelName = "body_path";
    public const string AnchorName  = "com";   // the pose anchor c(t), rig units, root-local

    // c(t) and its t-derivative (sparse C1 track — AnimAdditionSampler.SamplePoint). False
    // when the clip authors no anchor; callers keep their own fallback placement.
    public static bool TrySampleAnchor(AnimationDocument doc, float t, out Vector2 c, out Vector2 dc)
        => AnimAdditionSampler.SamplePoint(doc, t, AnchorName, out c, out dc);

    // World offset from the sim body to the rig root for anchor c: −facingAndScale(c). Linear
    // in c, so it maps an anchor velocity ċ to the root's velocity the same way.
    public static Vector2 RootOffset(Vector2 c, int facing, float scale)
    {
        int dir = facing == 0 ? 1 : facing;
        return new Vector2(-(dir * c.X * scale), -(c.Y * scale));
    }

    // p(phase) if the clip authored a body path (phase clamped to [0,1]; the sparse
    // sampler holds endpoints — no cyclic wrap, so p(1) is the true final position).
    public static bool TrySample(AnimationDocument doc, float phase, out Vector2 p)
        => AnimAdditionSampler.SamplePoint(doc, MathHelper.Clamp(phase, 0f, 1f), ChannelName, out p);

    // Per-cycle displacement D = p(1) − p(0) for loop extension:
    //     pExtended(n + phase) = n·D + p(phase).
    public static bool TryCycleDisplacement(AnimationDocument doc, out Vector2 d)
    {
        d = default;
        if (!TrySample(doc, 0f, out var p0) || !TrySample(doc, 1f, out var p1)) return false;
        d = p1 - p0;
        return true;
    }
}
