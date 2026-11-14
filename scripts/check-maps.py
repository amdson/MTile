#!/usr/bin/env python3
"""List the per-area MAP.md files whose code has changed since they were verified.

Each map carries two header lines:

    Verified against: <commit> (<date>)
    Covers: <comma-separated repo-relative paths or globs>

For every MAP.md in the repo this counts the commits after <commit> that touch
any covered path (the map file itself excluded) and reports the maps that are
behind. A stale map is a prompt to re-verify it, not proof that it is wrong.

    scripts/check-maps.py            # table of every map, stale ones marked
    scripts/check-maps.py --stale    # only the stale ones; exit 1 if any
    scripts/check-maps.py -v         # also list the commits behind each stale map
"""
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
STAMP = re.compile(r"^Verified against:\s*([0-9a-f]{7,40})\b(?:\s*\(([^)]*)\))?", re.M)
COVERS = re.compile(r"^Covers:\s*(.+)$", re.M)


def git(*args):
    return subprocess.run(
        ["git", *args], cwd=ROOT, capture_output=True, text=True
    )


def find_maps():
    out = git("ls-files", "--cached", "--others", "--exclude-standard", "--", "MAP.md", "**/MAP.md").stdout
    return sorted(Path(p) for p in out.splitlines() if p)


def parse_covers(text):
    """Covers entries may carry a parenthetical note and a <placeholder> segment."""
    text = re.sub(r"\([^)]*\)", "", text)
    paths = []
    for raw in text.split(","):
        p = re.sub(r"<[^>]*>", "*", raw.strip().strip("`"))
        if p:
            paths.append(p)
    return paths


def check(map_path):
    """Return (status, date, commits_behind) for one map."""
    head = "\n".join((ROOT / map_path).read_text(encoding="utf-8").splitlines()[:12])
    stamp, covers = STAMP.search(head), COVERS.search(head)
    if not stamp or not covers:
        return "NO STAMP", "", []
    commit, date = stamp.group(1), stamp.group(2) or ""
    if git("cat-file", "-e", commit + "^{commit}").returncode != 0:
        return "BAD COMMIT", date, []
    paths = parse_covers(covers.group(1))
    dead = [p for p in paths if not git("ls-files", "--", p).stdout.strip()]
    if dead:
        return "BAD PATH", date, [f"matches no tracked file: {p}" for p in dead]
    log = git(
        "log", "--format=%h %ad %s", "--date=short", f"{commit}..HEAD", "--",
        *paths, f":(exclude){map_path.as_posix()}",
    )
    commits = [line for line in log.stdout.splitlines() if line]
    return ("STALE" if commits else "ok"), date, commits


def main(argv):
    only_stale = "--stale" in argv
    verbose = "-v" in argv or "--verbose" in argv
    maps = find_maps()
    if not maps:
        print("no MAP.md files found")
        return 0
    bad = 0
    for m in maps:
        status, date, commits = check(m)
        if status != "ok":
            bad += 1
        elif only_stale:
            continue
        behind = f"{len(commits)} commit(s) since" if status == "STALE" else ""
        print(f"{status:<10} {m.as_posix():<34} {date:<12} {behind}")
        if verbose or status == "BAD PATH":
            for c in commits:
                print(f"             {c}")
    return 1 if (only_stale and bad) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
