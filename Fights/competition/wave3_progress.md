# Wave 3 competition — progress

Coordinator: Claude (Fable 5.1), started 2026-10-01 11:40. Rules: `Plans/FIGHTER_COMPETITION.md`.
Four Claude classes, two bots each, authored in independent sessions; a class's score is the
mean rating of its two bots. Codex later entered one additional bot (nine submissions total);
its one-bot mean is reported separately. Codex completed coordination and scoring at the user's request. At most three authoring sessions run at a time, in order
Haiku → Sonnet → Opus → Fable. Pool: `wave3.pool.json` (Dev visible, Holdout hidden).

## Sessions

| slot | model | status | bot | started | finished | dev score (self-reported) | notes |
|---|---|---|---|---|---|---|---|
| haiku-1 | Haiku 4.5 | scored | Ironshot | 11:40 | 11:53 | 0.889 ±0.007 (1034-188-26, 16 seeds) | walker 100 px/s, 450 px/s shot + melee (Str 1.35) + lunge, reaction 3; draws Lancer 0-96-0, loses to Wraith 22-0-74; commit a33efc4 |
| haiku-2 | Haiku 4.5 | scored | Arrowhawk | 11:40 | 11:52 | 0.873 ±0.010 (1031-145-72, 16 seeds) | walker 100 px/s, 250 px/s shot + lunge + jab, energy-rationed reads, dodges tells; draws Lancer 0-96-0, loses to Turret 18-1-77; commit 4510ef3 |
| sonnet-1 | Sonnet 5.5 | scored | Thornback | 11:40 | 11:44 | 0.943 (1130-118-0, 16 seeds) | Lancer derivative (cling crawler, 1.6-dmg shot, Health 9, TargetMemory); draws Lancer 0-8-0; commit 3e95e28; 4 league runs |
| sonnet-2 | Sonnet 5.5 | scored | Stonecreep | 11:46 | 12:08 | 0.937 ±0.004 (1117-131-0, 16 seeds) | Lancer refinement: cling crawler at Density 1.7 (radius 13.3 so it actually anchors; found Lancer at 14.3 never anchors and is a stationary turret), 500 px/s shot, Health 9.5; tried shot phasing vs enemy windup (+0.003, noise); draws Lancer 0-96-0; commit 5f378db |
| opus-1 | Opus 5.5 | scored | Thornrush | 11:53 | 12:20 | 0.992 ±0.002 (1237-4-7, 16 seeds) | rammer: Contact Damage 3 / HalfWidth 16, 141 px/s, jump 600; hops on the exact shot-release frame computed from the fixed windup, fires a 600 px/s ball from the air into the shooter's recovery; built proxy bots for the hidden half (0.987 vs them); commit 30c91b6 |
| opus-2 | Opus 5.5 | scored | Thornbull | 11:54 | 12:14 | 0.998 ±0.001 (1245-0-3, 16 seeds) | rammer: Contact action Damage 7 / HalfWidth 16 (no windup, no tell, staggers), 150 px/s legs, Density 0.6; loses only to Lancer on hills from the left spawn; commit de2c641 |
| fable-1 | Fable 5.1 | scored | Twinbarrel | 12:09 | 12:35 (commit) | 0.999 ±0.001 (1245-3-0, 16 seeds; saved dev report) | alternating ranged actions skip recovery using priority preemption; commit 887b010 |
| fable-2 | Fable 5.1 | scored | Ironmaw | 12:15 | 12:29 (commit) | not recorded | fast contact closer, 3.36 effective damage, low knockback, tell-timed dodges and edge guard; commit 9d27c9d |
| codex-1 | Codex | scored | Gravemantle | — | — | 1.000 ±0.000 (1248-0-0, 16 seeds) | two 0.35-damage, HalfWidth 280 contact actions alternate via priority preemption; zero knockback; mass 2.48; commit 8f1d751 |

Status: queued → running → submitted (branch has its one file) → collected → scored.

## Close-out

- [x] all eight original submissions plus Codex submitted (nine total)
- [x] `collect --wave 3` accepted all nine files; no name clashes; copied without merging
- [x] `FighterPackage` tests green on the collected field: 133/133 passed (Release)
- [x] `--competition score` → `wave3.md` (holdout / dev / gap / head-to-head)
- [x] rating fit (Bradley–Terry on an Elo scale over the scoring bouts; pool mean = 1500)
- [x] class table: mean rating of each class's two bots
- [x] plan log + guide lessons

