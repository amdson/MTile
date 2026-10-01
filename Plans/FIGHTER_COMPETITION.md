# Fighter competition: the supervisor's manual (from wave 3)

How a wave of the fighter-package competition is run, scored and closed. Competitors read
`Plans/FIGHTER_PACKAGE_GUIDE.md` (its §12 is their side of these rules); this page is the
supervisor's side. The design background is `Plans/FIGHTER_DESIGN_PLAN.md` §11 (scoring) and
§13 (benchmark tracks, contamination, untrusted code).

Code: `Entities/Enemies/Fighters/FighterCompetition.cs` (pool, split, scorer, roll),
`MTile.Bench/Competition.cs` (the `--competition` CLI), `scripts/competition.py` (git plumbing:
base branch, worktrees, collection). Tests: `FighterCompetitionTests`.

## 1. Purpose

Waves 1 and 2 were scored by a league over the whole field, and every competitor could run
that league against every opponent, on the same seeds, as often as it liked. Whatever an
author can see, it tunes to: wave 2's seeded league showed seed 0 was the best seed for four
packages, because it was the start every author iterated on (guide §10). The forge
(`--forge`) overfits the same way, since its objective is the field it is handed.

So from wave 3 the score is a holdout score. Half the existing packages are removed from the
competitors' tree, and every submission is scored against that hidden half on seeds nobody
developed on. That number measures whether a design generalises to opponents it never met.
The same submission's score against the visible half is reported next to it, and the
difference (the **overfit gap**) is itself a finding about the author: an agent that wins
its dev pool and loses its holdout pool tuned to opponents rather than designing a fighter.
This is plan §13.3's "the gap between public and held-out rating is itself a reported
number", applied to opponents as well as conditions.

## 2. The wave cycle

Freeze the pool, split it, cut a base branch without the holdout code, let competitors work
in worktrees off that base, collect one file from each, score, publish, roll. Every command
below is run from the repo root on `fighter-plan`. Wave 3 values are filled in.

**Freeze.** The pool is every participant that exists when the wave is cut
(`FighterLeague.AllParticipants()`: 13 packages and the six roster bots for wave 3). Nothing
new lands on `fighter-plan` under `Packages/` between here and collection, or it will be
mistaken for a submission (§5).

**Split.** Ranked from the latest full ladder, written to the pool file:

```bash
dotnet run --project MTile.Bench -c Release -- --competition split --wave 3 --ladder Fights/wave2_long_league.md
#   -> Fights/competition/wave3.pool.json  (default --out)
```

Read the Dev and Holdout lists in the pool file before going on. As a hand check against
`Fights/wave2_long_league.md`: the six roster bots and `ExampleBrawler` are Dev by rule, and
the snake deal over the other twelve packages gives Dev = Lancer, Kestrel, Vanguard,
Breacher, Wraith, Bulwark and Holdout = Phalanx, Harrier, Sentinel, Mason, Sapper, Skirmish.
The command's output is authoritative; a mismatch means the ladder parse or the deal is off.

**Commit the pool file.** `Fights/competition/` is tracked (only `Fights/wave*/` and
`Fights/league/` are ignored), and the pool is the record of what the wave was scored
against.

**List the holdout files** (what the base branch will delete):

```bash
dotnet run --project MTile.Bench -c Release -- --competition files --pool Fights/competition/wave3.pool.json
```

One path per line. It throws if a holdout package does not live in `Packages/<Name>.cs`; the
file is the unit of hiding, so fix the file name before going further.

**Cut the base branch.**

```bash
scripts/competition.py base --wave 3            # --from fighter-plan is the default
```

This builds `competition-wave3-base` in a temporary worktree: `git rm` of the holdout files,
one commit ("Competition wave 3: base (holdout packages removed)"), then the temporary
worktree is removed. It never touches the primary checkout, which is dirty on purpose. The
base is cut from the committed `fighter-plan`, so uncommitted work in the primary checkout
does not reach competitors. If the pool file is committed first, competitors can read the
holdout names in it; that is no leak, since the names are the published ladder minus the
packages in their tree (§4).

**One worktree per competitor, one brief.**

```bash
scripts/competition.py worktree --wave 3 --name <agent>
#   git worktree add .claude/worktrees/<agent> -b competition-wave3-<agent> competition-wave3-base
scripts/competition.py brief --wave 3 --name <agent>        # the prompt to give that session (§5)
```

**Collect.** When the agents are done:

```bash
scripts/competition.py collect --wave 3 --dry-run   # print accepted / rejected with reasons
scripts/competition.py collect --wave 3             # copy each accepted file into this checkout
```

