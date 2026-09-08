---
name: screenshot
description: Use when you need to SEE a frame of MTile — visually verifying a render/animation/VFX change, grabbing a PNG of the running game or an editor tool for review. Covers MTILE_SCREENSHOT (game), MTILE_SHOT (every MTile.Demo mode + modifiers), freeze-frame scenario configs, and the batch clip-strip exporter.
---

# Frame snapshots for visual review

Every capture path renders through an offscreen `RenderTarget2D`, so window focus
doesn't matter — but a window does still open briefly (a GPU + display session is
required; this is unattended, not truly headless). Save PNGs somewhere gitignored
or temporary; only `screenshot_*.png` (the F12 pattern) is in `.gitignore`.

**Run from the repo root** so CWD-relative config loading reads `configs/`
originals (same rule as normal runs), and remember the file-lock gotcha: a running
game blocks the Desktop build's final copy step — close it first.

## The live game (`Drawing/ScreenshotSystem.cs`)

```bash
MTILE_SCREENSHOT=/tmp/shot.png dotnet run --project MTile.Desktop
```

Boots the game, waits 20 frames for the world to settle, saves the frame, exits.
~5 s round trip after a warm build. Then `Read` the PNG. The path is CWD-relative.
Desktop only (no-op in the browser build). F12 in a live session is the manual
equivalent (timestamped PNG next to the binary).

The default boot is the normal stage + idle player. To capture a *situation*,
pass a scenario config as the CLI arg:

```bash
MTILE_SCREENSHOT=/tmp/freeze.png dotnet run --project MTile.Desktop -- Testing/freeze.json
```

`Testing/*.json` are `GameConfig` files; `FreezeFrame*` knobs (`GameConfig.cs:160`)
teleport the player to a position/velocity, step the sim exactly once with a held
input, and freeze time — the frame shows that tick's corrector trajectories
(ballistic aqua, solved magenta, reference gold). Copy `Testing/freeze.json` and
edit `FreezeFramePosX/Y`, `FreezeFrameVelX/Y`, `FreezeFrameInputX`, `FreezeFrameDown`
to stage the moment you want to look at. Other `GameConfig` fields work too
(`SpawnSecondPlayer`, `StartingBlockType`, debug-draw toggles in `GameConfig.cs`).

## The demo tools (`MTILE_SHOT` — same contract, every mode)

```bash
MTILE_SHOT=/tmp/editor.png dotnet run --project MTile.Demo -- groundslash1 --usebind rabbit
MTILE_SHOT=/tmp/bind.png MTILE_SHOT_PREVIEW=1 MTILE_SHOT_CLIP=walk dotnet run --project MTile.Demo -- --bind rabbit
MTILE_SHOT=/tmp/take.png MTILE_SHOT_FRAME=42 dotnet run --project MTile.Demo -- --load Takes/foo.take.json
```

Renders a few frames, saves, exits. Modifiers (see `MTile.Demo/CONTROLS.md` §
"Screenshot env vars" for the authoritative table):

- **Clip editor**: `MTILE_SHOT_HELP` (help panel), `MTILE_SHOT_WIRE`,
  `MTILE_SHOT_NOSKEL`, `MTILE_SHOT_BODY`
- **Bind editor**: `MTILE_SHOT_PREVIEW` (deformed preview on),
  `MTILE_SHOT_CLIP=<clip>` (pose the deformation with a clip's first frame),
  `MTILE_SHOT_LAYER=<layer>`
- **Take viewer**: `MTILE_SHOT_HELP`, `MTILE_SHOT_FRAME=<n>` (scrub cursor)

`TreeViewerGame` honors `MTILE_SHOT` too.

## Many frames at once — the strip exporter

For animation work a single frame usually isn't enough; `--strip` batch-exports a
labeled grid PNG of a clip, take, or live scenario (stairs, slash-combo animator
rehearsal, `--trails` for attachment ribbons). That's a different tool with its
own doc: **`MTile.Demo/STRIPS.md`**.

```bash
dotnet run --project MTile.Demo -- --strip /tmp/slash.png groundslash1 --frames 12
dotnet run --project MTile.Demo -- --strip /tmp/combo.png --scenario slash-combo --frames 64 --trails
```

## Which capture for which question

| Question | Tool |
|---|---|
| "Does my render/HUD/VFX change look right in-game?" | `MTILE_SCREENSHOT` on the game |
| "What does the corrector do at this exact state?" | `MTILE_SCREENSHOT` + a `Testing/` freeze-frame config |
| "Does the clip/skin/binding look right?" | `MTILE_SHOT` on the demo tool, or `--strip` for the whole motion |
| "How does the move read over time?" | `--strip` (`STRIPS.md`), `--scenario` for runtime pacing |
