using System;

namespace MTile;

// The crystal phase machine shared by every crystal in the game (todo.txt #8 spike
// placement, and #7's spiked avalanche): a crystal is SEEDED (a small, harmless crystal
// appears), waits, FLASHES (a short, harmless warning), then is GROWN (the only phase
// that may damage), and — for lifecycles with a finite grown time — is then GONE.
//
// Deliberately a pure function of elapsed sim frames: a crystal stores only the
// TerrainFrame it was seeded on, and its phase is PhaseAt(TerrainFrame - seededFrame).
// Integer frames make the timing exact (no float clock drift between peers) and mean
// nothing is mutated per tick, so a rollback restores a crystal's clock just by
// restoring its seed frame. Each use site owns its own sparse store and decides what
// GROWN means (#8: promote to the 1-cell TileSpikes overlay; #7: a large damaging
// shape) — this type owns only the timing.
//
// Durations are code constants on the presets rather than MovementConfig dials: they
// are combat telegraph timings (sim-affecting, pinned by tests), not movement tuning,
// and MovementConfig hot-reload is the movement tuner. Promote them to a config if
// they ever need live tuning.
public enum CrystalPhase : byte
{
    Seed,
    Flash,
    Grown,
    Gone,
}

public readonly struct CrystalLifecycle
{
    // Phase lengths in sim frames. GrownFrames == Permanent → never reaches Gone.
    public readonly int SeedFrames;
    public readonly int FlashFrames;
    public readonly int GrownFrames;

    public const int Permanent = int.MaxValue;

    public CrystalLifecycle(int seedFrames, int flashFrames, int grownFrames = Permanent)
    {
        if (seedFrames < 0 || flashFrames < 0 || grownFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(seedFrames), "phase lengths are non-negative");
        SeedFrames = seedFrames; FlashFrames = flashFrames; GrownFrames = grownFrames;
    }

    // Seconds → frames at the fixed sim rate, rounded (1.0 s → 60, 0.25 s → 15).
    public static int Frames(float seconds) => (int)MathF.Round(seconds / Simulation.FixedDt);

    public static CrystalLifecycle FromSeconds(float seed, float flash, float grown = float.PositiveInfinity)
        => new(Frames(seed), Frames(flash), float.IsPositiveInfinity(grown) ? Permanent : Frames(grown));

    // Frames from seeding to the first Grown frame (the whole telegraph).
    public int FramesToGrown => SeedFrames + FlashFrames;

    public CrystalPhase PhaseAt(int elapsedFrames)
    {
        if (elapsedFrames < SeedFrames) return CrystalPhase.Seed;
        if (elapsedFrames < FramesToGrown) return CrystalPhase.Flash;
        if (GrownFrames == Permanent || elapsedFrames - FramesToGrown < GrownFrames) return CrystalPhase.Grown;
        return CrystalPhase.Gone;
    }

    // 0..1 progress through the current phase (render-side easing: a seed swelling, a
    // flash blinking). Grown under a Permanent lifecycle reports 1.
    public float PhaseProgress(int elapsedFrames)
    {
        static float Frac(int t, int len) => len <= 0 ? 1f : Math.Clamp(t / (float)len, 0f, 1f);
        return PhaseAt(elapsedFrames) switch
        {
            CrystalPhase.Seed  => Frac(elapsedFrames, SeedFrames),
            CrystalPhase.Flash => Frac(elapsedFrames - SeedFrames, FlashFrames),
            CrystalPhase.Grown => GrownFrames == Permanent ? 1f : Frac(elapsedFrames - FramesToGrown, GrownFrames),
            _                  => 1f,
        };
    }

    // ── Presets ─────────────────────────────────────────────────────────────────
    // todo.txt #8: a placed spike seeds for 1.0 s, flashes for 0.25 s, then is a
    // permanent 1-cell spike overlay (until its block breaks).
    public const float SpikeSeedSeconds  = 1.0f;
    public const float SpikeFlashSeconds = 0.25f;
    public static readonly CrystalLifecycle SpikePlacement = FromSeconds(SpikeSeedSeconds, SpikeFlashSeconds);
}
