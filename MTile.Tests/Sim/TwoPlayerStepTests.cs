using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// GGPO_PLAN stage 1 — the two-player Step(p0, p1) path plus the bot "spoof" that
// stands in for a network peer. Verifies (a) two real players run deterministically
// through one Simulation, (b) the bot actually moves + attacks, and (c) the
// two-player path survives a snapshot/restore round-trip (so rollback can sit on top).
public class TwoPlayerStepTests(ITestOutputHelper output)
{
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    // Primary at x=40, a bot-driven secondary at x=160 on the same floor.
    private static Simulation BuildSim(out (PlayerCharacter Player, Controller Ctrl) _)
    {
        var sim = new Simulation(Floor(), new Vector2(40f, 38f));
        sim.AddSecondaryPlayer(new Vector2(160f, 38f));
        _ = sim.SecondaryPlayers[0];
        return sim;
    }

    // P1 walks right + taps jump; P2 is the bot (deterministic given its seed).
    private static PlayerInput P1At(int frame) => new()
    {
        Right = true,
        Space = frame % 24 < 3,
    };

    private static int Bits(float f) => System.BitConverter.SingleToInt32Bits(f);

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
        return sb.ToString();
    }

    [Fact]
    public void TwoBots_RunIdentically_AcrossRepeatedRuns()
    {
        // Same seed + same P1 script ⇒ bit-identical traces. This is the determinism
        // claim stage 1 rests on: two real players through one Step(p0,p1) loop.
        const int N = 200;

        List<string> Run()
        {
            var sim = BuildSim(out _);
            var bot = new BotInputSource(seed: 7);
            var trace = new List<string>(N);
            for (int f = 0; f < N; f++)
            {
                sim.Step(P1At(f), bot.Poll(sim, f));
                trace.Add(Probe(sim));
            }
            return trace;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(a[i], b[i]);

        output.WriteLine($"Two-player run identical across {a.Count} frames over two runs.");
    }

    [Fact]
    public void Bot_ActuallyMovesAndAttacks()
    {
        const int N = 300;
        var sim = BuildSim(out _);
        var bot = new BotInputSource(seed: 7);
        var p2 = sim.SecondaryPlayers[0].Player;

        float startX = p2.Body.Position.X;
        float maxDrift = 0f;
        bool sawAttack = false;

        for (int f = 0; f < N; f++)
        {
            sim.Step(P1At(f), bot.Poll(sim, f));
            maxDrift = System.MathF.Max(maxDrift, System.MathF.Abs(p2.Body.Position.X - startX));
            // Any non-Idle/None action name means a slash/stab/etc. fired.
            if (p2.CurrentActionName != "None" && !string.IsNullOrEmpty(p2.CurrentActionName)
                && p2.CurrentActionName != "Idle")
                sawAttack = true;
        }

        Assert.True(maxDrift > 8f, $"Bot should wander; max horizontal drift was {maxDrift:F1}px");
        Assert.True(sawAttack, "Bot should trigger at least one attack action over 300 frames");
        output.WriteLine($"Bot drifted up to {maxDrift:F1}px and triggered attacks.");
    }

    [Fact]
    public void Players_DamageEachOther_ButNotThemselves()
    {
        // Per-player factions (Player1 vs Player2): P1 slashes toward an adjacent P2
        // and P2 must take damage, while P1's own health is untouched (self-immune).
        var sim = new Simulation(Floor(), new Vector2(40f, 38f));
        var (p2, _) = sim.AddSecondaryPlayer(new Vector2(64f, 38f));   // ~24px to P1's right
        var p1 = sim.Player;

        Assert.Equal(Faction.Player1, p1.Faction);
        Assert.Equal(Faction.Player2, p2.Faction);

        // P1 clicks (1-frame LMB press → Click → Slash) toward P2 every 12 frames;
        // P2 stands still. Aim the cursor at P2 so the slash arc sweeps over it.
        for (int f = 0; f < 90; f++)
        {
            var p1In = new PlayerInput
            {
                LeftClick          = f % 12 == 0,
                MouseWorldPosition = p2.Body.Position,
            };
            sim.Step(p1In, default);
        }

        // Direct hits feed the escalation percent now (Phase 5), not HP. P2 should
        // accrue percent from P1's slashes; P1 stays at 0 (self-immune).
        Assert.True(p2.Combat.DamagePercent > 0f,
            $"P2 should accrue percent from slashes (got {p2.Combat.DamagePercent}).");
        Assert.Equal(0f, p1.Combat.DamagePercent);   // attacker never hits itself
        output.WriteLine($"P2 percent {p2.Combat.DamagePercent}; P1 at {p1.Combat.DamagePercent}.");
    }

    [Fact]
    public void TwoPlayerPath_SnapshotRestore_RoundTrips()
    {
        // Stage 1 must not regress rollback-safety: snapshot mid-run, keep going, then
        // restore and replay the identical (P1 + bot) inputs — bit-for-bit.
        const int K = 40;
        const int N = 160;

        var sim = BuildSim(out _);
        // Pre-generate the bot input stream so live + replay feed identical P2 inputs
        // (the bot lives outside the sim and is NOT restored — see BotInputSource).
        var p2In = new PlayerInput[N];
        var simForInputs = BuildSim(out _);
        var botForInputs = new BotInputSource(seed: 7);
        for (int f = 0; f < N; f++)
        {
            p2In[f] = botForInputs.Poll(simForInputs, f);
            simForInputs.Step(P1At(f), p2In[f]);
        }

        for (int f = 0; f < K; f++) sim.Step(P1At(f), p2In[f]);
        var snap = sim.Snapshot();

        var liveTrace = new List<string>();
        for (int f = K; f < N; f++) { sim.Step(P1At(f), p2In[f]); liveTrace.Add(Probe(sim)); }

        sim.Restore(snap);
        var replayTrace = new List<string>();
        for (int f = K; f < N; f++) { sim.Step(P1At(f), p2In[f]); replayTrace.Add(Probe(sim)); }

        Assert.Equal(liveTrace.Count, replayTrace.Count);
        for (int i = 0; i < liveTrace.Count; i++)
        {
            if (liveTrace[i] != replayTrace[i])
            {
                output.WriteLine($"Divergence at replay frame {K + i}:");
                output.WriteLine("LIVE:\n"   + liveTrace[i]);
                output.WriteLine("REPLAY:\n" + replayTrace[i]);
            }
            Assert.Equal(liveTrace[i], replayTrace[i]);
        }
        output.WriteLine($"Two-player snapshot/restore identical across {liveTrace.Count} frames after restore@{K}.");
    }
}
