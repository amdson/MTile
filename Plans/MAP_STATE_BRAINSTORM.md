# Map State Brainstorm — Stateful PvP via Map, Not Player

> Status: brainstorm. No commitments, no implementation plan yet.
> See [`TODO_TOP_BULLETS_PLAN.md`](TODO_TOP_BULLETS_PLAN.md) for actively scheduled work.

## Problem framing

PvP combat in MTile currently feels too symmetric — there's little incentive
to control vertical space, and fortifications don't pay off enough to make
building meaningful. We want to add **stateful elements that diverge over a
match**, creating natural advantage asymmetries, *without* asymmetric
movesets. Symmetry stays at the player level; asymmetry emerges from how
players invest in and shape the map.

## Constraints

- **Player movesets stay equivalent.** Whatever new mechanics show up are
  available to anyone who earns the situation.
- **No global external influences for now.** No rising lava floors, no
  gravity fields, no shrinking arenas. Map state must come from *player
  actions*, not the stage itself.
- **The "core" of stateful PvP is map-side for now.** Player-state mechanics
  (rubble inventory, heat, charge meters, etc.) are interesting but deferred
  — captured at the bottom for future reference.

## Theme: interacting state elements produce big effects

The unifying principle: individual map-state mechanics are *modest* on their
own (a charged block, a gas cloud, a lava pool). The *interactions* between
them — beam through charged block, gas pocket ignition, lava cooling to
permanent stone — are where the **extreme effects** come from. State doesn't
explode by itself; state explodes when *another* state element touches it.

This frames the design space: each mechanic should be relatively contained,
but compose cleanly with the others.

## Unified energy field (preferred consolidation of Threads A + B + J)

> User-preferred direction as of this revision. Charged blocks, lava, gas,
> and composite recipes (notably glass) collapse into one mechanic instead
> of three. Threads A, B, and J below remain for reference, but if this
> consolidation ships, those threads are *manifestations*, not separate
> systems.

### Core idea

A single per-cell scalar — **Energy** — and a small table of
material-dependent responses. The phenomena previously written up as
separate threads are different *materials reacting to the same field*:

- **Charge** = energy stored in a Stone cell (Stone barely leaks).
- **Lava** = energy in a Sand cell above the melt threshold (Sand
  fluidizes when hot).
- **Gas** = energy in an Empty cell (Empty leaks fast → spread).
- **Glass** = a Sand cell whose energy drained below the melt threshold
  *while still saturated* (cooled-while-hot).
- **Ignition** = an Empty cell whose energy spiked past an explosive
  threshold.

One state, one tick, one input vocabulary ("how much energy is in this
cell?"), one snapshot field.

### Mechanical shape

Sparse `EnergyAccumulator` (cell → float) — copy [TileImpactAccumulator.cs](../World/TileImpactAccumulator.cs)'s
Dict + decay + prune + Capture/Restore pattern. Per-frame `Tick`:

1. **Diffuse** to neighbors at material-dependent rate (Empty very fast,
   Stone very slow). This single rule covers "gas spreads", "heat
   conducts", "charge stays put".
2. **Decay** to ambient at material-dependent rate (existing pattern).
3. **React**: each cell checks its `(material, energy)` against the
   reaction table and may transition.

### Inputs

- **Charge action** (RMB-hold variant): dumps energy into a cell. The
  "charged block" action survives unchanged, it just writes to the
  energy field now.
- **Beams**: deposit energy along their path (small additive pass in
  the beam-resolve step).
- **Combat hits**: small per-hit energy deposit, scaled by move power.
- **Optional**: explosions dump in a radius (couples extremes back into
  the same field).

### Material response table

The only new design surface. Adding a material = one row.

| Material | Diffuse | Reactions |
|---|---|---|
| **Empty** | Very high | E > E_gas → render as gas, DoT in-cell; E > E_ignite → fireball (radial energy dump) |
| **Sand** | Medium | E > E_melt → fluidize (gravity-fed flow); cool-while-saturated → Glass |
| **Stone** | Very low | E > E_blow → detonate (radial energy dump, reuses eruption planner geometry) |
| **Dirt** | Low | E > E_bake → Clay (harder material) |
| **Foam** | Medium | E > E_pop → decay instantly with kick |
| **Glass** | Low | E > E_shatter → shatter (frags + energy release) |

### Why interactions become automatic

Combos stop being scripted special cases — they fall out of the
dynamics:

- **Beam ignites gas**: beam deposits energy in an Empty cell → cell
  crosses E_ignite → fireball. No special-case wiring.
- **Charged Stone amplifies a beam**: beam path picks up energy from
  cells it passes through. A high-energy Stone column donates a lot.
- **Lava cools to Glass**: a Sand cell saturated past E_melt flows;
  if it stops flowing and its energy bleeds below E_melt while still
  above the lock-in threshold, it transitions to Glass. The recipe
  doesn't need a lookup — it's the energy curve.
- **Block eruption from "charged seed"**: sprout growth rate is a
  function of local energy. Charged seed = energy budget for fast
  finalize. The current planner reuses unchanged.
- **Gas pocket pressure-builds inside a wall**: confined Empty cells
  accumulate from any leak source; rupture releases as ignition or
  blast depending on the level.

### What it preserves from each parent thread

- **Thread A's open-questions still apply** — Q1 (cross-player
  ownership of energy invested into a cell) is still the load-bearing
  one. Energy is just the universal substrate; whose energy is it?
- **Thread B's spread rules** — material diffusion rates ARE the spread
  rules. "Gas spreads through air" = "Empty has very high diffusion".
  "Lava flows downward" = Sand's fluidize state hooks into gravity-fed
  flow.
- **Thread J's recipes** — emerge from cooling/heating trajectories
  through the material response table, not from a separate recipe
  registry.

### Tradeoffs (where this is *worse* than separate systems)

- **Tuning becomes coupled**: nudging Empty diffusion to fix gas spread
  also affects how fast charged Stone bleeds into adjacent Sand. Loss
  of independent knobs.
- **Loss of mechanical identity**: players think "energy in this
  region" not "my lava vs my charge". Could blur strategic intent.
