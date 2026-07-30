using System;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// ReferenceCorrector: the no-coast corrector mode for guided moves — the
// retargeted clip IS the swept trajectory, the solve deforms the arc around
// terrain, and the caller's servo tracks the deformed target. Pins: identity on
// clear terrain, and a real deflection away from a tile the raw arc clips.
public class ReferenceCorrectorTests
{
    private const float Dt = 1f / 60f;

    private static EnvironmentContext Ctx(ChunkMap terrain, Vector2 bodyPos) => new()
    {
        Chunks    = terrain,
        Body      = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6), bodyPos),
        Dt        = Dt,
        Gravity   = new Vector2(0f, 600f),
        Corrector = new CorrectorScratch(),
        Modifiers = MovementModifiers.Identity,
    };

    // A straight horizontal reference: clip (0,0) → (1,-1) retargeted so the
    // path runs rightward at constant height (gate y == entry y makes the
    // clip's y-channel inert; only x advances).
    private static HermiteClipDocument StraightClip()
    {
        var doc = new HermiteClipDocument
        {
            Name = "test_straight",
            Keys =
            {
                new HermiteClipKey { X = 0f, Y =  0f, TX = 1f, TY = -1f },
                new HermiteClipKey { X = 1f, Y = -1f, TX = 1f, TY = -1f },
            },
        };
        doc.RederiveT();
        return doc;
    }

    [Fact]
    public void ClearTerrain_ReturnsRawClipTarget()
    {
        var terrain = SimTerrain.FromAscii("X", originTileX: 0, originTileY: 30);   // far away
        var entry = new Vector2(50f, 50f);
        var frame = new ReferenceFrame(entry, entry + new Vector2(60f, 0f));
        var clip  = StraightClip();
        var ctx   = Ctx(terrain, entry);

        var anchors = default(ChannelAnchors);
        ReferenceCorrector.DeformedTarget(ctx, clip, frame, progress: 0.2f, duration: 0.4f,
                                          ref anchors, out var target, out var targetVel);

        // No rows → identical to evaluating the clip directly.
        Assert.Equal(frame.Map(clip.Eval(0.2f)), target);
        Assert.Equal(frame.MapTangent(clip.EvalTangent(0.2f)) / 0.4f, targetVel);
    }

    [Fact]
    public void ArcThroughTile_TargetDeflectsAwayFromIt()
    {
        // A floor slab lying ACROSS the path ahead: reference runs rightward at
        // y=50; tiles occupy rows starting at y=48 from x=96 on — the swept
        // body (radius ~ half a tile) will violate the slab's exposed top/side.
        var terrain = SimTerrain.FromAscii(new string('X', 10) + "\n" + new string('X', 10),
                                           originTileX: 6, originTileY: 3);   // tiles at x≥96, y≥48
        var entry = new Vector2(40f, 50f);
        var frame = new ReferenceFrame(entry, entry + new Vector2(120f, 0f));
        var clip  = StraightClip();
        var ctx   = Ctx(terrain, entry);

        var anchors = default(ChannelAnchors);
        ReferenceCorrector.DeformedTarget(ctx, clip, frame, progress: 0.1f, duration: 0.5f,
                                          ref anchors, out var target, out _);

        var raw = frame.Map(clip.Eval(0.1f));
        Assert.NotEqual(raw, target);
        // The slab is below/ahead — the exposed faces push up (−y) and/or back
        // (−x); the deformation must not push the target INTO the slab.
        var d = target - raw;
        Assert.True(d.Y < 0.01f, $"deformation pushed toward the slab: Δ=({d.X:F2},{d.Y:F2})");
        Assert.True(d.Length() > 0.01f, "deformation should be non-trivial");
        // And the anchor carries the applied tick-0 deformation for continuity.
        Assert.NotEqual(Vector2.Zero, anchors[0]);
    }
}
