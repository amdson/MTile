using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// A movement- or driver-sourced overlay request for one frame: paint `Clip` sampled at
// normalized time `Tau` over the base pose. `Key` identifies the request across frames so
// an in-progress fade stays on its slot (see OverlayStack.Update's matching rules).
public struct OverlayRequest
{
    public string            Key;
    public AnimationDocument Clip;
    public float             Tau;
    public OverlayRequest(string key, AnimationDocument clip, float tau)
    { Key = key; Clip = clip; Tau = tau; }
}

// The overlay compositor: an ordered stack of crossfading motion layers painted over the
// base clip's pose (extracted from CharacterAnimator — the slot lifecycle is bookkeeping,
// not solve math). Slot 0 is PRIVILEGED — the Action-FSM overlay that drives ActionWeight /
// ActionBound / the knife pose; slots 1+ serve movement/driver requests, matched by key.
//
// Compositing model is alpha-over (painter's algorithm), NOT a weighted average: slots are
// painted foreground-last (slot 0 wins a shared bone), each at a per-bone opacity in [0,1]
// (Region mask × eased weight, OffRegionWeight outside the mask). The base layer's surviving
// coefficient per bone is the order-independent product Π(1−w) over the active slots — cached
// in BaseBlend, which the cadence solver's analytic Jacobian scales each base-driven (Δφ)
// column by. Everything here is frozen per frame BEFORE the solve runs: poses are sampled
// once in Update, so Compose applies the same constant blend to every candidate pose the
// solver scores.
public sealed class OverlayStack
{
    // Asymmetric on purpose: GroundSlash1 lasts ~0.14s, so the overlay must register almost
    // immediately; the slow ease-out keeps the arm raised across the brief ReadyAction gaps
    // between combo hits instead of dipping back to the walk pose.
    private const float EaseIn    = 25f;   // 1/s — weight rise rate while a clip is bound
    private const float EaseOut   = 8f;    // 1/s — weight release rate while fading
    private const int   SlotCount = 3;     // Action (0) + two movement overlays
    private const float WeightEps = 1e-3f;

    private sealed class Slot
    {
        public string             Key;        // source id (action / request key); null = idle
        public AnimationDocument  Clip;       // target clip this frame; null = idle / fading out
        public bool[]             Mask;       // Region bone mask (held through the fade-out)
        public float              OffWeight;  // Clip.OffRegionWeight for bones outside Mask (0 = hard mask)
        public float              Weight;     // eased opacity 0..1
        public bool               Active;     // composed this frame (Weight>eps && Mask!=null)
        public readonly SkeletonPose Pose;    // last-sampled overlay pose (persisted for the fade-out)
        public readonly float[]   BoneWeight; // per-bone opacity ((Mask?1:OffWeight)*Weight) — compose scratch
        public Slot(Skeleton rig) { Pose = rig.CreatePose(); BoneWeight = new float[rig.Count]; }
    }

    private readonly Skeleton _rig;
    private readonly bool[][] _regionMasks;                    // per-AnimRegion bone masks (shared with the animator)
    private readonly Slot[]   _slots;
    private readonly float[]  _baseBlend;                      // per-bone Π(1−w) over the active slots
    private readonly bool[]   _claimed = new bool[SlotCount];  // request→slot assignment scratch
    private readonly SkeletonPose _kfA, _kfB, _kfC, _kfD;      // sampling scratch (C1 keyframe quad)

    public OverlayStack(Skeleton rig, bool[][] regionMasks)
    {
        _rig = rig;
        _regionMasks = regionMasks;
        _slots = new Slot[SlotCount];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new Slot(rig);
        _baseBlend = new float[rig.Count];
        for (int i = 0; i < _baseBlend.Length; i++) _baseBlend[i] = 1f;
        _kfA = rig.CreatePose(); _kfB = rig.CreatePose();
        _kfC = rig.CreatePose(); _kfD = rig.CreatePose();
    }

    // The Action slot's eased opacity (drives CharacterAnimState.ActionWeight and the
    // upper-body stiffness ramp) and whether an action clip is currently bound (vs fading).
    public float        ActionWeight => _slots[0].Weight;
    public bool         ActionBound  => _slots[0].Clip != null;
    // The raw action-overlay pose (authored attack trajectory at its τ, full weight, no pose
    // smoothing) — for hosts anchoring effects to the full authored motion (the slash glow).
    public SkeletonPose ActionPose   => _slots[0].Pose;

    public bool    AnyActive { get; private set; }   // any slot composed this frame
    // Per-bone Π(1−w): the base layer's surviving coefficient under the active overlays. The
    // cadence Jacobian's Δφ channel is scaled by this (advancing the phase only moves the base
    // layer; an overlay at opacity w absorbs fraction w of that motion). The array identity is
    // stable — the animator aliases it once.
    public float[] BaseBlend => _baseBlend;

