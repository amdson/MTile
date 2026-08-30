---
name: test-slices
description: Use when running MTile's xUnit suite and you want the right subset rather than all ~724 tests — the seven subsystem groups behind scripts/test-group.py, which source dirs map to which group, the slow outliers excluded by default, and one-off --filter recipes.
---

### Run a group, not the suite

```bash
scripts/test-group.py list                 # groups and their match terms
scripts/test-group.py combat               # one group
scripts/test-group.py combat animation     # several (they may overlap)
scripts/test-group.py full                 # sweep, minus the slow outliers  (~18 s / 714)
scripts/test-group.py full --slow          # literally everything            (minutes)
scripts/test-group.py combat --no-build    # trailing args pass through to `dotnet test`
```

The default is the group matching what you edited. `full` belongs at the end of a piece of work,
before a commit, or when the change is genuinely cross-cutting — a shared base class, `Hitbox` /
`HitResolver`, the ECS, a config loader, `InputParser`.

| Group | Source it covers | ≈ |
|---|---|---|
| `combat` | `Character/Action/`, `Character/Input/`, `Entities/`, `World/Hitbox.cs`, `World/CombatSystem.cs`, `Physics/HitResolver.cs`, `Presentation/` | 2 s / 238 |
| `movement` | `Character/Movement/`, `Character/Sensing/` | 3 s / 177 |
| `corrector` | `Character/Corrector/`, lattice + fold engines | 9 s / 130 |
| `terrain` | `World/` tiles + chunks, block build/paint/burst/peel | 2 s / 151 |
| `physics` | `Physics/` | 1 s / 20 |
| `animation` | `Animation/`, `Skeletons/`, `SkeletonStates/`, rig code in `Drawing/` | 1 s / 91 |
| `simcore` | `Simulation.cs`, `Sim/`, `Net/`, `configs/`, `Stage.cs` | 4 s / 69 |

### Things that bite

**Matching is on test-class NAME, not namespace.** Namespaces are not a usable selector here —
`GrabTests` is in `MTile.Tests` while `CaveMouthTests` is in `MTile.Tests.Sim`. The groups are lists
of name substrings in `scripts/test-group.py`; a term like `Jump` catches `JumpingStateTests`,
`ArcJumpStateTests` and `VaultJumpAndLipReproTests` alike.

**A new test class whose name matches no term runs only in the full sweep.** That is how group
coverage rots. When adding one, either name it so an existing term catches it or add a term.

**Two slow outliers are excluded from every group and from plain `full`:** the `Zzz*` scratch/timing
harnesses, and `BuriedInIntactTiles` — 2 tests that are the large majority of the suite's wall clock
(the whole run is ~2m47s with them, ~18 s without). They are real tests, so `full --slow` before a
release-ish milestone; not in an edit-test loop.

**Parallelization is off assembly-wide** (`MTile.Tests/TestAssemblySetup.cs`) because sim tests
mutate `MovementConfig.Current`. Group runs are fast because they are small, not because they are
parallel.

**Red does not mean you broke it.** BACKLOG.md §5 carries the known-failing table (4 corrector, 1
anim-solver at the time of writing). Check there before investigating.

### One-off filters

```bash
dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~SnapshotRoundTrip"
dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~DownAirSlash&FullyQualifiedName!~Zzz"
dotnet test MTile.Tests/MTile.Tests.csproj --filter "..." --logger "console;verbosity=detailed"   # see ITestOutputHelper lines
```

`--logger "console;verbosity=detailed"` is how you read `output.WriteLine` diagnostics; without it
they only appear for failing tests.

**Gotcha:** while the game is running, `MTile.exe` is file-locked and the build's copy step fails
even though the compile and test dll succeed. Add `--no-build`, or close the game.
