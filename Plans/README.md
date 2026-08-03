# Plans index

Design notes and roadmaps. **Plan headers lie as they age** — several here were written
as "proposed" and shipped months later without the header being touched. Check the source
before trusting any status line, and prefer `CODEBASE_OVERVIEW.md` for what the code
actually does today.

`Archive/` holds superseded plans, kept for rationale only.

## Live work

| Plan | State |
|---|---|
| `CORRECTOR_CONSOLIDATION_PLAN.md` | Phases 1–6 shipped. Open: lever-normalized hinge weighting (§6). The corrector is the actively-tuned area of the codebase. |
| `ANIMATION_SOLVER_PLAN.md` | Through §11.6 Phase 3.5. Open: horizontal `d.x`/ComOffset (vertical only today), JointLimits as a real constraint class, local-SDF non-penetration. |
| `GAME1_REFACTOR_PLAN.md` | Item 1 done (debug overlay, HUD, chunk renderer, cosmetics extracted). ~870 lines of render/HUD still inline. |
| `BOT_AI_PLAN.md` | Not started. `Net/BotInputSource.cs` is still the seeded-random stub. |
| `ANIMATION_POLISH_PLAN.md`, `ANIMATION_DIRECTIONS.md` | Polish items 1–3 done; directions doc is uncommitted thinking. |

## Shipped (kept for rationale)

`BALLISTIC_CORRECTOR_PLAN.md` · `ELECTIVE_REFUSAL_NOTE.md` · `MOVEMENT_NIGHT_PLAN.md`
(items 7+8 landed as prototype — see `Character/ReferencePath.cs`) · `SPRITE_SKIN_PLAN.md` ·
`STAB_AIM_PLAN.md` · `VAULT_HAND_PIN_PLAN.md` · `ANIMATION_LOCOMOTION_PLAN.md` ·
`RENDERING_UPGRADE_PLAN.md` · `ENEMY_CAPABILITY_FRAMEWORK.md` · `TILE_SPROUT_GRAPH_PLAN.md` ·
`ANIM_TAKE_VIEWER_PLAN.md` · `ANIMATION_STRETCH_AND_REFERENCE.md` · `CLIP_BACKLOG.md` (17/17) ·
`ANIMATION_BATCH.md` / `ANIMATION_RETRO.md` (status table inside is a stale snapshot — run
`MTile.Probe -- list` for live clip status).

`ROLLBACK_ROADMAP.md` is **stale**: several unchecked goals are done, and the ECS migration
plus `Net/RollbackSession.cs` supersede parts of it.

## Reference / surveys (not commitments)

`ANIMATION_CODE_STATE.md` · `ANIMATION_SOLVER_OVERVIEW.md` · `COMBAT_AND_CONTENT_ROADMAP.md` ·
`DYNAMIC_PHYSICS_ROADMAP.md` · `MAP_STATE_BRAINSTORM.md` · `LEDGE_PULL_INPUT_MATRIX.md` ·
`BLOCK_ERUPTION_NOTES.md` · `ledge_vault_design.md` (superseded in practice by the corrector
climb family) · `ANIMATION_CLIP_GAPS.md` (mostly closed by `CLIP_BACKLOG.md`).

## Partial / undecided

`ECS_MIGRATION_PLAN.md` (Phase 0 shipped and wired into `Simulation`; later phases open) ·
`ACTION_REDESIGN_PLAN.md` (parser/FSM split landed, no completion record) ·
`TODO_TOP_BULLETS_PLAN.md` (steps 2 and 4 evidenced in tests; rest unverified, bullets still
in `todo.txt`) · `AIR_SLASH_PLAN.md` (A/B/C memo, no option recorded) · `HIT_MOMENTUM_PLAN.md` ·
`PARAMETRIZED_ATTACK_ANIM_PLAN.md` (partly superseded by `STAB_AIM_PLAN.md`).

## Other backlogs not covered here

`todo.txt` (root) · `Animation/anim_todo.txt` · `Animation/TODO.md` · `Character/movement_todo.md`
