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
| sonnet-3 | Sonnet 5.5 | running (resumed 22:15) | Bullrush (draft) | 19:10 | | | |
| sonnet-4 | Sonnet 5.5 | running (resumed 22:15) | Ironhide (9879f31) | 19:10 | | | |
| sonnet-5 | Sonnet 5.5 | running (resumed 22:15) | Ironwing (draft) | 19:10 | | | |
| sonnet-6 | Sonnet 5.5 | queued | | | | | |

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
