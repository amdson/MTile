using System;
using System.Text;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Two headless peers over a modelled network: what does rollback COST, and does it still
// land on the right answer, as the link degrades?
//
// RollbackHarnessTests already gates basic correctness at one operating point. This sweeps
// the operating point and reports the cost distribution, because the two questions have
// different failure modes and only one of them is a wrong answer:
//
//   • correctness fails loudly    — peers disagree, or the desync checksum fires.
//   • performance fails quietly   — everything is correct, but a 30-frame rollback replays
//                                   30 Simulation.Steps inside one frame and the game
//                                   hitches. No assert catches that; you have to look.
//
// So the asserts here cover correctness and the structural bounds (nothing may exceed the
// snapshot ring), and the cost numbers are printed for reading. Run with:
//   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~RollbackNetSweep"
public class RollbackNetSweepTests(ITestOutputHelper output)
{
    private const int Frames = 300;   // 5 s of play at 60 Hz

    // Bit-exact state probe. Floats are compared as BITS: rollback must reconstruct the
    // timeline exactly, not merely closely, or the two peers' checksums will diverge later.
    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        static int Bits(float v) => BitConverter.SingleToInt32Bits(v);
        void P(PlayerCharacter p)
        {
            var b = p.Body;
            sb.Append($"P{p.Id}|{Bits(b.Position.X)},{Bits(b.Position.Y)};")
              .Append($"{Bits(b.Velocity.X)},{Bits(b.Velocity.Y)}|")
              .Append($"{p.CurrentStateName}/{p.CurrentActionName}|hp{Bits(p.Health)}|f{p.Frame}\n");
        }
        P(sim.Player);
        foreach (var (sp, _) in sim.SecondaryPlayers) P(sp);
        foreach (var e in sim.Entities)
        {
            var b = e.Body;
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(b.Position.X)},{Bits(b.Position.Y)};")
              .Append($"{Bits(b.Velocity.X)},{Bits(b.Velocity.Y)}\n");
        }
        return sb.ToString();
    }

    // The ground truth every degraded run must reproduce: the same inputs with a perfect
    // link, where no rollback ever fires.
    private static string Reference()
    {
        var h = new NetHarness(NetProfiles.Lan, NetProfiles.Lan, seed: 1,
                               NetHarness.Script(11), NetHarness.Script(22));
        h.RunTo(Frames, "reference");
        Assert.Equal(0, h.A.RollbackCount);      // a clean link must not need rollback:
        Assert.Equal(0, h.B.RollbackCount);      // InputFrameDelay already hides 1 frame
        return Probe(h.A.Sim);
    }

    [Fact]
    public void Sweep_EveryProfile_ConvergesOnTheCleanTimeline()
    {
        string truth = Reference();
        var rows = new StringBuilder();
        rows.AppendLine($"Two peers, {Frames} frames each, vs a clean-link reference.");
        rows.AppendLine("steps/frame = Simulation.Step calls in one tick across BOTH peers "
                      + "(1 forward each + rollback replay).");
        rows.AppendLine();

        foreach (var profile in NetProfiles.All)
        {
            var h = new NetHarness(profile, profile, seed: 99,
                                   NetHarness.Script(11), NetHarness.Script(22));
            var st = h.RunTo(Frames, profile.Name);
            st.Converged = Probe(h.A.Sim) == truth && Probe(h.B.Sim) == truth;
            rows.AppendLine(st.ToString() + (st.Converged ? "  OK" : "  DIVERGED"));

            // Correctness: both peers must land exactly on the clean timeline. This is the
            // assertion that matters — everything else is cost.
            Assert.True(Probe(h.A.Sim) == Probe(h.B.Sim),
                $"[{profile}] peers disagree with each other");
            Assert.True(st.Converged,
                $"[{profile}] converged peers do not match the clean-link reference");

            // Same build on both sides ⇒ deterministic ⇒ the desync guard must stay silent.
            // If this fires, the sim has non-determinism independent of the network.
            Assert.True(st.DesyncA + st.DesyncB == 0, $"[{profile}] desync guard fired");

            // Structural bound: a rollback deeper than the snapshot ring cannot be served,
            // so exceeding it means corrections are being dropped silently.
            Assert.True(Math.Max(st.WorstDepthA, st.WorstDepthB) < MTile.Net.RollbackSession.BufferLen,
                $"[{profile}] rollback depth {Math.Max(st.WorstDepthA, st.WorstDepthB)} "
                + $"reached the {MTile.Net.RollbackSession.BufferLen}-frame snapshot ring");
        }

        output.WriteLine(rows.ToString());
    }

    // Independent loss should never defeat the redundancy window: each input is resent
    // RedundancyWindow times, so losing every copy of one frame's input needs p^8 — about
    // 6.5e-5 even at 30% loss. If MissedRollbacks fires here, redundancy is not working the
    // way the protocol claims, which is a far more serious finding than a slow frame.
    [Fact]
    public void IndependentLoss_NeverOutrunsTheRedundancyWindow()
    {
        foreach (var profile in new[] { NetProfiles.Poor, NetProfiles.Awful })
        {
            var h = new NetHarness(profile, profile, seed: 4242,
                                   NetHarness.Script(11), NetHarness.Script(22));
            var st = h.RunTo(Frames, profile.Name);
            output.WriteLine(st.ToString());
            Assert.True(st.MissedA + st.MissedB == 0,
                $"[{profile}] {st.MissedA + st.MissedB} corrections were dropped — the "
                + "snapshot ring aged out under INDEPENDENT loss, which redundancy should "
                + "have covered");
        }
    }

    // Asymmetric link: fine downstream, awful upstream. Worth its own case because the
    // symmetric model hides it — the stall cap is one-directional, so the peer with the
    // good path is the one that ends up waiting.
    [Fact]
    public void AsymmetricLink_StallsTheFastSide_ButStillConverges()
    {
        string truth = Reference();
        var h = new NetHarness(ab: NetProfiles.Lan, ba: NetProfiles.Awful, seed: 7,
                               NetHarness.Script(11), NetHarness.Script(22));
        var st = h.RunTo(Frames, "asym");
        output.WriteLine(st.ToString());
        output.WriteLine($"stalls: A={st.StallsA} B={st.StallsB}  (A hears B late, so A waits)");

        Assert.Equal(Probe(h.A.Sim), Probe(h.B.Sim));
        Assert.Equal(truth, Probe(h.A.Sim));
        Assert.True(st.DesyncA + st.DesyncB == 0, "desync guard fired on an asymmetric link");
    }

    // Burst loss is the case redundancy cannot cover: a contiguous outage takes every copy
    // of a frame's input with it. This test does NOT assert zero missed corrections —
    // whether the current BufferLen/RedundancyWindow survives a given burst length is
    // exactly the open question. It asserts the guarantee that must hold regardless: if
    // corrections ARE dropped, the checksum guard has to notice rather than the peers
    // silently drifting apart.
    [Fact]
    public void BurstLoss_EitherRecovers_OrIsCaughtByTheDesyncGuard()
    {
        var h = new NetHarness(NetProfiles.Burst, NetProfiles.Burst, seed: 31337,
                               NetHarness.Script(11), NetHarness.Script(22));
        var st = h.RunTo(Frames, "burst");
        st.Converged = Probe(h.A.Sim) == Probe(h.B.Sim);
        output.WriteLine(st.ToString() + (st.Converged ? "  peers agree" : "  PEERS DIVERGED"));
        output.WriteLine($"missed corrections: A={st.MissedA} B={st.MissedB}");

        Assert.True(st.Converged || st.DesyncA + st.DesyncB > 0,
            "peers diverged under burst loss and the desync guard did NOT fire — a silent "
            + "desync is the one outcome the protocol must never produce");
    }
}
