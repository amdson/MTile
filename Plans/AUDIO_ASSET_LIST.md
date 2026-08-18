# Audio asset list — what to actually source

The **acquisition** companion to [`AUDIO_PLAN.md`](AUDIO_PLAN.md). That doc's §9 answers
*what sim state drives each sound*; this one answers *what files do we need, how many
variants, and what does it sound like*. Nothing here is implemented — this is a shopping
list, ordered so that the first tier alone makes the game feel alive.

## How to read the columns

- **Kind** — `loop` (level-triggered, needs a seamless sustain) or `one-shot`. From
  `AUDIO_PLAN.md` §2. This determines the *file*, not just the code: a loop must be
  loop-point clean, a one-shot must be transient-forward with no lead-in silence.
- **Var** — how many distinct variants to source. **This is the number people get wrong.**
  High-frequency events need 4–8 or they machine-gun by the fourth trigger; a sound that
  fires once per life needs 1. Round-robin + ±5% pitch jitter on top (render-side RNG,
  never anything the sim can see).
- **Len** — rough target duration after editing.

---

## Tier 1 — the vertical slice and the core loop

If only these existed, the game would already feel dramatically better. Roughly in the
order I'd acquire them.

| Sound | Kind | Var | Len | Character | Search terms |
|---|---|---|---|---|---|
| **Tile break** | one-shot | 6–8 | 150–300 ms | Sharp crack + gravel scatter tail. The single most-heard sound in the game — the terrain *is* the weapon. Worth over-investing in. | `rock break`, `stone impact`, `debris scatter`, `concrete crack`, `rubble` |
| **Wall-slide scrape** | loop | 1–2 | 2–3 s | Continuous grit-on-stone friction, no rhythmic artifacts. Gain + pitch ride slide speed, so source it *neutral* and let the mixer shape it. | `stone scrape loop`, `friction drag`, `gravel slide`, `grinding loop` |
| **Landing thud** | one-shot | 4–6 | 100–250 ms | Body-weight impact, dry. Needs a soft→hard set (or one sample the mixer shapes by `LastImpulseMagnitude`), since landings span a huge energy range. | `body fall impact`, `thud dry`, `footstep land heavy` |
| **Footsteps** | one-shot | 6–8 | 80–150 ms | Light, dry, low-mid. Fires constantly — the most repetition-sensitive sound on the list. Get 8. | `footstep gravel`, `footstep dirt`, `foley step` |
| **Hit connect** | one-shot | 4–6 | 150–300 ms | Meaty impact + a bright transient. Should read as *escalation* — see the layer note below. | `punch impact`, `body hit`, `meat impact`, `whoosh hit` |
| **Jump** | one-shot | 3–4 | 100–200 ms | Effort exhale + cloth/scuff. Keep it quiet; it fires constantly. | `jump grunt`, `effort exhale`, `cloth movement` |

## Tier 2 — the building and combat systems

Where MTile stops sounding like a generic platformer.

| Sound | Kind | Var | Len | Character | Search terms |
|---|---|---|---|---|---|
| **Mass-ball paint hiss** | loop | 1–2 | 2–3 s | Granular pour / sand-stream. Gain rides the *funded* deposition rate, so it naturally stutters when the meter starves — that's a feature, don't smooth it. | `sand pour loop`, `granular flow`, `gravel pour`, `spray loop` |
| **Charge whine** | loop | 1 per phase | 2–4 s | Rising tonal. Needs three colours for `Ramping` / `Peak` / `Overheld` — the last should be actively unpleasant so overholding is audibly a mistake. | `charge up`, `energy whine`, `capacitor whine`, `rising tone` |
| **Peel tether tension** | loop | 1–2 | 2–3 s | Rope/cable creak under strain. `PeelStrain` is already 0..1, which maps directly to pitch+gain — an unusually clean parameter. | `rope creak`, `cable tension`, `stretch strain loop` |
| **Peel snap** | one-shot | 2–3 | 200–400 ms | Whip-crack release. The payoff for the tension loop; make it satisfying. | `rope snap`, `whip crack`, `cable break` |
| **Eruption fire** | one-shot | 3–4 | 400–800 ms | Low percussive whump + debris. The big one — should feel expensive. | `explosion debris`, `earth rumble`, `impact boom`, `rockfall` |
| **Tile place / commit** | one-shot | 4–6 | 100–200 ms | Soft set / click. Fires in bursts, so it must coalesce cleanly (`Deposit` returns a *count* — use it). | `stone place`, `block set`, `soft impact` |
| **Mass-ball whoosh** | loop | 1–2 | 1–2 s | Airy pass-by. Per-ball, so several can be live at once — cap it. | `whoosh loop`, `air movement`, `projectile fly` |
| **Crush damage** | one-shot | 2–3 | 300–500 ms | Heavy, sickening. This is the *only* HP loss in the game — it should be the scariest sound in the bank. | `bone crunch`, `heavy crush`, `impact damage` |

