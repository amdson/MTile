#!/usr/bin/env bash
# Wire the built sound clips in Assets/Sounds into the game.  (macOS/Linux; the
# Windows equivalent is sync-sounds.ps1 — keep the two in step. Both must emit
# BYTE-IDENTICAL output, or alternating between machines churns the diff.)
#
#   ./scripts/sync-sounds.sh              # regenerate mgcb entries + manifest
#   ./scripts/sync-sounds.sh --dry-run    # print the plan, touch nothing
#
# Scans Assets/Sounds/*.ogg, then regenerates two things so a new clip needs no
# hand-editing anywhere:
#
#   1. The GENERATED SOUNDS region of every .mgcb target — one pipeline entry per
#      clip, built out-of-tree from ../Assets/Sounds and mapped to the asset name
#      "Sounds/<stem>". Both toolchains (MonoGame 3.8.4.1 desktop, KNI 4.1.9001 web)
#      have OggImporter + SoundEffectProcessor, so the same entries serve both.
#
#      Content.Mac.mgcb is in that list because macOS builds a shader-free content
#      set (mgfxc is a Windows binary needing Wine). A target that does not exist is
#      skipped rather than created — the sound entries are identical across variants,
#      only the .fx entries differ.
#   2. Audio/SoundManifest.g.cs — the compile-time (stem, variant count) table
#      SoundBank loads from. Generated rather than read at runtime because the WASM
#      host cannot enumerate a content directory.
#
# Naming: <stem>.ogg for a single clip, or <stem>_01.ogg, <stem>_02.ogg, … for
# round-robin variants. That is exactly what build-sfx.sh --name <stem> produces.
#
# A stem with no matching entry in SoundKind is reported with '?' — it will build but
# nothing will ever play it until a SoundKind + stem exist.
#
# Sorting is LC_ALL=C (byte order) to be reproducible across machines. PowerShell's
# Sort-Object is culture-aware; for the lowercase [a-z0-9_] stems this pipeline
# produces the two agree, and the zero-diff check below is what keeps that honest.
set -euo pipefail

sound_dir="Assets/Sounds"
manifest="Audio/SoundManifest.g.cs"
mgcb_targets=("Content/Content.mgcb" "Content/Content.Mac.mgcb")
dry_run=0

while [ $# -gt 0 ]; do
    case "$1" in
        --sound-dir) sound_dir="${2:-}"; shift ;;
        --manifest)  manifest="${2:-}"; shift ;;
        --mgcb)      mgcb_targets=("${2:-}"); shift ;;
        --dry-run)   dry_run=1 ;;
        -h|--help)   sed -n '2,30p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

begin_mark='#--------------------------- GENERATED SOUNDS -------------------------------#'
end_mark='#------------------------- END GENERATED SOUNDS -----------------------------#'
# Names BOTH scripts: whichever one runs, the emitted bytes are the same.
written_by='# Written by scripts/sync-sounds (.ps1/.sh) — do not hand-edit; your changes will be lost.'

if [ ! -d "$sound_dir" ]; then
    echo "no $sound_dir — nothing to sync"
    exit 0
fi

# ── clips ───────────────────────────────────────────────────────────────────────
clips=()
while IFS= read -r line; do [ -n "$line" ] && clips+=("$line"); done < <(
    find "$sound_dir" -maxdepth 1 -type f -name '*.ogg' -exec basename {} \; | LC_ALL=C sort
)
echo "== sync-sounds: ${#clips[@]} clip(s) in $sound_dir =="

# Group by stem: a trailing _NN is a variant index, anything else is the whole stem.
# Two parallel arrays keyed by stem (bash 3.2 on macOS has no associative arrays).
stems=()
counts=()          # number of _NN variants
bares=()           # 1 if an unnumbered <stem>.ogg exists

stem_index() {
    local want="$1" i=0
    for s in ${stems[@]+"${stems[@]}"}; do
        [ "$s" = "$want" ] && { echo "$i"; return; }
        i=$((i + 1))
    done
    echo -1
}

