# scripts/

Operational tooling — publishing, asset conversion, and remote build boxes. None of it is
part of a normal build; `dotnet build MTile.sln` and `dotnet test` need nothing here.

| Script | Purpose |
|---|---|
| [`publish-web.sh`](publish-web.sh) | AOT-publish the Blazor/KNI web build and push it to GitHub Pages (macOS/Linux). |
| [`publish-web.ps1`](publish-web.ps1) | The same, on Windows. |
| [`extract-sfx-candidates.ps1`](extract-sfx-candidates.ps1) | Pull the hand-picked SFX candidates out of the Sonniss zips into `Audio/candidates/`. |
| [`build-sfx.ps1`](build-sfx.ps1) | Batch-convert hand-cut sound slices into game-ready Ogg. |
| [`sync-sounds.ps1`](sync-sounds.ps1) | Wire the built Ogg clips into the content pipeline + sound manifest. |
| [`vm-bootstrap.sh`](vm-bootstrap.sh) | Bootstrap a fresh Ubuntu/Debian cloud VM for headless build + test. |
| [`GCP_SETUP.md`](GCP_SETUP.md) | Operator notes for the GCP dev box that `vm-bootstrap.sh` provisions. |

---

## `publish-web.sh` / `publish-web.ps1` — web publish → GitHub Pages

Two ports of one script — use the one for your platform, and **change both together**.

```bash
./scripts/publish-web.sh                  # macOS/Linux: AOT publish -> copy -> commit -> push
./scripts/publish-web.sh --no-push        # stop after the commit (inspect first)
./scripts/publish-web.sh --skip-build     # reuse the last publish output (copy/push only)
./scripts/publish-web.sh --site-repo <path>
```

```powershell
pwsh scripts/publish-web.ps1              # Windows: same steps
pwsh scripts/publish-web.ps1 -NoPush      # stop after the commit (inspect first)
pwsh scripts/publish-web.ps1 -SkipBuild   # reuse the last publish output (copy/push only)
pwsh scripts/publish-web.ps1 -SiteRepo <path>
```

The site checkout lives outside this repo, so its path is per-machine. The `.sh` port finds it
by looking for a directory holding `_config.yml` — first a sibling of the game repo, then
`~/dev/amdson.github.io` — and `MTILE_SITE_REPO` or `--site-repo` overrides that. The `.ps1`
defaults to the Windows box's `C:\Users\amdic\amdson.github.io`.

Publishes to <https://amdson.github.io/mtile/>.

**Always publish through this script.** `MTile.Web.csproj` still carries
`<RunAOTCompilation>false</RunAOTCompilation>`, and AOT is mandatory for a playable web build
— **2.7 fps interpreted vs ~40 fps AOT**. The script overrides the flag with
`-p:RunAOTCompilation=true`, so a plain `dotnet publish -c Release` ships the 2.7 fps build.

Needs `dotnet workload install wasm-tools`. First compile takes ~15 min; output wwwroot is
~49 MB. The mirror step (`rsync -a --delete`, or `robocopy /MIR` on Windows) skips `.br`/`.gz`
(Pages gzips on the fly) and `.md` (the Pages Jekyll build would turn stray markdown into site
pages). It mirrors rather than copies, so a file dropped from the build is dropped from the site.

See `Plans/WEB_PVP.md` and `Plans/INTERNET_READY_PLAN.md`.

## `extract-sfx-candidates.ps1` — Sonniss zips → candidate folder

```powershell
pwsh scripts/extract-sfx-candidates.ps1 -DryRun   # print the plan, extract nothing
pwsh scripts/extract-sfx-candidates.ps1           # -> Audio/candidates/<group>/
```

Reads `Assets/Sounds/Sonniss.com-GDC2026-*.zip` and extracts 74 hand-picked WAVs (331 MB) into
group folders — `tile_break/`, `scrape_loop/`, `hit_impact/`, and so on. Selection is a literal
list of filename substrings in the script; each must match exactly one entry or the script reports
it rather than guessing.

Extracting selectively is the point: the four bundles are 6.6 GB uncompressed and the machine did
not have room for them.

Both the zips and `Audio/candidates/` are **gitignored** — Sonniss licenses permit use in a shipped
game but not redistribution, and this repo is public. Because this script makes the folder
reproducible, it is safe to delete it to reclaim disk.

