# Wave 4 competition — progress

Coordinator: Claude (Opus 5.5), started 2026-10-02. Rules: `Plans/FIGHTER_COMPETITION.md`.
Four Sonnet 5.5 sessions, one bot each, author tag `sonnet-w4`; then (2026-10-02) three Opus 5.5 sessions, author tag `opus-w4`, against the same pool and base. At most three authoring
sessions run at a time. Brief: `Plans/FIGHTER_COMPETITOR_BRIEF.md` (rendered per slot by
`scripts/competition.py brief --wave 4 --name <slot>`); new since wave 3: Dash, Mine
(summoned, 1.0 s public warning), cheap nearby blocks, faster air blocks (guide §2, §14).

Pool: [`wave4.pool.json`](wave4.pool.json), rolled from the full 29-bot league
[`Fights/wave3_full_league.md`](../wave3_full_league.md) (pool 3 + the nine wave-3
submissions + the coordinator's exhibition bot Hollowstep). Base branch
`competition-wave4-base` (cac6943) = `fighter-plan` minus the 11 holdout packages.

- **Dev (18, visible):** Thornbull, Ironmaw, Twinbarrel, Phalanx, Arrowhawk, Harrier, Mason,
  Breacher, Wraith, Brick, Bulwark, Turret, Gunner, Gravemantle, Builder, Flyer,
  ExampleBrawler, Sprinter
- **Holdout (11, hidden):** Thornrush, Hollowstep, Thornback, Stonecreep, Lancer, Ironshot,
  Sentinel, Vanguard, Sapper, Skirmish, Kestrel

Slots are `sonnet-3`..`sonnet-6`: `sonnet-1` and `sonnet-2` are wave 3's worktrees, kept.

## Sessions

| slot | model | status | bot | started | finished | dev score (self-reported) | notes |
|---|---|---|---|---|---|---|---|
| sonnet-3 | Sonnet 5.5 | scored | Bullrush | 19:10 | 22:16 | 0.997 ±0.001 (1721-6-1, 16 seeds) | Thornbull-style rammer: Contact Damage 7.1, Health 7.5 (survives a 7.0 touch), GroundPower 420, jump 400, Density 0.6; **first bot to use Dash**: dashes at a retreating target 12–160 px past touch range, turning Gravemantle's 95 draws into wins; worst vs Mason, Gravemantle, Arrowhawk; commit f8648e0 |
| sonnet-4 | Sonnet 5.5 | scored | Ironhide | 19:10 | 22:14 | 0.992 ±0.002 (1712-6-10, 16 seeds) | contact rammer on a Thornbull-style brain: Health 9, Contact Damage 8, GroundPower 330 (128 px/s), Density 0.5 (radius 24); survives Thornbull's 7-damage touch and kills with its own; worst on hills vs Phalanx (9-6-1), Thornbull (12-0-4), Arrowhawk (13-0-3); no Dash or Mine; commit 9879f31 |
| sonnet-5 | Sonnet 5.5 | scored | Ironwing | 19:10 | 22:15 | 0.993 ±0.002 (1713-7-8, 16 seeds) | Thornbull's brain copied, spec re-split: Contact Damage 7, Health 9.4, GroundPower 330, jump 370, Density 0.6 (radius 22); survives a 7–9 damage touch so rammer mirrors become wins; worst vs Arrowhawk, Mason, Twinbarrel; considered Dash and Mine on paper, used neither; commit d3d4fd0 |
| sonnet-6 | Sonnet 5.5 | scored | Gritmaul | 22:14 | 23:00 | 0.996 ±0.001 (1720-4-4, 16 seeds) | Thornbull-derived rammer: Health 8, Contact Damage 5.5, 150 px/s, energy trimmed to 1 / 0.5 to pay for health; first named Bullrush (clash with sonnet-3), renamed; no Dash or Mine; commit d79da43 |

| opus-3 | Opus 5.5 | running | | 11:19 | | | |
| opus-4 | Opus 5.5 | running | | 11:19 | | | |
| opus-5 | Opus 5.5 | running | | 11:19 | | | |
Status: queued → running → submitted (branch has its one file) → collected → scored.

## Close-out

- [x] all four submitted
- [x] `collect --wave 4` accepted every file (after Gritmaul's rename; collect now rejects name clashes)
- [x] `FighterPackage` tests green on the collected field (163/163)
- [x] `--competition score` → [`wave4.md`](wave4.md) (188 s)
- [ ] plan log + guide lessons

## Results — scored 2026-10-02

| rank | bot | holdout | dev | gap | h2h | rating | W-D-L hold |
|---|---|---|---|---|---|---|---|
| 1 | Gritmaul | **0.989** | 0.992 | +0.003 | 0.342 | 2402 | 1044-1-11 |
| 2 | Bullrush | 0.969 | 0.995 | +0.026 | 0.217 | 2339 | 1022-2-32 |
| 3 | Ironhide | 0.963 | 0.990 | +0.027 | 0.821 | **2546** | 1017-1-38 |
| 4 | Ironwing | 0.932 | 0.987 | +0.055 | 0.424 | 2358 | 984-0-72 |

Sonnet class mean rating **2411** (wave 3 Sonnet: 1961 rescored). All four rate above every
pool bot (Thornbull 2085 next).

Holdout by opponent (W-D-L of 96, 16 seeds × 3 terrains × both sides):

| hidden bot | Gritmaul | Bullrush | Ironhide | Ironwing |
|---|---|---|---|---|
| Thornrush | 96-0-0 | 96-0-0 | 96-0-0 | 96-0-0 |
| Hollowstep | 94-0-2 | 95-0-1 | 96-0-0 | 93-0-3 |
| Thornback | 91-1-4 | 63-2-31 | 68-0-28 | 66-0-30 |
| Stonecreep | 95-0-1 | 96-0-0 | 91-1-4 | 82-0-14 |
| Lancer | 94-0-2 | 96-0-0 | 94-0-2 | 75-0-21 |
| Ironshot | 94-0-2 | 96-0-0 | 92-0-4 | 92-0-4 |
| the other five | 480-0-0 | 480-0-0 | 480-0-0 | 480-0-0 |

Head to head (row vs column): Ironhide beats all three others 96-0-0 (its 8-damage touch
kills every other submission's health in one hit, and its 9 health survives theirs); the
other pairings are mostly draws (mutual kills).

**Reading.** All four are Thornbull-derived contact rammers that moved budget into health,
so a simultaneous touch kills only the other side. That beat every hidden bot built for
wave 3, including both counters to rammers: Thornrush (a rammer itself, 5 health) and
Hollowstep (a counter-puncher whose two 1.5-damage balls cannot kill 7.5–9.4 health before
the touch lands). Only Thornback, a clinging 9-health turret, took real points. Among the
submissions it is a health/damage threshold race, which Ironhide won (h2h 0.821) and
Gritmaul lost least to the hidden field. Only Bullrush used a new tool (a gap-closing Dash).

## Log

- 2026-10-02 — full 29-bot league (16 seeds, 1800 frames, 195 s on 8 threads) → roll → pool
  file and ladder committed; `fighter_gauntlet` made tolerant of hidden packages; base cut
  and gate-checked (12 Dev packages, build green, 105 fighter tests green).
- 19:10 — sonnet-3, sonnet-4, sonnet-5 launched (three at a time); sonnet-6 queued for the first free slot.
- 22:15 — all three sessions stalled (watchdog: a command with no output for 600 s). Cause: a
  computational bottleneck — the league now uses every core by default, so three concurrent
  league runs (~24 sim threads + builds, Steam also busy) on 8 cores pushed the load average to
  ~61. Work survived: sonnet-4 committed Ironhide (9879f31); sonnet-3 (Bullrush) and sonnet-5
  (Ironwing) had uncommitted drafts. All three resumed with their context and told to pass
  `--threads 2` and keep commands under ~5 min; the brief template now says the same, so
  sonnet-6 gets identical instructions.
- 22:14 — sonnet-4 submitted Ironhide (9879f31, one file, clean tree). sonnet-6 launched into the free slot.
- 22:15 — sonnet-5 submitted Ironwing (d3d4fd0, one file, clean tree). Second Thornbull-derived high-health rammer; no new tools used so far.
- 22:16 — sonnet-3 submitted Bullrush (f8648e0, one file, clean tree). First use of a new tool: a gap-closing Dash. Three of three submissions are high-health Thornbull-derived rammers.
- 23:00 — Gritmaul renamed (d79da43). Collected all four; gate 163/163.
- 23:10 — scoring killed at the 40-min background cap; a 384-bout check showed 1.7 s of
  bouts in 940 s wall clock: the machine was asleep or starved overnight (load ~36), not a
  code path. Next morning the same check took 0.78 s.
- 09:55 — scored (188 s). Gritmaul wins the wave on holdout 0.989.
- 11:19 — three Opus sessions (opus-3/4/5; opus-1/2 are wave 3's worktrees) launched on the same
  pool. Before launching, the spec-copy fix (21726b0: the fight record dropped every Dash and
  Mine knob, so a mine crashed any league run and a dash lost its i-frames) was cherry-picked
  onto `competition-wave4-base` (ee37253), and `collect` now diffs each branch from its
  merge-base with the base, so the Sonnet branches still read as one added file each. Sonnet
  bots are not visible to the Opus sessions (they are submissions, not pool). Same brief
  template; Opus sessions get the tools working, the Sonnet sessions had the bug.