Every branch `competition-wave3-*` except `-base` is checked; see §4 for why this copies and
never merges, and §5 for the acceptance rule. Then gate the collected files the way any
package is gated:

```bash
dotnet build MTile.Core.csproj
dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~FighterPackage"
```

**Score.**

```bash
dotnet run --project MTile.Bench -c Release -- --competition score --pool Fights/competition/wave3.pool.json
#   -> Fights/competition/wave3.md  (default --out), also printed to stdout
```

With no `--submission`, the submissions are every participant in this checkout that is not in
the pool. `--submission Name` (repeatable) scores a subset; `--quiet` silences per-bout
progress. Use `-c Release`. A run is deterministic: the same build, pool and submissions give
the same file, byte for byte (no timing columns).

**Publish.** Commit `Fights/competition/wave3.md` with the accepted package files, and add a
line to the plan's campaign log (§16) and a lessons section to the guide, as for waves 1
and 2.

**Roll** (§6) once wave 3 is closed:

```bash
dotnet run --project MTile.Bench -c Release -- --league --seeds 16 --frames 1800 --no-record --out Fights/wave3_full
dotnet run --project MTile.Bench -c Release -- --competition roll --pool Fights/competition/wave3.pool.json --ladder Fights/wave3_full/league.md --wave 4
#   -> Fights/competition/wave4.pool.json  (default --out)
```

The full league covers pool 3 plus the wave-3 submissions, which is what `roll` requires of its
ladder. `Fights/wave3_full/` is ignored; copy its `league.md` to a tracked
`Fights/*_league.md` if the ladder is to be published (the roll's pool file names its source).

## 3. Scoring rules

Each submission is scored **one versus the pool**, never all-to-all: the submission plays the
pool, the pool does not replay itself.

| Setting | Value |
|---|---|
| Frames | 1800 per bout (the league default is 720; the competition always uses 1800, the clock under which draws fell to 15% in `Fights/wave2_long_league.md`) |
| Terrains | all three (flat, corridor, hills) |
| Scoring seeds | 100..115 (`FirstSeed = 100`, `Seeds = 16`) |
| Spawn orders | both, as the league always plays them |
| Points | win 1, loss 0, draw `FighterCosts.Current.DrawScore` = 0.4 (plan §11.2); a timeout is a draw |

Competitors develop on the league's seeds 0..15 (`--first-seed` defaults to 0). Scoring
seeds are fresh so spawn geometry and brain streams cannot be tuned to.

- **Holdout score** (the primary rank): the league score of the submission in a league with
  `Include = Holdout ∪ {sub}` and `Only = {sub}`, so only the submission's bouts are played.
  For wave 3 that is 6 opponents × 3 terrains × 2 orders × 16 seeds = 576 bouts.
- **Dev score** (diagnostic): the same against Dev (13 opponents for wave 3, 1248 bouts).
- **Overfit gap** = dev − holdout (diagnostic). Positive means the submission did better
  against what its author could see.
- **Head-to-head** (tiebreak only): a league among the submissions alone, same frames and
  seeds, run when there are at least two. Reported as its own ladder and matrix.
- `±se` is the standard error over seeds, as in the league.

**Sort order:** holdout score descending, then head-to-head score descending, then ordinal
name. Dev score and the gap never move a rank.

A submission whose spec does not compile is listed as disqualified with the reason and not
ranked. The report (`CompetitionReport.Markdown()`): a header (wave, pool, frames, seeds,
draw score); the main table (rank, name, author, holdout ±se, dev ±se, gap, head-to-head,
W-D-L vs holdout, W-D-L vs dev); the head-to-head ladder and matrix; the pool's Dev and
Holdout lists.

## 4. Isolation and leakage

**Isolation is physical.** Packages are discovered by reflection, so a package whose file is
not in the tree does not exist for any tool the competitor can run: the league, the forge,
`--spec`, `--record-fight`, the tests. Deleting the holdout files on the base branch is the
whole mechanism; there is no flag to forget.

**What stays visible, and why that is acceptable.** The ladders (`Fights/*_league.md`), the
headline fight files in `Fights/`, the guide's lessons (§8 to §11) and the plan's campaign log
all name holdout bots, their scores, their designs in a sentence, and what beat them. That is
published history, the same as any real competition's past results. A competitor that reads
"Phalanx: Harrier's chassis with an 800 px/s shot and Health 6" still has to design against
it blind. What is hidden is the code: the exact spec numbers and the brain.

**The git-history caveat.** The base branch deletes the files in its tip commit, but every
earlier commit still holds them, and a worktree shares the repository's object store. A
competitor can recover a holdout package with one `git show`. The guide forbids it (§12), and
the supervisor spot-checks agent transcripts for `git log`, `git show` or checkouts that
touch `Packages/`. The stricter option, not the default: give competitors a fresh clone whose
only commit is a squash of the base tree (an orphan branch with no parent), so the history
is not there to read. It costs the shared object store and the one-command worktree setup,
which is why it is documented and not used.

**Why collection copies one file and never merges.** The earlier waves were collected with
the merge-worktrees procedure (`.claude/skills/merge-worktrees/SKILL.md`): `git merge --no-ff`
each agent branch. Here that would be wrong. Every submission branch descends from the base
commit, and the base commit is the `git rm` of the holdout packages, so merging a submission
into `fighter-plan` carries those deletions with it and wipes half the field from the
supervisor's tree. (The merge procedure also requires a clean primary checkout, and this one
is dirty on purpose.) `collect` instead reads the one file from the branch
(`git show <branch>:<path> > <path>`) and writes it into the primary checkout. The branch's
commits are never part of `fighter-plan`'s history.

