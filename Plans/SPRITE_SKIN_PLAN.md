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

---

## 10. Multi-image bindings (rabbit & badger) — execution plan

### Campaign status (branch `sprite-skin-multi-image`, started 2026-07-30)

Decisions made with user: P1=rabbit, P2=badger defaults with `player` as fallback;
xvfb + Mesa installed on the box for headless GPU steps.

- [x] M1 — Phase 1: format (`SpriteSkinLayer.Image/OffsetX/OffsetY`, optional doc `Image`) + round-trip test
- [x] M2 — Phase 0: import tool (`--import`), run on rabbit_and_badger → cropped PNGs + first-pass jsons
- [x] M3 — Phase 2: bake (per-layer textures/meshes in `SpriteSkin`) + KNI web build check
- [x] M4 — Phase 3: bind editor (composite backdrop, layer cycling, union-bbox fit)
- [ ] M5 — Phase 4: wiring (per-player binding names, optional `Tint`)

### Campaign log

- M1 format: per-layer `Image`/`OffsetX`/`OffsetY`, doc `Image` optional (`HasValidImages`),
  `LayerImagePath` resolver; `SpriteBindingSerializationTests` 4/4 green.
- M2 import: `ImportGame` (`--import/--out/--scale`), ran at scale 0.25 under xvfb —
  12 parts cropped (rabbit assembles 254x507 px, badger 284x465; registration confirmed),
  first-pass rabbit/badger.json generated with auto-fit ImageToRig + default bind pose.
  Environment fixes rolled in: `.config/dotnet-tools.json` was GITIGNORED (root cause of
  the long-standing content-build failure) — force-added; `Content.Demo.mgcb` gives the
  Demo a font-only content build (game .fx shaders need Wine on Linux; Demo never used
  them). Box also needed: xvfb+Mesa, fonts (Arial-named Liberation in ~/.fonts), disk
  cleanup (/var/log + apt cache were filling the 10G root).
- M3 bake: per-layer Image branch in the `SpriteSkin` ctor (own texture, always
  premultiplied, mesh in layer-local px, rig pos = ImageToRig(pos + offset), UVs
  layer-local); mask carving now lazy + mixed-mode safe (own-image layers excluded from
  mask color table / catch-all). Core + KNI compile clean, 454 tests pass. Visual smoke:
  `--usebind rabbit` MTILE_SHOT under xvfb shows the assembled rabbit deforming on walk.
  NOTE: KNI web CONTENT step can't run on this box (MGCB.exe, exit 127) — verified via
  `/t:Compile`, same C# surface; full web build unaffected on Windows.
- M4 editor: `--bind rabbit` json-first; backdrop composites all layer images at offsets
  (registration verified visually for BOTH characters — badger boots hide behind the
  cloak as planned); `[`/`]` layer cycling with dimmed deselection + yellow mesh outline
  + bones in header; fits use the union canvas bbox; `MTILE_SHOT_LAYER` added for
  headless verification. Screenshots confirm: assembly, walk-clip deformation, layer
  highlight.

Generalize the binding so a layer can bring its OWN PNG (decomposed-limb art) instead of
being carved out of one shared image by the mask. The mask path stays; the two modes
coexist per layer. Everything here is render-only — none of it touches the sim.

### 10.1 The assets (verified facts, 2026-07-29)

`SkeletonAssets/rabbit_and_badger/` — 12 PNGs, **all 3800×2400, all colorType 6 (real
RGBA alpha; no matting needed)**, one body part per file, two characters. Commit the
PNGs (~6.8 MB total); do NOT commit the sibling `.zip`. Inventory (near/far limb
assignment is a GUESS — resolve visually in the bind editor and fix by reordering json):

| File | Character | Part (best guess) |
|---|---|---|
| IMG_4367 | rabbit | head (helmet + ears) |
| IMG_4368 | rabbit | leg, green trouser + paw |
| IMG_4369 | rabbit | leg, green trouser + paw (other) |
| IMG_4370 | rabbit | arm, pauldron + bracer + paw |
| IMG_4371 | rabbit | torso: cross surcoat + red scarf collar |
| IMG_4372 | rabbit | arm (other) |
| IMG_4373 | badger | arm, gray fur + claws |
| IMG_4374 | badger | leg: boot + UNFILLED white thigh outline |
| IMG_4375 | badger | leg: boot + unfilled thigh (other) |
| IMG_4376 | badger | torso: blue cloak + knife + rope belt |
| IMG_4377 | badger | arm/leg, gray fur + claws (other) |
| IMG_4378 | badger | head (hood) |

Key property: the parts appear to be **layer exports in registration** — each part sits at
its drawn position on the shared canvas (rabbit assembles around canvas center, badger at
left), so ONE shared `ImageToRig` + one bind pose covers every part. The editor's
composited backdrop (10.5) verifies this in seconds. If it ever fails, the per-layer
`OffsetX/Y` (10.3) is the escape hatch: parts get individual offsets from the import
manifest instead of a shared origin.

Badger gotcha: the white thigh outlines are opaque pixels — they WILL mesh and render.
Intended to sit under the cloak, so boots go BEHIND the cloak in draw order.

Memory math (why import must crop): 12 × 3800×2400 RGBA ≈ 435 MB decoded. Never load raw.

### 10.2 Phase 0 — import tool (`MTile.Demo -- --import`)

