# Clip attachments and the rabbit knife

The rabbit's nine slash clips now render a short, single-edged white knife. The
PNG strip forms and dissolves the blade; its skeleton clip supplies the swing.
A fading ribbon spans the blade's recent base/tip positions in world space.
Stab and charge effects retain their existing presentation. Damage and reach
still come from the simulation; this attachment is cosmetic.

## Authoring

Open the editor with the rabbit skin:

```sh
dotnet run --project MTile.Demo -- groundslash1 --usebind rabbit
```

1. Hover the joint at the end of an arm and press **E**. Enter `knife` (or another
   effect catalog name), then Enter. On an existing attachment, E edits its name.
   An existing zero-length socket is preferred over the hand at the same position.
2. Scrub to the desired start and press **U**, then to the end and press **I**.
   These edit the attachment most recently selected with E. Its window is shown
   below the pose timeline. Space plays the clip with a trail; scrubbing shows the
   blade at the sampled time without stale trail history.
3. **Shift+E** over the joint removes its attachment. **Ctrl+S** saves.

To author an independent blade orientation, attach to a clip-local child bone
and animate that bone's rotation. The existing rabbit slash clips already have
a zero-length `knife` bone under `arm_r_lower`, with zero local rotation so the
blade points along the forearm. Directly attaching to an arm
joint instead follows that arm's local +X axis. Bone naming alone does not spawn
an effect: the clip's explicit attachment tag enables it.

The equivalent clip JSON is:

```json
"Attachments": [
  { "Bone": "knife", "Effect": "knife", "Start": 0, "End": 0.64 }
]
```

`Start` and `End` are normalized **clip** times, with an exclusive end. Rabbit
slash windows end by `1 - SettleShare`, so the blade is gone when recovery starts.
The ground combo finishes dissolving earlier, during follow-through.
The shared strip runs once across this window by default. Optional `Scale` (default 1) and
`Rotation` (radians, default 0) adjust its size and local direction in JSON.
Changing `SettleShare` later does not automatically move an existing window;
edit its end as appropriate. New editor tags default to the current swing end.

The ground combo also authors optional JSON controls:

- `FrameTimes`: one normalized clip time per PNG frame. This holds full blade
  frames through the hit window instead of dissolving at a uniform playback rate.
  Times must be ordered and inside `[Start, End)`; invalid schedules fall back
  to uniform playback. Adjust this list too when changing the outer window.
- `TrailStart` / `TrailEnd`: a separate clip window for emitting the trail;
  omitted values use the attachment window. Existing trail samples still fade.
- `TrailWidth`: outer fraction of the blade that paints the ribbon (default 1).
  The ground combo uses 1.0 so the streak spans the full knife length.
- `TrailOpacity`: multiplier for this attachment's trail (default 1).

These settings are currently authored in JSON; the editor previews them.

**Progress curve.** `Progress` is an optional `AnimCurve` (the same spline type as a contact's
weight) from window fraction u to strip progress. Null = linear. When set it **supersedes
`FrameTimes`**; the window gate and trail fade stay linear in u. In the editor: endpoint menu →
the element's entry → **Progress curve**. The first time, the curve is seeded from `FrameTimes`
(monotone tangents, so nothing changes on screen) or the identity, and the curve window opens
whenever that element is selected. Edit keys and tangents there, the same way as the weight curve; the curve's
shape is drawn under the element's timeline bar. **Remove curve** falls back to `FrameTimes`.
Like contact curves, it lives on the window's own domain, so retiming the window stretches the ease.

## Shared asset

[`knife.png`](../Assets/AnimationEffects/knife.png) is a transparent RGBA strip:
eight 320 × 160 cells, 2560 × 160 overall, fixed pivot (16, 80). The blade is
about two rabbit forearms long. [`knife.json`](../Assets/AnimationEffects/knife.json)
defines frame dimensions/count, pivot, rig units per pixel, per-frame tip distance,
and trail lifetime (currently 65 ms). PNG pixels use straight alpha on disk;
the renderer premultiplies them once on loading.

To add another effect, put `<name>.json` and its horizontal PNG strip in
`Assets/AnimationEffects`, then enter that name with E. A strip animates its own
shape; the final skeleton pose supplies its position, orientation, stretch, and
facing. Missing assets are logged once and leave the legacy slash fallback enabled.
Definitions and textures are cached: restart the host after changing an asset.
The desktop asset glob and the web staging target include these resources.

