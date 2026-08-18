using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Net;

namespace MTile.Tests.Sim;

// Shared two-peer headless rollback harness: a network model plus a driver that runs two
// RollbackSessions against each other, collecting cost and health statistics.
//
// This is a superset of the private harness inside RollbackHarnessTests (which predates it
// and still owns the basic correctness gates). The additions are the ones you need to ask
// "what does a BAD network cost us?" rather than "is rollback correct?":
//
//   • burst loss     — real links drop in runs, not independently. This matters more than
//                      any other knob: RedundancyWindow resends each input 8 times, so
//                      independent loss has to hit ~all 8 copies (p^8) to cause damage,
//                      while a burst takes out a contiguous span of frames at once.
//   • asymmetry      — the two directions get their own latency/loss. A link that is fine
//                      one way and awful the other is common (upstream vs downstream) and
//                      produces one-sided stalls the symmetric model never shows.
//   • per-frame cost — resim steps and stalls, sampled per driver tick, so the output is a
//                      distribution rather than a total. A run averaging 1.2 resim steps
//                      per frame but spiking to 30 is a stutter, and the mean hides it.
//
// Everything here is test-only: no sim state depends on it.
internal static class NetProfiles
{
    // (name, one-way latency in sim frames, jitter, independent loss, burst loss)
    public static readonly NetConditions Lan   = new("lan",   1, 0,  0.00, 0.0, 0);
    public static readonly NetConditions Good  = new("good",  3, 1,  0.01, 0.0, 0);
    public static readonly NetConditions Poor  = new("poor",  6, 4,  0.10, 0.0, 0);
    public static readonly NetConditions Awful = new("awful", 10, 8, 0.30, 0.0, 0);
    // Bursty: modest average loss, but it arrives in runs of ~6 frames — the shape that
    // actually defeats redundancy.
    public static readonly NetConditions Burst = new("burst", 4, 2,  0.00, 0.04, 6);

    public static readonly NetConditions[] All = { Lan, Good, Poor, Awful, Burst };
}

// One direction's (or both directions') link parameters, in SIM FRAMES rather than ms —
// the harness ticks at one sim frame per tick, so 1 tick = 1/60 s at the shipped rate.
internal readonly struct NetConditions(string name, int latency, int jitter,
                                       double loss, double burstStart, int burstLen)
{
    public string Name        { get; } = name;
    public int    Latency     { get; } = latency;
    public int    Jitter      { get; } = jitter;      // ± this many frames ⇒ reorder
    public double Loss        { get; } = loss;        // independent per-packet drop
    public double BurstStart  { get; } = burstStart;  // per-packet chance of opening a burst
    public int    BurstLen    { get; } = burstLen;    // packets swallowed once a burst opens

    public int    RoundTrip   => 2 * Latency;
    public override string ToString()
        => $"{Name} (lat {Latency}±{Jitter}f, loss {Loss:P0}"
           + (BurstLen > 0 ? $", bursts {BurstStart:P0}×{BurstLen}f)" : ")");
}

// One direction of the link. Held separately per direction so a run can be asymmetric.
internal sealed class NetPath(int seed, NetConditions c)
{
    private struct Pending { public long DeliverTick; public InputPacket Packet; }
    private readonly List<Pending> _q = new();
    private readonly Random _rng = new(seed);
    private readonly NetConditions _c = c;
    private int _burstLeft;

    public int Sent, Dropped, DroppedToBurst;

    public void Send(long now, in InputPacket p)
    {
        Sent++;

        // Burst loss is checked FIRST and is stateful: once a burst opens it swallows the
        // next BurstLen packets unconditionally. That correlation is the whole point — the
        // redundancy window resends an input 8 times, so only a contiguous outage can take
        // every copy of a given frame's input with it.
        if (_burstLeft > 0) { _burstLeft--; Dropped++; DroppedToBurst++; return; }
        if (_c.BurstLen > 0 && _rng.NextDouble() < _c.BurstStart)
        {
            _burstLeft = _c.BurstLen - 1;
            Dropped++; DroppedToBurst++;
            return;
        }
        if (_rng.NextDouble() < _c.Loss) { Dropped++; return; }

        int lat = Math.Max(0, _c.Latency + _rng.Next(-_c.Jitter, _c.Jitter + 1));
        _q.Add(new Pending { DeliverTick = now + lat, Packet = p });
    }