    // Resolve one frame: bind/ease the Action slot from the action layer, match `requests`
    // onto slots 1+ (key match first so an in-progress fade keeps its slot; then idle slots
    // adopt new requests; unmatched slots fade out holding their last pose/mask), and refresh
    // the per-bone weights + BaseBlend. Call once per frame BEFORE anything composes.
    public void Update(string actionKey, AnimationDocument actionClip, float actionTau,
                       List<OverlayRequest> requests, float dt)
    {
        Bind(_slots[0], actionKey, actionClip);
        Ease(_slots[0], actionClip, actionTau, dt);

        Array.Clear(_claimed, 0, _claimed.Length);
        for (int si = 1; si < _slots.Length; si++)
        {
            int req = -1;
            // prefer the request already on this slot (key match) to keep the weight continuous
            for (int k = 0; k < requests.Count && k < _claimed.Length; k++)
                if (!_claimed[k] && string.Equals(requests[k].Key, _slots[si].Key, StringComparison.Ordinal))
                { req = k; break; }
            if (req < 0 && _slots[si].Weight <= WeightEps)   // else adopt a new request on an idle slot
                for (int k = 0; k < requests.Count && k < _claimed.Length; k++)
                    if (!_claimed[k]) { req = k; break; }
            if (req >= 0)
            {
                _claimed[req] = true;
                Bind(_slots[si], requests[req].Key, requests[req].Clip);
                Ease(_slots[si], requests[req].Clip, requests[req].Tau, dt);
            }
            else
            {
                Bind(_slots[si], _slots[si].Key, null);   // fade out (holds mask/pose)
                Ease(_slots[si], null, 0f, dt);
            }
        }

        AnyActive = false;
        for (int i = 0; i < _rig.Count; i++) _baseBlend[i] = 1f;
        foreach (var slot in _slots)
        {
            if (!slot.Active) continue;
            AnyActive = true;
            // Per-bone survival of the base layer, using each bone's GRADED weight so a graded
            // off-region overlay correctly reports the legs as only partly overridden. Off-region
            // bones at OffWeight=0 contribute *=1 (no-op) — the old hard-mask product exactly.
            for (int i = 0; i < _rig.Count; i++)
                _baseBlend[i] *= 1f - slot.BoneWeight[i];
        }
    }

    // Paint the frozen overlay poses onto `pose` — the single point the solve and the draw
    // share, so both evaluate the SAME post-blend skeleton. No-op when nothing is active.
    public void Compose(SkeletonPose pose)
    {
        if (!AnyActive) return;
        // Foreground-LAST (slot 0, the Action overlay, wins a shared bone over a movement
        // overlay). BaseBlend is a product → order-independent, so the analytic Jacobian stays
        // consistent regardless of this paint order.
        for (int si = _slots.Length - 1; si >= 0; si--)
            if (_slots[si].Active) Paint(pose, _slots[si].Pose, _slots[si].BoneWeight);
    }

    // Paint a motion layer onto the accumulator pose: per bone, blend toward the layer by its
    // opacity (0 keeps the accumulator, 1 takes the layer; shortest-path angle lerp). The one
    // composition primitive for all motion layers; allocation-free.
    private void Paint(SkeletonPose acc, SkeletonPose layer, float[] weight)
    {
        for (int i = 0; i < _rig.Count; i++)
        {
            float w = weight[i];
            if (w > 0f) acc.Local[i] = BoneTransform.Lerp(acc.Local[i], layer.Local[i], w);
        }
    }

    // Point a slot at a target overlay (key + clip). On a KEY change rebind the clip and (when
    // non-null) its Region mask; a null clip keeps the prior mask/pose so a fade-out blends away
    // from the last sample, not garbage. Weight is NOT reset → a combo rebind keeps the arm up.
    private void Bind(Slot slot, string key, AnimationDocument clip)
    {
        if (!string.Equals(key, slot.Key, StringComparison.Ordinal))
        {
            slot.Key = key;
            slot.Clip = clip;
            if (clip != null) { slot.Mask = _regionMasks[(int)clip.Region]; slot.OffWeight = clip.OffRegionWeight; }
        }
        else slot.Clip = clip;
    }

    // Ease a slot's opacity toward 1 (clip bound) or 0 (fading), sample its pose at τ when
    // bound, and refresh its per-bone compose weights / Active flag. A fully faded, unbound
    // slot snaps to 0 and frees its key for reuse.
    private void Ease(Slot slot, AnimationDocument clip, float tau, float dt)
    {
        bool bound = clip != null;
        float wRate = bound ? EaseIn : EaseOut;
        slot.Weight += ((bound ? 1f : 0f) - slot.Weight) * (1f - MathF.Exp(-wRate * dt));
        if (bound)
            AnimationSampler.SampleSmooth(clip, tau, _kfA, _kfB, _kfC, _kfD, slot.Pose);
        slot.Active = slot.Weight > WeightEps && slot.Mask != null;
        if (slot.Active)
            for (int i = 0; i < _rig.Count; i++)
                slot.BoneWeight[i] = (slot.Mask[i] ? 1f : slot.OffWeight) * slot.Weight;
        else if (!bound) { slot.Weight = 0f; slot.Key = null; }   // snap the fade tail, free the slot
    }
}
