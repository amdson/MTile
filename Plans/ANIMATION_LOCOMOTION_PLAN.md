# Animation ↔ Gamestate Matching: Locomotion Cadence (Mechanism #1)

Status: **planned, not implemented.** This is the design for the first slice of
matching animation playback to real motion. Scope here is deliberately narrow:
**cadence** (playback-rate matching for walk/run on ground). Positional constraint
solving (foot-lock IK, vault-hand-on-corner) is **Mechanism #2** and is only
sketched at the end so this data model doesn't paint us into a corner.

Companion docs: [ANIMATION_LAYERING_PLAN.md](ANIMATION_LAYERING_PLAN.md) (movement
+ action layering), and the animator itself lives in
[Animation/CharacterAnimator.cs](../Animation/CharacterAnimator.cs).

---

## 1. The problem, split into two mechanisms

The user's framing: "the pace of running needs to match the player movement speed,"
and "if the player vaults, their hand is on the corner." These read as one problem
but have **different dimensionality**, and conflating them makes both harder:

| | Mechanism #1 — **Cadence** | Mechanism #2 — **Contact constraints** |
|---|---|---|
| DOF | 1 (a scalar: how fast the clip plays) | 2 per contact (a world point the node must hit) |
| Drives | playback rate / phase advance | actual bone positions (via IK) |
| Solves | "pace matches speed" | "foot doesn't slip", "hand on corner" |
| Needs | clip stride length + body distance | contact labels + 2-bone IK + terrain query |
| This doc | **yes** | sketched only (§8) |

A 1-DOF time knob **cannot** satisfy a 2-DOF positional invariant except in the
idealized flat-ground / constant-velocity case. So we build cadence first (it makes
walk *and* run look right on flat ground with zero IK), then layer IK on top later.

We already distance-phase crudely: [CharacterAnimator.cs](../Animation/CharacterAnimator.cs)
advances `Phase` by `speed * dt * PhasePerPixel`. The hardcoded `PhasePerPixel = 0.010f`
is exactly the constant this plan replaces with a value **derived from the clip**.

---

## 2. Core principle: distance-phasing with clip-derived stride

Drive the phase by **distance traveled**, not by inverting foot velocity.

The tempting formulation `dφ/dt = -v_body / (d footLocal/dφ)` blows up at the stride
extremes where the foot reverses (denominator → 0) and when standing still. Instead:

```
phase += distanceTraveledAlongGround / clipStride
```

where `clipStride` is the world distance the body should cover in **one full cycle**
so the plant foot lands without slipping. The key move: **derive `clipStride` from
the clip itself** = the horizontal local travel of the planted foot during its stance
window. Then:

- Re-tuning a clip in the editor auto-retunes its pace (no magic constant to chase).
- Running = the same machinery with a longer-stride clip. No code path differs.
- It's stable: we integrate distance, never divide by a vanishing derivative.

### 2.1 Deriving `clipStride` from a clip

A foot is "planted" over a contiguous span of keyframes (its stance window). During
stance, in the skeleton's local/root frame, the foot travels backward from a forward
extreme to a rear extreme — that backward local travel is what visually propels the
body. The body must advance by exactly that much over the same phase span so the
foot stays put in world space.

```
For each foot's stance window [φ_down, φ_up]:
    footLocalX(φ_down)  = forward-most foot x in root space
    footLocalX(φ_up)    = rear-most foot x in root space
    strideContribution  = footLocalX(φ_down) - footLocalX(φ_up)   // > 0
```

For a clean alternating gait the two feet contribute equal strides; we take
`clipStride = sum of per-foot stride contributions` over one full cycle (or the mean
of the two, depending on labeling — see §6 open question). This is computed **once**
when a clip is bound (precompute, cache on the document), not per frame.

Foot local position at a keyframe = resolve the pose under an **identity root** and
read the contact node's world position. We already have everything for this:
`SkeletonPose.ComputeWorld(Affine2.Identity)` then `WorldPosition(node)`.

---

## 3. What a "contact node" is (rig decision)

A contact is a labeled **point on the skeleton**, not just a bone. Today the biped
([Drawing/SkeletonExamples.cs](../Drawing/SkeletonExamples.cs)) has `leg_l_lower` /
`leg_r_lower` whose `Length` is only a leaf orientation tick — the visible shin is the
*translation* to the lower-leg joint, so there's no explicit toe/ankle point.

**Decision to confirm (see §9):** define a contact point as a **bone tip** =
`WorldOf(bone).TransformPoint(new Vector2(bone.Length, 0))`, and add explicit
`foot_l` / `foot_r` leaf bones to the biped so the contact point is unambiguous and
editable. Alternative: reuse the lower-leg joint as the ankle. Adding feet is cleaner
and gives the future IK something to aim.

---

## 4. Data model changes (sketches)

