---
name: test-slices
description: Use when running MTile's xUnit suite and you want the right subset rather than all 489 tests — which Zzz scratch classes to skip for a fast full-coverage run, and the --filter alternations that target combat/action FSM, snapshot/rollback, corrector/fold, or the animation solver.
---

### Running a relevant slice of the suite

The full suite is ~75 s / 489 tests, but **two scratch classes are 65% of that** — `Sim.ZzzLatticeTiming`
(~35 s) and `Animation.ZzzRestSpasm` (~13 s). The `Zzz` prefix marks long-running harnesses, so the
default full-coverage run should skip them (~35 s, 481 tests):

```bash
dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName!~Zzz"
```

Targeted slices, by what a change touches (`--filter` alternations; namespaces are NOT a reliable
selector — `GrabTests` is in `MTile.Tests` while `CaveMouthTests` is in `MTile.Tests.Sim`):

| Area | Filter | ≈ |
|---|---|---|
| Combat / action FSM | `~Combat\|~Grab\|~Escalation\|~AttackRecoil\|~Commitment\|~Eruption\|~ClipBinding\|~ActionOverlay` | 4 s / 77 |
| Snapshot / rollback | `~Snapshot\|~Rollback\|~InputCodec\|~ECS` | 2 s / 15 |
| Corrector / fold | `~Corrector\|~Fold\|~Ballistic\|~CaveMouth\|~BumpyTunnel\|~SpeedInvariant` | ~5 s |
| Animation solver | `~Solver\|~Anim\|~Pose\|~Skeleton\|~NoPen` | ~5 s |

(Prefix each term with `FullyQualifiedName`, e.g. `--filter "FullyQualifiedName~Combat|FullyQualifiedName~Grab"`.)
