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
| `INTERNET_READY_PLAN.md` | Phase 1 (Firestore signaling + room codes) **implemented**. Phase 2 (TURN) not started — the blocker for strangers on symmetric NAT. Phase 3 (ship a URL) partial: deployed to GitHub Pages via `scripts/publish-web.ps1`, but the csproj AOT flag is still unflipped. Phase 4 (desync/disconnect handling) not started. |
| `WEB_PVP.md` | Operator guide for running a browser match. **Stale**: written before room codes, so it documents only the copy/paste lobby. |
| `ANIMATION_BINDING_MAP.md` | Reference, verified 2026-08-04. The authoritative map of movement-state → AnimTag → AnimClip → clip file, action class → clip, and reference arcs. Read before renaming anything animation-side. |
| `AUDIO_PLAN.md` | **Proposed, nothing implemented** — greenfield verified 2026-08-14 (no audio code, assets, or `.mgcb` entries). Rollback-safe design: level-triggered (predicate) vs edge-triggered (event + `(frame, id)` dedup) sounds over an `ISoundSource` registry modelled on the move-driver registry, reconciled once per *rendered* frame from `CosmeticUpdateSystem`. DesktopGL/KNI audio parity verified at the API level (§7); browser audio unlock and KNI runtime fidelity are open. Also scopes a fix for the existing duplicate-particles-on-rollback bug at `Game1.cs:315`. |
| `AUDIO_ASSET_LIST.md` | The acquisition companion to `AUDIO_PLAN.md` §9 — ~30 files in tier 1, ~120–150 across all three, with variant counts, durations, sonic character, and search terms per sound. Plus sourcing (Sonniss GDC bundle recommended; **BBC archive is non-commercial, avoid**) and format rules. Nothing sourced yet. |
| `AUDIO_CANDIDATES.md` | Triage of the Sonniss GDC 2026 bundle (301 WAVs) → 88 hand-picked candidates in `Audio/candidates/` (gitignored; re-extract with `scripts/extract-sfx-candidates.ps1`). Two headlines: **no footsteps at all** (a tier-1 gap needing separate sourcing), and **search by acoustic character, not by category name** — the first pass missed every fireworks file while hunting for "rock", and fireworks/hail/fire-crackle are among the better rubble sources here. Character notes are inferred from filenames, not from listening. |
| `BLOCK_THROW_PLAN.md` | **Proposed, nothing built** (2026-08-28). `todo.txt` #2 — the block-grab *throw* half: swipe-and-release velocity, a held ball that survives the release so an undecided peel isn't forfeited, shared textured orb sprite, trail particles, dissipation knob. §4 is the critique of the original sketch; §6 the phase order. |
| `BOT_AI_PLAN.md` | Not started. `Net/BotInputSource.cs` is still the seeded-random stub. |
| `ANIMATION_POLISH_PLAN.md`, `ANIMATION_DIRECTIONS.md` | Polish items 1–3 done; directions doc is uncommitted thinking. |

## Shipped (kept for rationale)

`BALLISTIC_CORRECTOR_PLAN.md` · `ELECTIVE_REFUSAL_NOTE.md` · `MOVEMENT_NIGHT_PLAN.md`
(items 7+8 landed as prototype — see `Character/Corrector/ReferencePath.cs`) · `SPRITE_SKIN_PLAN.md` ·
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

## Item-level backlog

Plans are documents; **[../BACKLOG.md](../BACKLOG.md) is the item-level list** of what's actually
outstanding, with verified status per entry. The old scattered `todo.txt` / `anim_todo.txt` /
`Animation/TODO.md` / `movement_todo.md` files were consolidated into it and deleted.
