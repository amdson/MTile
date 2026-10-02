# Wave 4 competition — progress

Coordinator: Claude (Opus 5.5), started 2026-10-02. Rules: `Plans/FIGHTER_COMPETITION.md`.
Four Sonnet 5.5 sessions, one bot each, author tag `sonnet-w4`. At most three authoring
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
| sonnet-3 | Sonnet 5.5 | submitted | Bullrush | 19:10 | 22:16 | 0.997 ±0.001 (1721-6-1, 16 seeds) | Thornbull-style rammer: Contact Damage 7.1, Health 7.5 (survives a 7.0 touch), GroundPower 420, jump 400, Density 0.6; **first bot to use Dash**: dashes at a retreating target 12–160 px past touch range, turning Gravemantle's 95 draws into wins; worst vs Mason, Gravemantle, Arrowhawk; commit f8648e0 |
| sonnet-4 | Sonnet 5.5 | submitted | Ironhide | 19:10 | 22:14 | 0.992 ±0.002 (1712-6-10, 16 seeds) | contact rammer on a Thornbull-style brain: Health 9, Contact Damage 8, GroundPower 330 (128 px/s), Density 0.5 (radius 24); survives Thornbull's 7-damage touch and kills with its own; worst on hills vs Phalanx (9-6-1), Thornbull (12-0-4), Arrowhawk (13-0-3); no Dash or Mine; commit 9879f31 |
| sonnet-5 | Sonnet 5.5 | submitted | Ironwing | 19:10 | 22:15 | 0.993 ±0.002 (1713-7-8, 16 seeds) | Thornbull's brain copied, spec re-split: Contact Damage 7, Health 9.4, GroundPower 330, jump 370, Density 0.6 (radius 22); survives a 7–9 damage touch so rammer mirrors become wins; worst vs Arrowhawk, Mason, Twinbarrel; considered Dash and Mine on paper, used neither; commit d3d4fd0 |
| sonnet-6 | Sonnet 5.5 | running | | 22:14 | | | |

Status: queued → running → submitted (branch has its one file) → collected → scored.

## Close-out

- [ ] all four submitted
- [ ] `collect --wave 4` accepted every file
- [ ] `FighterPackage` tests green on the collected field
- [ ] `--competition score` → `wave4.md`
- [ ] plan log + guide lessons

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
