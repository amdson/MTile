---
name: audio-pipeline
description: Use when adding, converting, or debugging MTile sound effects — hand-cut slices in Audio/raw to game-ready Ogg in Assets/Sounds, the build-sfx.ps1 / sync-sounds.ps1 pipeline, Content.mgcb + SoundManifest.g.cs regeneration, and mapping a SoundKind so a clip is actually audible.
---

# Audio asset pipeline

```bash
# Audio assets: hand-cut slices in Audio/raw → game-ready Ogg in Assets/Sounds
pwsh scripts/build-sfx.ps1 -Name tile_break -DryRun   # print the plan + filter chain
pwsh scripts/build-sfx.ps1 -Name tile_break           # → tile_break_01.ogg, _02, …
pwsh scripts/build-sfx.ps1 -Loop -Name wall_scrape    # loops: no silence trim, no end fade
pwsh scripts/sync-sounds.ps1                          # wire clips into the pipeline + manifest
```

`build-sfx.ps1` needs ffmpeg (`winget install Gyan.FFmpeg`, then restart the shell). It outputs the
format `Plans/AUDIO_ASSET_LIST.md` specifies — mono, 22.05 kHz, Ogg Vorbis, **loudness-matched by
LUFS** (peak normalization does not match perceived loudness, and round-robin variants that differ
in loudness read as a bug rather than as variety). It deliberately does *not* bake in pitch, gain,
pan, or variant selection: those are runtime concerns (`Plans/AUDIO_PLAN.md` §5), and doing both is
the classic mistake.

**Slotting a clip in: `build-sfx.ps1` → `sync-sounds.ps1` → build.** `sync-sounds.ps1` scans
`Assets/Sounds/*.ogg` and regenerates the `GENERATED SOUNDS` region of `Content/Content.mgcb`
plus `Audio/SoundManifest.g.cs`; both are generated, don't hand-edit them. The clips stay in
`Assets/Sounds` and are built out-of-tree (`/build:../Assets/Sounds/x.ogg;Sounds/x.xnb`), so
nothing is moved or duplicated, and the same entries serve both content toolchains — no
wwwroot copy rule needed. `SoundEffect.FromStream` is WAV-only on both backends, which is why
audio goes through the pipeline while the PNGs under `Assets/` do not.

A clip is only *audible* once a `SoundKind` maps to its stem (`Audio/SoundKind.cs`) and
`Audio/GameAudio.cs` says what triggers it — that policy file is the only hand-written step.
Unmapped stems build fine and are reported by `sync-sounds.ps1` with a `?`. A kind with no
clips on disk is silent, never an error, so the game runs identically with an empty bank.
Dev hotkeys: **F9** fires the committed `dev_tone.ogg`, **F10** mutes. Design:
`Plans/AUDIO_PLAN.md`; web audio still needs a click-to-start unlock before it will sound.
