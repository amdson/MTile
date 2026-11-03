using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Spike moat: overlay spikes on the floor strip between us and the opponent, a couple of
// tiles out, so the approach costs HP. A spiked floor cell ticks ContactDamage (0.25) on
// anyone opposing who stands/walks on it every 0.5 s and pops them up with a short
// hitstun; a body knocked into it (a stab, a pulse) takes impact damage up to 1.5.
//
// Placement is the same raw gesture EncloseManeuver's spike pass uses: Num5 held,
// Shift+RMB, cursor hopping one floor cell per frame (BarrierLayer). A placed spike only
// seeds and grows ~1.25 s later, so the moat goes down while the opponent is still out
// at range — it is laid ahead of the approach, not under their feet.
public sealed class RampartSpikeMoat : BotManeuver
{
    private readonly RampartSense _s;
    public RampartSpikeMoat(RampartSense s) => _s = s;

    public int   NearTiles  = 1;     // first moat column past our body edge
    public int   FarTiles   = 5;     // last (build reach ~5.8 tiles)
    public float MinGap     = 6f * Chunk.TileSize;   // they must still be beyond the moat
    public float MaxGap     = 60f * Chunk.TileSize;
    public int   MinCells   = 1;     // not worth a pass for fewer
    public float ReserveBuild = 60f; // keep this much reservoir for walls/waves
    public float Utility    = 0.85f;
    public int   MaxFrames  = 60;
    public override int Cooldown => 30;
    public override bool OwnsLocomotion => true;

    private readonly BarrierLayer _layer = new();
    private readonly List<Point> _cells = new();
    private TileType _origType;
    private bool _origSpikes;
    private int _frames;

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        float adx = MathF.Abs(v.Dx);
        if (adx < MinGap || adx > MaxGap || MathF.Abs(v.Dy) > 6f * Chunk.TileSize) return 0f;
        var m = v.Self.Abilities.Meters;
        if (m.Build < ReserveBuild) return 0f;
        if (m.BuildMove + m.EruptMove < MaterialStrengths.SpikeBuildCost) return 0f;
        Plan(v, v.Toward);
        if (_cells.Count < MinCells) return 0f;
        return Utility;
    }

    // Floor cells around the fort: toward `dir` NearTiles..FarTiles past our body edge,
    // then BackTiles behind us — unspiked, in reach, clear of the opponent's body box.
    public int BackTiles = 3;
    private void Plan(BotView v, int dir)
    {
        _cells.Clear();
        if (MathF.Abs(v.Me.X - _s.HomeX) > 0.8f * Chunk.TileSize) return;   // only at the fort
        var me = BarrierPlanner.BodyBox(v, v.Me, 0f);
        if (me.groundRow < 0) return;
        var them = BarrierPlanner.BodyBox(v, v.TargetPos, 3f);
        Side(v, dir, NearTiles, FarTiles, me, them);
        Side(v, -dir, 1, BackTiles, me, them);
    }

    private void Side(BotView v, int dir, int k0, int k1,
                      (int minX, int maxX, int minY, int maxY, int groundRow) me,
                      (int minX, int maxX, int minY, int maxY, int groundRow) them)
    {
        int edge = dir > 0 ? me.maxX : me.minX;
        for (int k = k0; k <= k1; k++)
        {
            int gx = edge + dir * k;
            if (gx >= them.minX - 1 && gx <= them.maxX + 1) break;
            int g = BarrierPlanner.GroundRow(v, gx, me.groundRow - 2, 4);
            if (g < 0) continue;
            if (v.Solid(gx, g - 1)) continue;    // covered: not a walking surface
            var c = new Point(gx, g);
            if (v.Sim.Chunks.Spikes.IsSpiked(c.X, c.Y) || v.Sim.Chunks.SpikeSeeds.Has(c.X, c.Y)) continue;
            if (!BarrierPlanner.InReach(v.Me, c, 4f)) continue;
            _cells.Add(c);
        }
    }

    public override void Begin(BotView v)
    {
        _frames = 0;
        _origType = v.Self.ActiveBlockType;
        _origSpikes = v.Self.SpikesSelected;
        Plan(v, v.Toward);
        _layer.Reset(spikes: true, TileType.Stone);
        _layer.Cells.AddRange(_cells);
        _layer.StallFrames = 20;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        if (++_frames > MaxFrames) return Restore(ref input);
        var st = _layer.Step(v, ref input);
        if (st != BarrierLayer.Status.Placing) return Restore(ref input);
        return true;
    }

    public override void Abort(BotView v, ref PlayerInput input) => Restore(ref input);

    private bool Restore(ref PlayerInput input)
    {
        input.RightClick = false;
        input.Shift = false;
        if (_origSpikes) input.Num5 = true;
        else BarrierPlanner.PressPicker(ref input, _origType);
        return false;
    }
}
