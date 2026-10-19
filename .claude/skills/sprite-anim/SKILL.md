---
name: sprite-anim
description: >-
  Use when asked to generate/draft an animation clip from a description ("make a
  heavy overhead slash", "draft a rising uppercut") for any MTile rig. An image model
  (GPT via codex) draws the motion as a color-coded stick-figure sprite sheet; code
  decodes it into rig rotations; you check and correct the mapping, then assemble a
  draft clip into SkeletonDrafts/<rig>/ and verify it in-engine. You never
  author joint angles yourself. Wiring the clip into the game is out of scope.
allowed-tools: [Read, Write, Edit, Bash, Glob, Grep]
shell: bash
---

# sprite-anim: one-shot animation drafts

Pipeline code: `scripts/sprite_pose/` (`sprite_anim.py` is the CLI; `fit.py` the decoder;
`rig.py` profiles/FK/rendering; `bench.py` the zero-GPT decoder benchmark). Design and
history: `Plans/SPRITE_ANIM_SKILL_PLAN.md`, `scripts/sprite_pose/README.md`.

Setup once: `python3 -m venv .probe/venv && .probe/venv/bin/pip install pillow numpy scipy`,
`dotnet build MTile.Probe && dotnet build MTile.Demo` (verify needs both). Below,
`SA=".probe/venv/bin/python scripts/sprite_pose/sprite_anim.py"`, run from the repo root.

## Budget — read first

GPT image calls are scarce (the user's budget is low). `gen` makes **exactly one**
`codex exec -m gpt-6-luna` call per invocation and caches the result; never loop it.
Fix frames with `mapping.json`, `--keep`, or rejection — **regenerate only when the sheet
is unusable as a whole** (wrong format, wrong move, figure broken in most frames), and ask
the user before more than one regeneration per clip. Parallel `gen`s are safe (images are
collected by codex session id).

## Steps

1. **refs** — `$SA refs --run NAME --rig RIG --from CLIP@T [--to CLIP@T] [--weapon]`
   - `--from` is the start pose: `idle@0` from the live pool, or `backup:idle@0` from the
     pre-wipe clip tarball (the live library may be rest-pose stubs — BACKLOG §5.0).
   - `--weapon` draws a violet blade from the near hand and decodes it into a `knife`
     ExtraBone (use it for any armed move).
   - Writes `ref.png` + `legend.png` from the rig's sprite profile
     (`scripts/sprite_pose/profiles/<rig>.json`, auto-derived; edit it for odd rigs).
2. **gen** — `$SA gen --run NAME --move "<description>" [--frames 16]`
   - Write the move like a director: anticipation, the strike, follow-through, what the
     legs and torso do. Add "the far arm only moves a little for balance" (GPT's far arm
     otherwise flails and eats the STEEP budget) and, for attacks, "end at the
     follow-through — do NOT return to the starting pose".
   - Then LOOK at `sheet.png`. Accept if: 4×4 grid, colors per legend, no feet/hands, the
     move is the one asked for and reads with impact.
3. **decode** — `$SA decode --run NAME`. Prints per-frame cost + FLAGS + notes and writes
   `decode_overlay.png` (fitted rig in black over the source; blue = ground line),
   `decode_classified.png`, `decode_rebuilt.png`, `decoded.json`.
4. **Check the mapping (your job).** Read `decode_overlay.png` for EVERY frame, not just
   flagged ones — a wrong-but-plausible fit can be unflagged. Look for: a black bone lying
   off its colored stroke; near/far limbs swapped (black arm on the cyan stroke, etc.); a
   limb pointing out of the wrong end of its stroke; a frame that differs wildly from its
   neighbours. Then fix by writing `.probe/sprite/NAME/mapping.json` and re-running decode:
   ```json
   {"frames": {"7": {"reject": true},
               "5": {"swap": ["arm"]},
               "9": {"occluded": ["arm_l_lower"]},
               "3": {"pin": {"arm_r_lower": [412, 180]}},
               "4": {"flip": ["arm_r_upper"]},
               "6": {"knife": null}}}
   ```
   `reject` drops the frame (neighbours carry it); `swap` exchanges near/far observations
   for that limb kind; `occluded` ignores a bone's pixels and interpolates it over time;
   `pin` forces a bone's TIP to a pixel (cell coordinates — read them off
   `cells/NN.png`); `flip` forces the other orientation; `knife` overrides the blade.
   Flag meanings: `joint_limit` — an elbow/knee bent past its range (often GPT actually
   drew it that way; accept or reject the frame); `at_limit` — a hinge folded to its max
   flexion by the solver's bound, which usually means GPT drew that limb SHORTER than the rig
   (e.g. an upper arm hidden behind the head) and the fit folded it to match — if the drawing
   shows a straight limb, reject the frame (a neighbour carries the pose) rather than pinning
   both ends (two pins at mismatched proportions over-constrain the fit); `poor_fit` — a bone far off its pixels;
   `jump` — a bone turned > 2.5 rad since the last frame (near/far swap?);
   `foreshortened` — the stroke is much shorter than the bone. Notes (`hidden`, `short`,
   `blade:not_found`) are usually harmless.
5. **assemble** — `$SA assemble --run NAME --duration S [--keep A-B] [--loop] [--contacts]`
   - Reduces the 16 frames to key poses (`--key-tol`, default 0.12 rad), clamps small
     joint-limit overshoots, and retimes: GPT's even spacing is kept except where an
     interval would exceed the digest's STEEP gate.
   - It prints the fastest bone's total travel. **The STEEP gate caps a clip at about
     7–8 rad of travel per bone per clip**; a full windup + strike + return-to-idle rarely
     fits. If it reports intervals "still above", trim with `--keep` to the
     anticipation→follow-through span (attacks in this game are short overlays).
   - `--contacts` writes SelfPlant spans for soles on the ground line (locomotion only).
   - Output: `.probe/sprite/NAME/NAME.json` and `SkeletonDrafts/<rig>/NAME.json`
     (`Type: "Draft"`; outside `SkeletonStates/`, so no host copies or loads it).
6. **verify** — `$SA verify --run NAME`: copies the clip into the pool as `zzdraft_NAME`
   for the duration of the command, runs Probe `digest` (→ `digest.txt`) and MTile.Demo
   `--strip` (→ `engine_strip.png`), then deletes the copy. Gate: **0 digest FLAG lines**,
   and the engine strip reads as the same move as `sheet.png`.
7. **report** — `$SA report --run NAME` → `.probe/sprite/NAME/index.html` (inputs, prompt,
   raw output, decode stages, flags table, source vs clip GIFs, engine strip, digest).
   Hand the user that path plus your verdict.

## Out of scope (leave for the user)

Binding `Type` to an ActionState, `Region` (recommend `UpperBody` for attacks in the
report), promoting into `SkeletonStates/<rig>/`, ClipBindingTests, attachment `FrameTimes`/trail
tuning. Never hand-edit joint angles to "improve" a draft (memory: no manual limb
angles) — correct the *mapping*, or regenerate.

## Known limits

- GPT's proportions differ from the rig (shorter torso, lower shoulders): the fit uses
  TRUE rig lengths, so the head circle sits a little off and upper arms show `poor_fit`
  at large shoulder angles. Poses still read correctly.
- Strongly occluded poses (curled tumbles, limbs behind a thick torso) decode worst
  (`bench.py` GPT-like mode: median 4°, 90% < 16°, broken poses are always flagged).
- Undrawn bones (`biped_rabbit` struts and 0.25-unit feet) hold the start pose; strutted
  rigs should run Probe `bakeyaw` on the draft afterwards.
