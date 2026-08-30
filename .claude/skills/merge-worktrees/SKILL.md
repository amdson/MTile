---
name: merge-worktrees
description: Use when collecting finished work from parallel agent worktrees back into main — surveying .claude/worktrees/ for unmerged branches, dry-running each merge, landing the clean ones with a build check, and bailing out cleanly on conflicts that need a human judgment call.
---

### Merging agent worktrees into main

Parallel agents each work on `worktree-<name>` in `.claude/worktrees/<name>/`. This skill lands the
finished ones on `main` **one at a time, verified**, and stops at anything that needs the user.

The whole point is the failure mode: a merge you can't resolve must leave `main` byte-identical to
where it started. Never leave a conflicted index, a half-resolved file, or a merge commit that
doesn't build.

#### 1. Preflight

```bash
git -C "$(git rev-parse --show-toplevel)" status --porcelain   # main must be clean
git rev-parse main                                             # RECORD THIS — the undo anchor
```

If the primary checkout is dirty, stop and ask — don't stash the user's in-progress edits.
Report the recorded SHA at the end so the user can undo everything with
`git reset --hard <sha>`.

#### 2. Survey

```bash
.claude/skills/merge-worktrees/survey.sh          # all unmerged branches
.claude/skills/merge-worktrees/survey.sh 7        # only those touched in the last 7 days
```

Read-only — it dry-runs every merge with `git merge-tree --write-tree`, which resolves against the
object store and never touches a working tree. Columns: age, commits ahead of main, files changed,
conflict count (or `clean`), and the worktree's name plus its uncommitted-file count.

Interpreting it:

- **`clean`** — merge it (step 3).
- **`** N UNCOMMITTED **`** — an agent is mid-edit or forgot to commit. **Skip the branch entirely.**
  Merging its tip silently drops that work; committing on its behalf is not your call.
- **Locked worktree** (`git worktree list` shows `locked`) — a session is probably still live in it.
  Its commits are immutable so merging the current tip is safe, but more may land after you.
  Merge it, then say so in the report; never delete the branch or worktree.
- **Age ≫ others** — a 10-day branch has drifted under everything merged since. Expect conflicts,
  and weight toward giving up rather than reasoning through a big three-way diff.

Merge **clean branches first, cheapest first** (fewest files), and **re-run the survey after every
merge** — each merge moves `main`, so conflict counts go stale immediately. A branch that showed
one conflict often goes clean once a related branch lands, and vice versa.

#### 3. Merge one branch

```bash
BR=worktree-foo
BASE=$(git rev-parse HEAD)                    # per-branch undo point
git merge --no-ff "$BR" -m "Merge branch '$BR'"
```

`--no-ff` on purpose: it keeps each agent's work as one revertable unit, matching this repo's
history.

Then **verify — a textually clean merge is not a correct merge.** Two agents adding an
`ActionState`, a priority constant, or a registry entry merge without complaint and then don't
compile:

```bash
dotnet build MTile.Core.csproj                # fastest correctness check
```

Build fails and the fix isn't a one-line mechanical reconcile (a duplicated `using`, two entries
appended to the same enum/registry that just need both kept):

```bash
git reset --hard "$BASE"                      # branch skipped, main restored exactly
```

Do **not** start refactoring merged code to make it build. That's the user's judgment call — a
semantic clash between two agents means both designs need a decision, not a patch.

> Gotcha: if the game is running, `MTile.exe` is file-locked and the *copy* step of a
> Desktop/Tests build fails after a successful compile. `MTile.Core.csproj` alone avoids it.

#### 4. Conflicts — resolve or give up

On conflict, the default is **give up**: `git merge --abort`, record the reason, move on to the next
branch. Resolve only these, and only when the conflict is genuinely of this shape:

| Resolve | Why it's safe |
|---|---|
| `BACKLOG.md`, `CODEBASE_OVERVIEW.md`, `Plans/*.md`, `README.md` | Both sides append to a list; keep both sides, in branch-date order. |
| `Audio/SoundManifest.g.cs`, the `GENERATED SOUNDS` region of `Content/Content.mgcb` | Generated. Never hand-merge — take either side, then regenerate with `scripts/sync-sounds.ps1` and commit the result. |
| A `using` block, or two independent methods added to the same class at the same spot | Keep both; the conflict is adjacency, not disagreement. |
| `.csproj` item groups | Additive; keep both, drop exact duplicates. |

**Always give up on these** — each is a design decision, not a merge:

- **`configs/*.json`** — `movement_config`, `anim_solver_config`, `impact_profiles`,
  `material_strengths`. Conflicting numbers are two agents' playtest tuning. Picking one, or
  averaging, silently discards calibration that was verified by feel. Hand it back.
- **`Character/Movement/MovementPriorities.cs`** — colliding priority values change preemption
  across the whole FSM. The file is the single source of truth precisely because it's decided in
  one place.
- **Corrector / animation-solver internals** (`Character/Corrector/*`, `Animation/*`) — both sides
  rewriting the same solve is never a textual merge.
- **Rename vs. edit** — one branch moved a file (e.g. the `Character/` reorg), another edited it in
  place. Git shows this as delete/modify; resolving it by hand loses one side.
- Any hunk where **both sides rewrote the same function body** differently.

Rule of thumb: if resolving requires you to decide *which behavior is correct*, it isn't a merge
conflict, it's a design conflict. Abort.

```bash
git merge --abort && git status --porcelain    # must print nothing
```

#### 5. Sanity check the batch

Once the clean merges are in, run the sweep (~18 s — it already excludes the slow outliers):

```bash
scripts/test-group.py full
```

Per-merge, the group covering what the branch touched is enough and costs seconds; see the table in
CLAUDE.md or `/test-slices`. Save `full` for the end of the batch.

Check reds against **BACKLOG.md §5**'s known-failing table before blaming a merge. A genuinely new
failure that traces to one merge: `git revert -m 1 <merge-sha>` for that branch, note it, keep the
rest.

Merges stay **local** — don't push unless the user asks.

#### 6. Report

State, per branch: **merged** (with the merge SHA), **skipped** (with the reason and the exact
conflicting paths), or **blocked** (uncommitted work in its worktree). For every skip, give the
command to pick it up by hand:

```bash
cd .claude/worktrees/<name> && git merge main    # resolve on the branch, then re-run this skill
```

Merging *main into the branch* is usually the friendlier direction for a human — they resolve in
the agent's own context, and the next survey pass sees the branch as clean.

Finish with the undo anchor from step 1: `git reset --hard <sha>` reverts the entire batch.

#### Cleanup (only when asked)

Merged worktrees are safe to remove, but they're cheap and an agent may still be attached, so don't
do it unprompted:

```bash
git worktree list --porcelain | ...            # confirm branch is merged AND worktree unlocked
git worktree remove .claude/worktrees/<name>
git branch -d worktree-<name>                  # -d, never -D: it refuses if not merged
```
