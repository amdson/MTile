# Multiplayer Testing Guide

How to run and test the desktop rollback-netcode build (GGPO_PLAN stage 5). The two
players connect peer-to-peer over WebRTC (SIPSorcery); signaling is a manual copy/paste
of two base64 blobs.

> **Prerequisite:** both sides must run the **same build** and the same
> `game_config.json` / `movement_config.json`. The simulation is deterministic only
> across identical builds — mismatched config/binaries will drift apart on screen.

---

## Quick test — two windows on one machine

The fastest sanity check. Open **two terminals** at the repo root.

**Terminal 1 — host (you'll be player 1):**
```
dotnet run --project MTile.Desktop -- host
```
It prints an **OFFER** blob (one long line) and waits.

**Terminal 2 — joiner (you'll be player 2):**
```
dotnet run --project MTile.Desktop -- join
```
1. Paste the host's OFFER line, press Enter.
2. It prints an **ANSWER** blob.

**Back in Terminal 1:** paste the joiner's ANSWER line, press Enter.

Both windows print `Connected. You are player N. Launching…` and open the game. You
drive **P1** in the host window and **P2** in the joiner window; each camera follows its
own player. Move and attack in one window and watch it mirror in the other.

> The `--` matters: it tells `dotnet run` to pass `host`/`join` to the game, not to
> `dotnet`. Alternatively run the built exe directly:
> ```
> MTile.Desktop\bin\Debug\net8.0\MTile.exe host
> MTile.Desktop\bin\Debug\net8.0\MTile.exe join
> ```

---

## Two different machines

Same flow, but the OFFER/ANSWER blobs travel between machines (paste them into a chat,
email, or shared note — each is a single line of base64):

1. Machine A: `... -- host` → copy the OFFER → send to B.
2. Machine B: `... -- join` → paste the OFFER → copy the ANSWER → send back to A.
3. Machine A: paste the ANSWER.

The default Google STUN server (`stun:stun.l.google.com:19302`) is already configured
for NAT traversal, so this works on most home networks. Same-LAN connects directly.

### Custom STUN / TURN

Pass server URLs as extra args (they replace the default):
```
dotnet run --project MTile.Desktop -- host stun:your.stun.server:3478
dotnet run --project MTile.Desktop -- host turn:user:pass@your.turn.server:3478
```
A TURN relay is only needed if **both** peers are behind symmetric NATs (rare on home
connections, common on some corporate/mobile networks). TURN wiring is not yet tested.

---

## Offline / solo (unchanged)

No args runs the original single-player game:
```
dotnet run --project MTile.Desktop
```
If `game_config.json` has `SpawnSecondPlayer: true`, a local bot drives P2 (the stage-1
bring-up path) — no networking involved.

---

## What "working" looks like

- Both windows reach `Connected…` within a few seconds (single machine) or a few more
  (across the internet, while STUN resolves).
- Each player moves smoothly in their own window with **no input lag** (input delay +
  prediction hide the round-trip).
- The remote player's motion in your window matches what they did in theirs. Brief
  "correction" snaps under bad network conditions are normal — that's rollback fixing a
  misprediction.
- Attacks land across players (P1 and P2 are different factions and can damage each
  other).

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `Connection timed out` after pasting | Blob was truncated or line-wrapped on paste. Re-copy the **entire** single line between the `===` markers. |
| Times out across the internet, fine on LAN | NAT traversal failed (likely symmetric NAT). Needs a TURN relay (not yet wired). |
| Windows firewall prompt on first run | Allow UDP for the app — WebRTC needs it. |
| Both connect but the two views **drift apart** over time | Determinism desync: the builds or config differ, or a float-nondeterminism bug. The `RollbackSession.DesyncCount` / `OnDesync` guard detects this (computed every confirmed frame) but isn't shown on screen yet — ask to surface it. |
| One window freezes briefly then catches up | The stall cap pausing a peer that ran too far ahead of the other. Expected under lag; should self-correct. |
| `Networking error: …` printed | The handshake threw (bad/empty blob, or ICE failure). Restart both sides and retry. |

---

## How it's verified under the hood

The two halves are each covered by automated tests; only the live wiring between them
is manual:

- **Rollback algorithm** — `MTile.Tests/Sim/RollbackHarnessTests.cs`: two sessions over
  an in-memory lossy link (latency / 25% drop / reorder) reconstruct a clean reference
  bit-for-bit, and the desync guard fires when sims are forced to diverge.
- **Transport** — `MTile.Tests/Sim/RtcConnectionTests.cs`: two real `RtcConnection`s
  complete the WebRTC handshake over loopback and round-trip an encoded `InputPacket`.
- **Wire format** — `MTile.Tests/Sim/InputCodecTests.cs`: packet encode/decode round-trip.

Run them all with:
```
dotnet test MTile.Tests/MTile.Tests.csproj
```
