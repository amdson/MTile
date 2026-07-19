# Clip backlog — animations still to author

Derived from the actual state/action inventory (Movement*.cs, ActionStates.cs) vs. the
24 clips in `SkeletonStates/`. Action overlays auto-bind by Type name (= the ActionState
class name), so those need **no code**; movement clips marked ⚙ need a small wiring
change (AnimTag / SelectClip) like CrouchWalk got. Author with the probe command
workflow (`.claude/skills/anim-probe/SKILL.md`).

## High — visible constantly or an explicit placeholder today

- [x] **hang** ⚙ — done. `hang.json` (FullBody, loop, 3 keys, 2.0s): both hands authored
  onto the ledge corner at rig-local (15.8, −29.8) — the corner's fixed offset from the
  body center, since the hang spring holds the body at (corner.X − wallDir·Radius,
  corner.Y + Radius) and `Radius/scale = 9.5/0.6 ≈ 15.8`. That fixed offset is why NO grip
  pin was needed: the clip alone lands the hands on the real edge. Legs dangle with a slow
  sway; the sim spring supplies the body motion. `LedgeGrabState.Enter` now pins
  `Facing = _wallDir` (as `WallSlidingState` does) so a drop-in grab can't clutch backwards.
- [x] **hitstun** ⚙ — done. `AnimTag.Stunned` + `StunnedState.AnimationTag` + a SelectClip
  branch placed BEFORE the grounded speed checks (knockback slides the body through the walk
  band, which would otherwise hide the flinch). `hitstun.json` (FullBody one-shot, 4 keys,
  0.35s): idle → torso/head thrown back with arms flung behind (lean −5.5) → forward settle
  → idle. Feet stay planted — the rig's leg reach (19.25) can't step the brace foot further
  back than idle's while staying on the ground, so the flinch is torso/arm-driven.
- [x] **tumble** ⚙ — done (calibration batch). FullBody 5-key loop, hip-rotation wobble
  (partial, since raw radians can't loop a full 2π spin) + windmilling arms. Wiring:
  `AnimTag.Tumble` + `TumbleState.AnimationTag` + a `SelectClip` branch that MUST precede
  the generic `!Grounded → Jump/Fall` return (Tumble is a strict subset of airborne).
- [x] **guard** — done (calibration batch). `guard.json` UpperBody `OffRegionWeight=0.4`,
  2s near-static loop, both hands raised head/chest-front, staggered (knife arm higher).
- [x] **duckunder** ⚙ — done (calibration batch; new, was not in this backlog). Still
  crouch under a low ceiling: head tucked, back near-horizontal, free hand braced
  overhead. Wired via `CharacterAnimSample.LowCeiling` (reuses the `CeilingChecker` query
  CrouchedState already does) + `SelectClip` branch under `AnimTag.Crouch`. Moving
  variant (`duckwalk`) deliberately out of scope; bracing hand is authored, not
  IK-pinned to the real ceiling height — both noted as future polish.

## Medium — actions that currently play with no overlay

- [x] **blockready** — done (batch). 4-key building gesture, brace 0.3.
- [x] **blockeruption** — done (batch). Two-hand gesture sweep + erupt, brace 0.3. NOTE: BlockEruptionAction has no OverlayDuration override, so this clip's Duration (0.6) paces real gameplay.
- [x] **beam** — done (batch). Charge gather -> right-arm extend-and-lock hold.
- [x] **grenade** — done (calibration batch). Raised overhand lob, 5 keys phased like
  `energyball` (a true over-shoulder cock-back trips the STEEP digest flag on this rig).
- [x] **lobbedarea** — done (batch). Heavy raised-overhand charge + ballistic release, 10 keys over the 1.8s charge.
- [x] **grab** — done (batch). Two-arm seize hold + throw fling.

## Low — polish on movement that currently reuses Jump/Fall

- [x] **walljump kickoff** ⚙ — done (batch): `walljumpkick.json`, AnimTag.WallJump wired.
- [x] **doublejump flip** ⚙ — done (batch): `doublejumpflip.json` (hip-lever tuck), AnimTag.DoubleJump wired.
- [x] **run turn / skid** ⚙ — done (batch): `runturn.json` one-shot; SelectClip plays it when grounded, speed > RunSpeedThreshold, velocity opposes facing.
- [x] **run (edit)** — done (batch): per-keyframe com.Y profile raised at the two flight
  apexes (zero bone edits — choreography/cadence untouched, RealRunJson passes).
- [x] **land** ⚙ — done. Procedural squash RETIRED (was a hack): touchdown arms a
  0.25s `LandTime` window; a landing settling into the Idle band plays `land.json`
  (deep absorb → rise, final key == idle exactly). Moving landings keep their
  locomotion cycle by design — a run-land stumble clip would be a future add, not a
  squash revival. Uses a per-key com profile to sink the body with feet planted.
- [x] **ledgejump / dropdown** — done. `ledgejump.json` (coiled load → forward-driving
  spring off the lip, 4 keys) + `dropdown.json` (deliberate compact slip-off, final key
  == fall@0 for a popless handoff), AnimTag wiring for both.

## Already covered (no work)

idle, walk, walkback, run, **crouchwalk (new)**, crouch, jump, fall, wallslide, vault +
vaulthands, all 9 slashes (ground 1–3, air 1–2, airturn, crouch, grabbed, guardretaliate),
stab, pulse, energyball, wave. `NullAction`/`ReadyAction`/`RecoveryAction` are
no-overlay by design.
