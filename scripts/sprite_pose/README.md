# sprite_pose — image-model animation drafts → skeleton poses (experiment)

Hypothesis: LLMs are bad at authoring joint angles, but image models are good at
drawing bodies in motion. So ask an image model for a **sprite sheet in a
color-coded stick format** (OpenPose-style, in-distribution for image models) and
decode it back to rig rotations with code, with an agent checking the mapping.

- `stick.py` — FK + renderer for `Skeletons/biped.json` poses. Near limbs (`_r`)
  warm colors, far limbs (`_l`) cool. `python stick.py <outdir>` writes the
  reference renders, using poses from the pre-wipe clip backup.
- `decode.py` — `python decode.py <sheet.png> <out.png>`: splits a 4×4 sheet,
  classifies pixels by nearest legend color, fits each bone's axis, rebuilds the
  pose at true rig bone lengths, and writes a source|decoded comparison sheet + a
  `.json` of per-frame rotations and flags (backward knee, missing bone).

Needs Pillow + numpy (`python -m venv v && v/bin/pip install pillow numpy`).

## Experiment, 2026-10-19 (`codex exec -m gpt-6-luna`, image generation)

| Request | Inputs | Result (`samples/`) |
|---|---|---|
| E1: 16-frame walk cycle, 4×4 | start pose + legend | `gpt_walk.png` — reads as a walk, colors held. But frames clump (1–4 ≈ 13–16), torso drawn as a blob, **feet added** |
| E2: 16-frame slash, start→end | start/end pair + legend | `gpt_slash_a/b.png` — **convincing**: anticipation, overhead windup, lunge, follow-through, recovery. Ignored the end-pose constraint (drifts back to start) |
| E3: walk as 16×16 px pixel art | start pose + legend | `gpt_walk_pixel.png` — clean and consistent, but arms barely move; too coarse for angles |

Decode (no agent, ~100 lines): the slash decodes nearly frame-perfect
(`decoded_slash_b.png`, zero flags). The walk fails on 11/16 frames — the added
feet make each shin an L, so the shin axis flips 180° (`decoded_walk.png`). The
flags caught every one. That is the case for the agent-in-the-loop mapping step.

### Lessons for the next round
- Say **"no feet, no hands — limbs end in round caps"** and **"thin torso line"**.
- The end-pose constraint was not honored; either drop it or decode-then-blend
  to the authored end pose.
- Ask for **evenly spaced** frames explicitly, or accept clumping and retime after decoding.
- Codex mechanics: put the prompt before `-i a.png b.png` (variadic). Unprompted,
  each run regenerated 4–7 times (15 generations for 3 asks) and E2's delivered
  `out.png` was redrawn by Codex in PIL (discarded; raw generations live in
  `~/.codex/generated_images/`). Prompt for exactly one generation, no code, no saving.

### Prompt used (E2; E1/E3 differ only in the task sentence)
> Task: a 16-frame SWORD SLASH sprite sheet for this stick character (the sword
> itself is NOT drawn — only the body). The attached ref_pair.png shows the START
> pose (left) and the END pose (right). Frame 1 must match the start pose, frame 16
> must match the end pose. In between: anticipation (wind the near arm up and back
> behind the head, body coils slightly), then a fast overhead swing forward and down
> with a lunge, settling into the end pose. Draw in EXACTLY the visual format of the
> attached reference: a flat colored stick figure on a pure white background, side
> view, facing right, no shading … Every bone keeps its color from the attached
> legend … Keep the bone lengths and proportions identical … 4×4 grid of equal
> square cells.
