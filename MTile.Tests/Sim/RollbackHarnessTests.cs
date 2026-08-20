using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// GGPO_PLAN stage 2 — the correctness gate, fully headless. Two RollbackSessions (one
// per player) run their own Simulation and exchange InputPackets through an in-memory
// link with injectable latency / loss / reorder. The invariants:
//   • a zero-latency link never triggers a rollback (input delay hides 1-tick delivery);
//   • a lossy/latent link DOES trigger rollbacks (the path is exercised);
//   • after settling, both peers' sims are bit-identical to each other AND to a clean
//     zero-latency reference — i.e. rollback faithfully reconstructs the true timeline.
//
// Local inputs are pure functions of frame (NOT of sim state), so the clean and lossy
// runs feed identical input streams and any divergence is a rollback bug, not input drift.
public class RollbackHarnessTests(ITestOutputHelper output)
{
    // ── In-memory transport with latency / drop / reorder ──────────────────────────
    private sealed class LossyLink
    {
        private struct Pending { public long DeliverTick; public InputPacket Packet; }
        private readonly List<Pending> _toA = new();
        private readonly List<Pending> _toB = new();
        private readonly Random _rng;
        private readonly int _minLat, _maxLat;
        private readonly double _drop;
        public int Sent, Dropped;

        public LossyLink(int seed, int minLat, int maxLat, double drop)
        { _rng = new Random(seed); _minLat = minLat; _maxLat = maxLat; _drop = drop; }

        public void SendToA(long now, in InputPacket p) => Enqueue(_toA, now, p);
        public void SendToB(long now, in InputPacket p) => Enqueue(_toB, now, p);

        private void Enqueue(List<Pending> q, long now, in InputPacket p)
        {
            Sent++;
            if (_rng.NextDouble() < _drop) { Dropped++; return; }   // packet lost
            int lat = _rng.Next(_minLat, _maxLat + 1);              // variable ⇒ reorder
            q.Add(new Pending { DeliverTick = now + lat, Packet = p });
        }

        public void DeliverDue(long now, List<InputPacket> toA, List<InputPacket> toB)
        {
            Drain(_toA, now, toA);
            Drain(_toB, now, toB);
        }

        public void DeliverAll(List<InputPacket> toA, List<InputPacket> toB)
        {
            Drain(_toA, long.MaxValue, toA);
            Drain(_toB, long.MaxValue, toB);
        }

        private static void Drain(List<Pending> q, long now, List<InputPacket> outList)
        {
            for (int i = q.Count - 1; i >= 0; i--)
                if (q[i].DeliverTick <= now) { outList.Add(q[i].Packet); q.RemoveAt(i); }
        }

        public bool Idle => _toA.Count == 0 && _toB.Count == 0;
    }

    private sealed class Harness
    {
        public readonly RollbackSession A, B;
        private readonly LossyLink _link;
        private long _tick;

        public Harness(LossyLink link, Func<int, PlayerInput> scriptA, Func<int, PlayerInput> scriptB,
                       Func<Simulation> buildA = null, Func<Simulation> buildB = null)
        {
            _link = link;
            A = new RollbackSession((buildA ?? BuildSim)(), localPlayer: 0, scriptA, p => _link.SendToB(_tick, p));
            B = new RollbackSession((buildB ?? BuildSim)(), localPlayer: 1, scriptB, p => _link.SendToA(_tick, p));
        }

        private readonly List<InputPacket> _toA = new();
        private readonly List<InputPacket> _toB = new();

        // Run until both peers reach frame N (capping each at N — a capped peer still
        // processes its inbox so late arrivals settle). Then a flush phase delivers all
        // in-flight packets so every remote frame is confirmed and rollbacks settle.
        public void RunTo(int n)
        {
            int guard = 0, guardMax = Math.Max(2000, n * 60);
            while ((A.Frame < n || B.Frame < n) && guard++ < guardMax)
            {
                Deliver(_link.DeliverDue);
                if (A.Frame < n) A.TryStep(); else A.ProcessInbox();
                if (B.Frame < n) B.TryStep(); else B.ProcessInbox();
                _tick++;
            }
            Assert.True(A.Frame >= n && B.Frame >= n,
                $"Peers failed to reach frame {n} (A={A.Frame}, B={B.Frame}) within {guardMax} ticks");

            // Flush: deliver everything (ignoring latency) and keep settling until the
            // link is idle and neither inbox has anything left to reconcile.
            guard = 0;
            do
            {
                _toA.Clear(); _toB.Clear();
                _link.DeliverAll(_toA, _toB);
                foreach (var p in _toA) A.Receive(p);
                foreach (var p in _toB) B.Receive(p);
                A.ProcessInbox();
                B.ProcessInbox();
            }
            while ((!_link.Idle || !A.InboxEmpty || !B.InboxEmpty) && guard++ < 10000);
        }

