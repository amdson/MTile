using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// The live planner path end-to-end (step planner P3 — Plans/ANIMATION_STEP_PLANNER_IMPL.md):
// the real biped walk clip (its contact spans compile to a stride track), a real CharacterAnimator,
// and ascii terrain in the sample. Pins that the ownership handover happens (planner
// contacts drive the cadence), that stance targets sit on real treads, that the legacy
// path survives both the A/B toggle and a chunk-less sample, and that a step up changes
// the selected tread elevation.
public class AnimPlannedGaitTests
{
    private const int TS = Chunk.TileSize;
    private const float Scale = 0.6f;   // the scale the other animator tests use

    private static CharacterAnimator Animator()
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
        throw new DirectoryNotFoundException("SkeletonStates/biped");
    }

    // The walk clip's authored touchdown offset is ~40 rig units below the com anchor,
    // so a body riding 40·Scale above the floor puts planned landings on the floor line.
    private static float BodyRideHeight => 40f * Scale;

    [Fact]
    public void Walk_OnFlatTerrain_PlannerOwnsContacts_AndCadenceAdvances()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float floorTop = 3 * TS;
        var anim = Animator();

        float dt = 1f / 30f, vx = 25f, x = 40f;
        float prev = anim.State.Phase, totalPhase = 0f;
        int plannedContactFrames = 0, selfPlantFrames = 0, stanceOnFloor = 0;
        for (int i = 0; i < 90; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, floorTop - BodyRideHeight), new Vector2(vx, 0f), 1, true,
                "StandingState", "", dt, chunks: chunks));
            if (anim.PlannerActive && anim.ContactCount > 0) plannedContactFrames++;
            if (!anim.PlannerActive && anim.ContactCount > 0) selfPlantFrames++;
            for (int f = 0; f < anim.Planner.FeetCount; f++)
            {
                var p = anim.Planner.Plans[f];
                if (p.State == FootPlanState.Stance && p.HasSupport
                    && MathF.Abs(p.Target.Y - floorTop) < 0.01f) stanceOnFloor++;
            }
            float ph = anim.State.Phase, d = ph - prev; if (d < -0.5f) d += 1f;
            totalPhase += d; prev = ph;
        }

        Assert.True(totalPhase > 0.2f, $"cadence didn't advance ({totalPhase:0.000})");
        Assert.True(plannedContactFrames > 45,
            $"planner contacts held on only {plannedContactFrames}/90 frames");
        Assert.Equal(0, selfPlantFrames);   // the planner owns every contact while it runs
        Assert.True(stanceOnFloor > 30, $"stance targets sat on the floor tread on {stanceOnFloor} foot-frames");
    }

    // The A/B knob: with PlannerEnabled=false the exact same clip runs the animator's own
    // SelfPlant lifecycle — contacts still exist, the planner never runs.
    [Fact]
    public void PlannerDisabled_FallsBackToSelfPlant()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float floorTop = 2 * TS;
        bool prevEnabled = AnimSolverConfig.Current.PlannerEnabled;
        AnimSolverConfig.Current.PlannerEnabled = false;
        try
        {
            var anim = Animator();
            float dt = 1f / 30f, vx = 25f, x = 40f;
            int contactFrames = 0;
            for (int i = 0; i < 60; i++)
            {
                x += vx * dt;
                anim.Update(new CharacterAnimSample(
                    new Vector2(x, floorTop - BodyRideHeight), new Vector2(vx, 0f), 1, true,
                    "StandingState", "", dt, chunks: chunks));
                if (anim.ContactCount > 0) contactFrames++;
                Assert.False(anim.PlannerActive);
                Assert.Equal(0, anim.Planner.FeetCount);
            }
            Assert.True(contactFrames > 30, $"legacy contacts on only {contactFrames}/60 frames");
        }
        finally { AnimSolverConfig.Current.PlannerEnabled = prevEnabled; }
    }

    // No chunks in the sample (recorder, hand-built tests, hosts that don't wire
    // terrain): the clip must self-plant, untouched by the planner.
    [Fact]
    public void NoChunks_RunsSelfPlantPath()
    {
        var anim = Animator();
        float dt = 1f / 30f, vx = 25f, x = 0f;
        float prev = anim.State.Phase, totalPhase = 0f;
        for (int i = 0; i < 60; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), 1, true, "StandingState", "", dt));
            Assert.False(anim.PlannerActive);
            float ph = anim.State.Phase, d = ph - prev; if (d < -0.5f) d += 1f;
            totalPhase += d; prev = ph;
        }
        Assert.True(totalPhase > 0.2f, $"legacy cadence didn't advance ({totalPhase:0.000})");
    }

    // Walk across a 1-tile step up: the selected treads must move to the upper
    // elevation once the body crosses — landing selection tracks real terrain, not a
    // fixed ground plane.
    [Fact]
    public void Walk_AcrossAStepUp_SelectsTheUpperTread()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float lowerTop = 3 * TS, upperTop = 2 * TS, lipX = 16 * TS;
        var anim = Animator();

        float dt = 1f / 30f, vx = 25f, x = lipX - 60f;
        int lowerStance = 0, upperStance = 0;
        for (int i = 0; i < 200; i++)
        {
            x += vx * dt;
            // The body ramps up over the lip the way the fold glides it (a ~1-tile
            // rise spread across ~2 tiles of travel).
            float ground = x > lipX ? upperTop : lowerTop;
            float blend = MathHelper.Clamp((x - (lipX - 2 * TS)) / (2 * TS), 0f, 1f);
            float y = MathHelper.Lerp(lowerTop, ground == upperTop ? upperTop : lowerTop, blend)
                      - BodyRideHeight;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, MathF.Min(y, ground - BodyRideHeight + 4f)), new Vector2(vx, 0f), 1, true,
                "StandingState", "", dt, chunks: chunks));
            for (int f = 0; f < anim.Planner.FeetCount; f++)
            {
                var p = anim.Planner.Plans[f];
                if (p.State != FootPlanState.Stance || !p.HasSupport) continue;
                if (MathF.Abs(p.Support.Y - lowerTop) < 0.01f) lowerStance++;
                if (MathF.Abs(p.Support.Y - upperTop) < 0.01f) upperStance++;
            }
        }
        Assert.True(lowerStance > 10, $"never planted on the lower floor ({lowerStance})");
        Assert.True(upperStance > 10, $"never planted on the upper floor ({upperStance})");
    }
}
