#!/usr/bin/env python3
"""Headless smoke test for the MTile web build.

Usage: web_smoke.py <url> [--seconds N] [--click-solo] [--shot PATH]
Loads the page in headless Chromium (SwiftShader WebGL), captures console
messages and page errors for N seconds, reports whether the Blazor app got
past the loading screen and whether the canvas is being ticked.
Exit 0 = no page errors and app rendered; 1 otherwise.
"""
import asyncio, sys, json
from playwright.async_api import async_playwright

def _browser_path():
    """The system browser to drive. Defaults to the dev box's chromium; macOS has
    no /usr/bin/chromium, so MTILE_SMOKE_BROWSER overrides — e.g.
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"."""
    import os
    return os.environ.get("MTILE_SMOKE_BROWSER", "/usr/bin/chromium")


async def main():
    url = sys.argv[1]
    seconds = 20
    click_solo = "--click-solo" in sys.argv
    shot = None
    if "--seconds" in sys.argv:
        seconds = int(sys.argv[sys.argv.index("--seconds") + 1])
    if "--shot" in sys.argv:
        shot = sys.argv[sys.argv.index("--shot") + 1]

    console, errors = [], []
    async with async_playwright() as p:
        browser = await p.chromium.launch(
            executable_path=_browser_path(),
            args=["--enable-unsafe-swiftshader", "--use-angle=swiftshader",
                  "--disable-dev-shm-usage"])
        page = await browser.new_page(viewport={"width": 1280, "height": 900})
        page.on("console", lambda m: console.append(f"[{m.type}] {m.text[:500]}"))
        page.on("pageerror", lambda e: errors.append(str(e)[:1000]))
        await page.goto(url, wait_until="load", timeout=60000)
        await page.wait_for_timeout(seconds * 1000)

        if click_solo:
            try:
                await page.get_by_text("Solo", exact=True).click(timeout=5000)
                await page.wait_for_timeout(8000)
            except Exception as e:
                errors.append(f"solo click failed: {e}")

        state = await page.evaluate("""() => ({
            loadingGone: !document.getElementById('loading'),
            hasCanvas: !!document.getElementById('theCanvas'),
            canvasSize: (c => c ? [c.width, c.height] : null)(document.getElementById('theCanvas')),
            errorUiShown: (e => e && getComputedStyle(e).display !== 'none')(document.getElementById('blazor-error-ui')),
            bodyText: document.body.innerText.slice(0, 400),
        })""")
        if shot:
            await page.screenshot(path=shot)
        await browser.close()

    print("STATE:", json.dumps(state, indent=1))
    print(f"CONSOLE ({len(console)}):")
    for c in console[-40:]:
        print(" ", c)
    print(f"PAGEERRORS ({len(errors)}):")
    for e in errors:
        print(" ", e)
    ok = state["loadingGone"] and not state["errorUiShown"] and not errors
    print("RESULT:", "PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)

asyncio.run(main())
