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

The scripts use the system `/usr/bin/chromium` (no `playwright install` download).

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
