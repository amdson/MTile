---
name: sprite-anim
description: >-
  Use when asked to generate/draft an animation clip from a description ("make a
  heavy overhead slash", "draft a better air hammer") for any MTile rig. An image
  model (GPT via codex) draws the move as an 8-frame colour-coded stick sheet with
  dots at hands, feet, blade tip, hip and neck; code detects those joints, a
  hard-masked solver fills the rest, you fix only flagged frames by editing JSON,
  then the draft is assembled (moving com, GPT's timing) into SkeletonDrafts/<rig>/
  and verified in-engine. You never author joint angles yourself. Wiring the clip
  into the game is out of scope.
allowed-tools: [Read, Write, Edit, Bash, Glob, Grep]
shell: bash
---

# sprite-anim: one-shot animation drafts

Code: `scripts/sprite_pose/` (`sprite_anim.py` is the CLI). Design + history:
`Plans/SPRITE_ANIM_SKILL_PLAN.md`, `scripts/sprite_pose/README.md`.

**Setup (once per checkout), from the repo root:**
```bash
python3 -m venv .probe/venv && .probe/venv/bin/pip install pillow numpy scipy
dotnet build MTile.Probe && dotnet build MTile.Demo        # verify needs both
SA=".probe/venv/bin/python scripts/sprite_pose/sprite_anim.py"
```
Run every command from the repo root. A run lives in `.probe/sprite/NAME/` (gitignored).

## Budget — read first

GPT image calls are scarce (the user's budget is low). `gen` makes **exactly one**
`codex exec -m gpt-6-luna` call and caches the image as `sheet.png`; never loop it.
Fix problems in `state.json`, not by regenerating. Regenerate only when the sheet is
unusable as a whole (wrong move, broken format in most frames) — at most once per clip
without asking the user.

## The workflow

**1. refs** — the inputs GPT gets, all rendered from the rig by code:
```bash
$SA refs --run NAME --rig biped --from backup:idle@0 --weapon \
         --markers ends_torso --example backup:groundslash3
```
- `--from CLIP@T` is the start pose (`idle@0` = live pool, `backup:idle@0` = the
  pre-wipe tarball; the live library may be rest-pose stubs).
- `--weapon` draws a violet blade from the near hand (decoded into a `knife` bone). Omit it
  for unarmed moves.
- `--markers ends_torso` is the tested format: filled dots at each hand and foot (in the
  lower bone's colour), a violet blade-tip dot, a **pink hip dot** and a **brown neck dot**.
  Elbows/knees get no dot (read from where two stroke colours meet).
- `--example CLIP` renders a DIFFERENT authored move in the exact format as `example.png`;
  GPT copies the style from it. Always pass one.

**2. gen** — one GPT call, 8 frames on a 4×2 grid:
```bash
$SA gen --run NAME --frames 8 --move "<description>"
```
Write the move like a director: anticipation, the strike, follow-through, what legs and
torso do. Add "the far arm only moves a little for balance" and, for attacks, "end at
the follow-through — do NOT return to the starting pose". For airborne moves say the feet
never touch the ground line. Then LOOK at `sheet.png`: 4×2 grid, legend colours, the dots
present, the move readable and with impact.

**3. detect** — the drawn dots become FIXED joints in `state.json`:
```bash
$SA detect --run NAME          # -> state.json, joints_review.png
```
Expect "11 joints found" per frame (10 without `--weapon`). Look at `joints_review.png`:
every label should sit on its dot / stroke junction.

**4. solve** — fixed values are kept exactly, only free values are optimised (per frame,
warm-started from the previous one):
```bash
$SA solve --run NAME           # -> state.json (solved), decoded.json, decode_overlay.png
```
Read `decode_overlay.png` for EVERY frame (fitted rig in black over GPT's drawing), not
just flagged ones.

**5. Fix flagged frames — the agent's job.** Edit `.probe/sprite/NAME/state.json`, then
re-solve only those frames: `$SA solve --run NAME --frames 3,5`. A frame's state:
```json
{"root":  {"pos": [212, 245], "fixed": true},
 "bones": {"arm_r_upper": {"tip": [270, 230], "fixed": ["tip"]},
           "arm_r_lower": {"rot": -0.4, "stretch": 0.8},
           "leg_l_upper": {"hidden": true}},
 "knife": {"tip": [366, 422], "fixed": ["tip"]},
 "reject": false}
```
A bone's key is its TIP (`arm_r_upper` = near elbow, `arm_r_lower` = near hand,
`leg_*_upper` = knee, `leg_*_lower` = foot, `chest` = neck; `root` = hip; `_r` = near,
`_l` = far). Allowed edits, each with a stated reason (add a `"note"`):
- **move a mark**: set `tip` to the right pixel (read pixels off `cells/NN.png`;
  `joints_review.png` is drawn at 2× — halve its coordinates);
- **free a mark**: remove `"fixed"` — the solver then places that joint by IK (use it when
  a mark is right but GPT drew that bone out of proportion);
- **hidden**: `"hidden": true` for a bone with no usable pixels (stays smooth with its
  neighbours);
- **fix a rotation/stretch** only when you're certain of it (e.g. a forearm pointing at
  the camera → `"stretch": 0.5, "fixed": ["stretch"]`);
- **reject** the frame (`"reject": true`) when it is unusable — its neighbours carry the pose.

Flags: `conflict` — a fixed mark is impossible for that bone (too far/near: a misplaced
mark, or GPT drew the bone out of proportion → free it); `joint_limit` — an elbow/knee
outside its range; `poor_fit` — a free bone far off its stroke; `jump` — a bone flipped
since the previous frame.

**Rules for the loop (don't chase numbers):** there is no score to optimise. A frame is
done when its overlay matches the drawing and it has no flags. At most two edit rounds per
frame, then reject it. Never pin a joint onto a GPT drawing error just to make it fit —
the clip should show the pose GPT *meant*.

**6. assemble** — the clip:
```bash
$SA assemble --run NAME --duration S [--loop] [--contacts]
```
Defaults: every frame becomes an evenly spaced key (GPT's timing); light temporal smoothing
(`--no-smooth` to skip); a **moving com** — `Motion: Track` + per-key `body_path` (the hip
measured against the planted foot) + ground guide, with the per-key `com` anchor sinking
crouches (`--no-motion` for a stationary clip). Opt-ins: `--key-tol 0.12` (key
reduction), `--steep-limit 7` (stretch intervals to satisfy the digest's STEEP rule),
`--keep A-B` (trim frames). Output: `.probe/sprite/NAME/NAME.json` and
`SkeletonDrafts/<rig>/NAME.json` (`Type: "Draft"`; outside `SkeletonStates/`, so no host
copies or loads it).

**7. verify** — `$SA verify --run NAME`: copies the clip into the pool as `zzdraft_NAME`
only for the command, runs Probe `digest` (→ `digest.txt`) and `motion` (→ `motion.txt`)
and MTile.Demo `--strip` (→ `engine_strip.png`, hip-pinned so travel isn't visible), then
deletes the copy. Pose flags (recurvatum etc.) must be 0. STEEP lines are informational —
that rule is calibrated for walk cycles, not attacks.

**8. report** — `$SA report --run NAME` → `.probe/sprite/NAME/index.html`. Hand the user
that path, the number of GPT calls and agent edits, and your verdict.

## Out of scope (leave for the user)

Binding `Type` to an ActionState, `Region` (recommend `UpperBody` for attacks), promoting
into `SkeletonStates/<rig>/`, ClipBindingTests, attachment `FrameTimes`/trail tuning. Never
hand-edit joint angles in the clip to "improve" it (memory: no manual limb angles) — fix the
frame state, or regenerate.

## Known limits

- Tested on 5 ground slashes + a lunge on `biped`; locomotion loops, airborne moves and
  `biped_rabbit` are less proven (the rabbit's struts/feet hold the start pose; run Probe
  `bakeyaw` on strutted drafts).
- GPT's proportions vary between frames; `solve` reads a per-sheet proportion estimate, so a
  pose drawn very differently (a standing frame among lunges) can come out slightly short or
  long — check the first and last frames.
- `body_path` travel is what GPT drew relative to the planted foot (e.g. it may lunge by
  dropping the hips back instead of stepping forward).
- Older paths still in the CLI (`decode`, `joints`, `--markers dots|shapes|ends`) are the
  pre-dot workflow; use `detect` + `solve` with `ends_torso`.
