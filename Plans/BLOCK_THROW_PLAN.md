# Block Throw Plan — `todo.txt` #2

**Status: proposed, nothing built** (written 2026-08-28 from the todo entry and a read of
the current code). The peel *grab* half shipped 2026-08-07 (`BACKLOG.md` 3.9/3.10 are its
open tuning items); this plan is about the *throw* half.

The todo entry, verbatim, is five tweaks plus a mechanism sketch. §1 restates the tweaks
as goals, §2 pins what the code does today, §3 lays out the sketch, §4 is the critique
(the part you asked for), §5 is the revised design, §6 the phased build order.

---

## 1. Goals (the five tweaks)

| # | Tweak | What it actually asks for |
|---|---|---|
| T1 | Lower the decay rate of the grabbed mass | Slower carry bleed. |
| T2 | Particles: dirt-ball sprite + speed trail | Render-only VFX on the thrown ball. |
| T3 | One sprite for held and thrown mass — a circle sampled from the block texture | Held orb and projectile drawn by the same routine, textured by material. |
| T4 | Swipe-and-release throw: static mouse ⇒ drop; fast swipe then release ⇒ fly along the swipe | Throw velocity comes from the **cursor's motion at release**, not from "toward the cursor at fixed speed". |
| T5 | Drag-and-release in one motion throws seamlessly if the blocks break | The peel contest may still be undecided when LMB comes up; the release must not forfeit it. |

T4 and T5 are the gameplay change; T1 is a knob; T2/T3 are rendering.

---

## 2. What exists today (verified)

All in `Character/Action/ActionStates.cs` unless noted.

- **Peel phase** (`BlockGrabAction.UpdatePeel`, :2869): while LMB is held, a gaussian kernel
  under the cursor paints tether onto solid cells (the group, ≤25 members,
  `PeelMemberBuffer.Capacity`). The **spring** is cursor→group-COM,
  `force = PeelSpringCoeff·(dist/TileSize)^PeelSpringPower` (:2891), snaps above
  `PeelSpringMax` (:2894 — attempt dies), and its per-member share erodes tether + glue.
  When `force ≥ glueTotal` the group breaks out (`BreakOutGroup`, :3000) into
  `vars.OrbHeld / OrbBlocks / OrbType`.
- **Carry phase**: the orb is *abstract* — no position, no velocity. It is drawn at
  `body + aim·HandDistance` (:3174) as a flat square (`sb.Draw(pixel, Rectangle)`, :3178),
  radius `∝ √(remaining/harvested)`. Remaining blocks bleed **linearly to zero over
  `DissipateSeconds = 2.0f`** (:2776, `RemainingBlocks` :3141) — a `const`, not a knob.
  Movement modifiers apply while carrying (:3083).
- **Release**: `CheckConditions` returns false the frame LMB is up (:2810). `Exit` (:3047)
  spawns a `LobbedAreaProjectile` at the hand with velocity
  `Body.Velocity + normalize(cursor − body) · ThrowSpeed(620)` (:3062). So the throw is
  *aimed*, at a fixed speed, regardless of how the mouse moved. A release before break-out
  ends the state with nothing (the contest is forfeited — the T5 complaint).
- **Projectile** (`Entities/Projectiles/LobbedAreaProjectile.cs`): gravity 1, collides
  with tiles, "lands" when speed < 30 px/s, then deposits `budget` into `TileMassField` +
  publishes a radial AOE hitbox. Drawn as `Sprites.Ball(5f)` in `Color.Sienna` (:60-61) —
  a fixed 5 px dot, ignoring both budget and material. Snapshotted via `EntityData`
  (`Budget`, `TileType`, `Detonated`).
- **A cursor-following ball already exists elsewhere**: `BlockPaintAction` keeps
  `vars.BallPos/BallVel` (shared `ActionVars` fields, :1829) driven by
  `SmoothPen.CriticallyDampedStep` (`Character/Input/SmoothPen.cs:47`, dt-stable, made for
  `ActionVars`), and at release detaches it as a `MassBall` carrying `BallVel` if
  `|BallVel| ≥ EruptReleaseSpeed` (:1872). That is exactly the "ball inherits swipe
  velocity" mechanism T4 wants, already proven in a sibling action.
