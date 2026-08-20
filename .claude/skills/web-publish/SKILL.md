---
name: web-publish
description: Use when building, publishing, or smoke-testing the MTile browser build (KNI/Blazor WASM) — the dev server, scripts/publish-web.sh (macOS/Linux) or publish-web.ps1 (Windows) to GitHub Pages, why AOT compilation is mandatory, what a Mac-published build ships without, and the Playwright browser smoke tests in MTile.Web/smoke.
---

# Web build & publish (KNI/Blazor WASM)

```bash
# Web build (KNI/Blazor WASM)
dotnet build MTile.Web/MTile.Web.csproj
dotnet run --project MTile.Web              # dev server (interpreted — slow, fine for logic)

# Web publish → GitHub Pages (https://amdson.github.io/mtile/)
./scripts/publish-web.sh                    # macOS/Linux: --no-push / --skip-build / --site-repo
pwsh scripts/publish-web.ps1                # Windows:     -NoPush / -SkipBuild / -SiteRepo
```

**AOT is mandatory for a playable web build: 2.7 fps interpreted vs ~40 fps AOT.** The csproj still
has `<RunAOTCompilation>false</RunAOTCompilation>`; the publish script overrides it with
`-p:RunAOTCompilation=true`. **A plain `dotnet publish -c Release` therefore ships the 2.7 fps
build** — always publish via the script. AOT needs `dotnet workload install wasm-tools`; the first
compile takes ~15 min and the output wwwroot is ~49 MB.

The two publish scripts are ports of each other — `.sh` (rsync) and `.ps1` (robocopy) — so a change
to one belongs in the other. The site checkout path is per-machine: `.sh` finds it by looking for
`_config.yml` (sibling of the game repo, then `~/dev/amdson.github.io`, overridable with
`MTILE_SITE_REPO` or `--site-repo`); `.ps1` hardcodes the Windows box's path.

**Publishing from macOS ships a shader-free build.** KNI's content-pipeline package carries only
Windows natives. `MTile.Web/kni-mgcb.sh` covers most of that by borrowing macOS ffmpeg/freetype from
MonoGame's own mgcb tool (pinned in `.config/dotnet-tools.json` — run `dotnet tool restore`), but the
`.fx` compile needs `d3dcompiler_47.dll`, which has no macOS build at all. So the Mac web build uses
the shader-free `Content.Mac.mgcb`, and `CapsuleSplat`/`MetaballComposite` are absent from the
payload. Harmless today — their only consumer is the metaball dev preview that `game_config.json`
leaves off, and `Game1.cs:485-489` catches the `ContentLoadException` — but publish from Windows if
those shaders ever reach the real render path.

Browser smoke tests (Playwright, headless Chromium + SwiftShader) live in `MTile.Web/smoke/`:
`web_smoke.py` (boot + console errors), `pvp_move.py` (two *separate* browser processes — not two
tabs, since a backgrounded tab's rAF throttles and the peer stall-caps — driving the manual
copy/paste lobby and pixel-diffing that input mirrored). Setup in `MTile.Web/smoke/README.md`.

The smoke scripts drive a browser already installed rather than downloading one, defaulting to
`/usr/bin/chromium`; set `MTILE_SMOKE_BROWSER` to override — required on macOS, e.g.
`/Applications/Google Chrome.app/Contents/MacOS/Google Chrome`. `smoke/serve.js` serves a published
wwwroot with the MIME types Blazor needs; with no `node` on PATH, the emscripten workload ships one
at `~/.dotnet/packs/Microsoft.NET.Runtime.Emscripten.*.Node.*/*/tools/bin/node`.
