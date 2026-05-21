# Goal 7 — GGPO-style Rollback Netcode

The capstone of [ROLLBACK_ROADMAP.md](ROLLBACK_ROADMAP.md). Goals 1–6 made the
simulation deterministic and snapshot/restore-able (`Simulation.Step(input)`,
`Snapshot()`, `Restore()` — see [STATE_SNAPSHOT_PLAN.md](STATE_SNAPSHOT_PLAN.md)).
This goal layers the *online* loop on top: predict the remote player's input, run
locally with zero perceived lag, and roll back + re-simulate the moment a prediction
turns out wrong.

Two-player, peer-to-peer, **same build on both ends** (the float-determinism premise
we've held since goal 0). Browser-first (the KNI/Blazor WASM build), since that's
where the WebRTC transport lives.

---

## Reference code we're porting

The user supplied a working JS prototype in two files. This plan mirrors their design
decisions and corrects a couple of bugs in the rollback path.

- **`rtc_connection_reference.js`** — signaling + connection. Firebase Firestore
  carries the SDP offer/answer + ICE candidates; an `RTCPeerConnection` + a single
  `DataChannel` form the P2P link. `run_game(player_ind, pc_channel)` hands the open
  channel to the game loop. (Discussed in chat; the C# bridge is in §C below.)
- **`rtc_netcode_reference.js`** — the rollback loop itself. The parts we port:
  | JS construct | Role |
  |---|---|
  | `gamestate_buffer[60]` | ring of per-frame game states → our `SimSnapshot` ring |
  | `local_input_buffer` / `remote_input_buffer` (`[frame, input, imputed]`) | per-frame input rings, tagged predicted/confirmed |
  | `input_frame_delay = 3` | local input applied 3 frames late (latency hidden rollback-free) |
  | `remote_input_stack` + `recieve_input` | inbox for datachannel packets, drained each tick |
  | `handle_input_stack()` | reconcile arrivals; flag rollback when a real input ≠ the imputed one |
  | repeat-last + `imputed=true` | prediction of a missing remote input |
  | `curr_frame >= highest_remote_frame + delay + 3 → continue` | stall cap: bound how far we predict ahead |
  | `datachannel == null → evil_AI(gamestate)` | offline/bot path filling the remote input |
  | `update(gamestate, [p0,p1])` | one fixed step over BOTH players' inputs → our `Simulation.Step(p0, p1)` |

  **Bugs in the reference we will NOT reproduce** (line refs from the prototype):
  1. The rollback loop seeds `rollback_gamestate = gamestate` (the live/current state)
     instead of the buffered state at `rollback_frame`, and never writes the result
     back into `gamestate` / `gamestate_buffer`. As written it re-simulates into a
     throwaway local. Our version restores from the snapshot ring and writes forward.
  2. `handle_input_stack()` is declared to return `[rollback_frame, rollback_flag]`
     but the caller destructures three values (`highest_remote_frame` shadowed). We
     return an explicit struct.

---

## A. Where it plugs into MTile

`Index.razor.cs` already runs the loop: a JS `requestAnimationFrame` → `[JSInvokable]
TickDotNet()` → `_game.Tick()`. Today `Game1` gathers hardware input and calls
`Simulation.Step(input)` for the local player only. The netcode inserts a
**`RollbackSession`** between input-gather and `Step`:

```
TickDotNet → gather local PlayerInput → session.Tick(localInput) → render
                                              │
                          ┌───────────────────┴────────────────────┐
                          │  drain remote inbox → detect rollback   │
                          │  → (rollback? Restore+replay)           │
                          │  → Step(p0,p1) with delayed inputs      │
                          │  → snapshot, buffer, send local input   │
                          └─────────────────────────────────────────┘
```

The session owns the `Simulation`; `Game1`'s render path reads `sim.Player`,
`sim.Entities`, etc. exactly as it does now. Solo play keeps calling `Step(input)`
directly (or runs the session with the bot filling P2 — see §G).

---

## B. New components (all in a `Net/` folder)

| Type | Responsibility |
|---|---|
| `RtcConnection` | JS-interop wrapper over `RTCPeerConnection`/`DataChannel` (§C). Events: `Opened(int localPlayer)`, `Message(byte[])`. Method: `Send(byte[])`. |
| `NetInput` (struct) | The wire form of `PlayerInput` — packed bitflags + mouse **world** coords (§E). |
| `InputCodec` | `NetInput ↔ PlayerInput`, and packet `Encode/Decode` (`[frame, NetInput]`, optionally a redundancy window + checksum). |
| `InputRing` | Fixed `BufferLen` ring of `(frame, NetInput, bool imputed)` per player; `Set/Get/WasImputed/Window`. |
| `SnapshotRing` | Fixed ring of `SimSnapshot` keyed by frame (mod `BufferLen`). |
| `RollbackSession` | The loop: prediction, rollback detection, restore+replay, stall cap, send. Owns `Simulation`, the rings, and the inbox stack. |

---

## C. Transport bridge (from the chat sketch)

Browser WebRTC is JS-only, so `rtc_connection_reference.js` stays (refactored to a
`window.mtileRtc` surface) and C# interops to it. Channel config changes from the
reference's default to **unreliable + unordered** (`{ ordered:false, maxRetransmits:0 }`)
so a dropped packet never head-of-line-blocks the newest input; we offset loss by
sending a small **window** of recent inputs per packet (§F). `RtcConnection` is a thin
`IJSRuntime` wrapper + `DotNetObjectReference` for the `[JSInvokable]` `OnRtcOpen(int)`
/ `OnRtcMessage(byte[])` callbacks. (Full sketch in chat; ~40 lines each side.)

> Baseline alternative: the reference uses a *reliable, ordered* channel + single-frame
> sends and lets the receive stack handle ordering. That also works and is simpler;
> the unreliable+window choice is the optimization. Ship reliable-ordered first if it
> de-risks the bring-up, then switch.

---

## D. Per-tick algorithm (`RollbackSession.Tick(PlayerInput local)`)

Fixed `FixedDt` accumulator drives N sub-steps per render frame, exactly like the
reference's `while (delta > timestep)`. Each sim sub-step:

```
1. Drain inbox → reconcile (ReconcileRemote): for each received (frame, input):
     • frame > current      → leave on stack (remote is ahead; no slot yet)
     • frame in [current-BufferLen+1, current]:
         stored = remoteRing.Get(frame)
         if stored.Imputed && stored.Input != received → rollbackTo = min(rollbackTo, frame)
         remoteRing.Set(frame, received, imputed:false)        // confirm it
     • else (too old) → drop
   → returns earliest mispredicted frame, or none.

2. If a rollback frame F was found:
     sim.Restore(snapshots[F])
     for f in F .. current-1:
         snapshots[f] = sim.Snapshot()
         sim.Step( inputFor(0,f), inputFor(1,f) )              // confirmed-or-predicted
     // sim is now back at 'current' with corrected history.

3. Stall cap (don't outrun the peer): 
     if current >= highestRemoteFrame + InputFrameDelay + StallSlack → return (skip this step)

4. Predict remote for the frame about to run:
     if !remoteRing.HasConfirmed(current):
         remoteRing.Set(current, remoteRing.Get(current-1).Input, imputed:true)   // repeat-last

5. Advance one step with the DELAYED inputs:
     snapshots[current] = sim.Snapshot()
     sim.Step( inputFor(0,current), inputFor(1,current) )
     current++

6. Sample + buffer + send THIS tick's local input, scheduled for current+InputFrameDelay:
     localRing.Set(current-1+InputFrameDelay, NetInput.From(local))
     net.Send( InputCodec.Encode(localPlayer, current-1+InputFrameDelay,
                                 localRing.Window(.., RedundancyWindow), Checksum()) )
```

`inputFor(player, f)` = `localRing/remoteRing.Get(f)` decoded to `PlayerInput`, with
the local player's own ring always authoritative. **Input delay** (step 6) means the
input you sample at wall-frame W isn't consumed until sim-frame W+`InputFrameDelay`,
so up to `InputFrameDelay` frames of latency are absorbed with *no* rollback at all —
rollback only kicks in beyond that. This is the reference's `trailing_buffer_ind`
trick, framed as "schedule local input `delay` frames into the future" (cleaner, same
result).

---

## E. Input serialization & the world-coordinates rule

**Design rule (settled): the sim update loop deals only in world coordinates; the
camera is never read inside `Step`.** Screen↔world is a render concern. `Game1`
converts the hardware cursor to world space *before* handing input to the sim, and the
camera is used only for rendering and animation generation — never inside any
`Update`/`Step` path. This keeps the camera fully render-side (goal-4 §I) and means
nothing in the sim depends on viewport/camera state.

`PlayerInput` already encodes this: the sim consumes **`MouseWorldPosition`** (a world
`Vector2`) and that's the field actions/build/aim read. The raw screen `MousePosition`
(`Point`) is render/debug-only — the only reader is `Game1.Draw` (the cursor dot);
**no sim code reads it** (verified). So:

- **Wire format:** pack the ~10 booleans into one `ushort`; send `MouseWorldPosition`
  as two floats. ~10 bytes/frame; an 8-frame redundancy window is ~80 bytes/packet.
- **Do NOT send `MousePosition`** (screen). Each peer computes its own cursor world
  position locally with its own camera and that's what crosses the wire, so the peer
  never needs the sender's camera and the camera never has to be deterministic.
- **Follow-up cleanup (optional but aligned):** since the sim never reads
  `MousePosition`, it can be dropped from `PlayerInput` entirely — `Game1` would keep
  the raw screen point render-side and feed only `MouseWorldPosition` into the sim.
  Leaving it for now is harmless (it just isn't serialized); removing it makes the
  "sim is world-coords-only" rule structural rather than convention.

---

## F. Loss tolerance & desync detection

- **Redundancy window:** every packet carries the last `RedundancyWindow` (≈8) local
  inputs, not just the newest. Over an unreliable channel, a single drop is covered by
  the next packet. The receive logic is already idempotent (re-confirming a frame with
  the same value is a no-op; a *different* value flags rollback).
- **Checksum / desync guard:** include a cheap state hash in each packet (e.g. FNV over
  both players' `Body.Position`/`Velocity` + the `HitIdAllocator` value at a recently
  *confirmed* frame). The peer compares against its own value for that frame; a mismatch
  is a hard desync — log the frame and (debug) dump both `SimSnapshot`s. This is the
  single most valuable debugging tool for the float-determinism risk (§H), and it's
  ~20 lines. The reference has nothing like it.

---

## G. Offline / bot path (mirrors `evil_AI`)

When there's no peer (`datachannel == null` in the reference), P2's input comes from
`evil_AI(gamestate)`. We mirror this so the whole session is exercisable headlessly:

- `IRemoteInputSource` with two impls: `NetRemoteInput` (the datachannel) and
  `BotRemoteInput` (a scripted/AI `PlayerInput` each frame).
- A headless `RollbackHarness` test (sibling to `SimRunner`) drives two
  `RollbackSession`s against in-memory `IRemoteInputSource`s with **injected latency +
  loss + reordering**, and asserts both peers' per-frame traces stay identical to a
  no-network reference run. This is the goal-7 analogue of `SnapshotRoundTripTests` and
  the real correctness gate — it lets us prove rollback without a browser or a second
  machine.

---

## H. Determinism notes (carried from goals 0–6)

- **Fixed `dt`:** already enforced (`Simulation.FixedDt`); the session's accumulator
  must never pass a variable dt into `Step`.
- **Same-build float determinism:** holds within one WASM module on both peers, but
  transcendentals (`MathF.Sin/Cos/Atan2` in `SteeringRamp`, the planners, turret aim)
  are the classic cross-runtime desync source. The §F checksum catches it; if it bites,
  the fix is a shared math shim (fixed lookup tables or a vetted polynomial) — out of
  scope until the checksum says it's needed.
- **Terrain restore is same-instance only** (journal marks are instance-relative —
  goal 6). Rollback always restores onto the peer's *own* `Simulation`, so this is
  exactly the supported case; no portability needed.
- **No wall-clock / RNG / hardware reads inside `Step`** — audited clean through goal 3
  (statics killed) and goal 4 (input via `PlayerInput`). Any new sim code must keep this.
- **No camera reads inside `Step`** (§E rule) — the sim is world-coords-only; screen↔world
  conversion happens render-side before input enters the sim. This is what lets the
  cursor cross the wire as world coords with no shared/deterministic camera.

---

## I. Simulation / PlayerInput API changes needed

Small, mechanical additions to the otherwise-finished sim:

1. **`Simulation.Step(PlayerInput p0, PlayerInput p1)`** — inject each into the right
   controller (`_controller` for the primary, the secondary player's `Ctrl`) before the
   existing per-player update loop. The current `Step(PlayerInput)` becomes
   `Step(input, default)` or stays for solo. (Requires the match to have spawned a
   second player — `AddSecondaryPlayer`, already present.)
2. **`Simulation.Checksum()`** — FNV/xor over the handful of values in §F. Cheap, pure.
3. **(Maybe) expose `CurrentFrame`** on the sim for the session's bookkeeping, or let
   the session own the frame counter (it already must, for the rings).

No changes to `Snapshot()`/`Restore()` — they're complete.

---

## J. Staged execution plan (each stage builds + is testable)

1. **`Step(p0, p1)` + a 2-player local match.** ✅ **done.** `Simulation.Step(p0, p1)`
   injects `p1` into the first secondary player's controller, then runs the shared
   step (`Simulation.cs`). The spoof seam is `Net/IRemoteInputSource` — the local
   stand-in for a future peer (the reference's `evil_AI` path); `Net/BotInputSource`
   is the stage-1 impl: seeded-random movement + click/stab attacks aimed at P1, held
   over short runs so it reads as behavior not noise. The bot lives *outside* the sim
   (like a keyboard) — its RNG is never snapshotted; rollback replays its buffered
   output, never re-invokes it. `Game1` drives P2 with the bot whenever the stage
   spawns a second player (`game_config.json: SpawnSecondPlayer`). Verified by
   `MTile.Tests/Sim/TwoPlayerStepTests`: two bots → bit-identical traces across runs,
   the bot actually moves + attacks, and the two-player path survives a
   snapshot/restore round-trip.
   - **Per-player factions:** the `Faction` enum is now `{ Player1, Player2, Enemy,
     Neutral }`. Each player owns a distinct faction (primary = `Player1`, secondary =
     `Player2`); action hitboxes + player-spawned projectiles carry the owner's faction
     (`ctx.Faction`) instead of a hardcoded `Faction.Player`, so the two players damage
     each other while staying self-immune. NPC/projectile code that meant "any player"
     uses `Factions.IsPlayer` (e.g. bullet deflect, which now inherits the deflecting
     player's faction). Covered by `Players_DamageEachOther_ButNotThemselves`.
2. **`RollbackSession` + rings + bot remote, in-process.** ✅ **done.** `Net/InputRing`
   (per-player `(frame, input, imputed)` ring), `Net/SnapshotRing` (`SimSnapshot` ring),
   `Net/InputPacket` (redundancy-window packet + `InputCompare.Equal`), and
   `Net/RollbackSession` — the loop from §D: `ReconcileRemote` → rollback+replay
   (`ProcessInbox`, restoring `snapshots[F]` and replaying forward — the reference's
   throwaway-resim bug fixed) → stall cap → repeat-last prediction → snapshot+`Step(p0,p1)`
   → schedule local input `InputFrameDelay` ahead + send a redundancy window. Verified by
   `MTile.Tests/Sim/RollbackHarnessTests`: two sessions over an in-memory `LossyLink`
   (injectable latency/drop/reorder). Zero-latency ⇒ **0 rollbacks** and peers identical;
   3–9-tick jittery latency + **25% loss** ⇒ rollbacks fire (~50/run, 85/300 packets
   dropped) yet both peers reconstruct the clean reference **bit-for-bit**. Local inputs
   are pure functions of frame (sim-state-independent) so any divergence is a rollback
   bug, not input drift. Caveat: terrain restore is same-instance (goal 6), satisfied
   here since each peer rolls back its own sim.
3. **`Checksum()` + desync guard** ✅ **done.** `Simulation.Checksum()` is an FNV-1a
   fingerprint over both players' pose/velocity/health, the entity set, and the id/hit-id
   counters (exact float bits, pure, order-stable). Each `InputPacket` piggybacks
   `(ChecksumFrame, Checksum)` for the sender's newest *fully-confirmed* frame; the peer
   buffers the claim and compares it against its own checksum once it has confirmed that
   frame too (apples-to-apples — both sides have the identical confirmed input set), via
   `RollbackSession.ConfirmedFrame`/`CheckPendingChecksums`. A mismatch bumps `DesyncCount`
   and fires `OnDesync(frame, ours, theirs)`. Verified in `RollbackHarnessTests`: the guard
   stays silent across the zero-latency and the 25%-loss runs, and **fires** (every
   confirmed frame) when one peer's sim is built with a 0.5px-shifted spawn.
4. **`RtcConnection` bridge + signaling.** 🟡 **transport done (desktop); signaling/UI
   pending.** Decision: desktop-first via **SIPSorcery** (pure-C# WebRTC) rather than the
   browser/JS path — see the new `MTile.Rtc` library (kept out of Core so the KNI/web glob
   never pulls in sockets; referenced by `MTile.Desktop` + `MTile.Tests`).
   - `Net/InputCodec` — the §E wire format: per input a `ushort` of packed buttons +
     `MouseWorldPosition` (2 floats), 10 bytes; packet header carries player, frame,
     and the `(ChecksumFrame, Checksum)` desync claim. `MousePosition` (screen) is never
     sent. Round-trip + malformed-buffer tests in `InputCodecTests`.
   - `MTile.Rtc/RtcConnection` — SIPSorcery `RTCPeerConnection` + one unreliable/unordered
     `DataChannel`; non-trickle signaling (`CreateOfferAsync`/`CreateAnswerAsync`/
     `AcceptAnswer` exchange base64 SDP blobs after ICE gathering). Open-readiness is taken
     from the channel's `readyState` (its `onopen` proved unreliable across the SCTP
     handshake). STUN configurable for NAT traversal; none needed for LAN/loopback.
   - `MTile.Rtc/RtcSessionLink` — binds an `RtcConnection` to a `RollbackSession`: encodes
     outgoing packets, and parks incoming datachannel bytes on a concurrent queue drained
     by `PumpInbox` on the game thread (SIPSorcery delivers off-thread).
   - **Verified headlessly:** `RtcConnectionTests` stands up two `RtcConnection`s in-process,
     completes the offer/answer handshake over loopback (ICE+DTLS, no STUN), and round-trips
     an encoded `InputPacket` across the channel — the desktop analogue of a two-tab connect.
   - **Still to do:** a real signaling exchange between two machines (copy/paste console flow
     or Firestore), and the desktop **lobby/connect UX**.
5. **Wire the transport into `RollbackSession` + the game loop.** 🟡 **code complete on
   desktop; needs a live two-machine click-test.** `Net/NetSetup` is the Core-side seam
   (local player index, a `Send` delegate, and a thread-safe inbox the transport feeds);
   it keeps Core free of any socket dependency. `Game1(NetSetup)` builds a `RollbackSession`
   over its own `Simulation`, samples the local player's input each `Update`, drains the
   inbox, and calls `TryStep` instead of `Step` (camera + landing puff follow the *local*
   player, so the joiner views P2). The desktop entry point (`MTile.Desktop/Program.cs`)
   gained `host`/`join` modes that run the SIPSorcery offer/answer copy/paste handshake,
   then launch `Game1` as player 1 / player 2. All three hosts build; the offline + bot
   paths are unchanged. **Untested by automation:** the actual end-to-end match (real
   connection + the Game1 loop) — that's the manual two-instance test.
6. **Tuning + polish:** `InputFrameDelay` knob (auto from measured RTT?), redundancy
   window size, stall slack, connection-lost handling, a desync-recovery path (full
   state resend over a reliable side-channel, since terrain can't be journaled across
   instances — a fresh `SimSnapshot` + terrain *full copy* would be the resync payload).

Milestone after stage 5: a playable 2-player rollback match in the browser. Stage 6 is
hardening.

---

## K. Open questions

- **`InputFrameDelay`:** fixed at 3 (reference) vs. adaptive from RTT. Start fixed;
  revisit once stage-5 shows real latencies.
- **Reliable-ordered vs. unreliable+window channel:** reference uses reliable; we lean
  unreliable+redundancy. Decide at stage 4 based on bring-up friction (§C).
- **Desync resync payload:** a journaled terrain can't transfer cross-instance, so a
  mid-match resync needs a *full* terrain copy (not a journal mark). Acceptable as a
  rare reliable-channel event; confirm the size is sane for the 25-chunk worlds.
- **Spectators / >2 players:** out of scope; the id counter + per-player rings would
  generalize, but P2P mesh beyond 2 is a separate design.
- **Frame-count divergence under heavy stall:** the reference's stall cap can let one
  peer pause; confirm the accumulator + stall interact so both peers converge in
  frame count rather than drifting.
