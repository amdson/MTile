using System;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// SpikeGuard (Net/Bot/SpikeGuard.cs): with an idle P1 standing behind a carpet of its own
// spikes and the bot (P2) spawned on the carpet, a duelist must not tick itself to death.
public class BotManeuverSpikeGuardTests(ITestOutputHelper output)
{
    private static float T => Chunk.TileSize;

    // P1-owned spikes on the floor from `fromCol` to `toCol` (inclusive), P2's floor row.
    private static void Carpet(Simulation sim, int fromCol, int toCol)
    {
        var p2 = sim.SecondaryPlayers[0].Player;
        int fy = (int)MathF.Floor((p2.Body.Bounds.Bottom + SpikeContactSystem.FootReach + 1f) / T);
        for (int x = Math.Min(fromCol, toCol); x <= Math.Max(fromCol, toCol); x++)
            if (sim.Chunks.GetCellState(x, fy) == TileState.Solid && sim.Chunks.GetCellState(x, fy - 1) == TileState.Empty)
                SpikeTiles.PlaceArmed(sim.Chunks, x, fy - 1, Teams.Player);
    }

    private (float SpikeHurt, float Dealt, SpikeGuard G) Run(string bot, bool guard, int carpetHalf, int frames = 900)
    {
        var sim = BotDuel.BuildArena("plain_close");
        for (int i = 0; i < 30; i++) sim.Step(default, default);   // settle onto the floor
        var p1 = sim.Player; var p2 = sim.SecondaryPlayers[0].Player;
        int c2 = (int)MathF.Floor(p2.Body.Position.X / T), c1 = (int)MathF.Floor(p1.Body.Position.X / T);
        int dir = c1 < c2 ? -1 : 1;
        // Under P2 and toward P1, stopping 4 cells short of P1 (P1 stands on clean floor).
        Carpet(sim, c2 - dir * carpetHalf, c1 - dir * 4);

        IRemoteInputSource b = bot switch
        {
            "Bulldog" => BulldogBot.Create(0, 3), "Rampart" => RampartBot.Create(0, 3),
            "Riposte" => RiposteBot.Create(0, 3), "Citadel" => CitadelBot.Create(0, 3),
            _ => BotDuel.Brawler(0, 3),
        };
        var g = b switch
        {
            BulldogBot x => x.Spikes, RampartBot x => x.Spikes, RiposteBot x => x.Spikes,
            CitadelBot x => x.Spikes, BrawlerBotInputSource x => x.Spikes, _ => null,
        };
        g.Enabled = guard;

        float spikeHurt = 0f, prev = 0f, dealt = 0f, prevP1 = 0f;
        int spikes0 = SpikeTiles.Count(sim.Chunks);
        for (int f = 0; f < frames; f++)
        {
            var ib = b.Poll(sim, f);
            sim.Step(default, ib);
            float d = p2.Combat.DamageTaken;
            spikeHurt += d < prev ? d : d - prev;   // P1 is idle: everything P2 takes is spikes
            prev = d;
            float d1 = p1.Combat.DamageTaken;
            dealt += d1 < prevP1 ? d1 : d1 - prevP1;
            prevP1 = d1;
        }
        output.WriteLine($"{bot,-8} guard {guard,-5}: spike damage {spikeHurt:F2}, dealt {dealt:F2}, spikes {spikes0}->{SpikeTiles.Count(sim.Chunks)} " +
                         $"(escapes {g.Escapes}, hops {g.Hops}, stops {g.Stops}, clears {g.Clears}, steers {g.Steers})");
        return (spikeHurt, dealt, g);
    }

    [Theory]
    [InlineData("Bulldog")]
    [InlineData("Rampart")]
    [InlineData("Riposte")]
    [InlineData("Citadel")]
    [InlineData("Brawler")]
    public void Duelist_DoesNotTickItselfToDeath_OnSpikes(string bot)
    {
        var off = Run(bot, false, 3);
        var on  = Run(bot, true, 3);
        // Crossing a carpet to reach the target costs a tick or two by design; parking on
        // it (the bug: 7.5 HP over these 15 s) must not happen.
        Assert.True(on.SpikeHurt <= 4.0f, $"{bot} took {on.SpikeHurt:F2} HP from spikes with the guard on");
        Assert.True(on.SpikeHurt <= off.SpikeHurt + 1.0f, $"{bot}: guard on {on.SpikeHurt:F2} vs off {off.SpikeHurt:F2}");
    }
}
