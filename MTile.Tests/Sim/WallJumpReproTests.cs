using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Repro for: pressing Space while falling next to a wall fires a NORMAL jump (JumpingState).
// WallJumpingState only fires when a horizontal arrow is held; with NO arrow held it falls
// through, so if GroundChecker false-positives off the wall, JumpingState (which only needs
// ground) is the one that fires. Drives the real movement FSM via a hand-rolled sim loop so
// we can inspect the live body + ground probe each frame.
public class WallJumpReproTests
{
    private readonly ITestOutputHelper _out;
    public WallJumpReproTests(ITestOutputHelper output) => _out = output;

    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    // Tall wall on the right (column 10, rows 0..40); open air to its left, no floor in the
    // player's column for the duration of the test.
    private static ChunkMap RightWall()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 41; r++)
        {
            var line = new char[12];
            for (int i = 0; i < 12; i++) line[i] = 'O';
            line[10] = 'X';
            sb.Append(line).Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    // Spawn flush against the wall's left face and fall with NO arrow held; tap Space mid-fall.
    [Fact]
    public void FallingFlushAgainstWall_NoArrow_SpacePressed_DoesNotNormalJump()
    {
        var terrain = RightWall();
        const float wallLeft = 10 * 16;

        // Find the body half-width, then spawn so bounds.Right == wallLeft (flush).
        var probe = new PlayerCharacter(Vector2.Zero);
        float halfWidth = probe.Body.Bounds.Right - probe.Body.Position.X;

        var player = new PlayerCharacter(new Vector2(wallLeft - halfWidth, 60f));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hitboxes = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();

        const int SpaceFrame = 6;
        bool normalJumpFired = false;
        string stateAtSpace = "?";

        for (int f = 0; f < 16; f++)
        {
            var input = new PlayerInput { Space = f == SpaceFrame };   // NO arrow held
            ctrl.InjectInput(input);
            terrain.TickSprouts(Dt);
            player.Update(ctrl, terrain, hitboxes, hurtboxes, Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

            bool grounded = GroundChecker.TryFind(
                player.Body, terrain, PlayerCharacter.Radius, PlayerCharacter.Radius, out var g);
            string state = player.CurrentStateName;
            _out.WriteLine($"f={f} state={state} pos=({player.Body.Position.X:F2},{player.Body.Position.Y:F2}) " +
                           $"Right={player.Body.Bounds.Right:F2} vy={player.Body.Velocity.Y:F1} grounded={grounded}");

            if (f == SpaceFrame) stateAtSpace = state;
            if (f >= SpaceFrame && state == "JumpingState") normalJumpFired = true;
        }

        _out.WriteLine($"state at space press: {stateAtSpace}");
        Assert.False(normalJumpFired,
            $"a normal JumpingState fired off the wall while falling (no arrow held); state at space = {stateAtSpace}");
    }

    // Scan: how far must the body's right edge be from / into the wall before the ground
    // probe catches the wall column and a normal jump fires? Presses Space on frame 0 (no
    // arrow) so GroundChecker sees the exact spawn position (no StepSwept resolution yet).
    [Fact]
    public void Scan_GapToWall_VsNormalJump()
    {
        var probe = new PlayerCharacter(Vector2.Zero);
        float halfWidth = probe.Body.Bounds.Right - probe.Body.Position.X;
        const float wallLeft = 10 * 16;

        for (int tenths = -80; tenths <= 40; tenths += 10)
        {
            float gap = tenths / 10f;   // body Right = wallLeft - gap; negative = penetrating
            var terrain = RightWall();
            var player = new PlayerCharacter(new Vector2(wallLeft - gap - halfWidth, 60f));
            var bodies = new List<PhysicsBody> { player.Body };
            var ctrl = new Controller();

            ctrl.InjectInput(new PlayerInput { Space = true });   // no arrow
            terrain.TickSprouts(Dt);
            player.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);

            bool grounded = GroundChecker.TryFind(
                player.Body, terrain, PlayerCharacter.Radius, PlayerCharacter.Radius, out _);
            _out.WriteLine($"gap={gap,5:F1}  Right={player.Body.Bounds.Right,7:F2}  grounded={grounded,-5}  state={player.CurrentStateName}");

            // Physical range: a body flush with (gap 0) or clear of the wall (gap > 0) must
            // never read as grounded off the wall. (gap < 0 is the body penetrating the wall,
            // which collision resolves away in steady play, so it's out of scope here.)
            if (gap >= 0f)
                Assert.False(grounded, $"flush/clear body (gap={gap}) read as grounded off the wall");
        }
    }
}
