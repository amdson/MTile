# Browser Port Plan

Goal: ship MTile as a static web page so friends can play it without installing anything.

## Strategy

There is no official MonoGame → WebAssembly path yet. The mature community route is **[KNI](https://github.com/kniEngine/kni)** — a maintained MonoGame fork by nkast that exposes the same `Microsoft.Xna.Framework` API and ships a Blazor WebAssembly backend (`Kni.Platform.Blazor`). Game code stays the same; only the host platform changes.

Alternatives considered and rejected:
- **Wait for official MonoGame WASM** — in progress, no stable timeline.
- **Rewrite in a JS engine (Phaser / PixiJS)** — would throw away the physics, FSM, and renderer work.
- **Cloud-streaming (GeForce Now-style)** — too much infra for a friend-test build.

## Constraints introduced by the browser

| Concern | Desktop | Browser (KNI/Blazor WASM) |
|---|---|---|
| File I/O | `File.ReadAllText` etc. | Read-only via HTTP fetch; `TitleContainer.OpenStream` is the portable API |
| Writes | Local disk | `localStorage` via JS interop, or no-op |
| `FileSystemWatcher` | Hot-reload works | Not available |
| Threading | Full | Single-threaded by default (WASM threads exist but require COOP/COEP headers) |
| Audio | Auto-start | Requires user gesture to unlock |
| Input | Direct keyboard | Browser eats some combos (Ctrl+W, F-keys). Mouse capture requires pointer-lock API. |
| Fullscreen | `IsFullScreen = true` | Must be triggered from a user gesture |
| Startup | Native binary | 10–25 MB WASM payload — first load needs Brotli + a loading screen |

## Plan, by phase

### ✅ Phase 1 — Cross-platform read API (DONE)

Switched all file reads to a `TitleContent.TryOpenRead` helper that uses `TitleContainer.OpenStream` for title-relative paths and falls back to `File.OpenRead` for absolute paths. This lets tests pass temp absolute paths and the production game pass title-relative paths through the same Load methods.

Files touched:
- `TitleContent.cs` — new helper
- `GameConfig.cs` — `Load` reads via helper
- `Character/MovementConfig.cs` — `Load` reads via helper; still auto-Saves defaults on missing for desktop dev hot-reload
- `World/TerrainLoader.cs` — config and chunk files read via helper; sibling chunk paths joined with forward slashes
- `Game1.cs` — passes `"game_config.json"` and `$"Levels/{stage.TerrainConfig}"` as title-relative paths; `movement_config.json` stays absolute so the `FileSystemWatcher` keeps working on desktop

Desktop build verified green. The two pre-existing test failures (`SproutPushTests`, `SimulationTests.CrouchAndWalkIntoWall_DoesNotEnterWallSliding`) are unaffected.

### Phase 2 — Project split

Carve a shared `MTile.Core.csproj` containing all game code (everything currently in [MTile.csproj](MTile.csproj) except `Program.cs`). The current root project becomes `MTile.Desktop.csproj` and just contains `Program.cs` plus the DesktopGL package reference. Tests follow `MTile.Core`.

This is mechanical but worth doing in its own commit because it touches every file's project membership and the diff would otherwise drown the Phase 3 changes.

### Phase 3 — Add `MTile.Web` Blazor WASM host (BUILD GREEN, READY FOR RUNTIME TEST)

What's in place:
- [Game1.cs:84-100](Game1.cs#L84-L100) — `FileSystemWatcher` block guarded by `OperatingSystem.IsBrowser()`. Web build skips the watcher and loads `movement_config.json` via title-relative path.
- [MTile.Web/MTile.Web.csproj](MTile.Web/MTile.Web.csproj) — Blazor WebAssembly SDK project. Re-compiles the same source MTile.Core does (via `<Compile Include="..\**\*.cs" Exclude="...">`) against KNI's BlazorGL platform instead of DesktopGL. Packages mirror nkast's [WebGLxnaProj](https://github.com/nkast/WebGLxnaProj) sample at KNI 4.1.9001.
- KNI canvas host wiring: [Pages/Index.razor](MTile.Web/Pages/Index.razor) + [Pages/Index.razor.cs](MTile.Web/Pages/Index.razor.cs) — `OnAfterRender` calls JS `initRenderJS`, JS calls back `TickDotNet` from `requestAnimationFrame`, first tick constructs `new MTile.Game1()` and `Run()`s it, subsequent ticks call `_game.Tick()`.
- [MTile.Web/wwwroot/index.html](MTile.Web/wwwroot/index.html) — KNI WASM JS shims (`nkast.Wasm.Dom`, `Canvas`, `Audio`, etc.) plus the `tickJS()` requestAnimationFrame loop.
- [MTile.Web/Program.cs](MTile.Web/Program.cs), [App.razor](MTile.Web/App.razor), [_Imports.razor](MTile.Web/_Imports.razor) — standard Blazor WASM hosting.
- Content pipeline via `<KniContentReference Include="..\Content\Content.mgcb">` + `<KniPlatform>BlazorGL</KniPlatform>`. `DebugFont.xnb` builds and lands at `MTile.Web/wwwroot/Content/DebugFont.xnb`.
- Asset staging target in csproj — copies `game_config.json`, `movement_config.json`, `Levels/**` into source `wwwroot/` at `BeforeBuild`. This is needed because Blazor WASM's dev server serves source `wwwroot/`, not `bin/wwwroot/`, so the more idiomatic `<Content Include ... Link="wwwroot\...">` rules don't actually expose the files at runtime. The staged copies are gitignored.

Verified:
- `dotnet build MTile.Web/MTile.Web.csproj` — **0 errors, 0 warnings** on the first try (genuinely surprising; KNI 4.1.9001's API surface is fully compatible with what MTile uses).
- `dotnet run --project MTile.Web/MTile.Web.csproj` brings up the dev server on `http://localhost:5000`. All asset URLs return 200:
  - `/`, `/index.html`, `/game_config.json`, `/movement_config.json`, `/Levels/terrain.json`, `/Levels/start.txt`, `/Content/DebugFont.xnb`
- Desktop sln still builds clean. Tests still pass 77/79 (same two pre-existing failures).

**MTile.Web is intentionally still NOT in [MTile.sln](MTile.sln)** — including it would pull KNI's emcc-based native build (~90s cold, ~15s warm) into every `dotnet build` on the desktop dev loop. Web is its own workflow:

```
dotnet build MTile.Web/MTile.Web.csproj
dotnet run   --project MTile.Web/MTile.Web.csproj
# then open http://localhost:5000 in a browser
```

Remaining unknowns (only discoverable by actually opening the page in a browser):
- Whether KNI's BlazorGL `Game.Run()` actually starts on first `TickDotNet` without throwing.
- Whether the canvas sizes correctly (the JS sets it to the holder's clientWidth/Height; the game also tries to set `PreferredBackBufferWidth/Height` from `game_config.json`).
- Whether KNI's content pipeline produces an XNB that the runtime content reader accepts (different XNB versions between MonoGame and KNI builds in the past).
- Whether `OperatingSystem.IsBrowser()` returns true in this hosting model (it should — it's a runtime check via the BCL).

### Phase 4 — Asset & load-time tuning

1. Compress with Brotli at publish time (`dotnet publish -c Release` + the Blazor compression).
2. Trim aggressively — `<PublishTrimmed>true</PublishTrimmed>`, then chase reflection warnings until the trimmer is happy.
3. Add a loading screen that shows download progress.
4. Run-AOT if size permits (`<RunAOTCompilation>true</RunAOTCompilation>`) — big perf win for the physics loop, costs ~2–3× the binary size.

### Phase 5 — Hosting

Publish output is static files. Pick one:
- **Cloudflare Pages** — free, fast, custom domain easy.
- **GitHub Pages** — free, no custom build needed if you commit `wwwroot/` output to a `gh-pages` branch.
- **Netlify** — free, similar to Cloudflare.

CI: a GitHub Actions workflow on push to `main` that runs `dotnet publish MTile.Web -c Release` and uploads `bin/Release/net8.0/publish/wwwroot/` to the chosen host.

### Phase 6 — Browser-specific polish

- Click-to-start screen (audio unlock + optional fullscreen request).
- Touch input handler if you want mobile to work — the current `Controller` reads keyboard + mouse only.
- Friendly "browser not supported" message for missing WebGL 2.
- Pause when the tab loses focus (otherwise the physics keeps integrating with a frozen dt).

## Risk callouts

- **Content pipeline drift.** KNI's content builder tracks MonoGame closely but isn't identical. Budget a half-day to chase down asset processor differences (spritefont generation is the usual culprit; `DebugFont` will probably need to be rebuilt).
- **Determinism.** If you ever record/replay inputs for the simulation tests, WASM's JIT vs. AOT vs. interpreter modes can produce subtly different floating-point results. Tests stay on the desktop runtime; don't try to run them in-browser.
- **`Path.Combine` with backslashes.** Phase 1 forward-slashed the chunk path join. Audit any other Path.Combine of asset-relative paths before Phase 3 — Windows-native separators trip TitleContainer's HTTP path lookup on web.
- **Movement config hot-reload.** The dev workflow relies on editing `movement_config.json` next to a running build. The web build will lose this entirely; movement tuning continues to happen on desktop and gets baked into the web build at publish time.

## Open questions for later

- Do we want a "share a level" feature once it's in-browser? Levels are plain JSON + txt — would be trivial to load from a URL fragment. Out of scope for the first friend-test build.
- Save state? Nothing currently persists across runs, so deferring this is free.
