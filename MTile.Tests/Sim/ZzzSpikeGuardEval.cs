using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Scratch A/B for SpikeGuard in real bouts: the field's round robin with every brain's
// guard on vs off, tuning seeds. Spike HP is approximated as the 0.25-sized HP drops
// (SpikeContactSystem.ContactDamage; no attack deals exactly that). Run by hand, Release.
public class ZzzSpikeGuardEval(ITestOutputHelper output)
{
    private static readonly (string Name, DuelBot Bot)[] Field =
    {
        ("Bulldog", BulldogBot.Create), ("Rampart", RampartBot.Create), ("Riposte", RiposteBot.Create),
        ("Citadel", CitadelBot.Create), ("Brawler", BotDuel.Brawler),
    };

    private static SpikeGuard Guard(IRemoteInputSource b) => b switch
    {
        BulldogBot x => x.Spikes, RampartBot x => x.Spikes, RiposteBot x => x.Spikes,
        CitadelBot x => x.Spikes, BrawlerBotInputSource x => x.Spikes, _ => null,
    };

    [Fact]
    public void AB()
    {
        int[] seeds = { 1, 2, 3, 4 };
        foreach (bool on in new[] { false, true })
        {
            var spike = new ConcurrentDictionary<string, float>();
            var deaths = new ConcurrentDictionary<string, int>();
            var pts = new ConcurrentDictionary<string, float>();
            var jobs = new List<(int I, int J, string Arena, int Seed)>();
            for (int i = 0; i < Field.Length; i++)
            for (int j = 0; j < Field.Length; j++)
                if (i != j) foreach (var a in BotDuel.Arenas) foreach (int s in seeds) jobs.Add((i, j, a, s));
            Parallel.ForEach(jobs, job =>
            {
                var sim = BotDuel.BuildArena(job.Arena);
                var a = Field[job.I].Bot(-1, job.Seed * 2 + 1); var b = Field[job.J].Bot(0, job.Seed * 2 + 2);
                Guard(a).Enabled = on; Guard(b).Enabled = on;
                var pa = sim.Player; var pb = sim.SecondaryPlayers[0].Player;
                float prevA = 0, prevB = 0, sa = 0, sb = 0, ha = 0, hb = 0; int da = 0, db = 0;
                for (int f = 0; f < BotDuel.DefaultFrames; f++)
                {
                    var ia = a.Poll(sim, f); var ib = b.Poll(sim, f);
                    sim.Step(ia, ib);
                    float s0a = sa, s0b = sb;
                    Track(pa, ref prevA, ref sa, ref ha, ref da);
                    Track(pb, ref prevB, ref sb, ref hb, ref db);
                    // Confirm a 0.25 drop really was a spike (the spike system stamped the
                    // victim this frame) rather than a crystal contact; undo it if not.
                    if (sa > s0a || sb > s0b)
                    {
                        var cd = sim.Snapshot().SpikeCooldowns;
                        bool Stamped(PlayerCharacter p) => cd.TryGetValue(p.Id, out int fr) && sim.Frame - fr <= 1;
                        if (sa > s0a && !Stamped(pa)) { sa = s0a; NotSpike.AddOrUpdate("x", 1, (_, x) => x + 1); }
                        if (sb > s0b && !Stamped(pb)) { sb = s0b; NotSpike.AddOrUpdate("x", 1, (_, x) => x + 1); }
                    }
                }
                string an = Field[job.I].Name, bn = Field[job.J].Name;
                spike.AddOrUpdate(an, sa, (_, x) => x + sa); spike.AddOrUpdate(bn, sb, (_, x) => x + sb);
                deaths.AddOrUpdate(an, da, (_, x) => x + da); deaths.AddOrUpdate(bn, db, (_, x) => x + db);
                float w = db != da ? (db > da ? 1f : 0f) : (hb - ha >= 0.5f ? 1f : ha - hb >= 0.5f ? 0f : 0.5f);
                pts.AddOrUpdate(an, w, (_, x) => x + w); pts.AddOrUpdate(bn, 1f - w, (_, x) => x + 1f - w);
            });
            output.WriteLine($"guard {(on ? "ON" : "off")}: (points / {jobs.Count * 2 / Field.Length}, spike HP taken, deaths)");
            foreach (var (n, _) in Field)
                output.WriteLine($"  {n,-8} {pts.GetValueOrDefault(n),6:F1}  spike {spike.GetValueOrDefault(n),6:F1}  deaths {deaths.GetValueOrDefault(n)}");
        }
        output.WriteLine($"0.25 drops that were not spikes: {NotSpike.GetValueOrDefault("x")}");
    }

    private static readonly ConcurrentDictionary<string, int> NotSpike = new();

    private static void Track(PlayerCharacter p, ref float prev, ref float spike, ref float hp, ref int deaths)
    {
        float d = p.Combat.DamageTaken;
        if (d < prev) { deaths++; prev = 0f; }
        float drop = d - prev;
        if (drop > 0f) { hp += drop; if (MathF.Abs(drop - SpikeContactSystem.ContactDamage) < 0.01f) spike += drop; }
        prev = d;
    }
}
