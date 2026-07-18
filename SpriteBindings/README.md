# Sprite bindings — authoring reference

A binding skins the biped rig with a PNG, deformed live by rigid MLS (design:
[Plans/SPRITE_SKIN_PLAN.md](../Plans/SPRITE_SKIN_PLAN.md)). Each binding is a pair of
sibling files in this folder — `<name>.png` (the artwork) + `<name>.json` (alignment +
settings) — plus, optionally, `<name>_mask.png` (the layer mask). Working example:
`test_hero.*`.

## Workflow

```bash
# 1. Drop your drawing here (neutral pose, limbs slightly apart if possible), then align:
dotnet run --project MTile.Demo -- --bind <name>.png
#    drag joints onto the drawing; G previews the deformation; Space plays a clip; Ctrl-S

# 2. Preview against any clip in the animation editor:
dotnet run --project MTile.Demo -- walk --usebind <name>
#    G sprite on/off   W mesh wireframe   X skeleton on/off

# 3. Use in game: game_config.json
#    "DrawPlayerSpriteSkin": true, "PlayerSpriteBinding": "<name>"
```

## Layer masks (color-coded regions)

Layers split the drawing into regions that deform independently — each region is moved
by ONLY its own bones, gets its own mesh (no shared triangles with other regions), and
draws in a fixed back-to-front order. Use them whenever limbs are drawn touching or
overlapping the body; without layers those pixels smear between regions.

**The mask** is a PNG with the exact dimensions of the artwork, painted with one flat
color per region over the sprite's silhouette:

- Every opaque sprite pixel is assigned to the layer whose `Color` is **nearest in RGB**
  at that mask pixel, within `MaskTolerance` (default 90 per channel) — anti-aliased
  mask edges and sloppy borders resolve sanely.
- Everything else — mask-transparent pixels AND pixels matching no color within
  tolerance — falls to the **catch-all** layer (the one declared without a `Color`).
  So painting the mask **directly on a copy of the artwork** works: the unpainted art
  pixels miss every layer color and land in the catch-all. You only paint the regions
  you're splitting off.
- Use loud, far-apart colors (pure `#FF0000`, `#00FF00`, `#0000FF`, `#FFFF00`,
  `#FF00FF`) so nearest-color never surprises you.

**In the json** (order = draw order, first = furthest back):

```jsonc
"Mask": "<name>_mask.png",
"Layers": [
  { "Name": "arm_far",  "Color": "#FF0000", "Bones": ["arm_r_*"] },
  { "Name": "leg_far",  "Color": "#0000FF", "Bones": ["leg_r_*", "foot_r"] },
  { "Name": "body",                          "Bones": ["hip", "chest", "head"] },
  { "Name": "leg_near", "Color": "#00FF00", "Bones": ["leg_l_*", "foot_l"] },
  { "Name": "arm_near", "Color": "#FFFF00", "Bones": ["arm_l_*"] }
]
```

- `Bones`: names from `Skeletons/biped.json`; a trailing `*` is a prefix wildcard
  (`arm_l_*` → `arm_l_upper`, `arm_l_lower`). Empty/omitted → all bones.
- Omit `Mask`/`Layers` entirely for a single-layer binding (whole sprite, all bones).
- Rig bone names, for reference: `hip, chest, head, arm_l_upper, arm_l_lower,
  arm_r_upper, arm_r_lower, leg_l_upper, leg_l_lower, foot_l, leg_r_upper,
  leg_r_lower, foot_r`. Canonical facing is +X; in a drawing facing the viewer,
  pick a convention (e.g. `_l` = image-left) and keep it consistent with the bind pose.

**Seam placement:**

- Cut through **joint lines** (shoulder, hip) — both sides ride the same skeleton
  there, so their motion agrees. A seam through mid-limb shears (the halves are solved
  against different bone sets).
- Hairline crack at a seam in extreme poses? Extend the FRONT layer's mask region a
  few pixels past the seam — the back layer keeps drawing underneath, the front covers it.
- Layers rebake at load: after editing the mask, relaunch the editor/game to see it.

## Settings (json, all optional)

| Key | Default | Meaning |
|---|---|---|
| `MeshStep` | 10 | Grid cell size, image px. Lower (5–8) = smoother joint bends, and gaps narrower than a cell stop bridging. |
| `AlphaThreshold` | 8 | Cells with no pixel ≥ this alpha are dropped. Raise to shed faint glow/halo pixels. |
| `WeightAlpha` | 2.0 | MLS falloff exponent (1 = soft/broad influence … 2 = tight/local). Raise toward 2 if a limb drags pixels that aren't its own; layers are the hard fix. |
| `HandleStep` | 0.25 | Handle spacing along each bone, as a fraction (0.5 = mid+tip, 0.25 = quarters). Smaller = more rigidity near joints. |
| `MaxHandles` | 8 | Per-vertex influence budget (perf knob). |
| `MaskTolerance` | 90 | Max per-channel RGB distance for a mask pixel to claim a layer color; beyond it → catch-all. Raise if brush edges leave slivers; lower if art colors sit near a paint color. |

All of these bake at load — restart to see changes.

## Mask painting on Windows

Krita (free) or Photopea (free, browser): sprite as bottom layer, mask on a layer above
at ~50% opacity, hard/pixel brush. Fastest workflow: magic-wand a limb on the SPRITE
layer, then bucket-fill that selection on the mask layer. Export the mask layer alone
at full canvas size.
