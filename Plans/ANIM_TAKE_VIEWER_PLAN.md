# Animation Takes: record gameplay → inspect offline

Goal: record a stretch of real gameplay, save it to disk, and open it in a standalone
viewer (`dotnet run --project MTile.Demo -- --load Takes/<name>.take.json`) that can play
it forwards/backwards, pause, step frames, and overlay solver internals — contacts and
their live weights, pins, no-pen surfaces, the solved Δφ/δ/d.x — on top of the rig and
the terrain it ran through.

## The key architectural fact

`CharacterAnimator.Update(CharacterAnimSample)` is a deterministic function of the sample
stream. The sample (`Animation/CharacterAnimSample.cs`) is the complete one-way boundary:
position, velocity, facing, grounded, tag, action timing, movement progress, pins,
surfaces, grip, aim, dt. Nothing else flows in.

So a saved take does **not** persist poses or solver state at all — it persists the
**sample stream** (plus drawable terrain context), and the viewer **re-runs the animator**
over it. That gives us, for free:

1. **No SimSnapshot serialization.** The blocker that deferred disk persistence (Type-keyed
   component stores, journal indexed into the live instance) is simply routed around; the
   sim is never restored offline. The in-game scrubber keeps using SimSnapshot in memory.
2. **Full introspection.** The viewer owns a live animator, so every internal (frozen
   contact weights, pin targets, residuals) is inspectable — not just whatever we thought
   to record.
3. **Offline solver tuning.** Edit `anim_solver_config.json`, press R in the viewer, and
   the same take re-solves under the new weights in ~a second. Deterministic A/B on real
   gameplay.

Scrubbing is order-free the same way the dense-terrain capture made the in-game scrubber
order-free: on load the viewer replays ALL frames once (a "pre-solve pass"), caching each
frame's composed pose + root + a debug snapshot. Scrub = array indexing. Re-solve = redo
the pass.

## Take file format

`Recording/AnimTake.cs` (root source → compiles into Core; System.Text.Json; plain
DTOs — no XNA types in the serialized shape, so the format is host-agnostic):

- `Meta`: `Version`, `SkeletonScale`, `PlayerRadius`, frame count.
- `Frames[]`: one `SampleDto` per rendered frame — every `CharacterAnimSample` field
  (pins/surfaces deep-copied: the live sample's surface array is a shared scratch),
  plus `TerrainIndex` into:
- `TerrainStates[]`: sparse lists of non-empty tiles `{gtx, gty, state, type}` derived
  from the recorder's per-frame `DenseTerrainCapture`s, stored **only when the terrain
  actually changed** (cell-equality against the previously stored state). A typical take
  has a handful of states (one per slash impact), not one per frame.

Files land in `Takes/` at the repo root (gitignored), named `take_<yyyyMMdd_HHmmss>.take.json`.

Primary player only for v1; the frame DTO is a list-ready shape if secondaries earn a use.

## Capture side (Game1 / GameRecorder)

`GameRecorder` already captures per frame (SimSnapshot + dense terrain + poses). Add:

- `CaptureFrame` also stores the frame's `CharacterAnimSample` (rebuilt via
  `CharacterAnimSample.From(sim.Player, dt)` — a pure read of the same state the cosmetic
  pass just consumed) with pins/surfaces arrays cloned.
- **Ctrl+S** (recorder Idle with a take in memory, or during playback) serializes the take.
  The HUD line shows the saved path.

## Viewer side (MTile.Demo)

`--load <path>` in `Program.cs` → new `ViewerGame` (separate class; DemoGame stays the
clip editor). On load:

- Build the rig + clips exactly like the game: `SkeletonExamples.Biped()` +
  `AnimationStore.LoadAll(<repo>/SkeletonStates)`, `CharacterAnimator(skel, take.SkeletonScale, clips)`.
- Pre-solve pass: `Update(sample)` per frame; cache pose (`CloneLocal`), root
  (`AttackGlowSystem.RigRoot` overload taking `(bodyPos, facing)` instead of a
  `PlayerCharacter`), facing, and an `AnimFrameDebug` snapshot (new, in the Diagnostics
  partial): solved Δφ/δ/d.x, |Δθ|max + bone, clip + phase, contacts (bone/target/frozen
  weight/source), pins, surfaces, aim state.
- Draw: terrain tiles as rects (state/type-colored), player body circle (`PlayerRadius`),
  velocity vector, the rig via the same `CharacterAnimator.Draw` the game uses, overlays
  from the cached debug snapshot.
- Transport (mirrors the in-game scrubber): Space/K pause, L/J play fwd/rev, ←/→ step
  (Shift ×10), Home/End, timeline bar with click-to-seek, +/- zoom, camera follows the body.
- Overlay toggles: C contacts (marker size/alpha by weight + weight text), P pins/surfaces,
  D per-frame solver readout (clip, φ, Δφ, δ, d.x, |Δθ|max, state/tag/action).
- **R**: reload `anim_solver_config.json` + rerun the pre-solve pass (offline tuning loop).

## Tests

`MTile.Tests/Animation/AnimTakeTests.cs`:
- Round-trip: build a synthetic take → save → load → identical samples + terrain states.
- Replay determinism: two pre-solve passes over the same take produce bit-identical poses
  (guards the "animator is a pure function of the sample stream" premise this whole
  design rests on — fails if sim-coupled or static mutable state sneaks into the animator).

## Later (not v1)

- Video export: `--export` renders each frame to PNG (reuse the screenshot render-target
  path) + an ffmpeg one-liner; or just screen-record the viewer.
- Sprite-skin drawing in the viewer (`--usebind`), secondary players, entity ghosts.
- Binary container if JSON takes get unwieldy (a 30 s take should be a few MB).