## Final results — scored 2026-10-01

[Full generated report](wave3.md), including per-terrain head-to-head matrices and all
pool ratings. All nine submissions qualified. Settings: 1800 frames, flat/corridor/hills,
both spawn orders, seeds 100–115, win 1 / draw 0.4 / loss 0. There are 576 holdout,
1248 dev, and 768 head-to-head bouts per submission. Including the rating-anchor pool
league, the run covered 36,288 unique bouts in 538.4 seconds.

**Gravemantle wins the wave:** holdout 576–0–0, dev 1248–0–0, head-to-head 768–0–0.
It beat every other submission 96–0–0, including both Fable entrants (192–0–0 combined).
Twinbarrel is runner-up individually; Opus narrowly leads Fable by mean class rating.

Ranks below follow the official holdout-first ordering; head-to-head breaks ties.
Dev and gap use the official fresh scoring seeds, not the authoring seeds in Sessions.

| Rank | Bot | Holdout ±se | Dev ±se | Dev − holdout | H2H score | Rating | Holdout W-D-L | H2H W-D-L |
|---|---|---|---|---|---|---|---|---|
| 1 | Gravemantle | 1.000 ±0.000 | 1.000 ±0.000 | 0.000 | 1.000 | 3108 | 576-0-0 | 768-0-0 |
| 2 | Twinbarrel | 0.992 ±0.003 | 0.993 ±0.003 | +0.001 | 0.803 | 2517 | 568-8-0 | 613-9-146 |
| 3 | Thornbull | 0.985 ±0.006 | 0.993 ±0.002 | +0.008 | 0.674 | 2349 | 567-1-8 | 518-0-250 |
| 4 | Thornrush | 0.977 ±0.008 | 0.979 ±0.005 | +0.002 | 0.487 | 2182 | 563-0-13 | 355-47-366 |
| 5 | Ironmaw | 0.849 ±0.016 | 0.967 ±0.005 | +0.118 | 0.258 | 1993 | 489-0-87 | 179-48-541 |
| 6 | Thornback | 0.704 ±0.005 | 0.934 ±0.004 | +0.229 | 0.344 | 1997 | 292-284-0 | 148-290-330 |
| 7 | Stonecreep | 0.699 ±0.001 | 0.925 ±0.005 | +0.227 | 0.266 | 1956 | 287-289-0 | 87-294-387 |
| 8 | Ironshot | 0.693 ±0.004 | 0.881 ±0.008 | +0.188 | 0.206 | 1899 | 285-286-5 | 42-290-436 |
| 9 | Arrowhawk | 0.651 ±0.015 | 0.862 ±0.010 | +0.211 | 0.297 | 1909 | 257-295-24 | 112-290-366 |

### Head-to-head, combined across terrains and spawn orders

Each cell is **row bot's W-D-L** against the column bot (96 bouts). These are aggregated
from the generated report's three directional matrices; row totals match its H2H ladder.

| Bot | Gravemantle | Twinbarrel | Thornbull | Thornrush | Ironmaw | Thornback | Stonecreep | Ironshot | Arrowhawk |
|---|---|---|---|---|---|---|---|---|---|
| Gravemantle | — | 96-0-0 | 96-0-0 | 96-0-0 | 96-0-0 | 96-0-0 | 96-0-0 | 96-0-0 | 96-0-0 |
| Twinbarrel | 0-0-96 | — | 90-0-6 | 59-0-37 | 93-0-3 | 93-2-1 | 91-5-0 | 94-0-2 | 93-2-1 |
| Thornbull | 0-0-96 | 6-0-90 | — | 96-0-0 | 96-0-0 | 50-0-46 | 89-0-7 | 94-0-2 | 87-0-9 |
| Thornrush | 0-0-96 | 37-0-59 | 0-0-96 | — | 41-46-9 | 44-0-52 | 92-1-3 | 94-0-2 | 47-0-49 |
| Ironmaw | 0-0-96 | 3-0-93 | 0-0-96 | 9-46-41 | — | 47-0-49 | 19-0-77 | 58-2-36 | 43-0-53 |
| Thornback | 0-0-96 | 1-2-93 | 46-0-50 | 52-0-44 | 49-0-47 | — | 0-96-0 | 0-96-0 | 0-96-0 |
| Stonecreep | 0-0-96 | 0-5-91 | 7-0-89 | 3-1-92 | 77-0-19 | 0-96-0 | — | 0-96-0 | 0-96-0 |
| Ironshot | 0-0-96 | 2-0-94 | 2-0-94 | 2-0-94 | 36-2-58 | 0-96-0 | 0-96-0 | — | 0-96-0 |
| Arrowhawk | 0-0-96 | 1-2-93 | 9-0-87 | 49-0-47 | 53-0-43 | 0-96-0 | 0-96-0 | 0-96-0 | — |

