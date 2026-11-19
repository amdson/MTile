# Plants — growable, owned threats that make terrain matter in PvP

> Status: **proposed 2026-11-19, nothing implemented.** Design settled in conversation with the
> user; §2 is decisions, §7 is what is still open. Numbers are starting points, to be kept as
> named constants and tuned by feel.

## 1. The problem this answers

Two players 15 blocks apart on flat ground have no reason to build: a wall between them blocks
both equally, has no clock, and the attacker can simply wait. Building only breaks the symmetry
if the thing built is *owned* (treats its owner differently), *accumulates* (waiting costs the
other player), or *carries a clock*. Plants are all three, and their flavour brings the terrain
rules with them — a plant is rooted in a tile, takes time to grow, and is answered by digging,
walling, burying and throwing.

Accepted consequence: the duel becomes **territorial** (home ground / away ground). The thing to
avoid is a **standoff**, which is what the two "breaker" plants and the burial rule are for.

## 2. Decisions

1. **Plants are growable, owned, and few.** A hard per-player cap (start: 3 live plants, thorns
   counted separately — §4.3). Each is paid from the build meter, so planting competes with walls.
2. **Five plants in the first set:** Spitter, Snapper, Thorns, Siege pod, Block-eater (§4).
   Fruit bush and a detonate-on-hit explosive are the reserves, not in scope.
3. **Controls: bar swap.** One hotkey flips the number row between the block bar and the seed
   bar; the existing place gesture plants the selected seed. A cursor ghost shows what would be
   planted and whether the spot is legal (§3).
4. **Burial suppresses, it does not kill.** Blocks may be placed over a plant. A covered plant is
   dormant and loses health slowly; uncovered, it resumes (§5).
5. **The siege pod is a one-off.** Its aim direction is set at placement and telegraphed for its
   whole growth. It fires by itself on ripening, as a beam with two parts: a broad blast around
   the pod and a narrow long-range lance. The lance simply does tile damage, tuned per block
   type, and penetrates three layers of stone (§4.4).
6. **Thorn growth is capped** — per patch and per player (§4.3).
7. **Block placement costs get rebalanced** so that blanketing an area is not a cheap reset (§6).
8. **Spikes and the crystal wave stay where they are** — block-bar key 5, input unchanged. Thorns
   are a separate seed that grows spike tiles (§4.3).
9. **The siege pod is not team-filtered.** Blast and lance both hurt its owner, the owner's
   plants and the owner's blocks like anyone else's.
10. **Plants root in dirt only.**
11. **Health lost while buried does not come back.**
12. **Stage: the `adventure` terrain — rolling dirt hills over stone — walled at both edges by
    steep cliffs of indestructible rock** (§8a).
13. **Bots get basic plant strategies, at low effort** (§8b).
14. **Thorns reinforce the blocks they grow on** (§4.3).
15. **Art: code-built polygon figures, written alongside each plant's entity** — somewhat
    detailed, a notch more refined than Prism, but their job is to telegraph and to tell the
    plants apart (§10).

## 3. Controls

- **Bar swap key: `F`.** `PlayerInput.F` is already polled (`Character/Input/Controller.cs:94`),
  carried on the wire (`Net/InputCodec.cs:44`) and compared (`Net/InputPacket.cs:50`), and has no
  gameplay reader — so this costs no netcode change. Edge-triggered (press, not hold).
- **Active bar is per-player sim state**, next to `_activeBlockType`
  (`Character/PlayerCharacter.cs:489`, picker at `:731-735`, snapshot at `:1082`/`:1109`). On the
  seed bar `Num1..Num5` select a seed kind; the block selection is remembered across swaps.
- **Planting reuses the place gesture** (Shift+RMB today, `BlockPlaceAction`). On the seed bar
  plain RMB does the same single placement — a paint stroke of capped plants means nothing.
  No new `ActionState` subclass, so no new clip is owed to `ClipBindingTests`.
- **Siege pod aim:** press places, drag sets the direction, release commits.
- **Cursor ghost** (render-only, declared through `Telegraph`): the plant's silhouette at the
  target cell, tinted legal/illegal; for the siege pod, its aim line.
- **HUD:** `Drawing/HudRenderer.cs:90` (`DrawBlockPickerHud`) draws whichever bar is active.

Scaling past five seeds is a pre-match choice, not more keys: the stage defines its five native
plants (symmetric), or a loadout (asymmetric). Not needed for the first set.

## 4. The plants

Shared rules:

- **Rooted in dirt.** A plant sits in an open cell against a **dirt** face (otherwise the same
  placement shape as a spike tile). When its rooting tile breaks, the plant dies. This is the
  hard counter. Wanting a plant somewhere stony means laying dirt first. Two exceptions: the
  block-eater roots on an enemy-owned block of any material, and a thorn patch needs dirt only
  for its seed cell — its offshoots follow the spike rule (any solid face).