    public void DeliverDue(long now, List<InputPacket> outList)
    {
        for (int i = _q.Count - 1; i >= 0; i--)
            if (_q[i].DeliverTick <= now) { outList.Add(_q[i].Packet); _q.RemoveAt(i); }
    }

    public void DeliverAll(List<InputPacket> outList) => DeliverDue(long.MaxValue, outList);
    public bool Idle => _q.Count == 0;
}

// What a run cost. Frame-indexed samples, not just totals — see the note above.
internal sealed class NetRunStats
{
    public string Profile;
    public int    Frames;
    public int    RollbacksA, RollbacksB;
    public long   ResimA, ResimB;
    public int    WorstDepthA, WorstDepthB;
    public int    StallsA, StallsB;
    public int    MissedA, MissedB;
    public int    DesyncA, DesyncB;
    public int    SentAB, DroppedAB, SentBA, DroppedBA;
    public bool   Converged;

    // Simulation.Steps executed in one driver tick, across both peers: 1 normal step plus
    // whatever a rollback replayed. This is the number the frame budget actually sees.
    public readonly List<int> StepsPerFrame = new();

    public double MeanStepsPerFrame
    {
        get
        {
            if (StepsPerFrame.Count == 0) return 0;
            long s = 0;
            foreach (int v in StepsPerFrame) s += v;
            return s / (double)StepsPerFrame.Count;
        }
    }

    public int WorstStepsPerFrame
    {
        get { int m = 0; foreach (int v in StepsPerFrame) if (v > m) m = v; return m; }
    }

    public int StepsPercentile(double q)
    {
        if (StepsPerFrame.Count == 0) return 0;
        var copy = new List<int>(StepsPerFrame);
        copy.Sort();
        return copy[Math.Min(copy.Count - 1, (int)(copy.Count * q))];
    }

    public override string ToString()
        => $"{Profile,-6} rollbacks {RollbacksA + RollbacksB,4}  resim {ResimA + ResimB,5}  "
         + $"worst depth {Math.Max(WorstDepthA, WorstDepthB),3}  "
         + $"steps/frame mean {MeanStepsPerFrame,5:0.00} p99 {StepsPercentile(0.99),3} worst {WorstStepsPerFrame,3}  "
         + $"stalls {StallsA + StallsB,4}  missed {MissedA + MissedB,3}  "
         + $"desync {DesyncA + DesyncB,2}  loss {DroppedAB + DroppedBA,4}/{SentAB + SentBA,4}";
}

internal sealed class NetHarness
{
    public readonly RollbackSession A, B;
    private readonly NetPath _aToB, _bToA;
    private readonly List<InputPacket> _toA = new(), _toB = new();
    private long _tick;

    public NetHarness(NetConditions ab, NetConditions ba, int seed,
                      Func<int, PlayerInput> scriptA, Func<int, PlayerInput> scriptB,
                      Func<Simulation> buildA = null, Func<Simulation> buildB = null)
    {
        _aToB = new NetPath(seed * 7919 + 1, ab);
        _bToA = new NetPath(seed * 7919 + 2, ba);
        A = new RollbackSession((buildA ?? BuildSim)(), localPlayer: 0, scriptA,
                                p => _aToB.Send(_tick, p));
        B = new RollbackSession((buildB ?? BuildSim)(), localPlayer: 1, scriptB,
                                p => _bToA.Send(_tick, p));
    }

