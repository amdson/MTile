using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// TEMP DIAGNOSTIC (part 2): per-channel attribution + post-hit movement. Delete me.
public class ZzzMovementResponse2
{
    private static readonly string Out =
        Environment.GetEnvironmentVariable("MTILE_RESP_OUT2") ?? Path.Combine(Path.GetTempPath(), "mtile_resp2.txt");

    private const float T = Chunk.TileSize;
    private static readonly PlayerInput None = default;
    private static readonly PlayerInput R = new() { Right = true };
    private static readonly PlayerInput RJ = new() { Right = true, Space = true };

    private static void LoadLiveConfig()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MTile.Core.csproj"))) d = d.Parent;
        MovementConfig.Load(Path.Combine(d!.FullName, "configs", "movement_config.json"));
    }

    private static string Channels(PlayerCharacter p)
    {
        var l = p.CorrectorDebug.Ledger;
        var sb = new StringBuilder();
        for (int c = 0; c < l.ChannelCount; c++)
        {
            var f = l.Channels[c].Force;
            if (f.LengthSquared() < 25f) continue;
            sb.Append($" {l.Channels[c].Channel}({f.X:F0},{f.Y:F0})");
        }
        return sb.ToString();
    }

    // Solo loop (SimRunner.Run's order) with ledger access.
    private static void Solo(StringBuilder sb, string title, ChunkMap terrain, Vector2 start, InputScript script,
                             int frames, int from, int to)
    {
        var player = new PlayerCharacter(start);
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hit = new HitboxWorld(); var hurt = new HurtboxWorld();
        sb.AppendLine($"## {title}");
        SimFrame? prev = null;
        for (int f = 0; f < frames; f++)
        {
            ctrl.InjectInput(script.Get(f, prev));
            terrain.TickSprouts(Simulation.FixedDt);
            player.Update(ctrl, terrain, hit, hurt, Simulation.FixedDt);
            var af = player.Body.AppliedForce;
            string ch = Channels(player);
            var outcome = player.CorrectorDebug.LatticeOutcome;
            LaunchSystem.StepSwept(bodies, terrain, Simulation.FixedDt, new Vector2(0f, 600f));
            var b = player.Body;
            prev = new SimFrame(f, 0, b.Position.X, b.Position.Y, b.Velocity.X, b.Velocity.Y, af.X, af.Y, player.CurrentStateName, false);
            if (f >= from && f < to)
                sb.AppendLine($"  f{f,3} x={b.Position.X,7:F2} y={b.Position.Y,6:F2} vx={b.Velocity.X,6:F1} vy={b.Velocity.Y,7:F1} F=({af.X,6:F0},{af.Y,6:F0}) {player.CurrentStateName.Replace("State", ""),-12} {outcome,-7}{ch}");
        }
    }

    private static ChunkMap Rows(params string[] rows) => SimTerrain.FromAscii(string.Join("\n", rows), 0, 0);
    private static string Line(int solidFrom, int solidTo, int w = 70)
    {
        var c = new char[w];
        for (int i = 0; i < w; i++) c[i] = i >= solidFrom && i < solidTo ? 'X' : '.';
        return new string(c);
    }

    [Fact]
    public void Trace()
    {
        LoadLiveConfig();
        var sb = new StringBuilder();
        sb.AppendLine($"config: ParkourEnabled={MovementConfig.Current.ParkourEnabled} TileSize={T}");
        string E = Line(0, 0), F = Line(0, 70);
        float floorTop = 6 * T, restY = floorTop - 20.5f;

        // 1. Running jump: who brakes x?
        Solo(sb, "1 running jump (R held throughout; Space at f70)", Rows(E, E, E, E, E, E, F), new Vector2(150, restY),
            new InputScript().For(40, None).For(30, R).For(30, RJ).Forever(R), 130, 68, 82);

        // 2. Walk off a 1-tile ledge: who boosts x?
        Solo(sb, "2 walk off 1-tile ledge at x=220 (R held)", Rows(E, E, E, E, E, Line(0, 20), F), new Vector2(150, 5 * T - 20.5f),
            new InputScript().For(40, None).Forever(R), 140, 104, 132);

        // 3. One-high step up with the live config (parkour off).
        Solo(sb, "3 one-high step up at x=220 (R held)", Rows(E, E, E, E, E, Line(20, 70), F), new Vector2(150, restY),
            new InputScript().For(40, None).Forever(R), 150, 92, 128);

        // 3b. One-high step up, release R 6 frames before the face: does it stop where a flat stop would?
        foreach (int rel in new[] { 96, 100, 104, 108, 112 })
        {
            var s = new StringBuilder();
            Solo(s, "", Rows(E, E, E, E, E, Line(20, 70), F), new Vector2(150, restY),
                new InputScript().For(40, None).For(rel - 40, R).Forever(None), 170, 169, 170);
            var s2 = new StringBuilder();
            Solo(s2, "", Rows(E, E, E, E, E, E, F), new Vector2(150, restY),
                new InputScript().For(40, None).For(rel - 40, R).Forever(None), 170, 169, 170);
            sb.AppendLine($"## 3b release at f{rel}: step:{s.ToString().Split('\n')[1].Substring(0, 44)}   | flat:{s2.ToString().Split('\n')[1].Substring(0, 44)}");
        }

        // 4. Landing from a neutral jump while holding R the whole time: x speed through the catch.
        Solo(sb, "4 jump (4f) with R held: landing", Rows(E, E, E, E, E, E, F), new Vector2(150, restY),
            new InputScript().For(40, None).For(30, R).For(4, RJ).Forever(R), 140, 108, 132);

        // ── Post-hit ─────────────────────────────────────────────────────────
        // Attacker at x=200 facing +x runs the ground string (tap every 5f from f13); victim 24px ahead.
        var flat = new[] { E, E, E, E, E, E, F };
        var stepBehind = new[] { E, E, E, E, E, Line(25, 70), F };       // 1-high step ~4 tiles behind the victim
        var ledgeBehind = new[] { E, E, E, E, E, E, Line(0, 25), Line(0, 70) }; // floor ends ~4 tiles behind; drop 1 tile... (row 6 is the floor here)
        (string name, string[] rows, float floorY)[] arenas =
        {
            ("flat", flat, 6 * T), ("step-behind", stepBehind, 6 * T), ("ledge-behind", ledgeBehind, 6 * T),
        };
        var victims = new (string name, PlayerInput input)[]
        {
            ("idle", new PlayerInput()), ("hold-away", new PlayerInput { Right = true }),
            ("hold-toward", new PlayerInput { Left = true }), ("jump-held", new PlayerInput { Space = true }),
        };
        foreach (int taps in new[] { 1, 6 })
        foreach (var (aname, rows, floorY) in arenas)
        {
            sb.AppendLine($"## HIT {(taps == 1 ? "single S1" : "S1-S2-S3 string")} on {aname}: victim x relative to its start, by held input");
            var traces = new Dictionary<string, List<string>>();
            var summary = new List<string>();
            foreach (var (vname, vin) in victims)
            {
                var mouse = new Vector2(400f, floorY - 12f);
                var tapA = new PlayerInput { LeftClick = true, MouseWorldPosition = mouse };
                var idleA = new PlayerInput { MouseWorldPosition = mouse };
                var att = new InputScript().For(13, idleA);
                for (int i = 0; i < taps; i++) att = att.For(1, tapA).For(4, idleA);
                att = att.Forever(idleA);
                // Victim idles until first hit, then applies its input forever.
                bool hitYet = false;
                var vscript = new InputScript().Until(new PlayerInput(), _ => hitYet).Forever(vin);
                var cfg = new SimConfigMulti
                {
                    Terrain = Rows(rows), Frames = 150,
                    Players = new[]
                    {
                        new SimPlayer { StartPosition = new Vector2(200f, floorY - 20.5f), Script = att },
                        new SimPlayer { StartPosition = new Vector2(224f, floorY - 20.5f), Script = vscript, Faction = Faction.Neutral },
                    },
                };
                var lines = new List<string>();
                int firstHit = -1, lastStun = -1, hits = 0; float peak = 0; bool wasStun = false; float x0 = 224f;
                float xAtStunEnd = 0;
                SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
                {
                    var v = ps[1]; var c = v.Combat;
                    bool hs = c.HitstunActive;
                    if (hs && !wasStun) { hits++; if (firstHit < 0) { firstHit = f; hitYet = true; } }
                    if (!hs && wasStun) { lastStun = f; xAtStunEnd = v.Body.Position.X - x0; }
                    wasStun = hs;
                    peak = MathF.Max(peak, MathF.Abs(v.Body.Velocity.X));
                    if (firstHit >= 0 && f < firstHit + 70)
                        lines.Add($"{v.Body.Position.X - x0,6:F1}/{v.Body.Velocity.X,5:F0}/{v.Body.Velocity.Y,5:F0} {(hs ? "H" : ".")}{(c.Launched ? "L" : ".")}{(c.StunActive ? "S" : ".")} {v.CurrentStateName.Replace("State", ""),-9}");
                }, outPlayers: ps =>
                {
                    summary.Add($"  {vname,-12} first hit f{firstHit}, hitstun windows {hits}, last hitstun ends f{lastStun} (+{lastStun - firstHit}f) at dx={xAtStunEnd:F1}; peak |vx| {peak:F0}; final dx={ps[1].Body.Position.X - x0:F1} y={ps[1].Body.Position.Y:F1} hp={ps[1].Health:F2}");
                });
                traces[vname] = lines;
            }
            foreach (var s in summary) sb.AppendLine(s);
            sb.AppendLine("  per frame after first hit: dx/vx/vy flags(H=hitstun L=launched S=stun) state   [idle | hold-away | hold-toward | jump-held]");
            int n = traces.Values.Min(l => l.Count);
            for (int i = 0; i < n; i += (i < 30 ? 1 : 3))
                sb.AppendLine($"  +{i,2}: " + string.Join(" | ", victims.Select(v => traces[v.name][i])));
        }

        File.WriteAllText(Out, sb.ToString());
    }
}