- **Owned.** Carries its placer's team; hurts every other team (the spike rule,
  `SpikeContactSystem.Opposes`). Its owner's attacks do not damage it. The siege pod is the
  exception: it hurts everyone and everything.
- **Grows.** Seedling → mature over a growth time. A seedling does nothing and has little health.
- **Hittable.** Real health pool and hurtbox, so slashes, thrown clods and beams all kill it.

| Plant | Job | Answered by |
|---|---|---|
| Spitter | Poke-tier shots at range; makes waiting cost HP and forces guard for the owner's grab | Wall its sightline, dig its soil, bury it |
| Snapper | Heavy bite at close range, slow re-arm; guards a garden against a rush | Clod from range, bury, bait the bite |
| Thorns | Spreads along surfaces, hurts the other team, toughens the blocks under it; area denial and fortification | Any hit knocks them off; build over |
| Siege pod | One-off beam; kills plants, carves terrain | Kill the seed, more than three layers of stone in the line, step off the line |
| Block-eater | Spreads through enemy-owned blocks and destroys them | Cut a gap, break the infected blocks yourself |

Two holders (long and short range), one denier, two breakers. Every answer is an existing
terrain verb — a plant that only another plant can answer is out of spirit.

### 4.1 Spitter
Fires a slow projectile at the nearest opposing player in range with line of sight. Poke tier
(≤ 0.3 body damage) so a held guard blocks it — its value is forcing the guard. Start: growth
12 s, range ~20 tiles, one shot / 1.5 s.

### 4.2 Snapper
Telegraphed bite on an opposing body within ~2 tiles; heavy tier, beats guard. Long re-arm
(start 4 s) so a baited bite is a real opening.

### 4.3 Thorns
The existing spike tile (`TileState.Spike`, team in `Tile.Team`, arming via `TileSpikeSeeds`)
plus spreading: a planted patch periodically requests one new spike on an adjacent open cell that
touches solid. **Caps:**
- per patch: at most N cells (start 10) and at most R tiles from the seed cell (start 4);
- per player: at most M thorn cells alive in total (start 24);
- growth rate: one cell per ~1.5 s, and a patch whose seed cell is knocked off stops spreading.

**Reinforcement.** A block with a thorn on any of its faces takes reduced tile damage (start
×0.5), applied where every other tile hit lands (`ChunkMap.DamageCell`), so it covers slashes,
impacts and the block-eater alike. It lasts only while the thorn stands: any hit knocks a thorn
off, so an attacker strips the thorns and then cuts the block. Details settled at build time:
- only thorn-grown spikes reinforce, read from the patch table. Hand-placed spikes stay as they
  are — their old 2× reinforcement was removed on purpose when spikes became tiles (2026-11-07);
- nothing is exempt, the siege lance included: its damage is tuned against ordinary blocks and
  goes through the same damage path, so a thorned wall simply absorbs more of it (§4.4);
- whether a single swing that reaches both the thorn and its block gets the reduced or the full
  number is an ordering choice inside `CombatSystem`'s tile pass — pick whichever reads better.

Thorns do not count against the 3-plant cap; a player may have a limited number of patches
(start 2). Hand-placed spikes (block-bar key 5) and the crystal wave are untouched: a thorn patch
is a second way to get spike tiles, not a replacement, and hand-placed spikes are outside the
thorn caps.

### 4.4 Siege pod
`Entities/RayEmitter.cs` is already most of this: a rooted node, a locked direction, a telegraph
phase drawn as a silhouette, terrain-occluded reach recomputed every frame, and a disc blast at
end of life. The pod is that shape with different numbers:

