using System;
using System.Collections.Generic;
using System.Linq;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Rampart (Net/Bot/Duelists/Rampart): the terrain-zoner duelist. Name matches the
// "BotManeuver" term, so it runs in the bot group.
public class BotManeuverDuelistRampartTests(ITestOutputHelper output)
{
    // Rampart with every maneuver but `keep` switched off, driving P2 against an AFK P1.
    private (Simulation sim, RampartBot bot) Solo(string arena, int frames, params string[] keep)
        => Solo(arena, frames, true, keep);

    private (Simulation sim, RampartBot bot) Solo(string arena, int frames, bool advance, params string[] keep)
    {
        var sim = BotDuel.BuildArena(arena);
        var bot = new RampartBot(1, 0);
        if (!advance) { bot.AdvanceFrames = int.MaxValue / 2; bot.QuietFrames = int.MaxValue / 2; }
        foreach (var m in bot.Maneuvers) if (Array.IndexOf(keep, m.Name) < 0) bot.Disabled.Add(m.Name);
        int maxCrystals = 0;
        for (int f = 0; f < frames; f++)
        {
            var prev = bot.Active;
            sim.Step(default, bot.Poll(sim, f));
            if (bot.Active != null && bot.Active != prev)
                output.WriteLine($"f{f} start {bot.Active.Name} gap {(sim.SecondaryPlayers[0].Player.Body.Position.X - sim.Player.Body.Position.X) / Chunk.TileSize:F1}");
            maxCrystals = Math.Max(maxCrystals, sim.Chunks.Crystals.Count);
            if (Environment.GetEnvironmentVariable("RAMPART_CRYS") == "1" && f % 6 == 0 && sim.Chunks.Crystals.Count > 0)
            {
                int now = sim.Chunks.TerrainFrame;
                int x0 = (int)(sim.Player.Body.Position.X / Chunk.TileSize) - 2, x1 = (int)(sim.SecondaryPlayers[0].Player.Body.Position.X / Chunk.TileSize) + 2;
                int fy = (int)MathF.Floor((sim.Player.Body.Position.Y + 20f) / Chunk.TileSize);
                var balls = string.Join(",", sim.Entities.OfType<MassBall>().Select(b => $"{b.Body.Position.X / Chunk.TileSize:F1}/{b.Body.Position.Y / Chunk.TileSize:F1} v{b.Body.Velocity.X:F0},{b.Body.Velocity.Y:F0}"));
                output.WriteLine($"  f{f} balls {balls}");
                for (int y = fy - 3; y <= fy; y++)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int x = x0; x <= x1; x++)
                        sb.Append(sim.Chunks.GetCellState(x, y) != TileState.Empty ? 'X'
                            : sim.Chunks.Crystals.TryGet(x, y, out var cr) ? (cr.Pending ? ',' : cr.PhaseAt(now) == CrystalPhase.Gone ? '_' : '^') : '.');
                    output.WriteLine($"    {y}: {sb}");
                }
            }
        }
        output.WriteLine($"P1 lost {sim.Player.Combat.DamageTaken:F2}, P2 lost {sim.SecondaryPlayers[0].Player.Combat.DamageTaken:F2}, " +
                         $"max crystals {maxCrystals}, starts {string.Join(" ", bot.Starts.Select(k => $"{k.Key}:{k.Value}"))}");
        return (sim, bot);
    }

    [Fact]
    public void CrystalWave_LevelLaunch_HitsAfkTarget()
    {
        var (sim, bot) = Solo("plain_close", 420, nameof(RampartCrystalWave));
        Assert.True(bot.Starts.GetValueOrDefault(nameof(RampartCrystalWave)) >= 1);
        Assert.True(sim.Player.Combat.DamageTaken >= 1.0f, "a level wave should run the 24 tiles to the AFK target");
        Assert.Equal(0f, sim.SecondaryPlayers[0].Player.Combat.DamageTaken);
    }

    [Fact]
    public void SpikeCarpet_RingsTheFort()
    {
        var (sim, _) = Solo("plain_close", 240, advance: false, nameof(RampartSpikeMoat));
        var me = sim.SecondaryPlayers[0].Player;
        int mx = (int)MathF.Floor(me.Body.Position.X / Chunk.TileSize);
        int front = 0, back = 0;
        foreach (var cell in SpikeTiles.All(sim.Chunks))
        {
            if (sim.Chunks.GetCellTeam(cell.gtx, cell.gty) != me.Team) continue;
            if (cell.gtx < mx) front++; else back++;   // P1 is to the left
        }
        output.WriteLine($"spikes front {front}, back {back}");
        Assert.True(front >= 4, "the carpet should cover the approach side");
        Assert.True(back >= 1, "and some of the back");
        Assert.Equal(0f, me.Combat.DamageTaken);
    }

    [Fact]
    public void Duel_IsDeterministic()
    {
        var a = BotDuel.Run("plain_close", RampartBot.Create, BotDuel.Brawler, seed: 5, frames: 900);
        var b = BotDuel.Run("plain_close", RampartBot.Create, BotDuel.Brawler, seed: 5, frames: 900);
        output.WriteLine(a.ToString());
        Assert.Equal(a.ToString(), b.ToString());
    }

    // Two forts must not stare at each other: the anti-stall push brings them to blows.
    [Fact]
    public void Mirror_DoesNotStall()
    {
        var r = BotDuel.Run("hall_close", RampartBot.Create, RampartBot.Create, seed: 1, frames: 1800);
        output.WriteLine(r.ToString());
        Assert.True(r.DamageToP1 + r.DamageToP2 > 2f, "two Ramparts should still fight");
    }

    // The deliverable: beats the stock Brawler in the league format (all arenas, both
    // sides, 4 seeds).
    [Fact]
    public void RoundRobin_BeatsBrawler()
    {
        var table = BotDuel.RoundRobin(new (string, DuelBot)[] { ("Rampart", RampartBot.Create), ("Brawler", BotDuel.Brawler) },
                                       new[] { 1, 2, 3, 4 }, log: output.WriteLine);
        foreach (var s in table) output.WriteLine(s.ToString());
        var me = table.First(s => s.Name == "Rampart");
        var them = table.First(s => s.Name == "Brawler");
        Assert.True(me.Points > them.Points, "Rampart should out-point the Brawler");
        Assert.True(me.W >= 3 * me.L, $"expected a clear margin, got {me.W}-{me.D}-{me.L}");
        Assert.True(me.Kills > me.Deaths);
    }

    [Fact]
    public void Trace()
    {
        var env = Environment.GetEnvironmentVariable("RAMPART_TRACE");
        if (string.IsNullOrEmpty(env)) return;
        var parts = env.Split(';');   // arena;seed;frames;side(0=R is P1)
        string arena = parts[0]; int seed = int.Parse(parts[1]), frames = int.Parse(parts[2]), side = int.Parse(parts[3]);
        var sim = BotDuel.BuildArena(arena);
        var me = new RampartBot(side == 0 ? seed * 2 + 1 : seed * 2 + 2, side == 0 ? -1 : 0);
        var mirror = Environment.GetEnvironmentVariable("RAMPART_OPP") == "mirror";
        IRemoteInputSource br = mirror ? new RampartBot(side == 0 ? seed * 2 + 2 : seed * 2 + 1, side == 0 ? 0 : -1)
                                       : new BrawlerBotInputSource(side == 0 ? seed * 2 + 2 : seed * 2 + 1, side == 0 ? 0 : -1);
        string BA() => br is RampartBot rb ? rb.Active?.Name ?? "-" : ((BrawlerBotInputSource)br).Active?.Name ?? "-";
        var pm = side == 0 ? sim.Player : sim.SecondaryPlayers[0].Player;
        var pb = side == 0 ? sim.SecondaryPlayers[0].Player : sim.Player;
        float hm = pm.Health, hb = pb.Health;
        string lastAct = "";
        for (int f = 0; f < frames; f++)
        {
            var im = me.Poll(sim, f); var ib = br.Poll(sim, f);
            if (side == 0) sim.Step(im, ib); else sim.Step(ib, im);
            string act = me.Active?.Name ?? "-";
            float gap = (pb.Body.Position.X - pm.Body.Position.X) / Chunk.TileSize;
            float dy = (pb.Body.Position.Y - pm.Body.Position.Y) / Chunk.TileSize;
            string st = $"f{f} gap {gap:F1} dy {dy:F1} R[{act}|{pm.CurrentActionName}] hp {pm.Health:F2} B[{BA()}|{pb.CurrentActionName}] hp {pb.Health:F2} bm {pm.Abilities.Meters.Build:F0}";
            if (pm.Health < hm - 1e-4f) output.WriteLine("R HURT " + st);
            if (pb.Health < hb - 1e-4f) output.WriteLine("B HURT " + st);
            if (act != lastAct && act != "-") output.WriteLine("  start " + st);
            if (f % 60 == 0) output.WriteLine($"  t {st} myX {pm.Body.Position.X / Chunk.TileSize:F1} home {me.Sense.HomeX / Chunk.TileSize:F1}");
            lastAct = act;
            if (Environment.GetEnvironmentVariable("RAMPART_CRYS") == "1" && f % 5 == 0 && sim.Chunks.Crystals.Count > 0)
            {
                int now = sim.Chunks.TerrainFrame;
                var cs = sim.Chunks.Crystals.Entries.Where(e => !e.Value.Pending && e.Value.PhaseAt(now) != CrystalPhase.Gone).Select(e => e.Key.gtx).ToList();
                int fy = (int)MathF.Floor((pm.Body.Position.Y + 20f) / Chunk.TileSize);
                int cx = (int)(pm.Body.Position.X / Chunk.TileSize);
                for (int y = fy - 3; y <= fy; y++)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int x = cx - 14; x <= cx + 14; x++)
                        sb.Append(sim.Chunks.GetCellState(x, y) == TileState.Spike ? 'S' : sim.Chunks.GetCellState(x, y) != TileState.Empty ? 'X'
                            : sim.Chunks.Crystals.TryGet(x, y, out var cr) ? (cr.Pending ? ',' : cr.PhaseAt(now) == CrystalPhase.Gone ? '_' : '^') : '.');
                    output.WriteLine($"      row {y}: {sb} (x0 {cx - 14})");
                }
                output.WriteLine($"    crys f{f} live {cs.Count} x[{(cs.Count > 0 ? cs.Min() : 0)},{(cs.Count > 0 ? cs.Max() : 0)}] me {pm.Body.Position.X / Chunk.TileSize:F1} B {pb.Body.Position.X / Chunk.TileSize:F1},{pb.Body.Position.Y / Chunk.TileSize:F1} {pb.CurrentActionName}");
            }
            hm = pm.Health; hb = pb.Health;
        }
    }

    [Fact]
    public void Scratch()
    {
        var env = Environment.GetEnvironmentVariable("RAMPART_SCRATCH");
        if (string.IsNullOrEmpty(env)) return;
        // "arena,arena;seed,seed;frames"
        var parts = env.Split(';');
        var arenas = parts[0].Split(',');
        var seeds = parts[1].Split(',').Select(int.Parse).ToArray();
        int frames = int.Parse(parts[2]);
        var made = new List<RampartBot>();
        DuelBot mine = (self, seed) => { var b = new RampartBot(seed, self); foreach (var n in (Environment.GetEnvironmentVariable("RAMPART_OFF") ?? "").Split(",", StringSplitOptions.RemoveEmptyEntries)) b.Disabled.Add(n); made.Add(b); return b; };
        int W = 0, D = 0, L = 0, K = 0, De = 0; float Dd = 0, Dt = 0;
        foreach (var arena in arenas)
        foreach (int seed in seeds)
        for (int side = 0; side < 2; side++)
        {
            made.Clear();
            DuelBot opp = (Environment.GetEnvironmentVariable("RAMPART_OPP") ?? "brawler") switch
            {
                "zoner"  => (s2, sd) => new BrawlerBotInputSource(sd, s2) { EngageRange = 10f * Chunk.TileSize },
                "mirror" => (s2, sd) => new RampartBot(sd, s2),
                "riposte" => RiposteBot.Create,
                "rush"   => (s2, sd) => new BrawlerBotInputSource(sd, s2, BrawlerBotInputSource.DefaultManeuvers().Where(m => m is EscapeGrabManeuver or GuardManeuver or PulseManeuver or GrabManeuver or SlashManeuver or StabManeuver or AirHammerManeuver or AirWhipKickManeuver or BurstManeuver or CrouchSlashManeuver or DownAirManeuver or DashManeuver or WallClimbManeuver or BreachManeuver or BackoffManeuver or DropdownManeuver)),
                _        => BotDuel.Brawler,
            };
            var r = side == 0 ? BotDuel.Run(arena, mine, opp, seed, frames)
                              : BotDuel.Run(arena, opp, mine, seed, frames);
            int w = side == 0 ? r.Winner : -r.Winner;
            if (w > 0) W++; else if (w < 0) L++; else D++;
            K += side == 0 ? r.KillsByP1 : r.KillsByP2; De += side == 0 ? r.KillsByP2 : r.KillsByP1;
            Dd += side == 0 ? r.DamageToP2 : r.DamageToP1; Dt += side == 0 ? r.DamageToP1 : r.DamageToP2;
            output.WriteLine($"{(side == 0 ? "R=P1" : "R=P2")} {r}  => {(w > 0 ? "WIN" : w < 0 ? "LOSS" : "draw")}");
            string F(Dictionary<string, float> d) => string.Join(" ", d.OrderByDescending(k => k.Value).Select(k => $"{k.Key.Replace("Rampart", "R").Replace("Maneuver", "")}:{k.Value:F1}"));
            output.WriteLine("   dealt " + F(made[0].Dealt));
            output.WriteLine("   taken " + F(made[0].Taken));
            output.WriteLine("   sizes " + string.Join(" ", made[0].DealtBySize.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}")));
            output.WriteLine("   " + string.Join(" ", made[0].Starts.OrderByDescending(k => k.Value).Select(k => $"{k.Key.Replace("Rampart", "R").Replace("Maneuver", "")}:{k.Value}")));
        }
        output.WriteLine($"TOTAL {W}-{D}-{L}  K/D {K}/{De}  dmg {Dd:F1}/{Dt:F1}");
    }
}
