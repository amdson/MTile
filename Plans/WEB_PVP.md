# Browser PvP — operator guide (web-pvp branch, 2026-08-08)

Two-player rollback PvP between two **browser** instances of the KNI/Blazor
WASM build, with manual copy-paste signaling (a small signaling server can
later drive the same JS functions — `wwwroot/mtileRtc.js` is DOM-free by
design). Supersedes the runtime-bring-up unknowns in
`Archive/BROWSER_PORT_PLAN.md` — the browser build now runs.

## Play a match

Dev server: `dotnet run --project MTile.Web` → http://localhost:5000.
Published build: any static file host (see below).

1. Player 1 clicks **Host**, copies the offer blob, sends it to player 2
   (chat, email, anything).
2. Player 2 clicks **Join**, pastes the offer, clicks **Create answer**,
   copies the answer blob, sends it back.
3. Player 1 pastes the answer, clicks **Connect**. The match starts on both
   sides when the data channel opens (~1 s; 30 s timeout otherwise).

Wire format and channel config are byte-identical to the desktop
`MTile.Desktop -- host/join` flow (`MTile.Rtc/RtcConnection.cs`):
`base64(JSON {type,sdp})` blobs, non-trickle ICE, channel `"mtile"`
`{ordered:false, maxRetransmits:0}`.

## Constraints & caveats

- **Same build only.** Browser↔browser or desktop↔desktop, never mixed —
  float determinism does not hold across runtimes (`ROLLBACK_ROADMAP.md`).
- **Two windows, not two tabs.** A backgrounded tab's requestAnimationFrame
  throttles, that peer stops feeding inputs, and the visible peer freezes at
  the prediction cap (correct rollback behavior). Separate windows or separate
  machines are fine.
- **NAT traversal**: STUN only (`stun:stun.l.google.com:19302`, same default
  as desktop). Symmetric-NAT pairs will fail to connect until a TURN server
  is configured — pass more URLs to `mtileRtc.createOffer/acceptOfferCreateAnswer`
  when that day comes.
- Hidden-tab/pause polish, audio, and touch input remain future work
  (`Archive/BROWSER_PORT_PLAN.md` phase 6).

## Hosting the published build

```bash
dotnet publish MTile.Web/MTile.Web.csproj -c Release -o out/
# serve out/wwwroot from any static host — verified working via plain `python3 -m http.server`
```

Any static host works (Cloudflare Pages, Netlify, GitHub Pages, a VPS with
nginx). No server code, no special headers required in the current setup.
Brotli/AOT/trimming size work is still open (BROWSER_PORT_PLAN phase 4 — the
default publish is ~19 MB before compression, and the dev-server warning about
`wasm-tools` applies to publish size too).

## Building web content on Linux

`dotnet build MTile.Web/MTile.Web.csproj` builds BlazorGL content (font +
effects) on Linux via `MTile.Web/kni-mgcb.sh` — see that script's header for
the two native libraries a fresh box needs (prebuilt Linux mojoshader from
kniEngine/kniDependencies, and a SysV→ms_abi shim over vkd3d ≥ 1.19; master
copies live in `~/.local/lib/kni-native/` on the dev box, with the shim source
`d3dshim.c` alongside). Background: vkd3d exports the Windows x64 calling
convention on Linux, so .NET P/Invoke cannot call it directly; mojoshader
additionally requires full-mask TEXKILL, which is why MetaballComposite.fx
uses a float4 `clip()` instead of a scalar `discard`.

## Verification

Headless smoke tests (boot + full two-browser PvP with pixel-diff assert):
`MTile.Web/smoke/` — see its README. Current status: solo boot, PvP handshake,
and input mirroring all PASS against both the dev server and a static-served
publish; rendering was verified in SwiftShader (software WebGL) — worth one
eyeball pass on a real GPU for the metaball/glow effects.
