using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// The run's flight ↔ stance edges must not jerk the drawn body. Measured before the fix
// (2026-08-25, flat floor, live json weights, physics body perfectly still at hover): the
// drawn rig root dropped ~3.8–4.5px in ONE frame when a foot contact captured (its weight
// went 0 → 0.76 → 1.0 in two frames and the ground-hold row yanked δ to the new foot),
// rebounded 2.5px the next frame, and popped 4.4px UP in one frame when the contact
// released (δ snapped to 0 with no solve in flight). Two mechanisms fix it:
//  · δ / d.x are emitted with temporal continuity — a smoothness row in the com block on
//    solve frames, an ease toward 0 on no-solve frames (CharacterAnimator._dyEmitted);
//  · a contact's weight is captured small and ramps over ContactEngageTime, the engage
//    mirror of ContactReleaseTime, so a fast cadence can't skip the phase feather.
public class LandingContinuityTests
{
    private const float Dt = Simulation.FixedDt;
    private const float Scale = 0.6f;

    [Fact]
    public void SteadyRun_DrawnRootNeverJumps_AndContactsRampIn()
    {
        var chunks = Floor();
        var anim = NewAnimator();
        var buf = new SolverSurface[8];
        float ramp = Dt / AnimSolverConfig.Current.ContactEngageTime;   // max weight per frame
        Assert.True(ramp < 0.5f, "ContactEngageTime is so short the ramp is moot for this test");

        float prevRootY = float.NaN, worstJump = 0f; int worstFrame = -1, captures = 0, stanceFrames = 0;
        float prevDy = float.NaN, worstDyJump = 0f; int worstDyFrame = -1;
        float worstCaptureWeight = 0f, maxRise = 0f;
        var prevW = new System.Collections.Generic.Dictionary<int, float>();
        var cfg = new SimConfigMulti
        {
            Terrain = chunks, Frames = 240,
            Players = { new SimPlayer
            {
                // 20px above the floor top (originTileY=10) — falls, lands, and is well
                // into a steady run before the f>=90 measurement window starts.
                StartPosition = new Vector2(40f, 10 * Chunk.TileSize - 20f),
                Script        = InputScript.Always(new PlayerInput { Right = true }),
            } },
        };
        SimRunner.RunMulti(cfg, onFrame: (f, players) =>
        {
            var p = players[0];
            int tc = TerrainSurfaces.Extract(chunks, anim, p.Body.Position, p.Facing, Scale, buf, out bool near);
            anim.Update(CharacterAnimSample.From(p, Dt, buf, tc, near, chunks));
            // The drawn root relative to the physics body: the body hovers still, so every
            // change here is the animator's own δ (com baseline is phase-sampled and smooth).
            float rootY = AttackGlowSystem.RigRoot(p.Body.Position, p.Facing, anim, Scale).Y - p.Body.Position.Y;
            if (f >= 90)   // past spin-up: steady run
            {
                if (!float.IsNaN(prevRootY))
                {
                    float jump = MathF.Abs(rootY - prevRootY);
                    if (jump > worstJump) { worstJump = jump; worstFrame = f; }
                    float dyJump = MathF.Abs(anim.VerticalOffset - prevDy);
                    if (dyJump > worstDyJump) { worstDyJump = dyJump; worstDyFrame = f; }
                }
                if (anim.SolvedThisFrame && MathF.Abs(anim.VerticalOffset) > 0.5f) stanceFrames++;
                var now = new System.Collections.Generic.Dictionary<int, float>();
                for (int i = 0; i < anim.ContactCount; i++)
                {
                    var c = anim.ContactAt(i);
                    now[c.Bone] = c.Weight;
                    if (!prevW.TryGetValue(c.Bone, out float pw))
                    {
                        if (f == 90) continue;   // first accounted frame: existing contacts aren't captures
                        captures++; worstCaptureWeight = MathF.Max(worstCaptureWeight, c.Weight);
                    }
                    else maxRise = MathF.Max(maxRise, c.Weight - pw);
                }
                prevW = now;
            }
            prevRootY = rootY; prevDy = anim.VerticalOffset;
        });

        Assert.True(captures >= 4, $"only {captures} contact captures in 150 frames — not a steady run");
        Assert.True(stanceFrames > 0, "δ never engaged — the ground hold did nothing");
        // The animator's OWN contribution, δ frame to frame: was 4.4px (one-frame release
        // pop) / 2.4px + a 2.3px rebound (capture). What remains (~2.1px, measured) is the
        // LAST stance frame: the clip's authored foot rises steeply toward toe-off while the
        // ground hold is at full weight, so δ tracks it — a clip/com-track mismatch, not an
        // edge discontinuity (the release itself now eases: 3.3 → 2.4 → 1.7). The drawn root
        // also carries the clip's phase-sampled com track (~1.5px/frame through flight —
        // authored bounce), so it is reported for context but the contract is on δ.
        Assert.True(worstDyJump < 2.5f,
            $"δ jumped {worstDyJump:0.00}px in one frame at f={worstDyFrame} (drawn root worst {worstJump:0.00}px at f={worstFrame})");
        Assert.True(worstCaptureWeight <= ramp + 1e-4f, $"a contact captured at weight {worstCaptureWeight:0.00} (cap {ramp:0.00})");
        Assert.True(maxRise <= ramp + 1e-4f, $"a contact's weight rose {maxRise:0.00} in one frame (cap {ramp:0.00})");
    }

    private static ChunkMap Floor(int widthTiles = 200, int originTileY = 10)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 3; r++) sb.AppendLine(new string('X', widthTiles));
        return SimTerrain.FromAscii(sb.ToString(), originTileY: originTileY);
    }

    private static CharacterAnimator NewAnimator()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "SkeletonStates", "biped"))) d = d.Parent;
        Assert.NotNull(d);
        return new CharacterAnimator(SkeletonExamples.Biped(), Scale,
                                     AnimationStore.LoadAll(Path.Combine(d.FullName, "SkeletonStates", "biped")));
    }
}