- **Growth = telegraph.** The aim line is visible to both players from the moment of planting.
- **Fires once** on ripening, then is gone. Two components in the same instant:
  - *broad local* — a disc blast around the pod (RayEmitter's is 55 px);
  - *narrow long* — a thin lance along the aim, far longer than a spitter's range.
- **Heavy tier on bodies**, kills plants it touches.
- **The lance does tile damage, tuned per block type.** It carries a damage budget down the
  line and deals real tile damage through the ordinary path (`ChunkMap.DamageCell`): each cell
  takes what it needs to break, and the lance goes on with what is left. The per-material lance
  numbers are tuned so that against **ordinary, undamaged** blocks it gets through:

  | Material | Lance gets through |
  |---|---|
  | Stone | 3 cells, stops at the 4th |
  | Dirt | 12 cells |
  | Sand, Foam | close to unlimited |
  | Hardened | nothing — stops at the first cell |

  So a stone cell costs four dirt cells, and a mixed line adds up (one stone + eight dirt is the
  whole budget). This is deliberately not proportional to `MaxHP` (stone 12, dirt 2); the lance's
  per-material numbers are their own named constants.

  Nothing else is special-cased — the engine decides the rest. A thorn-reinforced block (§4.3)
  absorbs more, so thorned stone is cover at fewer than four layers; an already-damaged block
  absorbs less. RayEmitter's march-to-first-solid reach has to become a march that spends damage
  as it goes.
- **Not team-filtered.** Blast and lance both hit the owner, the owner's plants and the owner's
  blocks. Planting one in the middle of your own garden costs you the garden.
- Killing the seed early removes it with no blast (RayEmitter's rule).
- Start: growth 20 s; one siege pod alive per player.

### 4.5 Block-eater
Planted on a block owned by the *other* player (`World/BlockOwnership.cs`). Spreads cell to cell
through contiguous enemy-owned blocks, damaging each through `ChunkMap.DamageCell` so
`OnTileBroken` and the journal behave as for any other break. Stops at unowned or own terrain —
a gap is a firebreak. Most new sim behaviour of the five: a sparse per-cell table on `ChunkMap`
with `Capture`/`Restore`, like `TileSpikeSeeds`.

## 5. Burial

- A plant whose own cell becomes `Solid` is **dormant**: no growth, no firing, no ripening.
- While covered it loses health slowly (start: dead in ~10 s — longer than an unpressured
  dig-out, shorter than a full exchange). Uncovered, it resumes.
- Consequences wanted: the material spread does the pricing (stone burial is dear and slow to dig
  out; foam is cheap and comes off in one hit), and an owner may bury their own plant to shelter
  it at the price of dormancy.
- **Lost health does not recover.** Repeated short burials wear a plant down, so cheap foam
  burial is a real attrition tool and sheltering your own plant is a spend, not a free store.
- A buried plant must stay visible through the block (render-only).

## 6. Economy

The reset to prevent: blanket the garden from range, cheaply. It needs reach, low cost and an
outright kill; §5 already removes the kill. In order of preference for the rest:

1. **One meter.** Seeds and blocks spend the same `BuildMeters` pool, so blanketing leaves nothing
   to plant with.
2. **Tune the avalanche, not the tile price.** It is the tool that covers area from range
   (`todo.txt`: "slow down avalanche block generation"). Raising every tile's cost would tax the
   ordinary building this plan is meant to increase.
3. **The block-eater** makes a blanket of owned blocks a liability to the player who laid it.
4. Held in reserve: building on or beside enemy-owned ground costs more than building at home.

## 7. Open questions

None on the design. What is left is tuning: every number marked "start" in §4–§5, and the
arena width in §8a.

## 8. Build order

Each phase is playable and tests one claim.

0. **Plumbing.** Bar swap, seed selection, ghost, HUD. No plants yet.
1. **Plant base + Spitter.** Rooted entity with growth, team, root death, burial (§5), cap, cost.
   Claim: at 15 blocks, the player without a spitter is forced to move.
2. **Siege pod**, from `RayEmitter`. Claim: a garden is breakable from range; no standoff.
3. **Thorns** — spreading + caps on the existing spike tile.
4. **Snapper.**
5. **Block-eater.**
6. **Economy pass** (§6) once blanketing can be observed in play.
7. **Bots** (§8b).

The duel stage (§8a) is wanted from phase 1 on — the claims above cannot be tested on `spires`.

### 8a. Stage

The `adventure` terrain, closed off. `Levels/adventure.json` already produces what plants need:
the `WorldGenerator` with only its rolling base on (hill and spire amplitudes 0), no sand
(`SandMin`/`SandRange` 0), a flat ten tiles of dirt (`DirtMin` 10) and solid stone beneath. So the
surface is rootable everywhere, with no soil-laying. Reuse those settings as they are.

What is new is the edges: steep walls of `Hardened` rock (120 HP, unplaceable, ungrabbable; the
lance stops at it) on the left and right, so nobody can walk away from a garden.
`World/WorldGenerator.cs` has no notion of a boundary today — it is endless by design — so this
is a small generator option (beyond ±N tiles, emit `Hardened` from well above the tallest hill
down) plus a new level json and a `Stages` entry beside `adventure` (`Stage.cs:397`). The arena
width N is a tuning number; start a few screens wide.

### 8b. Bots

Expected to be hard, so deliberately small. No learning, no new bot family — a few maneuvers on
the existing controller:

- plant a spitter on nearby dirt when the opponent is far and none is alive;
- treat an opposing plant in reach as a target worth a few swings;
- step off a telegraphed siege line and out of the pod's blast disc.

Out of scope: garden layout, sieging with pods, burial, soil-laying, block-eater play. A bot that
does the three things above is enough to playtest against; stop there.

## 9. Implementation notes

- **Plants are entities, thorns and the eater are tile tables.** Spitter / Snapper / Siege pod are
  rooted `Entity` subclasses like `RayEmitter` and `EnemySpawner`: health, hurtbox, telegraph and
  snapshot come with the base. Each needs an `EntityKind` and an `EntityFactory.Rehydrate` entry.
- **Rollback.** Everything that varies rides `EntityData` or a `ChunkMap` sparse table with
  `Capture`/`Restore`; no sim-affecting statics; the active bar and selected seed join the player
  snapshot. Extend `SnapshotRoundTripTests`.
- **Sim never draws.** Ghosts, aim lines and buried-plant outlines go through `Telegraph(...)`.
- **Root death and burial** read the plant's own two cells. The convention is world reactions via
  `ChunkMap.OnTileBroken`, but its subscribers are cosmetic today; spikes are cleared inside
  `BreakCell` itself (`ChunkMap.cs:848`). Decide at phase 1 whether plants hook the same path or
  check their cells in `Update`.
- **Tests.** Name new test classes so they match a term in `scripts/test-group.py` (`combat` /
  `terrain`), or they run only in the full sweep.
- **Both hosts.** Nothing here should need a DesktopGL-only API; check the web build at phase 0.

## 10. Art

No AI-generated art. Placeholders are **code-built polygon composites**, and hand-drawn sheets
replace them later without touching plant code.

**The bar:** somewhat detailed — slightly more refined than `PrismFigure` — but the figures exist
to *telegraph* (growth stage, about to act, dormant, which way the pod points) and to
*differentiate* (five plants and two teams readable at a glance at gameplay zoom). Detail that
serves neither is not wanted. **Each figure is written in the same phase as its entity** (§8), so
its poses match the states the plant really has.

Both halves already exist:

- `Drawing/PrismFigure.cs` and `Drawing/QuillbackFigure.cs` are the pattern: a figure class with a
  pose enum and a `Frame(...)` function taking a few named scalars (hover, sway, glow…), from
  which each `SpriteAnimation`'s keyframes are built. One `PlantFigure` per plant, in that style.
- `Drawing/EntityArt.cs` (`EntityArtSystem`, `SheetSprite`) swaps an entity's code-drawn sprite
  for an authored sheet when one exists and keeps the original as the fallback. That is the slot
  the real spritesheets drop into.

Poses each plant needs: seed, sprout, mature idle (sway), act (spit / bite / fire), dormant
(buried), wilt (dying). The siege pod also swells through its growth and takes its aim angle.
Thorns and the block-eater are tile-shaped, so they belong with the tile renderer
(`Drawing/ChunkRenderer.cs`), not a figure. All of it is render-only and reads plant state; none
of it feeds back into the sim.

## Campaign status

Branch `plants`, worktree `.claude/worktrees/plants` (cut from main `dd0357c`, 2026-11-19).
Supervisor integrates; parcels are built by subagents in their own worktrees and merged here.

- [x] M0 Plumbing — bar swap (`F`), seed selection, cursor ghost, HUD @c96def0
- [x] M1 Plant base (root, growth, team, burial, cap, cost) + Spitter + its figure @c96def0 (figure, ghost, HUD not yet seen on screen)
- [x] M2 Duel stage `garden` — adventure terrain walled with `Hardened` (§8a) @fbcb20c (not yet run in game)
- [~] M3 Siege pod (§4.4) — figure merged @fbcb20c; firing mechanics in flight; entity wrapper to do
- [~] M4 Thorns: spreading, caps, reinforcement + tile art (§4.3)
- [~] M5 Snapper (§4.2) — figure merged @fbcb20c; entity in flight
- [~] M6 Block-eater + tile art (§4.5)
- [x] M7 Economy: seeds on the build meter (§6.1), done with M1. Avalanche retune (§6.2) is feel work — not done here
- [ ] M8 Bots: the three behaviours in §8b
- [ ] M9 Full sweep, web build check, docs (overview + area maps)

## Campaign log

(one line per milestone: what, commit, tests)

- M0+M1 foundation — c96def0, 9ab685b, c00117b. 29 plant tests green. Contracts for the other
  plants are in the header comment of `Entities/Plants/PlantEntity.cs` and in `SeedPlanting.cs`.
  Deviation: a buried plant can still be hit through its covering block (see Decisions needed).
- M2 stage + snapper figure + siege pod figure merged — fbcb20c. Build green; 62 plant / garden /
  snapshot / input-codec tests green.
- Baseline reds: the combat+terrain+simcore groups show ~40 reds outside plant code, over a dozen
  of them not in BACKLOG §5's table. To be triaged at M9, not assumed pre-existing.

## Decisions needed

- **Can a buried plant be hit through the block covering it?** Today yes: hit occlusion never
  counts the target's own cell. Burying your own plant to shelter it (§5) therefore protects it
  from line-of-sight attacks coming from elsewhere but not from a swing at the block itself.
  Options: leave it; or make a buried plant unhittable until the block is broken.
