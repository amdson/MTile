# Block Eruption — Design Notes

Reading your todo entry, I want to push back gently on the mass-ball simulation and propose a simpler model that I think captures the same feel with a fraction of the moving parts. But first, let me restate the goals I'm reading out of your description, because they're the load-bearing thing — the implementation should follow them, not the other way around.

## Feel goals (what I think you're after)

1. **Bursty fall-off along the path.** Lots of blocks at the start, trickling to a point at the end. The shape *visually traces* the gesture but isn't uniform along it.
2. **Total budget capped by charge time.** A 0.2s charge gets you a small puff; a 1.5s charge gets you a full pyramid. Hard cap somewhere so a held charge doesn't run away.
3. **Speed-shape tradeoff with area conservation.** Slow sweep → wide low mound. Fast sweep → tall narrow tower. Roughly the same *count* of blocks, just packed differently.
4. **Robust to gesture imperfection.** Sweep is a *suggestion*, not literal. Wiggles get smoothed, sharp corners get rounded, double-backs get collapsed.
5. **Ergonomic trigger.** Charge while mouse is inside solid; the eruption *origin* is the exit-from-solid cell, not the press cell.

These five together are what makes the move feel good. Any approach that nails them is fine; my job is to find the cheapest one.

## Your three candidate approaches

| Approach | Verdict |
|---|---|
| **(A) Priority field** — distance-from-path scoring, fill highest first | Underrated. Closest to a clean implementation. |
| **(B) Mass-ball physics sim** — ball loses mass into a field, neighbor spillover, iterate to convergence | Cool but I'd hold for v2. |
| **(C) Bezier curve fit** — smooth the raw mouse input under derivative constraints | Useful as a *preprocessor* for either (A) or (B), not as the main approach. |

### Why I'd defer (B)

The mass-ball simulation is the most physically satisfying framing — I get the appeal. The reason I'd shelve it for v1 is the **coupled tuning surface**:

- Ball velocity model (initial impulse, spring stiffness to puller, mass-dependent inertia)
- Mass leak rate (per second? per pixel? velocity-dependent?)
- Per-cell fill threshold (uniform? terrain-type-dependent?)
- Neighbor spillover rule (uniform 4-neighbor? weighted by distance? cascading?)
- Convergence iteration (how many passes? termination criterion?)

Each one has a "feels weird until tuned" failure mode, and they interact. Once you've sunk a week into making the ball not orbit weirdly when the mouse double-backs, the system is hard to reason about because *the same parameter affects both shape and rate*. You can't change one knob without re-tuning two others.

I'd build (B) only after (A) ships and you can articulate what (A) *specifically* doesn't do that the mass-ball version would.

### Why (C) folds in for free

Whichever core approach you pick, the raw mouse path needs smoothing. A 30-fps mouse sample drawn quickly looks like a jagged polyline, not a curve. The cheapest fix: a velocity-tracked "pen" that low-pass-filters the cursor.

```
pen.Velocity += (mouse - pen.Position) * pullStiffness * dt
pen.Velocity *= damping
pen.Position += pen.Velocity * dt
```

That's exactly the spring-puller idea from (B), but used only for *path smoothing*, not for distributing mass. Slow gestures naturally produce smooth tight curves (pen tracks closely); fast gestures produce smoother wider arcs (pen lags and overshoots round corners). The "minimum curvature" property you mentioned falls out — `pullStiffness` and `damping` set the curvature limit implicitly.

The pen position samples become the input to the priority field. You get curve-smoothing as a one-screen helper, not a Bezier solver.

## What I'd build: priority-field over a smoothed path

```
1. While LMB held + mouse inside solid:
     accumulate charge time → budget = lerp(BudgetMin, BudgetMax, t/MaxCharge)
     (saturating — past MaxCharge, no more)
     
2. On mouse exit-from-solid (the "ignition" event):
     erupt_origin = last cell mouse was inside
     start path sampler
     
3. While LMB still held (in empty space):
     advance a smoothing pen toward mouse, record (position, velocity) samples
     
4. On LMB release (the "fire" event):
     run the eruption with: budget, samples, erupt_origin
```

### Eruption distribution (the core of the system)

Given `N` samples along the smoothed path:

```
budget_remaining = total_budget
for i in 0..N:
    p, v_local = samples[i]
    
    # Front-loaded decay: most blocks come from the first portion of the path.
    weight_at_i = decay(i / N)                    # e.g. (1 - i/N)^2
    
    # Area-conserving radius — wide at slow speed, narrow at fast speed.
    # Product (weight * area) ≈ constant across speeds, so total count is stable.
    radius = baseRadius * sqrt(refSpeed / max(|v_local|, minSpeed))
    
    # Deposit this sample's share into nearby cells.
    sample_budget = weight_at_i * budget_remaining
    for cell within radius of p, where cell is currently empty:
        falloff = (1 - distance(p, cell) / radius)^2
        cell_score[cell] += sample_budget * falloff
    budget_remaining -= sample_budget
    
After loop:
    sort cells by cell_score descending
    spawn sprouts for top K cells where K corresponds to total budget
```

