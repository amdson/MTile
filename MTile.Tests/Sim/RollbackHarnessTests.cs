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
