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
    // `floorRows` deepens the floor (and walls) for long full-kit fights, which carve.
    // `wallCols` thickens both side walls inward (playable span shrinks accordingly).
    public static ChunkMap Flat(bool step = false, int floorRows = 4, int wallCols = 1)
    {
        var rows = new List<string>();
        for (int y = 0; y < 8 + floorRows; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 40; x++)
                sb.Append(y >= 8 || x < wallCols || x >= 40 - wallCols || (step && x >= 30 && y >= 6) ? 'X' : 'O');
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
        // Frame P1 first lost HP (−1 if never) and total HP each side lost, summed across
        // respawns (a respawn resets CombatState.DamageTaken). Kills = respawns seen.
        public int   FirstHitOnP1 = -1;
        public float DamageToP1;
        public float DamageToP2;
        public int   P1Deaths, P2Deaths;
        // Per-frame P2 input, for gesture debugging.
        public readonly List<PlayerInput> P2Inputs = new();

        public bool Saw(string actionName) => P2Actions.Contains(actionName);
        public string Summary() =>
            $"frames {Frames}; P1 lost {DamageToP1} (first hit {FirstHitOnP1}, deaths {P1Deaths}); " +
            $"P2 lost {DamageToP2} (deaths {P2Deaths}); " +
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
        float p1Prev = 0f, p2Prev = 0f;
        void Tally(PlayerCharacter p, ref float prev, ref float total, ref int deaths)
        {
            float d = p.Combat.DamageTaken;
            if (d < prev) { deaths++; prev = 0f; }   // respawned: the counter restarted
            total += d - prev;
            prev = d;
        }
        for (int f = 0; f < frames; f++)
        {
            var p2In = bot.Poll(sim, f);
            r.P2Inputs.Add(p2In);
            sim.Step(p1?.Invoke(f, sim) ?? default, p2In);
            if (!r.P2Actions.Contains(p2.CurrentActionName)) r.P2Actions.Add(p2.CurrentActionName);
            if (!r.P2States.Contains(p2.CurrentStateName))   r.P2States.Add(p2.CurrentStateName);
            if (r.FirstHitOnP1 < 0 && sim.Player.Combat.DamageTaken > 0f) r.FirstHitOnP1 = f;
            Tally(sim.Player, ref p1Prev, ref r.DamageToP1, ref r.P1Deaths);
            Tally(p2,         ref p2Prev, ref r.DamageToP2, ref r.P2Deaths);
            r.Frames = f + 1;
        }
        return r;
    }

    // A bot that runs only the given maneuvers (plus default locomotion).
    public static BrawlerBotInputSource Only(params BotManeuver[] maneuvers)
        => new(seed: 3, maneuvers: maneuvers);
}