This gives you:
- **Pyramid shape**: scores fall off radially from the path, sharpest near the origin where weight is highest
- **Front-heavy distribution**: `decay()` puts most of the budget in the first samples
- **Speed-shape tradeoff**: fast sweeps shrink `radius`, so cells far from path don't score → narrower; slow sweeps grow `radius` → wider. Area-conservation keeps count roughly constant
- **Hard budget cap**: top-K spawn — explicit upper bound

The "score" idea is essentially your priority-field (A), but the input isn't just distance — it's a path-integrated weight. Same machinery, richer scoring.

### Sprout integration is already a feature, not a bug

The existing `TileSproutGraph` handles sprouts that don't have a solid parent yet — they sit in `Pending` until a neighbor finalizes. When you spawn 50 sprouts in a chunk, the ones touching existing terrain start growing immediately; the rest wait. As the front layer finalizes, the next layer promotes. **Free ripple visual.**

You probably want to spawn from the **closest-to-origin** outward in time so the wavefront propagates in the right direction. Sort spawn order by `distance(cell, erupt_origin)` ascending, and add a small per-spawn delay (or stagger via the existing `SproutLifetime`). The graph already handles the dependency ordering, so spawning all at once also works — Pending sprouts just wait for the wave to reach them.

## Open questions / things to decide

1. **What happens to budget allocated to already-solid cells along the sweep?** Two reasonable answers:
   - **Discard** — solid blocks absorb the "wasted" eruption. Simple, intuitive (you punched into solid; that energy is gone).
   - **Spread to neighbors** — more dynamic but feels weird (eruption "pierces" terrain you can't see through). I'd go with discard.

2. **Should the move work *only* if the mouse exits solid?** Otherwise the player held LMB inside solid forever with no consequence. Probably yes — no ignition, no eruption. The charge fizzles silently if they don't drag out.

3. **Maximum budget cap.** What does a 2-second hold do? My instinct: saturate at ~1s of charge time → ~50-80 blocks. Beyond that, no more. Indicate saturation visually (charge indicator stops pulsing brighter).

4. **What block type spawns?** Match the *origin* tile's type? Dirt by default? User-selectable later? Easiest v1: take the type of the cell where the eruption ignited.

5. **`BlockReady` priority in the action FSM.** It should preempt `ReadyAction` (passive 15) when mouse is in a solid cell — otherwise the player can't distinguish a regular charge from a block-eruption charge. Maybe passive 18-20.

6. **Cancellation.** If the player exits the chunk, dies, or runs out of charge mid-sweep, what happens? Cleanest: if LMB releases while still inside solid (no ignition), nothing fires. If chunk changes mid-charge, abort and refund charge.

## Risks I'd flag

- **Cost scaling.** Each path sample touches ~`radius²` cells. With 30 samples and radius=4 tiles, that's ~30 × 50 = 1500 cell-touches per eruption. Trivial. Even at radius=10 it's 9000, still fine for an on-demand event. Don't pre-emptively optimize.
- **Top-K spawn vs threshold spawn.** Top-K gives a stable count but rejects "natural" cells outside the count even if they scored high; threshold spawns however many cross the bar but the count varies. I'd start with top-K — predictable budget is more important than score-natural cutoff.
- **The "pen" can lag badly on fast straight gestures.** Tune `pullStiffness` so the pen reaches the cursor within a few hundred ms even at max speed. Worst case the pen smooths a straight line into a slightly curved one — visually fine.
- **Interaction with existing build input (right-drag).** Right-click is the existing tile-build mechanism. Left-click in solid is unused, so no direct conflict, but worth confirming the input router cleanly distinguishes the two.

## v1 scope I'd actually ship

| Component | New / Modified | Notes |
|---|---|---|
| `IntentType.BlockEruption` | new enum value | emitted when LMB-in-solid releases after drag |
| `InputParser` | modified | detect: LMB-down inside solid → tracked charge; mouse-exit-solid → ignition; LMB-release → emit intent with `(originCell, smoothedPath[])` |
| `Character/SmoothPen.cs` | new, ~30 lines | spring-pulled smoothing of cursor input |
| `Character/EruptionPlanner.cs` | new, ~80 lines | priority-field accumulator + top-K spawn |
| `Character/ActionStates.cs` | additions | `BlockReadyAction`, `BlockEruptionAction` (both with movement modifier slowdown) |
| `Character/PlayerCharacter.cs` | additions | register new actions |

No physics simulation, no convergence loop, no neighbor spillover. The mass-ball machinery stays in the design folder as v2.

## Bottom line

Your instinct on the mass ball is right that the *feel* it'd produce is excellent — emergent, weighty, surprising. But the priority-field-over-smoothed-path version hits the same five feel goals with maybe a third of the code and a tenth of the tuning surface. Ship that first. If, three weeks after shipping, you find yourself saying "the eruptions are correct but they don't feel *alive* enough," that's when the mass ball earns its complexity. Until then, it's a beautiful pattern with no fitness function pulling for it.

Also: the smoothed-pen helper is a v1-quality piece of code that pays back regardless of which approach wins. Build it standalone first; both (A) and (B) will use it.
