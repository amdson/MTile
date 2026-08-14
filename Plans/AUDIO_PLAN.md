# Audio Plan — rollback-safe sound

Status: **proposed, nothing implemented.** Verified greenfield 2026-08-14 — no `SoundEffect`
/ `AudioEngine` / `SoundEffectInstance` / `MediaPlayer` reference in any `.cs` file, no
`.wav`/`.ogg`/`.mp3` anywhere in the repo, no `Sounds/`/`Audio/`/`SFX/` folder, no audio
entry in `Content/Content.mgcb`. The only audio-adjacent things that exist are *capability*,
not code: `MTile.Web.csproj:47-48` already references `nkast.Xna.Framework.Audio` /
`.Media`, and `MTile.Web/wwwroot/index.html:48` already loads the KNI WASM audio JS shim.

The hard part of audio here is not mixing, it is rollback: a sound that has already
reached the speaker cannot be un-played. This plan makes that a non-problem by
construction rather than by cleanup.

---

## 1. The core rule

**Audio is render-only, strictly downstream of the sim** — the same rule that already
governs particles, trail, camera and sprites (`CODEBASE_OVERVIEW.md:306`, and the contract
comment at `Game1.cs:622-624`: *"Cosmetic-only pass … reads sim state, never writes"*).
Nothing under `Simulation`, `Net/`, or `Character/` learns that audio exists. The dependency
runs one way.

Concretely: no audio state is snapshotted, no audio state is restored, and a rollback
requires no audio-specific handling at all. That falls out of the two mechanisms below.

## 2. Level-triggered vs edge-triggered

Every sound is one of two kinds, and the test is a single question:

> *If I paused the game, could I tell from sim state alone whether this should be sounding?*

**Yes → level-triggered (predicate-driven).** Wall-slide scrape, peel-tether tension,
mass-ball paint hiss. The sound has a *continuous prerequisite*. Each rendered frame we
re-derive the set of sounds that should be live and reconcile against what is live.
This is idempotent for free: the answer is a pure function of current state, so it is
correct after any rollback, in any order, without memory. No start event exists, so no
start event can be stranded.

**No → edge-triggered (event-driven).** Tile break, impact, landing, swing start. The
justification existed for one instant and is gone. These cannot be re-derived, so they
need external memory to become idempotent — see the dedup table in §4.

**Design bias: prefer level-triggering wherever the state exists to support it.** Once a
sound has a continuous prerequisite, a start edge is *redundant* — "not playing and
predicate true" is a start. Adding the edge buys nothing and introduces a dedup
interaction. Edge-triggering is for sounds that genuinely have no ongoing state to point
at, and that set should stay small.

### 2a. The frame-stamp promotion (a codebase-specific lever)

Several things that *look* edge-triggered are already level-triggered here, because the sim
snapshots a **frame stamp** of when they last happened. Comparing that stamp to the current
frame is a pure function of sim state, so the sound becomes a predicate with a decay
envelope and needs no dedup at all:

| Stamp | Where | Gives you |
|---|---|---|
| `CombatState.LastHitFrame` + `LastHitImpulse` | `Character/CombatState.cs:24` | "was hit within N frames", with magnitude |
| `CombatState.HitstunExpireFrame` / `StunExpireFrame` | `Character/CombatState.cs:17-18` | hitstun/stun as a live window |
| `PlayerCharacter._lastCrushFrame` | `Character/PlayerCharacter.cs:98` (stamped `:398`) | crush impact as a window (private today — would need an accessor) |
| `ActionVars.PeelSnapped` | `Character/ActionVars.cs:67` | tether snap, sim-written and snapshotted |
| `PhysicsBody.LastImpulseMagnitude` | `Physics/PhysicsBody.cs:34`, snapshotted in `Physics/BodyState.cs:30` | landing/impact hardness, zeroed per step at `Physics/PhysicsWorld.cs:157` |

Current frame: prefer `Simulation.Frame` (added — snapshotted, rewinds on `Restore`).
`PlayerCharacter.Frame` (`Character/PlayerCharacter.cs:235`, snapshotted `:639`/`:661`) is
equally sound and is what the stamps in the table above are themselves compared against, so
use it where you already hold the player. See §11.

**Prefer a frame stamp over the dedup table whenever one already exists.** It converts a
one-shot into the structurally safe category for free. Where none exists, adding one to the
sim purely for audio would be a sim change made for a render reason — don't; use the dedup
table instead.

## 3. Sound identity

`SoundId` **must be a pure function of sim state** — `(playerId, Kind.WallScrape)`,
`(tileX, tileY, Kind.Break)`, `(hitId, Kind.Impact)`. Never a counter, never allocation
order, never anything the audio layer carries across frames.

