# SFX candidates — Sonniss GDC 2026 triage

Working table for turning the Sonniss bundle into game-ready SFX. Companion to
[`AUDIO_ASSET_LIST.md`](AUDIO_ASSET_LIST.md) (what we need) and [`AUDIO_PLAN.md`](AUDIO_PLAN.md)
(how it gets played).


## Candidates by target sound

Paths are relative to `Audio/candidates/`. Durations are measured from the RIFF headers. All files
are 96 or 192 kHz 24-bit stereo, which is why they are large — that is *good* for slicing and
pitching (lots of headroom), and `build-sfx.ps1` collapses it to mono 22.05 kHz Ogg at the end.

### `tile_break/` → **tile break** (one-shot, 6–8 variants, ~0.2–0.4 s)

Tier 1, and the most-heard sound in the game. Everything here is a substitute — see above.

| File | Len | Why it's here |
|---|---|---|
| `ice, movement, ice drift, ice field cracking up, initial, wide-001` | 83.3 s | **Start here.** A long field recording of ice sheet failing — likely to contain dozens of distinct cracks. One file could yield the whole variant set, which is exactly what you want for round-robin (same room, same mic, natural variation). |
| `ice, crack, ice block snapping-001` | 5.9 s | Sharp snap. Closest thing to a clean "block gives way". |
| `ice, block of ice crushed, heavy-015` | 2.8 s | Heavier, crunchier — candidate for breaking a tough/reinforced tile. |
| `ice, surface cracking, fissure, fast, hard-003` | 5.0 s | Fast fissure; possibly better as a *crack-before-break* tell than the break itself. |
| `GORESplt_Gore Designed Transient Heavy Impact Smash 01` | 1.8 s | Designed transient smash. Gore-library origin, but a heavy designed impact is a heavy designed impact. |
| `GLASMvmt_Whoosh Glass Crystal Fragments Sharp Shards Dry 05` | 2.6 s | "Dry" is promising — shards without reverb layer well under a stone body. |
| `ICEBrk_Skill Freeze Whoosh Break Impact Layered Movement Shatter 03` | 2.5 s | Already game-designed and layered. May be too whooshy/magical on its own. |
| `Woosh Debris` | 2.4 s | Debris scatter — a **tail layer** to sit under a break transient, not a break by itself. |
| `EXPLDsgn_Explosion Small Blast ... Crunchy Boom Cartoon Noisy Crash` | 1.5 s | "Cartoon" in the name is a warning. Possible fallback for a big multi-tile break. |
| `Accept Boing Crunch` | 1.0 s | Long shot. Included because "crunch" is rare in this bundle and it's tiny. |

**Second wave — picked by character (see above).** These are the reason the stone gap may be less
severe than the first pass concluded:

| File | Len | Why it's here |
|---|---|---|
| `13 Fireworks_powerful explosions_multiples in a row_near` | 24.2 s | **Most likely the one you heard** — "multiples in a row" + "near" means discrete, close, un-reverbed cracks in sequence. That is structurally a rubble collapse. |
| `02 Fireworks_ explosions_dense_whistles` | 97.5 s | Dense, and long enough to mine many variants. The whistles are the problem — you'd be cutting around them. |
| `SBfa_Fireworks 001` | 44.9 s | Third fireworks take, no whistles named. |
| `AMBCnst_Baltimore Construction Streetside Heavy Machinery And Jakchammers 01` | 109.8 s | **Jackhammers on concrete — the literal sound of masonry being destroyed**, and conceptually the closest thing in the entire bundle to tile break. It's a street ambience, so it's distant and traffic-washed; the question is whether any moment is isolated enough to cut. |
| `HAIL_Hail on Door Window, UVPC` | 66.0 s | Hard granular impacts on a resonant surface — dozens of tiny discrete transients. Good source for *small* debris pings, and for the tail after a break. |
| `24 Campfire, Dropping Fresh Pine Branches in Fire, Crackling, Sizzling Strong, Close 02` | 120.6 s | Close-mic'd crackle, 2 minutes of it. Fire pops are sharp broadband transients; pitched down they read as splintering. |
| `FIREBurn_Loop Elements Fire Crackling Crunchy Flame Burn 03` | 12.4 s | Already loop-prepared and tagged "crunchy". Small file, cheap to check. |
| `ANMLRept_Dinosaur Eating Meat 01` | 19.5 s | Bone crunch. Unpleasant provenance, right texture — this is exactly the kind of file that only turns up by character search. |
| `OBJMisc_Hair Scissor, Snips 4` | 5.2 s | Sharp dry snips. Candidate for a *small* tile cracking rather than a full break. |

