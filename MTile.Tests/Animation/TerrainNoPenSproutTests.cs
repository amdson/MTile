using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Growing sprouts in the animation no-penetration surface set. A sprout is
// collision-solid while it grows, but its cell is TileState.Sprouting — never Solid —
// so TerrainSurfaces' cell scan used to be blind to it and limbs clipped straight
// through a block growing into them. Covers: the moving leading face emitted as a
// half-plane at the volume's CURRENT position, the trailing face (backed by the solid
// parent) staying silent, and a tip buried inside a volume getting an exit plane.
public class TerrainNoPenSproutTests
{
    private readonly ITestOutputHelper _o;
    public TerrainNoPenSproutTests(ITestOutputHelper o) => _o = o;

    private const float Dt = Simulation.FixedDt;
    private const float Scale = 0.6f;
    private const float TS = Chunk.TileSize;

    // Solid rows at gty 10..12 → ground line y = FloorOriginTileY * TS.
    private const int FloorOriginTileY = 10;
    private static readonly float FloorTopY = FloorOriginTileY * TS;

    // Sprouts live at gty = FloorOriginTileY - 1, growing UP out of the floor: the
    // exposed top face starts at the floor line (progress 0) and moves up by one full
    // tile height as progress goes to 1.
    private static float SproutTopY(float progress) => FloorTopY - TS * progress;

    // Animator root position for the standing-pose tests below. X centered over the
    // sprouted columns (gx 4..8); Y keeps the same fixed 20px offset above the floor
    // line the original 16px-grid authoring used (feet land near the ground regardless
    // of Chunk.TileSize — the skeleton's own dimensions don't scale with the tile grid).
    private static readonly float PosX = 6.5f * TS;
    private static readonly float PosY = FloorTopY - 20f;

    // Solid rows at gty 10..12 → ground line y = 160.
    private static ChunkMap Floor(int widthTiles = 60, int originTileY = 10)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 3; r++) sb.AppendLine(new string('X', widthTiles));
        return SimTerrain.FromAscii(sb.ToString(), originTileY: originTileY);
    }

    private static CharacterAnimator NewAnimator()
        => new(SkeletonExamples.Biped(), Scale, AnimationStore.LoadAll(StatesDir()));

    private static string StatesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "SkeletonStates", "biped");
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        return "SkeletonStates/biped";
    }

    private static void WarmIdle(CharacterAnimator anim, Vector2 pos)
    {
        for (int i = 0; i < 20; i++)
            anim.Update(new CharacterAnimSample(pos, Vector2.Zero, +1, true, "StandingState", "", Dt));
    }

    // Grow a row of sprouts up out of the floor and advance them to `progress`.
    private static ChunkMap SproutRow(int gx0, int gx1, int gty, float progress)
    {
        var chunks = Floor();
        for (int gx = gx0; gx <= gx1; gx++) Assert.NotNull(chunks.TryRequestTile(gx, gty));
        foreach (var s in chunks.ActiveSprouts) Assert.Equal(SproutFaces.Below, s.Faces);
        chunks.TickSprouts(MovementConfig.Current.SproutLifetime * progress);
        return chunks;
    }

    [Fact]
    public void Extract_GrowingSprout_EmitsMovingLeadingFace()
    {
        // Row of sprouts in cells gty=9 growing UP out of the floor. At 50% the exposed
        // top face is at SproutTopY(0.5) — half a tile above the ground line the plain
        // tile scan would report.
        var chunks = SproutRow(4, 8, 9, 0.5f);
        var anim = NewAnimator();
        var pos = new Vector2(PosX, PosY);
        WarmIdle(anim, pos);

        var buf = new SolverSurface[8];
        int n = TerrainSurfaces.Extract(chunks, anim, pos, +1, Scale, buf, out bool near);

        float sproutTopY = SproutTopY(0.5f);
        bool sproutTop = false, groundTop = false;
        for (int i = 0; i < n; i++)
        {
            _o.WriteLine($"plane {i}: p=({buf[i].Point.X:0.#},{buf[i].Point.Y:0.#}) n=({buf[i].Normal.X},{buf[i].Normal.Y}) mask={buf[i].BoneMask:x}");
            if (buf[i].Normal.Y >= -0.9f) continue;
            if (MathF.Abs(buf[i].Point.Y - sproutTopY) < 0.01f) sproutTop = true;
            if (MathF.Abs(buf[i].Point.Y - FloorTopY) < 0.01f) groundTop = true;
        }
        Assert.True(sproutTop, $"no up-facing plane at the growing volume's leading face (y={sproutTopY})");
        // The floor under the sprouts is covered by them, so its own top face is now
        // interior — but the floor extends past the row, so a y=160 plane is legitimate
        // only if it comes from a cell outside gx 4..8. Either way the sprout plane is
        // the one the solver needs, and standing feet are in the engage band.
        Assert.True(near, "feet on a sprouted floor should report near=true");
        _o.WriteLine($"ground plane also present: {groundTop}");
    }

    [Fact]
    public void Extract_SproutTrailingFace_IsNotEmitted()
    {
        // The BOTTOM face of an upward-growing volume sits inside the Solid parent it is
        // pushing out of. Emitting it would push a limb down into the rock.
        var chunks = SproutRow(4, 8, 9, 0.5f);
        var anim = NewAnimator();
        var pos = new Vector2(100f, 140f);
        WarmIdle(anim, pos);

        var buf = new SolverSurface[8];
        int n = TerrainSurfaces.Extract(chunks, anim, pos, +1, Scale, buf, out _);
        for (int i = 0; i < n; i++)
            Assert.False(buf[i].Normal.Y > 0.9f && buf[i].Point.Y > 160f,
                $"down-facing plane at y={buf[i].Point.Y} is a trailing face buried in the floor");
    }

    [Fact]
    public void Extract_TipInsideGrowingVolume_GetsUpwardExit()
    {
        // A foot standing on the floor with a nearly-complete sprout row on top of it:
        // the tip is INSIDE the volume and its cell is Sprouting, so neither the
        // buried-tile branch nor the free-face scan can see it.
        var chunks = SproutRow(4, 8, 9, 0.95f);
        var anim = NewAnimator();
        var pos = new Vector2(PosX, PosY);
        WarmIdle(anim, pos);

        var buf = new SolverSurface[8];
        int n = TerrainSurfaces.Extract(chunks, anim, pos, +1, Scale, buf, out bool near);
        Assert.True(n >= 1, "no surfaces at all with a sprout closing over the feet");

        float volumeTopY = SproutTopY(0.95f);
        bool exitUp = false;
        for (int i = 0; i < n; i++)
        {
            _o.WriteLine($"plane {i}: p=({buf[i].Point.X:0.##},{buf[i].Point.Y:0.##}) n=({buf[i].Normal.X},{buf[i].Normal.Y}) mask={buf[i].BoneMask:x}");
            if (buf[i].Normal.Y < -0.9f && MathF.Abs(buf[i].Point.Y - volumeTopY) < 0.05f) exitUp = true;
            // Never an exit DOWN through the floor, nor sideways into the neighbouring volume.
            Assert.False(buf[i].Normal.Y > 0.9f && buf[i].Point.Y > FloorTopY - 2f,
                $"exit plane pushes the tip down into the floor (y={buf[i].Point.Y})");
        }
        Assert.True(exitUp, "a tip buried in the growing volume got no upward exit plane");
        Assert.True(near);
    }
}
