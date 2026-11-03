using System;
using System.Collections.Generic;
using System.Linq;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class BotManeuverDuelistBulldogTests(ITestOutputHelper output)
{
    [Fact]
    public void Diag()
    {
        int frames = int.Parse(Environment.GetEnvironmentVariable("BD_FRAMES") ?? "1800");
        var seeds = (Environment.GetEnvironmentVariable("BD_SEEDS") ?? "1,2").Split(',').Select(int.Parse).ToList();
        var arenas = (Environment.GetEnvironmentVariable("BD_ARENAS") ?? string.Join(",", BotDuel.Arenas)).Split(',');
        string opp = Environment.GetEnvironmentVariable("BD_OPP") ?? "brawler";
        var dealt = new Dictionary<string, float>(); var taken = new Dictionary<string, float>();
        var starts = new Dictionary<string, int>();
        foreach (var arena in arenas)
        foreach (int seed in seeds)
        for (int side = 0; side < 2; side++)
        {
            var sim = BotDuel.BuildArena(arena);
            var me = new BulldogBot(seed * 7 + side, side == 0 ? -1 : 0);
            IRemoteInputSource them = opp == "mirror" ? new BulldogBot(seed * 13 + side, side == 0 ? 0 : -1)
                                                      : new BrawlerBotInputSource(seed * 13 + side, side == 0 ? 0 : -1);
            var mine = side == 0 ? sim.Player : sim.SecondaryPlayers[0].Player;
            var their = side == 0 ? sim.SecondaryPlayers[0].Player : sim.Player;
            float pm = 0, pt = 0;
            var lastMine = new Queue<string>(); var lastTheir = new Queue<string>();
            string myMan = "-";
            for (int f = 0; f < frames; f++)
            {
                var im = me.Poll(sim, f); var it = them.Poll(sim, f);
                myMan = me.Active?.Name ?? myMan;
                if (side == 0) sim.Step(im, it); else sim.Step(it, im);
                string ma = mine.CurrentActionName, ta = their.CurrentActionName;
                if (ma != "NullAction" && ma != "RecoveryAction") { lastMine.Enqueue(ma + "/" + myMan); if (lastMine.Count > 1) lastMine.Dequeue(); }
                if (ta != "NullAction" && ta != "RecoveryAction") { lastTheir.Enqueue(ta); if (lastTheir.Count > 1) lastTheir.Dequeue(); }
                float dm = mine.Combat.DamageTaken, dt = their.Combat.DamageTaken;
                if (dm > pm) { string k = lastTheir.Count > 0 ? lastTheir.Peek() : "?"; taken[k] = taken.GetValueOrDefault(k) + dm - pm; }
                if (dt > pt) { string k = lastMine.Count > 0 ? lastMine.Peek() : "?"; dealt[k] = dealt.GetValueOrDefault(k) + dt - pt; }
                pm = dm < pm ? 0 : dm; pt = dt < pt ? 0 : dt;
                if (dm < pm) pm = dm;
            }
            foreach (var kv in me.Starts) starts[kv.Key] = starts.GetValueOrDefault(kv.Key) + kv.Value;
        }
        output.WriteLine("] DEALT " + string.Join(", ", dealt.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value:F1}")));
        output.WriteLine("] TAKEN " + string.Join(", ", taken.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value:F1}")));
        output.WriteLine("] STARTS " + string.Join(", ", starts.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
    }

    [Fact]
    public void DiagCombo()
    {
        var sim = BotDuel.BuildArena("plain_close");
        var b = new BulldogBot(1, 0);
        b.Maneuvers.RemoveAll(m => m is not BulldogString);
        var me = sim.SecondaryPlayers[0].Player; var dummy = sim.Player;
        float prev = 0; string lastA = "";
        for (int f = 0; f < 600; f++)
        {
            var i = b.Poll(sim, f);
            sim.Step(default, i);
            string a = me.CurrentActionName;
            if (a != lastA) output.WriteLine($"] f{f} me {a} dx {dummy.Body.Position.X - me.Body.Position.X:F0} click {i.LeftClick}");
            lastA = a;
            if (dummy.Combat.DamageTaken != prev) output.WriteLine($"] f{f} HIT {dummy.Combat.DamageTaken - prev:F2} by {a} stun {dummy.Combat.HitstunActive}");
            prev = dummy.Combat.DamageTaken;
        }
    }

    [Fact]
    public void DiagBout()
    {
        string arena = Environment.GetEnvironmentVariable("BD_ARENAS") ?? "hall_close";
        int frames = int.Parse(Environment.GetEnvironmentVariable("BD_FRAMES") ?? "900");
        var sim = BotDuel.BuildArena(arena);
        var b = new BulldogBot(3, 0);
        var br = new BrawlerBotInputSource(4, -1);
        var me = sim.SecondaryPlayers[0].Player; var op = sim.Player;
        float pm = 0, po = 0; string la = "", lo = "";
        for (int f = 0; f < frames; f++)
        {
            var i = b.Poll(sim, f); var j = br.Poll(sim, f);
            sim.Step(j, i);
            string a = me.CurrentActionName, o = op.CurrentActionName;
            float dx = op.Body.Position.X - me.Body.Position.X;
            if (a != la || o != lo)
                output.WriteLine($"] f{f} dx {dx,4:F0} dy {op.Body.Position.Y - me.Body.Position.Y,4:F0} me {a}({b.Active?.Name}) {me.CurrentStateName} | op {o}({br.Active?.Name})");
            la = a; lo = o;
            if (me.Combat.DamageTaken != pm) output.WriteLine($"]   ***** f{f} I TOOK {me.Combat.DamageTaken - pm:F2}");
            if (op.Combat.DamageTaken != po) output.WriteLine($"]   +++++ f{f} I DEALT {op.Combat.DamageTaken - po:F2}");
            pm = me.Combat.DamageTaken; po = op.Combat.DamageTaken;
        }
    }

    // Proxy opponents for robustness checks (the real league bots are unseen): stock
    // maneuvers re-weighted into a zoner and a counter-puncher.
    internal static DuelBot Opponent(string kind) => kind switch
    {
        "zoner" => (self, seed) => new BrawlerBotInputSource(seed, self, new BotManeuver[]
        {
            new EscapeGrabManeuver(), new GuardManeuver(), new PulseManeuver(),
            new SlashManeuver(), new BurstManeuver(),
            new BlockThrowManeuver { CooldownFrames = 90 },
            new EruptionManeuver(),
            new CrystalWaveManeuver { StaticUtility = 1.1f },
            new WallUpManeuver { ApproachSpeed = 10f },
            new BlockBurstManeuver { OpenUtility = 0.9f },
            new BackoffManeuver { RunDist = 90f, CloseDist = 80f, CrowdDist = 40f, Utility = 0.95f, MaxFrames = 40 },
        }),
        "counter" => (self, seed) => new BrawlerBotInputSource(seed, self, new BotManeuver[]
        {
            new EscapeGrabManeuver(), new GuardManeuver { ThreatRange = 90f }, new PulseManeuver(),
            new GrabManeuver(), new SlashManeuver(), new CrouchSlashManeuver(), new StabManeuver(),
            new BackoffManeuver { Utility = 0.95f },
        }) { EngageRange = 50f },
        _ => BotDuel.Brawler,
    };

    static IRemoteInputSource Configured(int self, int seed)
    {
        var b = new BulldogBot(seed, self);
        string e(string k) => Environment.GetEnvironmentVariable(k);
        if (e("BD_ENGAGE") is string en) b.EngageRange = float.Parse(en);
        if (e("BD_RETREAT") is string r) b.RetreatInLag = r == "1";
        if (e("BD_RUNTIL") is string ru) b.RetreatUntil = int.Parse(ru);
        if (e("BD_RDIST") is string rd) b.RetreatDist = float.Parse(rd);
        var st = b.Maneuvers.OfType<BulldogString>().First();
        if (e("BD_TAP2") is string t2) st.Tap2 = int.Parse(t2);
        if (e("BD_TAP3") is string t3) st.Tap3 = int.Parse(t3);
        if (e("BD_STAB") is string sb) b.Maneuvers.OfType<BulldogStab>().First().MixShare = double.Parse(sb);
        if (e("BD_GRAB") is string gb) b.Maneuvers.OfType<BulldogGrab>().First().MixShare = double.Parse(gb);
        if (e("BD_DROP") is string drop)
            foreach (var n in drop.Split(',')) b.Maneuvers.RemoveAll(m => m.Name == n);
        return b;
    }

    [Fact]
    public void Scratch()
    {
        int frames = int.Parse(Environment.GetEnvironmentVariable("BD_FRAMES") ?? "1800");
        var seeds = (Environment.GetEnvironmentVariable("BD_SEEDS") ?? "1,2").Split(',').Select(int.Parse).ToList();
        var arenas = (Environment.GetEnvironmentVariable("BD_ARENAS") ?? string.Join(",", BotDuel.Arenas)).Split(',');
        var bots = new List<(string, DuelBot)> { ("Bulldog", Configured), ("Brawler", Opponent(Environment.GetEnvironmentVariable("BD_OPP") ?? "brawler")) };
        bool verbose = Environment.GetEnvironmentVariable("BD_VERBOSE") == "1";
        foreach (var arena in arenas)
        {
            var t = BotDuel.RoundRobin(bots, seeds, new[] { arena }, frames, verbose ? s => output.WriteLine(s) : null);
            foreach (var s in t) if (s.Name == "Bulldog") output.WriteLine($"[{arena}] {s}");
        }
    }
}