        private void Deliver(Action<long, List<InputPacket>, List<InputPacket>> deliver)
        {
            _toA.Clear(); _toB.Clear();
            deliver(_tick, _toA, _toB);
            foreach (var p in _toA) A.Receive(p);
            foreach (var p in _toB) B.Receive(p);
        }
    }

    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    // Both peers must build an IDENTICAL sim (same terrain, same spawns).
    private static Simulation BuildSim()
    {
        var sim = new Simulation(Floor(), new Vector2(60f, 38f));
        sim.AddSecondaryPlayer(new Vector2(110f, 38f));
        return sim;
    }

    // Deterministic per-frame input stream, independent of sim state. Holds a movement
    // direction for short runs and taps slash occasionally — enough to move both players
    // and exercise combat without spawning projectiles (no F / RMB).
    private static Func<int, PlayerInput> Script(int seed)
    {
        var rng = new Random(seed);
        var cache = new Dictionary<int, PlayerInput>();
        // Precompute held "decisions" deterministically per frame so repeated calls for
        // the same frame return the same input.
        bool left = false, right = false, space = false; int hold = 0;
        int built = -1;
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
            // Build sequentially up to f so the held-run RNG is deterministic per frame.
            for (int g = built + 1; g <= f; g++) cache[g] = Build(g);
            built = Math.Max(built, f);
            return cache[f];
        };
    }

    private static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        void P(PlayerCharacter p)
        {
            var b = p.Body;
            sb.Append($"P{p.Id}|{Bits(b.Position.X)},{Bits(b.Position.Y)};{Bits(b.Velocity.X)},{Bits(b.Velocity.Y)}|")
              .Append($"{p.CurrentStateName}/{p.CurrentActionName}|hp{Bits(p.Health)}|f{p.Frame}\n");
        }
        P(sim.Player);
        foreach (var (sp, _) in sim.SecondaryPlayers) P(sp);
        foreach (var e in sim.Entities)
        {
            var b = e.Body;
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(b.Position.X)},{Bits(b.Position.Y)};{Bits(b.Velocity.X)},{Bits(b.Velocity.Y)}\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void ZeroLatency_NeverRollsBack_PeersAgree()
    {
        const int N = 150;
        var link = new LossyLink(seed: 1, minLat: 1, maxLat: 1, drop: 0.0);
        var h = new Harness(link, Script(11), Script(22));
        h.RunTo(N);

        Assert.Equal(0, h.A.RollbackCount);
        Assert.Equal(0, h.B.RollbackCount);
        Assert.Equal(0, h.A.DesyncCount);
        Assert.Equal(0, h.B.DesyncCount);
        Assert.Equal(Probe(h.A.Sim), Probe(h.B.Sim));
        output.WriteLine($"Zero-latency: 0 rollbacks, 0 desyncs, peers identical at frame {N}.");
    }

    [Fact]
    public void LatencyAndLoss_RollsBack_AndReconstructsTheReference()
    {
        const int N = 150;

        // Reference: clean zero-latency run (no rollback) — the ground truth.
        var refLink = new LossyLink(seed: 1, minLat: 1, maxLat: 1, drop: 0.0);
        var reference = new Harness(refLink, Script(11), Script(22));
        reference.RunTo(N);
        string truth = Probe(reference.A.Sim);

        // Lossy: 3–9 ticks of jittery latency (⇒ reorder) + 25% packet loss.
        var lossyLink = new LossyLink(seed: 99, minLat: 3, maxLat: 9, drop: 0.25);
        var lossy = new Harness(lossyLink, Script(11), Script(22));
        lossy.RunTo(N);

        // The lossy path must have actually exercised rollback…
        Assert.True(lossy.A.RollbackCount > 0 || lossy.B.RollbackCount > 0,
            "Expected rollbacks under latency+loss");
        // …and still converged: peers agree, and match the clean reference exactly.
        string lossyA = Probe(lossy.A.Sim);
        string lossyB = Probe(lossy.B.Sim);
        if (lossyA != truth) { output.WriteLine("REFERENCE:\n" + truth); output.WriteLine("LOSSY A:\n" + lossyA); }
        Assert.Equal(lossyA, lossyB);
        Assert.Equal(truth, lossyA);
        // Same build ⇒ deterministic ⇒ the desync guard must stay silent.
        Assert.Equal(0, lossy.A.DesyncCount);
        Assert.Equal(0, lossy.B.DesyncCount);

        output.WriteLine($"Lossy run reconstructed the reference at frame {N}. " +
                         $"Rollbacks A={lossy.A.RollbackCount} B={lossy.B.RollbackCount}; " +
                         $"desyncs=0; link sent={lossyLink.Sent} dropped={lossyLink.Dropped}.");
    }

    // ── Terrain-aware repro ────────────────────────────────────────────────────────
    // Probe() above fingerprints players + entities only, and Simulation.Checksum() —
    // what the wire-level desync guard compares — covers bodies/health/entities and
    // likewise stops short of terrain. So a terrain-only divergence is invisible to
    // BOTH the existing tests and the runtime guard, while being the most visible
    // thing on screen. This pair adds the terrain signature to the comparison.

    // Same shape as SnapshotRoundTripTests.AppendTerrain: dense cell states over a
    // window, live sprout nodes with exact age, and per-cell damage HP.
    private static void AppendTerrain(ChunkMap chunks, StringBuilder sb)
    {
        for (int gty = -2; gty <= 8; gty++)
        {
            sb.Append("T");
            for (int gtx = -6; gtx <= 22; gtx++)
                sb.Append((int)chunks.GetCellState(gtx, gty));
            sb.Append('\n');
        }
        foreach (var sp in chunks.ActiveSprouts)
            sb.Append($"S{sp.Gtx},{sp.Gty}:{sp.Type}|age{Bits(sp.Age)}\n");
        var dmg = new List<string>();
        foreach (var d in chunks.Damage.Damaged) dmg.Add($"{d.Key.gtx},{d.Key.gty}={Bits(d.Value)}");
        dmg.Sort();
        foreach (var d in dmg) sb.Append("D").Append(d).Append('\n');
    }

    private static string ProbeFull(Simulation sim)
    {
        var sb = new StringBuilder(Probe(sim));
        AppendTerrain(sim.Chunks, sb);
        return sb.ToString();
    }

    // Like Script(), but deliberately hammers the terrain-editing verbs (place / break,
    // with block-type switches) at a spot that tracks the player. Still a pure function
    // of frame, so the clean and lossy runs see identical input streams.
    private static Func<int, PlayerInput> TerrainScript(int seed, float baseX)
    {
        var rng = new Random(seed);
        var cache = new Dictionary<int, PlayerInput>();
        bool left = false, right = false, space = false; int hold = 0;
        int built = -1;
        PlayerInput Build(int f)
        {
            if (hold <= 0)
            {
                hold = rng.Next(6, 16);
                int dir = rng.Next(3);
                left = dir == 1; right = dir == 2;
                space = rng.Next(4) == 0;
            }
            hold--;
            int pick = rng.Next(4);
            return new PlayerInput
            {
                Left = left, Right = right, Space = space,
                LeftClick  = rng.Next(3) == 0,     // break
                RightClick = rng.Next(3) == 0,     // place
                Num1 = pick == 0, Num2 = pick == 1, Num3 = pick == 2, Num4 = pick == 3,
                MouseWorldPosition = new Vector2(baseX + (f % 33), 24f + (f % 17)),
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

    [Fact]
    public void LatencyAndLoss_TerrainAlsoReconstructsTheReference()
    {
        const int N = 240;

        var refLink = new LossyLink(seed: 1, minLat: 1, maxLat: 1, drop: 0.0);
        var reference = new Harness(refLink, TerrainScript(11, 70f), TerrainScript(22, 95f));
        reference.RunTo(N);
        string truth = ProbeFull(reference.A.Sim);

        var lossyLink = new LossyLink(seed: 99, minLat: 3, maxLat: 9, drop: 0.25);
        var lossy = new Harness(lossyLink, TerrainScript(11, 70f), TerrainScript(22, 95f));
        lossy.RunTo(N);

        Assert.True(lossy.A.RollbackCount > 0 || lossy.B.RollbackCount > 0,
            "Expected rollbacks under latency+loss");

        string lossyA = ProbeFull(lossy.A.Sim);
        string lossyB = ProbeFull(lossy.B.Sim);

        output.WriteLine($"Rollbacks A={lossy.A.RollbackCount} B={lossy.B.RollbackCount}; " +
                         $"desyncs A={lossy.A.DesyncCount} B={lossy.B.DesyncCount}.");
        output.WriteLine(FirstDiff("A-vs-B", lossyA, lossyB));
        output.WriteLine(FirstDiff("truth-vs-A", truth, lossyA));

        Assert.Equal(lossyA, lossyB);
        Assert.Equal(truth, lossyA);
    }

    // Report the first differing line so a failure names the divergence instead of
    // dumping two multi-KB blobs.
    private static string FirstDiff(string label, string x, string y)
    {
        if (x == y) return $"{label}: identical";
        var a = x.Split('\n');
        var b = y.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string la = i < a.Length ? a[i] : "<missing>";
            string lb = i < b.Length ? b[i] : "<missing>";
            if (la != lb) return $"{label}: first diff at line {i}:\n  {la}\n  {lb}";
        }
        return $"{label}: differ in length only";
    }

    // ── Burst loss: what actually threatens the protocol ───────────────────────────
    // Independent per-packet loss (LossyLink) is the wrong model for a real link, where
    // loss is correlated — a Wi-Fi hiccup or a hand-off drops a run of packets, not a
    // scattering. That matters because the two constants defending against loss are very
    // differently sized: BufferLen=60 frames (1s) of snapshot ring, but RedundancyWindow=8
    // means frame f only rides in packets f..f+7. Lose all EIGHT and f is gone for good —
    // no later packet carries it — so the tighter bound is 8 frames (~133ms), not 60.
    //
    // The stall cap makes a total blackout harmless: _highestRemote stops advancing, both
    // peers stall, and they resume together. The dangerous shape is a burst that ends —
    // frames f..f+7 lost, f+8 delivered — because _highestRemote jumps the hole and the
    // sim walks on with an imputed input that can never be corrected.
    private sealed class BurstLink
    {
        private struct Pending { public long DeliverTick; public InputPacket Packet; }
        private readonly List<Pending> _toA = new(), _toB = new();
        private readonly Random _rng;
        private readonly int _lat, _burst, _gap;
        private int _sinceBurst;
        private int _burstLeft;
        public int Sent, Dropped, Bursts;
        public bool Blackout;   // hard-cut the B->A path (dead peer)

        // Every `gap` packets, drop `burst` consecutive ones.
        private readonly bool _oneWay;

        // oneWay: drop only on the B→A path. A symmetric blackout is the SAFE shape —
        // both peers' _highestRemote freeze, both stall, and they resume in step. One
        // direction failing is what lets A keep hearing... nothing, and walk past a hole.
        public BurstLink(int seed, int lat, int burst, int gap, bool oneWay = false)
        { _rng = new Random(seed); _lat = lat; _burst = burst; _gap = gap; _oneWay = oneWay; }

        public void SendToA(long now, in InputPacket p) => Enqueue(_toA, now, p, lossy: true);
        public void SendToB(long now, in InputPacket p) => Enqueue(_toB, now, p, lossy: !_oneWay);

        private void Enqueue(List<Pending> q, long now, in InputPacket p, bool lossy)
        {
            Sent++;
            if (Blackout && lossy) { Dropped++; return; }
            if (!lossy) { q.Add(new Pending { DeliverTick = now + _lat, Packet = p }); return; }
            if (_burstLeft > 0) { _burstLeft--; Dropped++; return; }
            if (++_sinceBurst >= _gap) { _sinceBurst = 0; _burstLeft = _burst - 1; Bursts++; Dropped++; return; }
            q.Add(new Pending { DeliverTick = now + _lat, Packet = p });
        }

        public void DeliverDue(long now, List<InputPacket> toA, List<InputPacket> toB)
        { Drain(_toA, now, toA); Drain(_toB, now, toB); }
        public void DeliverAll(List<InputPacket> toA, List<InputPacket> toB)
        { Drain(_toA, long.MaxValue, toA); Drain(_toB, long.MaxValue, toB); }
        private static void Drain(List<Pending> q, long now, List<InputPacket> outList)
        {
            for (int i = q.Count - 1; i >= 0; i--)
                if (q[i].DeliverTick <= now) { outList.Add(q[i].Packet); q.RemoveAt(i); }
        }
        public bool Idle => _toA.Count == 0 && _toB.Count == 0;
    }

    // Harness above is hardwired to LossyLink; this mirrors it over BurstLink.
    private sealed class BurstHarness
    {
        public readonly RollbackSession A, B;
        private readonly BurstLink _link;
        private long _tick;
        private readonly List<InputPacket> _toA = new(), _toB = new();

        public BurstHarness(BurstLink link, Func<int, PlayerInput> sa, Func<int, PlayerInput> sb)
        {
            _link = link;
            A = new RollbackSession(BuildSim(), 0, sa, p => _link.SendToB(_tick, p));
            B = new RollbackSession(BuildSim(), 1, sb, p => _link.SendToA(_tick, p));
        }

        public void RunTo(int n)
        {
            int guard = 0, guardMax = Math.Max(4000, n * 80);
            while ((A.Frame < n || B.Frame < n) && guard++ < guardMax)
            {
                _toA.Clear(); _toB.Clear();
                _link.DeliverDue(_tick, _toA, _toB);
                foreach (var p in _toA) A.Receive(p);
                foreach (var p in _toB) B.Receive(p);
                if (A.Frame < n) A.TryStep(); else A.ProcessInbox();
                if (B.Frame < n) B.TryStep(); else B.ProcessInbox();
                _tick++;
            }
            guard = 0;
            do
            {
                _toA.Clear(); _toB.Clear();
                _link.DeliverAll(_toA, _toB);
                foreach (var p in _toA) A.Receive(p);
                foreach (var p in _toB) B.Receive(p);
                A.ProcessInbox(); B.ProcessInbox();
            }
            while ((!_link.Idle || !A.InboxEmpty || !B.InboxEmpty) && guard++ < 10000);
        }
        public bool Reached(int n) => A.Frame >= n && B.Frame >= n;

        // Step both peers a fixed number of times with no target frame — for watching
        // what a peer does when the other is simply gone.
        public void RunFor(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                _toA.Clear(); _toB.Clear();
                _link.DeliverDue(_tick, _toA, _toB);
                foreach (var p in _toA) A.Receive(p);
                foreach (var p in _toB) B.Receive(p);
                A.TryStep(); B.TryStep();
                _tick++;
            }
        }
    }

    [Theory]
    // burst length in packets ≈ ms of outage at 60/s. 7 is inside RedundancyWindow=8;
    // 8 and 12 are past it.
    [InlineData(4,  120, false)]
    [InlineData(7,  120, false)]
    [InlineData(8,  120, false)]
    [InlineData(12, 120, false)]
    [InlineData(4,  120, true)]
    [InlineData(8,  120, true)]
    [InlineData(12, 120, true)]
    [InlineData(20, 120, true)]
    // Past MaxSendWindow (60): the re-send can no longer reach back far enough.
    [InlineData(45,  150, true)]
    [InlineData(90,  200, true)]
    public void BurstLoss_ShowsWhereTheProtocolBreaks(int burst, int gap, bool oneWay)
    {
        const int N = 400;
        var link = new BurstLink(seed: 7, lat: 2, burst: burst, gap: gap, oneWay: oneWay);
        var h = new BurstHarness(link, TerrainScript(11, 70f), TerrainScript(22, 95f));
        h.RunTo(N);

        string pa = h.Reached(N) ? ProbeFull(h.A.Sim) : "<did not reach N>";
        string pb = h.Reached(N) ? ProbeFull(h.B.Sim) : "<did not reach N>";
        bool agree = pa == pb;

        output.WriteLine(
            $"burst={burst} (~{burst * 1000 / 60}ms) gap={gap} oneWay={oneWay} bursts={link.Bursts} " +
            $"dropped={link.Dropped}/{link.Sent} | reachedN={h.Reached(N)} " +
            $"rollbacks A={h.A.RollbackCount} B={h.B.RollbackCount} " +
            $"worstDepth A={h.A.WorstRollbackDepth} B={h.B.WorstRollbackDepth} " +
            $"missed A={h.A.MissedRollbacks} B={h.B.MissedRollbacks} " +
            $"desync A={h.A.DesyncCount} B={h.B.DesyncCount} | peersAgree={agree}");
        if (!agree) output.WriteLine(FirstDiff("A-vs-B", pa, pb));

        // Measurement, not a gate: the point is the printed table. The one hard
        // invariant is that a rollback never needed a snapshot older than the ring —
        // if that trips, BufferLen (not RedundancyWindow) is the binding constraint.
        Assert.True(h.A.WorstRollbackDepth < RollbackSession.BufferLen
                 && h.B.WorstRollbackDepth < RollbackSession.BufferLen,
            $"rollback depth reached the {RollbackSession.BufferLen}-frame ring");
    }

    [Fact]
    public void OneDirectionalBurst_PastRedundancyWindow_StillConverges()
    {
        // This diverged permanently before the stall gate moved onto _confirmedThrough
        // and SendWindow became ack-driven. With a fixed redundancy window, frame f rode
        // only in packets f..f+7; losing all eight in ONE direction (the other stays
        // clean, so the far peer keeps producing frames and the old _highestRemote gate
        // never engaged) made f unrecoverable, the imputed input stood forever, and the
        // sims parted — with DesyncCount stuck at 0, because a permanent hole pins
        // _confirmedThrough and CheckPendingChecksums skips every later claim.
        //
        // Now the sender re-sends from the peer's ack until it lands, and the receiver
        // will not step past a frame it could no longer correct, so neither half of that
        // can happen. Guarding both halves at once: peers must AGREE (no fork) and must
        // REACH N (no deadlock — a stalled peer keeps transmitting so acks still flow).
        const int N = 400;
        var link = new BurstLink(seed: 7, lat: 2, burst: 12, gap: 120, oneWay: true);
        var h = new BurstHarness(link, TerrainScript(11, 70f), TerrainScript(22, 95f));
        h.RunTo(N);
        Assert.True(h.Reached(N), "peers deadlocked instead of healing the hole");
        Assert.Equal(ProbeFull(h.A.Sim), ProbeFull(h.B.Sim));
    }

    [Fact]
    public void DeadPeer_StallsRatherThanForking_AndReportsIt()
    {
        // A peer that goes away for good. The gate holds the survivor at
        // _confirmedThrough + slack forever, which is correct — it must not invent a
        // timeline it can never reconcile — but silence would read as a hang, so
        // OnStallTimeout names the frame we are stuck behind.
        const int Cut = 40;
        var link = new BurstLink(seed: 3, lat: 1, burst: 1, gap: int.MaxValue);
        var h = new BurstHarness(link, TerrainScript(11, 70f), TerrainScript(22, 95f));
        h.RunTo(Cut);

        int stalledAt = -1;
        h.A.OnStallTimeout = f => stalledAt = f;

        // Cut the B→A path entirely and let A spin well past the timeout.
        link.Blackout = true;
        int before = h.A.Frame;
        h.RunFor(RollbackSession.StallTimeoutSteps + 60);

        // The invariant is about distance from CONFIRMED input, not from the cut: packets
        // already in flight keep confirming for a tick or two after the link dies, so the
        // frame count advances slightly more than the slack.
        int ahead = h.A.Frame - h.A.ConfirmedThrough;
        output.WriteLine($"A advanced {h.A.Frame - before} frames after the cut, now {ahead} " +
                         $"ahead of confirmed (gate allows {RollbackSession.InputFrameDelay + RollbackSession.StallSlack}); " +
                         $"stalls={h.A.StallCount} timeoutAtFrame={stalledAt}");

        Assert.True(ahead <= RollbackSession.InputFrameDelay + RollbackSession.StallSlack,
            $"survivor ran {ahead} frames past the last confirmed input — further than it could correct");
        Assert.True(stalledAt >= 0, "OnStallTimeout never fired for a dead peer");
    }

    [Fact]
    public void DesyncGuard_FiresWhenSimsDiverge()
    {
        // Force a divergence the checksum is meant to catch: peer B builds its sim with
        // a slightly shifted spawn. Inputs are identical (sim-independent scripts), so
        // the protocol runs normally — but the two sims' state never matches, and once
        // frames confirm on both ends the checksum claims disagree.
        const int N = 60;
        Simulation Diverged()
        {
            var sim = new Simulation(Floor(), new Vector2(60.5f, 38f));   // +0.5px on P1
            sim.AddSecondaryPlayer(new Vector2(110f, 38f));
            return sim;
        }

        var link = new LossyLink(seed: 5, minLat: 1, maxLat: 1, drop: 0.0);
        var h = new Harness(link, Script(11), Script(22), buildA: BuildSim, buildB: Diverged);
        h.RunTo(N);

        Assert.True(h.A.DesyncCount > 0 || h.B.DesyncCount > 0,
            "Desync guard should fire when the two sims diverge");
        output.WriteLine($"Desync guard fired: A={h.A.DesyncCount} B={h.B.DesyncCount} claims.");
    }
}