for c in ${clips[@]+"${clips[@]}"}; do
    base="${c%.ogg}"
    if [[ "$base" =~ ^(.+)_([0-9][0-9])$ ]]; then
        stem="${BASH_REMATCH[1]}"; numbered=1
    else
        stem="$base"; numbered=0
    fi
    idx="$(stem_index "$stem")"
    if [ "$idx" -lt 0 ]; then
        stems+=("$stem"); counts+=(0); bares+=(0); idx=$((${#stems[@]} - 1))
    fi
    if [ "$numbered" -eq 1 ]; then counts[$idx]=$(( ${counts[$idx]} + 1 )); else bares[$idx]=1; fi
done

# ── known SoundKind stems (PascalCase -> snake_case, same rule as SoundKinds.BuildStems) ──
known=""
if [ -f "Audio/SoundKind.cs" ]; then
    known="$(
        awk '/enum[ \t]+SoundKind/{inenum=1; next} inenum && /^\}/{exit} inenum' Audio/SoundKind.cs |
        sed 's|//.*||' |
        sed -n 's/^[[:space:]]*\([A-Za-z][A-Za-z0-9]*\)[[:space:]]*\(=[[:space:]]*[0-9][0-9]*[[:space:]]*\)\{0,1\},.*$/\1/p' |
        grep -v '^None$' |
        sed 's/\(.\)\([A-Z]\)/\1_\2/g' |
        tr '[:upper:]' '[:lower:]'
    )"
fi
is_known() { printf '%s\n' "$known" | grep -qx "$1"; }

# ── mgcb entries ────────────────────────────────────────────────────────────────
generated="$begin_mark"$'\n'"$written_by"$'\n'$'\n'
for c in ${clips[@]+"${clips[@]}"}; do
    generated+="#begin ../$sound_dir/$c"$'\n'
    generated+='/importer:OggImporter'$'\n'
    generated+='/processor:SoundEffectProcessor'$'\n'
    generated+='/processorParam:Quality=Best'$'\n'
    generated+="/build:../$sound_dir/$c;Sounds/${c%.ogg}.xnb"$'\n'
    generated+=$'\n'
done
generated+="$end_mark"

# ── manifest ────────────────────────────────────────────────────────────────────
entries=""
while IFS= read -r stem; do
    [ -n "$stem" ] || continue
    idx="$(stem_index "$stem")"
    n="${counts[$idx]}"
    entries+="$(printf '        ("%s", %d),' "$stem" "$n")"$'\n'
done < <(printf '%s\n' ${stems[@]+"${stems[@]}"} | LC_ALL=C sort)

manifest_text="// <auto-generated>
//   Regenerate with:  pwsh scripts/sync-sounds.ps1   (or ./scripts/sync-sounds.sh)
//   Source of truth is the files in $sound_dir/*.ogg — do not hand-edit.
//
//   This is a generated C# table rather than a runtime manifest file on purpose: the
//   WASM host cannot enumerate a content directory, and a compile-time table needs no
//   IO on either backend.
// </auto-generated>

namespace MTile;

public static class SoundManifest
{
    // (stem, variant count). Count 0 means \"one unnumbered file\", <stem>.ogg.
    public static readonly (string Stem, int Variants)[] Entries =
    {
${entries%$'\n'}
    };
}"

# ── report ──────────────────────────────────────────────────────────────────────
unknown=0
while IFS= read -r stem; do
    [ -n "$stem" ] || continue
    idx="$(stem_index "$stem")"
    if [ "${counts[$idx]}" -gt 0 ]; then n="${counts[$idx]} variant(s)"; else n="single"; fi
    if is_known "$stem"; then flag=" "; else flag="?"; unknown=1; fi
    printf '  %s %-24s %s\n' "$flag" "$stem" "$n"
done < <(printf '%s\n' ${stems[@]+"${stems[@]}"} | LC_ALL=C sort)
if [ "$unknown" -eq 1 ]; then
    echo "  ? = no SoundKind maps to this stem; it will build but never play."
    echo "      Add an enum entry + stem in Audio/SoundKind.cs."
fi

targets=()
for path in "${mgcb_targets[@]}"; do
    if [ -f "$path" ]; then targets+=("$path"); else echo "  - $path not present, skipped"; fi
done
if [ "${#targets[@]}" -eq 0 ]; then
    echo "none of the --mgcb targets exist: ${mgcb_targets[*]}" >&2
    exit 1
fi

written="$(printf '%s, ' "${targets[@]}")$manifest"
if [ "$dry_run" -eq 1 ]; then echo "--dry-run: would write $written"; exit 0; fi

# Splice, preserving every byte outside the region — including the UTF-8 BOM these
# files carry (they were first generated by Windows PowerShell 5.1, where "utf8"
# means with-BOM, and are committed that way; writing them BOM-less would make every
# regeneration a spurious three-byte diff).
# The block goes through a file, not awk -v: the macOS awk rejects a newline inside a
# -v assignment, so a multi-line block has to be read with getline.
block_file="$(mktemp)"
printf '%s\n' "$generated" > "$block_file"
trap 'rm -f "$block_file"' EXIT

for path in "${targets[@]}"; do
    tmp="$(mktemp)"
    if grep -qF "$begin_mark" "$path"; then
        awk -v begin="$begin_mark" -v end="$end_mark" -v blockfile="$block_file" '
            $0 == begin { while ((getline line < blockfile) > 0) print line; skipping = 1; next }
            $0 == end   { skipping = 0; next }
            !skipping   { print }
        ' "$path" > "$tmp"
    else
        { sed -e :a -e '/^\n*$/{$d;N;};/\n$/ba' "$path"; printf '\n%s\n' "$generated"; } > "$tmp"
    fi
    cat "$tmp" > "$path"
    rm -f "$tmp"
done

printf '\xef\xbb\xbf%s\n' "$manifest_text" > "$manifest"
echo "wrote $written"
