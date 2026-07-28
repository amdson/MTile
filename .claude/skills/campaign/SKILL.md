---
name: campaign
description: Multi-hour autonomous implementation of a written plan. Fable supervises, delegating parcels to 0+ Opus subagents and verifying their work, committing per milestone, self-pacing with wakeups until the plan is done or blocked.
---

# Campaign: plan-driven autonomous implementation

Invoked as `/campaign <plan file or task description>`, optionally with a token
budget directive (e.g. `+400k`). The user has written an extensive plan (usually
in `Plans/`) and wants it implemented over multiple hours without supervision —
often on the GCP dev box in tmux. You are the supervisor: you own scope,
sequencing, verification, and the final state. Opus subagents are your workers
when — and only when — the work decomposes cleanly.

This is the opposite of /rapid: full engineering standards apply. Tests
written/updated per milestone, invariants preserved (determinism, KNI/web
parity), no TEMP hacks unless the plan itself calls for them.

## Startup protocol (once, at invocation)

1. Read the plan file end to end. Read enough of the referenced code to know
   whether the plan still matches reality; note divergences.
2. Triage the plan into:
   - **Mechanical items** — decomposable, verifiable by build/tests. Campaign fodder.
   - **Judgment items** — design decisions, taste calls, anything the plan
     leaves open. These are NOT yours to decide overnight.
3. If the user is present (interactive invocation), surface the judgment items
   and any plan/reality divergences NOW, in one batch — this is the last
   question you may ask. If unattended, log them to the plan file (see Durable
   state) and implement only what the plan fully specifies.
4. Confirm the working branch. Never campaign on main; create or continue a
   feature branch.

## Execution loop

Work milestone by milestone (a milestone ≈ one plan checklist item or a
coherent group). For each:

1. **Decide solo vs delegate** (see rubric below).
2. Implement or dispatch.
3. **Verify before integrating.** Delegated output is untrusted: read the
   diff, build (`dotnet build MTile.Core.csproj` minimum, full `MTile.sln` +
   web build when the milestone warrants), run the relevant tests. Rework or
   redo rather than integrate something you can't defend.
4. **Commit per milestone** with a clear message; push to the remote branch
   (the box is ephemeral — unpushed work is at risk).
5. Update the plan file (status + log), then schedule the next hop.

## Delegation rubric (the "0+" in 0+ Opus)

- **Zero agents (solo)** when the work is design-coupled, touches shared
  invariants, or needs one coherent authorial voice. Fleets of workers make
  locally-plausible choices that don't cohere; verification catches wrongness,
  not incoherence.
- **Parallel Agent calls, `model: "opus"`** for a handful of independent
  parcels (one per file/state/test-class). Give each a self-contained prompt:
  the goal, the exact files, the conventions that apply, and what "done" means
  (builds + which tests pass). Independent parcels launch in one batch.
- **A Workflow** for large fan-outs or when findings need adversarial
  verification (N workers → verify stages → synthesis). Invoking this skill
  counts as the user's opt-in to workflows. If the user gave a `+Nk` budget,
  scale the fleet with `budget.remaining()`; otherwise size the fan-out from
  the work-list, not a fixed number.
- Sizing is yours to choose unless the user specified it. Prefer fewer,
  bigger, well-briefed parcels over many small ones.

## Pacing and liveness

- After each milestone, schedule a wakeup (ScheduleWakeup) whose prompt is
  `/campaign continue` — the skill re-enters and reads the plan file to find
  the next item. Reason string = what the next hop will do.
- Tracked work (builds, subagents, workflows) re-invokes you on completion —
  never schedule short polls for it; use long fallback heartbeats (20+ min).
  Short wakeups only for external state the harness can't see.
- Never end a turn on a question or an unscheduled promise. Either the
  campaign is finished (final report, no wakeup) or a wakeup is scheduled.

## Durable state (compaction WILL happen — files are ground truth)

Maintain in the plan file itself:
- Status markers on each item: `[ ]` / `[~] in progress` / `[x] done @commit`
  / `[!] blocked` / `[?] needs user decision`.
- A `## Campaign log` section: one line per milestone — what, commit hash,
  test status.
- A `## Decisions needed` section: judgment items hit mid-run, each with 2-3
  sentences of context so the user can rule on them cold.

Commit the plan file with each milestone. After any compaction, re-read the
plan file before trusting your memory of progress.

## Blockers and stop conditions

- An item that fails **3 distinct attempts** gets marked `[!]` with a note and
  skipped; move to independent work. Do not grind.
- A mid-run judgment call gets marked `[?]`, logged, skipped. Do not decide it.
- **Stop the campaign** (final report, no further wakeups) when: all items are
  done/blocked/deferred; the budget is exhausted; or continuing would require
  a user decision for everything that remains.
- Final report: milestones completed (with commits), test suite status, items
  blocked and why, decisions awaiting the user, and the single next action you
  recommend.

## Environment assumptions

- Permissions must not stall the run: the project allows `dotnet *`; runs
  should use acceptEdits (or auto) mode. If a permission prompt is possible
  for a planned operation, restructure to avoid it rather than risk hanging.
- On the GCP box: bash, not PowerShell; push early and often.
