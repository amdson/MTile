using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Net;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Scratch diagnostic (Zzz*: excluded from the group runs). Frame-by-frame trace of the
// ground string S1 -> S2 -> S3 against a victim who mashes slash: when does the victim's
// hitstun end relative to the attacker's next hitbox?
public class ZzzComboTrace(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    [Theory]
    [InlineData(24)]
    [InlineData(18)]
    public void GroundString_VsMashingVictim(int startGap)
    {
        const int tapPeriod = 5;   // attacker taps every 5 frames (buffered, so the string fires as early as allowed)
        output.WriteLine($"Radius={PlayerCharacter.Radius} S1 reach={PlayerCharacter.Radius * 1.5f * 1.75f:F1} S2 reach={PlayerCharacter.Radius * 1.5f * 1.75f * 1.05f:F1} S3 reach={PlayerCharacter.Radius * 1.5f * 1.75f * 1.3f:F1}");
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        float floorTopY = 2 * Chunk.TileSize;
        var attackerStart = new Vector2(70f, floorTopY - PlayerCharacter.Radius);
        var victimStart   = new Vector2(70f + startGap, floorTopY - PlayerCharacter.Radius);
        var mouseAhead    = new Vector2(200f, 28f);
        var mouseBack     = new Vector2(-100f, 28f);

        var tapA  = new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAhead };
        var idleA = new PlayerInput { MouseWorldPosition = mouseAhead };
        var attacker = new InputScript().For(13, idleA);
        for (int i = 0; i < 6; i++) attacker = attacker.For(1, tapA).For(tapPeriod - 1, idleA);
        attacker = attacker.Forever(idleA);

        // Victim idles until the first hit has registered (lands f18, flinch f19), then mashes slash every 4 frames.
        var tapV  = new PlayerInput { LeftClick = true, MouseWorldPosition = mouseBack };
        var idleV = new PlayerInput { MouseWorldPosition = mouseBack };
        var victim = new InputScript().For(20, idleV);
        for (int i = 0; i < 40; i++) victim = victim.For(1, tapV).For(3, idleV);
        victim = victim.Forever(idleV);

        var cfg = new SimConfigMulti
        {
            Terrain = terrain, Frames = 120, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = attacker },
                new SimPlayer { StartPosition = victimStart,   Script = victim, Faction = Faction.Neutral },
            },
        };

        var rows = new List<string>();
        string lastRow = null;
        SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            var a = ps[0]; var v = ps[1];
            int vStun = v.Combat.HitstunActive ? v.Combat.HitstunExpireFrame - f : 0;
            int aStun = a.Combat.HitstunActive ? a.Combat.HitstunExpireFrame - f : 0;
            string row = $"A={a.CurrentActionName,-16} hpA={a.Health:F2} hsA={aStun,2} | V={v.CurrentActionName,-16} hpV={v.Health:F2} hsV={vStun,2} gap={v.Body.Position.X - a.Body.Position.X:F0}";
            if (row != lastRow) { rows.Add($"f{f,3}: {row}"); lastRow = row; }
        });
        foreach (var r in rows) output.WriteLine(r);
    }

    // AirWhipKick landing on a victim: how long is the victim held vs how long is the
    // attacker committed? Both airborne in a tall shaft (as AirWhipKickTests); the kick
    // fires toward -X, the victim hangs 1.8R to the left on the kick line (drift-compensated
    // the same way AirWhipKickTests does it). The victim mashes slash once the kick has hit.
    [Fact]
    public void WhipKick_VsMashingVictim()
    {
        const float R = PlayerCharacter.Radius;
        string open = new string('O', 28), solid = new string('X', 28);
        int rows = (int)System.MathF.Ceiling(600f / Chunk.TileSize);
        var terrain = SimTerrain.FromAscii(string.Join("\n", System.Linq.Enumerable.Repeat(open, rows).Append(solid)),
                                           originTileX: 0, originTileY: 0);
        var airStart = new Vector2(200f, 40f);
        var press    = airStart + new Vector2(-40f, 0f);
        var release  = airStart + new Vector2(-300f, 0f);
        InputScript Attacker() => new InputScript()
            .For(2,  new PlayerInput { MouseWorldPosition = press })
            .For(1,  new PlayerInput { LeftClick = true, MouseWorldPosition = press })
            .For(16, new PlayerInput { LeftClick = true, MouseWorldPosition = release })
            .Forever(new PlayerInput { MouseWorldPosition = release });

        // Pass 1: drift of a free-falling dummy relative to the gesture-holding attacker at strike.
        const int StrikeLag = 7;
        int entered = -1; Vector2 drift = Vector2.Zero;
        var dummyStart = airStart + new Vector2(300f, 0f);
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = terrain, Frames = 40, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = airStart,   Script = Attacker() },
                new SimPlayer { StartPosition = dummyStart, Script = InputScript.Always(default), Faction = Faction.Neutral },
            },
        }, onFrame: (f, ps) =>
        {
            if (entered < 0 && ps[0].CurrentActionName == nameof(AirWhipKick)) entered = f;
            if (entered >= 0 && f == entered + StrikeLag)
                drift = (ps[0].Body.Position - airStart) - (ps[1].Body.Position - dummyStart);
        });
        output.WriteLine($"kick enters at f{entered}, drift={drift}");

        // Pass 2: victim on the kick line, idle until the strike, then mashing slash every 4 frames.
        var mouseAtAttacker = new Vector2(400f, 40f);
        var tapV  = new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAtAttacker };
        var idleV = new PlayerInput { MouseWorldPosition = mouseAtAttacker };
        var victim = new InputScript().For(entered + StrikeLag + 2, idleV);
        for (int i = 0; i < 60; i++) victim = victim.For(1, tapV).For(3, idleV);
        victim = victim.Forever(idleV);
        var victimStart = airStart + new Vector2(-1.8f * R, 0.83f * R) + drift;

        var lines = new List<string>();
        string last = null;
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = terrain, Frames = 140, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = airStart,    Script = Attacker() },
                new SimPlayer { StartPosition = victimStart, Script = victim, Faction = Faction.Neutral },
            },
        }, onFrame: (f, ps) =>
        {
            var a = ps[0]; var v = ps[1];
            int vHs = v.Combat.HitstunActive ? v.Combat.HitstunExpireFrame - f : 0;
            int vSt = v.Combat.StunActive    ? v.Combat.StunExpireFrame    - f : 0;
            string aHold = a.Combat.HitstopActive ? "*" : " ";
            string vHold = v.Combat.HitstopActive ? "*" : " ";
            string row = $"A={a.CurrentActionName,-16}{aHold} mv={a.CurrentState.GetType().Name,-14} hpA={a.Health:F2} | V={v.CurrentActionName,-16}{vHold} mv={v.CurrentState.GetType().Name,-14} hpV={v.Health:F2} hs={vHs,2} st={vSt,2} dx={v.Body.Position.X - a.Body.Position.X:F0} dy={v.Body.Position.Y - a.Body.Position.Y:F0}";
            if (row != last) { lines.Add($"f{f,3}: {row}"); last = row; }
        });
        foreach (var l in lines) output.WriteLine(l);
    }

    // Bot guard timing vs a scripted slasher (BotManeuverPulseGuardTests' rig): per frame,
    // P1's action, the bot's Shift input / guard state / hitstun, and the parry stamps.
    [Fact]
    public void BotGuard_VsScriptedSlasher()
    {
        var sim = new Simulation(BotArena.Flat(), new Vector2(100f, 67f));
        var (p2, _) = sim.AddSecondaryPlayer(new Vector2(124f, 67f));
        var bot = BotArena.Only(new GuardManeuver());
        string last = null;
        for (int f = 0; f < 200; f++)
        {
            var p2In = bot.Poll(sim, f);
            var p1In = new PlayerInput { LeftClick = f % 40 == 0, MouseWorldPosition = p2.Body.Position };
            sim.Step(p1In, p2In);
            var c = p2.Combat;
            int hs = c.HitstunActive ? c.HitstunExpireFrame - p2.Frame : 0;
            string row = $"P1={sim.Player.CurrentActionName,-16} P2={p2.CurrentActionName,-20} shift={(p2In.Shift ? 1 : 0)} guard={(c.GuardActive ? 1 : 0)} age={(c.GuardActive ? p2.Frame - c.GuardStartFrame : -1),3} charged={(c.GuardCharged ? 1 : 0)} broken={(c.GuardBroken ? 1 : 0)} hs={hs,2} hp2={p2.Health:F2} hp1={sim.Player.Health:F2} dist={p2.Body.Position.X - sim.Player.Body.Position.X:F0}";
            if (row != last) { output.WriteLine($"f{f,3}: {row}"); last = row; }
        }
    }

    // Bot grab -> hold -> throw on an idle P1 (BotManeuverGrabTests rig): per frame, P1's
    // state, health, crush impulse, flags and velocity.
    [Fact]
    public void BotGrabThrow_Trace()
    {
        string last = null;
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(120f, 67f), new Vector2(150f, 67f), 120,
            BotArena.Only(new GrabManeuver()),
            p1: (f, sim) =>
            {
                var p = sim.Player; var c = p.Combat;
                string row = $"P1={p.CurrentStateName,-14} act={p.CurrentActionName,-14} hp={p.Health:F2} crush={p.Body.LastCrushImpulse,4:F0} grab={(c.GrabbedActive?1:0)} hs={(c.HitstunActive?1:0)} st={(c.StunActive?1:0)} L={(c.Launched?1:0)} v=({p.Body.Velocity.X,5:F0},{p.Body.Velocity.Y,5:F0}) pos=({p.Body.Position.X:F0},{p.Body.Position.Y:F0}) bot={sim.SecondaryPlayers[0].Player.CurrentActionName}";
                if (row != last) { output.WriteLine($"f{f,3}: {row}"); last = row; }
                return default;
            });
        output.WriteLine(r.Summary());
    }
}