## Tier 3 — movement texture and polish

Each is small; collectively they're most of the "feel".

| Sound | Kind | Var | Len | Character | Search terms |
|---|---|---|---|---|---|
| **Wall jump** | one-shot | 3–4 | 150–250 ms | Scuff + push-off. | `wall kick`, `scuff push` |
| **Ledge grab** | one-shot | 3–4 | 100–200 ms | Hand slap on stone. | `hand slap stone`, `grab impact` |
| **Ledge pull / climb effort** | loop or one-shot | 2–3 | 400–800 ms | Exertion grunt + fabric. Drive the envelope off `AnimationProgress`. | `effort grunt`, `climb exertion` |
| **Double jump** | one-shot | 2–3 | 150–250 ms | Distinct from ground jump — airier, slight tonal lift. | `air jump`, `swoosh short` |
| **Crouch / dropdown** | one-shot | 2–3 | 100–200 ms | Cloth + light scuff. | `cloth rustle`, `crouch foley` |
| **Attack whiff** | one-shot | 4–6 | 200–400 ms | Whoosh, pitch-varied by attack type. | `whoosh swing`, `air swipe`, `blade whoosh` |
| **Stun / tumble** | loop | 1–2 | 1–2 s | Disoriented tonal wobble or body-tumble foley. | `dizzy tone`, `body tumble` |
| **Sprout growth hum** | loop | 1 | 2–3 s | Low organic/crystalline hum, gain by growing-count. | `growth hum`, `crystal resonance`, `low drone` |
| **Respawn** | one-shot | 1 | 500–800 ms | Bright reset. | `respawn`, `power up`, `materialize` |
| **Build reservoir empty** | one-shot | 1–2 | 150–300 ms | Dry click / negative. Must be unmissable but not annoying — it fires on player error. | `error click`, `empty click`, `denied` |
| **Mass-ball extinguish** | one-shot | 2–3 | 200–400 ms | Fizzle-out. | `fizzle`, `extinguish`, `dissipate` |

## Non-clip layers

Two things on `AUDIO_PLAN.md` §9's list are **not** sample-shaped and shouldn't be sourced
as clips:

- **Escalation tension layer** — driven by monotonic `DamagePercent`. Better as a
  continuous tonal bed whose filter/gain rise with damage, or as a global pitch offset
  applied to hit sounds, than as a clip. Cheapest real version: bias hit-connect pitch
  upward with `DamagePercent`, which gets the escalation read for zero new assets.
- **Hitstun / guard / grabbed** — states, better served by ducking/filtering the existing
  mix than by their own sounds. Defer until there's a bus structure.

---

## Sourcing

**Sonniss GDC Game Audio Bundle** — the primary recommendation. Free annual release,
professionally recorded, royalty-free, commercial use, no attribution required. Back
catalogue spans roughly 2015→present, tens of GB per year, heavy on exactly this palette
(impacts, debris, stone, rockfall, whoosh, metal). Caveat: raw library material — you're
slicing a 200 ms hit out of a 40-second field recording.

**Freesound.org** — huge and searchable, good for one-off oddities. **Filter to CC0** unless
you want to maintain an attribution ledger; licenses are per-file and quality varies a lot.

**Kenney.nl** — CC0, zero friction, stylized/generic. The right choice for **placeholder**
audio so Phase 0 isn't blocked on curation. Not your final sound.

**Avoid the BBC Sound Effects archive** despite frequent recommendation — the RemArc license
is personal/educational/research only, **not commercial**. This repo is public and deployed;
don't let those files in at all.