The largest dev-to-holdout gaps are Thornback (+0.229) and Stonecreep (+0.227),
mostly because their hidden matchups draw rather than finish. Neither loses a holdout
bout, but draws score only 0.4. Ironmaw's 0.967 official dev score falls to 0.849 holdout
and 0.258 head-to-head: the stronger submission field exposes weaknesses the dev pool
mostly misses. Twinbarrel's 0.992 holdout and 0.803 head-to-head make it the strongest
Fable entrant by a wide margin.

## Class results

| class | bot 1 | bot 2 | mean rating | mean holdout score |
|---|---|---|---|---|
| Codex (one entrant) | Gravemantle | — | 3108 | 1.000 |
| Opus 5.5 | Thornbull | Thornrush | 2265 | 0.981 |
| Fable 5.1 | Twinbarrel | Ironmaw | 2255 | 0.920 |
| Sonnet 5.5 | Thornback | Stonecreep | 1977 | 0.702 |
| Haiku 4.5 | Ironshot | Arrowhawk | 1904 | 0.672 |

Ratings are the scorer's fitted values, rounded for display; class means are computed
before rounding. Opus leads the original four-class comparison by about 10 rating points;
no uncertainty interval for that difference is fitted. Codex is a separate one-bot entry
with the information advantage described under provenance below.

## Rescored under the close-out rules (2026-10-01, commit 5078cde)

[`wave3_rescored.md`](wave3_rescored.md): the same nine bots, same pool, seeds and frames,
after the three post-wave fixes — fixed per-kind priorities with uncuttable recoveries, a
Contact box that is the body (radius + 3 px), and the rollback fix (a touch's recovery no
longer locks the body after a restore). The original `wave3.md` stands as the official result;
this is the field as wave 4 will inherit it.

| Rank | Bot | Holdout ±se | Dev ±se | Gap | H2H | Rating | was (rank, holdout) |
|---|---|---|---|---|---|---|---|
| 1 | Thornrush | 0.983 ±0.005 | 0.979 | −0.003 | 0.686 | 2179 | 4, 0.977 |
| 2 | Thornbull | 0.980 ±0.006 | 0.986 | +0.007 | 0.907 | 2367 | 3, 0.985 |
| 3 | Ironmaw | 0.850 ±0.017 | 0.975 | +0.126 | 0.476 | 2011 | 5, 0.849 |
| 4 | Twinbarrel | 0.842 ±0.015 | 0.941 | +0.099 | 0.376 | 1969 | 2, 0.992 |
| 5 | Thornback | 0.704 ±0.005 | 0.934 | +0.229 | 0.463 | 1971 | 6, 0.704 |
| 6 | Stonecreep | 0.699 ±0.001 | 0.925 | +0.227 | 0.424 | 1950 | 7, 0.699 |
| 7 | Ironshot | 0.693 ±0.004 | 0.881 | +0.188 | 0.307 | 1872 | 8, 0.693 |
| 8 | Arrowhawk | 0.651 ±0.015 | 0.862 | +0.211 | 0.452 | 1904 | 9, 0.651 |
| 9 | Gravemantle | 0.214 ±0.013 | 0.345 | +0.131 | 0.115 | 1423 | 1, 1.000 |

| class | mean rating | mean holdout | was (rating) |
|---|---|---|---|
| Opus 5.5 | 2273 | 0.981 | 2265 |
| Fable 5.1 | 1990 | 0.846 | 2255 |
| Sonnet 5.5 | 1961 | 0.702 | 1977 |
| Haiku 4.5 | 1888 | 0.672 | 1904 |
| Codex (one bot) | 1423 | 0.214 | 3108 |

