---
name: rapid
description: Use when the user wants fast human-in-loop iteration — they are actively playtesting (usually in-game) and want minimal-latency back-and-forth. Activated explicitly via /rapid; stays in effect for the rest of the session unless the user says otherwise.
---

# Rapid human-in-loop mode

The user is sitting in the loop: they run the game, observe, and report. Your job
is to be a fast pair of hands and eyes on the code, not a thorough engineer.
Latency per exchange is the metric. Depth, coverage, and durability are
explicitly NOT the metric.

## Behavior contract

- **Smallest edit that answers the current question.** No anticipating the next
  problem, no adjacent improvements, no "while I'm here" cleanups.
- **No tests unless asked.** Don't write test files, don't run test suites, and
  don't extend diagnostics speculatively. The user's in-game session IS the test.
  If asked to "reproduce" or "trace" something, one minimal diagnostic is fine.
- **Verify with the cheapest build only**: `dotnet build MTile.Core.csproj`
  (or the touched project). Skip the web/KNI build, skip test runs, skip
  multi-project sweeps unless something in the edit specifically risks them.
- **Answer questions as answers.** If the user asks "why does X happen" or
  "is Y plausible", read the code and answer — don't fix, don't instrument,
  don't propose a workplan unless asked.
- **Keep replies short.** Lead with what changed or the answer, 1–4 sentences.
  No headers, no bullet lists of implications, no expectation-setting about what
  they'll observe. They're about to observe it.
- **Mark throwaway edits** with a brief `TEMP EXPERIMENT` comment so the session's
  accumulated hacks are greppable. Keep a running mental list; when the user asks
  "where are we" or "what did we change", summarize the TEMP set concisely.
- **Don't** update plans, memory files, or commit anything mid-loop unless asked.
- **Broken invariants are fine.** Tests may go red, parity may break, energy
  honesty may be suspended — note it in one clause, don't repair it. Cleanup is
  a separate session-end activity, on request.

## When the loop ends

If the user signals wrap-up ("okay, let's keep this" / "clean this up" /
"commit"), exit the mode: enumerate the TEMP edits, ask which survive, and
restore normal engineering standards for whatever gets kept.
