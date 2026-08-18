# Batch-convert raw sound slices into game-ready SFX.
#
#   .\scripts\build-sfx.ps1                          # Audio/raw -> Assets/Sounds
#   .\scripts\build-sfx.ps1 -Name tile_break         # rename+number: tile_break_01.ogg, _02, ...
#   .\scripts\build-sfx.ps1 -Loop                    # loops: no silence trim, no end fade
#   .\scripts\build-sfx.ps1 -DryRun                  # print the plan, touch nothing
#
# Takes hand-cut slices (from Audacity/Reaper, out of the Sonniss bundle or wherever)
# and produces the format Plans/AUDIO_ASSET_LIST.md specifies: mono, 22.05 kHz, Ogg
# Vorbis, loudness-matched.
#
# Loudness matching is the point. Round-robin variants that differ in perceived
# loudness read as a bug rather than as variety, and peak normalization does not
# match perceived loudness - so this normalizes by LUFS (EBU R128) instead.
#
# Author-time only. Pitch jitter, gain, pan and round-robin selection all happen at
# runtime in the mixer (Plans/AUDIO_PLAN.md ss5) - do not bake them in here.
#
# Requires ffmpeg on PATH:  winget install Gyan.FFmpeg   (then restart the shell)
param(
    [string]$In      = "Audio/raw",
    [string]$Out     = "Assets/Sounds",
    [string]$Name    = "",
    [int]   $Rate    = 22050,
    [double]$Lufs    = -16.0,
    [int]   $Quality = 4,          # libvorbis -q:a, 0..10. 4 ~= 128kbps, plenty for SFX.
    [switch]$Loop,
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

foreach ($exe in @("ffmpeg", "ffprobe")) {
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) {
        throw "$exe not found on PATH. Install with: winget install Gyan.FFmpeg (then restart the shell)"
    }
}

# Resolve relative paths against the repo root, not the caller's cwd.
if (-not [System.IO.Path]::IsPathRooted($In))  { $In  = Join-Path $repo $In }
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $repo $Out }

if (-not (Test-Path $In)) {
    throw "Input folder not found: $In`nPut your hand-cut slices there (.wav/.flac/.aif/.mp3)."
}

$sources = Get-ChildItem -Path $In -File |
    Where-Object { $_.Extension -match '^\.(wav|flac|aif|aiff|mp3|ogg|m4a)$' } |
    Sort-Object Name

if ($sources.Count -eq 0) { throw "No audio files in $In" }

if (-not $DryRun -and -not (Test-Path $Out)) {
    New-Item -ItemType Directory -Path $Out -Force | Out-Null
}

$kind = "one-shot"
if ($Loop) { $kind = "loop" }
Write-Host "== build-sfx: $($sources.Count) file(s), $kind, mono ${Rate}Hz ogg q$Quality @ ${Lufs} LUFS ==" -ForegroundColor Cyan
Write-Host "   in : $In"
Write-Host "   out: $Out"
if ($Loop) {
    Write-Host "   loop mode: silence trim and end fade disabled (they would break the loop point)" -ForegroundColor DarkGray
}
Write-Host ""

$results = @()
$i = 0

foreach ($src in $sources) {
    $i++

    if ($Name -ne "") {
        $stem = "{0}_{1:d2}" -f $Name, $i
    } else {
        # Sanitize: lowercase, spaces/punctuation to underscore. Sonniss filenames are long
        # and full of spaces and hyphens.
        $stem = $src.BaseName.ToLower() -replace '[^a-z0-9]+', '_' -replace '^_+|_+$', ''
    }
    $dst = Join-Path $Out "$stem.ogg"

    if ((Test-Path $dst) -and -not $Force -and -not $DryRun) {
        Write-Host ("  skip  {0,-28} (exists, use -Force)" -f "$stem.ogg") -ForegroundColor DarkGray
        continue
    }

    # Probe duration so the end fade can be placed. ffprobe writes the value to stdout.
    $durRaw = & ffprobe -v quiet -of csv=p=0 -show_entries format=duration -- "$($src.FullName)"
    $dur = 0.0
    if ($durRaw) { [double]::TryParse($durRaw.Trim(), [ref]$dur) | Out-Null }

    # Filter chain, in order:
    #   silenceremove - strip leading silence. A one-shot with lead-in silence feels late
    #                   on every trigger. Loops skip this (it would move the loop point).
    #   loudnorm      - EBU R128 loudness match. Single-pass: less exact than two-pass but
    #                   well within tolerance for short SFX, and half the runtime.
    #   afade         - 5ms tail fade to kill the end click from a hard cut. Loops skip it.
    $filters = @()
    if (-not $Loop) {
        $filters += "silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.005"
    }
    $filters += ("loudnorm=I={0}:TP=-1.5:LRA=11" -f $Lufs)
    if (-not $Loop -and $dur -gt 0.05) {
        $fadeStart = [math]::Round($dur - 0.005, 4)
        $filters += ("afade=t=out:st={0}:d=0.005" -f $fadeStart)
    }
    $chain = $filters -join ","

    if ($DryRun) {
        Write-Host ("  plan  {0,-28} <- {1}" -f "$stem.ogg", $src.Name)
        Write-Host ("        {0}" -f $chain) -ForegroundColor DarkGray
        continue
    }

    & ffmpeg -hide_banner -loglevel error -nostdin -y `
        -i "$($src.FullName)" `
        -af $chain `
        -ac 1 -ar $Rate `
        -c:a libvorbis -q:a $Quality `
        -- "$dst"

    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $($src.Name)" }

    $inKb  = [math]::Round($src.Length / 1kb, 1)
    $outKb = [math]::Round((Get-Item $dst).Length / 1kb, 1)
    Write-Host ("  ok    {0,-28} {1,7} KB -> {2,6} KB   {3,5:n2}s" -f "$stem.ogg", $inKb, $outKb, $dur) -ForegroundColor Green

    $results += [pscustomobject]@{ Name = "$stem.ogg"; InKB = $inKb; OutKB = $outKb; Seconds = [math]::Round($dur, 2) }
}

Write-Host ""
if ($DryRun) {
    Write-Host "Dry run - nothing written." -ForegroundColor Yellow
} elseif ($results.Count -gt 0) {
    $totalKb = [math]::Round(($results | Measure-Object -Property OutKB -Sum).Sum, 1)
    Write-Host ("Wrote {0} file(s), {1} KB total." -f $results.Count, $totalKb) -ForegroundColor Cyan
    if ($Loop) {
        Write-Host "Loop check: play each one looped for ~30s. A click you cannot hear once is obvious after ten repeats." -ForegroundColor DarkGray
        Write-Host "If it clicks, crossfade the file onto itself in the editor - ffmpeg cannot fix a bad loop point here." -ForegroundColor DarkGray
    }
    Write-Host "Note: MTile.Web.csproj StageGameAssetsToWwwroot has no copy rule for audio yet" -ForegroundColor DarkGray
    Write-Host "      (Plans/AUDIO_PLAN.md ss8) - these will not reach the web build until it does." -ForegroundColor DarkGray
}
