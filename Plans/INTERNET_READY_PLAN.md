# Internet-Ready Multiplayer Plan

Goal: two people on different home networks open a URL, one shares a short room
code, and they're playing. This is the last mile on top of the finished rollback
core (`Net/RollbackSession.cs`) and the working browser transport
(`MTile.Web/wwwroot/mtileRtc.js` + copy/paste lobby in `Pages/Index.razor.cs`).

Reference implementation: https://github.com/amdson/rtcpvp — Firestore signaling
where the room code is the Firestore doc id. Its signaling code is already
vendored in this repo as `rtc_connection_reference.js` (repo root, uncompiled).

## Scope decision

**Internet-ready = browser ↔ browser.** Desktop↔browser cross-play is off the
table anyway (float determinism, `Plans/WEB_PVP.md:29-31`), and a URL is the
only zero-install way to hand the game to a friend. Desktop `host`/`join`
copy-paste stays as the dev path; porting Firestore signaling to SIPSorcery/C#
is a later nice-to-have, not part of this milestone.

## What already exists (don't rebuild)

- `mtileRtc.js` is deliberately DOM-free with four entry points
  (`createOffer` / `acceptAnswer` / `acceptOffer` / plus state polling) exactly
  so "a future signaling server can drive them programmatically" (its own
  comment, line 10). The lobby FSM in `Index.razor.cs` already handles
  Menu → Gathering → Connecting → Playing → Failed.
- Headless Playwright smoke (`MTile.Web/smoke/pvp_move.py`) verifies the whole
  two-browser handshake — it becomes the regression net for this work.

## Phase 1 — Firestore signaling + room codes (the actual last step)

> **STATUS 2026-08: IMPLEMENTED** (fresh Firebase project — "Option B"). Code:
> `wwwroot/signaling.js` (Firestore driver), `wwwroot/firebase-config.js`
> (placeholder — paste real config per `MTile.Web/FIREBASE_SETUP.md`),
> `firestore.rules`, room-code phases in `Index.razor(.cs)` with `?room=` deep
> link; manual blob flow kept as "Host (manual)"/"Join (manual)" and the smoke
> harness updated to drive it. Remaining: user creates the Firebase project and
> pastes the config; then a real two-machine test.

Replace the copy/paste textareas with the rtcpvp flow, minimally adapted:

1. **New `wwwroot/signaling.js`** (keep `mtileRtc.js` untouched and DOM-free).
   Load Firebase via ESM CDN imports (`https://www.gstatic.com/firebasejs/…`)
   — no npm/bundler step in the Blazor host. Reuse the rtcpvp project or make a
   fresh Firebase project; config object is public by design.
2. **Blob-over-Firestore, not trickle, for v1.** `mtileRtc.js` already does
   non-trickle gathering (5 s wait, full SDP in one base64 blob) and the smoke
   harness covers it. So the doc schema is just:
   `rooms/{code} = { offer: <blob>, answer: <blob>, createdAt }`.
   - Host: `createOffer()` → write `offer` → `onSnapshot` waits for `answer` →
     `acceptAnswer()`.
   - Joiner: read `offer` → `acceptOffer()` → write `answer`.
   This reuses the exact four entry points the copy/paste lobby drives today —
   `Index.razor.cs` changes are mostly swapping "paste blob" for "enter code".
   (Trickle ICE à la rtcpvp — `offerCandidates`/`answerCandidates`
   subcollections — is a later upgrade for faster connects; not needed for
   correctness.)
3. **Room code UX**: generate a short human code (5 chars, unambiguous
   alphabet) as the doc id instead of Firestore's auto-id. Host screen shows
   the code + a copyable join URL (`?room=XYZ12`); `Index.razor` reads the
   query param and auto-joins.
4. **Firestore security rules**: allow create/read/update on `rooms/*` only,
   cap doc size, and add a TTL policy (Firestore TTL on `createdAt`, ~1 h) so
   stale rooms self-delete. rtcpvp's test-mode-rules approach is the one thing
   NOT to copy.
5. **Interop**: `Index.razor.cs` drives `signaling.js` via `IJSRuntime` the
   same way it drives `mtileRtc.js` today (byte[]↔base64 fallback pattern
   already exists at `Index.razor.cs:174-182`).
6. **Config**: signaling endpoint + ICE servers move into one place
   (`wwwroot/netconfig.json` or `configs/game_config.json`) instead of the three
   hardcoded STUN literals (`Program.cs:29`, `Index.razor.cs:35`,
   `mtileRtc.js:20`).

## Phase 2 — TURN (symmetric-NAT pairs currently just fail)

- Extend ICE config to credentialed entries: `{ urls, username, credential }`
  in `mtileRtc.js` (browser API supports it natively; today it builds
  `{ urls }` only, line 117).
- Use a managed TURN service (Cloudflare TURN or Metered/Open Relay free tier)
  — input traffic is ~1-2 KB/s per peer, so free tiers are plenty. Self-hosted
  coturn on the GCP box is the fallback if credentials-in-client is a concern
  (then: short-lived HMAC credentials, standard coturn `use-auth-secret`).
- Desktop parity (optional, later): `RtcConnection.cs:36-46` ctor takes bare
  URL strings and can't express TURN credentials — extend to
  `RTCIceServer { urls, username, credential }` when desktop internet play
  matters.

## Phase 3 — Ship a URL

- Flip `<RunAOTCompilation>` to `true` for Release in `MTile.Web.csproj`
  (currently `false` with a stale "Defer until Phase 4" comment — a plain
  `dotnet publish -c Release` today yields the 2.7 fps interpreted build).
- Host the published `wwwroot` as a static site. Firebase Hosting is the
  natural pick (same project as signaling, free tier, `firebase deploy`);
  GitHub Pages / Cloudflare Pages work too. Must serve correct
  `Content-Type: application/wasm` and ideally Brotli (`.br`) — Firebase
  Hosting handles both.
- Optional: a GitHub Action that publishes on push to `main`.

## Phase 4 — Minimum robustness for strangers-on-the-internet

Small, but currently zero:

1. **Surface desync**: subscribe `RollbackSession.OnDesync` in the web host and
   show a banner ("out of sync — restart match"). Today it fires into the void
   (no production subscribers; only tests listen).
2. **Surface disconnect mid-game**: `Index.razor.cs:185-193` handles connection
   state only pre-game; once `Playing`, a dropped peer means the stall cap
   freezes the game silently. Detect `disconnected/failed/closed` while
   `Playing` → overlay "opponent disconnected" + back-to-menu.
3. Keep the Playwright smoke green; add one smoke variant that goes through
   Firestore signaling (can point at the emulator or a test project).

## Explicitly deferred

- Trickle ICE (faster connect, not required).
- Desktop Firestore signaling / desktop TURN credentials.
- Desync *recovery* (state resync) — `Plans/Archive/GGPO_PLAN.md:326-329`.
- Matchmaking beyond room codes; reconnect/rejoin mid-match.
- Soak/latency testing under real WAN conditions (`BACKLOG.md` item 3.8) —
  becomes actually doable once Phase 1–3 land; do a real cross-network playtest
  as the acceptance check.

## Acceptance

Two machines on different networks (one ideally on phone hotspot to exercise
CGNAT/TURN): open the hosted URL, host shows code, joiner enters it, match
runs at 60 fps sim with no silent desync, and a mid-match disconnect shows a
message instead of a freeze.