- **Input history**: `Controller` keeps a 32-frame ring of `PlayerInput` incl.
  `MouseWorldPosition` (`Character/Input/Controller.cs:36-52`), all inside the sim ⇒
  deterministic. Note `MouseWorldPosition` is camera-relative in effect — commit `c0a4a3a`
  moved the stab/circle gestures to a player-relative frame for exactly this reason
  (`InputParser.cs:66`).
- **Render hooks**: action overlay draw is `Draw(SpriteBatch, pixel, body, vars)`
  (`Game1.cs:1032`) — no atlas access; entities draw via `Sprite.Draw(DrawContext)`
  (`Game1.cs:936`); tile grain lives in `TileTextureAtlas` on `ChunkRenderer`
  (`Game1.cs:522`). Particles: `Drawing/ParticleSystem.cs` + `Drawing/Effects.cs`
  (`TileBreak/HitSpark/Puff`). Edge-triggered cosmetics go through
  `PresentationEvents` keyed by `(sim frame, id)` so rollback re-sim can't double-fire
  (`Game1.cs:372-405`); level-triggered ones are re-derived from state each rendered frame.

---

## 3. The sketch, restated precisely

1. Keep a **pulling point** `P` with position and velocity after break-out, not just during
   the peel.
2. While LMB is down, `P = cursor`, updated instantly, but **soft-clamped** to a radius `R`
   around the player (a radial remap of `cursor − player`; `R` = block-placement reach,
   `BlockPaintAction.BuildReach = 64 px`).
3. On release, set `P`'s velocity from **mouse position history** and let `P` fly on.
4. If the group hasn't broken out yet at release, keep running the peel contest against the
   flying `P` (spring may snap, glue may give).
5. If/once the group has broken out, the **mass ball chases `P`** directly, capped at a max
   speed; destroy `P` when the ball reaches it.

---

## 4. Critique — does it achieve the goals?

Yes, and the pulling point is the right spine. Its job — which the sketch states and an
earlier draft of this doc missed — is **uniformity**: a release during peel and a release
during carry must go through the *same* rule (point takes the mouse's velocity, ball chases
the point), so a throw feels the same whether or not the blocks had come free yet. Any
design that launches the carried ball from its own hand velocity has no equivalent for the
peel case and ends up with two throw rules. So the point stays, in both phases. What follows
is about *specifying* the point and the chase so the ball actually ends up at swipe speed
— the sketch leaves the follower and the end condition loose, and the obvious readings of
both break the uniformity they're there to provide.

### 4.1 The follower must match velocity, not just close distance

"Pulled directly toward the pulling point, up to a maximum velocity" reads as a velocity
servo: `BallVel = clampLength(k·(P − B), Vmax)`. That has the wrong fixed points. While the
gap is large the ball moves at `Vmax` regardless of how fast the swipe was; as it catches
up, `k·(P − B) → 0` and so does its velocity. Either way the swipe speed never reaches
the ball — you get `Vmax` or a stall, and "destroy the point when reached" hands the ball
whichever of those it happens to be in.

What T4 needs is a follower whose velocity converges to the **point's** velocity: a
critically damped tracker. `SmoothPen.CriticallyDampedStep` (`Character/Input/SmoothPen.cs:47`)
is exactly that, already dt-stable, already designed to live in `ActionVars`, and already
what `BlockPaintAction` uses for its ball (:1839). Tracking a point moving at `v` it settles
to velocity `v` with a constant positional lag (`≈ v·smoothTime`), so the ball's velocity
at detach ≈ the point's velocity in both phases — the uniformity you want, by construction.
`Vmax` becomes a plain cap on `|BallVel|`, which is then the one throw-speed cap.

The same follower gives **static ⇒ drop** for free: a still mouse at release means the
point has ≈0 velocity and the ball is already sitting on it, so the chase ends immediately
with ≈0 velocity. No dead-zone knob needed; a 1-px tremor yields a 1-px-per-frame point
that the ball catches in a frame.