This is the load-bearing invariant. It is the same property that makes
`s.Tag == _tag` work as the animation continuation test in
[`Animation/MoveDriver.cs:115`](../Animation/MoveDriver.cs#L115) — identity of the *result*
is what provides continuity, not anyone remembering a start. If ids are derivable the diff
is correct after any rollback; if they are not, every argument in this document stops
holding. Make it a struct with a cheap hash and be strict.

`HitIdAllocator` is already the right identity source for combat sounds — sim-owned,
deterministic, snapshotted. `Next()` at `World/HitIdAllocator.cs:22` (pre-increment, first
id is 1), snapshot hook `Value` at `:26`, and its header comment (`:6-14`) says outright
that it exists so replays mint identical ids. That is exactly the property `SoundId` needs.

The other stable keys available today:

- **Player**: index into `Simulation`'s players.
- **Tile**: `(gtx, gty)` — integer cell indices, already the tile key everywhere.
- **Hit**: `Hitbox.HitId`.
- **Entity**: `EntityId` (the value identity `CombatSystem`'s dedupe table already keys on,
  `World/CombatSystem.cs:81-84`).

## 4. Architecture

Modelled on the move-driver registry in
[`Animation/CharacterAnimator.cs:166-171`](../Animation/CharacterAnimator.cs#L166-L171) — a
registry of policy objects handed sim state and a scratch buffer cleared and refilled each
frame. The selector loop is `CharacterAnimator.cs:399-402`; the scratch clear + fill is
`:447-448`; the scratch type is `FrameInputs` at `Animation/MoveDriver.cs:63-69`, whose
`Clear()` is three list-clears.

```csharp
public interface ISoundSource
{
    void Collect(in SimAudioView sim, AudioFrame outp);
}

public sealed class AudioFrame          // scratch, cleared + refilled every frame
{
    public void Sustain(SoundId id, SoundClip clip, float gain, float pitch, Vector2 pos);
    public void Fire   (SoundId id, SoundClip clip, int simFrame, float gain, Vector2 pos);
}
```

Differences from the driver registry, both deliberate:

- **Additive, not winner-take-all.** The animator does `break` on first match
  (`CharacterAnimator.cs:401`) because exactly one clip can play. Audio does not: no
  `break`, every source contributes. Voice caps and coalescing therefore belong in the
  mixer, not in sources.
- **Sources must hold no mutable state between frames.** The moment one caches "was I
  playing last frame" it has reintroduced an edge and the rollback-safety argument no
  longer applies. `Collect` is a pure read of sim state into the scratch buffer.

`SimAudioView` is a **read-only façade** over the sim, not the sim itself — it keeps the
dependency honest and gives derived queries (`AnyPlayerWallSliding`, `TetherTension`) a
home that is not sim code. Give it the current frame (`Player.Frame`) so §2a predicates can
evaluate their windows.

Preallocate like `FrameInputs` does: fixed lists reused across frames, no per-frame
allocation. Audio runs every rendered frame at 60 fps on a WASM target where the AOT budget
is already tight (~40 fps, `Plans/WEB_PVP.md`).

### Reconciliation, once per *rendered* frame

**Not once per `Step`.** A rollback executes many `Step` calls inside one rendered frame —
the resim loop is `for (int f = rollbackTo; f < _frame; f++)` at
[`Net/RollbackSession.cs:108`](../Net/RollbackSession.cs#L108) with `_sim.Step(...)` at
`:111` and no render in between. Running the diff per step would sample intermediate states
that were never real. Run `Collect` once, after the session has finished stepping, against
final state.

That is also why rollback needs no special case: the registry lives outside the sim, and
we simply reconcile against wherever the sim ended up.

- `Sustain` entries diff against live voices → start / retarget / fade-retire.
- `Fire` entries pass through a `(simFrame, SoundId)` dedup table sized to **~2× the
  rollback window** — `RollbackSession.BufferLen = 60` (`Net/RollbackSession.cs:29`), so
  ~120 entries — then launch.

**Dedup subsumes replay suppression.** A replayed frame re-emits with the same
`(simFrame, id)` and is dropped. This is preferred over exposing an `IsResimulating`
flag from `RollbackSession`: one mechanism instead of two, no new `Net/` surface, and it
correctly handles the same frame being rolled back *repeatedly* — which a bare flag only
handles by accident. **Do not add an `IsResimulating` flag.**

## 5. Mixer rules

- **Retarget, don't restart.** Matching id updates gain/pitch/pan on the existing voice
  (`SoundEffectInstance.Volume` / `.Pitch` / `.Pan`, all present on both backends — §7).
  Getting this wrong gives machine-gun retriggering. This is the "extend the existing
  clip" behaviour, exactly as in clip selection.
- **Fade out on departure** (~50–80 ms), never a hard stop. Cheap, and it absorbs
  single-frame predicate flicker. Neither backend has a fade primitive — implement it as a
  per-voice gain ramp in the mixer's own per-frame tick.
- **Per-key voice cap + per-frame coalesce.** Burst and peel can break many tiles in one
  frame; "40 tiles broke" must be one louder sound, not 40 voices. In practice this is
  the first real problem to hit, ahead of anything rollback-related — see §9's note on
  `TileMassField.Deposit` returning a committed-tile *count*, and on eruption/burst
  clearing whole neighbourhoods through `ChunkMap.BreakCell`.

Coalescing policy: group a frame's `Fire` entries by `SoundKind`, cap at N voices per kind,
and fold the remainder into gain (sublinear, e.g. `gain·√n`) and a position centroid.

## 6. Accepted tradeoff

Exactly one, and it should be stated plainly because everything else here is structural:

> **A mispredicted remote event may fire a short one-shot for something that did not
> quite happen.**

We play optimistically — on first simulation of a frame, not on confirmation — because a
~50 ms delay on your own attack is far more objectionable than a rare spurious sound.
Misprediction affects remote input, so the typical error is a sound landing 1–3 frames
early, which is inaudible as an error rather than wrong.

Not on the tradeoff list: stranded loops (level-triggering makes them impossible), and
double-fired one-shots (dedup).

Known residual, mild: a **dropped loop**. If a design keeps a start edge, the edge can be
deduped on a frame sampled mid-rollback-window while the corrected timeline had the loop
continuous, and nothing can restart it. Silence rather than noise, and it self-heals on
re-entry. Note this failure **only exists for designs that keep a start edge** — a pure
level-triggered source re-derives "should be live" from scratch every frame and cannot hit
it. Another reason for the §2 bias.

## 7. Platform: what audio API is actually available

Both hosts compile the **same root `.cs` files** — `MTile.Web.csproj:29` re-globs
`..\**\*.cs` rather than referencing `MTile.Core` — so an API present in only one backend
breaks the web build. Checked directly against the assemblies on disk rather than from
memory.

**Desktop**: `MonoGame.Framework.DesktopGL`, namespace `Microsoft.Xna.Framework.Audio`,
full XNA audio surface.

**Web**: `nkast.Xna.Framework.Audio` **4.1.9001** (`MTile.Web.csproj:47`), shipping
`Xna.Framework.Audio.dll`. Its public types live in the **same** `Microsoft.Xna.Framework.Audio`
namespace, so source is portable. Members enumerated from the package's own XML doc at
`~/.nuget/packages/nkast.xna.framework.audio/4.1.9001/lib/net8.0/Xna.Framework.Audio.xml`:

| Type | Members present in KNI |
|---|---|
| `SoundEffect` | `FromStream(Stream)`, `CreateInstance()`, `Play()`, `Play(float,float,float)`, `.ctor(byte[], int, AudioChannels)`, `.ctor(byte[],int,int,int,AudioChannels,int,int)`, `Duration`, `Name`, `IsDisposed`, static `MasterVolume`, `DistanceScale`, `DopplerScale`, `SpeedOfSound` |
| `SoundEffectInstance` | `Play/Pause/Resume/Stop/Stop(bool)/Reset`, `Volume`, `Pitch`, `Pan`, `IsLooped`, `State`, `Apply3D(AudioListener, AudioEmitter)` and the array overload |
| `AudioListener`, `AudioEmitter`, `AudioChannels` | present |
| `DynamicSoundEffectInstance` | present — `SubmitBuffer`, `PendingBufferCount`, `GetSampleDuration`, `GetSampleSizeInBytes`, `IsLooped` |

**Compile-time parity is therefore complete for everything this plan needs** (create
instance, loop, set volume/pitch/pan, stop). No API in §4/§5 is desktop-only.

### 7a. Verified against the compiled assemblies

A second pass reflection-loaded and metadata-scanned the actual DLLs in the NuGet cache
rather than reading XML docs. Three findings that change decisions:

**The BlazorGL backend is a real implementation, not stubs.** `Kni.Platform.dll`
(`~/.nuget/packages/nkast.kni.platform.blazor.gl/4.1.9001/lib/net8.0/`) contains
`ConcreteAudioService`, `ConcreteAudioFactory`, `ConcreteSoundEffect`,
`ConcreteSoundEffectInstance`, `ConcreteDynamicSoundEffectInstance`,
`ConcreteMediaPlayerStrategy` — backed by the WebAudio names `AudioContext`, `AudioBuffer`,
`AudioBufferSourceNode`, `PannerNode`, `GainNode`. That substantially de-risks §7 risk 1:
`GainNode` is the natural implementation of `Volume` and `PannerNode` of `Pan`, so both are
very likely real. Also, `nkast.Kni.Platform.Blazor.GL`'s nuspec **hard-depends** on
`nkast.Xna.Framework.Audio` + `nkast.Wasm.Audio` — audio is not optional on BlazorGL, and
`MTile.Web/bin/` already ships `Xna.Framework.Audio.dll`, `Xna.Framework.Media.dll`, and
`nkast.Wasm.Audio.dll` today. Risk 1 stays open pending a browser test, but the expected
outcome moved from "unknown" to "probably fine."

**⚠ One concrete API delta: `SoundEffect.FromFile` exists on MonoGame 3.8.4.1 but is ABSENT
from KNI 4.1.9001.** Zero occurrences in the net8.0 dll; `GetMethods()` on the net40 lib
confirms no such method. **Use `SoundEffect.FromStream` in all shared code.** This is exactly
the failure mode `CLAUDE.md` warns about — it compiles clean via `MTile.Core` and breaks only
the web build. Same shape of hazard on the media side: `Song.FromUri` exists on both, but
`Song.FromFile` / `MediaLibrary` are browser-hostile (another reason for §7 risk 3).

**There is no opt-out seam for audio code.** `MTile.Web.csproj:29-30` excludes by
*project directory* only — nothing filename- or feature-based — so any new audio `.cs` at
the repo root is automatically compiled into the web build. You cannot develop desktop-only
audio and exclude it later without adding a new exclusion pattern. Plan for both backends
from the first file.

Honest limits on that pass: Windows PowerShell 5.1 cannot reflection-load net8.0 assemblies,
so KNI member *signatures* come from the net40 lib in the same package (cross-checked against
net8.0 by metadata string scan — all names match), and the **MonoGame side is verified at
type/member-name level only**, not full parameter lists.

**Risks — real, and none of them compile errors:**

1. **Runtime parity is NOT verified.** The types exist in KNI's metadata and §7a shows the
   BlazorGL backend really implements them over WebAudio; what remains unverified is whether
   `Pitch`, `Pan`, and `Apply3D` behave *faithfully*. **Open.** Mitigation: keep the mixer's
   platform surface to a narrow internal interface (`Play/Stop/SetGain/SetPitch/SetPan`), so
   a KNI gap degrades to a no-op in one place instead of scattering `#if`. Verify with a
   browser smoke test (§12 Phase 1) before any sound design depends on pitch.
2. **Browsers require a user gesture to unlock audio** — already documented as a known
   desktop/browser divergence at `Plans/Archive/BROWSER_PORT_PLAN.md:22`, with
   "click-to-start screen (audio unlock…)" listed at `:97` and never built. `index.html:21`
   has a loading div but no click-to-start. Until one exists, browser audio will be silent
   or throw on first play. **This is a prerequisite for web audio, not a detail.**
3. **Do not use `Song`/`MediaPlayer`** for anything reactive. `nkast.Xna.Framework.Media` is
   referenced (`MTile.Web.csproj:48`) but the media path is the most divergent part of both
   frameworks. `SoundEffect`/`SoundEffectInstance` only.
4. **Don't touch `Apply3D`.** This is a 2D game with a known camera; compute pan and
   distance-gain in the mixer from world position vs `Camera`. That also sidesteps risk 1's
   worst case and keeps the falloff tunable.

## 8. Content and assets

`Content/Content.mgcb` has **no `/reference:` lines** (`:11-13`) and three entries —
`DebugFont.spritefont`, `CapsuleSplat.fx`, `MetaballComposite.fx`. `SoundEffectProcessor` is
built in, so audio needs no new reference. An entry looks like:

```
#begin Sounds/tile_break.wav
/importer:WavImporter
/processor:SoundEffectProcessor
/processorParam:Quality=Best
/build:Sounds/tile_break.wav
```

Then `Content.Load<SoundEffect>("Sounds/tile_break")`.

The same `.mgcb` is built by **two different toolchains** and any entry must survive both:
`MonoGame.Content.Builder.Task` 3.8.4.1 on desktop (`MTile.Desktop.csproj:73`) and
`nkast.Xna.Framework.Content.Pipeline.Builder` 4.1.9001 on web (`MTile.Web.csproj:55`, via
`KniContentReference` at `:132-138`, output landing in `wwwroot/Content/`).

Three web-specific constraints:

- **`/compress:False` (`Content.mgcb:9`).** Pipeline-built `.xnb` audio ships as *uncompressed
  PCM*. A modest bank becomes multi-MB fast. Either accept it, flip compression, or ship
  raw files outside the pipeline the way the art already does — `Game1.cs:401-413` and
  `:453-454` load PNGs via `Texture2D.FromStream` explicitly so they work "without an
  `.mgcb` rebuild"; `SoundEffect.FromStream` is the exact analogue and exists on both
  backends (§7). **Open question:** which route. Pipeline is simpler; raw `.ogg`/`.wav`
  keeps wwwroot small and matches the existing asset habit.
- **WASM cannot enumerate directories.** The clip loader already works around this with an
  HTTP-fetched `index.json` manifest on browser vs a disk scan on desktop
  (`Game1.cs:432-446`). A sound bank must do the same, or be a hardcoded name list. A
  directory-scan sound registry will not work on Web.
- **Raw assets need an explicit copy rule.** `MTile.Web.csproj:67-99`
  (`StageGameAssetsToWwwroot`) copies per-folder/per-extension with no generic glob — line
  92 restricts to `.png` specifically to keep `paper.pdf` out of the payload. Raw audio
  needs a new `<Copy>` there. **This target does not exist today** — it is net-new work on
  the raw-asset route, and its absence is easy to miss because desktop will work fine
  without it.
- **The pipeline route has an ffmpeg dependency.** KNI's MGCB *can* build audio for
  BlazorGL — `nkast.xna.framework.content.pipeline.builder/4.1.9001/tools/` ships
  `Xna.Framework.Content.Pipeline.Audio.dll`, `.Media.dll`, plus `ffmpeg.exe`/`ffprobe.exe`.
  Those are **Windows binaries**, and the non-Windows path routes MGCB through
  `kni-mgcb.sh` (`MTile.Web.csproj:20`), which would need ffmpeg available separately. Fine
  today (you build on Windows); a live risk if the web build ever moves to Linux CI. Worth
  weighing before committing to pipeline-built audio.

**Size budget is not the binding constraint.** The published wwwroot is ~59 MB on disk:
`_framework/` 37 MB (`dotnet.native.wasm` alone is 15.7 MB, the price of mandatory AOT) and
`Assets/` 21 MB of PNGs. `Content/` is 36 KB. `scripts/publish-web.ps1:37-42` mirrors with
`robocopy /MIR /XF *.br *.gz`, shedding ~10 MB of precompressed duplicates. A few hundred KB
of compressed SFX is noise against that — but a few MB of uncompressed PCM `.xnb` is not
noise, which is what makes the format decision above worth making deliberately.

## 9. Candidate sounds, grounded

> The **acquisition** side of this table — variant counts, durations, sonic character,
> search terms, sourcing and format — lives in
> [`AUDIO_ASSET_LIST.md`](AUDIO_ASSET_LIST.md). This section is the *code* side: which sim
> state drives each sound and where it lives.

Predicate = pure read of snapshotted sim state, reconciled per rendered frame. Event = fires
inside `Step` (therefore refires on every resim at `Net/RollbackSession.cs:111`) and must be
ledgered or deduped, never played at the callsite.

Movement states are best keyed on `MovementState.AnimationTag` (`Character/Movement.cs:88`)
rather than the concrete type — the tag is the existing stable discriminator, and it is what
`TagClipDriver.Matches` already uses.

| Sound | Kind | Sim state / event | file:line |
|---|---|---|---|
| Wall-slide scrape (loop, gain·pitch from slide speed) | predicate | `CurrentState.AnimationTag == AnimTag.WallSlide`; speed `Body.Velocity.Y`; fast-slide branch on `Input.Down` | `Character/WallStates.cs:10,12,112-116`; `PlayerCharacter.cs:614` |
| Wall jump | predicate (tag window) | `AnimTag.WallJump` | `Character/WallStates.cs:130,142` |
| Ledge grab / pull / jump | predicate | `AnimTag.LedgeGrab` / `LedgePull` (+ `AnimationProgress`) / `LedgeJump` | `Character/LedgeStates.cs:13,15,223,225,409,418`; progress `Character/Movement.cs:95` |
| Climb effort (parkour / mantle / arc-jump) | predicate | `ClimbManeuverBase.AnimationProgress` drives a scrape/grunt envelope | `Character/ClimbStates.cs:54,301-334` |
| Crouch / dropdown / double jump / stun / tumble | predicate | corresponding `AnimTag` | `Character/LocomotionStates.cs:149,151,199,205`; `JumpStates.cs:262,267`; `ReactionStates.cs:26,33,79,95` |
| Landing thud (gain from impact) | predicate (edge already exists, render-side) | `IsGrounded` compared to last **rendered** frame; hardness `Body.LastImpulseMagnitude` | `Drawing/CosmeticUpdateSystem.cs:115-120`; `PlayerCharacter.cs:612`; `Physics/PhysicsBody.cs:34` |
| Footsteps | **render-derived, see caveat** | `PoseState.Phase` + authored contact labels | `Animation/PoseState.cs:24`; `CharacterAnimator.cs:551,554-556`; `Animation/ContactLabel.cs` |
| Mass-ball paint hiss (loop) | predicate | `paid` — the *funded* deposition rate from `Meters.SpendForTiles`; demand rate at `:1621` | `Character/ActionStates.cs:1621-1623`; `Character/BuildMeters.cs:141` |
| Charge whine (rising, phase-coloured) | predicate | `Meters.ChargeFraction`, `Meters.Phase` (`Ramping/Peak/Overheld`), `ChargingRequested` | `Character/BuildMeters.cs:72,73,75,187` |
| Build reservoir empty / starved | predicate | `Meters.Build`, `CanAfford` | `Character/BuildMeters.cs:62,157` |
| Peel tether tension (loop) | predicate | `ActionVars.PeelStrain` — a 0..1 spring load, explicitly "sim-written, read by Draw" | `Character/ActionVars.cs:66`; written `Character/ActionStates.cs:2580-2602` |
| Peel snap | predicate (snapshotted flag) | `ActionVars.PeelSnapped` | `Character/ActionVars.cs:67` |
| Sprout growth hum | predicate | count of `Chunks.Graph.Growing` / `Pending` | `World/TileSproutGraph.cs:27-28` |
| Mass-ball whoosh (loop, per ball) | predicate | entity of `EntityKind.MassBall` in `sim.Entities`; `Body.Velocity`. Single-state — always "flying and leaking" | `Entities/MassBall.cs:22,39,67-83` |
| Mass-ball extinguish | event | `_mass <= DoneMass` → `Health = 0` | `Entities/MassBall.cs:69` |
| Escalation tension layer | predicate | `CombatState.DamagePercent` (monotonic) | `Character/CombatState.cs:32` |
| Hitstun / stun / guard / grabbed | predicate | `HitstunActive`, `StunActive`, `GuardActive`/`GuardCharged`, `GrabbedActive`, `GrabStrength` | `Character/CombatState.cs:17,18,58,78,113-114` |
| Hit connect (impact) | event, id = `HitId` | `CombatSystem.Apply`, entity path; `PeekHits(hitId) > 0` is the per-frame confirm | `World/CombatSystem.cs:57,50,151-172`; `Simulation.cs:291` |
| Attack whiff / swing start | predicate | `ActionState.AnimationProgress(in ActionVars)` = `TimeInState / Duration` | `Character/ActionStates.cs:58,283,758,…` |
| Crush damage | predicate (frame stamp) | `_lastCrushFrame` changing; gate `LastImpulseMagnitude > CrushImpulseThreshold` | `Character/PlayerCharacter.cs:388-398,98` |
| Tile break | **event**, id = `(gtx,gty)` | `ChunkMap.OnTileBroken(Vector2 pos, TileType)` — the only delegate on `ChunkMap` | decl `World/ChunkMap.cs:80`, fire `:471` in `BreakCell` `:454` |
| Tile place / commit | event, count-valued | `TileMassField.Deposit` **returns committed-tile count**; promotion to Solid has no event | `World/TileMassField.cs:65`; `World/ChunkMap.cs:322` |
| Eruption fire | event | `BlockPaintAction` eruption branch | `Character/ActionStates.cs:1596-1599` |
| Respawn | event | `Simulation.OnPlayerRespawn` | decl `Simulation.cs:126`, fire `:310` |

**Footstep caveat, worth stating explicitly.** The only normalized stride phase in the
codebase is `PoseState.Phase` (`Animation/PoseState.cs:24`), which lives in
`CharacterAnimator` — **render-only, ticked from `CosmeticUpdateSystem.cs:89`, never
snapshotted.** Footsteps driven off it are *not* derived from sim state, and that is fine
precisely because audio reconciles once per rendered frame: the animator is already one
frame's worth of settled render state, and it never rolls back because it never rewinds. But
it must be understood as a *third* category — render-derived — and kept out of the
`SimAudioView` façade so the boundary stays legible. A sim-only alternative exists if
wanted: `|Body.Velocity.X|` + `IsGrounded` against `MovementConfig.Current.MaxWalkSpeed`.

**Note what is missing:** there is **no tile-placement event** (only `TryRequestTile` →
`TileState.Sprouting` → promotion, `World/ChunkMap.cs:221,235,322`), and `MassBall` has **no
state machine at all** — no impact event, since `Body.IgnoreTiles = true` and
`GravityScale = 0f` (`Entities/MassBall.cs:58,60`). Both are cases where a predicate is the
only option, not a preference.

## 10. Where the code goes

**The reconcile call site already exists and is already documented as exactly this seam.**
`CosmeticUpdateSystem.Update` (`Drawing/CosmeticUpdateSystem.cs:55`) is the once-per-rendered-
frame, after-the-sim-has-stepped, reads-but-never-writes pass — its header comment (`:6-10`)
enumerates cursor ribbon, sprite sync, animators, trail, particles, landing puff, camera.
Audio is the next item on that list.

Put the reconcile **at the end of `Update`, after `_particles.Update(dt)` (`:122`) and beside
`_camera.TrackTarget(...)` (`:124`)** — after the animators (`:89`) so `PoseState.Phase` is
current for footsteps, and after the camera has the frame's target so pan is computed
against the right listener.

Doing it inside `CosmeticUpdateSystem` rather than in `Game1` means it is written **once** and
covers both drive paths automatically:

- networked: `_session.TryStep()` at `Game1.cs:584`, then `_cosmetics.Update(...)` at `:585-586`;
- offline: the `while (_simAccum >= 1f)` fixed-step loop at `Game1.cs:611-618`, then
  `_cosmetics.Update(...)` at `:626`.

Both already call cosmetics exactly once per rendered frame regardless of how many `Step`s
ran. That is the property the whole design rests on, and it is free.

### If a per-step event list is needed

For the genuinely event-driven set (§9), the precedent to copy is **`CorrectorLedger`**
(`Character/CorrectorLedger.cs`) — per-step derived data that is deliberately never
snapshotted. Its contract comment (`:39-42`) is the exact contract an `AudioLedger` wants:

> *"Lifecycle: cleared in `CorrectorScratch.BeginFrame`, written by the apply site right
> after its final solve. Pure per-frame derived data — never snapshot state; consumers must
> read it in the same step (or from render, which may not feed back into the sim)."*

Its shape, all worth mirroring:

- **Fixed-capacity arrays + a count**, never `List<>` — `Channels`/`ChannelCount` `:60-61`,
  `Contacts`/`ContactCount` `:63-64`. Zero allocation on the sim hot path.
- **`Clear()` (`:70-75`) resets counts only**, not contents; entries past the count are
  simply unreadable.
- Owned by a per-player scratch: `CorrectorScratch.Ledger` (`Character/CorrectorScratch.cs:88`),
  cleared in `BeginFrame()` (`:95`), which is called once at the top of each player's step
  from `Character/PlayerCharacter.cs:436`.
- Exposed to render through a read-only property: `PlayerCharacter.ForceLedger`
  (`Character/PlayerCharacter.cs:210`).
- **Absent from `CaptureState`/`RestoreState`** — the field lists are
  `Character/PlayerCharacter.cs:638-647` and `:660-671`; `_correctorScratch` appears in
  neither, and `Ledger` has zero hits in `SimSnapshot.cs`/`Simulation.cs`/`World/`.

Under rollback a resim simply overwrites the ledger; only the last resim's contents survive
to the render read — which is precisely the behaviour audio wants, and is why this is a
cleaner alternative to the `(simFrame, SoundId)` dedup table for events the sim can stamp
in-place. **Both mechanisms are valid; the dedup table is the fallback for events that arrive
through a callback the sim doesn't own** (`ChunkMap.OnTileBroken`).

### The existing defect this shares a seam with

`Game1.cs:311-317` subscribes cosmetic particle spawns directly to sim events:

```csharp
        // Cosmetic feedback hooks. The sim raises these during Step; Game1 turns
        // them into particles. ChunkMap tints the tile-break burst by material.
        _sim.OnPlayerRespawn += pos => Effects.Puff(_particles, pos, Color.LimeGreen);
        _sim.Chunks.OnTileBroken += (pos, type) =>
            Effects.TileBreak(_particles, pos, TilePalette.BaseColor(type));
```

Both fire inside `Step`, so both refire on every rolled-back frame at
`Net/RollbackSession.cs:111` — spraying duplicate particles. Tracked as `BACKLOG.md:5.14`.
It is the identical defect class, cosmetic-only and therefore unnoticed.

Contrast with the landing puff at `Drawing/CosmeticUpdateSystem.cs:115-120`, which detects
the air→ground edge by comparing to **last rendered frame** and is already correct. The repo
thus contains one example of each pattern. The right scope is a **shared presentation-events
seam** that particles and audio both use — not an audio-only mechanism — and converting the
particle hooks onto it both fixes the bug and proves the seam before audio depends on it.

## 11. Open questions and prerequisites

Marked honestly; these are not settled.

- ~~**Sim frame counter.**~~ **RESOLVED — `Simulation.Frame` added.** `PlayerCharacter.Frame`
  would have worked (it is snapshotted and rewinds correctly), but it is per-player, and a
  global presentation key should not depend on which player exists. `Simulation._frame` is
  incremented in `Step` alongside `_elapsed` and carried in `SimSnapshot.Frame`, so a
  rolled-back frame is re-stepped under its old number — which is the whole dedup premise.
- **KNI runtime audio fidelity** (§7 risk 1) — types exist, behaviour unverified. Needs a
  browser test, not a code read.
- **Browser audio unlock** (§7 risk 2) — no click-to-start screen exists. Prerequisite for
  any web audio at all.
- **Pipeline vs raw assets** (§8) — undecided. Affected by `/compress:False` and the WASM
  directory-enumeration constraint.
- ~~**Presentation-events seam scope**~~ **RESOLVED — shared, and built first.**
  `Presentation/PresentationEvents.cs` (`PresentationId` / `PresentationEvent` /
  `PresentationEventLog`), with the particle hooks moved onto it and drained by
  `Game1.PresentThisFrame()` once per rendered frame. Closes the duplicate-particles bug
  (`BACKLOG.md` 5.14) and means the edge-triggered half of the seam already exists when
  audio arrives — `Phase 0`'s tile-break sound is a second consumer of the same log, not a
  parallel mechanism. Covered by `MTile.Tests/Sim/PresentationEventLogTests.cs`.
- **Mixing/ducking policy** — entirely unaddressed. Not a blocker for the vertical slice.

## 12. Phases

The bias throughout: **a thin vertical slice beats a complete registry with nothing plugged
into it.** One predicate sound and one event sound end to end will surface the real problems
(coalescing, unlock, KNI behaviour) faster than any amount of infrastructure.

**Phase 0 — the slice.** One `SoundEffect` loaded, one hardcoded predicate sound (wall
scrape: `AnimTag.WallSlide`, gain from `Body.Velocity.Y`) and one hardcoded event sound (tile
break, keyed `(gtx,gty)`), reconciled from `CosmeticUpdateSystem.Update` at `:124`. No
registry, no façade, no `ISoundSource` — a single class with a dictionary of live voices, a
dedup ring, and retarget-don't-restart. Ship it on desktop first. This is the whole design
proven in ~200 lines, and it is where the retarget/fade semantics get tuned by ear.

**Phase 1 — web parity.** Build `MTile.Web`, add the click-to-start unlock, and verify in a
real browser that `Volume`/`Pitch`/`Pan` do something. Resolve §7 risk 1 and §8's format
question with measurements. Extend `MTile.Web/smoke/web_smoke.py` to assert no audio console
errors. **Do this before generalizing** — a KNI runtime gap discovered here changes the mixer
interface, and it is far cheaper to learn now.

**Phase 2 — generalize.** Extract `SimAudioView`, `ISoundSource`, `AudioFrame`, and the
mixer, with Phase 0's two sounds as the first two sources. Validate with a debug-overlay list
of live voices (id, gain, age) — the existing debug HUD is the place. Add the sim frame
counter decision from §11.

**Phase 3 — coalescing and caps.** Per-kind voice caps + per-frame fold, exercised
specifically against burst/peel and eruption, which is where "40 tiles broke" happens.
Expect this to be the first thing that actually sounds bad.

~~**Phase 4 — the presentation-events seam.**~~ **DONE, and pulled ahead of Phase 0** — the
particle hooks now go through `PresentationEventLog`, which fixed `BACKLOG.md` 5.14 and
built 7.3's mechanism on a cosmetic consumer where a mistake is invisible rather than
audible. Phase 0's tile-break sound plugs into `Game1.PresentThisFrame()`.

**Phase 5 — content.** The rest of §9's table, the sound bank + manifest, real mixing, and
a bus/ducking policy.

Tests: audio is render-only, so the sim suite is unaffected by construction — which is itself
the thing to assert. A cheap regression is a headless test that drives `SimRunner` through a
scripted rollback and checks that a fake mixer sees each event id exactly once. That test is
worth writing at Phase 2, not before.
