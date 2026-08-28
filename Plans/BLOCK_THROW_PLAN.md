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

### 4.3 Two things outlive the release — decide where each lives

Today LMB-up *is* the exit (:2810), and `Exit` both throws and consumes the click intents
(:3070). After release two things keep running with no button held: the **peel contest**
(if the group hadn't broken out) and the **chase** (once a ball exists). They have
different natural homes.

- **The contest stays in the action** — a `Releasing` phase inside `BlockGrabAction`, the
  same shape as `GrabAction.GrabThrowing` (:3308). `CheckConditions` stays true until the
  group breaks out, snaps, or a hard cap (`GrabReleaseMaxSeconds ≈ 0.25`) elapses. The
  25-member `PeelMemberBuffer` already lives in `ActionVars`; moving it into `EntityData`
  would bloat every entity snapshot for one rare case.
- **The chase goes in the projectile.** The moment a ball exists with LMB up — at release
  in the carry case, at break-out in the `Releasing` case — spawn the `LobbedAreaProjectile`
  carrying the point (`PointPos`, `PointVel`, `Chasing` in `EntityData`: two `Vector2` and a
  bool, cheap) and let the entity run the follower itself: gravity 0 and `IgnoreTiles`
  while chasing (`MassBall` already uses both), then flip to the normal ballistic lob when
  it detaches. One chase implementation, entered from two places with the same arguments —
  that's the uniformity, in code. It also frees the action the instant a carried ball is
  released (no priority lock through the chase), and the only reason the action lingers is
  a contest that's still live.

  The alternative — running the chase in the action's `Releasing` phase for both cases and
  spawning the projectile at detach — works too, and keeps `EntityData` untouched, at the
  cost of the 46/46 lock lasting through every throw's chase. Pick the entity.

Consequences to plan for, not discover:
- Intent consumption (`Click`/`PressEdge`, :3070) and the recovery stamp must fire on the
  **release frame**, not at eventual exit — otherwise the Shift+LMB release routes into
  `EnergyBall/Beam` 0.25 s late.
- Movement modifiers should drop on release (the orb isn't in hand any more).
- 46/46 priority holds through the window: the player can't start another action for
  ≤0.25 s after a release that's still contesting. Acceptable — say so in the header.
- `PaintTether` must **not** run in the phase (nothing is painting), only prune → spring →
  wear → glue → break-out, with the spring endpoint = `P`.
- The point's flight has to be reproduced identically in the action (contest) and in the
  entity (chase): a straight line, `PointPos += PointVel·dt`, no drag (§4.4). Keep it that
  trivial so there's nothing to disagree about.

### 4.4 No drag on the point — and the contest has a short fuse

Drag on the point would break the uniformity: the peel-case ball starts in the crater and
chases longer than the carry-case ball at the hand, so with a decaying point it detaches
slower. A throw shouldn't be weaker because the blocks took a few frames to come loose. So
the point coasts at constant velocity, in both the action and the entity.

The consequence is a short contest: at 600 px/s the point moves 10 px/frame and
`PeelSpringMax = 60` is hit at `dist ≈ 97 px` (`4·(d/16)^1.5 = 60`), so a swipe-release
resolves in ~10 frames, snap or break-out. That's a fine feel — a yank is short — and
"snap" is the honest failure. If playtests want a longer window, the knob is the
post-release spring (e.g. scale `PeelSpringMax` during `Releasing`), not point drag.

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

The sketch's mechanism, with the follower and end condition pinned down. One rule, stated
once:

> **The pulling point `P`** is where the mouse is while LMB is down (soft-clamped to the
> player in the carry phase), and flies off in a straight line at the mouse's velocity when
> LMB comes up. **The ball**, from the moment it exists, follows `P` with a critically
> damped, speed-capped tracker. When the ball's velocity has converged to `P`'s (or the
> chase times out) `P` is destroyed and the ball is a free ballistic lob.

Everything sim-side is value-typed and snapshotted (`ActionVars` in the action,
`EntityData` in the projectile). Names are suggestions.

**State added to `ActionVars`** (reuse `BallPos/BallVel` — `BlockGrabAction` never touches
them today and only one action owns `vars` at a time):

| Field | Role |
|---|---|
| `BallPos`, `BallVel` | the held ball, carry phase |
| `SwipeVel` | EMA of cursor world velocity (the point's release velocity) |
| `PrevCursor` | last frame's `MouseWorldPosition` for the EMA |
| `GrabPhase` | `Peel / Carry / Releasing` (replaces `OrbHeld` + implicit) |
| `PointPos`, `PointVel` | the flying `P`, `Releasing` only |
| `ReleaseTime` | seconds in `Releasing` |

**State added to `EntityData`** (LobbedArea slot): `PointPos`, `PointVel`, `Chasing`.

**Peel phase** — unchanged (spring endpoint = raw cursor), plus the `SwipeVel` EMA.

**Break-out while held** — seeds the ball at the group COM with zero velocity; the carry
tracker then visibly pulls it out of the crater to the hand. No special case.

**Carry phase** — per frame:
1. `P = player + softClamp(cursor − player, R)`, pushed out of solid (§4.5).
2. `SmoothPen.CriticallyDampedStep(ref BallPos, ref BallVel, P, GrabBallSmoothTime, dt)`;
   clamp `|BallVel| ≤ GrabBallMaxSpeed`.
3. Bleed as today, with `GrabDissipateSeconds` from config.
4. Draw the orb **at `BallPos`** (not at the hand).

**Release, ball in hand** — `P` detaches: spawn the `LobbedAreaProjectile` at `BallPos`
with `BallVel`, `PointPos = P`, `PointVel = SwipeVel`, `Chasing = true`. Consume intents,
stamp recovery, exit. The action is done; the entity chases.

**Release during peel** — enter `Releasing`: `PointPos = cursor`, `PointVel = SwipeVel`,
consume intents now, drop movement modifiers. Each frame: `PointPos += PointVel·dt`; run
prune → spring (endpoint `PointPos`) → wear → glue → break-out. Outcomes:
- break-out ⇒ spawn the projectile at the **group COM** with zero velocity and the *same*
  `(PointPos, PointVel, Chasing = true)`. Stamp recovery, exit. Identical call to the
  carry-case spawn; only the ball's start differs.
- snap, `ReleaseTime ≥ GrabReleaseMaxSeconds`, or LMB pressed again ⇒ exit empty.

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
| `GrabReleaseMaxSeconds` | 0.25 | `Releasing` (contest) hard cap |

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

Each is independently shippable and testable with `SimRunner` + `InputScript`
(`MTile.Tests/Sim/`); the existing `BlockPeelTests` (6 tests) must stay green throughout —
`FreeHangingBlock_OneSweep_GrabsAndThrows` is the one most likely to need its script
adjusted, since its release becomes a swipe-velocity throw and the projectile now spends
its first frames chasing rather than flying.

| Phase | Delivers | Touches | Test |
|---|---|---|---|
| 0 | T1: `DissipateSeconds` → `GrabDissipateSeconds` knob | `MovementConfig.cs`, `configs/movement_config.json`, `ActionStates.cs:2776,3144` | `RemainingBlocks` at t = knob/2 is half |
| 1 | T4: held ball (`BallPos/BallVel` tracker to the clamped point, drawn at ball), `SwipeVel` EMA, projectile chase (`PointPos/PointVel/Chasing` in `EntityData`, gravity/tiles flip at detach), solid-cell guard | `ActionStates.cs` carry phase + `Exit`; `ActionVars.cs`; `LobbedAreaProjectile.cs`; `EcsComponents.cs`, `EntityFactory.cs` | static release ⇒ ball detaches within a few frames at ≈0 relative speed and falls; 300 px/s leftward swipe ⇒ ball detaches flying left at ~300 px/s; swipe past `R` is not attenuated; running player + static mouse ⇒ ball carries the run speed; snapshot round-trip of a chasing projectile |
| 2 | T5: `Releasing` phase (contest only); intents/recovery/modifiers on the release frame; break-out spawns the same chasing projectile from the COM | `ActionStates.cs` `CheckConditions/Update/Exit` split | free-hanging block, release 3 frames before break-out ⇒ still throws, and **its detach velocity equals the carry-case throw for the same swipe** (the uniformity test); anchored stone ⇒ exits empty within cap; no `EnergyBall` fires on the release frame |
| 3 | T3: `MassOrbTextures`, shared `DrawOrb`, projectile sized by budget | `Drawing/`, `LobbedAreaProjectile.cs`, `ActionStates.cs:3149`, `Game1.cs` load | visual — screenshot via `/run` |
| 4 | T2: trail + landing burst | `CosmeticUpdateSystem.cs`, `PresentationEvents`, `Effects.cs` | visual; verify no double-burst on a rollback re-sim (the `(frame,id)` dedup) |

Phases 0-2 are sim changes: `dotnet build MTile.Core.csproj` plus
`dotnet test --filter "FullyQualifiedName~BlockPeel"` while iterating (not the full suite).
Both hosts compile the same source — nothing above uses a DesktopGL-only API, but Phase 3's
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
