using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Repro for: pressing Space while falling next to a wall fires a NORMAL jump.
// JumpingState only needs JumpJustPressed + TryGetGround, and the ground probe is a
// strip the full width of the body extending straight down. A vertical wall the body
// is flush against intersects that strip at its boundary, and the wall column always
// has a tile-top sitting just below the body — so GroundChecker reports "ground" when
// there's only a wall to the side and open air beneath.
public class GroundCheckerWallTests
{
    private readonly ITestOutputHelper _out;
    public GroundCheckerWallTests(ITestOutputHelper output) => _out = output;

    // Tall wall at column 10; column 9 (where the body sits) is open all the way down,
    // so there is NO real floor under the body — only the wall to its right.
    private static ChunkMap WallColumn()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 30; r++)
        {
            var line = new char[12];
            for (int i = 0; i < 12; i++) line[i] = 'O';
            line[10] = 'X';
            sb.Append(line).Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    // Place the body so its bounding-box right edge sits `gap` px from the wall face
    // (negative gap = penetrating the wall). Reports whether GroundChecker sees ground.
    [Theory]
    [InlineData(2.0f)]    // clear gap — should never be grounded
    [InlineData(0.5f)]
    [InlineData(0.0f)]    // flush
    [InlineData(-0.5f)]   // slight penetration (what wall-slide can leave)
    public void FallingBesideWall_GroundProbe(float gap)
    {
        var chunks = WallColumn();
        const float wallLeft = 10 * 16;

        var player = new PlayerCharacter(new Vector2(0f, 80f));
        float halfWidth = player.Body.Bounds.Right - player.Body.Position.X;
        // Right edge = wallLeft - gap  ⇒  center.X = wallLeft - gap - halfWidth
        player.Body.Position = new Vector2(wallLeft - gap - halfWidth, 80f);

        bool grounded = GroundChecker.TryFind(
            player.Body, chunks, PlayerCharacter.Radius, PlayerCharacter.Radius, out var contact);

        _out.WriteLine($"gap={gap}: bounds.Right={player.Body.Bounds.Right:F3}, grounded={grounded}, " +
                       $"surfaceY={(contact?.Position.Y.ToString("F1") ?? "—")}");

        // There is no floor under the body — it must never read as grounded.
        Assert.False(grounded, $"falling beside a wall (gap={gap}) was reported grounded");
    }
}
