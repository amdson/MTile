#!/usr/bin/env python3
"""Branch plumbing for the fighter-package competition (Plans/FIGHTER_COMPETITION.md).

    scripts/competition.py base     --wave N [--from fighter-plan]
        cut branch competition-wave<N>-base: <from> with the Holdout packages git rm'd
    scripts/competition.py worktree --wave N --name <agent>
        give a competitor a worktree .claude/worktrees/<agent> on competition-wave<N>-<agent>
    scripts/competition.py collect  --wave N [--dry-run]
        copy each competitor's single new package file into this checkout (or just report)

Works from any directory: the repo root comes from `git rev-parse --show-toplevel`.

The supervisor's checkout is dirty on purpose, so nothing here touches its index or
working tree except `collect`, which only WRITES the accepted package files (never
`git merge` — the base commit deletes the holdout files and a merge would carry that).
`base` builds its commit in a temporary worktree that is removed afterwards.
"""
import argparse, os, re, shutil, subprocess, sys, tempfile

PACKAGES = "Entities/Enemies/Fighters/Packages/"


def git(*args, cwd=None, check=True):
    r = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    if check and r.returncode != 0:
        sys.exit(f"competition: git {' '.join(args)} failed:\n{r.stderr.strip()}")
    return r


def repo_root():
    here = os.path.dirname(os.path.abspath(__file__))
    r = subprocess.run(["git", "rev-parse", "--show-toplevel"], cwd=here, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit("competition: not inside a git checkout")
    return r.stdout.strip()


def branch_exists(root, name):
    return git("rev-parse", "--verify", "--quiet", "refs/heads/" + name, cwd=root, check=False).returncode == 0


def base_branch(wave):
    return f"competition-wave{wave}-base"


def pool_rel(wave):
    return f"Fights/competition/wave{wave}.pool.json"


def holdout_files(root, pool):
    """Ask the bench (`--competition files`) for the holdout package paths. The bench prints
    a banner line first, so keep only the lines that look like package paths."""
    cmd = ["dotnet", "run", "--project", "MTile.Bench", "-c", "Release", "--",
           "--competition", "files", "--pool", pool]
    r = subprocess.run(cmd, cwd=root, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f"competition: {' '.join(cmd)} failed:\n{(r.stdout + r.stderr).strip()}")
    files = [l.strip() for l in r.stdout.splitlines()
             if l.strip().startswith(PACKAGES) and l.strip().endswith(".cs")]
    if not files:
        sys.exit(f"competition: the pool lists no holdout files:\n{r.stdout.strip()}")
    return files


def cmd_base(a):
    root = repo_root()
    branch = base_branch(a.wave)
    rel = pool_rel(a.wave)
    pool = os.path.join(root, rel)
    if not os.path.isfile(pool):
        sys.exit(f"competition: no pool file {rel} — run `--competition split` first")
    if branch_exists(root, branch):
        sys.exit(f"competition: branch {branch} already exists — delete it first to re-cut")
    git("rev-parse", "--verify", "--quiet", a.from_ + "^{commit}", cwd=root)

    files = holdout_files(root, rel)

    # Is the pool file committed (and identical) on <from>? If not, the base commit carries it,
    # so competitors' tooling can read it.
    with open(pool, "rb") as f:
        pool_bytes = f.read()
    shown = subprocess.run(["git", "show", f"{a.from_}:{rel}"], cwd=root, capture_output=True)
    add_pool = shown.returncode != 0 or shown.stdout != pool_bytes

    tmp = tempfile.mkdtemp(prefix=f"competition-wave{a.wave}-base-")
    ok = False
    git("worktree", "add", "-q", tmp, "-b", branch, a.from_, cwd=root)
    try:
        r = git("rm", "-q", "--", *files, cwd=tmp, check=False)
        if r.returncode != 0:
            sys.exit(f"competition: git rm failed in the base worktree (are the holdout files committed on {a.from_}?):\n{r.stderr.strip()}")
        if add_pool:
            os.makedirs(os.path.dirname(os.path.join(tmp, rel)), exist_ok=True)
            with open(os.path.join(tmp, rel), "wb") as f:
                f.write(pool_bytes)
            git("add", "-f", "--", rel, cwd=tmp)
        git("commit", "-q", "-m", f"Competition wave {a.wave}: base (holdout packages removed)", cwd=tmp)
        ok = True
    finally:
        git("worktree", "remove", "--force", tmp, cwd=root, check=False)
        shutil.rmtree(tmp, ignore_errors=True)
        git("worktree", "prune", cwd=root, check=False)
        if not ok:
            git("branch", "-D", branch, cwd=root, check=False)

    print(f"cut {branch} from {a.from_}")
    print("removed:")
    for f in files:
        print("  " + f)
    if add_pool:
        print(f"added {rel} (not committed on {a.from_})")


def cmd_worktree(a):
    root = repo_root()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", a.name) or a.name == "base":
        sys.exit(f"competition: bad agent name '{a.name}' (letters, digits, _ . -; not 'base')")
    base = base_branch(a.wave)
    branch = f"competition-wave{a.wave}-{a.name}"
    path = os.path.join(root, ".claude", "worktrees", a.name)
    if not branch_exists(root, base):
        sys.exit(f"competition: no base branch {base} — run `competition.py base --wave {a.wave}` first")
    if os.path.exists(path):
        sys.exit(f"competition: {path} already exists")
    if branch_exists(root, branch):
        sys.exit(f"competition: branch {branch} already exists")
    git("worktree", "add", "-q", path, "-b", branch, base, cwd=root)
    print(path)


def cmd_collect(a):
    root = repo_root()
    base = base_branch(a.wave)
    if not branch_exists(root, base):
        sys.exit(f"competition: no base branch {base}")
    refs = git("for-each-ref", "--format=%(refname:short)", f"refs/heads/competition-wave{a.wave}-*", cwd=root).stdout.split()
    branches = sorted(b for b in refs if b != base)
    if not branches:
        print(f"no submission branches competition-wave{a.wave}-*")
        return 0

    rows, rejected = [], 0
    for b in branches:
        diff = git("diff", "--no-renames", "--name-status", f"{base}..{b}", cwd=root).stdout.strip().splitlines()
        entries = [l.split("\t") for l in diff]
        reason = None
        if len(entries) != 1:
            reason = f"{len(entries)} changed files (expected exactly one new package)"
        else:
            status, path = entries[0][0], entries[0][-1]
            if status != "A":
                reason = f"{status} {path} (expected one ADDED file)"
            elif not (path.startswith(PACKAGES) and path.endswith(".cs")):
                reason = f"A {path} (not under {PACKAGES}, or not .cs)"
            elif os.path.exists(os.path.join(root, path)):
                reason = f"{path} already present, delete it first"
        if reason:
            rejected += 1
            detail = [reason] + (["  " + l.replace("\t", "  ") for l in diff] if len(entries) != 1 or entries[0][0] != "A" else [])
            rows.append((b, "REJECTED", detail))
            continue
        if not a.dry_run:
            data = subprocess.run(["git", "show", f"{b}:{path}"], cwd=root, capture_output=True, check=True).stdout
            with open(os.path.join(root, path), "wb") as f:
                f.write(data)
        rows.append((b, "would accept" if a.dry_run else "accepted", [path]))

    bw = max(len(r[0]) for r in rows)
    sw = max(len(r[1]) for r in rows)
    for b, status, detail in rows:
        print(f"{b.ljust(bw)}  {status.ljust(sw)}  {detail[0]}")
        for extra in detail[1:]:
            print(" " * (bw + sw + 4) + extra)
    return 1 if rejected else 0


def main():
    p = argparse.ArgumentParser(
        prog="competition.py",
        description="Branch plumbing for the fighter-package competition (Plans/FIGHTER_COMPETITION.md).")
    sub = p.add_subparsers(dest="cmd", required=True, metavar="{base,worktree,collect}")
    b = sub.add_parser("base", help="cut competition-wave<N>-base from <from> with the Holdout packages git rm'd")
    b.add_argument("--wave", type=int, required=True)
    b.add_argument("--from", dest="from_", default="fighter-plan", help="committed ref to cut from (default fighter-plan)")
    w = sub.add_parser("worktree", help="create .claude/worktrees/<agent> on branch competition-wave<N>-<agent>")
    w.add_argument("--wave", type=int, required=True)
    w.add_argument("--name", required=True)
    c = sub.add_parser("collect", help="copy each submission branch's one new package file into this checkout")
    c.add_argument("--wave", type=int, required=True)
    c.add_argument("--dry-run", action="store_true", help="only print what would be accepted/rejected")
    a = p.parse_args()
    return {"base": cmd_base, "worktree": cmd_worktree, "collect": cmd_collect}[a.cmd](a) or 0


if __name__ == "__main__":
    sys.exit(main())
