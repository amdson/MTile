using System;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// COMBAT_FEEL_PLAN Phase 6 — grab + struggle + throw (the RPS triangle's new corner).
//
// Grab is Shift+RMB: a strong short-range hold field that flags the victim
// GrabbedActive (gating their normal attacks/jump). It ignores guard (fields bypass
// the parry path). A grabbed victim's one option is the exempt struggle slash, which
// erodes the grabber's GrabStrength (without stunning them) until the grab drops
// (grab-break). Releasing RMB flings the victim.
public class GrabTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly Vector2 AttackerStart = new(70f, 20f);
    private static readonly Vector2 VictimStart   = new(95f, 20f);   // ~25px right, inside grab range
    private static readonly Vector2 MouseRight    = new(220f, 20f);

    // Attacker settles, then holds Shift+RMB (aim right) — press-edge at frame 10 fires
    // the grab, which then holds while RMB stays down.
    private static InputScript GrabHold() => new InputScript()
        .For(10, new PlayerInput { MouseWorldPosition = MouseRight })
        .Forever(new PlayerInput { Shift = true, RightClick = true, MouseWorldPosition = MouseRight });

    private static SimConfigMulti Build(InputScript attacker, InputScript victim) => new SimConfigMulti
    {
        Terrain = FlatGround(),
        Frames  = 70,
        Dt      = Dt,
        Gravity = new Vector2(0f, Gravity),
        Players = new[]
        {
            new SimPlayer { StartPosition = AttackerStart, Script = attacker },
            new SimPlayer { StartPosition = VictimStart,   Script = victim, Faction = Faction.Neutral },
        },
    };

    // A held grab flags the victim GrabbedActive.
    [Fact]
    public void Grab_HoldsVictim_SetsGrabbedActive()
    {
        bool sawGrabbed = false;
        SimRunner.RunMulti(Build(GrabHold(), InputScript.Always(default)),
            onFrame: (f, ps) => { if (ps[1].Combat.GrabbedActive) sawGrabbed = true; });
        Assert.True(sawGrabbed, "Victim should be flagged GrabbedActive while held.");
    }

    // Grab ignores guard: a guarding victim (Shift held) is still grabbed.
    [Fact]
    public void Grab_IgnoresGuard()
    {
        var guard = InputScript.Always(new PlayerInput { Shift = true });
        bool sawGrabbed = false;
        SimRunner.RunMulti(Build(GrabHold(), guard),
            onFrame: (f, ps) => { if (ps[1].Combat.GrabbedActive) sawGrabbed = true; });
        Assert.True(sawGrabbed, "Guard must not prevent a grab (fields bypass the parry path).");
    }

    // While grabbed, a normal slash is gated off (BlocksAttack), but the exempt struggle
    // slash fires and erodes the grabber's GrabStrength — WITHOUT stunning them — until
    // the hold drops and the victim is freed. (Victim holds Left to face the grabber so
    // the struggle arc reaches it.)
    [Fact]
    public void StruggleSlash_ErodesGrabStrength_AndBreaksGrab_WithoutStunningGrabber()
    {
        // Victim: idle until grabbed, then repeatedly tap LMB while facing left
        // (Left held + cursor to the left) so the struggle arc sweeps over the grabber.
        var mouseLeft = new Vector2(0f, 20f);
        var click   = new PlayerInput { Left = true, LeftClick = true, MouseWorldPosition = mouseLeft };
        var noClick = new PlayerInput { Left = true, MouseWorldPosition = mouseLeft };
        var victim = new InputScript()
            .For(14, default(PlayerInput))
            .For(1, click).For(3, noClick)
            .For(1, click).For(3, noClick)
            .For(1, click).For(3, noClick)
            .For(1, click).For(3, noClick)
            .For(1, click).For(3, noClick)
            .Forever(noClick);

        bool sawStruggle = false;
        bool grabberHitstun = false;
        float minGrabStrength = float.MaxValue;
        bool sawGrabbed = false;
        bool victimFreedAfterGrabbed = false;
        SimRunner.RunMulti(Build(GrabHold(), victim),
            onFrame: (f, ps) =>
            {
                if (ps[1].CurrentActionName == "GrabbedSlash") sawStruggle = true;
                if (ps[0].Combat.HitstunActive) grabberHitstun = true;
                if (ps[1].Combat.GrabbedActive) sawGrabbed = true;
                // Only track strength while the grab is live (it's reset on grab Enter).
                if (ps[0].CurrentActionName == "GrabAction")
                    minGrabStrength = MathF.Min(minGrabStrength, ps[0].Combat.GrabStrength);
                // Grab dropped after the victim had been grabbed → struggle freed them.
                if (sawGrabbed && !ps[1].Combat.GrabbedActive) victimFreedAfterGrabbed = true;
            });

        output.WriteLine($"struggle fired={sawStruggle}, grabber hitstun={grabberHitstun}, " +
                         $"min grab strength={minGrabStrength:F1}, freed={victimFreedAfterGrabbed}");
        Assert.True(sawStruggle, "Grabbed victim should be able to fire the exempt struggle slash.");
        Assert.False(grabberHitstun, "Struggle slash must NOT stun the grabber (it only erodes grab strength).");
        Assert.True(minGrabStrength <= 0f, "Repeated struggles should erode grab strength to 0.");
        Assert.True(victimFreedAfterGrabbed, "Grab should break (victim freed) once grab strength is depleted.");
    }

    // Releasing the grab flings the victim away (the throw). With the grabber facing/
    // aiming right, the victim is launched rightward.
    [Fact]
    public void Throw_FlingsVictim()
    {
        // Hold Shift+RMB through frame 24, then release (keep aiming right) → throw.
        var attacker = new InputScript()
            .For(10, new PlayerInput { MouseWorldPosition = MouseRight })
            .For(15, new PlayerInput { Shift = true, RightClick = true, MouseWorldPosition = MouseRight })
            .Forever(new PlayerInput { MouseWorldPosition = MouseRight });

        float maxVx = 0f;
        bool sawStun = false;
        SimRunner.RunMulti(Build(attacker, InputScript.Always(default)),
            onFrame: (f, ps) =>
            {
                if (f >= 25) maxVx = MathF.Max(maxVx, ps[1].Body.Velocity.X);
                if (f >= 25 && ps[1].Combat.StunActive) sawStun = true;
            });

        output.WriteLine($"victim max Vx after release = {maxVx:F1}, stunned={sawStun}");
        Assert.True(maxVx > 200f, $"Throw should fling the victim rightward (max Vx {maxVx:F1}).");
        Assert.True(sawStun, "Thrown victim should be stunned on exit (so they tumble/bounce, not act freely).");
    }
}
