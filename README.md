# MTile

A 2D platformer where **the terrain is the weapon** — slash, stab, pulse, and erupt
blocks to reshape a chunked tile world while moving through it. C#/MonoGame,
deterministic 60 fps sim with GGPO-style rollback netcode.

**Play in the browser: <https://amdson.github.io/mtile/>** — solo vs. a bot, or
peer-to-peer PvP: one player hits **Host** and sends the 5-char room code (or the
invite link), the other hits **Join**. Matchmaking is a tiny Firestore handshake;
the match itself is a direct WebRTC data channel between the two browsers.

## Run locally

```bash
dotnet run --project MTile.Desktop        # the game window
dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName!~Zzz"
dotnet run --project MTile.Web            # web build, local dev server
```

Architecture: [CODEBASE_OVERVIEW.md](CODEBASE_OVERVIEW.md). Build/test mechanics and
conventions: [CLAUDE.md](CLAUDE.md). Design docs and roadmaps: [Plans/](Plans/).

## Publishing the web build

The hosted game is static files served by GitHub Pages out of the
`amdson.github.io` repo's `mtile/` folder. One script is the whole release
pipeline:

```powershell
.\scripts\publish-web.ps1             # AOT publish -> copy -> commit -> push -> live in ~1 min
.\scripts\publish-web.ps1 -NoPush     # stop after the commit, inspect first
.\scripts\publish-web.ps1 -SkipBuild  # reuse the last publish output (copy/push only)
```

It AOT-publishes `MTile.Web` (mandatory — interpreted WASM runs at 2.7 fps, AOT at
~40; expect several minutes), mirrors the output into the site repo (dropping
`.br`/`.gz` that Pages won't serve and `.md` that its Jekyll build would turn into
site pages), and commits with the game commit hash so the live version is always
traceable. Full details and prerequisites: [scripts/README.md](scripts/README.md).

Since rollback assumes identical sims on both peers, publish gameplay changes
deliberately — a stale cached client will desync against a fresh one until both
players reload.

## Multiplayer notes

- Signaling config lives in
  [MTile.Web/wwwroot/firebase-config.js](MTile.Web/wwwroot/firebase-config.js);
  backend setup (Firestore, security rules, TTL) is documented in
  [MTile.Web/FIREBASE_SETUP.md](MTile.Web/FIREBASE_SETUP.md). Without a config the
  lobby falls back to a serverless manual copy/paste handshake.
- Desktop PvP exists for development
  (`dotnet run --project MTile.Desktop -- host` / `-- join`) but browser and
  desktop builds cannot cross-play (float determinism differs).
- Known gap: STUN-only for now — a pair of players behind symmetric NATs will fail
  to connect until a TURN relay is added
  ([Plans/INTERNET_READY_PLAN.md](Plans/INTERNET_READY_PLAN.md), Phase 2).
