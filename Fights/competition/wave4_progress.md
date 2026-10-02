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

| opus-3 | Opus 5.5 | scored | Bramblehorn | 11:19 | 11:37 | 0.997 ±0.001 (1719-8-1, 16 seeds) | one-touch rammer (Contact Damage 10, Active 0.6, Health 7.5, 134 px/s) **plus a long untouchable dash** (790 px/s × 0.15 s): dashes through shots on the predicted release frame, through an opposing rammer's unfired hide, and into a cooling hide; randomised crossings so mirrors decide; worst vs Thornbull (6 draws, 1 loss), Bulwark, Gravemantle; weak spot per author: a one-touch-kill rammer on hills; commit 5f5826b |
| opus-4 | Opus 5.5 | scored | Brambleram | 11:19 | 11:48 | 0.998 ±0.001 (1723-5-0, 16 seeds) | one-touch rammer (Contact Damage 11.5, live 0.6 s, Health 7.3, Density 0.5, TargetMemory) with a full-length untouchable 120 px dash; learns the opponent's ball speed and hide window from the first exchange and times dashes frame by frame through shots and through rammers; worst: single-seed draws vs Gravemantle, Phalanx (hills), Mason; commit 486bc52 |
| opus-5 | Opus 5.5 | scored | Cinderhoof | 11:19 | 11:39 | 0.991 ±0.002 (1706-15-7, 16 seeds) | rammer for the rammer duel: Contact Damage 12.1 (one-touch kills up to 12 health), Health 8.1 (wins exact-equality trades), hide fired only when the boxes will meet, no opening-frame gate; learns the opponent's hide window from the tell and dashes clear of a live hide, then back in during its recovery; walks through shots with feet down; worst vs Mason 86-9-1, Gravemantle 90-6-0, Arrowhawk 93-0-3; commit b5943c8 |
| codex-2 | Codex (Astra) | scored | Gravemarch | — | 12:44 (collected) | 0.996 dev (1717-9-2) | dash rammer: Health 12.2, Contact Damage 9.5 (live 0.6 s, no knockback), 1180 px/s × 0.1 s fully untouchable dash, legs 165, energy 1.1 / 0.7, TargetMemory; branch cut from the current base by its own session; commit 3764225 |
Status: queued → running → submitted (branch has its one file) → collected → scored.

## Close-out

- [x] all four submitted
- [x] `collect --wave 4` accepted every file (after Gritmaul's rename; collect now rejects name clashes)
- [x] `FighterPackage` tests green on the collected field (163/163)
- [x] `--competition score` → [`wave4.md`](wave4.md) (all seven, 247 s)
- [ ] plan log + guide lessons

## Results — all seven, scored 2026-10-02 (supersedes the Sonnet-only scoring of b899c6c)

Scored on the fixed build (the spec-copy fix, so dashes keep their i-frames): the Sonnet
holdout numbers are unchanged from the Sonnet-only scoring.

| rank | bot | class | holdout | dev | gap | h2h | rating | W-D-L hold |
|---|---|---|---|---|---|---|---|---|
| 1 | Brambleram | Opus | **1.000** | 0.998 | -0.002 | 0.686 | 2747 | 1056-0-0 |
| 2 | Bramblehorn | Opus | 0.999 | 0.999 | -0.001 | 0.766 | **2811** | 1055-1-0 |
| 3 | Gritmaul | Sonnet | 0.989 | 0.992 | +0.003 | 0.175 | 2366 | 1044-1-11 |
| 4 | Cinderhoof | Opus | 0.988 | 0.987 | 0.000 | **0.820** | 2805 | 1043-0-13 |
| 5 | Bullrush | Sonnet | 0.969 | 0.995 | +0.026 | 0.115 | 2313 | 1022-2-32 |
| 6 | Ironhide | Sonnet | 0.963 | 0.990 | +0.027 | 0.564 | 2584 | 1017-1-38 |
| 7 | Ironwing | Sonnet | 0.932 | 0.987 | +0.055 | 0.219 | 2329 | 984-0-72 |

Class means: **Opus 2788** (holdout 0.996), Sonnet 2398 (holdout 0.963).

**Reading.** All seven are contact rammers; the three Opus bots are the ones built around a
long, fully untouchable dash (through shots on the predicted release frame, through a
rammer's live touch into its cooldown) and a one-touch kill (10–12.1 damage). That beat the
holdout's one remaining resistance (Thornback, the clinging turret, which took 28–31 wins off
three Sonnets) and dominates the Sonnet rammers head to head (Cinderhoof 0.820, Bramblehorn
0.766, Brambleram 0.686 vs Ironhide 0.564 and the rest under 0.22). The same counter the
coordinator's exhibition bot Tripwire found (branch exhibition-coordinator-2).

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
- 11:37 — opus-3 submitted Bramblehorn (5f5826b, one file, clean tree): rammer + long untouchable dash, the first submission built around the dash.
- 11:39 — opus-5 submitted Cinderhoof (b5943c8, one file, clean tree). **Isolation leak reported by
  opus-5:** subagent sessions share one scratchpad directory, so concurrent competitors could
  see each other's scratch files (it saw Bramblehorn fight files, which carry the spec's
  numbers but not its brain, and another session's script overwrote its own). It moved to a
  private subfolder and says it read nothing of anyone else's. Within the agreed loose
  security; closed for later waves: the brief now says to keep scratch files under the
  worktree's (git-ignored) `Fights/league/`.
- 11:48 — opus-4 submitted Brambleram (486bc52). Collected the three Opus bots; gate 181/181; all seven scored (247 s). Brambleram wins on holdout 1.000.
- 12:44 — Astra (Codex) added Gravemarch on `competition-wave4-codex-2` (one file, cut from the
  current base). Collected; gate 187/187; all eight rescored. Gravemarch: holdout 0.994 (3rd),
  head-to-head **0.960** (638-18-16), rating **3120** (top). Note: its Health 12.2 sits one notch
  over the wave's top touch damage (Cinderhoof's 12.1), a number this tracker published on
  `fighter-plan` before the branch was committed; the holdout score is unaffected, the
  head-to-head should be read with that in mind. Class means: Codex 3120 (1 bot), Opus 2760,
  Sonnet 2386.
