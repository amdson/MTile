using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The recovery-airlock transition graph (commit profiles + eviction lookahead +
// the Ready-merged RecoveryAction). Three scenarios pin the design:
//
//   1. Guard DURING a stab wind-up — the stab's CommitProfile quotes a cheap
//      evict, RecoveryAction preempts the stab, and guard comes out of the short
//      countdown within a few frames. (The old FSM either let guard cancel the
//      stab anywhere via raw priority, or nothing at all — this pins the middle.)
//   2. Guard request during the stab's STRIKE — user intent is always
//      expressible: the eviction (bidding guard's Passive 40 over the stab's
//      Active 30) cuts the strike short, but at the FULL tail price, so the
//      trade is real. A mashed slash (Passive 30) can NOT do the same — the
//      inherited bid loses the priority race (scenario 4).
//   3. LMB pressed during an attack's recovery and HELD — the countdown runs
//      out into the charge posture (index 0 = the merged Ready), and the release
//      gesture fires the buffered attack with no dead frame in between.
//   4. Click mashed during the stab's strike — no eviction (priority race lost);
//      the stab completes and the buffered click fires only out of index 0.
public class RecoveryTransitionTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly Vector2 Start        = new(70f, 20f);
    private static readonly Vector2 PressMouse   = new(120f, 28f);
    private static readonly Vector2 ReleaseMouse = new(180f, 28f);   // 60px swipe ⇒ Stab

    private static SimConfigMulti Build(InputScript script, int frames = 70) => new SimConfigMulti
    {
        Terrain = FlatGround(),
        Frames  = frames,
        Dt      = Dt,
        Gravity = new Vector2(0f, 600f),
        Players = new[] { new SimPlayer { StartPosition = Start, Script = script } },
    };

    // Per-frame action-name trace for a single-player run.
    private List<string> Trace(InputScript script, int frames = 70)
    {
        var names = new List<string>(frames);
        SimRunner.RunMulti(Build(script, frames),
            onFrame: (f, ps) => names.Add(ps[0].CurrentActionName));
        return names;
    }

    private static int First(List<string> names, string action, int from = 0)
    {
        for (int f = from; f < names.Count; f++) if (names[f] == action) return f;
        return -1;
    }

    private static int Count(List<string> names, string action)
    {
        int n = 0;
        foreach (var s in names) if (s == action) n++;
        return n;
    }

    // Stab gesture (as in AttackRecoilTests): press (1f) + swiped hold (8f ≈
    // 0.27s > the 0.2s click cap) + release ⇒ Stab intent at the release frame.

    // ── 1. Shift at stab START: cheap wind-up cancel through the airlock ─────
    [Fact]
    public void GuardDuringStabWindup_EvictsThroughRecovery_WithinAFewFrames()
    {
        // Shift goes down one frame AFTER the release (a same-frame Shift would
        // suppress the stab intent via its !Shift gate) — the stab enters and the
        // guard request lands during its wind-up (0.18s ≈ 5 frames at this dt).
        var script = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = PressMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = PressMouse })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = ReleaseMouse })
            .For   ( 1, new PlayerInput { MouseWorldPosition = ReleaseMouse })
            .Forever   (new PlayerInput { Shift = true, MouseWorldPosition = ReleaseMouse });
        var names = Trace(script);

        int stabFirst  = First(names, "StabAction");
        int guardFirst = First(names, "GuardAction");
        int stabFrames = Count(names, "StabAction");
        output.WriteLine($"stab@{stabFirst} for {stabFrames}f, guard@{guardFirst}");

        Assert.True(stabFirst >= 0, "Stab gesture should fire StabAction.");
        Assert.True(guardFirst > stabFirst, "Guard should follow the stab, not preempt its entry frame.");
        // Evicted inside the wind-up: the stab never survives to its strike
        // window (≈ frame 5 of the activation), let alone its 18-frame duration.
        Assert.True(stabFrames <= 6,
            $"Stab should be evicted during its wind-up, but stayed current {stabFrames} frames.");
        // The whole detour — evict, short countdown, guard entry — is a handful
        // of frames, not the stab's 0.3s recovery.
        Assert.True(guardFirst - stabFirst <= 7,
            $"Guard should arrive within ~7 frames of the stab start (got {guardFirst - stabFirst}).");
        // And it goes THROUGH the airlock, not around it.
        int recoveryFirst = First(names, "RecoveryAction", stabFirst);
        Assert.True(recoveryFirst >= 0 && recoveryFirst < guardFirst,
            "Eviction must pass through RecoveryAction before guard fires.");
    }

    // ── 2. Shift during the STRIKE: evicted at the full tail price ───────────
    [Fact]
    public void GuardDuringStabStrike_EvictsAtFullTailPrice()
    {
        // Same gesture, but Shift arrives mid-strike (8 frames after release, ≈
        // 0.23s into the 0.18–0.55s strike window). Guard's inherited bid (P40)
        // beats the stab's Active 30, so the strike IS cut short — but the quote
        // is the full 0.3s tail (9 frames), and guard only enters once the
        // countdown reaches its 0.2s (6-frame) entry window.
        var script = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = PressMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = PressMouse })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = ReleaseMouse })
            .For   ( 8, new PlayerInput { MouseWorldPosition = ReleaseMouse })
            .Forever   (new PlayerInput { Shift = true, MouseWorldPosition = ReleaseMouse });

        var names = Trace(script);
        int stabFirst  = First(names, "StabAction");
        int guardFirst = First(names, "GuardAction");
        int stabFrames = Count(names, "StabAction");
        output.WriteLine($"stab@{stabFirst} for {stabFrames}f, guard@{guardFirst}");

        Assert.True(stabFirst >= 0, "Stab gesture should fire StabAction.");
        Assert.True(guardFirst >= 0, "Guard should be reachable mid-strike (user intent).");
        // Evicted when the Shift lands (~8 frames in) — well short of the
        // 18-frame full activation.
        Assert.True(stabFrames >= 5 && stabFrames <= 12,
            $"Stab should be evicted mid-strike around the Shift frame (current {stabFrames} frames).");
        // The price: full 9-frame tail quoted, guard enters at index ≤ 6 — so the
        // detour costs ~3 frames of recovery on top of the evicted strike.
        Assert.True(guardFirst - stabFirst >= 8 && guardFirst - stabFirst <= 16,
            $"Guard should arrive through the full-tail countdown (Δ {guardFirst - stabFirst} frames).");
        int recoveryFirst = First(names, "RecoveryAction", stabFirst);
        Assert.True(recoveryFirst >= 0 && recoveryFirst < guardFirst,
            "Eviction must pass through RecoveryAction before guard fires.");
    }

    // ── 3. LMB pressed during recovery and held: countdown → charge → attack ─
    [Fact]
    public void PressHeldThroughRecovery_ChargesAtIndexZero_AndFiresOnRelease()
    {
        // Quick click ⇒ GroundSlash1 (0.14s + 0.1s recovery), with LMB re-pressed
        // one frame after the click and held with a swipe, releasing well after
        // the slash's recovery expired. The press must survive the countdown as
        // the charge posture and the release must fire the stab immediately.
        var script = new InputScript()
            .For   (10, new PlayerInput { MouseWorldPosition = PressMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = PressMouse })
            .For   ( 1, new PlayerInput { MouseWorldPosition = PressMouse })              // release ⇒ Click ⇒ Slash1
            .For   (14, new PlayerInput { LeftClick = true, MouseWorldPosition = PressMouse })   // re-press + hold through recovery
            .For   ( 4, new PlayerInput { LeftClick = true, MouseWorldPosition = ReleaseMouse }) // swipe
            .Forever   (new PlayerInput { MouseWorldPosition = ReleaseMouse });           // release ⇒ Stab

        var names = Trace(script);
        int slashFirst = First(names, "GroundSlash1");
        int stabFirst  = First(names, "StabAction");
        output.WriteLine($"slash@{slashFirst}, stab@{stabFirst}");
        Assert.True(slashFirst >= 0, "Click should fire GroundSlash1.");
        Assert.True(stabFirst  >= 0, "Held press through recovery should still deliver the stab on release.");

        // Between the slash and the stab the player sits in the airlock the whole
        // time — recovery counting down, then charging at index 0 — never dropping
        // to NullAction (which is what used to strand a buffered wind-up).
        for (int f = slashFirst; f < stabFirst; f++)
            Assert.True(names[f] is "GroundSlash1" or "RecoveryAction",
                $"frame {f}: expected the airlock to hold the buffered press, got {names[f]}.");

        // Release frame → stab with no dead-time: the release lands at script
        // frame 30 (10+1+1+14+4), and the intent fires the same or next frame.
        int releaseFrame = 30;
        Assert.True(stabFirst - releaseFrame <= 2,
            $"Stab should fire within ~2 frames of release (release≈f{releaseFrame}, stab@f{stabFirst}).");
    }

    // ── 4. Click mashed mid-strike: the priority race protects the stab ──────
    [Fact]
    public void MashedClickDuringStabStrike_DoesNotEvict()
    {
        // Stab gesture, then a quick click 5 frames into the stab (inside the
        // strike). GroundSlash1's Passive 30 does not beat the stab's Active 30,
        // so recovery's inherited bid loses: the stab must run its full 18-frame
        // activation, and the buffered click fires only once the countdown
        // reaches index 0 (9 frames of tail).
        var script = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = PressMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = PressMouse })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = ReleaseMouse })
            .For   ( 5, new PlayerInput { MouseWorldPosition = ReleaseMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = ReleaseMouse })  // mash press
            .Forever   (new PlayerInput { MouseWorldPosition = ReleaseMouse });                   // release ⇒ Click

        var names = Trace(script, frames: 80);
        int stabFirst  = First(names, "StabAction");
        int stabFrames = Count(names, "StabAction");
        // The stab's lunge can leave the player momentarily airborne, so the
        // buffered click may resolve as AirSlash1 rather than GroundSlash1.
        int slashFirst = First(names, "GroundSlash1");
        if (slashFirst < 0) slashFirst = First(names, "AirSlash1");
        output.WriteLine($"stab@{stabFirst} for {stabFrames}f, slash@{slashFirst}");

        Assert.True(stabFirst >= 0, "Stab gesture should fire StabAction.");
        Assert.True(stabFrames >= 16,
            $"A mashed click must NOT cut the stab short (current only {stabFrames} frames).");
        Assert.True(slashFirst > 0, "The buffered click should still fire after the tail.");
        // Full activation (18f) + full recovery to index 0 (9f) before the slash.
        Assert.True(slashFirst - stabFirst >= 24,
            $"Slash fired early (Δ {slashFirst - stabFirst} frames) — it must wait out the whole tail.");
    }

    // ── 5. The charge curve: ramp → sweet spot → settle ──────────────────────
    [Fact]
    public void ChargeFraction_RampsSpikesAndSettles()
    {
        float ramp  = RecoveryAction.ChargeRampSeconds;
        float sweet = RecoveryAction.SweetSpotSeconds;

        Assert.Equal(0f,   RecoveryAction.ChargeFraction(0f));
        Assert.Equal(0.5f, RecoveryAction.ChargeFraction(ramp * 0.5f), 3);
        // Sweet spot: full charge across the whole window.
        Assert.Equal(1f, RecoveryAction.ChargeFraction(ramp));
        Assert.Equal(1f, RecoveryAction.ChargeFraction(ramp + sweet * 0.5f));
        Assert.Equal(1f, RecoveryAction.ChargeFraction(ramp + sweet));
        // Overheld: settles flat, forever — no decay to zero.
        Assert.Equal(RecoveryAction.SettleFraction, RecoveryAction.ChargeFraction(ramp + sweet + 0.01f));
        Assert.Equal(RecoveryAction.SettleFraction, RecoveryAction.ChargeFraction(10f));
    }

    // ── 6. No more 1s cap: an overheld charge keeps the ready posture ────────
    // The old MaxChargeHold spent the press at 1.0s and dumped the player out of
    // the wind-up — under charge scaling that would read "held longer, got
    // nothing". Now the stance persists as long as LMB is down (past PressEdge's
    // IntentBuffer lifetime too, which the button-latch continuation exists for)
    // and the release still fires the stab.
    [Fact]
    public void OverheldCharge_KeepsReadyPosture_AndStillFiresStabOnRelease()
    {
        const int holdFrames = 75;   // 2.5s at 30fps — well past ramp + sweet spot
        var script = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = PressMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = PressMouse })
            .For   (holdFrames - 1, new PlayerInput { LeftClick = true, MouseWorldPosition = ReleaseMouse })
            .Forever   (new PlayerInput { MouseWorldPosition = ReleaseMouse });

        var names = Trace(script, frames: 15 + holdFrames + 30);
        int release = 15 + holdFrames;
        // The whole hold (a couple of frames of entry slack aside) sits in the
        // charge posture — never dropping to NullAction mid-hold.
        for (int f = 18; f < release; f++)
            Assert.True(names[f] == "RecoveryAction",
                $"frame {f}: overheld charge should hold the ready posture, got {names[f]}.");
        int stabFirst = First(names, "StabAction", release - 1);
        Assert.True(stabFirst >= 0 && stabFirst - release <= 2,
            $"Release after an overhold should still fire the stab promptly (release≈f{release}, stab@f{stabFirst}).");
    }

}