One-time intake, desktop-only (Demo has the GraphicsDevice for PNG decode/encode — no new
deps; Demo is not part of the web build so KNI portability doesn't constrain it).

`dotnet run --project MTile.Demo -- --import SkeletonAssets/rabbit_and_badger --out SpriteBindings --scale 0.25`

Per source PNG: crop to alpha bounding box (+2 px margin), downscale by the shared
`--scale`, write `SpriteBindings/<char>/<part>.png`, and record `OffsetX/Y` = crop origin
× scale (canvas-space, post-scale) in a generated first-pass `<char>.json`. Also log per
file: alpha coverage % (verifies transparency held), cropped size. A hardcoded (or
sidecar) manifest maps IMG numbers → character/part names per the table above. Pick
`--scale` so the assembled character stands a few hundred px tall (≈2× max in-game draw
height); it's one number, re-runnable.

### 10.3 Phase 1 — format (`Drawing/SpriteBinding.cs`)

`SpriteSkinLayer` gains: `Image` (optional per-layer PNG, sibling-relative like doc
`Image`), `OffsetX`, `OffsetY` (int, position of this layer's cropped image on the shared
canvas, in the same image-px space `ImageToRig` consumes). Back-compat rules:
- Layer has `Image` → mesh/texture from that file; `Color`/mask ignored for it.
- Layer has no `Image` → carved from doc-level `Image` by mask color, exactly as today.
- Doc-level `Image` becomes optional; required only if some layer lacks its own.
Existing bindings (`test_hero`, `player`) must keep loading byte-identically — add a
round-trip serialization test (pure logic, no GraphicsDevice needed).

### 10.4 Phase 2 — bake (`Drawing/SpriteSkin.cs`)

Touch points, all in the constructor path:
- Per-layer texture load (`Texture2D.FromStream` + premultiply per texture — the current
  single premultiply pass moves into the per-layer branch).
- `BuildLayer` meshes from the layer's own pixel array/dimensions (w/h become per-layer),
  and vertex rig-positions become `doc.ImageToRig(pos + layerOffset)`.
- UVs stay relative to the layer's OWN texture (pos / layerW) — offset applies only to
  the rig-space transform, not the UV.
- Dispose already per-layer; `OwnsTexture` = true for per-image layers.
- Handle filtering, MLS bake, per-frame deform, draw loop: UNCHANGED.
- Dual-target rule applies (this file compiles under KNI too): stick to the API surface
  already used (FromStream/GetData/SetData/DrawUserIndexedPrimitives).
- Elbows/knees need nothing special: an arm layer listing `arm_l_*` spans both bones and
  MLS bends it; `HandleStep` 0.25 keeps joints tight. Heads/torso ride near-rigid.

### 10.5 Phase 3 — bind editor (`MTile.Demo/BindGame.cs`)

`--bind rabbit` resolves `SpriteBindings/rabbit.json` (json-first; current png-first path
stays for legacy single-image bindings). Changes:
- Backdrop composites ALL layer images at their offsets (this is the registration check —
  the figure must assemble). Draw dimmed as today when preview is on.
- Layer cycling (Tab is taken by edit mode — use `[`/`]` or number keys): highlight the
  selected layer's mesh outline, dim other layers' backdrops slightly.
- `FitRigToImage` fits over the union bbox of all layers (composite bounds), not one
  texture's bounds.
- Bone assignment per layer stays hand-edited json (written once per character); the
  editor only needs to SHOW which bones a layer owns (header line is enough).
- Pose drag / root drag / Shift+wheel scale / G preview / Space clip playback: unchanged.
- `MTILE_SHOT` screenshot contract already works for headless visual verification.

### 10.6 Phase 4 — wiring

Two bindings: `SpriteBindings/rabbit.json`, `badger.json`. `GameConfig` gets a per-player
binding name (current hardcoded `player.json` becomes the default). Optional cheap add
while there: per-layer `Tint` (far-limb darkening lives in data, not art).

First-pass layer order per character, back-to-front (guesses; fix = reorder the json):
far arm → far leg → torso → near leg → near arm → head. Bones: `arm_r_*` / `leg_r_*` /
`chest,hip` / `leg_l_*` / `arm_l_*` / `head`. Badger: BOTH boots before (behind) the
cloak so the white thigh outlines hide. Rabbit scarf-collar: try torso first; if it should
move with the head, that's a mask-split of the torso image later — don't block on it.

### 10.7 Judgment calls already made (don't re-litigate)

- Shared canvas + shared `ImageToRig` (registration) over per-part placement authoring.
- Import crops/downscales offline; runtime never sees the 3800×2400 originals.
- Near/far + draw order + scarf ownership are DATA guesses, corrected in json after a
  visual pass — not blockers, not code.
- The bind pose itself is authored interactively (local machine) — ship a rough auto-fit
  so the session starts from "adjust", not zero.

### 10.8 Remote / headless notes (GCP dev box)

- Code phases (10.2–10.4, 10.6) + serialization tests: fully headless-safe, `dotnet
  build MTile.sln` + `dotnet test`. The web-build check (`dotnet build
  MTile.Web/MTile.Web.csproj`) also matters here since SpriteSkin/SpriteBinding compile
  under KNI.
- GPU steps (import tool, bind editor, `MTILE_SHOT` screenshots) need a display: on the
  Debian box use `xvfb-run` with Mesa/llvmpipe (`apt install xvfb libgl1-mesa-dri`).
  DesktopGL under xvfb works for offscreen capture; if it fights, run import locally on
  Windows and push the outputs — it's one-shot.
- The INTERACTIVE binding session (dragging joints over the art) is local-only by nature.
- Source assets must be pushed for the remote box (repo is public; PNGs ~6.8 MB, fine).
  Exclude `rabbit_and_badger.zip`.
