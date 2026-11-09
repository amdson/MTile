using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Box the target in: a stone wall on each side of its body, a stone lid over its head,
// then spikes on the floor under its feet (and on the lid) so staying put costs HP and
// digging out — stone is 12× a slash's worth of tile HP — costs time.
//
// Only worth it against a target that will hold still for the ~0.5 s the build takes:
// stunned, in hitstun, or simply standing (AFK, charging, camping a corner). A moving
// target is not enclosable at one tile per frame, so the score is ~0 for it and the
// build aborts the moment the target leaves the box it was planned around.
//
// Input plan (OwnsLocomotion — the brain's chase would walk the bot into its own wall):
//   1. Approach to a standoff band (StandoffMin..StandoffMax px) where every planned cell
//      is inside BuildReach; back off if closer (the near wall must not land on us).
//   2. Blocks: Shift+RMB with Num1 (stone) held, cursor hopping one planned cell per frame
//      — each wall bottom-up (support), then the lid from both ends inward (each lid
//      cell is supported by the one before it).
//   3. Spikes: Num5 held, Shift+RMB, cursor on the floor cells under the body, then the lid
//      cells over it. Spikes need SOLID under them, so this pass naturally lags the
//      blocks by SproutLifetime; the layer retries until they finalize.
//      A placed spike only SEEDS (todo.txt #8): it grows into a damaging spike
//      CrystalLifecycle.SpikePlacement.FramesToGrown (~1.25 s) later, on its own.
//   4. Restore: one frame pressing the picker key the bot had before (Num1..Num5), RMB up.
//
// Spikes hurt the target because SpikeContactSystem's FootReach covers a grounded body's
// hover over the floor, and the lid sits within ContactSlop of a standing head when the
// box is tight. The side walls are deliberately NOT spiked: they sit up to a tile from
// the body edge (the box is padded so a wall never grows into the target), outside
// ContactSlop, so a spike there would be paid for and never touched.
public sealed class EncloseManeuver : BotManeuver
{
    public float StandoffMin  = 24f;   // px body-centre gap: closer and the near wall would clip us
    public float StandoffMax  = 40f;   // farther and the far-side lid is out of BuildReach
    public float EngageRange  = 6f * Chunk.TileSize;   // only plan when this close (px, horizontal)
    public float StillSpeed   = 12f;   // perceived px/s under which the target "stands still"
    public float EscapeDist   = 5f;    // perceived target drift (px) from the plan anchor ⇒ abort
    public bool  UseSpikes    = true;
    public int   ApproachFrames = 75;
    public int   MaxFrames    = 300;   // hard ceiling on the whole maneuver
    public override int Cooldown => 300;
    public override bool OwnsLocomotion => true;

    private enum Phase { Approach, Blocks, Spikes, Restore }

    private readonly BarrierLayer _layer = new();
    private readonly List<Point>  _spikeCells = new();
    private Phase   _phase;
    private Vector2 _anchor;           // perceived target position the plan was built for
    private int     _frames;
    private TileType _origType;
    private bool    _origSpikes;

    public override float Score(BotView v)
    {
        if (!v.Grounded || !v.TargetGrounded) return 0f;
        if (MathF.Abs(v.Dx) > EngageRange || MathF.Abs(v.Dy) > Chunk.TileSize) return 0f;

        bool helpless = v.TargetHelpless;
        bool still    = v.TargetVel.LengthSquared() < StillSpeed * StillSpeed;
        if (!helpless && !still) return 0f;

        // Meter for the walls at least (spikes are optional and come out of the refill).
        var m = v.Self.Abilities.Meters;
        if (m.BuildMove + m.EruptMove < 8f * MaterialStrengths.BuildCostFor(TileType.Stone)) return 0f;

        // Already boxed (both flanks solid at head height) ⇒ nothing to gain.
        var b = BarrierPlanner.BodyBox(v, v.TargetPos, 1f);
        if (b.groundRow < 0) return 0f;
        if (v.Solid(b.minX - 1, b.maxY) && v.Solid(b.maxX + 1, b.maxY)) return 0f;

        // A helpless target is the payoff case (beats an in-range slash, which is what
        // would knock it free anyway); a merely still one is a decent idea but loses to
        // the slash when that is on offer.
        return helpless ? 1.3f : 0.9f;
    }

    public override void Begin(BotView v)
    {
        _phase      = Phase.Approach;
        _frames     = 0;
        _anchor     = v.TargetPos;
        _origType   = v.Self.ActiveBlockType;
        _origSpikes = v.Self.SpikesSelected;
        Plan(v);
    }