**Paid** if you want to skip curation: ZapSplat (cheap, drops attribution), Soundly,
A Sound Effect. Probably unnecessary at this stage.

**Record your own crunch.** For a terrain-destruction game the signature sounds — chipping,
crumbling, cascade, mass settling — are unusually easy to foley. Gravel in a tray, breaking
celery, crushed ice, a bag of sand dropped on concrete. A phone recording of the *right
physical action* beats a well-recorded generic "rock break", and it's the one area where DIY
plausibly outperforms the library.

> Licensing terms and download availability change. Confirm current Sonniss terms and which
> years are still hosted before building a pipeline on them.

## Workflow

**Cut by hand, convert by script.** Slicing is a judgement call per file; everything after it
is mechanical and should never be done by hand across 150 files.

1. **Slice** in [Audacity](https://www.audacityteam.org/) (free — fine for trim/fade/export,
   which is the whole job) or [Reaper](https://www.reaper.fm/) ($60, 60-day full eval — worth
   it at this file count for its batch converter and render matrix). Cut tight to the
   transient: for a one-shot the first sample should be nearly the loudest, or every trigger
   feels late. Leave the tail — that is where the character is.
2. **Drop the slices in `Audio/raw/`.**
3. **Convert** with [`scripts/build-sfx.ps1`](../scripts/build-sfx.ps1) (needs ffmpeg —
   `winget install Gyan.FFmpeg`):

   ```powershell
   pwsh scripts/build-sfx.ps1 -Name tile_break -DryRun   # print the plan + filter chain
   pwsh scripts/build-sfx.ps1 -Name tile_break           # -> tile_break_01.ogg, _02, ...
   pwsh scripts/build-sfx.ps1 -Loop -Name wall_scrape    # loops: no trim, no end fade
   ```

   Per file it strips leading silence, loudness-matches to −16 LUFS, applies a 5 ms tail fade,
   and writes mono 22.05 kHz Ogg to `Assets/Sounds/`. `-Loop` disables the trim and the fade,
   both of which would move or break the loop point.

**Loop points are the one fiddly part.** `IsLooped` just wraps to the start, so any
discontinuity clicks once per cycle and no amount of ffmpeg fixes it. Crossfade the file onto
itself in the editor: take the last ~200 ms, overlap onto the head, crossfade, trim. Audition
looped for ~30 s — a click you cannot hear once is obvious after ten repeats. Sourcing tip
that sidesteps this: take a *steady* section from the middle of a long recording rather than
one with an obvious contour; steady material crossfades invisibly.

**Layer offline only when the mix is fixed.** A tile break is always crack + debris tail —
bake it into one file. A landing wants its thud and impact-crunch balanced differently by
impact hardness — keep those separate and let the mixer blend. Baking a mix you later need to
vary is the expensive mistake.

**Pitch is a runtime concern**, via `SoundEffectInstance.Pitch`. Two caveats: the range is
−1..1 (one octave down/up), and it is implemented as resampling, so it changes *duration*
too. Fine for the ±5% jitter that defeats repetition; wrong if you need a pitched sound to
keep its length — for that, shift offline with SoX (`sox in.wav out.wav pitch 200`, cents,
duration-preserving). Pitch is also on the KNI-unverified list (`AUDIO_PLAN.md` §7 risk 1),
so keep it behind the narrow mixer interface.

## Format

Decided by `AUDIO_PLAN.md` §8's open pipeline-vs-raw question, but independent of it:

- **Mono** for anything positional. Stereo doubles size for nothing on a panned point source.
- **22–32 kHz** for short impacts; nobody will hear the difference and it's a straight
  halving.
- **Ogg Vorbis on web.** `Content.mgcb:9` is `/compress:False`, so pipeline-built `.xnb`
  audio ships as uncompressed PCM — a few MB of that is not noise against the payload.
- **`SoundEffect.FromStream`, never `FromFile`** — `FromFile` does not exist in KNI
  (`AUDIO_PLAN.md` §7a). Applies to the raw-asset route.
- Keep raw/uncompressed masters out of the shipping path.

## Budget

Tier 1 alone is ~30 files. All three tiers plus variants is ~120–150 files. At Ogg mono
22 kHz that is a few hundred KB total — negligible against the ~59 MB published wwwroot.
**Sourcing time, not payload size, is the real cost.**
