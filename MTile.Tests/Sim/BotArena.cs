using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;

namespace MTile.Tests.Sim;

// Shared harness for bot tests (BrawlerBotInputSource + BotManeuvers): a real two-player
// Simulation, P1 scripted (default: AFK), P2 driven by a bot. Records what P2 did so a
// test can assert "the stab maneuver actually produced a StabAction".
public static class BotArena
{
    // Walled flat arena: 40 tiles wide, floor top at tile y = 8 (world y = 88), four
    // floor rows (slashes carve terrain), walls at tile x = 0 and 39 (origin −4 ⇒
    // playable x ≈ [−33, 385]). A standing body rests at y ≈ 67.
    // `step` raises the right-hand ten columns by two tiles (top at world y = 66).
    public static ChunkMap Flat(bool step = false)
    {
        var rows = new List<string>();
        for (int y = 0; y < 12; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 40; x++)
                sb.Append(y >= 8 || x == 0 || x == 39 || (step && x >= 30 && y >= 6) ? 'X' : 'O');
            rows.Add(sb.ToString());
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: -4, originTileY: 0);
    }

    // Custom arena from ascii ('X' solid), same origin convention as Flat.
    public static ChunkMap FromAscii(string ascii, int originTileX = -4, int originTileY = 0)
        => SimTerrain.FromAscii(ascii, originTileX, originTileY);

    public const float FloorBodyY = 67f;   // body centre of a player standing on Flat()'s floor

    public sealed class Run
    {
        public Simulation      Sim;
        public PlayerCharacter P1, P2;
        public BrawlerBotInputSource Bot;
        public int Frames;
        // Every distinct P2 action / movement state name seen, in first-seen order.
        public readonly List<string> P2Actions = new();
        public readonly List<string> P2States  = new();
        // Frame P1 first lost HP (−1 if never) and total HP P1 lost.
        public int   FirstHitOnP1 = -1;
        public float DamageToP1;
        public float DamageToP2;
        // Per-frame P2 input, for gesture debugging.
        public readonly List<PlayerInput> P2Inputs = new();

        public bool Saw(string actionName) => P2Actions.Contains(actionName);
        public string Summary() =>
            $"frames {Frames}; P1 lost {DamageToP1} (first hit {FirstHitOnP1}); P2 lost {DamageToP2}; " +
            $"P1 {P1.Body.Position} P2 {P2.Body.Position}; actions [{string.Join(",", P2Actions)}]; " +
            $"states [{string.Join(",", P2States)}]";
    }

    // Build a sim and step it `frames` times with P2 under `bot` (default: a brawler
    // with the full DefaultManeuvers set). `p1` scripts the primary (null ⇒ AFK).
    // `setup` runs once after both players exist (spawn entities, set block type, …).
    public static Run Fight(ChunkMap terrain, Vector2 p1Spawn, Vector2 p2Spawn, int frames,
                            BrawlerBotInputSource bot = null,
                            Func<int, Simulation, PlayerInput> p1 = null,
                            Action<Simulation> setup = null)
    {
        var sim = new Simulation(terrain, p1Spawn);
        var (p2, _) = sim.AddSecondaryPlayer(p2Spawn);
        setup?.Invoke(sim);
        bot ??= new BrawlerBotInputSource(seed: 3);
        var r = new Run { Sim = sim, P1 = sim.Player, P2 = p2, Bot = bot };
        for (int f = 0; f < frames; f++)
        {
            var p2In = bot.Poll(sim, f);
            r.P2Inputs.Add(p2In);
            sim.Step(p1?.Invoke(f, sim) ?? default, p2In);
            if (!r.P2Actions.Contains(p2.CurrentActionName)) r.P2Actions.Add(p2.CurrentActionName);
            if (!r.P2States.Contains(p2.CurrentStateName))   r.P2States.Add(p2.CurrentStateName);
            if (r.FirstHitOnP1 < 0 && sim.Player.Combat.DamageTaken > 0f) r.FirstHitOnP1 = f;
            r.Frames = f + 1;
        }
        r.DamageToP1 = sim.Player.Combat.DamageTaken;
        r.DamageToP2 = p2.Combat.DamageTaken;
        return r;
    }

    // A bot that runs only the given maneuvers (plus default locomotion).
    public static BrawlerBotInputSource Only(params BotManeuver[] maneuvers)
        => new(seed: 3, maneuvers: maneuvers);
}