- **Visual readability burden**: glowing Stone, smoldering Sand, and
  lit Empty all need to communicate "same field, different material"
  consistently. Rendering work is real.
- **Single tuning vocabulary** is a feature for designers but a tax on
  early calibration — every threshold (E_gas, E_melt, E_blow, E_bake,
  E_ignite, E_pop, E_shatter) needs to land in a *self-consistent*
  scale.

### v1 minimum build

1. `EnergyAccumulator` — copy TileImpactAccumulator; add neighbor
   diffusion to `Tick`. Snapshot via existing dict-copy pattern.
2. Material reaction table — Stone (detonate), Sand (melt → flow →
   glass), Empty (gas → ignite). Skip Dirt/Foam/Glass-as-source for v1.
3. One input — the existing charge action writes to the energy field.
4. One output — Stone detonation reuses [`BlockEruptionAction`](../Character/ActionStates.cs)'s
   planners with mass = f(energy).
5. Rendering — per-material energy-level shader/sprite override; ship
   with a debug overlay that draws the raw energy field.

That's the unified v1: charged blocks + lava + gas + glass + ignition
cascades from one new file plus the response table.

### Open questions specific to this consolidation

- **U1**: Is energy *typed* (each player's deposit is owned/colored) or
  *anonymous* (energy is energy)? Answer determines Thread A's Q1 for
  the whole system in one stroke.
- **U2**: Does diffusion respect material *boundaries* (energy doesn't
  cross from Stone to Sand cheaply — modeling thermal contact) or is
  it purely per-cell-rate (Stone leaks slowly outward into anything)?
  The first is more physically intuitive, the second is simpler.
- **U3**: Are reactions *one-way* (Sand → Glass, no return) or
  *reversible* (Glass + low-energy environment → back to Sand)?
  Reversibility lets terrain self-repair; one-way locks in commitment.

## Thread A — Charged blocks

### Core concept

Tiles can hold accumulated *charge* invested by a player. Charge is *stored
energy* that releases as effects when interacted with.

### How charge is invested

Options (not exclusive — could combine):

- **Time-investment**: hold an action (RMB variant?) over a block to charge.
- **Combat-investment**: every slash/stab/beam that lands on a block adds
  charge — combat damage feeds nearby fortifications.
- **Rubble-fed**: spend the player-state "broken block" currency to charge
  (couples to deferred player-state thread).
- **Ambient-fed**: charge passively trickles into blocks near sustained
  combat — the map *naturally* charges where fights have happened.
- **Hit-bounced** (weird angle): a block that absorbs an attack stores some
  fraction of that attack's energy. Attacking fortifications *strengthens*
  them. Strong design incentive: don't strike into enemy bases except to
  break, not to chip.

### What charged blocks can DO

- **Instant eruption**: holding RMB over a charged block skips the wind-up
  phase of [`BlockEruptionAction`](../Character/ActionStates.cs) AND scales
  the planner's mass by charge level. Existing planners (MassBall,
  PriorityField) already do the structural work — charge is just fuel input.
- **Beam amplifier**: a beam fired *through* a charged block has its energy
  refilled or its damage multiplied. Pre-built "beam shafts" become
  strategic infrastructure.
- **Detonate**: triggered by a direct hit, an aimed move, or a charge-chain
  trigger. Radius scaled by charge level.
- **Reflect / refract**: incoming attacks bounce or bend off charged blocks
  — chargers can set up trick shots; wall-aim becomes meaningful.
- **Conductor**: charge propagates between connected charged neighbors — the
  network shape is the explosion shape.
- **Charge-as-armor**: charged blocks have higher HP / are slower to break.
  Soft transition into "fortification mattering" without new tile types.
- **Sprout accelerator**: charged blocks make adjacent sprouts grow faster
  / chain through pending neighbors with reduced delay.
- **Crystallization wave**: a charged block discharged in *growth* mode
  fills surrounding empty cells with solid tiles in a propagating wave.
  The "reverse explosion" — instant fortress, or instant entomb.

### Weirder angles worth poking

- **Polarity**: each player's charge has a sign or color. Same-polarity
  attracts/resonates; opposite-polarity neutralizes or explodes on contact.
  Placing a charge next to an enemy's charge is *itself* an attack.
