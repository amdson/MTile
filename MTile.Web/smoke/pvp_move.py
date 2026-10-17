import asyncio, base64, json, re, sys
from playwright.async_api import async_playwright

from PIL import Image, ImageChops

def _browser_path():
    """The system browser to drive. Defaults to the dev box's chromium; macOS has
    no /usr/bin/chromium, so MTILE_SMOKE_BROWSER overrides — e.g.
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"."""
    import os
    return os.environ.get("MTILE_SMOKE_BROWSER", "/usr/bin/chromium")


URL = sys.argv[1]; PRE = sys.argv[2]
ARGS = ["--enable-unsafe-swiftshader", "--use-angle=swiftshader", "--disable-dev-shm-usage",
        "--disable-background-timer-throttling", "--disable-renderer-backgrounding",
        "--disable-backgrounding-occluded-windows"]

def _tamper_build(offer):
    """The host's offer blob with its embedded build id swapped for a bogus one."""
    d = json.loads(base64.b64decode(offer))
    assert "mtile" in d, "offer blob carries no lobby metadata"
    d["mtile"]["build"] = "not-this-build"
    return base64.b64encode(json.dumps(d).encode()).decode()


async def main():
    logs, errors = [], []
    async with async_playwright() as p:
        ba = await p.chromium.launch(executable_path=_browser_path(), args=ARGS)
        bb = await p.chromium.launch(executable_path=_browser_path(), args=ARGS)
        a = await ba.new_page(viewport={"width": 1100, "height": 800})
        b = await bb.new_page(viewport={"width": 1100, "height": 800})
        a.on("console", lambda m: logs.append("[A]" + m.text[:200]))
        b.on("console", lambda m: logs.append("[B]" + m.text[:200]))
        a.on("pageerror", lambda e: errors.append(f"A: {e}"))
        b.on("pageerror", lambda e: errors.append(f"B: {e}"))
        await a.goto(URL, wait_until="load"); await b.goto(URL, wait_until="load")
        # Drive the serverless manual blob exchange — "Host"/"Join" without the "(manual)"
        # suffix are the Firestore room-code path, which needs network + config.
        await a.get_by_role("button", name="Host (manual)").wait_for(timeout=90000)
        await b.get_by_role("button", name="Join (manual)").wait_for(timeout=90000)

        # Host picks a non-default stage; the joiner must see it before playing.
        await a.get_by_role("button", name=re.compile(r"^Arena")).click()
        await a.get_by_role("button", name="Host (manual)").click()
        offer_el = a.locator("textarea[readonly]"); await offer_el.wait_for(timeout=20000)
        offer = (await offer_el.input_value()).strip()

        # Version gate: an offer from a different build must be refused up front.
        await b.get_by_role("button", name="Join (manual)").click()
        await b.locator("textarea:not([readonly])").fill(_tamper_build(offer))
        await b.get_by_role("button", name="Create answer").click()
        await b.get_by_text("Different versions").wait_for(timeout=10000)
        print("version mismatch refused")
        await b.goto(URL, wait_until="load")
        await b.get_by_role("button", name="Join (manual)").wait_for(timeout=90000)

        await b.get_by_role("button", name="Join (manual)").click()
        await b.locator("textarea:not([readonly])").fill(offer)
        await b.get_by_role("button", name="Create answer").click()
        ans_el = b.locator("textarea[readonly]"); await ans_el.wait_for(timeout=20000)
        answer = (await ans_el.input_value()).strip()
        stage_line = await b.locator(".mtile-stageline").inner_text()
        assert "Arena" in stage_line, f"joiner did not see the host's stage: {stage_line!r}"
        print("joiner sees:", " ".join(stage_line.split()))
        await a.locator("textarea:not([readonly])").fill(answer)
        await a.get_by_role("button", name="Connect").click()
        await a.wait_for_selector(".mtile-lobby", state="detached", timeout=30000)
        await b.wait_for_selector(".mtile-lobby", state="detached", timeout=30000)
        print("both playing")
        await asyncio.sleep(4)

        await b.screenshot(path=PRE + "_join_t0.png")
        await a.keyboard.down("d")
        await asyncio.sleep(2.5)
        await b.screenshot(path=PRE + "_join_t1.png")   # mid-hold
        await a.screenshot(path=PRE + "_host_t1.png")
        await a.keyboard.up("d")
        await asyncio.sleep(1)

        # Disconnect overlay: the host drops the connection mid-match.
        await a.evaluate("window.mtileRtc.close()")
        overlay_ok = True
        try:
            await b.get_by_text("The other player disconnected").wait_for(timeout=45000)
            print("joiner shows disconnect overlay")
        except Exception as e:
            overlay_ok = False
            print("disconnect overlay NOT shown:", e)
        await b.screenshot(path=PRE + "_join_disconnected.png")
        await ba.close(); await bb.close()

    t0 = Image.open(PRE + "_join_t0.png").convert("L")
    t1 = Image.open(PRE + "_join_t1.png").convert("L")
    # play-area crop: skip HUD (top 140px) and build meter (bottom 60px)
    box = (0, 140, 1100, 740)
    diff = ImageChops.difference(t0.crop(box), t1.crop(box))
    changed = sum(1 for v in diff.getdata() if v > 24)
    print(f"join-page changed pixels in play area: {changed}")
    stalls = [l for l in logs if "stall" in l.lower()]
    print("errors:", errors[:3])
    ok = not errors and changed > 300 and overlay_ok
    print("RESULT:", "PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)

asyncio.run(main())