## 5. What a competitor gets and must deliver

**Gets:** a worktree at `.claude/worktrees/<name>` on branch `competition-wave<N>-<name>`, cut
from `competition-wave<N>-base`. That tree is `fighter-plan` minus the holdout package files:
the Dev packages, the roster, every tool, the guide, the published ladders and lessons.

**And one brief, the same for everyone.** `Plans/FIGHTER_COMPETITOR_BRIEF.md` is the prompt an
authoring session receives, rendered for its slot by

```bash
scripts/competition.py brief --wave 3 --name opus-1          # Author defaults to opus-w3
```

Edit the template, never the rendered text: within a wave every class gets the same words, so a
difference in results is the author, not the prompt. Wave 3 slipped here once (the Opus and
Fable sessions got one extra sentence about mirror draws; the Haiku and Sonnet ones did not),
which is why the brief now lives in a file. A later wave may change the brief, for the whole
wave.

**Delivers:** one new file, `Entities/Enemies/Fighters/Packages/<Name>.cs`, committed on its
branch. The acceptance rule is mechanical:

```
git diff --name-only competition-wave<N>-base..competition-wave<N>-<name>
```

must list exactly one path, a newly added file under `Entities/Enemies/Fighters/Packages/`.
Anything else (an edited shared file, a second file, a test, a config, a modified existing
package) rejects the branch. This is plan §13.5: a brain is untrusted code running in
process, so the supervisor takes exactly one reviewable file and nothing that changes the
arena it is judged in.

The package rules are unchanged from waves 1 and 2 (guide §1 and §5): the spec compiles with
zero violations under the default budget (`FighterCompiler.Compile(spec, new
PhysicsCostModel())`), the brain is deterministic and under the compute budget, and the class
lives in a file named after its `Name` (the pool's file mapping depends on it from the next
wave on).

A wave's submissions are by definition every participant in the supervisor's checkout that
is not in the pool. Keep stray packages out of `Packages/` between the split and the score.

## 6. Rolling, and why scores stay static

When wave N closes, pool N+1 is pool N plus wave N's submissions, re-split from a fresh full
ladder (`--competition roll`). The re-split means a package can move between Dev and Holdout
from one wave to the next as its rank changes, and a new wave's competitors see last wave's
Dev half plus whichever submissions land in Dev.

A wave's scores are never recomputed. A wave-3 score means "against pool 3, on seeds
100..115, at this build", and that is what it is reported as. Re-scoring old submissions
against a bigger pool would turn the number into an open-pool rating (plan §13.2), which
moves by construction and stops being comparable to the day it was earned. The fixed-pool
number is the reproducible one: the pool file and the build reproduce it exactly. A later
sim or pricing change (a retune, like retune 1's fixed timing) makes old numbers stale
rather than wrong; record the commit a wave was scored at with its report.

## 7. Open questions

- **Publish the split after the wave?** The pool file is committed, so the holdout names are
  public once the wave's report is. That is fine for wave N, but wave N+1's competitors then
  know which bots were hidden last time; the re-split limits what that tells them.
- **Sample size when the holdout half is small.** Wave 3's holdout is six packages; the score
  is a mean over six opponents and the roster is all Dev. One hard counter in the holdout half
  moves a submission's score by up to a sixth. The `±se` covers seeds, not the choice of
  opponents.
- **Hold out seeds and terrains too?** Scoring seeds are already fresh; terrains are not.
  A held-out fourth terrain would test whether a brain handles ground it never saw, at the
  cost of a terrain that must be built and kept out of every competitor's tree.