- **Circuit requirement**: a charged block only "lights" if there's a chain
  of charged tiles connecting back to a tile the placer originally placed
  (or back to the placer's body). Cutting any segment of the wire =
  disarms downstream. Mid-fight surgery on enemy infrastructure becomes
  gameplay.
- **Memory**: a block remembers the last attack-type that struck it.
  Triggering it replays that attack outward. Players can "load" a wall
  with their own moves — a single beam stored in stone, fired on touch.
  (Highly chaotic, possibly too much state.)
- **Frequency / tuning**: discrete charge "frequencies" — same-frequency
  blocks resonate (chain detonate); different ones dampen. Depth without
  a continuous parameter.
- **Attention-based decay**: charge fades only when an *opponent* sees the
  block (in their light cone). Stealth setups stay charged forever; spotted
  ones leak. Stealth without separate stealth machinery.

### Failure modes to design around

- **Trivial cost → noise.** Charge must be expensive enough that every
  block can't be charged.
- **No telegraph → frustrating.** Charged blocks need a visible cue (glow,
  hum) so the opponent has counterplay.
- **Permanent storage → pre-charge race.** Either decay over time or a
  per-player charge budget that caps total stored.
- **Easy to break → never invested in.** Charge should ALSO slightly
  harden the block, or grant a one-chip shield.

## Thread B — Fluid tiles (lava, gas)

### Core concept

A new tile *substrate* that spreads, damages, and dissipates. Unlike
solid tiles (Stone/Dirt/Sand/Foam), fluid tiles have flowing behavior —
they aren't fortifications, they're *hazards in motion*.

Mechanically, fluid tiles slot into the same sparse-state machinery as
[`TileSproutGraph`](../World/TileSproutGraph.cs), [`TileImpactAccumulator`](../World/TileImpactAccumulator.cs),
and [`FoamDecay`](../World/FoamDecay.cs) — sparse per-cell state with
per-frame ticking and decay.

### Two starter fluid types

**Lava**
- Damages players and tiles on contact.
- Spreads downward + lateral (gravity-fed) until equilibrium.
- Cools to Stone after N seconds — turns hazard into permanent terrain.

**Gas**
- Damages players on contact (DoT).
- Spreads via diffusion (random walk) or via player-driven push (slashes,
  beams move it). Player-driven feels most game-friendly.
- Dissipates to Empty over time.
- Reduces visibility — sprite fades, line-of-sight blocks.
- Flammable variant: beam / charged block ignites it for an explosion.

### Spread rule design choices

| Rule | Behavior | Feel |
|---|---|---|
| Gravity-fed (Minecraft-style) | Lava drips down + sideways | Predictable, map-shape-dependent |
| Pressure-driven | Per-cell volume; flows to lowest neighbor | Reservoirs build up, burst on rupture |
| Diffusion | Random-walk to adjacent cells | Gas-like, unpredictable but cheap |
| **Player-driven** | Fluids only move when struck / pushed | Combat IS what moves the hazard around — strong design |

Player-driven spread is the most game-coupled — agency over hazard
movement, no autonomous threats from past placements.

### Interaction with structures (where it gets good)

- **Sealed pocket**: build a wall around lava/gas, opponent breaks one
  block, the contents flood. Defensive trap.
- **Pressure tank**: gas confined builds explosive potential proportional
  to confinement time. Rupture = boom.
- **Vent**: a Solid tile that emits gas slowly while it exists. Choose
  between break-it (stop the hazard, take damage to do it) or leave-it
  (hazard persists).
- **Substrate permeability**: lava drips through Sand quickly, slowly
  through Dirt, never through Stone. Material choice gains tactical depth.

### Position-driven asymmetry without external forces

Even without global lava-floors or gravity fields, fluid behavior creates
local micro-biomes that make verticality matter:

- Lava settles downward → high ground naturally safer near a spill.
- Heavy gas pools low → trenches are death; pillars are safety.
- Light gas rises → ceilings and tunnels become unsafe; open low ground
  safe.
- Sealed structures can fill with their own pumped gas → fortifications
  cut both ways.

The advantage is **local and contingent** on where each player chose to
invest — exactly the desired asymmetry.

### Weirder angles

- **Gas as fog of war**: visibility reduction creates ambush windows.
  A player flooding with smoke is hiding their *own* movements too.
- **Sticky lava**: walking through doesn't instakill — the player carries
  a "burning" status that DoT's and ignites gas they walk near.
- **Lava as bridge**: lava cools to Stone, so flooding a gap *creates* a
  path at the cost of the area being hazardous for several seconds.
  Temporal commitment.
- **Phase mixing**: lava + water = steam (a gas variant). Cooling /
  heating dynamics from specific moves.

### Failure modes

- **Spread cascades** can blow up CPU. Cap per-frame spread events; use
  the sparse-map-with-priority pattern from FoamDecay.
- **Griefing potential**: anti-grief safeguards around spawn areas.
- **Cooling too fast** → lava is just "delayed block placement" with no
  hazard window. Tuning matters.
- **Cooling never** → end-game looks like a hellscape.

## Thread E — Thermal gradient (continuous temperature field)

### Core concept

Every tile carries a *temperature*. Unlike charge (discrete, owned,
explosive), heat is a **continuous scalar field** that diffuses between
neighbors. Map state becomes a thermal landscape — most of the map sits
at ambient, but local hot/cold spots emerge from combat.

Different from charge: charge is energy *waiting to release*, heat is
energy *currently expressed*. Heat ambient-diffuses; charge does not.

### How heat is introduced

- **Beams heat tiles they pass through** (and tiles they damage absorb a
  bigger spike).
- **Friction heat**: a player running fast along a wall warms it.
- **Explosions** dump heat in a radius.
- **Cold sources**: gas of a "cold" variant, lava cooling to stone (heat
  diffuses outward as lava solidifies), or a specific move.

### What heat does

- **Hot tiles damage on contact** above a threshold (player or tile-side
  damage — material-dependent).
- **Tiles change phase**: Sand at high heat → Glass (clear, fragile,
  high-HP-but-shatters-on-strong-hit). Dirt at high heat → baked clay.
  Water tile at low heat → Ice (slippery, see Thread I).
- **Charge couples to heat**: hot charged blocks discharge faster; cold
  ones charge more efficiently (overclock vs underclock).
- **Sprouts grow faster in warm zones** — combat-heated areas regrow
  terrain faster, rewarding aggression with reconstruction speed.
- **Diffusion shapes**: long thin probes cool fast; thick masses retain
  heat. Geometry of fortifications matters thermally.

### Weirder angles

- **Thermal shock**: a sudden cold-on-hot transition (water hits lava,
  or "freeze move" on a heated wall) cracks the tile — instant damage
  with no impulse.
- **Heat as currency for charge**: convert local thermal energy to
  charge. Pre-fight combat heats the area; you cash heat in to power
  late-game extremes.
- **Heat-tracking opponent**: bodies have a faint thermal signature
  visible through walls if hot enough (sustained combat → glow).
- **Phase memory**: glass remembers it was sand. Cool glass slowly +
  apply pressure → reverts to sand pile, releasing rubble.

### Failure modes

- Continuous field everywhere = expensive. Probably restrict to a sparse
  per-cell overlay like [`TileImpactAccumulator`](../World/TileImpactAccumulator.cs);
  ambient temperature is implicit zero.
- Too many phase transitions = unreadable. Limit to one or two per material.

## Thread F — Structural stress & cascading failure

### Core concept

Tiles accumulate **structural stress** from impacts, weight above them,
and connectivity loss below them. Stress is *not* HP — it's a
propagating fault-line property. A wall with a single broken brick may
hold; remove a load-bearing one and the cascade collapses sections that
weren't even hit.

Engineering becomes combat: where you build matters, where you strike
matters more.

### Stress sources

- **Impact** from attacks (already accumulated in [`TileImpactAccumulator`](../World/TileImpactAccumulator.cs)
  — the bones exist).
- **Weight above**: each solid tile adds load to tiles directly below;
  load propagates down columns and branches across cantilevers.
- **Span / unsupported**: tiles too far from a vertical support gather
  stress over time (creep).
- **Lateral push**: explosions and beams introduce horizontal stress
  not just normal damage.

### What stress does

- **Cracked tiles** (above threshold) visibly fissure, take 2x damage,
  transmit stress to neighbors at a higher rate.
- **Failure cascades**: a tile breaking dumps its supported-weight onto
  its remaining supporters, potentially over-stressing them too.
  *Buildings can collapse from a single key strike.*
- **Pre-loaded structures**: a player builds a wall under deliberate
  stress (heavy roof on a slim pillar) — one shot brings it down on the
  opponent.
- **Stress shielding**: charged blocks (Thread A) might *absorb* stress
  instead of HP — fortifying the structural skeleton, not the surface.
- **Aftershocks**: large failures emit a residual stress pulse that
  weakens nearby standing structures.

### Weirder angles

- **Tension vs compression**: tiles handle compression well, tension
  poorly. A horizontal beam suspended between two columns has tension
  on its underside — a single hit underneath shears it.
- **Resonance with vibration** (Thread G): a vibrating tile in a
  stressed structure causes cascade at lower stress thresholds.
- **Memory of strain**: a tile that *barely* survived stress retains
  a permanent "weak point" tag — visible to opponents who study the map.
- **Repair via charge**: pumping charge into cracked tiles heals the
  stress meter. Fortification rebuild loop.

### Failure modes

- Cascade explosion blowing up CPU — bound per-frame cascade size.
- "Whole map collapses" outcomes feel unfair without good telegraphing
  of stressed structures.

## Thread G — Wave / oscillation tiles (resonance, vibration, sound)

### Core concept

Tiles can carry **frequency state** — they vibrate at a tunable
frequency. Map state has a *spectral* dimension. Tiles at the same
frequency couple; different frequencies dampen or interfere. This is
the frequency-domain cousin of charge.

### How tiles get a frequency

- **Strike tuning**: hitting a tile with a specific move stamps a
  frequency on it (e.g., slashes = low, stabs = high, beams = mid).
- **Adopt from neighbor**: an empty (or freshly placed) tile next to a
  vibrating one picks up the frequency over time.
- **Player choice**: an action lets the player explicitly tune a tile.

### What vibration does

- **Sound emission**: a vibrating tile *plays audio at its frequency*,
  audible even through walls. Telegraphs structure to listeners.
- **Couples to same-frequency tiles**: a hit on a low-frequency tile
  rings all connected low-frequency tiles. Network-wide chain effects
  without explicit wires.
- **Standing waves**: two opposing vibrating tiles in a corridor create
  a destructive node midway — passing through the node hurts.
- **Anti-stealth**: invisible / camouflaged enemies vibrating tiles by
  passing through them get exposed acoustically.
- **Loosens sprouts**: a high-frequency tile next to a growing sprout
  shortens its lifetime / weakens its commit-state.

### Weirder angles

- **Frequency as ammo type**: each player's projectiles have a base
  frequency; targets at the same frequency take amplified damage,
  targets at orthogonal frequencies barely tickle. Players pick a
  "tuning" each life — soft class system without different movesets.
- **Beats & cancellation**: place two slightly different frequencies in
  proximity — they beat at the difference, creating a slow pulse that
  damages periodically.
- **Quiet zones**: an actively-emitted *anti-frequency* mutes natural
  tile sounds — local stealth bubble. Counter to acoustic telegraphing.
- **Resonance overload**: pumping enough same-frequency energy into a
  tile network shatters every tile in the network at once. "Glass
  cannon" map play.

### Failure modes

- Audio-driven mechanics rely on the player actually hearing — bad for
  deaf players, bad in a noisy mix. Always pair with visual cue.
- Frequency continuum invites "infinite tuning"; restrict to a small
  discrete set (3-5 frequencies) for legibility.

## Thread H — Light & shadow (visibility as map state)

### Core concept

Visibility isn't a player property — it's a **map property**. Tiles
emit, block, or absorb light. The lit/dark state of regions becomes a
contested resource. Symmetric movesets, asymmetric visibility advantage.

### Light sources & blockers

- **Beams illuminate** their path for some time after firing.
- **Charged blocks glow** (already in Thread A as telegraph) — passive
  light source.
- **Lava glows** strongly (Thread B).
- **Sand & Foam** transmit dim light (translucent).
- **Stone & Dirt** block fully.
- **Gas** scatters light — gas-filled regions glow weakly but block
  long-range sight.

### What darkness does

- **Players in shadow are not rendered to opponents** (or rendered
  faintly). Sight-lines become a fortification consideration.
- **Hidden charge / hidden traps**: structures built in darkness stay
  secret. Lighting an area reveals what's there.
- **Vision range** has a hard short-distance default in dark; lit
  regions extend it further.

### Weirder angles

- **Light as projectile**: a "flare" move shoots a glowing tile that
  illuminates a region for N seconds — sets up an attack window.
- **Shadow-traversal**: a stealth move slips faster through dark
  corridors. Encourages dim play.
- **Reflective tiles** (Thread A's reflect, but for light): bounce light
  around corners — set up surveillance angles.
- **Eclipse / sun cycle on the stage**: explicitly *off-limits* by user
  constraint (global external influence). Keep it player-driven only.

### Failure modes

- "Can't see anything" is frustrating. Default lit zones around spawns;
  fade-in over distance, never pitch black with no contrast.
- Couples with frame-rate / rendering — a darkness mechanic that drops
  FPS is worse than no mechanic.

## Thread I — Local force fields (gravity wells, wind, magnetism)

### Core concept

Player-placed tiles that exert **force on bodies & projectiles** within
a radius. Doesn't violate "no global gravity field" — the field is
local, sourced from a destroyable tile, and shaped by the placer.

Three flavors worth distinguishing:

### Variants

- **Gravity well**: pulls bodies + projectiles toward the tile center.
  Set up swirling-projectile traps; tether-style aggro tools.
- **Repulsor**: pushes everything away. Defensive shield, push enemies
  off ledges, deflect beams at angles.
- **Wind current**: directional, persistent (or fading). Created by
  high-impact moves (a powerful slash flings air); affects light bodies
  more than heavy. Couples to mass (deferred player state).
- **Magnetic node**: only affects "metallic" tiles/projectiles
  (charged blocks? Rubble?). Selective, doesn't yank the opponent
  directly. Set up rubble-vortex zones.

### Interactions

- **Wind + gas** (Thread B): gas pushed by wind currents. Combatants
  steer hazards into enemies.
- **Gravity well + sprout growth**: sprouts grow toward the well
  faster — gardening with attractors.
- **Magnetic + charged**: charged blocks slide along magnetic lines —
  remote rearrangement of infrastructure.
- **Repulsor + projectile-from-charge**: deflected back at source,
  optionally amplified.

### Weirder angles

- **Mutual gravitation**: two wells pull each other (and orbit if
  positioned right) — moving terrain features that drift between
  placements.
- **Wind decay tied to combat**: currents only persist while someone is
  fighting nearby. Quiet map = still air.
- **Inverted-gravity bubble**: a tile that flips gravity for everything
  inside its radius. Movement puzzle / panic button.

### Failure modes

- Bodies-in-force-fields breaks the carefully tuned movement feel; cap
  force magnitudes so player agency dominates field effects.
- Wells too strong → unfair grabs. Telegraph + breakable source tile.

## Thread J — Composite & recipe tiles (emergent materials)

### Core concept

Two basic substrates *combine* into a third. The crafting/combinatorics
happen on the *map*, not in an inventory. Cooking up exotic terrain via
specific juxtapositions becomes the long-game investment.

### Starting recipes

| Inputs | Output | Property |
|---|---|---|
| Sand + heat (Thread E) | Glass | High HP, shatters on threshold |
| Dirt + water | Mud | Low friction (Thread I-ish), slows movement |
| Foam + charge | Pulse-foam | Decays explosively |
| Lava + water | Steam (gas variant) | Hot gas, scalding |
| Sand + charge | Sapped-sand | Drains charge from adjacent blocks |
| Stone + vibration (Thread G) | Brittle stone | Cascade-prone (Thread F coupling) |
| Gas + cold | Liquefied gas | Pools low, then re-evaporates |
| Sprout + charge (mid-growth) | Charged-on-arrival | Sprout finalizes pre-armed |

### Why this is interesting

- Multiplies the design space without adding many primitives — every
  new substrate combines with every existing one.
- Rewards spatial planning: laying down sand *next to* the spot you'll
  later heat is a long-investment play.
- Couples threads naturally (heat + sand, charge + foam) so adding
  composites is *the* glue mechanic between threads.

### Weirder angles

- **Player-named recipes**: a specific juxtaposition triggers a
  one-time visual flourish + name pop ("you made Pulse-foam!") — soft
  goal/discovery layer in PvP.
- **Recipe diffusion**: a small Mud patch slowly converts adjacent
  Dirt + adjacent Water into more Mud (autocatalytic). Self-spreading
  composites are a contained hazard.
- **Anti-recipes**: some combos *cancel* (charge + ice = neutralized).
  Defensive composite plays.
- **Tier-2 composites**: combine two composites (Glass + charge?
  Mud + heat? = baked clay). Rare exotic states emerging mid-late
  match.

### Failure modes

- Combinatorics blow up — keep recipe table small (~10 entries) and
  curated, not procedurally generated.
- Unintended recipes auto-firing — recipes should require *intent*
  (deliberate juxtaposition), not random adjacency from sprout chains.

## Thread K — Shorter-form mechanic seeds (one-paragraph ideas)

A grab-bag of additional directions worth keeping on the radar. Each is
a paragraph rather than a full thread — placeholders for later.

- **Echo tiles**: a tile records the last N impacts on it; activating
  (with a charge spend?) replays those impacts outward. "Loaded" walls
  fire stored shots.
- **Phantom tiles**: visually solid but pass-through; an opponent who
  trusts them falls. Counter: shoot every wall before standing on it.
  Counter-counter: real walls cost rubble to mark.
- **Spring tiles**: store impact energy from a hit, release as launch
  on next contact. Build trampolines, or weaponize a launchpad against
  the opponent who walks onto it.
- **Spore / contagion tiles**: a tile type that converts adjacent
  matching tiles over time. Slow, network-shaped attrition. Cured by
  specific moves. Combat-driven version: hitting an infected tile
  spreads the spore onto the attacker's nearby placements.
- **Anchor tiles**: a tile designated as a structural anchor protects
  everything in its connected component from cascade failure (Thread
  F). Sever the anchor's link to the ground, the whole structure
  becomes brittle.
- **Conduit / pipe tiles**: non-solid tiles that route charge or
  fluid between distant points. Network-as-infrastructure: long thin
  pipes let you deliver heat / charge / gas to surgical strike points.
- **Mirror tiles**: refract beams at fixed angles. Optical
  infrastructure — building bank-shot artillery setups.
- **Time-pressurized tiles**: a tile that gains power proportional to
  *uninterrupted* time since placement. Long-investment artillery —
  surviving with one in your base for a minute is the win condition,
  or the threat.
- **Mass / topple physics**: stacked structures have a center-of-mass;
  if it shifts off the base support, the stack falls. Building too
  tall = unstable. Couples to Thread F naturally.
- **Aftershock tiles**: tiles that emit a delayed secondary effect N
  seconds after being hit. Stacking aftershocks creates a rhythm of
  rolling damage.
- **Slip / friction state**: tile surfaces have player-modifiable
  friction. Ice (low) or tar (high). Skating tactics; trap surfaces.
  Couples to Thread E (cold → ice).
- **Decoy / lure tiles**: a tile that draws homing projectiles or
  hostile AI toward itself. Defensive misdirection.
- **Phase-shift tiles**: a tile that cycles between solid and empty on
  a rhythm. Players time crossings. Sourced from a specific move so it
  stays player-driven. (Risk: too rhythm-game-y.)
- **Inverse-rubble tiles**: tiles that *cost* rubble to break (you must
  spend, not gain). Punishes attackers who carelessly chew through
  enemy fortification.

## Thread L — Autonomous tiles (turrets, posts, generators)

### Core concept

Player-placed tiles that **act on their own clock** — they fire, scan,
emit, or buff without the placer having to be present. Unlike charged
blocks (passive until triggered) or fluid tiles (passive once placed),
these tiles take *initiative* each tick. They give the placer a
**held area advantage** rooted in geography, not movement skill.

Symmetry is preserved at the player level — both players have access
to the same placement actions; asymmetry emerges from *where* and
*when* each invested.

### How they fit the unified energy field

Turrets are the cleanest extension of the energy-field consolidation:
a turret is just **a tile whose periodic reaction is "spend energy to
do X"** instead of "cross a threshold and discharge once". The
material response table gains a clock-driven row per turret type. Drain
its supply → silenced. No new fuel system needed.

This also resolves the perennial turret-design problem ("how is it
fueled?") for free: turrets compete with charged blocks, beams, and
ignition for the same energy budget.

### Candidates by role

**Damage dealers**

- **Sentry** — fires a small projectile at the nearest enemy in
  line-of-sight, on a clock. Cheap per-shot energy cost; high uptime if
  fed. The canonical turret.
- **Pulsar** — emits an AoE pulse every N seconds in a fixed radius.
  No targeting; area denial via "this region pulses". Stack multiples
  for tight coverage.
- **Mortar / lobber** — arcs a projectile over walls toward the
  nearest enemy. Indirect fire — counter to "hide behind a wall"
  strategies. Slow rate, telegraphed arc.
- **Beam tower** — continuous narrow beam in a fixed direction (or
  slow sweep). Crosses an area with sustained damage. Costly per
  second; commits to a sightline.

**Surveillance**

- **Beacon** — reveals enemy positions in radius to the placer.
  Doesn't deal damage; pure information. Counter: destroy or block
  with materials opaque to it (couples to Thread H's light/shadow if
  built).
- **Searchlight** — directional reveal cone (Thread H native).
  Sweeps slowly; enemies can time crossings.
- **Tripwire / sensor** — alerts placer (audio + minimap ping) when
  an enemy crosses a line. Cheap, info-only.

**Area denial / control**

- **Vent** — continuously emits gas (Thread B / energy-into-Empty in
  the consolidation). Passive hazard generator. Already implied by
  the unified field; "vent" is just "Stone next to Empty with a slow
  energy leak".
- **Ward** — projects a defensive zone. Enemies inside take a small
  DoT and/or have reduced movement; placer takes reduced damage
  inside. Buff-and-debuff symmetric around ownership.
- **Repulsor post** — autonomous version of Thread I's repulsor: pushes
  enemies back periodically. Forces routing around it without dealing
  damage.

**Infrastructure (turrets that feed turrets)**

- **Generator / battery** — passively produces energy into the local
  cell, slowly. Doesn't fight directly; *feeds the network*. Building
  a "power grid" of generators + turrets becomes a layer of play.
  Couples directly to U1: is the generated energy owned/typed?
- **Sprout farm** — periodically spawns a sprout on an adjacent empty
  cell. Slow auto-fortification — gradually grows walls in an area.
- **Tile factory** — extrudes a chosen-material tile in a chosen
  direction every N seconds. Like sprout farm but explicit material
  choice; slower / more expensive.
- **Hopper / repair post** — passively repairs cracked tiles
  (Thread F coupling) or refills charge in nearby Stone blocks. The
  fortification-maintenance role.

**Posts (placer-occupied, not autonomous)**

These aren't strictly turrets — they don't fire on their own — but they
share the "I built it here, I get an area advantage" pattern. Worth
naming because they're the symmetric counterweight to autonomy:

- **Sniper nest** — when the placer stands on this cell, their
  projectiles have extended range / faster speed / amplified damage.
  Encourages occupying a held position rather than running.
- **Charging dock** — placer regenerates energy / charge faster while
  standing on it. Fuel-economy posts.
- **Heal post** — slow HP regen while occupying. Couples to risk —
  occupying it is itself a sitting-duck moment.
- **Anchor stance** — placer can't be knocked back while standing on
  it. Footing post; counter to knockback-heavy strategies.

**Movement-shaping**

- **Tractor tower** — autonomous Thread I gravity well. Continuously
  pulls enemies toward itself. Sets up kill-zones around itself or
  herding lanes.
- **Wind fan** — emits a persistent directional current. Steers
  projectiles, gas, light bodies. Couples to Thread B & I.

### Cross-cutting design knobs

Every turret variant pivots on the same few axes:

- **Autonomy**: fires on its own / fires only while placer is alive /
  fires only while placer is nearby. Sliding scale; full autonomy =
  hold ground after death.
- **Targeting rule**: nearest enemy in LOS / nearest enemy in range /
  any enemy crossing a line / fixed direction / radial AoE. Different
  rules → very different counterplay.
- **Fuel source**: local energy field (couples to consolidation) /
  rubble inventory (couples to deferred player state) / unfueled-but-
  destroyable (HP only). Energy-field is the cleanest in the unified
  design.
- **Rate × power**: high-rate-low-power (sentry) vs low-rate-high-
  power (mortar) gives distinct silhouettes without new code.
- **Visibility to opponent**: glowing always (telegraphed,
  always-counter-playable) vs hidden until firing (ambush-friendly,
  feels-bad-without-warning). Probably always-visible is safer.

### Weirder angles

- **Capturable turrets**: a turret can be *converted* to the
  opponent's side by sustained attack — instead of destroying it, you
  flip it. Builds a steal-vs-shatter decision into every engagement.
- **Faulty turrets**: built fast/cheap turrets can misfire (hit the
  placer's own blocks, friendly fire). Quality-vs-speed tradeoff in
  placement.
- **Linked turrets**: a turret's effective range / damage scales with
  *how many of its allies are in line-of-sight to it*. Networks pay
  off non-linearly.
- **Inheritable state**: a turret accumulates "experience" (shots
  fired, kills) and gets visibly stronger. Long-lived ones matter
  more — protecting old turrets is valuable.
- **Turret-on-turret**: turrets can target each other (anti-turret
  variants — flak, EMP). Siege warfare on player infrastructure.
- **One-shot consumables**: a "trap mine" is a degenerate turret —
  one shot, then gone. Cheaper, single-purpose. Bridge to disposable
  placements.

### Failure modes

- **Turret stacks dominate**: if turrets fully replace player skill,
  the meta degenerates to "out-build the opponent". Counter:
  generous turret HP costs, sharp LOS / range limits, fuel scarcity.
- **AFK builds**: a player who builds a turret farm and hides becomes
  a strategy. Counter: turrets need *active* re-fueling (couples to
  the energy field naturally — fuel decays without input).
- **CPU**: many turrets all running clocks + LOS checks is the
  expensive part. Bound count per player; LOS via existing tile-query
  primitives (cheap because tile grid).
- **Visual noise**: turret-heavy maps become unreadable. Limit
  variety on screen via the same per-player count cap.
- **Asymmetric snowballing**: first turret kills make next turret
  builds cheaper → unrecoverable lead. Counter: turret build cost
  doesn't scale with score; comeback is geometric not arithmetic.

### Open questions specific to turrets

- **L1 — Lifespan policy**: do turrets persist indefinitely (until
  destroyed), decay with time, or decay with disuse (no fuel = self-
  destruct)? Decay-with-disuse couples best to the energy field and
  prevents AFK farms.
- **L2 — Autonomy on placer death**: does the placer's turret network
  persist after their death (in deathmatch / multi-round formats)?
  Persistence = legacy plays; non-persistence = clean reset.
- **L3 — Capture vs destroy**: are turrets *only* destroyable, or can
  they be flipped (see weirder angles)? Capture mechanics deepen
  combat but complicate ownership rules (and couple back to U1).

## Thread C — Interaction matrix (the payoff)

Where threads A and B meet is where the **memorable plays** live. Some
combinations the design space already implies:

| Combo | Effect |
|---|---|
| Charged + gas pocket | Chain ignition — huge area damage |
| Charged + lava pool | Submerged delayed mine; cools into stone with charge sealed inside |
| Beam + charged shaft + gas cloud | Amplified beam ignites gas in cascade |
| Beam + charged shaft + enemy fort | Pre-built "siege weapon" infrastructure |
| Sprout chain + charged seed | Network finalizes carrying charge, instant pre-armed structure |
| Lava + water | Steam expansion — pushes players and blocks (panic button) |
| Charged + charged (opposite polarity) | Self-detonating wall when placed adjacent |
| Crystallization wave + opponent in path | Instant entomb |
| Heat + sand | Glass (composite); enables long-range visibility through walls |
| Heat + charge | Overclocked discharge — bigger blast, shorter window |
| Cold + lava | Premature solidify — locks lava as terrain mid-flow |
| Vibration + stressed wall | Cascade collapse below normal stress threshold |
| Same-frequency tile network + single strike | Whole network rings → chained damage |
| Light beam + charged shaft + gas | Triple-amplified ignition |
| Gravity well + projectile spam | Vortex trap — area-denial via stored kinetic energy |
| Wind + gas cloud | Steerable gas; weaponized atmospherics |
| Magnetic node + charged blocks | Slide enemy infrastructure into a kill zone |
| Stress cascade + charged blocks in path | Detonations chain alongside structural failure |
| Echo tile (Thread K) + opponent's signature attack | Replay enemy's own move against them |
| Anchor (Thread K) + stress-loaded structure | One snip drops the whole fortress |
| Phantom (Thread K) + dark zone (Thread H) | Trust-the-floor mind game over visual confirmation |
| Mud composite + slope | Sliding hazard — controlled by where you pre-place water |
| Pulse-foam composite + crowd | Decay-detonation in waves around the carrier |

The goal isn't to design *each* combo in advance — it's to make sure the
underlying systems compose so combos *emerge*. If charge is "energy",
gas is "fuel", heat is "expressed energy", vibration is "frequency",
and stress is "structural debt", the combos are *obvious* without being
scripted.

## Thread D — Extreme effects (the release valve)

Stored state must release, or it's just noise. Big effects are how state
cashes out. Six rough archetypes:

| Shape | Feel | How it emerges |
|---|---|---|
| **Burst destruction** | Sudden contained explosion | Charged-block detonation; gas pocket rupture |
| **Sustained destruction** | Channeled decimation | Beam through charged shaft |
| **Mass creation** | Instant architecture | Charged eruption; crystallization wave |
| **Locked region** | Long-lasting hazard zone | Lava flood; gas cloud |
| **Map fracture** | Structural change at scale | Chain detonation across a network |
| **Player projectile** | Body itself becomes weapon | Charge-augmented dive stab |

### Three properties extremes need to feel good

1. **Telegraph proportional to power.** The bigger the effect, the louder
   the wind-up. Pre-charging visible. Beam audibly building.
2. **Cost is real and visible.** Spending charged blocks, blowing rubble
   inventory, taking damage during a vulnerable channel — visible on
   player or map.
3. **Counterplay is concrete.** Not "dodge generically" — *specifically*
   "shatter the charged block before it fires" or "vent the gas pocket"
   or "interrupt the channel".

Without all three, extremes feel either oppressive or never-used.

### Risks specific to extreme effects

- **Power creep**: if every move can trigger an extreme, the regular
  moveset stops mattering. Scarcity in *what can trigger* extremes is
  healthy.
- **Match-decisive without comeback**: a single huge play winning the
  match is bad design. HP buffers it, OR there's a parallel comeback
  mechanism.
- **Visual readability** at the moment of detonation. The opponent should
  *understand* what happened — the chain lit, then exploded — not just
  see the aftermath.
- **Match length pressure** from big map changes. Either hard timer or
  a slow "regrow" mechanism keeps matches playable.

## Open questions

These are the design decisions that aren't yet answered, and which the
shape of the final system depends on:

### Q1 — How do opposing players interact with each other's charged blocks?

This is the load-bearing question. Several plausible answers, each tilts
the whole design:

- **Neutral resource**: anyone can use any charged block once it exists.
  Reading the map = opportunism. Stealing enemy charge into your own
  attacks is core gameplay.
- **Owned by placer**: only the placer can fire from / amplify through
  their charged blocks. Opponent can only *destroy* them. Pure
  territorial. Most directly addresses the user's "fortifications should
  matter" goal.
- **Owned with polarity interaction**: opponent's charged blocks dampen
  the placer's attacks passing through, OR react (detonate, neutralize).
  Charges interact across players.
- **Drainable**: opponent attacks slowly drain your charge into their own
  meter. Aggression converts enemy infrastructure into your own
  resource.

Each choice changes the rhythm of play:
- Neutral → high tempo, opportunistic
- Owned → slow build-up, decisive engagements
- Polarity → tactical positioning, "place chess"
- Drainable → constant attrition, harassment-heavy

### Q2 — Occasional vs common extremes

Should extreme effects fire **1-2× per minute** (expensive, highlight-reel
design) or **every 10-20 seconds** (cheap, sustained-mayhem design)?

### Q3 — Single-source vs coordinated extremes

Should extremes require **multiple state elements combining** (charged
block + lava + beam all together) — high skill ceiling — or **single
sources amplifying** (one fully-charged thing fires) — high floor?

### Q4 — Cost location for extremes

Where should the extreme's cost live?

- **Time invested** (slow charge): rewards planning.
- **Risk taken** (vulnerable channel): rewards bravery.
- **Resources spent** (rubble inventory): rewards economy management.

Different cost locations encourage different playstyles. Could mix per
mechanic.

### Q5 — Charge decay model

- **No decay** (until used / destroyed): pre-charging dominates; race to
  set up.
- **Linear decay**: maintenance becomes a thing; can't pre-charge too
  early.
- **Use-dependent decay**: charge only erodes when used or attacked.
- **Attention-based decay** (weird): only decays when *seen* by an
  opponent.

### Q7 — How many concurrent map-state dimensions?

Each thread (charge, fluid, heat, stress, vibration, light, force,
composite) adds a per-cell state dimension. Stacking all of them =
unreadable map + heavy CPU + decision paralysis. Three viable cuts:

- **One dominant + minor coupling**: pick *one* thread to be the
  "feature" of map state (e.g., charge), let others appear as light
  flavor (heat shows up but doesn't have a deep system behind it).
- **Two-dimensional**: pair two complementary threads (charge + fluid,
  or stress + vibration) — they couple deeply but the rest is left out.
- **Sparse-everything**: every dimension exists but is *sparse* (only a
  handful of tiles in any given dimension at once). Player choice of
  *which* dimension to invest in per match becomes meta-strategy.

### Q8 — Player tuning per match vs per moment

Some threads (G's frequency tuning, J's recipe planning) imply the
player commits to a "loadout" before or early in a match. That borders
on asymmetric movesets via the back door. Two answers:

- **No commitment**: all moves available, frequency / recipe choice is
  per-action.
- **Soft commitment**: tuning is in-match-malleable but slow to switch —
  you can change but you pay an opportunity cost.

### Q9 — Map state visibility to opponents

How much of the opponent's investment is *visible*?

- **Fully visible**: glow / hum / cracks / heat shimmer all readable.
  Skill-ceiling shifts to *reading* the map.
- **Partially hidden**: some states (charge level, frequency, stress)
  are hidden until investigated by hitting. Investigation is its own
  cost.
- **Hidden by default, revealed by counter-moves**: a "scan" action
  exposes a region's full state for a moment. Information warfare.

### Q6 — Material as fluid permeability

If lava drips faster through Sand than Stone, the existing
[Tile types](../World/Tile.cs) gain tactical depth without new state.
But this couples player block-choice to a hazard-defense decision. Is
that good (depth) or bad (too much to think about per-build)?

## Deferred — Player state ideas (for later)

These came up during the same brainstorm but the user wanted to focus on
map state. Captured here for future reference:

- **Rubble inventory**: every destroyed tile awards rubble to the breaker.
  Rubble is throwable as a projectile, OR consumed to place a block, OR
  converted to charge. Aggression literally feeds fortification budget;
  pure builders run dry.
- **Heat / fatigue meter**: high-impact moves raise heat; above threshold,
  cooldowns stretch and player visibly steams (giving away position).
- **Counter-attack charge from received hits**: like Smash %, but inverse
  — taking hits feeds a comeback meter.
- **Mass / weight**: carrying rubble slows you but reduces incoming
  knockback. Real trade-off.
- **Marked**: landing a hit marks the target for N seconds — see them
  through walls, OR next hit on them is amplified.

These mostly couple naturally to the map-state threads above (rubble
funds charge; heat affects ignition; marks reveal hidden charged blocks).

## Next steps if/when this becomes real

A loose ordering, not a commitment. Current direction: the **unified
energy field** above (Threads A + B + J collapsed into one mechanic).

1. **Pick an answer to U1** (typed/owned energy vs anonymous). This is
   the consolidated form of Thread A's Q1 and shapes everything else
   about cross-player interaction.
2. **Pick an answer to U2** (does diffusion cross material boundaries
   freely or with resistance). Affects whether fortifications "leak"
   into surroundings or stay sealed.
3. **Prototype `EnergyAccumulator`** — copy [`TileImpactAccumulator`](../World/TileImpactAccumulator.cs),
   add neighbor diffusion to `Tick`. Snapshot follows existing pattern.
4. **Wire one input + one output**: charge action writes to the field;
   Stone detonate reuses [`BlockEruptionAction`](../Character/ActionStates.cs)
   with mass = f(energy). Validate the stored-state-releases loop.
5. **Add Sand reactions** (melt → fluidize, cool → Glass) and **Empty
   reactions** (gas, ignite). Now lava + gas + glass all exist.
6. **Combo verification**: beam-deposits, gas-pocket ignition,
   charged-shaft amplification should all work *without* any new
   special-case code — confirms the consolidation paid off.
7. **Backlog the orthogonal threads** (E heat-as-separate-thing, F
   stress, G vibration, H light, I force fields, K seeds) — defer
   until the unified loop feels right. Heat (Thread E) is partially
   subsumed by the energy field already.
