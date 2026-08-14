# Publish the web build to amdson.github.io/mtile/.
#
#   .\scripts\publish-web.ps1            # AOT publish -> copy -> commit -> push
#   .\scripts\publish-web.ps1 -NoPush    # stop after the commit (inspect first)
#   .\scripts\publish-web.ps1 -SkipBuild # reuse the last publish output (copy/push only)
#
# AOT is mandatory for playability (2.7 fps interpreted vs ~40 fps AOT - see
# Plans/WEB_PVP.md), so this always publishes with RunAOTCompilation=true; expect
# the build step to take several minutes.
param(
    [switch]$NoPush,
    [switch]$SkipBuild,
    [string]$SiteRepo = "C:\Users\amdic\amdson.github.io"
)

$ErrorActionPreference = "Stop"
$gameRepo = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $gameRepo "MTile.Web\bin\publish-gh"
$target = Join-Path $SiteRepo "mtile"

if (-not (Test-Path (Join-Path $SiteRepo "_config.yml"))) {
    throw "Site repo not found at $SiteRepo"
}

if (-not $SkipBuild) {
    Write-Host "== dotnet publish (AOT - this takes a while) ==" -ForegroundColor Cyan
    dotnet publish (Join-Path $gameRepo "MTile.Web\MTile.Web.csproj") -c Release `
        -p:RunAOTCompilation=true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}

$wwwroot = Join-Path $publishDir "wwwroot"
if (-not (Test-Path (Join-Path $wwwroot "_framework"))) {
    throw "No publish output at $wwwroot - run without -SkipBuild"
}

# Mirror into the site repo. GitHub Pages never serves the precompressed
# variants (it gzips on the fly), so skip .br/.gz to keep the site repo lean.
Write-Host "== copying to $target ==" -ForegroundColor Cyan
robocopy $wwwroot $target /MIR /XF *.br *.gz | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

$hash = (git -C $gameRepo rev-parse --short HEAD).Trim()
git -C $SiteRepo add mtile
git -C $SiteRepo diff --cached --quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host "No changes to publish." -ForegroundColor Yellow
    exit 0
}
git -C $SiteRepo commit -m "mtile: update to $hash"
if ($LASTEXITCODE -ne 0) { throw "commit failed" }

if ($NoPush) {
    Write-Host "Committed (push skipped). cd $SiteRepo; git push when ready." -ForegroundColor Yellow
} else {
    git -C $SiteRepo push
    if ($LASTEXITCODE -ne 0) { throw "push failed" }
    Write-Host "Live (after Pages rebuild, ~1 min): https://amdson.github.io/mtile/" -ForegroundColor Green
}

