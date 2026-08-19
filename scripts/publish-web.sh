#!/usr/bin/env bash
# Publish the web build to amdson.github.io/mtile/.  (macOS/Linux; the Windows
# equivalent is publish-web.ps1 — keep the two in step.)
#
#   ./scripts/publish-web.sh              # AOT publish -> copy -> commit -> push
#   ./scripts/publish-web.sh --no-push    # stop after the commit (inspect first)
#   ./scripts/publish-web.sh --skip-build # reuse the last publish output (copy/push only)
#   ./scripts/publish-web.sh --site-repo ~/dev/amdson.github.io
#
# AOT is mandatory for playability (2.7 fps interpreted vs ~40 fps AOT - see
# Plans/WEB_PVP.md), so this always publishes with RunAOTCompilation=true; expect
# the build step to take several minutes.
set -euo pipefail

no_push=0
skip_build=0
site_repo="${MTILE_SITE_REPO:-}"

while [ $# -gt 0 ]; do
    case "$1" in
        --no-push)    no_push=1 ;;
        --skip-build) skip_build=1 ;;
        --site-repo)  site_repo="${2:-}"; shift ;;
        -h|--help)    sed -n '2,13p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

game_repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="$game_repo/MTile.Web/bin/publish-gh"

# The site checkout isn't inside the game repo, so guess the usual spots: a
# sibling of the game repo, then ~/dev. MTILE_SITE_REPO or --site-repo wins.
if [ -z "$site_repo" ]; then
    for candidate in "$(dirname "$game_repo")/amdson.github.io" "$HOME/dev/amdson.github.io"; do
        if [ -f "$candidate/_config.yml" ]; then site_repo="$candidate"; break; fi
    done
fi
if [ -z "$site_repo" ] || [ ! -f "$site_repo/_config.yml" ]; then
    echo "Site repo not found${site_repo:+ at $site_repo} - pass --site-repo <path> or set MTILE_SITE_REPO" >&2
    exit 1
fi
target="$site_repo/mtile"

if [ "$skip_build" -eq 0 ]; then
    echo "== dotnet publish (AOT - this takes a while) =="
    dotnet publish "$game_repo/MTile.Web/MTile.Web.csproj" -c Release \
        -p:RunAOTCompilation=true -o "$publish_dir"
fi

wwwroot="$publish_dir/wwwroot"
if [ ! -d "$wwwroot/_framework" ]; then
    echo "No publish output at $wwwroot - run without --skip-build" >&2
    exit 1
fi

# Mirror into the site repo. Skip .br/.gz (GitHub Pages never serves the
# precompressed variants; it gzips on the fly) and .md (the Pages build's
# jekyll-optional-front-matter plugin turns any markdown into a site page,
# polluting the site nav; the game never fetches markdown at runtime).
# --delete matches robocopy /MIR: stale files from an older publish must go.
echo "== copying to $target =="
mkdir -p "$target"
rsync -a --delete --exclude='*.br' --exclude='*.gz' --exclude='*.md' \
    "$wwwroot/" "$target/"

hash="$(git -C "$game_repo" rev-parse --short HEAD)"
git -C "$site_repo" add mtile
if git -C "$site_repo" diff --cached --quiet; then
    echo "No changes to publish."
    exit 0
fi
git -C "$site_repo" commit -m "mtile: update to $hash"

if [ "$no_push" -eq 1 ]; then
    echo "Committed (push skipped). cd $site_repo && git push when ready."
else
    git -C "$site_repo" push
    echo "Live (after Pages rebuild, ~1 min): https://amdson.github.io/mtile/"
fi