    // Drive both peers to frame n, sampling per-tick cost, then flush the link so every
    // in-flight input is delivered and any outstanding rollback settles. Without the flush
    // the peers would be compared mid-correction and could differ legitimately.
    public NetRunStats RunTo(int n, string profileName)
    {
        var st = new NetRunStats { Profile = profileName, Frames = n };
        long prevResim = 0;

        int guard = 0, guardMax = Math.Max(4000, n * 80);
        while ((A.Frame < n || B.Frame < n) && guard++ < guardMax)
        {
            Deliver(_tick);

            int stepsThisTick = 0;
            if (A.Frame < n) { if (A.TryStep()) stepsThisTick++; } else A.ProcessInbox();
            if (B.Frame < n) { if (B.TryStep()) stepsThisTick++; } else B.ProcessInbox();

            // Resim replayed inside this tick's ProcessInbox calls, on top of the forward
            // steps above. Sampled as a delta so it is per-frame, not cumulative.
            long resimNow = A.ResimSteps + B.ResimSteps;
            stepsThisTick += (int)(resimNow - prevResim);
            prevResim = resimNow;
            st.StepsPerFrame.Add(stepsThisTick);

            _tick++;
        }

        // Flush: deliver everything regardless of latency and keep reconciling until the
        // link is idle and both inboxes are drained.
        guard = 0;
        do
        {
            _toA.Clear(); _toB.Clear();
            _bToA.DeliverAll(_toA);
            _aToB.DeliverAll(_toB);
            foreach (var p in _toA) A.Receive(p);
            foreach (var p in _toB) B.Receive(p);
            A.ProcessInbox();
            B.ProcessInbox();
        }
        while ((!_aToB.Idle || !_bToA.Idle || !A.InboxEmpty || !B.InboxEmpty) && guard++ < 10000);

        st.RollbacksA = A.RollbackCount;  st.RollbacksB = B.RollbackCount;
        st.ResimA = A.ResimSteps;         st.ResimB = B.ResimSteps;
        st.WorstDepthA = A.WorstRollbackDepth; st.WorstDepthB = B.WorstRollbackDepth;
        st.StallsA = A.StallCount;        st.StallsB = B.StallCount;
        st.MissedA = A.MissedRollbacks;   st.MissedB = B.MissedRollbacks;
        st.DesyncA = A.DesyncCount;       st.DesyncB = B.DesyncCount;
        st.SentAB = _aToB.Sent;           st.DroppedAB = _aToB.Dropped;
        st.SentBA = _bToA.Sent;           st.DroppedBA = _bToA.Dropped;
        return st;
    }

    private void Deliver(long now)
    {
        _toA.Clear(); _toB.Clear();
        _bToA.DeliverDue(now, _toA);
        _aToB.DeliverDue(now, _toB);
        foreach (var p in _toA) A.Receive(p);
        foreach (var p in _toB) B.Receive(p);
    }

    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    // Both peers must build an IDENTICAL sim (same terrain, same spawns).
    public static Simulation BuildSim()
    {
        var sim = new Simulation(Floor(), new Vector2(60f, 38f));
        sim.AddSecondaryPlayer(new Vector2(110f, 38f));
        return sim;
    }

    // Deterministic per-frame input, a pure function of frame number — NOT of sim state.
    // That independence is what makes a clean run usable as ground truth: the lossy run
    // feeds the identical input stream, so any divergence is a rollback bug rather than
    // input drift.
    public static Func<int, PlayerInput> Script(int seed)
    {
        var rng = new Random(seed);
        var cache = new Dictionary<int, PlayerInput>();
        bool left = false, right = false, space = false;
        int hold = 0, built = -1;

        PlayerInput Build(int f)
        {
            if (hold <= 0)
            {
                hold = rng.Next(6, 16);
                int dir = rng.Next(3);
                left = dir == 1; right = dir == 2;
                space = rng.Next(3) == 0;
            }
            hold--;
            return new PlayerInput
            {
                Left = left, Right = right, Space = space,
                LeftClick = rng.Next(10) == 0,
                MouseWorldPosition = new Vector2(80f + (f % 40), 30f),
            };
        }
        return f =>
        {
            if (cache.TryGetValue(f, out var pi)) return pi;
            for (int g = built + 1; g <= f; g++) cache[g] = Build(g);
            built = Math.Max(built, f);
            return cache[f];
        };
    }
}