## Runtime behavior and limits

`CharacterAnimator.SampleAttachments` exposes the base clip and bound overlays
using their actual sampled times, including the action/recovery remapping.
An overlay replaces a matching bone/effect binding and uses that bone's blend
weight. Unbound overlays emit no attachments even while their poses fade out.
`SpriteAttachmentRenderer` applies the final bone affine transform, so the knife
stays on the visible hand and mirrors its single cutting edge correctly.

Trail history is per actor and binding. Clip switches, time rewinds, facing flips,
reactivation after interruption, and large position jumps break the sweep.
Old samples fade after the attachment ends. The renderer interpolates the ribbon
between pose samples to avoid a visibly polygonal arc on fast swings.

The game draws attachments for primary and secondary player animators. It suppresses
the old slash glow when a `knife` attachment asset is available on that clip.
As with the existing glow, recorded-take playback currently omits these effects:
recordings restore poses, not attachment clocks/history. Raw clip preview/export
does support them. Effects draw in front of the character; per-skin-layer occlusion
is not part of this initial attachment implementation.

## Verification and previews

The focused suite covers real rabbit slash timing and recovery, interrupted
overlays, mirroring, mask/duplicate handling, serialization, trail discontinuities,
and PNG transparency/cell borders. Existing composition, binding, and action overlay
tests are included. The editor and exporter build and render through MonoGame.

```sh
dotnet test MTile.Tests/MTile.Tests.csproj --no-restore --filter 'FullyQualifiedName~AttachmentTests|FullyQualifiedName~SkeletonCompositionTests|FullyQualifiedName~ClipBindingTests|FullyQualifiedName~ActionOverlayTests'
dotnet run --project MTile.Demo -- --strip /tmp/knife-preview.png groundslash1 --usebind rabbit --frames 24 --columns 6 --size 320 --end 1 --trails
```

Omit `--trails` for isolated attachment poses; add `--facing -1` to check mirroring.
Trail exports sample the raw clip at its authored Duration; they are a visual
preview, not a simulation replay of the action/recovery timing.

## Art provenance

Generated with the built-in imagegen tool, then mechanically prepared with user
authorization: remove background contamination, isolate the eight frames, align
the grip points, pack the strip, and attenuate the final three frames' alpha.
The final generated source already carried alpha; it was preserved for the
prepared strip. Earlier nail-shaped drafts were discarded after the user's
clarification: the knife has one cutting edge; the reference is for the trail.

Final generation prompt:

```text
Use case: stylized-concept
Asset type: 2D game animation sprite strip, eight frames in one horizontal row.
Draw an actual SINGLE-EDGED KNIFE BLADE forming from white energy, not a nail, not a symmetric wedge, not a sword. A short utilitarian hunting/paring knife: clearly asymmetric silhouette with a STRAIGHT FLAT SPINE along its TOP, parallel-sided body through the first two thirds, then a downward sloping drop point; ONE curved sharp cutting edge along the BOTTOM sweeping upward into the point. A tiny ricasso/notch at the base makes it read as a real knife blade. No handle or guard; the character's hand will hold its base. Solid white silhouette, faint pale gray single bevel inset along the lower edge. Recognizable actual knife anatomy. Blade length about 5 times its broadest width. No stars, no aura, no flames, no decorative markings.
Eight frames left to right: tiny emerging base/shard; 40% formed knife; 80% knife; complete knife; same complete knife; complete knife thinning/fading; half-dissolved knife; tiny faint remnant. In all frames the base is at the left and the point faces right, spine upper, cutting edge lower. DO NOT animate rotation, the skeleton supplies the swing. Base anchor stays in identical location relative to each equal-width cell. All eight frames on ONE horizontal row with large clear gaps and the same vertical centerline, no touching sprites.
Use a perfectly solid BLACK background (#000000) for reliable extraction to alpha. No gradients in background, no checkerboard, no text, no cell borders. Smooth hand-drawn game art, crisp outline, very subtle bevel shading only. Requested canvas 2048x512. Do not draw a Hollow Knight nail; this is a short single edged knife, and the game will add a white motion trail separately.
```
