# Rendering Upgrade Plan — primitives, density fields, and segment metaballs

Status: **proposed** (June 2026). Adds a GPU primitive/mesh layer and a
RenderTarget-based density-field pipeline on top of the current SpriteBatch
renderer, culminating in a pixel-shader **metaball field generalized to line-segment
bones** for blobby/gooey character rendering. Everything here is **render-only and
downstream of `Simulation.Step`** — no sim state, no determinism concern.

## The problem

The entire renderer is one 1×1 white texture (`_pixel`) stretched into every rect,
line, ring, and disc via SpriteBatch ([../Drawing/DrawContext.cs](../Drawing/DrawContext.cs),
[../Game1.cs](../Game1.cs) `Draw`). There are no RenderTargets, no `Effect`s, no
vertex geometry, and the content pipeline builds only `DebugFont.spritefont`
([../Content/Content.mgcb](../Content/Content.mgcb)). That ceiling blocks gradients,
smooth curves, parametric surfaces, and density-based glow — and in particular blocks
the target effect: a **metaball field over the skeleton's bone segments**, rendered as
a single merged gooey silhouette around the character.

## The cross-platform constraint (the load-bearing fact)

The game compiles twice from the same root `.cs`: DesktopGL and KNI/BlazorGL (WebGL).
The relevant capabilities:

- **All non-shader GPU API is portable** — `DrawUserPrimitives<VertexPositionColor>`,
  `BasicEffect`, `RenderTarget2D`, `BlendState.Additive`, `Texture2D.SetData` exist in
  both DesktopGL and KNI. No `#if`, no content-pipeline work.
- **Custom pixel shaders are portable *if* authored for Shader Model 3.0** (`ps_3_0`).
  KNI/BlazorGL caps below SM4: an SM4 `.xnb` throws *"Shader model 4.0 is not supported
  by the current graphics profile 'HiDef'"* at `Content.Load`, but a `ps_3_0` effect
  built through the DesktopGL/OPENGL mgcb path loads on BlazorGL. So we stay on
  `/profile:Reach` + `ps_3_0`.

**Design rule for shaders:** never loop over all bones per-pixel. SM3's ~224-instruction
ceiling and weak dynamic loops make the naive "sum all segments in one pass" approach
fight the hardware. Instead **accumulate the field by additive blending** (one cheap
splat draw per bone) and keep every shader to a handful of instructions.

## Architecture: three layers

```
Layer 1  PrimitiveBatch ......... vertex-colored triangles (gradients, curves, surfaces)
Layer 2  DensityField ........... additive splats -> offscreen RT (point + capsule glow)
Layer 3  Metaball shaders ....... ps_3_0 capsule splat + threshold/colormap composite
```

Layers 1–2 are pure portable API. Layer 3 adds the first custom `Effect`s, kept inside
SM3. Each layer is independently useful and shippable.

### Layer 1 — `PrimitiveBatch` (portable, no shaders)

A thin wrapper over `GraphicsDevice.DrawUserPrimitives<VertexPositionColor>` driven by a
`BasicEffect { VertexColorEnabled = true }` carrying the camera matrix (same transform
SpriteBatch gets from `_camera.GetTransform`). Lives beside `DrawContext` (which keeps
owning sprites/text/UI). Buffers vertices, flushes on `End()` / state change.

Unlocks with zero shader work:
- **Gradients** — per-vertex color interpolates across triangles/quads.
- **Bezier / parametric curves** — evaluate the curve on CPU into points, emit a
  triangle-strip ribbon (width + color taper along arc length). Precedent: the
  Catmull-Rom tessellation already in [../Drawing/Trail.cs](../Drawing/Trail.cs), but
  into real triangles instead of stretched pixels.
- **Parametric surfaces** — tessellate the `(u,v)` grid into a vertex-colored triangle
  mesh.

### Layer 2 — `DensityField` (portable, no shaders)