### `debris_scatter/` → break **tail** layer

Not a break in itself — the settling/scattering that follows one. Layer under the transient.

| File | Len | Why it's here |
|---|---|---|
| `FOODMisc_Broom Sweeping Up Snacks On Hard Floor 2` | 6.0 s | Literally small hard fragments being pushed across a hard floor. |
| `PAPRMisc_Pile Of Antique Books Falling Over 8` | 2.8 s | A stack collapsing — the *gesture* of a structure giving way, in the wrong material. |
| `GAMEBoard_Event Board Reset Organic Multiple Pieces Wood Small 02` | 1.1 s | Multiple small pieces landing. Short and dry. |
| `Newspaper Static Foley Rummage` | 5.1 s | Fine crackle. High-frequency filler over a heavier body. |
| `MAGMisc_Wrapping Paper, Opening Present 1` | 5.6 s | Same idea, coarser. |

### `scrape_loop/` → **wall-slide scrape** (loop) + peel/drag texture

Tier 1. Needs a clean loop point — see the loop-crossfade note in `AUDIO_ASSET_LIST.md`.

| File | Len | Why it's here |
|---|---|---|
| `METLFric_Large Metal Box, Drag, Geofon` | 67.3 s | **Best loop source here** — long, sustained, continuous drag. Long recordings are what you want for loops; short ones force an awkward splice. Geofon (contact mic) means low-end body. |
| `WOODFric_Wood Shaker ... Table Alternate Grainy 05` | 4.2 s | "Grainy" is the right texture for stone-on-stone. |
| `GOREMisc_Cladding_Scratch06` | 1.7 s | Cladding = building material. Likely the most *stone-adjacent* scrape in the bundle. |
| `GOREMisc_Cladding_NailScratch19` | 1.0 s | Same library, sharper. |
| `GOREMisc_Concrete_MetalPipe02` | 0.4 s | **The only file in all 301 with "Concrete" in the name.** Short, but worth hearing given the stone gap. |
| `ICEFric_Dry Ice High Metal Squeal Groan Bright Squeak Dissonant Short 13` | 3.9 s | Squeal/groan — probably too tonal for a scrape, but good if you want the slide to feel *painful*. |
| `ICEFric_Dry Ice Squeak Metal ... Short 07` | 2.2 s | Same family, shorter. |
| `METLFric_SWING SCRAPE Swift Melee Weapon ... Long Blade 14` | 2.4 s | A swing, not a sustained scrape — likelier fit for a **slash** than a wall slide. |
| `OBJMisc_ScratchCard_SurfaceWipe_04` | 1.1 s | Fine-grained surface wipe. Candidate for a *light* scrape at low speed. |
| `OBJMisc_ScratchCard_CoinTapScratch_01` | 1.1 s | Same, with tap transients. |

### `hit_impact/` → **hit connect** (one-shot, 4–6 variants)

Tier 1. The best-served category in the bundle.

| File | Len | Why it's here |
|---|---|---|
| `SWSH_SWING IMPACTS Quick Heavy Weapon Swing To Thud Impact Var 01` | 61.8 s | **Start here.** "Var" + 62 s means a whole take of variants in one file — a full round-robin set from a single consistent source. |
| `FGHTImpt_4 x Punch, Body 02` | 3.9 s | Four punches in one file — four variants, cleanly. |
| `FGHTImpt_Combat Punch Impact Light Hit ... Crunchy Vintage Quick Smack 05` | 1.1 s | Light hit. Good for the low end of the escalation curve. |
| `METLImpt_METAL SWING HIT ... Metallic Body Impact And Resonant Tail 01` | 2.9 s | Resonant tail — good for a heavy/charged hit. |
| `METLImpt_Metal Old File Impact Tap Against Tire Iron` | 0.5 s | Short dry tap. Useful as a *layer* on top of a body hit. |
| `WEAPBlnt_Spear And Stick Impact, Wooden MKH 2` | 11.3 s | Multi-take blunt impacts. |
| `WOODImpt_Hit Blood Spill Splat ... Squelch Small Thump 03` | 0.3 s | Very short thump. The squelch may or may not suit the game's tone. |
| `Impact Cut Sweep` | 2.8 s | Designed, sweepy. More of a transition than a hit. |
| `Impact Hit Rapid Chord Reverb` | 3.2 s | Tonal — candidate for a **big** moment (KO), not a normal hit. |
| `CREAHmn_Designed Orc Male Attack Long Heavy Hit Charged Up 03` | 2.6 s | Has a vocal component. Only useful if the player character gets a voice. |
| `Arrow Hit Rattle` | 1.1 s | Long shot; a rattle tail could layer under a tile hit. |

