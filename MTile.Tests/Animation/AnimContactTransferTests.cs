using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Contact handoff at a clip switch (ANIMATION_OWNERSHIP_CONTRACT.md §4, timing-stage T4): a
// stance contact transfers to the incoming clip when the same foot is a support owner there
// and is in stance at the entry phase — across the self-plant → planner ownership boundary
// too, with the planner adopting the transferred point instead of re-selecting one.
public class AnimContactTransferTests
{
    private readonly ITestOutputHelper _o;
    public AnimContactTransferTests(ITestOutputHelper o) => _o = o;
    private const float Scale = 0.6f, Dt = 1f / 60f;

    // biped: run (SelfPlant labels) → walk (PlannedSupport labels) on flat terrain. Drop the
    // speed on a frame where a foot is planted in BOTH clips; the planted contact survives
    // the switch with its target unchanged and is now planner-owned.
    [Fact]
    public void RunToWalk_KeepsThePlantedFoot_AndThePlannerAdoptsIt()
    {
        var clips = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), "biped"));
        var skel = SkeletonExamples.Load("biped");
        var anim = new CharacterAnimator(skel, Scale, clips);
        var chunks = FlatFloor();
        float groundY = 10 * Chunk.TileSize;
        // The rig's sole sits 2·Radius below the body center (the com anchor identity), so the
        // body rests one diameter above the floor.
        var pos = new Vector2(100f, groundY - 2f * PlayerCharacter.Radius);
        var walk = clips.Find(c => c.Name == "walk");
        Assert.True(ClipStrideTrack.TryCompile(walk, skel, out var walkTrack, out string err), err);

        // Terrain surfaces as the game extracts them each frame (the plant snaps onto the floor).
        var surfaces = new SolverSurface[8];
        CharacterAnimSample Sample(Vector2 p, float vx)
        {
            int tc = TerrainSurfaces.Extract(chunks, anim, p, +1, Scale, surfaces, out bool near);
            return new CharacterAnimSample(p, new Vector2(vx, 0f), +1, true, "WalkState", "", Dt,
                                           surfaces: surfaces, surfaceCount: tc, surfacesNear: near, chunks: chunks);
        }

        // Run until a foot is planted at a phase inside that foot's WALK stance.
        int transferred = -1; Vector2 target = default;
        for (int i = 0; i < 400; i++)
        {
            pos.X += 90f * Dt;
            anim.Update(Sample(pos, 90f));
            for (int c = 0; c < anim.ContactCount; c++)
            {
                var (bone, w, t) = anim.ContactAt(c);
                var ft = walkTrack.ForBone(bone);
                // The next frame's phase is what the walk enters with; it must still be in stance.
                float nextPhase = anim.State.Phase + anim.PhaseStep;
                // (The run's and the walk's stances of one foot overlap only in the walk stance's
                //  second half, so accept any progress short of the imminent liftoff.)
                if (w >= 0.3f && ft != null && ft.StanceAt(nextPhase, out float u) >= 0 && u < 0.85f)
                { transferred = bone; target = t; }
            }
            if (transferred >= 0) break;
        }
        Assert.True(transferred >= 0, "never found a run frame with a planted foot inside its walk stance");
        _o.WriteLine($"switching at phase {anim.State.Phase:0.000} with {anim.Skeleton.Bones[transferred].Name} planted at {target}");

        // Switch: walk speed from here on.
        pos.X += 25f * Dt;
        anim.Update(Sample(pos, 25f));
        Assert.Equal(AnimClip.Walk, anim.State.Clip);

        bool found = false;
        for (int c = 0; c < anim.ContactCount; c++)
        {
            var (bone, w, t) = anim.ContactAt(c);
            if (bone != transferred) continue;
            found = true;
            Assert.True((t - target).Length() < 1e-3f, $"transferred target moved: {target} → {t}");
            Assert.True(w > 0.4f, $"transferred weight dropped to {w:0.00}");
        }
        Assert.True(found, "the planted foot was released at the clip switch");
        Assert.True(anim.PlannedContactCount >= 1, "the walk's planner did not adopt the transferred contact");
    }

    // A clip with no labels (Idle) carries nothing: every contact releases.
    [Fact]
    public void ToIdle_ReleasesEverything()
    {
        var clips = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), "biped"));
        var anim = new CharacterAnimator(SkeletonExamples.Load("biped"), Scale, clips);
        var pos = Vector2.Zero;
        for (int i = 0; i < 60; i++)
        {
            pos.X += 90f * Dt;
            anim.Update(new CharacterAnimSample(pos, new Vector2(90f, 0f), +1, true, "WalkState", "", Dt));
        }
        // Stop dead and let the stopping policy finish its step; once Idle is selected the
        // contacts must be gone.
        for (int i = 0; i < 60 && anim.State.Clip != AnimClip.Idle; i++)
            anim.Update(new CharacterAnimSample(pos, Vector2.Zero, +1, true, "StandingState", "", Dt));
        Assert.Equal(AnimClip.Idle, anim.State.Clip);
        Assert.Equal(0, anim.ContactCount);
    }

    private static ChunkMap FlatFloor()
    {
        var sb = new System.Text.StringBuilder();
        for (int r = 0; r < 18; r++)
        {
            for (int c = 0; c < 60; c++) sb.Append(r >= 10 ? 'X' : 'O');
            if (r < 17) sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: 0);
    }

    private static string FindDir(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, name);
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException(name);
    }
}