An offscreen `RenderTarget2D` that holds `F(p) = Σ kernelᵢ(p)` — the field is *summed by
the blend unit*, we never compute the sum on the CPU or in a per-pixel loop.

1. `SetRenderTarget(field)`, clear, `Begin(blendState: BlendState.Additive)`.
2. **Point splat:** draw a pre-baked radial-falloff sprite per particle, scaled by
   radius/weight, tinted by color. (Drop-in upgrade for the current particle glow.)
3. `SetRenderTarget(null)`, composite the field to screen (additive tint = smooth glow).

The field RT is sized to the **effect's screen-space AABB**, not the whole screen —
cheaper and sharper. This is the same plumbing Layer 3 reuses; the only thing that
changes there is the splat brush and the composite.

**Target-format gotcha (de-risk first):** additive accumulation wants headroom above
1.0, but an 8-bit `Color` target clamps at 1.0 and can band.
- Start with an **8-bit `Color` target**, scale kernels so the iso lands around ~0.5;
  the narrow smoothstep edge (Layer 3) hides banding. Good enough for glow/blob looks.
- If banding shows, move to a **half-float** target (`HalfVector4`). **Spike this on the
  web build early** — KNI targets WebGL2 (where `EXT_color_buffer_half_float` is normally
  available) but half-float RT support on BlazorGL is unverified. Keep desktop on
  half-float regardless if it helps; only the web parity needs confirming.

### Layer 3 — segment metaballs (two `ps_3_0` shaders)

Generalizes point metaballs to the skeleton's **bone line segments**. Field is a sum of
capsule kernels: `F(p) = Σ kernel(distToSegment(p, aᵢ, bᵢ))`.

**Shader A — capsule splat (`CapsuleSplat.fx`, `ps_3_0`).** Per bone, draw one quad =
the segment's AABB expanded by the kernel radius. The shader gets *that bone's* two
endpoints as `float2` parameters (transformed to field-RT space) and outputs the capsule
falloff, additively blended. Cost ≈ one `distToSegment` (a `dot`, `clamp`, `length`) +
the falloff — ~10 instructions, no loop, no constant array. Scales to any bone count
because it's **one draw per bone**, not one loop iteration per bone per pixel.

```
// core of the splat (sketch)
float t = saturate(dot(p - a, b - a) / dot(b - a, b - a));
float d = distance(p, lerp(a, b, t));     // distance to segment
float k = saturate(1 - d / radius);       // falloff kernel (square/smooth it for shape)
return k * k;                              // additive into the field RT
```

**Shader B — threshold + colormap (`MetaballComposite.fx`, `ps_3_0`).** Full-screen (or
AABB) pass over the field RT: `smoothstep(iso - w, iso + w, F)` for an antialiased
iso-edge, then a ramp for core-vs-rim color. One texture fetch + `smoothstep` + ramp —
trivial. This pass is what makes neighboring capsules read as one merged gooey silhouette.

**Skeleton hookup.** Bones are already segments: [../Drawing/SkeletonPose.cs](../Drawing/SkeletonPose.cs)
`ComputeWorld` gives per-bone world transforms, and [../Drawing/SkeletonRenderer.cs](../Drawing/SkeletonRenderer.cs)
already walks parent→joint segments. Transform each endpoint pair by the camera, feed the
splat draws. The metaball renderer becomes an *alternative* skeleton presentation
alongside the existing stick-figure one — `Skeleton`/`SkeletonPose`/[../Animation/CharacterAnimator.cs](../Animation/CharacterAnimator.cs)
are untouched.

## Content pipeline changes

`.fx` files build through the existing `Content.mgcb` with the effect importer/processor,
staying on `/platform:DesktopGL` + `/profile:Reach`, compiled `ps_3_0`. Each host already
copies built content to its output (Web → `wwwroot/`), so no per-host wiring beyond adding
the `#begin` blocks. Loaded in `Game1.LoadContent` next to `DebugFont`.

## Work breakdown

