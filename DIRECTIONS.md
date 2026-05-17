# Creative Directions

Three candidate directions for what to build next, all leveraging the systems already in place (action FSM, sprout DAG, hitbox/hurtbox, tile types, entities).

---

## 1. "The terrain IS the weapon" — chosen direction

**One-liner:** Arena combat where reshaping the room *is* the tactic, not flavor on top of combat.

### Core fantasy

You're in a small bounded arena with an enemy (or a few). You can't out-DPS them with the slash alone — the room itself is your toolkit. You mine away the floor under them, sprout a wall to block a charge, grow a pillar to break line-of-sight, then leap up and stab down through it. Every fight is a different room because every fight ends with a different room.

### Why it fits

Already shipped:
- **Action FSM** with combos and stab — the moveset is already richer than most platformers.
- **Diagonal stab hitbox** that punches through tiles separately from entities (`HitTargets.TilesOnly` on the shockwave) — built for this exact use case.
- **Sprout DAG** with Pending/Growing nodes — you can grow structures, not just place blocks.
- **Tile types with HP** — sand crumbles fast, stone is a real wall. Already a tactical axis.
- **Entity system** with mass, gravity scale, knockback — enemies will just plug in.

Missing pieces are scoped and well-defined (see below).

### What needs to be built

In rough dependency order:

#### a) Enemies with telegraphed attacks

A first enemy type — say a **Charger** — that:
- Patrols a platform, sees the player, winds up (visible telegraph: color flash + brief pause), charges horizontally.
- Has its own action FSM mirror (Idle / Aggro / Windup / Charge / Recover).
- Reuses `Entity` + `IHittable` (already done). Adds an `EnemyAI` component or subclass.
- The wind-up is the key — it's the window where the player decides "do I dodge, parry, or *reshape the floor*?"

A second type — **Turret** — fires projectiles on a clock. Stationary. Forces the player to use cover, which the player has to *build* or *expose* via sprouting/mining.

#### b) Tactical sprout types

Right now every sprout becomes a generic solid tile. Give each `TileType` its own sprout behavior at finalize time (this is the "Sprouts that do things" idea from #3, folded in as a prerequisite):

- **Sand sprout** — finalizes Solid but decays over ~3 seconds. Useful for temporary cover during a wind-up, then it crumbles.
- **Dirt sprout** — finalizes Solid, normal HP. The bread-and-butter wall.
- **Stone sprout** — finalizes Solid, high HP, but takes longer to grow (longer `SproutLifetime` for stone). High commitment, high payoff.

The player chooses which type via a hold-modifier or a separate input (TBD — start with one type and add the modifier when the moveset proves it needs the depth).

#### c) Mining/building loop tightened

- **Mining yields material.** Slashing a stone tile gives you a stone charge; slashing dirt gives dirt. A small floating resource counter in the HUD.
- **Sprouting consumes material.** Can't infinite-spam stone walls — you mined for them.
- This turns the existing mine/build loop into an economy. Doesn't need a full inventory UI — three counters is fine.

#### d) Arena framing

- A single hand-authored arena level (`Levels/arena.txt`) with one enemy, instead of the open Perlin world for combat encounters.
- Arena boundaries are unbreakable bedrock so the fight stays contained.
- Win condition: kill the enemy. Lose condition: HP to zero (player already has HP via `IHittable` — just needs a death state and reset).

### What this unlocks

Once a) + b) + c) are in, every encounter is a tiny puzzle:
- Charger winding up → **mine the floor under the wind-up spot**, charger falls into pit.
- Turret across a gap → **grow a stone pillar** to break line of sight, climb it, drop down behind.
- Two enemies converging → **sand wall between them** for three seconds while you focus one.

The fact that sprouts are *DAG-based with parents* means players can grow staircases, bridges, ceilings — emergent geometry, not just block placement.

### Suggested build order

1. **Tactical sprout types** (≈ a day) — pure win, no new systems, sets up everything downstream.
2. **Arena level + bedrock** (≈ a few hours) — scaffolding.
3. **Material economy** (≈ half a day) — small but changes the texture of every fight.
4. **Charger enemy** (≈ a day or two) — the first real combat test. Tune until it's beatable with combat alone, then **add a second one** and force terrain use.
5. **Turret enemy** (≈ a day) — forces cover usage, validates the building side of the loop.

Stop after step 5, evaluate. If the loop is fun, add enemy variety (jumpers, diggers, sprout-eaters). If not, the problem will surface early and cheaply.

### Risks / open questions

- **Material counter UI** — easy to over-design. Start with three numbers in the corner; iterate only if the fights feel obscured by it.
- **Sprout-during-combat ergonomics** — currently sprouting requires aiming a click. If that's too slow during a fight, consider a "drop a sprout at feet" quick-button.
- **AI predictability vs. fairness** — telegraphs need to be readable but not patronizing. Charger's wind-up frames are the main tuning knob.
- **Camera** — fixed-arena fights may want a different camera mode than the free-roam Perlin world. Defer until it actually feels bad.

---

## 2. "Climb your way out" — alternative

**One-liner:** Vertical roguelike. Start deep underground, mining and sprouting is the only way up.

The world is a tall narrow shaft. Each "biome" (sand → dirt → stone → ?) has different durability, different threats, different sprout costs. Hostile mobs spawn from the walls. Death sends you back to the bottom; the map regenerates.

**Why it's compelling:** the existing terrain gen (Perlin) is *already* doing vertical layering with depth-based types. The "ascent" framing gives the existing mining/sprouting loop a direction and a stakes.

**Why I'd defer it:** it needs a meta-progression layer (runs, unlocks, persistent state) and procgen tuning that #1 doesn't. Bigger scope, fuzzier near-term wins.

---

## 3. "Sprouts that do things" — folded into #1

**One-liner:** Per-type sprout behaviors (sand decays, dirt tendrils, stone permanent + slow).

This was originally pitched as its own direction because it's the lowest-effort win — ~10 lines per behavior in the sprout-finalize step. But it doesn't stand alone as a *game*; it's a feature.

It's now step 1 of direction #1, where it earns its keep as the foundation of tactical sprout choice.

---

## Decision

Going with **#1 "The terrain IS the weapon"**, building in the order listed above. Tactical sprout types first (also satisfies #3), then the arena scaffolding, then the combat loop.
