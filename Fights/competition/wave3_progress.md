# Wave 3 competition — progress

Coordinator: Claude (Fable 5.1), started 2026-10-01 11:40. Rules: `Plans/FIGHTER_COMPETITION.md`.
Four Claude classes, two bots each, authored in independent sessions; a class's score is the
mean rating of its two bots. At most three authoring sessions run at a time, in order
Haiku → Sonnet → Opus → Fable. Pool: `wave3.pool.json` (Dev visible, Holdout hidden).

## Sessions

| slot | model | status | bot | started | finished | dev score (self-reported) | notes |
|---|---|---|---|---|---|---|---|
| haiku-1 | Haiku 4.5 | submitted | Ironshot | 11:40 | 11:53 | 0.889 ±0.007 (1034-188-26, 16 seeds) | walker 100 px/s, 450 px/s shot + melee (Str 1.35) + lunge, reaction 3; draws Lancer 0-96-0, loses to Wraith 22-0-74; commit a33efc4 |
| haiku-2 | Haiku 4.5 | submitted | Arrowhawk | 11:40 | 11:52 | 0.873 ±0.010 (1031-145-72, 16 seeds) | walker 100 px/s, 250 px/s shot + lunge + jab, energy-rationed reads, dodges tells; draws Lancer 0-96-0, loses to Turret 18-1-77; commit 4510ef3 |
| sonnet-1 | Sonnet 5.5 | submitted | Thornback | 11:40 | 11:44 | 0.943 (1130-118-0, 16 seeds) | Lancer derivative (cling crawler, 1.6-dmg shot, Health 9, TargetMemory); draws Lancer 0-8-0; commit 3e95e28; 4 league runs |
| sonnet-2 | Sonnet 5.5 | submitted | Stonecreep | 11:46 | 12:08 | 0.937 ±0.004 (1117-131-0, 16 seeds) | Lancer refinement: cling crawler at Density 1.7 (radius 13.3 so it actually anchors; found Lancer at 14.3 never anchors and is a stationary turret), 500 px/s shot, Health 9.5; tried shot phasing vs enemy windup (+0.003, noise); draws Lancer 0-96-0; commit 5f378db |
| opus-1 | Opus 5.5 | running | | 11:53 | | | |
| opus-2 | Opus 5.5 | running | | 11:54 | | | |
| fable-1 | Fable 5.1 | running | | 12:09 | | | |
| fable-2 | Fable 5.1 | queued | | | | | |

Status: queued → running → submitted (branch has its one file) → collected → scored.

## Close-out

- [ ] all eight submitted
- [ ] `collect --wave 3` accepted eight files (name clashes renamed by the coordinator)
- [ ] `FighterPackage` tests green on the collected field
- [ ] `--competition score` → `wave3.md` (holdout / dev / gap / head-to-head)
- [ ] rating fit (Bradley–Terry on an Elo scale over the scoring bouts; pool mean = 1500)
- [ ] class table: mean rating of each class's two bots
- [ ] plan log + guide lessons

## Class results

| class | bot 1 | bot 2 | mean rating | mean holdout score |
|---|---|---|---|---|
| Haiku 4.5 | | | | |
| Sonnet 5.5 | | | | |
| Opus 5.5 | | | | |
| Fable 5.1 | | | | |

## Machine

8 cores, 16 GB. Load average at start 7.5 (before any session). Each session builds its own
worktree (`bin/`, `obj/` per worktree) and runs short leagues; three concurrent builds are the
expected peak. Bottleneck notes go here.

- 11:44 load average spiked to ~390 (1-min) while the three sessions' first builds overlapped
  with the coordinator's own build; it fell back within minutes. Steam (~80% of a core) and a
  Hierwave python process (~70%) are competing with the sessions; free memory was ~65 MB with
  two bench leagues running. Not blocking yet; sessions are build-bound, not league-bound.

## Log

- 11:40 worktrees `haiku-1`, `haiku-2`, `sonnet-1` cut from `competition-wave3-base`; sessions launched.
- 11:44 sonnet-1 submitted Thornback after 4 minutes (a Lancer derivative; dev 0.943).
- 11:45 coordinator added the rating fit (`FighterRating`, Bradley–Terry on the Elo scale; `--competition score` fits it unless `--no-rating`; `By author` table in the report). Tests 15/15.
- 11:46 worktree `sonnet-2` cut; session launched (3 active: haiku-1, haiku-2, sonnet-2).
- 11:52 haiku-2 submitted Arrowhawk (dev 0.873).
- 11:53 haiku-1 submitted Ironshot (dev 0.889). Both Haiku sessions took ~8 minutes.
- 11:54 worktrees `opus-1`, `opus-2` cut; sessions launched (3 active: sonnet-2, opus-1, opus-2).
- 12:08 sonnet-2 submitted Stonecreep (dev 0.937). Finding worth keeping: a Cling body with radius > 14 px never anchors (Lancer, Thornback), so those "crawlers" are stationary shooters.
- 12:09 worktree `fable-1` cut; session launched (3 active: opus-1, opus-2, fable-1).
- Fairness note: the Opus and Fable briefs carry one extra sentence (mirror draws are worth less than a finish) that the Haiku and Sonnet briefs did not. Recorded for the wave report. User: slight advantages are acceptable; the brief is now standardised as `Plans/FIGHTER_COMPETITOR_BRIEF.md` (`competition.py brief`), the Opus/Fable wording.
