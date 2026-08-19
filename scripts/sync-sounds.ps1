<#
.SYNOPSIS
  Wire the built sound clips in Assets/Sounds into the game.

.DESCRIPTION
  Scans Assets/Sounds/*.ogg, then regenerates two things so a new clip needs no
  hand-editing anywhere:

    1. The GENERATED SOUNDS region of every .mgcb in -Mgcb — one pipeline entry per
       clip, built out-of-tree from ../Assets/Sounds and mapped to the asset name
       "Sounds/<stem>". Both toolchains (MonoGame 3.8.4.1 desktop, KNI 4.1.9001 web)
       have OggImporter + SoundEffectProcessor, so the same entries serve both.

       Content.Mac.mgcb is in that list because macOS builds a shader-free content set
       (mgfxc is a Windows binary needing Wine). It is a local, untracked file on most
       machines, so a target that does not exist is skipped rather than created — the
       sound entries are identical across variants, only the .fx entries differ.
    2. Audio/SoundManifest.g.cs — the compile-time (stem, variant count) table
       SoundBank loads from. Generated rather than read at runtime because the WASM
       host cannot enumerate a content directory.

  Naming: <stem>.ogg for a single clip, or <stem>_01.ogg, <stem>_02.ogg, … for
  round-robin variants. That is exactly what build-sfx.ps1 -Name <stem> produces.

  A stem with no matching entry in SoundKind is reported — it will build but nothing
  will ever play it until a SoundKind + stem exist.

.EXAMPLE
  pwsh scripts/build-sfx.ps1 -Name tile_break   # Audio/raw -> Assets/Sounds/*.ogg
  pwsh scripts/sync-sounds.ps1                  # -> mgcb entries + manifest
  dotnet run --project MTile.Desktop
#>
[CmdletBinding()]
param(
    [string]$SoundDir = "Assets/Sounds",
    [string[]]$Mgcb   = @("Content/Content.mgcb", "Content/Content.Mac.mgcb"),
    [string]$Manifest = "Audio/SoundManifest.g.cs",
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$BeginMark = '#--------------------------- GENERATED SOUNDS -------------------------------#'
$EndMark   = '#------------------------- END GENERATED SOUNDS -----------------------------#'

if (-not (Test-Path $SoundDir)) {
    Write-Host "no $SoundDir — nothing to sync" -ForegroundColor Yellow
    return
}

$clips = Get-ChildItem $SoundDir -Filter *.ogg -File | Sort-Object Name
Write-Host "== sync-sounds: $($clips.Count) clip(s) in $SoundDir ==" -ForegroundColor Cyan

# Group by stem: trailing _NN is a variant index, anything else is the whole stem.
$groups = @{}
foreach ($c in $clips) {
    $base = [IO.Path]::GetFileNameWithoutExtension($c.Name)
    if ($base -match '^(?<stem>.+)_(?<n>\d{2})$') { $stem = $Matches.stem; $numbered = $true }
    else                                          { $stem = $base;        $numbered = $false }
    if (-not $groups.ContainsKey($stem)) { $groups[$stem] = [pscustomobject]@{ Numbered = 0; Bare = $false } }
    if ($numbered) { $groups[$stem].Numbered++ } else { $groups[$stem].Bare = $true }
}

# Cross-check against the SoundKind stem table so typos surface here, not as silence.
# Stems are derived from the SoundKind enum names (PascalCase -> snake_case), the same
# rule SoundKinds.BuildStems applies at runtime.
$known = @()
if (Test-Path 'Audio/SoundKind.cs') {
    $src = Get-Content 'Audio/SoundKind.cs' -Raw
    if ($src -match '(?s)enum\s+SoundKind\s*\{(?<body>.*?)\}') {
        $body = $Matches.body -replace '//[^\r\n]*', ''
        $known = [regex]::Matches($body, '(?m)^\s*([A-Za-z][A-Za-z0-9]*)\s*(?:=\s*\d+\s*)?,') |
                 ForEach-Object { $_.Groups[1].Value } |
                 Where-Object { $_ -ne 'None' } |
                 ForEach-Object { ($_ -creplace '(?<!^)([A-Z])', '_$1').ToLowerInvariant() }
    }
}

# ── mgcb entries ────────────────────────────────────────────────────────────────
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add($BeginMark)
$lines.Add('# Written by scripts/sync-sounds.ps1 — do not hand-edit; your changes will be lost.')
$lines.Add('')
foreach ($c in $clips) {
    $stem = [IO.Path]::GetFileNameWithoutExtension($c.Name)
    $lines.Add("#begin ../$SoundDir/$($c.Name)")
    $lines.Add('/importer:OggImporter')
    $lines.Add('/processor:SoundEffectProcessor')
    $lines.Add('/processorParam:Quality=Best')
    $lines.Add("/build:../$SoundDir/$($c.Name);Sounds/$stem.xnb")
    $lines.Add('')
}
$lines.Add($EndMark)
$generated = ($lines -join "`n")

$pattern = "(?s)" + [regex]::Escape($BeginMark) + ".*?" + [regex]::Escape($EndMark)
$targets = @()
$skipped = @()
foreach ($path in $Mgcb) {
    if (-not (Test-Path $path)) { $skipped += $path; continue }
    $text = Get-Content $path -Raw
    if ($text -match $pattern) { $newText = [regex]::Replace($text, $pattern, { $generated }) }
    else                       { $newText = $text.TrimEnd() + "`n`n" + $generated + "`n" }
    $targets += [pscustomobject]@{ Path = $path; Text = $newText }
}
if ($targets.Count -eq 0) { throw "none of the -Mgcb targets exist: $($Mgcb -join ', ')" }

# ── manifest ────────────────────────────────────────────────────────────────────
$entries = foreach ($stem in ($groups.Keys | Sort-Object)) {
    $g = $groups[$stem]
    $count = if ($g.Numbered -gt 0) { $g.Numbered } else { 0 }
    '        ("{0}", {1}),' -f $stem, $count
}
$manifestText = @"
// <auto-generated>
//   Regenerate with:  pwsh scripts/sync-sounds.ps1
//   Source of truth is the files in $SoundDir/*.ogg — do not hand-edit.
//
//   This is a generated C# table rather than a runtime manifest file on purpose: the
//   WASM host cannot enumerate a content directory, and a compile-time table needs no
//   IO on either backend.
// </auto-generated>

namespace MTile;

public static class SoundManifest
{
    // (stem, variant count). Count 0 means "one unnumbered file", <stem>.ogg.
    public static readonly (string Stem, int Variants)[] Entries =
    {
$($entries -join "`n")
    };
}
"@

foreach ($stem in ($groups.Keys | Sort-Object)) {
    $g = $groups[$stem]
    $n = if ($g.Numbered -gt 0) { "$($g.Numbered) variant(s)" } else { "single" }
    $flag = if ($known -contains $stem) { ' ' } else { '?' }
    $color = if ($known -contains $stem) { 'Green' } else { 'Yellow' }
    Write-Host ("  {0} {1,-24} {2}" -f $flag, $stem, $n) -ForegroundColor $color
}
$unknown = $groups.Keys | Where-Object { $known -notcontains $_ }
if ($unknown) {
    Write-Host "  ? = no SoundKind maps to this stem; it will build but never play." -ForegroundColor Yellow
    Write-Host "      Add an enum entry + stem in Audio/SoundKind.cs." -ForegroundColor Yellow
}

foreach ($p in $skipped) { Write-Host "  - $p not present, skipped" -ForegroundColor DarkGray }

# @(...) matters: with a single target $targets.Path is a scalar string, and
# "str" + $Manifest concatenates instead of building a two-element list.
$written = (@($targets.Path) + $Manifest) -join ', '
if ($DryRun) { Write-Host "-DryRun: would write $written" -ForegroundColor DarkGray; return }

# utf8BOM, not utf8: pwsh 7's "utf8" is BOM-less, but these files were first generated
# by Windows PowerShell 5.1 (where "utf8" means with-BOM) and are committed that way.
# Writing them BOM-less makes every regeneration a spurious one-byte diff.
foreach ($t in $targets) { $t.Text | Set-Content $t.Path -Encoding utf8BOM -NoNewline }
$manifestText | Set-Content $Manifest -Encoding utf8BOM
Write-Host "wrote $written" -ForegroundColor Green
