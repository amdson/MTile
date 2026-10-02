<!-- The competitor brief. This file is the ONE prompt every authoring session receives, rendered
by `scripts/competition.py brief --wave N --name <slot> --author <tag>` (placeholders {WAVE},
{SLOT}, {WORKTREE}, {AUTHOR}). Edit it here, never ad hoc per session: every class in a wave
must get the same words (wave 3's Haiku and Sonnet sessions lacked the last sentence of "HOW TO
WORK"; recorded in Fights/competition/wave3_progress.md). Wave N+1 may change the brief, but the
change applies to the whole wave. Supervisor side: Plans/FIGHTER_COMPETITION.md §5. -->

You are a competitor in wave {WAVE} of the MTile fighter-package competition. You will design, build, tune and submit ONE fighter bot as a single C# file. Your work is scored later against opponents you cannot see, so design a fighter that generalises; tuning to exactly the opponents in front of you is measured and penalised (the guide explains).

WORKSPACE — this is a hard rule:
- Your worktree is `{WORKTREE}` on git branch `competition-wave{WAVE}-{SLOT}`. Do ALL work there: every file read, build, test, league run and commit happens inside that directory. Use absolute paths under it. Never touch `/Users/amdson/dev/MTile` itself or any other worktree, never run `git checkout`, `git switch`, `git worktree`, `git merge` or `git rebase`, never change branches.
- Build/test commands (run from inside your worktree):
  - `dotnet build MTile.Core.csproj` — quickest compile check.
  - `dotnet build MTile.Bench/MTile.Bench.csproj -c Release` once, then use `--no-build` on the bench commands below to save time.
  - `dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~FighterPackage"` — the gate every package must pass (compiles under budget, deterministic, within the 20 µs Decide budget).
  - `dotnet run --project MTile.Bench -c Release --no-build -- --spec <Name>` — cost report for your spec.
  - `dotnet run --project MTile.Bench -c Release --no-build -- --league --only <Name> --seeds 4 --frames 1800 --no-record --quiet` — your bot against the whole visible field (dev seeds 0..15 are yours to use; `--seeds 16` for a final check).
  - `dotnet run --project MTile.Bench -c Release --no-build -- --record-fight <Name> <Opponent> <flat|corridor|hills> out.fight.json --seed N` then read the JSON summary, to see how a bout went.
  - `--forge` exists (see the guide) if you want an optimiser pass on your spec's numbers.

READ FIRST, in full: `{WORKTREE}/CLAUDE.md`, then `{WORKTREE}/Plans/FIGHTER_PACKAGE_GUIDE.md` (the whole brief: sensing API, cost model, actions, league rules, lessons from waves 1–2, and §12 — the competition rules you are playing under). Then read `{WORKTREE}/Entities/Enemies/Fighters/Packages/ExampleBrawler.cs` and two or three of the other packages in that folder to see how a package is written. The pool file `{WORKTREE}/Fights/competition/wave{WAVE}.pool.json` lists every name in the field; the ones whose source is not in `Packages/` are the hidden half. Do not try to recover hidden code from git history — it is against the rules and pointless: the hidden bots' ladders in `Fights/*_league.md` are public and are all you get.

DELIVERABLE:
- Exactly one NEW file: `{WORKTREE}/Entities/Enemies/Fighters/Packages/<Name>.cs`, where the class's `Name` property equals the file name, and `Author` is `"{AUTHOR}"`. `<Name>` must be unique: not any name in the pool file, not any existing file in `Packages/`, and preferably distinctive (two words fused, e.g. "Ironwing", not "Viper"). Do not modify any other file. A submission that changes or adds anything else is rejected automatically.
- Commit on your branch as you go (`git add Entities/Enemies/Fighters/Packages/<Name>.cs && git commit -m "..."`); only that path may ever be staged. Commit your final version before you finish. Never commit build output, fight files or notes.
- The spec must compile under the default budget with zero violations, and pass the `FighterPackage` test filter.

HOW TO WORK: design first (what wins in the current meta per the guide's lessons: timing is fixed, shots do spec damage, timeouts are draws worth 0.4, so you must be able to FINISH fights, on all three terrains, from varied starting distances and both sides). New since wave 3, all in the guide's §2 and §14: `Dash` (a short burst with a few untouchable frames), `Mine` (an explosive summoned at a point you choose after a public 1.0 s warning; it goes off on contact, on any hit, or on its fuse, and hurts everyone in reach), nearby blocks at 0.3 × build cost, and a faster air block. None of the visible bots use them yet. Then implement, build, run the gate, run the league, read a few recorded bouts, iterate. Budget: roughly 90 minutes of iteration or ~12 league runs, whichever comes first; stop when improvement stalls. Do not over-tune to a single opponent; the score that matters is against bots you have never seen. Be aware that the visible field saturates quickly: a mobile shooter can reach zero losses against it while still drawing every mirror match, and draws are worth 0.4 — a design that can finish a mirror match is worth more than one that only avoids losing.

FINAL REPORT (terse, this is all the coordinator sees): bot Name; one-paragraph design; your final `--seeds 16` league score for it and its W-D-L; the three opponents it does worst against; what you tried and discarded; the final commit hash on your branch; confirmation that only the one file is in your diff from `competition-wave{WAVE}-base` (`git diff --name-status competition-wave{WAVE}-base..HEAD`).
