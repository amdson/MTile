---
name: perf-profiling
description: Use when measuring or regression-checking MTile performance — the MTile.Bench harness (sim + animation-solver µs/frame, --check against baseline.txt, --corrector, the browser qp_bench), and the in-game per-pass frame profiler with its F11 console dump.
---

# Performance & frame profiling

```bash
# Performance (see Plans/PERF_AUDIT.md)
dotnet run -c Release --project MTile.Bench              # sim + animation-solver µs/frame
dotnet run -c Release --project MTile.Bench -- --check   # diff vs baseline.txt; exit 1 on a ~1.5x regression
dotnet run -c Release --project MTile.Bench -- --save MTile.Bench/baseline.txt   # re-baseline
dotnet run -c Release --project MTile.Bench -- --corrector             # corrector QP share of a sim tick
node MTile.Web/smoke/qp_bench.js http://127.0.0.1:8080/                # ...the same solve in the browser (AOT publish required)
```

**Frame profiling in-game**: `GameConfig.DebugFrameTimings` (on by default) draws a per-pass
breakdown — `sim / anim / cosmetic / chunks / skins / fx / glow / present / …`, mean+worst over
60 frames. **F11 dumps the last window to the console**, which is how the *web* build is
profiled (the dump lands in devtools). Draw-side scopes measure batch *building*; the GPU
submit lands in `present`. `Diagnostics/FrameProfiler.cs`.
