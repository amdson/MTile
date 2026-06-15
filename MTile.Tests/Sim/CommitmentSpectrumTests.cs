using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// COMBAT_FEEL_PLAN Phase 3 — the commitment spectrum (hit-confirm machinery).
//
// The hit-confirm channel (CombatSystem.PeekHits, latched into
// ActionVars.AttackConnected) tracks whether a slash connected. It is deliberately
// NOT gating combo chains right now — a slash opener chains into its follow-up
// whether or not it landed. These tests verify the machinery reports connection
// correctly AND that chaining is ungated (a whiffed opener still chains). To turn
// on whiff-punish, wrap the next-stage flag in `if (connected)` in the openers'
// OnExitSetFlags (see GroundSlash1).
public class CommitmentSpectrumTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    // Attacker throws S1 then, after recovery, a second click to chain S2. Mouse held
    // far right so both slashes orient +X. Two 1-frame clicks: frame 15 (S1), 24 (S2).
    private static InputScript ComboScript(Vector2 mouseAhead) => new InputScript()
        .For   (15, new PlayerInput { MouseWorldPosition = mouseAhead })
        .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAhead })
        .For   ( 8, new PlayerInput { MouseWorldPosition = mouseAhead })
        .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAhead })
        .Forever   (new PlayerInput { MouseWorldPosition = mouseAhead });

    private static SimConfigMulti BuildCombo(Vector2 victimStart)
    {
        var attackerStart = new Vector2(70f, 20f);
        var mouseAhead    = new Vector2(260f, 20f);
        return new SimConfigMulti
        {
            Terrain = FlatGround(),
            Frames  = 50,
            Dt      = Dt,
            Gravity = new Vector2(0f, Gravity),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = ComboScript(mouseAhead) },
                new SimPlayer { StartPosition = victimStart, Script = InputScript.Always(default),
                                Faction = Faction.Neutral },
            },
        };
    }

    // Runs the combo and reports: did the attacker's hit-confirm latch while slashing,
    // did GroundSlash2 fire, and what was the victim's final HP.
    private (bool sawConnected, bool sawS2, float victimPercent) RunCombo(Vector2 victimStart)
    {
        var cfg = BuildCombo(victimStart);
        var actions = new HashSet<string>();
        bool sawConnected = false;
        float victimPercent = 0f;
        SimRunner.RunMulti(cfg,
            onFrame: (f, ps) =>
            {
                actions.Add(ps[0].CurrentActionName);
                if (ps[0].CurrentActionName.Contains("Slash") && ps[0].CurrentActionVars.AttackConnected)
                    sawConnected = true;
            },
            // Direct hits no longer chip HP (Phase 5) — a landed slash shows up as the
            // victim's escalation percent rising.
            outPlayers: ps => victimPercent = ps[1].Combat.DamagePercent);
        output.WriteLine($"actions: {string.Join(",", actions)}; connected={sawConnected}; victim % {victimPercent}");
        return (sawConnected, actions.Contains("GroundSlash2"), victimPercent);
    }

    // S1 connects (victim in range): the hit-confirm latches AttackConnected, the
    // victim's percent rises, and the chain into S2 fires.
    [Fact]
    public void HitConnects_ConfirmLatches_AndChains()
    {
        var (sawConnected, sawS2, victimPercent) = RunCombo(victimStart: new Vector2(95f, 20f));
        Assert.True(victimPercent > 0f, "S1 should have connected (victim percent should rise).");
        Assert.True(sawConnected, "AttackConnected should latch when the slash lands.");
        Assert.True(sawS2, "S1 should chain into GroundSlash2.");
    }

    // S1 whiffs (victim out of range): the hit-confirm correctly reports NO connection
    // (machinery works), but the chain is NOT gated on it — S2 still fires.
    [Fact]
    public void Whiff_NoConfirm_ButStillChains()
    {
        var (sawConnected, sawS2, victimPercent) = RunCombo(victimStart: new Vector2(320f, 20f));
        Assert.Equal(0f, victimPercent);              // truly whiffed
        Assert.False(sawConnected, "AttackConnected must stay false on a whiff.");
        Assert.True(sawS2, "Chaining is ungated right now — a whiffed S1 still chains into S2.");
    }
}
