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

Mostly yes. T4 and T5 are the right diagnosis and the spring-extension idea is the right
family. Five things need changing, in order of how much they matter.

### 4.1 The ball's velocity should be the throw — the phantom chase is an indirection

Once the held mass is a real ball servo-following `P` (step 5), its own velocity at the
moment of release **is** the swipe velocity — no history lookup, no phantom to chase.
`BlockPaintAction` already does this (`BallVel` at `Exit`, :1872). Static mouse ⇒ ball at
rest ⇒ drop. Fast swipe ⇒ ball carrying the swipe ⇒ fly. That is T4 with one field-pair
(`BallPos/BallVel`, which `BlockGrabAction` doesn't use yet) and zero new concepts.

The chase as written also has no end condition: if the swipe is faster than the ball's speed
cap the ball never reaches `P`, `P` is never destroyed, and all it ever contributed was a
direction. And if the ball *does* reach it, its velocity at that instant is whatever the
servo gave it — again just "cap speed, toward `P`". Either way the phantom adds nothing the
release-frame ball velocity doesn't already encode. **Recommendation:** drop the post-release
`P` for the extracted case; launch the ball with its own velocity. Keep `P` only for §4.3.

### 4.2 …except the soft clamp eats the swipe

This is the one place the sketch's history-based velocity has a real reason to exist. If the
ball tracks the *clamped* point, an outward swipe — the throw gesture — is attenuated
exactly when it matters: with `r' = R·tanh(r/R)` the gain at `r = R` is `sech² = 0.42`, and
past it near zero. A throw flicked out past the radius arrives at the ball at a fraction of
its speed.

So: the ball's **position** tracks the clamped point (it stays near the hand), but the
**throw velocity** is measured on the *unclamped* cursor. Concretely, keep a smoothed
cursor velocity `vars.SwipeVel` (EMA over the last ~4 frames, ≈ 67 ms, of
`ΔMouseWorldPosition / dt`) and launch at

    launch = Body.Velocity + clampLength(SwipeVel − Body.Velocity, GrabThrowMaxSpeed)

The subtraction/re-add is the player-relative frame (`c0a4a3a`'s lesson): a running player
with a static mouse sees the mouse move at running speed in world space; that component must
not count as a swipe but *should* be inherited (the current throw already inherits
`Body.Velocity`, :3063). An EMA in `ActionVars` is one `Vector2` and needs no ring lookup;
if you'd rather use `Controller.GetPrevious(k)` it's equivalent, but note the ring stores
mouse positions only — there is no player-position history, so "player-relative over k
frames" has to approximate with the current `Body.Velocity` anyway.

Also make "static ⇒ drop" explicit with a dead-zone: `|SwipeVel − Body.Velocity| <
GrabDropSpeed` ⇒ launch at `Body.Velocity` only (no aim component at all). Otherwise a
1-px mouse tremor at release becomes a slow lob in a random direction.

### 4.3 T5 needs the action to outlive the release — decide where that lives

Today LMB-up *is* the exit (:2810), and `Exit` both throws and consumes the click intents
(:3070). The sketch needs the peel contest to keep running for a short window after release
with no button held, which means one of:

- **(a) a `Releasing` phase inside `BlockGrabAction`** — the same shape as
  `GrabAction.GrabThrowing` (:3308). `CheckConditions` stays true until the contest
  resolves or a hard cap (`GrabReleaseMaxSeconds ≈ 0.25`) elapses. The 25-member
  `PeelMemberBuffer` already lives in `ActionVars`, so nothing moves. **Recommended.**
- (b) move the group into an entity — would drag the member buffer into `EntityData`
  (bloats every entity snapshot for one rare case). No.

Consequences of (a) to plan for, not discover:
- Intent consumption (`Click`/`PressEdge`, :3070) and the recovery stamp must fire on the
  **release frame**, not at eventual exit — otherwise the Shift+LMB release routes into
  `EnergyBall/Beam` 0.25 s late.
- Movement modifiers should drop on release (the orb isn't in hand any more).
- 46/46 priority holds through the window: the player can't start another action for
  ≤0.25 s after a release that's still contesting. Acceptable — say so in the header.
- `PaintTether` must **not** run in the phase (nothing is painting), only prune → spring →
  wear → glue → break-out, with the spring endpoint = `P`.

### 4.4 The phantom needs drag, and the contest has a short fuse either way

With `P` coasting at constant velocity the spring goes superlinear fast: at 600 px/s `P`
moves 10 px/frame and `PeelSpringMax = 60` is hit at `dist ≈ 97 px` (`4·(d/16)^1.5 = 60`),
so a swipe-release resolves in ~10 frames — snap or break-out. That is *fine* as a feel
(a yank is short), but with no drag the failure mode is always "snap", never "held on and
let go". Give `P` an exponential velocity decay (`GrabReleaseDrag`) so distance growth
tapers and the glue-erosion integral gets a real window; combine with the hard cap.

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

Everything sim-side lives in `ActionVars` (value type, snapshotted for free). Names are
suggestions.

**State added to `ActionVars`** (reuse `BallPos/BallVel` — `BlockGrabAction` never touches
them today and only one action owns `vars` at a time):

| Field | Role |
|---|---|
| `BallPos`, `BallVel` | the held ball (carry phase) |
| `SwipeVel` | EMA of cursor world velocity |
| `PrevCursor` | last frame's `MouseWorldPosition` for the EMA |
| `GrabPhase` | `Peel / Carry / Releasing` (replaces `OrbHeld` + implicit) |
| `PointPos`, `PointVel` | phantom `P`, `Releasing` only |
| `ReleaseTime` | seconds in `Releasing` |

**Peel phase** — unchanged, except every frame also updates `SwipeVel`/`PrevCursor`.

**Break-out** — seeds the ball: `BallPos = group COM` (where the blocks were),
`BallVel = 0`. The first carry frames then visibly pull the ball out of the crater toward
the hand — the "yank" for free.

**Carry phase** — per frame:
1. `target = player + softClamp(cursor − player, R)`, pushed out of solid (§4.5).
2. `SmoothPen.CriticallyDampedStep(ref BallPos, ref BallVel, target, GrabBallSmoothTime, dt)`;
   clamp `|BallVel| ≤ GrabBallMaxSpeed`.
3. Bleed as today, with `GrabDissipateSeconds` from config.
4. Draw the orb **at `BallPos`** (not at the hand).

**Release with the ball in hand** — launch a `LobbedAreaProjectile` at `BallPos` with the
§4.2 velocity, consume intents, stamp recovery, exit. No `Releasing` phase.

**Release during peel** — enter `Releasing`: `PointPos = cursor`,
`PointVel = SwipeVel` (world frame), consume intents now. Each frame: `PointVel *= (1 −
GrabReleaseDrag·dt)`, `PointPos += PointVel·dt`; run prune → spring (endpoint `PointPos`)
→ wear → glue → break-out. Outcomes:
- break-out ⇒ spawn the projectile at the **group COM** with velocity
  `Body.Velocity + clampLength(PointVel − Body.Velocity, GrabThrowMaxSpeed)`, stamp
  recovery, exit. (Optionally: seed a 2-3 frame carry-style servo toward `PointPos` first
  so the ball visibly leaves the crater; instant is what every other throw does and is the
  default here.)
- snap, or `ReleaseTime ≥ GrabReleaseMaxSeconds`, or LMB pressed again ⇒ exit empty.

**Soft clamp**: `r' = R·tanh(r/R)` along the same direction. Identity-ish inside ~0.5R,
asymptotes to `R`. One line; if you want a hard cap instead, `min(r, R)` also works since
the ball's velocity no longer feeds the throw.

**Knobs** (all `MovementConfig`, hot-reload, beside the `Peel*` block):

| Knob | Default | Notes |
|---|---|---|
| `GrabDissipateSeconds` | 2.0 → try 4-5 | T1 |
| `GrabCarryRadius` | 64 (`= BuildReach`) | soft clamp radius |
| `GrabBallSmoothTime` | 0.08 | shorter than paint's 0.12 — it's in hand |
| `GrabBallMaxSpeed` | 1200 | servo cap; only limits the visual chase |
| `GrabThrowMaxSpeed` | 620 (current `ThrowSpeed`) | cap on the swipe component |
| `GrabDropSpeed` | 60 | below this the release is a drop |
| `GrabSwipeSmoothing` | 0.35 | EMA factor per frame |
| `GrabReleaseDrag` | 4.0 | phantom velocity decay /s |
| `GrabReleaseMaxSeconds` | 0.25 | `Releasing` hard cap |

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
adjusted, since its release becomes a swipe-velocity throw.

| Phase | Delivers | Touches | Test |
|---|---|---|---|
| 0 | T1: `DissipateSeconds` → `GrabDissipateSeconds` knob | `MovementConfig.cs`, `configs/movement_config.json`, `ActionStates.cs:2776,3144` | `RemainingBlocks` at t = knob/2 is half |
| 1 | T4: held ball (`BallPos/BallVel` servo to clamped target, drawn at ball), swipe-velocity launch with drop dead-zone, solid-cell guard | `ActionStates.cs` carry phase + `Exit`; `ActionVars.cs` | static release ⇒ projectile speed ≈ `Body.Velocity`; 300 px/s leftward swipe ⇒ projectile flies left at ~300 px/s; swipe past `R` is not attenuated; running throw inherits velocity |
| 2 | T5: `Releasing` phase + phantom with drag; intents/recovery on release frame | `ActionStates.cs` `CheckConditions/Update/Exit` split | free-hanging block, release 3 frames before break-out ⇒ still throws; anchored stone ⇒ exits empty within cap; no `EnergyBall` fires on the release frame |
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
   lags the player, so the world-space mouse velocity carries lagged camera motion. The
   player-relative subtraction in §4.2 removes most of it; if a fast turn-around still
   throws "backwards", switch the EMA to screen-space deltas (`PlayerInput.MousePosition`
   is also in the ring) scaled by the camera zoom.
