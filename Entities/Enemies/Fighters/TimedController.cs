using System;
using System.Diagnostics;

namespace MTile;

// TOOL / TEST CODE ONLY — the league (FighterLeague, MTile.Bench --league) and
// FighterPackageTests wrap a fighter's brain in this to measure it. It reads the wall
// clock (Stopwatch) and keeps mutable counters, both of which the determinism rules
// forbid in the sim proper, so it must never be wired into the game loop, a stage, or a
// shipped spec. It is in the library (and therefore globbed into the KNI web build) only
// so Bench and the tests share one implementation; Stopwatch exists on both variants,
// and nothing on the game path constructs one.
//
// Timing never feeds back: the decorator forwards Decide unchanged and only reads
// FighterSenses' per-frame counters afterwards, so a timed bout is the same bout.

// Accumulators for one fighter in one bout. Create a fresh one per bout (the league
// does) — the counters are tool-side state and must not carry across bouts.
public sealed class DecideTimer
{
    public long Ticks;       // Stopwatch ticks spent inside the wrapped Decide
    public long Calls;       // Decide calls (one per frame the entity is alive and updated)
    public long PaidReads;   // Σ FighterSenses.PaidThisFrame after each Decide
    public long Queries;     // Σ FighterSenses.QueriesThisFrame after each Decide

    public double TotalMicros  => Ticks * 1e6 / Stopwatch.Frequency;
    public double MeanMicros   => Calls == 0 ? 0.0 : TotalMicros / Calls;
    public double PaidPerCall  => Calls == 0 ? 0.0 : (double)PaidReads / Calls;

    public void Add(DecideTimer o)
    {
        Ticks += o.Ticks; Calls += o.Calls; PaidReads += o.PaidReads; Queries += o.Queries;
    }
}

public sealed class TimedController : EnemyController
{
    public EnemyController Inner { get; }
    public DecideTimer     Timer { get; }

    public TimedController(EnemyController inner, DecideTimer timer)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Timer = timer ?? throw new ArgumentNullException(nameof(timer));
    }

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        long t0    = Stopwatch.GetTimestamp();
        var  input = Inner.Decide(in ctx);
        Timer.Ticks += Stopwatch.GetTimestamp() - t0;
        Timer.Calls++;
        // Only a FighterController runs through FighterSenses (Begin resets the counters
        // each Decide); touching Senses for any other brain would just allocate one.
        if (Inner is FighterController)
        {
            var s = ctx.Self.Senses;
            Timer.PaidReads += s.PaidThisFrame;
            Timer.Queries   += s.QueriesThisFrame;
        }
        return input;
    }

    // A spec brain factory that wraps whatever `brain` builds.
    public static Func<FighterSpec, EnemyController> Wrap(Func<FighterSpec, EnemyController> brain, DecideTimer timer)
        => s => new TimedController(brain(s), timer);

    // The brain under any number of timing wrappers.
    public static EnemyController Unwrap(EnemyController c)
    {
        while (c is TimedController t) c = t.Inner;
        return c;
    }
}
