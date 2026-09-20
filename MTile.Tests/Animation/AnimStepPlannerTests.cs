using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// SupportQuery + StepPlanner (step planner P2 — Plans/ANIMATION_STEP_PLANNER_IMPL.md):
// finite treads with stable identity, and the planner's stance/swing lifecycle driven
// headless over ascii terrain — selection near the preferred landing, hysteresis,
// late-swing lock, invalidation after tile removal, and honest Unplanned reporting.
public class AnimStepPlannerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int TS = Chunk.TileSize;

    // ── SupportQuery ─────────────────────────────────────────────────────────

    [Fact]
    public void QueryTreads_EmitsPerCellTops_WithStableIds()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOO
            OOOOOOOO
            XXXXXXXX
            XXXXXXXX", originTileX: 0, originTileY: 0);
        Span<SupportSegment> dst = stackalloc SupportSegment[32];
        int n = SupportQuery.QueryTreads(chunks, new Vector2(4 * TS, 2 * TS), 1.6f * TS, dst);
        Assert.True(n >= 3, $"expected several treads, got {n}");
        var ids = new HashSet<long>();
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(2 * TS, dst[i].Y, 3);            // only the exposed row-2 tops
            Assert.Equal(TS, dst[i].X1 - dst[i].X0, 3);   // per-cell, unmerged
            Assert.True(ids.Add(dst[i].Id), "tread ids must be unique");
            Assert.True(SupportQuery.Revalidate(chunks, dst[i]));
        }
    }

    [Fact]
    public void Revalidate_FailsAfterBreak_AndForInteriorCells()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOO
            XXXX
            XXXX", originTileX: 0, originTileY: 0);
        var tread = new SupportSegment { X0 = TS, X1 = 2 * TS, Y = TS, Id = SupportSegment.PackId(1, 1) };
        Assert.True(SupportQuery.Revalidate(chunks, tread));
        // Interior cell (solid above) is never a tread.
        var interior = new SupportSegment { Id = SupportSegment.PackId(1, 2) };
        Assert.False(SupportQuery.Revalidate(chunks, interior));
        chunks.BreakCell(1, 1);
        Assert.False(SupportQuery.Revalidate(chunks, tread));
    }

    // ── StepPlanner ──────────────────────────────────────────────────────────

    // The tiny rig + clip from AnimStrideTrackTests: foot tip rests ~(4,10) rig units
    // below/ahead of the body. One stance [0.1, 0.6), one swing [0.6, 1.1).
    private static Skeleton TinyRig()
    {
        var b = new SkeletonBuilder("tiny");
        int hip = b.AddRoot("hip", 0f, 4f);
        b.Add("foot", hip, MathHelper.PiOver2, 10f);
        return b.Build();
    }

    private static ClipStrideTrack Track()
    {
        var doc = new AnimationDocument
        {
            Name = "gait", Type = "Misc", Skeleton = "tiny", Loop = true, Duration = 0.8f,
            Keyframes = new List<AnimationKeyframe>
            {
                Key(0.1f, 0.00f), Key(0.3f, 0.10f), Key(0.6f, 0.25f), Key(0.8f, 0.05f),
            },
            // The stance the four keys used to imply: planted at 0.1 and 0.3, free after.
            Contacts = new List<ContactSpan>
            { new() { Point = "foot", Start = 0.1f, End = 0.6f } },
        };
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        return track;

        static AnimationKeyframe Key(float t, float hipRot) => new()
        {
            Time = t,
            Bones = new List<PoseBoneEntry> { new() { Bone = "hip", Rotation = hipRot } },
        };
    }

    private static PlannerInputs Inputs(ClipStrideTrack track, ChunkMap chunks, Vector2 body,
                                        Vector2 vel, float phase, float rate = 1.25f)
        => new()
        {
            BodyPos = body, BodyVel = vel, Facing = 1, Scale = 2f,
            Phase = phase, NominalRate = rate, Dt = 1f / 60f,
            Track = track, Chunks = chunks, PredictAt = null,
        };

    // Walk the phase through a full cycle over a flat floor: stance frames must plant
    // on the floor top with one stable tread; swing frames must select a landing.
    [Fact]
    public void FlatFloor_StanceAndSwing_PlanOnTheFloorTop()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float floorTop = 3 * TS;
        var track = Track();
        var planner = new StepPlanner();

        float phase = 0.15f;                  // inside the stance
        var body = new Vector2(60f, floorTop - 20f);
        var vel = new Vector2(80f, 0f);
        long stanceId = 0; int stanceFrames = 0, swingPlanned = 0;
        for (int f = 0; f < 60; f++)
        {
            planner.Update(Inputs(track, chunks, body, vel, phase));
            Assert.Equal(1, planner.FeetCount);
            var p = planner.Plans[0];
            if (p.State == FootPlanState.Unplanned)
                output.WriteLine($"f={f,2} phase={phase:0.000} Unplanned reject={p.Reject}");
            if (p.State == FootPlanState.Stance)
            {
                stanceFrames++;
                Assert.True(p.HasSupport, $"stance without support at f={f} (reject {p.Reject})");
                Assert.Equal(floorTop, p.Target.Y, 3);
                if (stanceId == 0) stanceId = p.Support.Id;
                else Assert.Equal(stanceId, p.Support.Id);   // fixed while planted
            }
            else if (stanceId != 0) stanceId = 0;            // stance ended; next cycle replants elsewhere
            if (p.State == FootPlanState.Swing)
            {
                swingPlanned++;
                Assert.Equal(floorTop, p.Support.Y, 3);       // landing on the floor
                // The planner preserves the authored swing SHAPE, which may graze or
                // dip sub-tile amounts below the surface (ground-hold's business); it
                // only guarantees no tile-scale penetration (SwingProbeLift).
                Assert.True(p.Target.Y <= floorTop + 3.5f,
                    $"swing target sank tile-deep under the floor: {p.Target.Y:0.0} vs {floorTop}");
            }
            phase = (phase + 1.25f / 60f) % 1f;
            body.X += vel.X / 60f;
        }
        Assert.True(stanceFrames > 10, $"stance never held ({stanceFrames})");
        Assert.True(swingPlanned > 10, $"swing never planned a landing ({swingPlanned})");
    }

    [Fact]
    public void StanceSupport_ReleasesWhenTheTileBreaks()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOO
            OOOOOOOO
            XXXXXXXX", originTileX: 0, originTileY: 0);
        float floorTop = 2 * TS;
        var track = Track();
        var planner = new StepPlanner();
        var body = new Vector2(30f, floorTop - 20f);

        planner.Update(Inputs(track, chunks, body, Vector2.Zero, phase: 0.2f));
        var p = planner.Plans[0];
        Assert.Equal(FootPlanState.Stance, p.State);
        var (gtx, gty) = SupportSegment.UnpackId(p.Support.Id);
        chunks.BreakCell(gtx, gty);

        planner.Update(Inputs(track, chunks, body, Vector2.Zero, phase: 0.21f));
        p = planner.Plans[0];
        // The broken tread is gone; the planner may fail over to a neighboring tread
        // (touchdown re-selection) but must never keep the dead one.
        if (p.HasSupport) Assert.NotEqual(SupportSegment.PackId(gtx, gty), p.Support.Id);
    }

    [Fact]
    public void NoTerrain_ReportsUnplanned_NoFabricatedSupport()
    {
        var chunks = SimTerrain.FromAscii("OOOO\nOOOO", originTileX: 0, originTileY: 0);
        var track = Track();
        var planner = new StepPlanner();
        planner.Update(Inputs(track, chunks, new Vector2(20f, 10f), Vector2.Zero, phase: 0.2f));
        var p = planner.Plans[0];
        Assert.Equal(FootPlanState.Unplanned, p.State);
        Assert.False(p.HasSupport);
        Assert.Equal(StepReject.NoSupport, p.Reject);
    }

    // Late in the swing, a valid selected landing must not switch even if the body
    // drifts toward a different tread.
    [Fact]
    public void LateSwing_LocksTheSelectedLanding()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float floorTop = 2 * TS;
        var track = Track();
        var planner = new StepPlanner();
        var vel = new Vector2(60f, 0f);

        // Mid-swing (phase 0.85 → u ≈ 0.5): select a landing.
        var body = new Vector2(60f, floorTop - 20f);
        planner.Update(Inputs(track, chunks, body, vel, phase: 0.85f));
        var mid = planner.Plans[0];
        Assert.Equal(FootPlanState.Swing, mid.State);
        Assert.True(mid.HasSupport);

        // Late swing (u > lock): teleport the body a tread to the right — the held
        // landing stays. Phase 0.05 wraps into the swing's tail [0.6, 1.1) → u = 0.9.
        body.X += TS;
        planner.Update(Inputs(track, chunks, body, vel, phase: 0.05f));
        var late = planner.Plans[0];
        Assert.Equal(FootPlanState.Swing, late.State);
        Assert.True(late.HasSupport);
        Assert.Equal(mid.Support.Id, late.Support.Id);
    }

    // ── Continuous replanning (runtime §6, workplan chunk 6) ─────────────────

    private const float Rate = 1.25f, Dt = 1f / 60f;

    private static FootPlan Frame(StepPlanner planner, ClipStrideTrack track, ChunkMap chunks,
                                  ref Vector2 body, ref float phase, Vector2 vel)
    {
        planner.Update(Inputs(track, chunks, body, vel, phase, Rate));
        phase = (phase + Rate * Dt) % 1f;
        body += vel * Dt;
        return planner.Plans[0];
    }

    private static void SetSolid(ChunkMap chunks, int gtx, int gty)
    {
        var pos = new Point((int)MathF.Floor(gtx / (float)Chunk.Size), (int)MathF.Floor(gty / (float)Chunk.Size));
        if (!chunks.TryGet(pos, out var chunk)) { chunk = new Chunk { ChunkPos = pos }; chunks[pos] = chunk; }
        chunk.Tiles[gtx - pos.X * Chunk.Size, gty - pos.Y * Chunk.Size].IsSolid = true;
    }

    // A steadily moving body keeps its committed landing (the wish barely moves); a material
    // prediction change replans, and the emitted target bends onto the new path instead of
    // jumping — then arrives at the new landing and plants exactly there.
    [Fact]
    public void Commitment_HoldsUnderSmallDrift_AndReplansContinuously()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float floorTop = 2 * TS;
        var track = Track(); var planner = new StepPlanner();
        var vel = new Vector2(60f, 0f);
        var body = new Vector2(60f, floorTop - 20f);
        float phase = 0.62f;

        var targets = new List<Vector2>();
        FootPlan p = default; Vector2 landing = default;
        for (int f = 0; f < 8; f++)
        {
            p = Frame(planner, track, chunks, ref body, ref phase, vel);
            Assert.Equal(FootPlanState.Swing, p.State);
            Assert.True(p.HasSupport, $"f={f} reject={p.Reject}");
            if (f == 0) landing = p.Landing; else Assert.Equal(landing, p.Landing);   // committed
            Assert.Equal(0, p.Replans);
            targets.Add(p.Target);
        }
        Assert.True(p.SwingU < 0.5f, $"u={p.SwingU}");

        // Material change: the body jumps a tread ahead → the wish moves > PlannerReplanDistance.
        body.X += TS;
        p = Frame(planner, track, chunks, ref body, ref phase, vel);
        targets.Add(p.Target);
        Assert.Equal(StepReplan.Prediction, p.Replan);
        Assert.Equal(1, p.Replans);
        Assert.True(p.HasSupport);
        Assert.True((p.Landing - landing).Length() > AnimSolverConfig.Current.PlannerReplanDistance,
            $"landing did not move: {landing} → {p.Landing}");
        // Continuity: the target's per-frame step across the replan matches the step before it.
        int k = targets.Count - 1;
        Vector2 stepBefore = targets[k - 1] - targets[k - 2], stepAt = targets[k] - targets[k - 1];
        Assert.True((stepAt - stepBefore).Length() < 0.5f,
            $"target jumped at the replan: step {stepBefore} → {stepAt}");
        Vector2 newLanding = p.Landing;

        // The rest of the swing: no further replans, bounded acceleration, arrival at the landing.
        float maxAcc = 0f;
        while (true)
        {
            p = Frame(planner, track, chunks, ref body, ref phase, vel);
            if (p.State != FootPlanState.Swing) break;
            Assert.Equal(newLanding, p.Landing);
            Assert.Equal(1, p.Replans);
            targets.Add(p.Target);
            int n = targets.Count - 1;
            maxAcc = MathF.Max(maxAcc, (targets[n] - 2f * targets[n - 1] + targets[n - 2]).Length());
        }
        Assert.True(maxAcc < 1.5f, $"swing target acceleration {maxAcc} px/frame²");
        Assert.True((targets[^1] - newLanding).Length() < 4f, $"last swing target {targets[^1]} vs landing {newLanding}");
        Assert.Equal(FootPlanState.Stance, p.State);
        Assert.Equal(newLanding, p.Target);
    }

    // Support lost mid-swing with nothing else to land on: the plan degrades to the clip's
    // motion, but the emitted target blends into it rather than snapping.
    [Fact]
    public void SupportLoss_MidSwing_BlendsIntoTheFallback()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float floorTop = 2 * TS;
        var track = Track(); var planner = new StepPlanner();
        var vel = new Vector2(60f, 0f);
        var body = new Vector2(60f, floorTop - 20f);
        float phase = 0.62f;
        var targets = new List<Vector2>();
        FootPlan p = default;
        for (int f = 0; f < 8; f++) { p = Frame(planner, track, chunks, ref body, ref phase, vel); targets.Add(p.Target); }
        Assert.True(p.HasSupport);

        for (int gtx = 0; gtx < 20; gtx++) chunks.BreakCell(gtx, 2);   // the whole floor goes
        p = Frame(planner, track, chunks, ref body, ref phase, vel);
        targets.Add(p.Target);
        Assert.Equal(FootPlanState.Unplanned, p.State);
        Assert.False(p.HasSupport);
        Assert.Equal(StepReplan.InvalidSupport, p.Replan);
        Assert.Equal(StepReject.NoSupport, p.Reject);
        int k = targets.Count - 1;
        Vector2 stepBefore = targets[k - 1] - targets[k - 2], stepAt = targets[k] - targets[k - 1];
        Assert.True((stepAt - stepBefore).Length() < 0.5f, $"target jumped into the fallback: {stepBefore} → {stepAt}");

        // Quiet afterwards (no re-acquire without terrain), converging onto the authored path.
        Vector2 authoredEnd = default;
        while (track.Feet[0].SwingAt(phase, out _) >= 0)
        {
            authoredEnd = body + 2f * track.Feet[0].Stances[0].TdOffset;   // the clip's own landing at this body
            p = Frame(planner, track, chunks, ref body, ref phase, vel);
            Assert.Equal(StepReplan.None, p.Replan);
            Assert.Equal(1, p.Replans);
            Assert.False(p.HasSupport);
            targets.Add(p.Target);
        }
        // At the swing's end the blend has faded: the target sits on the clip's own landing offset.
        Assert.True((targets[^1] - authoredEnd).Length() < 4f, $"fallback did not converge: {targets[^1]} vs {authoredEnd}");
    }

    // A wall raised across the committed path forces an obstruction replan; the replacement
    // landing is on the near side, judged by the clearance of the remaining path.
    [Fact]
    public void Obstruction_ReplansToAClearLanding()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float floorTop = 3 * TS;
        var track = Track(); var planner = new StepPlanner();
        var vel = new Vector2(60f, 0f);
        var body = new Vector2(60f, floorTop - 20f);
        float phase = 0.62f;
        FootPlan p = default;
        for (int f = 0; f < 6; f++) p = Frame(planner, track, chunks, ref body, ref phase, vel);
        Assert.True(p.HasSupport);
        Vector2 landing = p.Landing;
        int wallX = (int)MathF.Floor(landing.X / TS) - 1;
        Assert.True(wallX * TS > p.Target.X, "the wall must stand between the foot and its landing");
        SetSolid(chunks, wallX, 2); SetSolid(chunks, wallX, 1);

        p = Frame(planner, track, chunks, ref body, ref phase, vel);
        Assert.Equal(StepReplan.Obstruction, p.Replan);
        Assert.True(p.HasSupport, $"no clear landing found: {p.Reject}");
        Assert.True(p.Landing.X <= wallX * TS, $"replacement landing {p.Landing} is beyond the wall at x={wallX * TS}");
        Assert.Equal(floorTop, p.Landing.Y, 3);
    }

    // ── Swing shapes (workplan chunk 7) ──────────────────────────────────────

    // A foot planted at the base of a one-tile riser: the plain chord to the upper tread runs
    // straight into the riser, so the planner commits a climb-first shape whose toe path clears.
    [Fact]
    public void RiserAtTheTakeoff_CommitsAClimbFirstShape()
    {
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float lowerTop = 3 * TS, upperTop = 2 * TS, riserX = 8 * TS;
        var track = Track(); var planner = new StepPlanner();
        var vel = new Vector2(60f, 0f);
        // Body placed so the stance wish (body + 8 px) clamps to the lower tread's edge at the riser.
        var body = new Vector2(riserX - 6f, lowerTop - 20f);
        float phase = 0.2f;
        var p = Frame(planner, track, chunks, ref body, ref phase, vel);
        Assert.Equal(FootPlanState.Stance, p.State);
        Assert.Equal(riserX, p.Target.X, 2);
        Assert.Equal(lowerTop, p.Target.Y, 2);
        // Into the swing: the body keeps moving; the wish is on the upper tread.
        phase = 0.61f;
        FootPlan sw = default; int frames = 0;
        var targets = new List<Vector2>();
        while (track.Feet[0].SwingAt(phase, out _) >= 0 && frames++ < 60)
        {
            sw = Frame(planner, track, chunks, ref body, ref phase, vel);
            output.WriteLine($"u={sw.SwingU:0.00} {sw.State} shape={sw.Shape} land=({sw.Landing.X:0.0},{sw.Landing.Y:0.0}) replan={sw.Replan} rej={sw.Reject} tgt=({sw.Target.X:0.0},{sw.Target.Y:0.0})");
            if (sw.State == FootPlanState.Swing && sw.SwingU >= 0.125f) targets.Add(sw.Target);
            if (sw.SwingU > 0.5f) break;
        }
        Assert.True(sw.HasSupport, $"no landing on the step (reject {sw.Reject})");
        Assert.Equal(upperTop, sw.Landing.Y, 2);
        Assert.True(sw.Shape > 0, "the plain chord runs into the riser; a lifted shape was expected");
        // Past the takeoff, the emitted target clears the riser under the planner's own probe
        // rule (a toe 3 px above the path is in open space).
        foreach (var t in targets)
            Assert.False(chunks.GetCellState((int)MathF.Floor(t.X / TS), (int)MathF.Floor((t.Y - 3f) / TS)) == TileState.Solid,
                $"swing target {t} inside the riser");
    }
}