"Reached" can't be a distance test, because a velocity-matching follower never closes the
lag on a moving point. Use velocity convergence — `|BallVel − PointVel| < GrabCatchSpeed`
— plus a hard cap on chase time (`GrabChaseMaxSeconds`) for the case where the point
outruns `Vmax` (the ball then detaches at `Vmax` toward the point: the clamp, naturally).

### 4.2 The point's release velocity: unclamped, world-frame, smoothed

Two things the point does right that the doc should keep explicit:

- Its release velocity is the **unclamped** mouse velocity even though its held position is
  clamped. That matters: if the throw were read off the clamped position, an outward swipe
  (the throw gesture) would be attenuated exactly when it counts — with `r' = R·tanh(r/R)`
  the gain at `r = R` is `sech² ≈ 0.42`. Position clamped, velocity not.
- **World frame is the right frame.** `MouseWorldPosition` velocity ≈ camera velocity +
  screen-space swipe, and the camera follows the player, so a running player's static mouse
  gives the point the run speed — which is what "the orb inherits the thrower's velocity"
  (:3063) wants anyway. Contrast `c0a4a3a`, which went player-relative for *gesture
  recognition*; a throw velocity should stay in world space. If the camera lag ever shows up
  as a "backwards" throw on a fast turn-around, see §7.4.

Estimation: an EMA of `ΔMouseWorldPosition/dt` kept in `ActionVars` (`SwipeVel`,
`PrevCursor`) is one `Vector2` and no ring lookup. `Controller.GetPrevious(k)` (32-frame
ring, `Controller.cs:36-52`) is an equivalent zero-state alternative; either is
deterministic. ~4 frames / 67 ms of smoothing is the right order — enough to kill jitter,
short enough that the flick just before release is what's measured.

### 4.3 Nothing may outlive the release inside the action — the point is an entity the action drives, then hands off

