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
// RUNTIME STATUS (pre-rewrite, kept as-is on purpose): gameplay placement ignores this
// channel entirely and anchors to the sim body via the "com" addition alone — and does
// so INCONSISTENTLY across consumers today: the draw root subtracts dir·com.X and com.Y
// (AttackGlowSystem.RigRoot), while the cadence solve root (CharacterAnimator step 2)
// and ClipStrideTrack offsets subtract only com.Y. Nonzero authored com.X therefore
// shifts draw vs solve by up to a few px. The placement rewrite should collapse all
// three onto one convention; until then, do not add new com.X-dependent behavior.
public static class BodyPath
{
    public const string ChannelName = "body_path";

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