Gravemantle falls from first to last: its arena-wide contact field is now a 0.35-damage touch
(308 holdout draws, 268 losses). Twinbarrel loses its barrel swap and drops from 0.992 to
0.842 (138 holdout draws); it still beats the Haiku and Sonnet bots on the holdout but not
head-to-head. The two Opus rammers are untouched by the rules and now lead outright; the
Haiku and Sonnet shooters are unchanged to three decimals. Opus leads Fable by 283 rating
points instead of 10.

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
- 12:14 opus-2 submitted Thornbull (dev 0.998). **Pricing finding:** Contact damage costs 0.005 × Damage × HalfWidth, so a 7-damage, 16-wide hide is 0.56 mass — a one-hit kill with no windup and no tell. Plan §13.4: exploits are findings; price it after the wave. **Possible sim gap** reported by the author: a kill before the frame-60 rollback snapshot made the replay diverge from the restore frame ("the snapshot appears not to carry the post-kill retarget"); the brain avoids attacking before frame 65 to pass the gate. To reproduce after the wave.
- 12:15 worktree `fable-2` cut; session launched (3 active: opus-1, fable-1, fable-2). All eight sessions have now started.
- 12:33 fable-2 told to stop and commit (user request, usage). Ironmaw v2 committed untested.
- 12:48 fable-1 submitted Twinbarrel (dev 0.999) after 13 league runs and ~39 minutes — the wave's longest session. All eight submitted.
- 12:20 opus-1 submitted Thornrush (dev 0.992). Both Opus bots are Contact rammers. **Second loophole reported (not used):** `ActivePriority`/`PassivePriority` on actions are not fixed by the compiler, so a higher-priority action can preempt the bot's own windup or recovery — a way around the fixed-timing rule. To close after the wave. Thornrush also confirms the fixed-timing "phase" play: one tell read + the known windup gives the release frame; hop on it, shoot into the recovery.
- Fairness note: the Opus and Fable briefs carry one extra sentence (mirror draws are worth less than a finish) that the Haiku and Sonnet briefs did not. Recorded for the wave report. User: slight advantages are acceptable; the brief is now standardised as `Plans/FIGHTER_COMPETITOR_BRIEF.md` (`competition.py brief`), the Opus/Fable wording.

- 13:06 · **Wave closed.** Collected all nine committed submissions without merging or
  changing their code. Package gate 133/133. Scored holdout/dev, submission head-to-head,
  and rating-anchor pool league; full output in `wave3.md`. Gravemantle wins every one
  of its 2592 scoring bouts; Twinbarrel is runner-up. Original class means: Opus 2265,
  Fable 2255, Sonnet 1977, Haiku 1904; separate Codex entry 3108. Guide §13 and the
  campaign log record the findings. Wave 4 has not been cut or rolled.

## Scoring provenance and validation

The scoring engine and configs are from `fighter-plan` commit
`3633b9705ec40355858920bbfc70e9560cd002a6`, plus the nine unmodified package files
from the submission commits listed above. The pool remains `wave3.pool.json`.
No fighter was tuned after collection or after seeing holdout results.

```bash
dotnet build MTile.Bench/MTile.Bench.csproj -c Release
dotnet test MTile.Tests/MTile.Tests.csproj -c Release --filter "FullyQualifiedName~FighterPackage"
dotnet run --project MTile.Bench -c Release --no-build -- --competition score --pool Fights/competition/wave3.pool.json --quiet
```

The broader package/competition/rating test run passed 147/148 tests. Its sole failure,
`Roll_PoolGrowsBySubmissions_AndReSplits`, builds a fixture from the wave-2 ladder and
assumes that only its two deliberately removed names are new submissions. Reflection
now also discovers the nine collected bots, invalidating that fixture's expected list.
The exact package acceptance gate passed separately, 133/133. No simulation, scoring,
config, or test code was changed for this run.

Comparability: the existing brief discrepancy between early and later Claude sessions
remains documented above. Codex was explicitly allowed to read this progress file before
authoring, including the contact-pricing and priority loophole findings; its results
are therefore an additional entrant's result, not a controlled comparison of models
given identical information. Codex has one entrant versus two per Claude class.
The author-reported development figures above use seeds 0–15; official dev scores use
seeds 100–115 and must not be substituted for those author reports.
