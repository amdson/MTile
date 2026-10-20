# Sprite-anim skill: one-shot animation drafts from an image model

Status: **built (A–G), 2026-10-19.** Code: `scripts/sprite_pose/` (README has the findings).
Skill: `.claude/skills/sprite-anim/SKILL.md`. Test target: three ground slashes on `biped`
(`SkeletonDrafts/biped/slash_{overhead,sweep,rising}.json`), each one GPT call, all passing
Probe `digest` with 0 FLAG lines.

### Deviations from the plan below

- **Decoder stayed in Python** (`fit.py`), not ported into Probe. Engine-side checks still
  run through Probe (`digest`) and MTile.Demo (`--strip`) via `sprite_anim.py verify`.
- **Profiles live in `scripts/sprite_pose/profiles/`**, not `Skeletons/` (the game's rig dir).
- **Drafts land in `SkeletonDrafts/<rig>/`**, not `SkeletonStates/_drafts/`: the Desktop and
  Web hosts copy `SkeletonStates/**` recursively (and Web writes a manifest per subdir).
- **Added**: hard joint-limit bounds + `at_limit` flag; key reduction; STEEP-aware retime;
  `--keep` trimming; per-key `com` grounding anchor (K = sole−com.Y of the rig's idle, 40.06
  on biped — the anim-probe skill's 31.64 does not apply to this rig); `points` helper for pins.

### Open

- **Timing.** Retiming to satisfy STEEP gives big swings more phase, so strikes lose snap
  (overhead chop: windup 0.22–0.79, strike 0.79–0.96). Options: let attacks exceed STEEP
  (it is a readability convention, the engine plays them), prompt for smaller swings, or a
  timing pass that keeps the strike fast and spends the budget on anticipation.
- **Proportions.** GPT draws a shorter torso and lower shoulders than `biped`; raised arms
  read as folded elbows (`at_limit`). A per-sheet "GPT proportion" pre-fit could absorb it.
- The Demo `--strip` renders clips hip-pinned, so the `com` grounding is invisible there;
  only the game shows crouches sinking.
- `biped_rabbit` is profiled and rendered but no clip has been generated on it yet.

Goal: `/sprite-anim "<move>" --rig <rig> --from <clip@t> [--to <clip@t>]` produces a
**convincing first-draft clip** for any rig, with an inspectable record of every step.
An image model draws the motion as a color-coded stick sheet; code decodes it; an agent
checks and corrects the mapping. LLMs never author joint angles.

## Settled decisions

- **The rig is a parameter.** Nothing in the pipeline names `biped` bones. Every rig gets
  a *sprite profile* (below) that drives rendering, the prompt legend, and decoding.
- **Wiring is out of scope.** The skill emits a standalone draft clip into a staging dir
  (`SkeletonStates/_drafts/<rig>/<name>.json`, `Type: "Draft"`). Binding to an
  `ActionState` `Type`, `Region`, registration, and `ClipBindingTests` are done later by
  hand; the skill only *recommends* a Region in its report.
- **GPT budget is scarce.** Generation goes through one wrapper that enforces one image
  per call, caches every result, and never retries on its own (see memory: codex).

## Sprite profile (per rig, auto-derived, overridable)

`Skeletons/<rig>.sprite.json`, generated from `Skeletons/<rig>.json` on first use:

- **drawn bones**: Length ≥ `minDrawLength` (default 4 rig units). Biped: all 10.
  `biped_rabbit`: chest, head, arms, legs — the shoulder/hip struts (2.5–3.5) and the
  0.25-unit feet are **not drawn**.
- **side families**: `_r` = near (warm), `_l` = far (cool); colors assigned per chain depth
  from two fixed palettes, so a rig's legend is stable across runs. Overridable when a rig
  doesn't follow the `_l/_r` convention.
- **undrawn bones**: `hold` (keep bind rotation) or `derive` (filled by an existing Probe
  step — `biped_rabbit`'s struts come from `bakeyaw`, exactly as `port` does today).
- **root + head**: which bone is the root, which is drawn as a disc.

## Pipeline

1. **Reference render** — the start (and optional end) pose from `clip@t`, through the
   rig's FK, in the profile's colors + an auto-generated legend.
2. **Generate** — `scripts/sprite_pose/gen.sh`: fixed prompt template (4×4 = 16 evenly
   spaced frames; limbs end in round caps, no feet/hands; thin torso; weapon as its own
   color when the move has one; exactly one generation, no code, no saving), calls
   `codex exec -m gpt-6-luna`, collects the raw PNG from `~/.codex/generated_images/`,
   caches it beside the prompt.
3. **Decode** — generic tree fit, root outward, from the profile. Joint *positions* fitted
   with `PoseIk` least squares at the rig's true lengths (not per-bone angles).
   Orientation chosen for joint connectivity + temporal consistency with the previous
   frame. Shoulder/hip from the torso line, not from limb extremes (the raised-arm bug).
   Largest connected blob per color (the anti-aliasing bug).
4. **Flags** — every joint limit (not just knees), joint-gap residual, drawn-length anomaly
   (foreshortening), missing bone, limb teleport between frames (near/far swap), weapon
   missing.
5. **Agent check loop** — reads overlay sheet + flags + `digest`; records decisions in a
   mapping file (`swap near/far`, `occluded → interpolate`, `pin joint`, `reject frame`);
   re-fits. Stops when flags are clear, digest is clean, and the `MTile.Demo --strip`
   render of the clip matches the source sheet.
6. **Clip assembly** — retime clumped frames by pose distance; contacts from soles on the
   ground line (`contact`); `addcom`; `loopback` for cycles; weapon → knife bone / attachment
   angle; `derive` bones via the profile. Written to staging.
7. **Report** — the walkthrough folder (as `walkthrough.py` today) + unresolved flags +
   suggested Region.

## Build order

| # | Work | GPT calls |
|---|---|---|
| A | Sprite profile + generic renderer/legend; render both rigs | 0 |
| B | **Synthetic decoder benchmark**: render the ~45 backed-up clips as sheets, degrade them (anti-aliasing, blur, color drift, scale jitter, added feet), decode, measure angle error vs truth. Regression test for everything after. | 0 |
| C | Decoder hardening (step 3–4) until B passes a set bar; port the decoder into Probe (`probe fromsheet`) so it uses engine FK + `digest` | 0 |
| D | Clip assembly (step 6) + staging output | 0 |
| E | `gen.sh` + prompt template; calibration: ~5 moves, 1 call each, measure format adherence | ~5 |
| F | Mapping file + agent loop; `.claude/skills/sprite-anim/SKILL.md` | 0 |
| G | End-to-end on 2–3 moves per rig | ~6 |

**Checkpoint after C**: the benchmark's angle error decides whether one-shot is realistic.

## Out of scope

Wiring (above); skinned/drawn-character input (a later alternative input via a pose
estimator); cross-rig decoding (decode on the target rig directly — `port` stays the tool
for moving an existing clip between rigs).
