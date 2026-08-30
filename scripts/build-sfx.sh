#!/usr/bin/env bash
# Batch-convert raw sound slices into game-ready SFX.  (macOS/Linux; the Windows
# equivalent is build-sfx.ps1 — keep the two in step. Same defaults, same ffmpeg
# filter chain, so either machine produces the same clip.)
#
#   ./scripts/build-sfx.sh                        # Audio/raw -> Assets/Sounds
#   ./scripts/build-sfx.sh --name tile_break      # rename+number: tile_break_01.ogg, _02, ...
#   ./scripts/build-sfx.sh --loop                 # loops: no silence trim, no end fade
#   ./scripts/build-sfx.sh --dry-run              # print the plan, touch nothing
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
# runtime in the mixer (Plans/AUDIO_PLAN.md §5) - do not bake them in here.
#
# Requires ffmpeg + ffprobe on PATH:  brew install ffmpeg
set -euo pipefail

in_dir="Audio/raw"
out_dir="Assets/Sounds"
name=""
rate=22050
lufs=-16.0
quality=4          # libvorbis -q:a, 0..10. 4 ~= 128kbps, plenty for SFX.
loop=0
force=0
dry_run=0

while [ $# -gt 0 ]; do
    case "$1" in
        --in)      in_dir="${2:-}"; shift ;;
        --out)     out_dir="${2:-}"; shift ;;
        --name)    name="${2:-}"; shift ;;
        --rate)    rate="${2:-}"; shift ;;
        --lufs)    lufs="${2:-}"; shift ;;
        --quality) quality="${2:-}"; shift ;;
        --loop)    loop=1 ;;
        --force)   force=1 ;;
        --dry-run) dry_run=1 ;;
        -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo"

for exe in ffmpeg ffprobe; do
    command -v "$exe" >/dev/null 2>&1 || {
        echo "$exe not found on PATH. Install with: brew install ffmpeg" >&2; exit 1; }
done

[ -d "$in_dir" ] || {
    echo "Input folder not found: $in_dir" >&2
    echo "Put your hand-cut slices there (.wav/.flac/.aif/.mp3)." >&2; exit 1; }

sources=()
while IFS= read -r f; do [ -n "$f" ] && sources+=("$f"); done < <(
    find "$in_dir" -maxdepth 1 -type f \
        \( -iname '*.wav' -o -iname '*.flac' -o -iname '*.aif' -o -iname '*.aiff' \
           -o -iname '*.mp3' -o -iname '*.ogg' -o -iname '*.m4a' \) \
        -exec basename {} \; | LC_ALL=C sort
)
[ "${#sources[@]}" -gt 0 ] || { echo "No audio files in $in_dir" >&2; exit 1; }

[ "$dry_run" -eq 1 ] || mkdir -p "$out_dir"

kind="one-shot"; [ "$loop" -eq 1 ] && kind="loop"
echo "== build-sfx: ${#sources[@]} file(s), $kind, mono ${rate}Hz ogg q$quality @ ${lufs} LUFS =="
echo "   in : $in_dir"
echo "   out: $out_dir"
[ "$loop" -eq 1 ] && echo "   loop mode: silence trim and end fade disabled (they would break the loop point)"
echo

# PowerShell renders a [double] without trailing zeros, so "-16.0" prints as "-16" and
# "1.1950" as "1.195". ffmpeg reads either, but matching keeps the twins byte-comparable.
trim_zeros() {
    case "$1" in
        *.*) printf '%s' "$1" | sed -e 's/0\{1,\}$//' -e 's/\.$//' ;;
        *)   printf '%s' "$1" ;;
    esac
}

written=0
total_kb=0
i=0

for src in "${sources[@]}"; do
    i=$((i + 1))
    base="${src%.*}"

    if [ -n "$name" ]; then
        stem="$(printf '%s_%02d' "$name" "$i")"
    else
        # Sanitize: lowercase, spaces/punctuation to underscore. Sonniss filenames are
        # long and full of spaces and hyphens.
        stem="$(printf '%s' "$base" | tr '[:upper:]' '[:lower:]' \
                | sed -e 's/[^a-z0-9]\{1,\}/_/g' -e 's/^_\{1,\}//' -e 's/_\{1,\}$//')"
    fi
    dst="$out_dir/$stem.ogg"

    if [ -f "$dst" ] && [ "$force" -eq 0 ] && [ "$dry_run" -eq 0 ]; then
        printf '  skip  %-28s (exists, use --force)\n' "$stem.ogg"
        continue
    fi

    # Probe duration so the end fade can be placed.
    dur="$(ffprobe -v quiet -of csv=p=0 -show_entries format=duration -- "$in_dir/$src" || echo 0)"
    case "$dur" in ''|N/A) dur=0 ;; esac

    # Filter chain, in order:
    #   silenceremove - strip leading silence. A one-shot with lead-in silence feels late
    #                   on every trigger. Loops skip this (it would move the loop point).
    #   loudnorm      - EBU R128 loudness match. Single-pass: less exact than two-pass but
    #                   well within tolerance for short SFX, and half the runtime.
    #   afade         - 5ms tail fade to kill the end click from a hard cut. Loops skip it.
    filters=""
    [ "$loop" -eq 0 ] && filters="silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.005,"
    filters="${filters}loudnorm=I=$(trim_zeros "$lufs"):TP=-1.5:LRA=11"
    if [ "$loop" -eq 0 ] && awk -v d="$dur" 'BEGIN{exit !(d > 0.05)}'; then
        fade_start="$(trim_zeros "$(awk -v d="$dur" 'BEGIN{printf "%.4f", d - 0.005}')")"
        filters="${filters},afade=t=out:st=${fade_start}:d=0.005"
    fi

    if [ "$dry_run" -eq 1 ]; then
        printf '  plan  %-28s <- %s\n' "$stem.ogg" "$src"
        printf '        %s\n' "$filters"
        continue
    fi

    ffmpeg -hide_banner -loglevel error -nostdin -y \
        -i "$in_dir/$src" \
        -af "$filters" \
        -ac 1 -ar "$rate" \
        -c:a libvorbis -q:a "$quality" \
        -- "$dst"

    in_kb="$(awk -v b="$(wc -c < "$in_dir/$src")" 'BEGIN{printf "%.1f", b/1024}')"
    out_kb="$(awk -v b="$(wc -c < "$dst")" 'BEGIN{printf "%.1f", b/1024}')"
    printf '  ok    %-28s %7s KB -> %6s KB   %5.2fs\n' "$stem.ogg" "$in_kb" "$out_kb" "$dur"
    written=$((written + 1))
    total_kb="$(awk -v a="$total_kb" -v b="$out_kb" 'BEGIN{printf "%.1f", a+b}')"
done

echo
if [ "$dry_run" -eq 1 ]; then
    echo "Dry run - nothing written."
elif [ "$written" -gt 0 ]; then
    echo "Wrote $written file(s), $total_kb KB total."
    if [ "$loop" -eq 1 ]; then
        echo "Loop check: play each one looped for ~30s. A click you cannot hear once is obvious after ten repeats."
        echo "If it clicks, crossfade the file onto itself in the editor - ffmpeg cannot fix a bad loop point here."
    fi
    echo "Note: MTile.Web.csproj StageGameAssetsToWwwroot has no copy rule for audio yet"
    echo "      (Plans/AUDIO_PLAN.md §8) - these will not reach the web build until it does."
fi
