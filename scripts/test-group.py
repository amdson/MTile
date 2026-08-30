#!/usr/bin/env python3
"""Run one slice of the MTile suite instead of all ~724 tests.

    scripts/test-group.py <group> [group...]   one or more groups (they may overlap)
    scripts/test-group.py full                 everything except the slow outliers
    scripts/test-group.py full --slow          literally everything (the periodic sweep)
    scripts/test-group.py list                 print the groups and exit

Anything after the group names is passed straight through to `dotnet test`, so
`--no-build` and `--logger ...` work as usual.

Why groups: the suite is ~724 tests and most changes touch one subsystem. Running the
matching group is seconds; running everything is minutes, nearly all of it spent in
tests that could not possibly be affected by the edit.
"""
import subprocess, sys

# Groups are matched as SUBSTRINGS of the fully-qualified test name, so a term like
# "Jump" catches JumpingStateTests, ArcJumpStateTests and VaultJumpAndLipReproTests
# alike. Namespaces are NOT a usable selector here — GrabTests lives in MTile.Tests
# while CaveMouthTests lives in MTile.Tests.Sim — which is why these are name terms.
#
# Overlap is deliberate and harmless: a class that belongs to two subsystems should run
# for either one. When adding a test, check its class name matches a term below; if it
# matches nothing it runs ONLY in the periodic full sweep, which is how coverage rots.
GROUPS = {
 "combat": ["Combat","Guard","Grab","Escalation","AttackRecoil","Commitment","HitResolver",
            "HitboxOcclusion","HitEviction","HitFlash","ActionOverlay","ActionAimSolver",
            "ClipBinding","DownAirSlash","InputParserGesture","RecoveryTransition","Laser",
            "Bird","TemplateEnemy","GauntletEnemy","ZeusHill","TelegraphList",
            "PresentationEventLog","SandImpactDamage","PlayerImpactByVelocity","ImpactCrater",
            "RunningOverUnderImpact","ChargedBlast"],
 "movement": ["Dropdown","Jump","Ledge","Mantle","ArcJump","WallJump","Tumble","StandingJitter",
              "GroundFriction","GroundChecker","MovingPlatform","StairClimb","DeliberateClimb",
              "ClimbArbitration","PreRunAirborne","CoveredJump","PhaseAccel","Bounce",
              "LandingContinuity","HardLandingRepro","Corridor"],
 "corrector": ["Corrector","Fold","Lattice","Ballistic","CaveMouth","BumpyTunnel","SpeedInvariant",
               "ReferencePath","ReferenceCorrector","ClearanceConstraint","VaultJump","DiveDrill",
               "SolverRestEquilibrium","CorrectionSolver"],
 "terrain": ["World","Block","Sprout","Foam","Terrain","SeamGuard","ExposedCorner","TileMassField",
             "Eruption","OneBlockTrigger","DenseTerrainScrub","BuriedInIntactTiles","OpenTailLoop",
             "ChargedBlockUse","BlockCharge","ChargedBlast","PullPoint"],
 "physics": ["Physics","HighSpeedTunneling","NoPenetrationSolver","FixedPointSolver","QrStep"],
 "animation": ["Anim","Pose","Skeleton","BoneMask","CharacterAnimator","MlsDeformer",
               "SpriteBinding","Smoothing","MotionProbe","ParkourGrip","TerrainNoPen"],
 "simcore": ["Snapshot","Rollback","InputCodec","Simulation","TwoPlayerStep","ConfigLayout",
             "TraceExport","StageSaver","Rtc","PracticeBall","TrainingStage","GauntletStage"],
}

# Excluded from every group AND from plain `full`; included only with --slow. These are
# real tests, not junk — they just cost more than the rest of the suite combined, so they
# belong in the periodic sweep rather than in an edit-test loop.
#   Zzz*                 the marked scratch / timing harnesses
#   BuriedInIntactTiles  2 tests that are the large majority of the suite's wall clock
SLOW = ["Zzz", "BuriedInIntactTiles"]

PROJ = "MTile.Tests/MTile.Tests.csproj"


def main(argv):
    if not argv or argv[0] in ("list", "-h", "--help"):
        print("groups:", " ".join(GROUPS), "| full | full --slow\n")
        for g, terms in GROUPS.items():
            print(f"  {g:<10} {','.join(terms)}")
        return 0

    names, passthru, want_slow, full = [], [], False, False
    for i, a in enumerate(argv):
        if a == "--slow":
            want_slow = True
        elif a == "full":
            full = True
        elif a.startswith("-"):
            passthru = argv[i:]
            break
        elif a in GROUPS:
            names.append(a)
        else:
            print(f"unknown group: {a}\nknown: {' '.join(GROUPS)} full", file=sys.stderr)
            return 2

    exclude = "" if want_slow else "".join(f"&FullyQualifiedName!~{c}" for c in SLOW)

    if full:
        flt = exclude.lstrip("&")
        label = "full" + (" (slow included)" if want_slow else "")
    else:
        if not names:
            print("no group given; try: test-group.py list", file=sys.stderr)
            return 2
        terms = [t for g in names for t in GROUPS[g]]
        flt = "(" + "|".join(f"FullyQualifiedName~{t}" for t in terms) + ")" + exclude
        label = " ".join(names) + (" (slow included)" if want_slow else "")

    cmd = ["dotnet", "test", PROJ] + (["--filter", flt] if flt else []) + passthru
    print(f"==> groups: {label}")
    return subprocess.call(cmd)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