### `land_place_thunk/` → **landing thud** + **tile place**

Tier 1 (landing) and tier 2 (place). Thin — none of these is obviously right.

| File | Len | Why it's here |
|---|---|---|
| `OBJLug_CaseDown_Concrete12` | 1.5 s | A weighted object set down **on concrete**. The most plausible landing-thud source in the bundle. |
| `MECHLtch_Click Deep Mechanism Latch ... Nearfield Thunk 02` | 0.4 s | Short deep thunk. Good candidate for **tile place** — placement wants a crisp confirm, not a boom. |
| `GAMEBoard_Game Play Piece ... Fall Bounce 04` | 0.2 s | Tiny, dry, organic. Alternative tile-place confirm. |
| `METLTonl_Item Spring Wire Impact Flick Top Clatter ... Roll Handling Short 01` | 11.5 s | Clatter/roll — a **debris tail** after a landing or break, not the impact itself. |

### `whoosh_jump/` → **jump**, **mass-ball whoosh**, slash swing

Tier 1 (jump) and tier 2 (mass ball).

| File | Len | Why it's here |
|---|---|---|
| `WINDDsgn_Wind, Rush, Whoosh, Long x5 01` | 1.1 s | **x5** — five whooshes, five variants. Clean airy rush, probably the best plain jump/swing source here. |
| `METLMisc_Metal, Slow Whoosh, Rattle, Pass By x4 01` | 1.2 s | x4 variants with a rattle — fits a *mass ball* (something solid moving) better than a bare jump. |
| `Woosh Sweep Slide Infographics Basic` | 0.5 s | Clean, short, neutral. Least characterful, which for a jump is often correct. |
| `DSGNStngr_Action Deploy Units Sword Slice Special Move Layered Swish 04` | 2.5 s | Layered game-designed swish — candidate for the **slash** action. |
| `WEAPSwrd_Sword Slide Cuts, Metallic, Impact CM4 2` | 13.0 s | Multi-take sword cuts. Slash variants. |
| `WEAPWhip_WHIP Snap Crack 05` | 1.5 s | Sharp crack — could serve **peel snap** as well as a fast slash. |
| `FIREWhsh_Whoosh Fire Deep Growl Monster Saturated Crisp 03` | 2.5 s | Heavy/saturated. Fits a charged eruption launch. |
| `Cofetti Whoosh Pluck Spill` | 2.5 s | Whoosh into a granular spill — possible **mass-ball burst**. |
| `Vibrato Impact Snap Spin Transition` | 3.2 s | Designed transition. Probably too musical for gameplay. |

### `charge_hiss/` → **charge whine** (3 phases) + **paint hiss** (loop)

Tier 2.

| File | Len | Why it's here |
|---|---|---|
| `OBJMisc_Spray Bottle, Spray 1` | 0.5 s | **Literal spray hiss** — the obvious paint-stroke source. Short, so a sustained paint loop needs it stretched or looped. |
| `OBJMisc_Hair Dryer, On, Idle, Off 4` | 12.3 s | On/idle/off in one file — that is *exactly* the three-phase shape a charge needs (attack / sustain loop / release). Filtered hard, a hairdryer is a perfectly good sustained-air bed. |
| `ELECArc_ArcPowerUpDesign04` | 7.5 s | A designed power-up ramp. Direct candidate for **charge whine**. |
| `ROBTMvmt_Tower Deploy Hitech Robot Motor Dark Thump Servo Whine 04` | 1.0 s | Servo whine + thump. Good for a mechanical charge/deploy. |
| `ELECBuzz_Buzz27` | 9.8 s | Long sustained buzz — loop source for a held charge. |
| `ELECArc_ArcDesign15` | 1.0 s | Short arc. Release/discharge candidate. |
| `DSGNBass_Jump Start Drop 3` | 3.7 s | Ramp into a drop — the "charge releases" shape. |
| `ELECMisc_Impact Electric Tonal Deep Movement Motion Hiss Glitch 01` | 3.7 s | Glitchy; likely too busy, but has hiss content. |

### `peel_tension_snap/` → **peel tension → snap**

Tier 2. A two-part sound: rising tension, then release. Best-matched category in the bundle.

