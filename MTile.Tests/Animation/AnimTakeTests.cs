using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// The take file (Recording/AnimTake.cs, Plans/ANIM_TAKE_VIEWER_PLAN.md) persists the
// animator's INPUT stream, and the offline viewer re-runs a CharacterAnimator over it.
// Two things must hold for that design to be sound:
//  · ROUND-TRIP — save → load reproduces every sample field and the deduped terrain states.
//  · REPLAY DETERMINISM — an animator fed the DTO-round-tripped stream emits bit-identical
//    poses to one fed the original samples. This guards the core premise (the animator is
//    a pure function of the sample stream); it fails if sim-coupled or mutable static
//    state ever sneaks into the animation layer.
public class AnimTakeTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void SaveLoad_RoundTrips_SamplesAndTerrain()
    {
        var take = new AnimTake { SkeletonScale = 0.6f, PlayerRadius = 9.5f };

        var terrainA = MakeTerrain(solidCells: 3);
        var terrainB = MakeTerrain(solidCells: 5);   // "a block appeared" mid-take

        take.AddFrame(MakeSample(0), terrainA);
        take.AddFrame(MakeSample(1), terrainA);      // identical terrain → deduped
        take.AddFrame(MakeSample(2), terrainB);      // changed → second state

        Assert.Equal(2, take.TerrainStates.Count);
        Assert.Equal(0, take.Frames[1].Terrain);
        Assert.Equal(1, take.Frames[2].Terrain);

        string path = Path.Combine(Path.GetTempPath(), $"mtile_take_test_{Guid.NewGuid():N}.json");
        try
        {
            take.Save(path);
            var loaded = AnimTake.Load(path);

            Assert.Equal(take.SkeletonScale, loaded.SkeletonScale);
            Assert.Equal(take.PlayerRadius, loaded.PlayerRadius);
            Assert.Equal(take.Frames.Count, loaded.Frames.Count);
            Assert.Equal(take.TerrainStates.Count, loaded.TerrainStates.Count);
            Assert.Equal(3, loaded.TerrainStates[0].Count);
            Assert.Equal(5, loaded.TerrainStates[1].Count);

            for (int i = 0; i < take.Frames.Count; i++)
            {
                var a = MakeSample(i);                      // the original sample
                var b = loaded.Frames[i].ToSample();        // through save → load → DTO
                Assert.Equal(a.Position, b.Position);
                Assert.Equal(a.Velocity, b.Velocity);
                Assert.Equal(a.Facing, b.Facing);
                Assert.Equal(a.Grounded, b.Grounded);
                Assert.Equal(a.MovementState, b.MovementState);
                Assert.Equal(a.Tag, b.Tag);
                Assert.Equal(a.Action, b.Action);
                Assert.Equal(a.Dt, b.Dt);
                Assert.Equal(a.ActionTime, b.ActionTime);
                Assert.Equal(a.ActionDuration, b.ActionDuration);
                Assert.Equal(a.MovementProgress, b.MovementProgress);
                Assert.Equal(a.HasGrip, b.HasGrip);
                Assert.Equal(a.GripTarget, b.GripTarget);
                Assert.Equal(a.HasAim, b.HasAim);
                Assert.Equal(a.AimDir, b.AimDir);
                Assert.Equal(a.Pins?.Length ?? 0, b.Pins?.Length ?? 0);
                for (int p = 0; p < (a.Pins?.Length ?? 0); p++)
                {
                    Assert.Equal(a.Pins[p].Bone, b.Pins[p].Bone);
                    Assert.Equal(a.Pins[p].Target, b.Pins[p].Target);
                }
                Assert.Equal(a.Surfaces?.Length ?? 0, b.Surfaces?.Length ?? 0);
                for (int s = 0; s < (a.Surfaces?.Length ?? 0); s++)
                {
                    Assert.Equal(a.Surfaces[s].Point, b.Surfaces[s].Point);
                    Assert.Equal(a.Surfaces[s].Normal, b.Surfaces[s].Normal);
                    Assert.Equal(a.Surfaces[s].Margin, b.Surfaces[s].Margin);
                }
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Replay_OfDtoRoundTrippedStream_IsBitIdentical()
    {
        var skel  = SkeletonExamples.Biped();
        var clips = new[] { WalkClip(skel), FallClip(skel) };

        // A walking stream with a mid-take hop — exercises the cadence solve (contacts,
        // δ/d.x, smoothing) and a grounded↔airborne transition.
        var stream = new List<CharacterAnimSample>();
        float x = 0f, vx = 30f;   // walk-range speed (only a Walk clip is bound)
        for (int i = 0; i < 90; i++)
        {
            x += vx * Dt;
            bool air = i is > 40 and < 55;
            stream.Add(new CharacterAnimSample(
                new Vector2(x, air ? -6f : 0f), new Vector2(vx, air ? 20f : 0f), +1, !air,
                air ? "FallingState" : "WalkState", "", Dt));
        }

        // Original stream → animator A; through the take DTO (as the viewer would see
        // it) → animator B. Poses must match to the bit, frame by frame.
        var take = new AnimTake();
        foreach (var s in stream) take.AddFrame(s, MakeTerrain(1));

        var a = new CharacterAnimator(skel, 0.6f, clips);
        var b = new CharacterAnimator(skel, 0.6f, clips);
        for (int i = 0; i < stream.Count; i++)
        {
            a.Update(stream[i]);
            b.Update(take.Frames[i].ToSample());
            for (int bone = 0; bone < skel.Count; bone++)
                Assert.True(a.Pose.Local[bone].Rotation == b.Pose.Local[bone].Rotation,
                    $"pose diverged at frame {i}, bone {skel.Bones[bone].Name}: " +
                    $"{a.Pose.Local[bone].Rotation} vs {b.Pose.Local[bone].Rotation}");
            Assert.True(a.VerticalOffset == b.VerticalOffset && a.HorizontalOffset == b.HorizontalOffset,
                $"solved offsets diverged at frame {i}");
        }
    }

    // --- fixtures ---------------------------------------------------------------

    private static CharacterAnimSample MakeSample(int i)
    {
        // Vary everything by index; include pins/surfaces on some frames only.
        var pins = i % 2 == 0
            ? new[] { new ExternalPin("arm_l_lower", new Vector2(10 + i, 20 + i)) }
            : null;
        var surfaces = i % 2 == 1
            ? new[] { new SolverSurface(new Vector2(5 + i, 6 + i), new Vector2(0, -1), 1.5f) }
            : null;
        return new CharacterAnimSample(
            new Vector2(100 + i * 3.5f, 50 - i), new Vector2(60 + i, -5f), i % 2 == 0 ? 1 : -1,
            i % 3 != 0, "WalkState", i == 2 ? "GroundSlash1" : "", Dt,
            actionTime: 0.1f * i, actionDuration: 0.4f, movementProgress: 0.25f * i,
            pins: pins, surfaces: surfaces,
            hasGrip: i == 1, gripTarget: new Vector2(7, 8),
            hasAim: i == 2, aimDir: new Vector2(0.6f, -0.8f), tag: (AnimTag)(i % 4));
    }

    private static DenseTerrainCapture MakeTerrain(int solidCells)
    {
        var state = new TileState[Chunk.Size * Chunk.Size];
        var type  = new TileType[Chunk.Size * Chunk.Size];
        for (int i = 0; i < solidCells; i++)
        {
            state[i * 7 + 3] = TileState.Solid;
            type[i * 7 + 3]  = (TileType)(i % 3);
        }
        return new DenseTerrainCapture
        {
            Chunks = new[]
            {
                new DenseTerrainCapture.ChunkCells { Pos = new Point(0, 0), State = state, Type = type },
            },
        };
    }

    // Bind-relative scissoring walk with planted feet — the standard fixture
    // (cf. SmoothingTests.WalkClip).
    private static AnimationDocument WalkClip(Skeleton skel)
    {
        var clip = new AnimationDocument { Name = "walk", Type = "Walk", Duration = 0.8f, Loop = true };
        clip.Keyframes.Add(Kf(skel, 0f,   legL:  1.0f, legR: -1.0f, plant: "foot_r"));
        clip.Keyframes.Add(Kf(skel, 0.5f, legL: -1.0f, legR:  1.0f, plant: "foot_l"));
        clip.Keyframes.Add(Kf(skel, 1f,   legL:  1.0f, legR: -1.0f, plant: "foot_r"));
        return clip;
    }

    private static AnimationDocument FallClip(Skeleton skel)
    {
        var clip = new AnimationDocument { Name = "fall", Type = "Fall", Duration = 1f, Loop = true };
        var p = skel.CreatePose();
        Swing(skel, p, "leg_l_upper", -0.5f);
        Swing(skel, p, "leg_r_upper",  0.3f);
        clip.Keyframes.Add(new AnimationKeyframe { Time = 0f, Bones = PoseData.Capture(p) });
        clip.Keyframes.Add(new AnimationKeyframe { Time = 1f, Bones = PoseData.Capture(p) });
        return clip;
    }

    private static AnimationKeyframe Kf(Skeleton skel, float t, float legL, float legR, string plant)
    {
        var p = skel.CreatePose();
        Swing(skel, p, "leg_l_upper", legL);
        Swing(skel, p, "leg_r_upper", legR);
        return new AnimationKeyframe
        {
            Time = t,
            Bones = PoseData.Capture(p),
            Contacts = new List<ContactLabel> { new() { Node = plant, Weight = 1f } },
        };
    }

    private static void Swing(Skeleton skel, SkeletonPose p, string bone, float rot)
    {
        int i = skel.IndexOf(bone);
        var t = p.Local[i];
        t.Rotation = skel.Bones[i].Rotation + rot;
        p.SetLocal(i, t);
    }
}
