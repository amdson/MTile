using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Roadmap goal 4 stage 7 — the determinism gate. Drives a full Simulation forward,
// snapshots it mid-run, lets it keep running, then restores the snapshot and re-runs
// the identical inputs. If Snapshot/Restore captures every bit of simulation state
// (players, FSMs, entities incl. spawned-after-snapshot ones, combat dedupe, moving
// platforms, hit-id + id counters), the post-restore replay must reproduce the live
// run frame-for-frame. Any divergence — a missed field, a stale soft-contact ref, a
// mis-rehydrated entity — shows up as a trace mismatch.
public class SnapshotRoundTripTests(ITestOutputHelper output)
{
    // Compact, exact-float signature of everything observable in the sim each frame.
    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        void P(PlayerCharacter p)
        {
            var b = p.Body;
            sb.Append($"P{p.Id}|{Bits(b.Position.X)},{Bits(b.Position.Y)};{Bits(b.Velocity.X)},{Bits(b.Velocity.Y)}|")
              .Append($"{p.CurrentStateName}/{p.CurrentActionName}|hp{Bits(p.Health)}|f{p.Frame}|cons{b.Constraints.Count}\n");
        }
        P(sim.Player);
        foreach (var (sp, _) in sim.SecondaryPlayers) P(sp);
        foreach (var e in sim.Entities)
        {
            var b = e.Body;
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(b.Position.X)},{Bits(b.Position.Y)};{Bits(b.Velocity.X)},{Bits(b.Velocity.Y)}|hp{Bits(e.Health)}\n");
        }
        AppendTerrain(sim.Chunks, sb);
        return sb.ToString();
    }

    // Terrain signature: solid/sprouting cell map over a window, plus live sprout
    // nodes (cell + exact age) and damaged-cell HP. Catches any divergence in the
    // dense grid, the sprout graph, or the per-cell HP store after a restore.
    private static void AppendTerrain(ChunkMap chunks, StringBuilder sb)
    {
        for (int gty = -2; gty <= 6; gty++)
        {
            sb.Append("T");
            for (int gtx = -2; gtx <= 10; gtx++)
                sb.Append((int)chunks.GetCellState(gtx, gty));
            sb.Append('\n');
        }
        foreach (var s in chunks.ActiveSprouts)
            sb.Append($"S{s.Gtx},{s.Gty}:{s.Type}|age{Bits(s.Age)}\n");
        var dmg = new List<string>();
        foreach (var d in chunks.Damage.Damaged) dmg.Add($"{d.Key.gtx},{d.Key.gty}={Bits(d.Value)}");
        dmg.Sort();
        foreach (var d in dmg) sb.Append("D").Append(d).Append('\n');
    }

    // Raw bit pattern of a float so the comparison is exact (no formatting tolerance).
    private static int Bits(float f) => System.BitConverter.SingleToInt32Bits(f);

    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    // crasher: a gravity-bound Ball that falls and chips the floor on impact, so the
    // terrain journal records deltas. Same-instance restores rewind those fine; the
    // cross-sim test passes crasher:false (a floating ball never hits the floor) since
    // a journal mark is instance-relative and can't carry terrain deltas to another sim.
    private static Simulation BuildSim(bool crasher = true)
    {
        // Player on the floor; a ball, a stalker that chases, and a turret that charges
        // and fires a bullet ~frame 36 — so an entity spawns *after* the snapshot frame,
        // exercising the drop-and-recreate path on restore.
        return new Simulation(Floor(), new Vector2(40f, 38f), g =>
        {
            g.SpawnEntity(crasher ? EntityFactory.Ball(new Vector2(90f, -40f))
                                  : EntityFactory.FloatingBall(new Vector2(90f, -40f)));
            g.SpawnEntity(EntityFactory.Stalker(new Vector2(150f, 38f)));
            g.SpawnEntity(EntityFactory.Turret(new Vector2(220f, 38f)));
        });
    }

    // Deterministic per-frame input: walk right the whole time, tap jump periodically.
    private static PlayerInput InputAt(int frame) => new()
    {
        Right = true,
        Space = frame % 20 < 3,   // brief jump press every 20 frames
    };

    [Fact]
    public void Snapshot_Then_Restore_ReproducesRunBitForBit()
    {
        const int K = 30;    // snapshot frame
        const int N = 120;   // total frames

        // ── Live run: step to K, snapshot, continue to N capturing the trace ──
        var live = BuildSim();
        for (int f = 0; f < K; f++) live.Step(InputAt(f));

        var snap = live.Snapshot();

        var liveTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(InputAt(f)); liveTrace.Add(Probe(live)); }

        // ── Restore onto the SAME sim and re-run the identical inputs ──
        live.Restore(snap);
        var replayTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(InputAt(f)); replayTrace.Add(Probe(live)); }

        // ── Compare frame-for-frame ──
        Assert.Equal(liveTrace.Count, replayTrace.Count);
        for (int i = 0; i < liveTrace.Count; i++)
        {
            if (liveTrace[i] != replayTrace[i])
            {
                output.WriteLine($"Divergence at replay frame {K + i}:");
                output.WriteLine("LIVE:\n"   + liveTrace[i]);
                output.WriteLine("REPLAY:\n" + replayTrace[i]);
            }
            Assert.Equal(liveTrace[i], replayTrace[i]);
        }

        output.WriteLine($"Round-trip identical across {liveTrace.Count} frames after restore@{K}.");
    }

    [Fact]
    public void Restore_To_FreshSnapshot_OnSecondSim_Matches()
    {
        // A snapshot taken on one sim restored onto a *separate* freshly-built sim
        // should also reproduce the original — proves the player/entity snapshot
        // carries no hidden dependence on the originating instance's transient object
        // identities. Uses crasher:false so terrain is never journaled (journal marks
        // are instance-relative and don't transfer across sims — that's same-instance
        // rollback's contract, covered by the other two tests).
        const int K = 45;
        const int N = 110;

        var a = BuildSim(crasher: false);
        for (int f = 0; f < K; f++) a.Step(InputAt(f));
        var snap = a.Snapshot();

        var aTrace = new List<string>();
        for (int f = K; f < N; f++) { a.Step(InputAt(f)); aTrace.Add(Probe(a)); }

        // Second sim: advance it to a different state first (so its transient lists
        // differ), then restore the snapshot from sim A and replay.
        var b = BuildSim(crasher: false);
        for (int f = 0; f < 10; f++) b.Step(InputAt(f));
        b.Restore(snap);
        var bTrace = new List<string>();
        for (int f = K; f < N; f++) { b.Step(InputAt(f)); bTrace.Add(Probe(b)); }

        for (int i = 0; i < aTrace.Count; i++)
        {
            if (aTrace[i] != bTrace[i])
            {
                output.WriteLine($"Divergence at replay frame {K + i}:");
                output.WriteLine("A:\n" + aTrace[i]);
                output.WriteLine("B:\n" + bTrace[i]);
            }
            Assert.Equal(aTrace[i], bTrace[i]);
        }
        output.WriteLine($"Cross-sim restore identical across {aTrace.Count} frames.");
    }

    [Fact]
    public void Snapshot_WithTerrainEdits_RoundTrips()
    {
        // Player stands still and drag-builds a row of Foam sprouts on the ground next
        // to it. Sprouts are requested (tile writes), grow, and finalize to Solid
        // (more tile writes) — all journaled — and the foam timers + sprout-graph ages
        // tick continuously. The build window straddles the snapshot frame, so the
        // journal has entries on both sides and the restore must rewind the
        // after-snapshot finalizes, rewind to the mid-grow sprout graph, and replay
        // identically.
        const int K = 18;    // snapshot mid-build (sprouts growing / just finalizing)
        const int N = 80;

        PlayerInput Build(int frame) => new()
        {
            // Drag-build held frames 8..36, cursor sweeping ground-adjacent cells.
            RightClick         = frame >= 8 && frame <= 36,
            MouseWorldPosition = new Vector2(24f + (frame - 8) * 2f, 40f),
            Num4               = frame == 8,   // select Foam once at the start
        };

        var live = BuildSim();
        for (int f = 0; f < K; f++) live.Step(Build(f));

        // Sanity: the build actually mutated terrain by the snapshot frame (otherwise
        // the round-trip would pass vacuously on untouched terrain).
        int solidAtK = CountSolid(live.Chunks);

        var snap = live.Snapshot();
        var liveTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(Build(f)); liveTrace.Add(Probe(live)); }
        int solidAtEnd = CountSolid(live.Chunks);

        live.Restore(snap);
        var replayTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(Build(f)); replayTrace.Add(Probe(live)); }

        Assert.True(solidAtEnd > solidAtK, $"Expected the build to add solid cells (K={solidAtK}, end={solidAtEnd})");
        for (int i = 0; i < liveTrace.Count; i++)
        {
            if (liveTrace[i] != replayTrace[i])
            {
                output.WriteLine($"Divergence at replay frame {K + i}:");
                output.WriteLine("LIVE:\n"   + liveTrace[i]);
                output.WriteLine("REPLAY:\n" + replayTrace[i]);
            }
            Assert.Equal(liveTrace[i], replayTrace[i]);
        }
        output.WriteLine($"Terrain round-trip identical across {liveTrace.Count} frames; solid {solidAtK}→{solidAtEnd}.");
    }

    private static int CountSolid(ChunkMap chunks)
    {
        int n = 0;
        for (int gty = -2; gty <= 6; gty++)
            for (int gtx = -2; gtx <= 10; gtx++)
                if (chunks.GetCellState(gtx, gty) == TileState.Solid) n++;
        return n;
    }
}
