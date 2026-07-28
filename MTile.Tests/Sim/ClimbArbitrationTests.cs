using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The climb family is an AUTOMATIC assist and must know its place:
//  - it never fires into geometry it can't stand up in (ceiling-capped hop +
//    standing-headroom refusal ⇒ tunnel bumps belong to the ambient fold);
//  - a player's own jump press wins the same-frame race at a lip
//    (climb Passive 29 < the whole launch family's bids).
// Both were live bugs: mantles fired in the bumpy corridor with open-sky-sized
// hops (head bonks, stalls, 51 px/s), and the old climb Passive 46 outbid
// Jump/RunningJump (30/35) despite a comment claiming jumps preempt.
public class ClimbArbitrationTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    private static ChunkMap CorridorTerrain()
    {
        const int W = 64;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new StringBuilder(W);
            for (int c = 0; c < W; c++)
            {
                bool tunnel = c >= 16;
                sb.Append(r switch
                {
                    6 => 'X',
                    2 when tunnel => 'X',
                    3 when tunnel && c % 4 == 3 => 'X',
                    5 when tunnel && c % 4 == 1 => 'X',
                    _ => 'O',
                });
            }
            rows[r] = sb.ToString();
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    // ── Full registry through the bumpy corridor: as fast as the restricted
    //    fold, and the climb family never fires under the tunnel ceiling ──
    [Fact]
    public void FullRegistry_BumpyCorridor_FoldOwnsTheTunnel()
    {
        var p = new PlayerCharacter(new Vector2(24f, 74f));   // FULL registry
        var bodies = new List<PhysicsBody> { p.Body };
        var ctrl = new Controller();
        var terrain = CorridorTerrain();

        var states = new List<string>();
        for (int f = 0; f < 800; f++)
        {
            ctrl.InjectInput(new PlayerInput { Right = true });
            p.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            states.Add(p.CurrentStateName);
        }

        float avg = (p.Body.Position.X - 24f) / (800 * Dt);
        output.WriteLine($"avg {avg:F1} px/s, final x={p.Body.Position.X:F1} of 1024");
        // Matches the fall+stand-restricted baseline (~76 px/s) — the broken
        // mantle-cycling behavior ran at ~51 with head bonks and trough stalls.
        Assert.True(avg > 70f, $"full-registry corridor too slow: {avg:F1} px/s");
        Assert.True(p.Body.Position.X > 1000f, $"stalled at x={p.Body.Position.X:F1}");
        Assert.DoesNotContain(states, s =>
            s.Contains("Parkour") || s.Contains("Mantle") || s.Contains("ArcJump"));
    }

    // ── Same-frame race at a lip: the player's jump beats the auto-climb ─────
    [Fact]
    public void JumpPress_InsideClimbTriggerWindow_JumpWins()
    {
        // Open-sky 1-block step (the climb WOULD fire here): floor row 3
        // (top 48), step cols 8.. (top 32, face at x=128).
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Start grounded INSIDE the trigger window (face 128 − trigger 32 →
        // body face past 96; center 102 → face 108), running at speed, jump
        // held from frame 0: both the climb and the jump bid on frame 0.
        var frames = SimRunner.Run(new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(102f, 27f),
            StartVelocity = new Vector2(120f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true, Space = true }),
            Frames        = 60,
            Dt            = Dt,
            Gravity       = Gravity,
        });

        // The RACE is the pin: the first maneuver-class state entered must be a
        // jump, not the auto-climb. (A climb firing LATER — e.g. after the jump
        // lands short and the body keeps running at the step — is legitimate
        // assist behavior, not a stolen frame.)
        var firstManeuver = frames.FirstOrDefault(f =>
            f.State.Contains("Jump") || f.State.Contains("Parkour")
            || f.State.Contains("Mantle") || f.State.Contains("ArcJump"));
        output.WriteLine($"first maneuver state: {firstManeuver?.State ?? "none"} " +
                         $"(frame {firstManeuver?.Frame ?? -1})");
        Assert.NotNull(firstManeuver);
        Assert.True(firstManeuver.State.Contains("Jump"),
            $"the automatic climb stole the frame from the player's jump: {firstManeuver.State}");
    }
}
