# Extract hand-picked Sonniss candidates into Audio/candidates/<group>/.
# Matches by distinctive substring; fails loudly if a pattern is ambiguous or missing.
param([switch]$DryRun)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = "c:\Users\amdic\dev\MTile"
$dest = Join-Path $repo "Audio\candidates"

# group => list of distinctive substrings
$picks = [ordered]@{
  "tile_break" = @(
    "block of ice crushed",
    "ice block snapping",
    "surface cracking, fissure",
    "ice field cracking up",
    "ICEBrk_Skill Freeze Whoosh Break",
    "GLASMvmt_Whoosh Glass Crystal Fragments",
    "Woosh Debris",
    "GORESplt_Gore Designed Transient Heavy Impact Smash",
    "EXPLDsgn_Explosion Small Blast",
    "Accept Boing Crunch",
    # Second wave, picked by ACOUSTIC CHARACTER rather than by category name.
    # Dense stochastic crackle == the granular signature of rubble, regardless of
    # what actually made the sound. The first pass missed all of these because it
    # searched for "rock"/"stone"/"debris" and none of them say that.
    "13 Fireworks_powerful explosions_multiples in a row_near",
    "Fireworks_ explosions_dense",
    "SBfa_Fireworks 001",
    "HAIL_Hail on Door Window",
    "FIREBurn_Loop Elements Fire Crackling Crunchy",
    "24 Campfire, Dropping Fresh Pine Branches",
    "AMBCnst_Baltimore Construction",
    "ANMLRept_Dinosaur Eating Meat",
    "OBJMisc_Hair Scissor, Snips"
  )
  "debris_scatter" = @(
    "FOODMisc_Broom Sweeping Up Snacks On Hard Floor",
    "PAPRMisc_Pile Of Antique Books Falling Over",
    "GAMEBoard_Event Board Reset Organic Multiple Pieces Wood",
    "Newspaper Static Foley Rummage",
    "MAGMisc_Wrapping Paper, Opening Present"
  )
  "scrape_loop" = @(
    "Cladding_NailScratch",
    "Concrete_MetalPipe",
    "Cladding_Scratch",
    "METLFric_SWING SCRAPE",
    "Large Metal Box, Drag, Geofon",
    "WOODFric_Wood Shaker",
    "Dry Ice High Metal Squeal",
    "Dry Ice Squeak Metal",
    "ScratchCard_SurfaceWipe",
    "ScratchCard_CoinTapScratch"
  )
  "hit_impact" = @(
    "FGHTImpt_Combat Punch Impact Light Hit",
    "FGHTImpt_4 x Punch, Body",
    "METAL SWING HIT Weapon Swing",
    "Metal Old File Impact Tap",
    "WOODImpt_Hit Blood Spill Splat",
    "WEAPBlnt_Spear And Stick Impact",
    "SWSH_SWING IMPACTS Quick Heavy",
    "Impact Cut Sweep",
    "Impact Hit Rapid Chord Reverb",
    "Arrow Hit Rattle",
    "CREAHmn_Designed Orc Male Attack"
  )
  "land_place_thunk" = @(
    "MECHLtch_Click Deep Mechanism Latch",
    "CaseDown_Concrete",
    "GAMEBoard_Game Play Piece Action Organic",
    "METLTonl_Item Spring Wire Impact Flick"
  )
  "whoosh_jump" = @(
    "WINDDsgn_Wind, Rush, Whoosh, Long",
    "METLMisc_Metal, Slow Whoosh, Rattle",
    "FIREWhsh_Whoosh Fire Deep Growl",
    "DSGNStngr_Action Deploy Units Sword Slice",
    "WEAPSwrd_Sword Slide Cuts",
    "Woosh Sweep Slide Infographics",
    "Cofetti Whoosh Pluck Spill",
    "Vibrato Impact Snap Spin Transition",
    "WEAPWhip_WHIP Snap Crack"
  )
  "charge_hiss" = @(
    "ROBTMvmt_Tower Deploy Hitech Robot Motor",
    "ArcPowerUpDesign",
    "ArcDesign15",
    "ELECBuzz_Buzz27",
    "DSGNBass_Jump Start Drop",
    "ELECMisc_Impact Electric Tonal Deep Movement",
    "Spray Bottle, Spray",
    "Hair Dryer, On, Idle, Off"
  )
  "peel_tension_snap" = @(
    "VelcroRip",
    "VelcroSqueeze",
    "Transition Frantic Shaker Snap",
    "MECHMisc_Tool Tape Measure Pull Retract",
    "CrackerPull_WithBang"
  )
  "eruption_rumble" = @(
    "THUN_Interior Thunder Rumble",
    "EffectiveTrailer_Booms_Vol2_075",
    "EffectiveTrailer_Booms_Vol2_214",
    "EffectiveTrailer_Booms_Vol2_011",
    "DSGNBass_Rattling Downer",
    "DSGNBass_Bass Drop & Downer Fast 16",
    "AEROJet_Blast Off Clean"
  )
  "strain_grab" = @(
    "FGHTGrab_Choking, Tension"
  )
  "ui" = @(
    "UIClick_UI Button Analog Vintage",
    "Interface Percussion Snap",
    "Interface Accept Glassy Snap",
    "Deny Muted",
    "Interface Deny Low Fat Dark",
    "UIAlert_Collect Scifi Futuristic"
  )
  "cloth_movement" = @(
    "ClothMovement29",
    "ClothMovement24",
    "SinglePats04"
  )
}

