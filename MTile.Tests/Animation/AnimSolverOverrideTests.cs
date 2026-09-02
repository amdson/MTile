using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// Programmatic per-frame overrides of the animation solver's knobs (FrameInputs.Solver —
// the animator's EFFECTIVE copy of AnimSolverConfig.Current, refreshed every Update and
// mutable by the active move driver in Contribute), plus the first user of the mechanism:
// GroundLocomotionDriver softening ComWeightY for a run squeezed under a low ceiling.
public class AnimSolverOverrideTests
{
    const float Dt = 1f / 60f;

    // --- the mechanism ------------------------------------------------------------

    // CopyFrom is a hand-written field list; a knob added to AnimSolverConfig but not to
    // CopyFrom would silently read at its DEFAULT on the effective copy (the json value
    // never reaching the solver). Walk every public settable property by reflection.
    [Fact]
    public void CopyFrom_CoversEveryKnob()
    {
        var src = new AnimSolverConfig();
        var dst = new AnimSolverConfig();
        var props = typeof(AnimSolverConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        int n = 0;
        foreach (var p in props)
        {
            if (!p.CanWrite) continue;
            n++;
            object v = p.PropertyType == typeof(float) ? 1000f + n
                     : p.PropertyType == typeof(int)   ? 7 + n
                     : p.PropertyType == typeof(bool)  ? !(bool)p.GetValue(dst)
                     : throw new Xunit.Sdk.XunitException($"knob {p.Name} has type {p.PropertyType} — extend this test");
            p.SetValue(src, v);
        }
        Assert.True(n >= 20, $"only {n} knobs found — reflection filter broke?");

        dst.CopyFrom(src);
        foreach (var p in props)
            if (p.CanWrite)
                Assert.True(Equals(p.GetValue(src), p.GetValue(dst)),
                            $"AnimSolverConfig.CopyFrom does not copy {p.Name}");
    }

    // The driver's override is exactly scoped: Run clip band, physically supported, roof
    // overhead. Everything else leaves the effective config equal to Current.
    [Fact]
    public void GroundLocomotion_SoftensComWeightY_OnlyForSupportedRunUnderRoof()
    {
        var cur  = AnimSolverConfig.Current;
        float soft = cur.LowCeilingRunComWeightY;
        Assert.NotEqual(cur.ComWeightY, soft);   // the experiment is armed by default
        var drv = new GroundLocomotionDriver();

        float After(float vx, int facing, bool roof, float gap = 0f)
        {
            var dst = new FrameInputs();
            dst.Solver.CopyFrom(cur);
            drv.Contribute(Sample(new Vector2(vx, 0f), facing, roof, gap), 0f, dst);
            Assert.Equal(cur.ComWeightX, dst.Solver.ComWeightX);   // only Y is touched
            Assert.Equal(cur.TierContact, dst.Solver.TierContact);
            return dst.Solver.ComWeightY;
        }

        Assert.Equal(soft,           After(90f,  +1, roof: true));           // run under a roof
        Assert.Equal(soft,           After(-90f, -1, roof: true));           // …facing left too
        Assert.Equal(cur.ComWeightY, After(90f,  +1, roof: false));          // open sky
        Assert.Equal(cur.ComWeightY, After(25f,  +1, roof: true));           // walk band
        Assert.Equal(cur.ComWeightY, After(0f,   +1, roof: true));           // idle
        Assert.Equal(cur.ComWeightY, After(90f,  -1, roof: true));           // reversal skid (RunTurn)
        Assert.Equal(cur.ComWeightY, After(90f,  +1, roof: true, gap: 12f)); // pre-contact Hold
    }

    // Through the animator: the override reaches the solve the same frame, lasts exactly
    // that frame, and the cadence still advances while it's in force (the solve stays
    // healthy with the softer tie — the run under the roof must still run).
    [Fact]
    public void Animator_OverrideIsPerFrame_AndCadenceStillAdvances()
    {
        var anim = RealAnimator();
        var cur  = AnimSolverConfig.Current;
        var pos  = Vector2.Zero;
        var vel  = new Vector2(90f, 0f);

        for (int i = 0; i < 20; i++) { pos.X += vel.X * Dt; anim.Update(Sample(vel, +1, roof: false, pos: pos)); }
        Assert.Equal(AnimClip.Run, anim.State.Clip);
        Assert.Equal(cur.ComWeightY, anim.SolverConfig.ComWeightY);

        float total = 0f, prev = anim.State.Phase;
        for (int i = 0; i < 40; i++)
        {
            pos.X += vel.X * Dt;
            anim.Update(Sample(vel, +1, roof: true, pos: pos));
            Assert.Equal(AnimClip.Run, anim.State.Clip);
            Assert.Equal(cur.LowCeilingRunComWeightY, anim.SolverConfig.ComWeightY);
            Assert.Equal(cur.ComWeightX, anim.SolverConfig.ComWeightX);
            float d = anim.State.Phase - prev; if (d < -0.5f) d += 1f;
            total += d; prev = anim.State.Phase;
        }
        Assert.True(total > 0.3f, $"cadence stalled under the softened com tie (total phase {total:0.000})");

        pos.X += vel.X * Dt;
        anim.Update(Sample(vel, +1, roof: false, pos: pos));
        Assert.Equal(cur.ComWeightY, anim.SolverConfig.ComWeightY);   // restored the very next frame
    }

    // --- the signal ---------------------------------------------------------------

    // CharacterAnimSample.From now flags LowCeiling for a GROUNDED, non-crouched body: an
    // upright run through a 3-high (33px) corridor — which Standing threads at fold hover
    // with ~2px of head-room, no auto-crouch — fires it; the same run under a 4-high roof
    // (~13px of head-room, the corridor stage's ordinary interior) does not. (Rebuilt for
    // the 11px grid: the body's fold-hover standing envelope, FoldHoverOffset +
    // (StandingHeight - Radius) ≈ 30.8px, is unchanged by TileSize, so what used to be a
    // 2-vs-3-tile split at 16px tiles is now a 3-vs-4-tile split at 11px tiles.)
    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void SampleFrom_UprightRunInCorridor_FlagsLowCeilingOnlyWhenTileIsRightOverhead(int gapTiles, bool expect)
    {
        const int W = 60;
        var sb = new StringBuilder();
        sb.AppendLine(new string('X', W));                                  // roof: tile row 0
        for (int r = 0; r < gapTiles; r++) sb.AppendLine(new string('O', W));
        sb.AppendLine(new string('X', W));                                  // floor top at (gap+1)·16
        sb.AppendLine(new string('X', W));
        var terrain = SimTerrain.FromAscii(sb.ToString());
        float floorTop = (gapTiles + 1) * Chunk.TileSize;

        var sim = new Simulation(terrain, new Vector2(48f, floorTop - PlayerCharacter.Radius - 2f));
        sim.Player.RestrictToFallAndStand();
        var input = new PlayerInput { Right = true };
        for (int f = 0; f < 120; f++) sim.Step(input);

        var s = CharacterAnimSample.From(sim.Player, Dt, chunks: sim.Chunks);
        Assert.True(s.Grounded, $"not grounded after settling: {s.MovementState}");
        Assert.Equal(AnimTag.None, s.Tag);                                  // upright, not auto-crouched
        Assert.True(GroundLocomotionDriver.IsRunning(in s), $"not at run speed: vx={s.Velocity.X:0.0}");
        Assert.Equal(expect, s.LowCeiling);
    }

    // --- helpers ------------------------------------------------------------------

    private static CharacterAnimSample Sample(Vector2 vel, int facing, bool roof, float gap = 0f,
                                              Vector2 pos = default)
        => new(pos, vel, facing, true, "Standing", "", Dt, lowCeiling: roof, groundGap: gap);

    private static CharacterAnimator RealAnimator()
    {
        var clips = AnimationStore.LoadAll(FindStatesDir());
        Assert.NotEmpty(clips);
        return new CharacterAnimator(SkeletonExamples.Biped(), 0.6f, clips);
    }

    private static string FindStatesDir()
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
}
