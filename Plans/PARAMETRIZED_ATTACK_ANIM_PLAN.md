# Parametrized Attack Animation Plan — augmented reference + solved poses

Status: **proposed** (June 2026). Phase 0 (editor: labeled additions) in progress; the rest
is design. Goal: drive the player skeleton (arms/body) to follow attacks whose geometry is
**parametrized at runtime** (the stab tip extends along a ray toward the mouse), which a
fixed clip can't express. All of this is **render-only and downstream of the sim** — the
skeleton already is (`CharacterAnimator` is pull-model). The eventual solver reads sim attack
params and produces a *visual* pose; it never writes back, so no rollback/determinism risk.

## The problem

A normal attack animation can just be played and the hitbox matched to it. The **stab** is
different: its tip position is a function of `body.Position + StabDir · TipExtension(t)`,
where `StabDir` points at the mouse (an arbitrary angle θ) and `TipExtension` is a Bézier
reach curve ([StabAction](../Character/ActionStates.cs)). The glowing dot rides that tip. So
the *target geometry is parametrized* (angle θ, phase t, dive boost, grounded/air) and the
arm/body pose must follow it — you can't pre-bake one clip.

## Approach: a reference animation + a solved parametrization

**Phase 1 — author a reference.** Author a canonical attack clip (e.g. θ = 0, pointing +X)
in the editor, but **augment the skeleton/animation with labeled geometric constructs**:
- a **"spear tip" reference point** — where the glow dot sits at each phase,
- a **"stab ray" vector** — the center→tip ray,
- optionally extra **bones** (e.g. a weapon bone) if the rig needs them.

The clip now encodes the *correspondence* between the pose and the attack geometry: at phase
t, "when the dot is here, the arm looks like this." This is the bridge an IK solver needs —
it tells the solver which skeleton feature maps to the target and what the natural pose is.

**Phase 2 — synthesize parametrized poses in code.** At runtime, the sim gives the actual
target (dot position / ray, at angle θ and phase t). We solve for a skeleton pose by
minimizing an **augmented loss**:
`L(pose) = ‖feature(pose) − target‖² + λ·‖pose − reference(t)‖² + jointLimits + regionWeights`,
where `feature` is the skeleton-derived dot (e.g. the hand / weapon tip) and `reference(t)`
is the sampled reference pose. Region weights keep the lower body planted while the upper
body reaches (reusing the existing `AnimRegion` mask). Minimizing places the dot exactly on
the parametrized target while staying close to the authored style. Needs hand-tuning in code
(weights, which feature, joint limits) but most of the *authoring* happens in the editor.

This is standard pose-prior IK / retargeting; θ ends up being mostly a rotation of the target
about the root, with the loss blending how much of the body rotates vs. stays.

## Data model

Two kinds of "additions", as the names suggest different homes:

### Skeleton additions = bones (rig structure)
Extra bones appended to the rig (`Skeletons/biped.json`), e.g. a `weapon` child of a hand.
Posed by rotation like any bone; authored once, shared by every clip. The editor mutates the
rig in place today (`SetBoneBindTranslation`); adding a bone builds a new `Skeleton`
(`Skeleton.WithBone`) and recreates the editor poses. Keyframes reference bones by **name**,
so old clips are unaffected (the new bone sits at bind in them).

### Animation additions = labeled points / vectors (per-keyframe annotations)
A new collection on `AnimationKeyframe`, parallel to `Bones`/`Contacts`:

```csharp
public enum AnimAdditionKind { Point, Vector }

public sealed class AnimAddition
{
    public string Name { get; set; }            // label, e.g. "spear_tip", "stab_ray"
    public AnimAdditionKind Kind { get; set; }   // Point | Vector
    public string Parent { get; set; }           // optional anchor bone; null = root/character space
    public float Px { get; set; }                // position, local to Parent (or root) — floats, not Vector2
    public float Py { get; set; }
    public float Dx { get; set; }                // Vector kind: components from the point; ignored for Point
    public float Dy { get; set; }
}
// AnimationKeyframe.Additions : List<AnimAddition>   (null on legacy files)
```

