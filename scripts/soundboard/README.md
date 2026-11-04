# Soundboard — swap, audition and crop sounds while the game runs

```bash
python3 scripts/soundboard/soundboard.py seed    # once: build the library + Audio/sound_map.json
python3 scripts/soundboard/soundboard.py serve   # then open http://localhost:8765
dotnet run --project MTile.Desktop               # from the repo root; it picks up every change live
python3 scripts/soundboard/soundboard.py ship    # bake the mapping into Assets/Sounds + content
```

## The page

- **Left: events.** One row per `SoundKind` (the enum *is* the event list, with its comment).
  Each row holds the event's clips; the game picks one per trigger, round-robin by seed.
  Click a clip to edit it, **▶** plays it exactly as the game renders it, **×** removes it.
  An event mapped to nothing goes silent (or borrows its `SoundKinds.Fallback`); "unmapped"
  events keep whatever ships in `Assets/Sounds`.
- **Right: library.** Every folder under the library root, searchable. **▶** auditions a file.
  **Drag a file onto an event** to add it, **onto a clip** to replace that clip. Clips can be
  dragged between events too (crop included).
- **Bottom: crop editor.** Drag across the waveform to select, drag either edge to adjust,
  wheel to zoom. Start/end, gain (dB), fade in/out, and the lead-silence trim are per clip.
  **Space** plays the crop. Every edit auto-saves, re-renders that event, and the running game
  swaps it in within a frame (a toast shows the reload).

## Where things live

| | |
|---|---|
| `Audio/sound_map.json` | **Committed.** The master mapping: `events.<stem>.clips[] = {file, start, end, gain_db, fade_in_ms, fade_out_ms, trim_silence}` (defaults omitted), plus `loop` per event. `file` is relative to the library root. |
| library | `~/dev/MTile-sfx-library` (or `"library"` in the map, or `MTILE_SFX_LIBRARY`). Outside the repo on purpose: it is large and machine-local. **Add sounds by dropping files or whole folders in**, then hit Rescan. Folders are the groups; nest as you like. Symlinks are followed. |
| `Audio/dev_cache/` | Gitignored. Each mapped event rendered to WAV, plus `manifest.json`. `Audio/SoundDevOverrides.cs` watches the manifest and replaces those kinds' clips in `SoundBank`. **Delete the folder to hear the shipped clips again.** |

`seed` fills the library with `shipped/<stem>/` (every clip the game ships, so every event
starts mapped) and `picks/<row>/` (the clips picked in the 2026-10 OGA picker,
`~/dev/sfx-picks.json`). The full candidate pool is linked in as `oga_candidates/`.

## What you hear is what ships

Both the dev render and `ship` use `scripts/build-sfx.sh`'s chain, applied after the crop:
lead-silence trim (< −50 dBFS, keeping 5 ms), −16 LUFS loudnorm, gain, fades (5 ms tail
minimum), mono 22.05 kHz. Loop events skip the trim and the tail fade. The lead trim is done
in Python rather than with ffmpeg's `silenceremove`, which (ffmpeg 9) swallows short crops
whole. The page's **▶ crop** button is a quick client-side preview *without* that chain;
**▶ as in game** plays the real render.

`ship` replaces each mapped event's `Assets/Sounds/<stem>_NN.ogg` set, copying untouched
`shipped/` clips byte-for-byte (no re-encode), then runs `sync-sounds`. It needs an ffmpeg
with libvorbis — on macOS the keg-only `ffmpeg-full` is found automatically. It lists every
clip re-rendered from a library source: **add those to `Audio/CREDITS.md`**.

## Notes

- Desktop only, launched from the repo root (the game looks for `Audio/` in its working
  directory). The web build and a packaged desktop build never watch anything.
- Audio is render-only, so a swap mid-match is safe, multiplayer included.
- A new *event* still needs code: add the `SoundKind`, then trigger it in `Audio/GameAudio.cs`.
  It shows up on the page on the next load.
- Per-event gain/pitch curves are still hard-coded in `GameAudio.cs`; the page edits clips only.