### 4.1 Contact labels on keyframes

Each keyframe declares which nodes are in contact at that instant. The label set
**changes between frames** (left foot plants, then right). Minimal, JSON-friendly,
additive to the existing format (absent → no contacts, fully back-compatible).

```csharp
// Animation/PoseState.cs  (or a new Animation/ContactLabel.cs)

// One contact annotation on a keyframe: a named node that is planted (zero world
// velocity) at this keyframe's instant. Weight allows soft transitions later;
// 1 = fully planted, 0 = free. For Mechanism #1 we only read Weight >= 0.5.
public sealed class ContactLabel
{
    public string Node   { get; set; }       // bone name; contact point = its tip
    public float  Weight { get; set; } = 1f; // planted strength, [0,1]
}
```

```csharp
// Animation/AnimationDocument.cs  — extend AnimationKeyframe

public sealed class AnimationKeyframe
{
    public float                Time     { get; set; }
    public List<PoseBoneEntry>  Bones    { get; set; } = new();
    public List<ContactLabel>   Contacts { get; set; }   // null/empty = airborne frame
}
```

### 4.2 Precomputed locomotion profile (the derived stride)

Computed once per clip at bind time and cached. Keeps the per-frame path allocation-
free and keeps derivation logic out of the hot loop.

```csharp
// Animation/LocomotionProfile.cs  (new)

// Per-clip cadence data derived from contact labels + foot geometry. Built once when
// a clip is bound to the animator (or by the editor for display). Pure function of
// the AnimationDocument + Skeleton — no per-frame state, safe to cache on the clip.
public sealed class LocomotionProfile
{
    public float ClipStride { get; private set; }   // world px the body covers per full cycle
    public bool  HasContacts { get; private set; }  // false → fall back to old PhasePerPixel

    // Resolves each keyframe under identity root, finds each node's stance window,
    // sums the backward local travel. See §2.1.
    public static LocomotionProfile Build(AnimationDocument doc, Skeleton skeleton,
                                          SkeletonPose scratch);

    // Cadence: how much normalized phase to advance for a given ground distance.
    // phaseDelta = distance / ClipStride   (guarded against ClipStride <= 0).
    public float PhaseForDistance(float groundDistance);
}
```

### 4.3 Binding the profile to a clip

The animator already holds `Dictionary<AnimClip, AnimationDocument> _clips`. Add a
parallel cache (or wrap both in a small struct):

```csharp
// in CharacterAnimator
private readonly Dictionary<AnimClip, LocomotionProfile> _profiles = new();
// built in the constructor right after _clips is populated:
//   _profiles[clip] = LocomotionProfile.Build(anim, skeleton, scratchPose);
```

---

## 5. Runtime integration (CharacterAnimator)

Change is localized to the phase-advance step (§ "2. Advance the locomotion phase").

### 5.1 Distance, along the ground

Track distance traveled between frames. For flat ground this is `|Δposition.X|`;
designed from the start to be the projection onto the surface tangent so slopes are
not a special case we retrofit later.

```csharp
// new field
private Vector2 _prevPosForPhase;

// in Update(), replacing the speed*dt*PhasePerPixel line:
float groundDist = MathF.Abs(s.Position.X - _prevPosForPhase.X);   // TODO: project on tangent
if (clip is AnimClip.Walk or AnimClip.WalkBack
    && _profiles.TryGetValue(clip, out var prof) && prof.HasContacts)
{
    _state.Phase = Wrap01(_state.Phase + prof.PhaseForDistance(groundDist));
}
else if (clip is AnimClip.Walk or AnimClip.WalkBack)
{
    _state.Phase = Wrap01(_state.Phase + MathF.Abs(s.Velocity.X) * dt * PhasePerPixel); // legacy fallback
}
else if (clip == AnimClip.Idle) { /* unchanged bob */ }
_prevPosForPhase = s.Position;
```

### 5.2 Phase → sample time

Authored clips are currently sampled by **elapsed seconds** (`_state.ClipTime`) in
`AnimationSampler.SampleAtTime`. For locomotion we want to sample by **phase**, not
wall-clock, so the cadence actually drives the pose. Add a phase-driven entry point:

```csharp
// AnimationSampler — phase is already normalized [0,1]; reuse SampleNormalized.
AnimationSampler.SampleNormalized(anim, _state.Phase, _kfA, _kfB, _target);
```

So: locomotion clips (Walk/WalkBack/Idle) sample by `_state.Phase`; one-shot clips
(Jump/Fall/Vault/Crouch) keep sampling by `_state.ClipTime`. A per-clip flag
(`Loop` already exists on the document, and is a decent proxy: looped → phase-driven,
one-shot → time-driven) can select which.

### 5.3 What stays the same

- The directional lean post-process (§3b) and landing squash (§3c) are unchanged —
  they sit on top of the sampled base pose.