# Index every entry once.
$index = @{}
$order = New-Object System.Collections.Generic.List[object]
foreach ($z in Get-ChildItem "$repo\Assets\Sounds\*.zip") {
    $arc = [System.IO.Compression.ZipFile]::OpenRead($z.FullName)
    foreach ($e in $arc.Entries) {
        if ($e.FullName -like "*.wav") {
            $order.Add([pscustomobject]@{ Zip = $z.FullName; Path = $e.FullName; MB = [math]::Round($e.Length/1MB,2) })
        }
    }
    $arc.Dispose()
}

$plan = New-Object System.Collections.Generic.List[object]
$bad  = New-Object System.Collections.Generic.List[string]

foreach ($group in $picks.Keys) {
    foreach ($pat in $picks[$group]) {
        $hits = @($order | Where-Object { $_.Path -like "*$pat*" })
        if ($hits.Count -ne 1) {
            $bad.Add("[$group] '$pat' matched $($hits.Count)")
            continue
        }
        $h = $hits[0]
        $plan.Add([pscustomobject]@{
            Group = $group
            Zip   = $h.Zip
            Path  = $h.Path
            MB    = $h.MB
            File  = [System.IO.Path]::GetFileName($h.Path)
        })
    }
}

if ($bad.Count -gt 0) {
    Write-Host "AMBIGUOUS/MISSING:" -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
}

$totalMB = [math]::Round(($plan | Measure-Object MB -Sum).Sum, 1)
Write-Host "Planned: $($plan.Count) files, $totalMB MB" -ForegroundColor Cyan
if ($DryRun) { $plan | Group-Object Group | ForEach-Object { "  {0,-18} {1,2} files" -f $_.Name, $_.Count }; return }

# Extract, grouping open archives to avoid reopening per file.
foreach ($zg in $plan | Group-Object Zip) {
    $arc = [System.IO.Compression.ZipFile]::OpenRead($zg.Name)
    foreach ($p in $zg.Group) {
        $outDir = Join-Path $dest $p.Group
        if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
        $outFile = Join-Path $outDir $p.File
        if (Test-Path $outFile) { continue }
        $entry = $arc.GetEntry($p.Path)
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $outFile, $true)
    }
    $arc.Dispose()
    Write-Host "  done: $([System.IO.Path]::GetFileName($zg.Name))"
}
$plan | Export-Csv "$PSScriptRoot\candidates.csv" -NoTypeInformation -Encoding UTF8
Write-Host "Extracted to $dest" -ForegroundColor Green
