# sprite_pose — image-model animation drafts → rig clips

The engine behind the `/sprite-anim` skill (`.claude/skills/sprite-anim/SKILL.md` is the
how-to; `Plans/SPRITE_ANIM_SKILL_PLAN.md` the design). An image model draws a move as a
color-coded stick-figure sprite sheet; this code decodes it into rig rotations, an agent
checks/corrects the mapping, and the result is assembled into a draft clip and verified
in-engine. LLMs never author joint angles.

| File | Role |
|---|---|
| `sprite_anim.py` | CLI: `refs` · `gen` · `decode` · `points` · `assemble` · `verify` · `report` |
| `fit.py` | Decoder: pixel observation → whole-skeleton least-squares fit at TRUE rig lengths, hard joint limits, wrong-end escapes, diagnostics |
| `rig.py` | Rig-agnostic sprite profiles, FK (with `Stretch`), stick renderer, legend |
| `bench.py` | Zero-GPT decoder benchmark: authored poses rendered clean / GPT-degraded, decoded, scored |
| `profiles/<rig>.json` | Auto-derived sprite profiles (drawn / held / derived bones, colors); hand-editable |
| `prompts/`, `samples/` | The 2026-10-19 first experiment (prompts, GPT sheets, the old decoder's walkthrough images) |

Python 3 + Pillow, numpy, scipy. Runs live in `.probe/sprite/<name>/` (gitignored);
drafts land in `SkeletonDrafts/<rig>/` (deliberately outside `SkeletonStates/`, which the
Desktop and Web hosts copy recursively).

## Decoder benchmark (`bench.py --n 24`, biped, authored poses from the clip backup)

| Mode | Median bone error | 90th pct | Poses with a bone off > 45° | Broken poses left unflagged | Knife angle |
|---|---|---|---|---|---|
| clean render | ~1.6° | ~6° | 2/24 (curled tumbles) | 0 | ~1° |
| GPT-like degraded¹ | ~4° | ~16° | 5–6/24 (occluded climb/tumble poses) | 0 | ~3.5° |

¹ bone lengths ×0.8–1.25, thick torso blob, feet stubs, color drift ±25, blur + JPEG, scale/offset jitter.
(Numbers from the run before the final solver changes; see the plan's status section for the latest.)

## Findings that shaped the design

- **The first experiment's decode failures were anti-aliasing, not feet**: keep each
  color's largest connected blob.
- **Fit the whole skeleton at true lengths**, not bone by bone: connectivity is exact
  by construction, and GPT's proportion drift becomes a residual instead of a pose error.
- **Head = thickest disc at an END of the dark shape, measured on all ink** (an arm
  crossing the head is still ink); also try the torso reversed. Fixed every upside-down decode.
- **Joint limits must be hard bounds** (elbow ≤ ~150°): a hidden upper arm otherwise
  decodes as an impossible fold. Bounding turns that into a fold *at* the limit, so
  `at_limit` flags it — a bound must never silently convert a flagged error into an
  unflagged one.
- **The digest's STEEP gate caps a clip at ~7–8 rad of travel per bone**; a full
  windup + strike + return rarely fits. Key reduction (Douglas–Peucker) removes the
  decoder's jitter in holds; trimming (`--keep`) and prompting for "end at the
  follow-through" do the rest. Retiming to satisfy STEEP slows the big swings — timing is
  the weakest part of a draft.
- **Codex**: prompt before `-i` (variadic); demand exactly one generation, no code;
  `-s read-only`; collect images by session id (parallel-safe).