| # | Change | Files | Portable? |
|---|---|---|---|
| 0 | **Spike:** confirm half-float RT loads/blends on the web build; pick field format | throwaway | verifies web |
| 1 | `PrimitiveBatch` (DrawUserPrimitives + BasicEffect, vertex colors) | `Drawing/PrimitiveBatch.cs` (new), `Game1.cs` (construct + camera matrix) | yes |
| 2 | Bezier/parametric curve + surface helpers on top of PrimitiveBatch | `Drawing/Primitives*.cs` (new) | yes |
| 3 | `DensityField` RT + baked radial kernel + composite; port particle glow to it | `Drawing/DensityField.cs` (new), `Game1.cs` Draw | yes |
| 4 | `CapsuleSplat.fx` + `MetaballComposite.fx` (`ps_3_0`); add to `Content.mgcb`; load in `LoadContent` | `Content/*.fx`, `Content/Content.mgcb`, `Game1.cs` | yes (SM3) |
| 5 | `SkeletonMetaballRenderer` — per-bone capsule splats from `SkeletonPose`, threshold composite | `Drawing/SkeletonMetaballRenderer.cs` (new) | yes |

## As-built gotchas (discovered during implementation)

- **SpriteBatch does NOT auto-set `MatrixTransform` on custom effects** in this
  MonoGame/DesktopGL build. A custom SpriteBatch effect whose VS does
  `mul(input.Position, MatrixTransform)` will collapse all geometry to nothing unless you
  set the parameter yourself: `fx.Parameters["MatrixTransform"].SetValue(
  Matrix.CreateOrthographicOffCenter(0, vpW, vpH, 0, 0, 1))` over the **current render
  target's** viewport before `Begin`. This silently produced empty render targets and was
  the single hardest bug in Layer 3. (Setting it manually is also more portable — works
  whether or not KNI auto-sets it.)
- **`DrawUserPrimitives` + a custom world `TEXCOORD` varying does not interpolate** on the
  MGFX/GL backend (it collapses to a constant per primitive). The splat therefore renders
  through SpriteBatch and reconstructs world position from SpriteBatch's reliable 0..1 UV
  plus per-bone `WorldMin`/`WorldSize` uniforms.
- **Use pure-additive blend (`One,One`), not `BlendState.Additive`** for density
  accumulation — `BlendState.Additive` premultiplies source by its own alpha, accumulating
  `f²` instead of `f` and crushing the field to thin cores.
- **Render targets default to `DiscardContents`** — the mid-frame target switch wipes the
  already-drawn scene on rebind. Set the backbuffer to `PreserveContents` via
  `GraphicsDeviceManager.PreparingDeviceSettings` (and on any manually-created target that
  the glow pass rebinds, e.g. the screenshot target).
- **The effect compiler strips unused parameters** — guard every `Parameters[name]?` with
  null-safe setters so debug shader variants don't NRE.
- **Spike #0 resolved:** 8-bit `Color` field shows no banding at the iso edge; no
  half-float target needed for the blob/glow look.

## Risks / open questions

- **Half-float RT on BlazorGL** — the one real unknown; gated by spike #0. Fallback is an
  8-bit field with iso ≈ 0.5.
- **`DrawUserPrimitives` perf on WebGL** — fine for our volumes, but batch aggressively
  (one flush per material/state) rather than per-shape.
- **mgcb effect build under KNI's content builder** — verify the `ps_3_0` `.xnb` the
  DesktopGL path emits is the one the web build loads; the SM4 trap is exactly here.
- **Premultiplied alpha** — the font uses `PremultiplyAlpha=True`; keep the density
  composite's blend state explicit so additive glow doesn't fight premultiplied sprites.

## Rollout order

Spike #0 → Layer 1 (1–2) → Layer 2 (3) → Layer 3 (4–5). Each step builds clean via
`dotnet build MTile.Core.csproj`; validate the web build after #0 and after #4 (the two
points where platform behavior can diverge).
