using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The hit airlock (Plans/HIT_AIRLOCK_PLAN.md): getting hit is an INVOLUNTARY
// eviction through RecoveryAction, hitstun/stun fold into the recovery index,
// and ArmorProfile is the per-action exception. Three scenarios pin it:
//
//   1. Armor: a light slash landing inside a stab's armored window (windup +
//      strike, threshold 300 vs Slash1 strength ~200) does NOT interrupt —
//      percent still accrues, no hitstun registers, the stab completes.
//   2. Flinch: a heavy hit (enemy stab, strength ~650) breaks the armor,
//      registers, and evicts the victim's own stab mid-swing into Recovery.
//   3. Guard break + tail-window escape: an out-of-cone hit evicts a live
//      guard; with Shift still held, guard re-enters only once the hit
//      disadvantage countdown reaches its 0.2s entry window — the victim
//      visibly sits in the airlock for the bulk of the window first.
public class HitEvictionTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly Vector2 AttackerStart = new(70f, 20f);
    private static readonly Vector2 VictimStart   = new(95f, 20f);

    // Attacker stab gesture (as in CombatHitstunTests.Stun_StabHit…): press at
    // f15, 8-frame swipe, release ⇒ StabAction enters f24; hitbox opens at
    // 0.18s ≈ f29–30 and lands on the victim standing 25px to the right.
    private static InputScript AttackerStabScript()
    {
        var press   = new Vector2(120f, 28f);
        var release = new Vector2(180f, 28f);
        return new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = press })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = press })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = release })
            .Forever   (new PlayerInput { MouseWorldPosition = release });
    }

    // Victim stab gesture aimed RIGHT (away from the attacker, so the victim's
    // own hitbox never touches the attacker). `idleFrames` positions the entry.
    private static InputScript VictimStabScript(int idleFrames)
    {
        var press   = new Vector2(145f, 28f);
        var release = new Vector2(205f, 28f);
        return new InputScript()
            .For   (idleFrames, new PlayerInput { MouseWorldPosition = press })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = press })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = release })
            .Forever   (new PlayerInput { MouseWorldPosition = release });
    }

    private (List<string> actions, List<bool> hitstun, float percent)
        Run(InputScript attacker, InputScript victim, int frames)
    {
        var actions = new List<string>(frames);
        var hitstun = new List<bool>(frames);
        float percent = 0f;
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = FlatGround(),
            Frames  = frames,
            Dt      = Dt,
            Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = AttackerStart, Script = attacker },
                new SimPlayer { StartPosition = VictimStart,   Script = victim,
                                Faction = Faction.Neutral },
            },
        },
        onFrame: (f, ps) =>
        {
            actions.Add(ps[1].CurrentActionName);
            hitstun.Add(ps[1].Combat.HitstunActive);
        },
        outPlayers: ps => percent = ps[1].Combat.DamagePercent);
        return (actions, hitstun, percent);
    }

    private static int First(List<string> a, string name, int from = 0)
    {
        for (int f = from; f < a.Count; f++) if (a[f] == name) return f;
        return -1;
    }

    private static int Count(List<string> a, string name)
    {
        int n = 0;
        foreach (var s in a) if (s == name) n++;
        return n;
    }

    // ── 1. Armor: a light slash cannot stuff a committed stab ────────────────
    [Fact]
    public void ArmoredStab_TanksLightSlash_NoFlinch()
    {
        // Attacker: quick click ⇒ GroundSlash1 enters f16, hitbox live f16–17
        // (Impulse mode, strength = KnockbackMagnitude 200 < armor 300).
        var mouseAhead = new Vector2(200f, 28f);
        var attacker = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = mouseAhead })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAhead })
            .Forever   (new PlayerInput { MouseWorldPosition = mouseAhead });

        // Victim: stab enters f15 — the attacker's slash lands 1–2 frames into
        // the victim's armored wind-up.
        var (actions, hitstun, percent) = Run(attacker, VictimStabScript(6), frames: 60);

        int stabFirst  = First(actions, "StabAction");
        int stabFrames = Count(actions, "StabAction");
        output.WriteLine($"victim stab@{stabFirst} for {stabFrames}f, percent={percent}");

        Assert.True(stabFirst >= 0, "Victim's stab gesture should fire StabAction.");
        Assert.True(percent > 0f,
            "The attacker's slash should still CONNECT (armor takes the damage as percent).");
        Assert.DoesNotContain(true, hitstun);   // armored ⇒ never registers ⇒ no hitstun
        // Not interrupted: the stab runs its full 18-frame activation.
        Assert.True(stabFrames >= 16,
            $"Armored stab should complete, but only stayed current {stabFrames} frames.");
    }

    // ── 2. Flinch: a heavy hit breaks the armor and evicts mid-swing ─────────
    [Fact]
    public void HeavyHit_EvictsVictimsStab_MidSwing()
    {
        // Attacker stab hits ≈ f30 (strength ≈ StrikeSpeed 650 > armor 300).
        // Victim's own stab enters f26 — evicted ~4 frames in, well short of the
        // 18-frame activation, and lands in Recovery for the hitstun window.
        var (actions, hitstun, _) = Run(AttackerStabScript(), VictimStabScript(17), frames: 90);

        int stabFirst  = First(actions, "StabAction");
        int stabFrames = Count(actions, "StabAction");
        int hitFrame   = hitstun.IndexOf(true);
        output.WriteLine($"victim stab@{stabFirst} for {stabFrames}f, hitstun@{hitFrame}");

        Assert.True(stabFirst >= 0, "Victim's stab gesture should fire StabAction.");
        Assert.True(hitFrame > 0, "The attacker's stab should land and register hitstun.");
        Assert.True(stabFrames >= 2 && stabFrames <= 12,
            $"Victim's stab should be flinch-evicted mid-swing (current {stabFrames} frames).");
        int recoveryFirst = First(actions, "RecoveryAction", stabFirst);
        Assert.True(recoveryFirst >= 0 && recoveryFirst <= hitFrame + 2,
            $"Eviction must land the victim in RecoveryAction right after the hit (recovery@{recoveryFirst}, hit@{hitFrame}).");
    }

    // ── 3. Guard break + the 0.2s tail-window re-entry ───────────────────────
    [Fact]
    public void GuardBrokenByHit_ReentersInTailWindow()
    {
        // Victim holds Shift the whole run: guard is live well before the hit.
        // The attacker strikes from the LEFT while the victim faces RIGHT, so
        // the hit is out of the parry cone and registers fully — evicting guard.
        // With Shift still down, guard's precondition waits on EntryOk(0.2s):
        // re-entry only once ≤6 frames of the ~21-frame disadvantage remain.
        var victim = new InputScript()
            .For   (5, default(PlayerInput))
            .Forever  (new PlayerInput { Shift = true });
        var (actions, hitstun, _) = Run(AttackerStabScript(), victim, frames: 90);

        int guardFirst = First(actions, "GuardAction");
        int hitFrame   = hitstun.IndexOf(true);
        output.WriteLine($"guard@{guardFirst}, hit@{hitFrame}");

        Assert.True(guardFirst >= 0 && guardFirst < hitFrame,
            "Guard should be live before the hit lands.");
        Assert.True(hitFrame > 0, "The stab should land (out-of-cone, no parry).");

        // Broken: the frames right after the hit are the airlock, not guard.
        for (int f = hitFrame + 1; f <= hitFrame + 8 && f < actions.Count; f++)
            Assert.True(actions[f] != "GuardAction",
                $"frame {f}: guard must be evicted by the hit, got {actions[f]}.");
        int recoveryFirst = First(actions, "RecoveryAction", hitFrame);
        Assert.True(recoveryFirst >= 0 && recoveryFirst <= hitFrame + 2,
            $"Victim should sit in RecoveryAction after the guard break (recovery@{recoveryFirst}).");

        // …and re-enters through the tail window, not after the whole countdown
        // (hitstun caps at 0.7s = 21 frames; entry window 6 ⇒ ≈ 15 frames in).
        int guardBack = First(actions, "GuardAction", hitFrame + 1);
        output.WriteLine($"guard re-entry@{guardBack} (Δ {guardBack - hitFrame})");
        Assert.True(guardBack > 0, "Held Shift should recover guard after the disadvantage window.");
        Assert.True(guardBack - hitFrame >= 9 && guardBack - hitFrame <= 25,
            $"Guard re-entry should land in the countdown's tail window (Δ {guardBack - hitFrame} frames).");
    }
}
