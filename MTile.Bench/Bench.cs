namespace MTile.Bench;

// Shared knobs for the harness.
internal static class Bench
{
    // Repetitions per scenario; the reported cost is the MINIMUM across them. Three is
    // enough to knock the observed 2× single-run spread down to a few percent without
    // tripling an already ~40 s run — the min converges much faster than a mean does,
    // because it is estimating a floor rather than averaging a heavy right tail.
    public const int Reps = 3;
}
