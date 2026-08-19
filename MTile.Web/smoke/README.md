# Headless browser smoke tests for the web build

Two Playwright scripts that boot the KNI/Blazor build in headless Chromium
(SwiftShader WebGL — no GPU needed, works on the dev box):

- `web_smoke.py <url> [--click-solo] [--shot out.png]` — loads the page,
  collects console/page errors, optionally clicks **Solo** and screenshots.
  PASS = Blazor booted past the loading screen with zero page errors.
- `pvp_move.py <url> <shot-prefix>` — full two-player round-trip: launches TWO
  separate Chromium processes (two tabs in one browser won't work — the
  backgrounded tab's rAF throttles, it stops feeding inputs, and the other
  peer stall-caps, which is correct rollback behavior), scripts the Host/Join
  copy-paste blob exchange, waits for both to enter Playing, holds `d` on the
  host, and asserts the joiner's play-area pixels changed (remote player
  visibly moved). PASS = handshake + input mirroring work.

## Setup (once per machine)

```bash
sudo apt-get install -y chromium python3-venv   # or python3.X-venv
python3 -m venv ~/.mtile-smoke-venv
~/.mtile-smoke-venv/bin/pip install playwright pillow
```

The scripts drive a browser already on the machine rather than downloading one
(`playwright install` is never needed). They default to `/usr/bin/chromium`; set
`MTILE_SMOKE_BROWSER` to point elsewhere — required on macOS, which has no such path:

```bash
export MTILE_SMOKE_BROWSER="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
```

On macOS the venv setup is just the last two lines above (Chrome stands in for chromium).

## Run

```bash
dotnet run --project MTile.Web        # dev server, default http://localhost:5000
~/.mtile-smoke-venv/bin/python MTile.Web/smoke/web_smoke.py http://localhost:5000/ --click-solo --shot /tmp/solo.png
~/.mtile-smoke-venv/bin/python MTile.Web/smoke/pvp_move.py http://localhost:5000/ /tmp/pvp
```

Also valid against a published build served statically:

```bash
dotnet publish MTile.Web/MTile.Web.csproj -c Release -o /tmp/mtile-publish
(cd /tmp/mtile-publish/wwwroot && python3 -m http.server 8080)
~/.mtile-smoke-venv/bin/python MTile.Web/smoke/pvp_move.py http://127.0.0.1:8080/ /tmp/pub
```

## Benchmarking the sim in the browser

`Diagnostics/QpBench.cs` compiles into both hosts, so the corrector QP — the sim's only hot
solver — can be timed on the same captured subproblem natively and in wasm. **F8** runs it
in-game and prints to the console (devtools on web) — but Chrome reserves the function keys,
so a headless driver cannot press F8 and `?qpbench=1` in the URL arms the same run instead.
Native reference comes from
`dotnet run -c Release --project MTile.Bench -- --corrector`.

Driving that headlessly on Windows uses node + Chrome rather than the Playwright/Python
scripts above (this box has no usable python):

```bash
npm i puppeteer-core                       # once, anywhere on PATH for node
pwsh scripts/publish-web.ps1 -NoPush       # or: dotnet publish MTile.Web -c Release -p:RunAOTCompilation=true -o <dir>
node MTile.Web/smoke/serve.js <publish-dir>/wwwroot 8080
node MTile.Web/smoke/qp_bench.js http://127.0.0.1:8080/
```

**It must be an AOT publish.** `dotnet run --project MTile.Web` is interpreted and roughly
15× slower, so benchmarking against the dev server measures the interpreter, not the browser.