Which file is a candidate for what: [`Plans/AUDIO_CANDIDATES.md`](../Plans/AUDIO_CANDIDATES.md).

## `build-sfx.ps1` — raw slices → game-ready SFX

```powershell
pwsh scripts/build-sfx.ps1 -Name tile_break -DryRun   # print the plan + filter chain
pwsh scripts/build-sfx.ps1 -Name tile_break           # -> tile_break_01.ogg, _02, ...
pwsh scripts/build-sfx.ps1 -Loop -Name wall_scrape    # loops: no silence trim, no end fade
```

Reads `Audio/raw/`, writes `Assets/Sounds/`. Flags: `-In` / `-Out` / `-Name` / `-Rate` /
`-Lufs` / `-Quality` / `-Loop` / `-Force` / `-DryRun`.

Requires ffmpeg: `winget install Gyan.FFmpeg` (then restart the shell).

Per file it strips leading silence, loudness-matches to −16 LUFS, applies a 5 ms tail fade,
and writes mono 22.05 kHz Ogg Vorbis. `-Loop` disables the trim and the fade — both would
move or break the loop point.

Two deliberate choices worth knowing:

- **LUFS, not peak.** Peak normalization does not match *perceived* loudness, and round-robin
  variants that differ in loudness read as a bug rather than as variety.
- **No pitch, gain, pan, or variant selection.** Those are runtime concerns
  (`Plans/AUDIO_PLAN.md` §5). Baking variation into the files *and* varying at runtime is the
  classic mistake.

**Then run [`sync-sounds.ps1`](sync-sounds.ps1)** to wire the output into the game — the
clips are otherwise inert files on disk.

Workflow, sourcing, and the full sound list: `Plans/AUDIO_ASSET_LIST.md`. Design:
`Plans/AUDIO_PLAN.md`.

## `sync-sounds.ps1` — clips on disk → clips in the game

```powershell
pwsh scripts/sync-sounds.ps1 -DryRun   # list what it sees, write nothing
pwsh scripts/sync-sounds.ps1           # regenerate mgcb entries + manifest
```

Scans `Assets/Sounds/*.ogg` and regenerates two generated artifacts:

- the `GENERATED SOUNDS` region of `Content/Content.mgcb` — one `OggImporter` +
  `SoundEffectProcessor` entry per clip, built out-of-tree from `../Assets/Sounds` and
  mapped to the asset name `Sounds/<stem>`. Both content toolchains (MonoGame 3.8.4.1
  desktop, KNI 4.1.9001 web) ship `OggImporter`, so one set of entries serves both hosts.
- `Audio/SoundManifest.g.cs` — the compile-time `(stem, variant count)` table `SoundBank`
  reads. Generated as C# rather than a runtime JSON manifest because the WASM host cannot
  enumerate a content directory.

Naming is the contract: `<stem>.ogg` for a single clip, `<stem>_01.ogg`, `<stem>_02.ogg`, …
for round-robin variants — exactly what `build-sfx.ps1 -Name <stem>` produces. A stem with
no matching entry in `Audio/SoundKind.cs` is flagged `?`: it will build, but nothing will
ever play it until a `SoundKind` + stem exist.

So the full loop for a new sound is: cut slices into `Audio/raw/` → `build-sfx.ps1 -Name x`
→ add `X` to `SoundKind` + `"x"` to its stem table → `sync-sounds.ps1` → build. Only the
`SoundKind` step is hand-written, and only for a sound the game doesn't already know about.

## `vm-bootstrap.sh` — headless build/test VM

```bash
curl -fsSL https://raw.githubusercontent.com/<repo>/main/scripts/vm-bootstrap.sh | sudo bash
```

Idempotent, so re-running it to update the box is fine. Installs prerequisites and the .NET 8
SDK, drops an SSH deploy key, clones or fast-forwards the repo, then restores, builds
`MTile.Core`, and runs the suite.

Deliberately does **not** set up a display, OpenGL/Mesa, or the MonoGame content pipeline —
`MTile.Core` and `MTile.Tests` need none of them. Running the actual game window would
additionally require Xvfb + Mesa.

Cloud-agnostic despite the GCP notes; works from GCP startup-script / EC2 user-data too, but
those run as root, so set `TARGET_USER` explicitly there.