- **Anchoring:** optional `Parent` bone; default (null) = root/character space. The stab dot
  and ray are root-relative. A point's world position = `Parent==null ? root.Transform(P) :
  boneWorld[Parent].Transform(P)`; a vector's world direction transforms the same way.
- **Serialization:** separate `float`s (the codebase avoids raw `Vector2` — `System.Text.Json`
  skips its fields). `AnimAdditionKind` rides the existing `JsonStringEnumConverter`
  (`"Point"`/`"Vector"`). Null `Additions` omitted (`WhenWritingNull`).

### Carry-over + interpolation
Mirrors the existing `ContactLabel` flow:
- **Authoring carry-over:** pressing **K** (sample new keyframe) clones the effective additions
  at the playhead from the keyframe at/before it (`CloneAdditionsAt`, like `CloneContactsAt`),
  so an addition persists forward and you only re-touch it to change it.
- **Sampling/interpolation:** at time t, match additions by **name** between the bracketing
  keyframes and lerp `P`/`D`; present in only one bracket → hold that value (carry forward).
  Removal ends the chain. Because authoring clones forward, additions are dense after
  introduction, so the common case is a clean lerp.

## Phase 0 — editor support (this step)

In [MTile.Demo/DemoGame.cs](../MTile.Demo/DemoGame.cs):

**Animation additions (points/vectors):**
- **P / V** — add a Point / Vector to the active keyframe at the cursor (cursor → root-local),
  entering a small **text-input naming** mode (type a label, Enter commits, Esc = default name).
- **Select + drag** — click a point/vector handle to select; drag a point to move it; drag a
  vector's tip to set its direction (origin stays at the point).
- **Backspace / X** — remove the selected addition from the active keyframe.
- **Render** — points as a labeled disc, vectors as a labeled arrow; editable on the active
  keyframe, dimmed (read-only, interpolated) elsewhere.
- New keyframes inherit additions (carry-over); save/load round-trips the JSON.

**Skeleton bones:**
- **B** — add a child bone of the selected joint at the cursor (text-input naming), via
  `Skeleton.WithBone`; recreate editor poses; mark the rig dirty; Ctrl-S writes `biped.json`.

## Work breakdown

| # | Change | Files |
|---|---|---|
| 0a | `AnimAddition` type + `AnimationKeyframe.Additions` | `Animation/AnimationDocument.cs` |
| 0b | Addition sampling/interp + carry-over helper | `Animation/AnimAdditionSampler.cs` (new) |
| 0c | `Skeleton.WithBone(...)` | `Drawing/Skeleton.cs` |
| 0d | Editor: add/select/drag/remove/name points & vectors; render | `MTile.Demo/DemoGame.cs` |
| 0e | Editor: add child bone; recreate poses; save rig | `MTile.Demo/DemoGame.cs` |
| 1 | Sample additions into world-space targets at runtime (read-only) | `Animation/CharacterAnimator.cs` or a new consumer |
| 2 | Augmented-loss pose solver (IK) driven by sim attack params | new; hand-tuned |
| 3 | Wire stab/slash to the solver; retire the fixed overlay where solved | `Character/*`, `Game1` |

## Risks / open questions

- **Naming UX** in a key-driven editor needs a text-input mode (`Window.TextInput`). Kept minimal.
- **Removal interpolation** semantics (fade vs. snap) left simple for v1 (hold/snap).
- **Solver cost/stability** (phase 2) — per-frame IK; start with a 1–2 bone analytic reach for
  the arm before a general optimizer.
- **Which feature maps to the dot** (hand tip vs. a weapon bone tip) — decided per attack in
  phase 2; the authored reference point is what disambiguates it.
