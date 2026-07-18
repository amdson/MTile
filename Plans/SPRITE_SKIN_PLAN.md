# Sprite Skin: MLS-deformed artwork over the biped rig

Render a hand-drawn PNG on top of the animated skeleton with minimal (as-rigid-as-
possible) deformation. One-time **binding** (align the rig over the drawing in an
editor) + per-frame **deformation** (moving-least-squares evaluated at the vertices
of a coarse mesh, driven by the posed skeleton). Because the deformation reads the
*posed* world transforms each render frame, a single binding covers every clip and
everything the animation solver produces — no per-clip authoring.

Reference: Schaefer, McPhail, Warren, *Image Deformation Using Moving Least
Squares* (SIGGRAPH 2006) — the **rigid** variant.

Authoring reference (binding workflow, layer-mask format, settings table):
[SpriteBindings/README.md](../SpriteBindings/README.md).

---

## 1. Core idea

- **Handles** are points sampled along bone segments. For each bone `i` with parent
  `p`, the segment is `world[p].Translation → world[i].Translation` (under the
  R·T·S chain, `world[i].Translation` IS bone i's far tip). Sample at fractions
  `t ∈ {0, ½, 1}` of the segment (dedupe: a child's t=0 coincides with its
  parent's t=1 — keep one). ~2 handles per bone ⇒ ~25 handles for the biped.
- **Bind handles** = the same (bone, t) pairs evaluated under the *bind pose* the
  user authored by dragging the skeleton over the PNG.
- **Posed handles** = the same (bone, t) pairs evaluated under the live pose.
- Parameterizing by *fraction along the segment* (not absolute distance) means art
  proportions that don't match rig proportions just bake in a gentle per-bone
  stretch — no length reconciliation needed, and bind-mode length edits never touch
  the shared rig.
- **Mesh**: a regular grid over the PNG (~8–12 px spacing in image space), cells
  fully outside the alpha discarded, kept cells' verts get UVs from image coords.
  A few hundred verts.
- **Per frame**: for every mesh vertex, MLS-rigid maps bind-handle positions →
  posed-handle positions; the heavy part is precomputed at bake (§4), leaving a
  small accumulate + one 2-vector normalize per vertex. Fill a
  `VertexPositionColorTexture[]`, draw with `DrawUserIndexedPrimitives`.

## 2. Spaces

Everything binds in **rig-local units** (the space `pose.ComputeWorld(Affine2.Identity)`
resolves in). The binding stores an image→rig transform (uniform scale + translation)
so mesh verts are converted to rig-local once at bake. At runtime the deformed verts
go through the **same root `Affine2`** the game already builds for
`SkeletonRenderer.Draw` (position · SkeletonScale · facing flip) — so mirroring,
placement, and COM anchoring are inherited for free, and the MLS solve itself always
runs in canonical facing.

## 3. Binding asset — `SpriteBindings/<name>.json`

```jsonc
{
  "Skeleton": "biped",              // must match rig name, like clips do
  "Image": "hero.png",              // sibling file, premultiplied at load
  "ImageToRig": { "Scale": 0.31, "Tx": -14.2, "Ty": -22.0 },
  "BindPose": [                     // per-bone, by name (robust to rig growth)
    { "Bone": "chest", "Rotation": -1.52, "Length": 16.8 },
    ...
  ],
  "MeshStep": 10,                   // grid spacing in image px
  "AlphaThreshold": 8,
  "Layers": [                       // v2 — see §8; v1 = one layer, all bones
    { "Name": "body", "Bones": ["*"] }
  ]
}
```

`Length` here is the *binding's* length (where the art's elbow actually is), stored
per-binding — the shared `Skeletons/biped.json` is never written by bind mode.

## 4. Bake (load time, once)

For each mesh vertex `v` (rig-local) against bind handles `p_i`:

1. Weights `w_i = 1 / (|p_i − v|² + ε)` (ε ≈ 0.25 rig-units² regularizes verts
   sitting on a handle).
2. Weighted centroid `p* = Σw_i p_i / Σw_i`, offsets `p̂_i = p_i − p*`.
3. Precompute the rigid-MLS per-handle 2×2 matrices (paper eq. 6/7):
   `A_i = w_i · [p̂_i; −p̂_i⊥] · [v−p*; −(v−p*)⊥]ᵀ` — depends only on bind data.
4. Store per vertex: `w_i[]`, `A_i[]` (flattened), `|v − p*|`, and `p*`-recovery
   weights. Memory: ~25 handles × ~500 verts × ~6 floats ≈ 300 KB. Fine.

Optionally prune per-vertex handle lists to the K nearest (K ≈ 8) — distant handles
have negligible weight and it quarters the per-frame cost.

## 5. Per-frame deform (CPU)

Given posed handles `q_i`:

```
q*  = Σ w_i q_i / Σ w_i                  // per vertex (precomputed w)
f_r = Σ (q_i − q*) · A_i                 // 2-vector accumulate
v'  = |v − p*| · f_r / |f_r| + q*        // rigid: rotation+translation only
```

~500 verts × 8 handles × a few mul-adds at 30 fps → microseconds. Allocation-free:
persistent vertex/index arrays, matching the existing alloc-free-surfaces
discipline.

## 6. Runtime integration

- New `Drawing/SpriteSkin.cs`: owns the baked data, a `Texture2D`, persistent
  `VertexPositionColorTexture[]` + `short[]` indices, a `BasicEffect`.
- Hook: where `CharacterAnimator.Draw` currently calls `SkeletonRenderer.Draw`,
  call `skin.Draw(gd, pose, root, cameraMatrix)` instead (debug toggle keeps the
  stick figure available). It calls `pose.ComputeWorld(Affine2.Identity)` for the
  solve and applies `root` to the output verts.
- Rendering: must happen outside the active `SpriteBatch` (End → mesh draw →
  Begin, or ordered after the batch). `BasicEffect` with an orthographic projection
  composed with the same camera matrix `SpriteBatch` uses. Precedent:
  `SkeletonMetaballRenderer` already does custom-effect rendering.
- **KNI check**: `DrawUserIndexedPrimitives`, `BasicEffect`,
  `VertexPositionColorTexture` all exist in KNI — but verify with a
  `MTile.Web` build before building far on top (per the dual-target rule).
- Clip-local `ExtraBones` (knives etc.): not part of the binding; they keep their
  current rendering. Bones present in the rig but absent from the binding's
  BindPose contribute no handles.

## 7. Binding workflow (editor)

Extend `MTile.Demo` with a **bind mode**: `dotnet run --project MTile.Demo -- --bind hero.png`
(PNG resolved against `Sprites/`; existing binding loaded if present).

- Draws the PNG as a backdrop (checkerboard behind alpha), the rig on top,
  semi-transparent mesh preview toggleable.
- **Reuses the existing edit machinery**: joint drag in Rotate mode poses the bind
  pose; Resize mode drags the binding `Length` (routed to the binding record, NOT
  `SetBoneLength` / the base rig); root drag + mouse wheel edit `ImageToRig`
  (translate / scale the whole rig over the art).
- No timeline, keyframes, or additions in this mode — it edits exactly one pose.
- Live preview: a `[`/`]`-style key scrubs a chosen clip (e.g. walk) with the
  deformation applied, so alignment quality is judged on motion, not just the
  static overlay.
- Ctrl-S writes `SpriteBindings/<name>.json`.

Authoring guidance for the PNG: draw in a neutral, limbs-slightly-apart pose
(A-pose analogue) — MLS quality degrades when bind-pose limbs overlap the torso,
because handles from both grab the same pixels (see §8).

## 8. Known artifacts and their mitigations

- **Joint pinch / candy-wrap** at extreme bends: the t=½ mid-segment handles
  already soften this; if a joint still collapses, add a t=¼/¾ sample on its two
  bones. Solver-side `AngleCorrLimit` caps the worst inputs.
- **Cross-grab** (arm drawn over torso drags torso pixels): v2 **Layers** — split
  the PNG into 2–4 overlapping layers (arms / torso+head / legs), each an
  independent mesh deformed by only its listed bones, drawn back-to-front. Softer
  than paper-doll cutting since each layer still deforms smoothly.
- **Alpha bleed** at mesh edges: premultiply alpha at load + dilate RGB into
  transparent texels at bake.
- **Left/right limb depth**: single-layer v1 can't reorder; the far arm should be
  drawn (shaded darker) in the art itself, as in classic side-scroller sprites.

## 9. Milestones

1. ✅ **Math + golden test**: `Drawing/MlsDeformer.cs` + `MTile.Tests/Animation/MlsDeformerTests.cs`
   (identity, exact rigid reproduction at several angles, interpolation, no-stretch,
   pruning locality).
2. ✅ **Runtime render**: `Drawing/SpriteBinding.cs` (document + `SkinHandleLayout`),
   `Drawing/SpriteSkin.cs`; Game1 loads `SpriteBindings/player.json` when present
   (`GameConfig.DrawPlayerSpriteSkin`); desktop + KNI web builds verified.
3. ✅ **Bind mode in MTile.Demo**: `dotnet run --project MTile.Demo -- --bind <name>.png`
   (`MTile.Demo/BindGame.cs`) — backdrop, drag-to-align, Shift+wheel rig scale,
   G deform preview, Space clip playback, Ctrl-S. `SpriteBindings/test_hero.*` is a
   working example (generated placeholder art).
4. **Polish**: alpha dilation, mipmaps for minification, secondary-player
   per-character bindings.
5. ✅ **Layers** (was v2): one PNG + color-coded mask (`Mask` + `Layers` in the binding
   json, back-to-front order, `#RRGGBB` per layer, `*` bone wildcards, colorless layer =
   catch-all). Each layer gets its own zeroed texture, its own mesh (no cross-region
   triangles), and bone-filtered handles (no cross-region influence). Example:
   `SpriteBindings/test_hero.json` + `test_hero_mask.png`.
   Also shipped: deformation-quality knobs `WeightAlpha` (MLS falloff exponent, default 2)
   and `HandleStep` (handle density along bones, default 0.25); editor `--usebind` skin
   preview with G/W/X (sprite / wireframe / skeleton) toggles.
6. **v3 ideas**: per-vertex tint (team colors), per-layer z tweaks by facing.