- The pull-model boundary is untouched: still read-only `CharacterAnimSample.From`,
  still render-only, still no feedback into the sim. Determinism is unaffected
  because none of this lives in `Simulation.Step`.

---

## 6. Editor support (MTile.Demo)

To author contacts and verify stride visually:

- **Label toggle:** select a node, press a key to toggle its contact label on the
  active keyframe (writes `AnimationKeyframe.Contacts`).
- **Render planted nodes** distinctly (e.g. filled red disc) so the stance schedule
  is visible while scrubbing.
- **Stride readout:** show `LocomotionProfile.ClipStride` for the selected clip, and
  optionally draw the planted foot's world track across the cycle (a horizontal bar
  whose length is the stride) so authors can see slip at a glance.
- Saving already round-trips the document; `Contacts` rides along via the existing
  `AnimationStore.Save` (System.Text.Json picks up the new property automatically).

---

## 7. Edge cases to handle (call them out now)

1. **Standstill / sub-threshold speed.** `groundDist → 0` freezes the cycle
   mid-stride with a foot possibly in the air. Keep the existing `WalkSpeedThreshold`
   blend to Idle; ensure the freeze lands on a planted-foot phase if we ever hold
   mid-walk.
2. **Flight phase (running).** A keyframe span with **zero** contacts is legal — the
   profile must tolerate it (don't divide by zero stride; that span just contributes
   no plant). Cadence coasts through flight.
3. **Slopes.** No-slip is along the **surface tangent**, not world-X. We stub
   `groundDist` as `|Δx|` now but route it through a "project on tangent" TODO so the
   fix is one function later, not a rewrite.
4. **Reach feasibility (couples to Mechanism #2).** If the body outruns the locked
   foot, the leg over-extends. The clean resolution is to *choose* stride so the
   locked foot stays within leg reach — i.e. `clipStride` is bounded by leg length.
   For now, cadence alone won't over-extend because there's no lock yet; note it for
   when IK lands.
5. **Direction sign on slopes.** Cadence magnitude uses distance; the *clip* already
   encodes forward vs backpedal (Walk vs WalkBack). Velocity sign only picks the clip
   (existing `SelectClip`), not the phase rate.

---

## 8. Forward path to Mechanism #2 (IK) — keep the door open

Not building now, but the §4 data model is chosen so this drops in cleanly:

- **Contact target + source policy.** A contact is `node → world target`, with two
  target sources:
  - *captured-from-self* (foot plant): on touchdown, capture the node's current world
    position; hold it until the label drops; IK keeps the leg reaching it.
  - *supplied-by-environment* (vault): the corner is a fixed world point handed in by
    the sim/level, active over a time window. Same primitive, different source.
- This means `ContactLabel` may later gain `enum ContactSource { SelfPlant, External }`
  and the animator a small `IkSolver` (2-bone analytic leg/arm solve). The captured
  world point lives in animator state (render-only), never in the sim.
- Hysteresis on plant/lift transitions to avoid jitter; release the lock when the
  label weight crosses below a threshold.

Sketch only — do not implement until cadence is in and proven on flat ground.

---

## 9. Open questions / decisions to confirm

1. **Add explicit `foot_l`/`foot_r` bones** to the biped, or treat the lower-leg
   joint as the ankle? (Recommend: add feet — cleaner contact point + IK target.)
2. **Per-foot vs summed stride.** Sum both feet's stance travel over a full cycle, or
   average and assume symmetric gait? (Recommend: sum; tolerant of asymmetric clips.)
3. **Phase vs time sampling selector.** Use the existing `Loop` flag (looped →
   phase-driven), or add an explicit `bool PhaseDriven` to `AnimationDocument`?
4. **Where stride is cached.** On `LocomotionProfile` held by the animator (proposed),
   or memoized on `AnimationDocument` itself? (Animator-side keeps the document a pure
   serialization DTO.)
5. **Initial labeling pass.** Hand-label `walk` and `walkback` seeds in
   `DemoGame.BuildSeeds`, or only via the editor? (Recommend: seed `walk` so there's a
   working example to derive stride from on day one.)

---

## 10. Suggested build order

1. Rig: add `foot_l`/`foot_r` (if confirmed) + `ContactLabel` type + keyframe field.
2. `LocomotionProfile.Build` + a unit test in `MTile.Tests` deriving stride from a
   hand-built 2-keyframe clip (pure math, no rendering — fits the headless test rig).
3. Editor: label toggle + planted-node rendering + stride readout.
4. Hand-label the `walk` seed; eyeball stride.
5. Animator: swap phase advance to `PhaseForDistance`, sample locomotion by phase.
6. Verify in-game: pace tracks speed across a range; no visible foot slip on flat
   ground. Then (separately) start Mechanism #2.