| File | Len | Why it's here |
|---|---|---|
| `OBJTape_VelcroSqueeze01` | 6.7 s | **Velcro is the classic tension sound** — sustained tearing that rises as it goes. Long enough to stretch across a variable-length peel. |
| `OBJTape_VelcroRip29` | 0.4 s | The release. Pairs directly with the above. |
| `MECHMisc_Tool Tape Measure Pull Retract Spring Slide Spin Long 07` | 3.3 s | Pull-with-resistance then snap-back. Structurally the exact shape of a peel. |
| `OBJMisc_CrackerPull_WithBang_Effected_03` | 0.5 s | Tension-then-bang in one gesture. |
| `Transition Frantic Shaker Snap` | 1.5 s | Ends on a snap. Candidate for the release transient alone. |

### `eruption_rumble/` → **eruption**, **crush**, low-end body

Tier 2. Well served — trailer libraries are all low-end.

| File | Len | Why it's here |
|---|---|---|
| `THUN_Interior Thunder Rumble` | 18.6 s | Long natural rumble. Best **sub-layer** under an eruption; thunder recorded indoors has body without rain noise. |
| `EffectiveTrailer_Booms_Vol2_011` / `_075` / `_214` | 17.3 / 15.5 / 16.3 s | Three trailer booms — pick one for the eruption transient. Long tails; you will want to cut them short. |
| `DSGNBass_Rattling Downer 3` | 5.6 s | Rattle + descending bass. Fits terrain collapsing. |
| `DSGNBass_Bass Drop & Downer Fast 16` | 2.1 s | Fast drop — tighter, likelier to fit a 60 fps action beat than the 16 s booms. |
| `AEROJet_Blast Off Clean` | 8.7 s | Sustained launch. Candidate for eruption *launch* rather than impact. |

### `strain_grab/` → **grabbed / strain**

| File | Len | Why it's here |
|---|---|---|
| `FGHTGrab_Choking, Tension 03` | 8.9 s | The only grab-tagged file in the bundle. Note `AUDIO_ASSET_LIST.md` argues grabbed state is better expressed as **ducking**, not a clip — so this may be unnecessary. |

### `ui/` → menu, room-code entry, accept/deny

Not in the asset list (which is gameplay-scoped), but browser PvP has a lobby, and these are tiny.

| File | Len | Why it's here |
|---|---|---|
| `UIClick_UI Button Analog Vintage Double Click Neutral Dry Press 11` | 0.2 s | Neutral dry press — the default click. |
| `Interface Accept Glassy Snap` | 0.4 s | Accept / room joined. |
| `Interface Percussion Snap` | 0.7 s | Alternative confirm. |
| `Deny Muted` / `Interface Deny Low Fat Dark` | 0.8 / 0.7 s | Rejected room code, invalid input. |
| `UIAlert_Collect Scifi Futuristic Electronic Bass Burst Sweep Heavy 04` | 0.3 s | Match-found / opponent-connected sting. |

### `cloth_movement/` → tier-3 movement texture

| File | Len | Why it's here |
|---|---|---|
| `FOLYClth_ClothMovement24` / `_29` | 2.3 / 1.5 s | Cloth rustle for run/land layering. |
| `FOLYClth_SinglePats04` | 0.6 s | Single pat — a per-step cloth accent. |

---

## What to do next

1. **Install ffmpeg** — `winget install Gyan.FFmpeg`, then restart the shell. Nothing downstream
   (`build-sfx.ps1`, any measurement I can do for you) works without it. It is not installed today.
2. **Audition the "start here" files first** — each plausibly yields a full variant set from a
   single consistent source, which is what round-robin wants:
   `13 Fireworks ... multiples in a row_near` and `ice field cracking up` (tile break),
   `SWSH SWING IMPACTS ... Var 01` (hit), `Large Metal Box, Drag` (scrape loop),
   `Wind Rush Whoosh Long x5` (jump).
3. **Decide the stone question.** Either source a rock/rubble pack, record your own, or build it
   from the character-matched sources above (fireworks + hail + a sub layer is a completely
   legitimate way to make a rubble break, and arguably better than a literal recording — real rock
   breaking is often disappointingly dull). This is a design call, not a sourcing one, and it
   defines what the game *sounds* like.
4. **Slice by hand** into `Audio/raw/`, then `pwsh scripts/build-sfx.ps1 -Name tile_break`.
5. **Reclaim disk.** Once slices are cut, `Audio/candidates/` can be deleted and re-extracted later
   with `scripts/extract-sfx-candidates.ps1`. The 6.6 GB of zips can go too once you are done
   mining them — but keep at least a record of which library each shipped sound came from, since
   the Sonniss license is per-library.

Nothing in `Audio/candidates/` has been converted, sliced, or modified — they are byte-identical
extractions from the zips.
