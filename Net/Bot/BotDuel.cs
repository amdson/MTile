using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Bot-vs-bot duels: two raw-input bots, one per player, in a real Versus Simulation
// (a dead player respawns at its own start). Used by the duel tests and by whoever
// arbitrates a bot league — everything here is deterministic given (arena, bots, seed).
//
// A duel bot is any IRemoteInputSource built for a side: selfIndex -1 drives the primary
// player (P1) against SecondaryPlayers[0]; selfIndex 0 drives P2 against the primary.
// Bots play by the same rules as BrawlerBotInputSource: raw PlayerInput only, perception
// through what a player could see (see Plans/BOT_AI_PLAN.md).
public delegate IRemoteInputSource DuelBot(int selfIndex, int seed);

public static class BotDuel
{
    public static PlayerCharacter Self(Simulation sim, int selfIndex)
        => selfIndex < 0 ? sim.Player : sim.SecondaryPlayers[selfIndex].Player;

    public static PlayerCharacter Opponent(Simulation sim, int selfIndex)
        => selfIndex < 0 ? sim.SecondaryPlayers[0].Player : sim.Player;

    public const int DefaultFrames = 3600;   // 60 s

    // Arenas, all built on the shipped duel stages (Hardened outer walls, so nobody
    // falls out of the world): "hall_close" (roofed hall, spawns 24 tiles apart),
    // "hall_far" (the stage's own spawns, ~72 tiles apart), "plain_close" (open sky).
    public static readonly string[] Arenas = { "hall_close", "hall_far", "plain_close" };

    public static Simulation BuildArena(string arena)
    {
        Stage baseStage = arena switch
        {
            "hall_close" or "hall_far" => Stages.Get("duel_hall"),
            "plain_close"              => Stages.Get("duel_plain"),
            _ => throw new ArgumentException($"unknown arena '{arena}'"),
        };
        var stage = baseStage;
        if (arena.EndsWith("_close"))
        {
            // Same terrain, spawns pulled in around the stage's midpoint.
            Vector2 mid = (baseStage.PlayerSpawn + baseStage.SecondSpawn.Value) * 0.5f;
            float half = 12 * Chunk.TileSize;
            stage = new Stage
            {
                Name        = baseStage.Name + "_close",
                Versus      = true,
                Terrain     = baseStage.Terrain,
                PlayerSpawn = new Vector2(mid.X - half, baseStage.PlayerSpawn.Y),
                SecondSpawn = new Vector2(mid.X + half, baseStage.PlayerSpawn.Y),
                Populate    = _ => { },
            };
        }
        return new Simulation(new GameConfig { SpawnSecondPlayer = true }, stage);
    }

    public struct Result
    {
        public string Arena;
        public int    Seed;
        public int    KillsByP1, KillsByP2;      // opponent deaths each side caused (or suffered)
        public float  DamageToP1, DamageToP2;    // HP lost, summed across lives
        // +1 P1 wins, -1 P2 wins, 0 draw: more kills wins; equal kills → more damage
        // dealt by a margin of at least 0.5 HP; otherwise a draw.
        public int Winner
        {
            get
            {
                if (KillsByP1 != KillsByP2) return KillsByP1 > KillsByP2 ? 1 : -1;
                float d = DamageToP2 - DamageToP1;
                return d >= 0.5f ? 1 : d <= -0.5f ? -1 : 0;
            }
        }
        public override string ToString() =>
            $"{Arena} s{Seed}: kills {KillsByP1}-{KillsByP2}, dmg dealt {DamageToP2:F1}-{DamageToP1:F1} → " +
            (Winner > 0 ? "P1" : Winner < 0 ? "P2" : "draw");
    }

    // One bout: `p1` drives the primary, `p2` the secondary.
    public static Result Run(string arena, DuelBot p1, DuelBot p2, int seed, int frames = DefaultFrames)
    {
        var sim = BuildArena(arena);
        var a = p1(-1, seed * 2 + 1);
        var b = p2(0, seed * 2 + 2);
        var r = new Result { Arena = arena, Seed = seed };
        var pa = sim.Player; var pb = sim.SecondaryPlayers[0].Player;
        float prevA = 0f, prevB = 0f;
        for (int f = 0; f < frames; f++)
        {
            var ia = a.Poll(sim, f);
            var ib = b.Poll(sim, f);
            sim.Step(ia, ib);
            Tally(pa, ref prevA, ref r.DamageToP1, ref r.KillsByP2);
            Tally(pb, ref prevB, ref r.DamageToP2, ref r.KillsByP1);
        }
        return r;
    }

    // DamageTaken restarts at 0 on respawn, so a drop is a death.
    private static void Tally(PlayerCharacter p, ref float prev, ref float total, ref int deaths)
    {
        float d = p.Combat.DamageTaken;
        if (d < prev) { deaths++; prev = 0f; }
        total += d - prev;
        prev = d;
    }

    public sealed class Standing
    {
        public string Name;
        public float  Points;       // win 1, draw 0.5
        public int    W, D, L, Kills, Deaths;
        public float  DamageDealt, DamageTaken;
        public override string ToString() =>
            $"{Name,-16} {Points,5:F1} pts  {W}-{D}-{L}  K/D {Kills}/{Deaths}  dmg {DamageDealt:F1}/{DamageTaken:F1}";
    }

    // Round robin: every pair, every arena, every seed, both sides (each bot plays P1 and
    // P2 once per arena/seed, so spawn-side asymmetries cancel).
    public static List<Standing> RoundRobin(IReadOnlyList<(string Name, DuelBot Bot)> bots,
                                            IReadOnlyList<int> seeds, IReadOnlyList<string> arenas = null,
                                            int frames = DefaultFrames, Action<string> log = null)
    {
        arenas ??= Arenas;
        var table = new List<Standing>();
        foreach (var b in bots) table.Add(new Standing { Name = b.Name });
        for (int i = 0; i < bots.Count; i++)
        for (int j = i + 1; j < bots.Count; j++)
        foreach (var arena in arenas)
        foreach (int seed in seeds)
        for (int side = 0; side < 2; side++)
        {
            int x = side == 0 ? i : j, y = side == 0 ? j : i;   // x is P1
            var r = Run(arena, bots[x].Bot, bots[y].Bot, seed, frames);
            log?.Invoke($"{bots[x].Name} (P1) vs {bots[y].Name} (P2): {r}");
            Score(table[x], r.Winner,  r.KillsByP1, r.KillsByP2, r.DamageToP2, r.DamageToP1);
            Score(table[y], -r.Winner, r.KillsByP2, r.KillsByP1, r.DamageToP1, r.DamageToP2);
        }
        table.Sort((p, q) => q.Points.CompareTo(p.Points));
        return table;
    }

    private static void Score(Standing s, int outcome, int kills, int deaths, float dealt, float taken)
    {
        if (outcome > 0) { s.W++; s.Points += 1f; }
        else if (outcome == 0) { s.D++; s.Points += 0.5f; }
        else s.L++;
        s.Kills += kills; s.Deaths += deaths; s.DamageDealt += dealt; s.DamageTaken += taken;
    }

    // The stock brawler as a duel bot — the shared baseline every new bot should beat.
    public static readonly DuelBot Brawler = (self, seed) => new BrawlerBotInputSource(seed, self);
}