Today LMB-up *is* the exit (:2810), and `Exit` both throws and consumes the click intents
(:3070). After release two things keep running with no button held: the **peel contest**
(if the group hadn't broken out) and the **chase** (once a ball exists). The tempting
answer — a `Releasing` phase inside `BlockGrabAction`, like `GrabAction.GrabThrowing`
(:3308) — is wrong for a feel reason: the natural follow-up to a throw is another action,
a slash say, pressed within a few frames. Either the grab's 46 Active lock eats that press
for up to 0.25 s, or (if the lock is dropped for the release phase) the follow-up preempts
the grab and kills a contest the player already perceives as a thrown throw. Both are
wrong, so the contest cannot live in the action, and the same argument already put the
chase in the projectile.

**Route the pull through a `PullPoint` entity from the press onward, and transfer ownership
on release.** The action becomes thin — input in, summary out:

- **Spawn on press.** `BlockGrabAction.Enter` spawns a `PullPointEntity` (new
  `EntityKind.PullPoint`) and keeps its `EntityId` in `vars.PullPointId` (a value struct,
  so it snapshots with `ActionVars`). Object references must not be held: a rollback
  restore rehydrates fresh entity objects (`Simulation.cs:455-478`), ids survive. Resolve
  it each frame through a new `IEntitySpawner.Resolve(EntityId)` (`World.IsAlive` +
  `EntityRef.Obj`, `Simulation.cs:203-214` shows the registration it inverts).
- **Drive while held** (`Driven = true`). Each frame the action writes `TargetPos` (raw
  cursor in the peel, soft-clamped in the carry), `OwnerPos`, and `SwipeVel` into the
  entity, and mirrors `PeelCount / PeelStrain / OrbHeld` back into `ActionVars` as
  read-only copies — `CheckConditions`, `Draw`'s tether tint, and the existing tests keep
  their shape. Players update before entities within a step (`Simulation.cs:289-297`), so
  a write on frame N is consumed on N and read back on N+1: one frame of latency on
  `PeelSnapped`/`OrbHeld`, harmless.
- **The entity owns the mechanics.** Paint → spring → wear → glue → break-out
  (`UpdatePeel`/`PaintTether`/`BaseGlue`/`BreakOutGroup`, :2869-3025) move onto the entity,
  re-parameterized on `(ref PeelGroupComp, target, ownerPos)` — a mechanical re-homing with
  no behaviour change while driven. The held ball, its tracker and its dissipation live
  there too: while held, the point entity *is* the orb.
- **Hand off on release** (`Release(swipeVel)`: `Driven = false`, `PointVel = swipeVel`).
  The action consumes intents, stamps recovery (only if something was harvested, as
  today), and exits **immediately**. The point flies straight, finishes any live contest,
  spawns the projectile, dies. No lingering lock; the slash goes through.
- **Where the group lives:** a sparse ECS value component `PeelGroupComp` (the 25-member
  buffer + count + strain + snapped) added only to point entities. `World.Capture`
  snapshots every value store generically (`Sim/ECS/World.cs:134-150`), so other entities
  pay nothing; the entity marshals it in `CaptureState/RestoreState` alongside
  `EntityData`, and `EntityFactory.Rehydrate` gets a `PullPoint` case.
- **Lifecycle:** the point dies on snap, when a held ball dissipates to zero, when it spawns
  the projectile, or `GrabPointMaxSeconds` after hand-off. The action ends when `Resolve`
  returns null — that *is* the snap path from the action's side.

Costs to budget for, not discover:
- Plumbing: `IEntitySpawner.Resolve`, `EntityKind.PullPoint`, `EntityFactory` case,
  `PeelGroupComp`, and a generic overlay-draw hook — today `Game1.cs:1039-1040` type-checks
  `EnemyEntity` for `DrawOverlay`; the point needs the same for tether tint + orb, so
  generalize to an interface.
- **The test harness.** `SimRunner` (`MTile.Tests/Sim/SimRunner.cs:76,172`) calls
  `PlayerCharacter.Update` with no spawner and never updates entities — `BlockPeelTests`
  runs entirely inside the action today. An entity-resident peel needs either (a)
  `SimRunner` to grow a spawner and an entity-update pass mirroring `Simulation.Step`
  :293-297 (it already mirrors that phase order, so this is in character), or (b) the peel
  tests to move onto a real `Simulation.Step` with scripted `PlayerInput`. **(a).** Note
  `FreeHangingBlock_OneSweep_GrabsAndThrows` never actually checked the throw — with no
  spawner, `Exit` skips the spawn — so (a) also makes its name true.
- `Entity.Update` receives only the primary player (`Simulation.cs:297`); the point must
  not read it. Everything it needs — owner position, faction, block type — is written in
  by the action each frame or captured at spawn.
- One live point per player: `CheckPreConditions` refuses while `vars.PullPointId` still
  resolves (a re-press during a live contest). Default: ignore the press; see §7.5.
- Intent consumption and recovery happen on the release frame by construction now (the
  action exits there), so the "0.25 s late `EnergyBall`" hazard is gone with the phase.

### 4.4 No drag on the point — and the contest has a short fuse

Drag on the point would break the uniformity: the peel-case ball starts in the crater and
chases longer than the carry-case ball at the hand, so with a decaying point it detaches
slower. A throw shouldn't be weaker because the blocks took a few frames to come loose. So
the point coasts at constant velocity — `PointPos += PointVel·dt`, nothing else. The
projectile carries a copy of the point during its chase (§5), and a straight line is the
one thing two copies can't disagree about.

The consequence is a short contest: at 600 px/s the point moves 10 px/frame and
`PeelSpringMax = 60` is hit at `dist ≈ 97 px` (`4·(d/16)^1.5 = 60`), so a swipe-release
resolves in ~10 frames, snap or break-out. That's a fine feel — a yank is short — and
"snap" is the honest failure. If playtests want a longer window, the knob is the
post-release spring (e.g. scale `PeelSpringMax` after hand-off), not point drag.

Don't expect T5 to succeed on fresh anchored ground: glue wear is time-integrated
(`GlueWear += 0.4·share·dt`), so a single fast swipe on an unworked dirt block (base glue
`1.5·(0.5+3) = 5.25`, floor 0.79) cannot erode enough in 10 frames. That's the shipped
design ("a small or free-hanging group pops in a single sweep") and T5 should be read as
"don't forfeit a contest that *would* have been won", not "make every swipe win". If
playtesting wants one-swipe grabs on worked ground, that's a peel-knob change, separate.

### 4.5 Where the ball spawns / is held — two geometry hazards

- **Clamp phase.** The sketch applies the soft clamp "with mouse down", i.e. also during the
  peel. The peel spring reads raw cursor distance and its six tests (`BlockPeelTests`) and
  every `Peel*` knob are tuned to that. Remapping the endpoint through `tanh` changes the
  force curve (and makes far groups harder to snap on). **Apply the clamp to the carry-phase
  ball target only**; leave the peel spring on the raw cursor.
- **Ball inside terrain.** A 64 px carry radius around the player reaches into the floor
  (player standing on ground, cursor pointing down). A `LobbedAreaProjectile` spawned inside
  solid is velocity-halted on frame 1 ⇒ "lands" ⇒ deposits the mound + AOE at that cell. The
  current hand offset (1.4·R along aim) mostly dodges this by accident. Fix: the servo target
  is additionally pushed out of solid (cheap: if the target cell is solid, fall back to
  `body + aim·HandDistance`), and spawn refuses a solid cell the same way.
- **Drop at the feet.** T4's "just drops down" means a zero-velocity projectile landing at
  the player's feet and erupting a `budget`-sized mound + AOE hitbox there. The hitbox is
  faction-filtered, but whether `TileMassField`'s sprout commit avoids the player's own
  body is an open question (§7) — check before shipping, or a self-drop entombs you.

### 4.6 Smaller notes

- T3's "same sprite": the two draw paths are structurally different (§2, last bullet) and
  neither can reach the atlas today. Not hard, but it's a small render plumbing task, not
  a one-liner — see Phase 3.
- T1 is a `const` → hot-reload knob. Note `RemainingBlocks` floors to an int, so a 9-block
  orb over 2 s loses one block every 0.22 s — "decay" here is a staircase. Lengthening the
  window is the whole fix; a grace period before bleed starts is a cheap optional second
  knob.
- The projectile keeps gravity (it's a lob that lands and erupts) — unlike `MassBall`
  (gravity 0, tile-phasing, leaks as it flies). The sketch doesn't say which; the "dirt
  ball with a trail that lands" reading is `LobbedAreaProjectile`, and nothing below
  changes that.

---

## 5. Revised design

The sketch's mechanism, with the follower, the end condition, and the ownership pinned
down. One rule, stated once:

> **The pulling point** is an entity. While LMB is down the action *drives* it — its
> position is the mouse (soft-clamped to the player once blocks are in hand) and it paints
> and pulls. On release the action *hands it off* with the mouse's velocity and exits; the
> point flies straight and finishes whatever contest is still live. **The ball**, from the
> moment it exists, follows the point with a critically damped, speed-capped tracker —
> inside the point entity while held, inside the projectile after release. When the ball's
> velocity has converged to the point's (or the chase times out) the point is gone and the
> ball is a free ballistic lob.

Everything sim-side is value-typed and snapshotted. Names are suggestions.

**`ActionVars`** (the action keeps only what it needs to drive and to report):

| Field | Role |
|---|---|
| `PullPointId` | `EntityId` of the point this activation spawned; resolved every frame |
| `SwipeVel`, `PrevCursor` | EMA of cursor world velocity — the hand-off velocity |
| `PeelCount`, `PeelStrain`, `OrbHeld` | **mirrors** copied from the entity each frame (existing fields, now read-only from the action's side) |
| `PeelMembers`, `PeelSnapped`, `OrbBlocks`, `OrbType`, `ChargeTime` | **removed** — moved to the entity |

**`PullPointEntity`** (`EntityKind.PullPoint`; `EntityData` for the scalars, sparse
`PeelGroupComp` for the group):

| Field | Role |
|---|---|
| `PointPos`, `PointVel` | the point; position written by the action while driven, integrated after hand-off |
| `Driven` | true while the action owns it |
| `TargetPos`, `OwnerPos` | written by the action each driven frame (kernel/spring endpoint; `GrabReach` origin) |
| `OwnerFaction`, `BlockType` | captured at spawn |
| `BallPos`, `BallVel`, `Blocks`, `HarvestBlocks`, `BallType`, `CarryTime` | the held ball (`Blocks > 0` ⇔ orb held) |
| `HandoffTime` | seconds since release; lifetime cap |
| `PeelGroupComp` | members (25), count, strain, snapped |

**`LobbedAreaProjectile`** (`EntityData`, LobbedArea slot): `PointPos`, `PointVel`, `Chasing`.

**Action, LMB down** — resolve the point (null ⇒ exit: it snapped or dissipated). Write
`TargetPos` — raw cursor in the peel, `player + softClamp(cursor − player, R)` pushed out
of solid (§4.5) once the ball is held — plus `OwnerPos`, and update the `SwipeVel` EMA.
Mirror the summary back. Movement modifiers as today while `OrbHeld`.

**Action, LMB up or preempted** — `point.Release(SwipeVel)`; consume `Click`/`PressEdge`;
stamp recovery if `HarvestBlocks > 0`; exit. Nothing else — the action is finished the
frame the button comes up.

**Point, driven** — prune → paint (kernel at `TargetPos`, admission within `GrabReach` of
`OwnerPos`) → spring with endpoint `TargetPos` → wear → glue → break-out. Break-out seeds
the ball at the group COM with zero velocity; from then on the ball tracks `TargetPos`
(`SmoothPen.CriticallyDampedStep`, `GrabBallSmoothTime`, `|BallVel| ≤ GrabBallMaxSpeed`)
— the first frames visibly pull it out of the crater to the hand — and `Blocks` bleeds
over `GrabDissipateSeconds`. Ball gone ⇒ die.

**Point, released** — if a ball was in hand: spawn the projectile at `BallPos` with
`BallVel` and `(PointPos, PointVel, Chasing = true)`, die. Otherwise: `PointPos +=
PointVel·dt`; prune → spring with endpoint `PointPos` → wear → glue → break-out (no paint).
Break-out ⇒ spawn the projectile at the **group COM** with zero velocity and the *same*
`(PointPos, PointVel, Chasing = true)`, die. Snap, or `HandoffTime ≥ GrabPointMaxSeconds`
⇒ die. The two spawns are the same call; only the ball's starting state differs — that is
the uniformity, in code.

**Projectile chase** (`LobbedAreaProjectile.ProjectileUpdate`, while `Chasing`): gravity 0,
`IgnoreTiles`; `PointPos += PointVel·dt`; `CriticallyDampedStep(Body.Position, Body.Velocity,
PointPos, GrabChaseSmoothTime, dt)`, clamp `|Body.Velocity| ≤ GrabBallMaxSpeed`. Detach when
`|Body.Velocity − PointVel| < GrabCatchSpeed` or `Age ≥ GrabChaseMaxSeconds`: `Chasing =
false`, gravity 1, tiles on, and the existing land-and-erupt logic takes over (the
`ArmDelay` land check must not run while chasing — a ball starting in a crater at rest would
"land" on frame 1). Outcome: detach velocity ≈ `PointVel`, capped — the same number in both
phases.

**Soft clamp** (carry only, §4.5): `r' = R·tanh(r/R)` along the same direction. Identity-ish
inside ~0.5R, asymptotes to `R`. A hard `min(r, R)` also works — the clamp only shapes where
the ball sits, never the throw.

**Knobs** (all `MovementConfig`, hot-reload, beside the `Peel*` block):

| Knob | Default | Notes |
|---|---|---|
| `GrabDissipateSeconds` | 2.0 → try 4-5 | T1 |
| `GrabCarryRadius` | 64 (`= BuildReach`) | soft clamp radius |
| `GrabBallSmoothTime` | 0.08 | held tracker; shorter than paint's 0.12 — it's in hand |
| `GrabBallMaxSpeed` | 800 | cap on ball speed, held and chasing — **the** throw-speed cap (today's fixed `ThrowSpeed` is 620) |
| `GrabChaseSmoothTime` | 0.05 | post-release tracker; tighter so detach comes fast |
| `GrabCatchSpeed` | 40 | `\|BallVel − PointVel\|` below this ⇒ detach |
| `GrabChaseMaxSeconds` | 0.25 | chase hard cap (point outran the ball ⇒ detach at cap speed) |
| `GrabSwipeSmoothing` | 0.35 | EMA factor per frame |
| `GrabPointMaxSeconds` | 0.25 | point lifetime after hand-off — the contest's hard cap |

**Rendering** (all render-only, none of it feeds the sim):

- `Drawing/MassOrbTextures` — built once at load from the `TileTextureAtlas` patch × a
  circular alpha mask, one `Texture2D` per `TileType` (same `GetData/SetData` pattern as
  `TileTextureAtlas.Build`, so it works on DesktopGL and KNI). Drawn scaled to radius
  `OrbMaxRadius·√(remaining/harvested)` for the held orb and `∝ √budget` for the projectile
  — one `DrawOrb(sb, pos, radius, type)` used by both. The action overlay gets it through
  a static reference set at load (as `ChunkRenderer.Atlas` is), the projectile through a
  `MassOrbSprite : Sprite`. `LobbedAreaProjectile` needs public `TileType`/`Budget`.
- Trail (level-triggered): `CosmeticUpdateSystem` walks `_sim.Entities`, and for each
  `LobbedAreaProjectile` spawns `⌈speed/400⌉` particles per rendered frame at its
  position, `ParticleKind.Line`, velocity `−0.3·ballVel` + jitter, tinted
  `TilePalette.BaseColor(type)`. Re-derived from state ⇒ rollback-safe by construction.
- Landing burst (edge-triggered): add `PresentationKind.MassLand` emitted from the
  projectile's detonation (needs a sim → presentation hook for entities; the tile
  events on `ChunkMap` are the pattern, `Game1.cs:372-384`) keyed by entity id, firing
  `Effects.TileBreak`-style debris in the material color. The mound's own `TilePlace`
  events already fire as tiles commit, so this is optional polish.

---

## 6. Phases

Each is independently shippable. Phase 1 is a structural refactor with **no feel change**
— it exists so the existing `BlockPeelTests` (6 tests) can be re-pointed at the entity and
stay green before any behaviour moves. Phase 1 also extends `SimRunner` (§4.3) with a
spawner and an entity-update pass; from then on every phase is testable with
`SimRunner` + `InputScript`. `FreeHangingBlock_OneSweep_GrabsAndThrows` is the one most
likely to need its script adjusted in Phase 2, since its release becomes a swipe-velocity
throw and the projectile spends its first frames chasing rather than flying.

| Phase | Delivers | Touches | Test |
|---|---|---|---|
| 0 | T1: `DissipateSeconds` → `GrabDissipateSeconds` knob | `MovementConfig.cs`, `configs/movement_config.json`, `ActionStates.cs:2776,3144` | `RemainingBlocks` at t = knob/2 is half |
| 1 | **Pull-point entity refactor, feel-identical.** `PullPointEntity` + `PeelGroupComp` + `EntityKind`/`EntityFactory` case; `IEntitySpawner.Resolve`; peel mechanics re-homed onto the entity; action drives + mirrors; overlay-draw interface; `SimRunner` spawner + entity pass. Release still throws as today (aimed, `ThrowSpeed`); a release before break-out still ends with nothing (`Release` ⇒ point dies immediately in this phase) | `Entities/PullPointEntity.cs` (new), `EcsComponents.cs`, `EntityKind.cs`, `EntityFactory.cs`, `Entity.cs` (`IEntitySpawner`), `Simulation.cs`, `ActionStates.cs`, `ActionVars.cs`, `Game1.cs:1039`, `MTile.Tests/Sim/SimRunner.cs` | all 6 `BlockPeelTests` green unchanged in intent; **rollback mid-peel**: snapshot at frame N with a live driven point, step on, restore, re-step ⇒ identical `PeelCount`/glue (`SnapshotRoundTrip` pattern); the throw in `…GrabsAndThrows` now actually asserted |
| 2 | T4: held ball in the point (tracker to the clamped target, drawn at ball), `SwipeVel` EMA, hand-off velocity, projectile chase (`PointPos/PointVel/Chasing`, gravity/tiles flip at detach), solid-cell guard | `PullPointEntity.cs`, `ActionStates.cs`, `LobbedAreaProjectile.cs`, `EcsComponents.cs` | static release ⇒ ball detaches within a few frames at ≈0 relative speed and falls; 300 px/s leftward swipe ⇒ ball detaches flying left at ~300 px/s; swipe past `R` is not attenuated; running player + static mouse ⇒ ball carries the run speed; snapshot round-trip of a chasing projectile |
| 3 | T5: the point lives `GrabPointMaxSeconds` after hand-off and finishes the contest; break-out spawns the same chasing projectile from the COM | `PullPointEntity.cs` | free-hanging block, release 3 frames before break-out ⇒ still throws, and **its detach velocity equals the carry-case throw for the same swipe** (the uniformity test); **a slash on the frame after release enters and the throw still lands** (the §4.3 test); anchored stone ⇒ point dies empty within cap; no `EnergyBall` fires on the release frame |
| 4 | T3: `MassOrbTextures`, shared `DrawOrb`, projectile sized by budget | `Drawing/`, `LobbedAreaProjectile.cs`, `PullPointEntity` overlay, `Game1.cs` load | visual — screenshot via `/run` |
| 5 | T2: trail + landing burst | `CosmeticUpdateSystem.cs`, `PresentationEvents`, `Effects.cs` | visual; verify no double-burst on a rollback re-sim (the `(frame,id)` dedup) |

Phases 0-3 are sim changes: `dotnet build MTile.Core.csproj` plus
`dotnet test --filter "FullyQualifiedName~BlockPeel"` while iterating (not the full suite).
Both hosts compile the same source — nothing above uses a DesktopGL-only API, but Phase 4's
texture build should be checked under KNI (`/web-publish` smoke).

---

## 7. Open questions

1. **Self-drop safety.** Does `TileMassField`'s sprout commit refuse a cell occupied by a
   player body? If not, a T4 drop at the feet can entomb the thrower. Check before Phase 1
   ships; if it doesn't, the drop should spawn slightly ahead of the player or the mound
   deposit should skip occupied cells.
2. **Recovery on a drop.** Should letting the ball fall cost the same 0.2 s recovery as a
   throw? Probably not — but a free drop makes "grab, drop, grab" a zero-cost dig loop.
   Left at "same recovery" for Phase 1; tune with the playtest.
3. **Should the peel spring endpoint be the ball, not the cursor, once the ball exists?**
   Moot in this design (the ball only exists post-break-out), but if Phase 2's optional
   "ball leaves the crater toward `P`" is wanted, that's the hook.
4. **Camera coupling of the swipe.** `MouseWorldPosition` = camera + screen; the camera
   lags the player, so the world-space mouse velocity carries *lagged* camera motion rather
   than the player's true velocity. Usually indistinguishable; if a fast turn-around ever
   throws "backwards", measure the swipe as screen-space deltas (`PlayerInput.MousePosition`
   is also in the ring) scaled by camera zoom, and add `Body.Velocity` explicitly.
5. **Preemption and re-press.** If something preempts the grab while LMB is still down
   (only `GrabAction` 48 and `GuardRetaliate` 55 sit above 46), should `Exit` hand the point
   off with the current swipe velocity (the hand leaves) or kill it (the grab was
   interrupted)? Default: hand off — it's what the player's hand did. And a Shift+LMB
   re-press while the previous point is still contesting: default ignore (precondition
   fails while `PullPointId` resolves); if that feels sticky, let the re-press kill the old
   point instead.