    // Build the block + spike cell lists around the target as perceived at `_anchor`.
    private void Plan(BotView v)
    {
        _layer.Reset(spikes: false, TileType.Stone);
        _spikeCells.Clear();
        // 1 px side pad: a wall cell never shares a column with any part of the body,
        // even with the body a hair off its resting x. Top pad −0.5: the lid sits flush
        // on a standing head (see BodyBox) — cheaper, no headroom to jump in, and close
        // enough for a spiked lid to be inside SpikeContactSystem.ContactSlop.
        var (minX, maxX, minY, maxY, ground) = BarrierPlanner.BodyBox(v, _anchor, 1f, padTop: -0.5f);
        if (ground < 0) return;
        int lidRow = minY - 1;
        int feetRow = maxY;

        // Near wall first: it's the closer, cheaper reach, and it's the one that also
        // keeps the target from walking out toward us while the far one goes up.
        int near = v.Toward > 0 ? minX - 1 : maxX + 1;
        int far  = v.Toward > 0 ? maxX + 1 : minX - 1;
        BarrierPlanner.AddColumn(v, _layer.Cells, near, feetRow - 1, minY);
        BarrierPlanner.AddColumn(v, _layer.Cells, far,  feetRow - 1, minY);

        // Lid: walls' top cells support the corners, then inward from both ends.
        int lo = minX - 1, hi = maxX + 1;
        while (lo <= hi)
        {
            var a = new Point(lo, lidRow);
            if (!BarrierPlanner.Requested(v, a)) _layer.Cells.Add(a);
            if (hi != lo)
            {
                var z = new Point(hi, lidRow);
                if (!BarrierPlanner.Requested(v, z)) _layer.Cells.Add(z);
            }
            lo++; hi--;
        }

        if (!UseSpikes) return;
        // Floor under the body first (always touched by a grounded target), lid second
        // (touched only if the head is within ContactSlop, i.e. a tight box).
        // Spike tiles sit in the open cell beside the solid: on top of the floor, under the lid.
        for (int x = minX; x <= maxX; x++) _spikeCells.Add(new Point(x, ground - 1));
        for (int x = minX; x <= maxX; x++) _spikeCells.Add(new Point(x, lidRow + 1));
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _frames++;
        if (_phase == Phase.Restore) return Restore(ref input);

        // Target got away from the box we planned (or never stood still): stop spending.
        if (Vector2.Distance(v.TargetPos, _anchor) > EscapeDist || _frames > MaxFrames)
            return Restore(ref input);

        switch (_phase)
        {
            case Phase.Approach:
            {
                float gap = MathF.Abs(v.Dx);
                if (gap > StandoffMax)      BotInput.Move(ref input, v.Toward);
                else if (gap < StandoffMin) BotInput.Move(ref input, -v.Toward);
                else if (MathF.Abs(v.MyVel.X) < 20f) _phase = Phase.Blocks;   // settled: build from here
                if (_frames > ApproachFrames) return Restore(ref input);
                return true;
            }
            case Phase.Blocks:
            {
                var s = _layer.Step(v, ref input);
                if (s == BarrierLayer.Status.OutOfReach)
                {
                    // Slid out of the band (pushback, a slope): re-approach without
                    // abandoning the cells already placed.
                    if (MathF.Abs(v.Dx) > StandoffMin) BotInput.Move(ref input, v.Toward);
                    else return Restore(ref input);
                }
                else if (s == BarrierLayer.Status.Stalled) return Restore(ref input);
                else if (s == BarrierLayer.Status.Done)
                {
                    if (!UseSpikes || _spikeCells.Count == 0) return Restore(ref input);
                    _layer.Reset(spikes: true, TileType.Stone);
                    _layer.Cells.AddRange(_spikeCells);
                    // A spike costs 2 stone, and the walls just emptied the working pool:
                    // the spike pass is mostly waiting on MoveRefill. The target is boxed
                    // in by now, so patience is cheap — the escape check still runs.
                    _layer.StallFrames = 150;
                    _phase = Phase.Spikes;
                }
                return true;
            }
            case Phase.Spikes:
            {
                var s = _layer.Step(v, ref input);
                // Out of reach / stalled / done all end the same way: the box stands
                // either way, the spikes are a bonus.
                if (s != BarrierLayer.Status.Placing) return Restore(ref input);
                return true;
            }
        }
        return true;
    }

    // Hit mid-build: the brain drops us, but the picker must still be put back.
    public override void Abort(BotView v, ref PlayerInput input) => Restore(ref input);

    // One frame with RMB up and the original picker key held, then done. The picker is
    // a persistent per-player setting, so leaving it on spikes would make every later
    // plain-RMB paint by this bot (another maneuver's) silently spike instead.
    private bool Restore(ref PlayerInput input)
    {
        _phase = Phase.Restore;
        input.RightClick = false;
        input.Shift      = false;
        if (_origSpikes) input.Num5 = true;
        else             BarrierPlanner.PressPicker(ref input, _origType);
        return false;
    }
}
