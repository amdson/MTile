#!/usr/bin/env bash
# Survey worktree branches that are not yet merged into main.
# Pure read-only: uses `git merge-tree --write-tree` so nothing touches the working tree.
# Usage: survey.sh [max-age-days]   (default: no age filter; ages are always reported)
set -uo pipefail

cd "$(git rev-parse --show-toplevel)" || exit 1
MAIN=${MAIN_BRANCH:-main}
MAXAGE=${1:-}
NOW=$(date +%s)

printf '%-42s %-6s %-6s %-6s %-9s %s\n' BRANCH AGE CMTS FILES CONFLICTS "WORKTREE / STATE"
printf '%.0s-' {1..110}; echo

# branch -> worktree path (and dirty state)
declare -a ROWS=()
while read -r br; do
  [ "$br" = "$MAIN" ] && continue
  git merge-base --is-ancestor "$br" "$MAIN" 2>/dev/null && continue   # already merged

  ts=$(git log -1 --format=%ct "$br")
  age_d=$(( (NOW - ts) / 86400 ))
  [ -n "$MAXAGE" ] && [ "$age_d" -gt "$MAXAGE" ] && continue

  cmts=$(git rev-list --count "$MAIN".."$br")
  files=$(git diff --name-only "$MAIN"..."$br" | wc -l | tr -d ' ')

  # dry-run merge; conflicted paths are the lines with a stage number in column 4
  out=$(git merge-tree --write-tree "$MAIN" "$br" 2>&1); rc=$?
  if [ $rc -eq 0 ]; then
    conf="clean"; cfiles=""
  else
    cfiles=$(printf '%s\n' "$out" | awk '$3 ~ /^[123]$/ {print $4}' | sort -u)
    n=$(printf '%s\n' "$cfiles" | grep -c . )
    conf="$n"
  fi

  wt=$(git worktree list --porcelain | awk -v b="refs/heads/$br" '
    /^worktree /{p=$2} /^branch /{if ($2==b) print p}')
  state="(no worktree)"
  if [ -n "$wt" ]; then
    d=$(git -C "$wt" status --porcelain 2>/dev/null | wc -l | tr -d ' ')
    state="${wt##*/}"; [ "$d" != 0 ] && state="$state  ** $d UNCOMMITTED **"
  fi

  printf '%-42s %-6s %-6s %-6s %-9s %s\n' "$br" "${age_d}d" "$cmts" "$files" "$conf" "$state"
  [ -n "$cfiles" ] && printf '%s\n' "$cfiles" | sed 's/^/      ! /'
  ROWS+=("$br")
done < <(git for-each-ref --sort=committerdate --format='%(refname:short)' refs/heads/)

echo
[ ${#ROWS[@]} -eq 0 ] && echo "Nothing unmerged." || echo "${#ROWS[@]} unmerged branch(es). Merge clean ones first, re-surveying after each."
