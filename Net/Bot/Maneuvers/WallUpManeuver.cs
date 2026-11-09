using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Defensive wall: throw up a stone column between self and the target, just in front of
// the bot, tall enough that walking into it stops a player cold.
//
// When it pays off (Score):
//   * the target is charging a ranged attack (lobbed area) — the column eats the
//     shot, and stone survives it where dirt would not;
//   * the bot is low on HP and the target is closing — buys time for Health regen and
//     for cooldowns to come back;
//   * a crystal wave (todo.txt #7) is coming: visible crystals of an opposing side on
//     the floor line ahead — the first seeds of a wave sit there for ~1 s before the
//     front runs. A crystal can only seed into open space and a tile claimed for real
//     terrain evicts a pending crystal, so a column raised between us and the seeds cuts
//     the wave. Scored above everything else the wall answers (1.4);
//   * otherwise, mildly, any target walking straight at the bot from mid range: a wall
//     breaks its approach and forces a jump/dig the bot can punish. Scored low so any
//     real attack outranks it.
//
// Input plan (OwnsLocomotion; the bot stands still while placing — moving would drag its
// reach and the column's footing around mid-build):
//   1. Pick the column: the first cell column past the bot's own body edge toward the
//      target, provided it is (a) outside the target's body box predicted a few frames
//      ahead, (b) on ground at the bot's floor level, (c) not already a wall.
//   2. Shift+RMB with Num1 held, cursor hopping up the column one cell per frame from the
//      ground (each cell is supported by the ghost below it).
//   3. Restore the picker key, done. The default chase takes over afterwards; it walks
//      into the wall, stalls and jumps it, which is also how the bot re-engages.
//
// Aborts (return false, picker restored): the column stops making progress, the meter
// runs dry (BarrierLayer.Stalled), the target body arrives in the column before it is
// requested (a sprout grown into a body is crushed — spending there is waste), or a
// hard frame ceiling.
public sealed class WallUpManeuver : BotManeuver
{
    // Only answer crystal waves — skip the ranged-charge / low-HP / approach walls. For
    // bots whose style is not walling (the duelists' wave instinct). Default off.
    public bool  CrystalOnly;
    public float MinRange   = 2.5f * Chunk.TileSize;   // closer than this there's no room for a column
    public float MaxRange   = 9f * Chunk.TileSize;
    public float ApproachSpeed = 30f;                  // perceived closing speed (px/s) that counts as "coming at me"
    public int   Height     = 5;                       // tiles; 55 px > a standing body + its stepping reach
    public float LeadSeconds = 0.25f;                  // how far ahead the target's box is predicted (reaction lag + build time)
    public int   MaxFrames  = 45;
    public override int Cooldown => 180;
    public override bool OwnsLocomotion => true;

    private static readonly HashSet<string> RangedCharges = new() { "LobbedAreaAction" };

    private readonly BarrierLayer _layer = new();
    private int      _frames;
    private int      _column;
    private TileType _origType;
    private bool     _origSpikes;
    private bool     _done;
    private bool     _crystal;   // this wall answers a crystal wave (test visibility below)
    public  bool     LastWasCrystalThreat => _crystal;

    // Crystal threat window: opposing crystals this far ahead (px) on our floor line.
    public float CrystalThreatRange    = 24f * Chunk.TileSize;
    public float CrystalThreatVertical = 4f * Chunk.TileSize;
    public float CrystalUtility        = 1.4f;

    // Nearest visible opposing crystal on our floor line (x of its near edge, and the
    // side it is on). Reads sim state the player can see — seeded crystals, not the
    // invisible pending requests.
    internal static bool CrystalThreat(BotView v, float range, float vertical, out int dir, out float nearX)
    {
        dir = 0; nearX = 0f;
        var field = v.Sim.Chunks.Crystals;
        if (field.Count == 0) return false;
        int now = v.Sim.Chunks.TerrainFrame;
        float best = float.MaxValue;
        const float T = Chunk.TileSize;
        foreach (var (cell, c) in field.Entries)
        {
            if (c.Pending || c.Broken || c.PhaseAt(now) == CrystalPhase.Gone) continue;
            if (!CrystalContactSystem.Opposes(in c.Side, v.Self.Faction, v.Self.Team, coop: v.Sim.Coop)) continue;
            float cx = cell.gtx * T + 0.5f * T, cy = cell.gty * T + 0.5f * T;
            float dx = cx - v.Me.X;
            if (MathF.Abs(dx) > range || MathF.Abs(cy - v.Me.Y) > vertical) continue;
            // Deterministic tie-break on the cell (dictionary order must not matter).
            float key = MathF.Abs(dx) + cell.gty * 1e-4f + cell.gtx * 1e-6f;
            if (key >= best) continue;
            best = key;
            dir = dx >= 0f ? 1 : -1;
            nearX = dir > 0 ? cell.gtx * T : (cell.gtx + 1) * T;
        }
        return dir != 0;
    }

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        if (CrystalThreat(v, CrystalThreatRange, CrystalThreatVertical, out int cdir, out float cx))
        {
            var cm = v.Self.Abilities.Meters;
            if (cm.BuildMove + cm.EruptMove >= Height * MaterialStrengths.BuildCostFor(TileType.Stone)
                && TryPickColumn(v, cdir, cx, out _, out _))
                return CrystalUtility;
        }
        if (CrystalOnly) return 0f;
        float gap = MathF.Abs(v.Dx);
        if (gap < MinRange || gap > MaxRange || MathF.Abs(v.Dy) > 2f * Chunk.TileSize) return 0f;

        var m = v.Self.Abilities.Meters;
        if (m.BuildMove + m.EruptMove < Height * MaterialStrengths.BuildCostFor(TileType.Stone)) return 0f;

        // Closing speed toward us (positive = approaching).
        float closing = -v.TargetVel.X * v.Toward;
        bool charging = RangedCharges.Contains(v.TargetAction);
        bool lowHp    = v.Self.Health < 0.5f * v.Self.MaxHealth;
        if (!charging && closing < ApproachSpeed) return 0f;

        if (!TryPickColumn(v, v.Toward, v.TargetPos.X, out _, out _)) return 0f;

        if (charging) return 1.3f;
        if (lowHp)    return 1.2f;
        return 0.4f;
    }

    // Bet (M4): a 5-tile column deals nothing; it is avoided taken. Build ~45 f + the threat's arrival.
    //   crystal wave: cuts up to 1.5 HP ~45% (-0.65); a charging shot ~1 HP ~60% (-0.58);
    //   low HP and closing: buys regen (-0.54); a plain approach: (-0.15).
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        if (s >= 1.35f) return new Bet(0f, -0.65f, 60);
        if (s >= 1.25f) return new Bet(0f, -0.58f, 60);
        if (s >= 1.15f) return new Bet(0f, -0.54f, 60);
        return new Bet(0f, -0.15f, 60);
    }

    // The wall column and its ground row. Starts at the first column clear of our own
    // body on side `dir` and walks outward a couple of cells until one fits; `beyondX`
    // (the target, or a crystal wave's nearest edge) must stay on the far side of it.
    private bool TryPickColumn(BotView v, int dir, float beyondX, out int column, out int ground)
    {
        column = 0; ground = -1;
        var me  = BarrierPlanner.BodyBox(v, v.Me, 1f);
        if (me.groundRow < 0) return false;
        var lead = v.TargetPos + new Vector2(v.TargetVel.X * LeadSeconds, 0f);
        var them = BarrierPlanner.BodyBox(v, lead, 2f);
        // Also the CURRENT perceived box: lead prediction must not skip over a body that
        // is already standing in the candidate column.
        var now  = BarrierPlanner.BodyBox(v, v.TargetPos, 2f);
        int first = dir > 0 ? me.maxX + 1 : me.minX - 1;
        for (int i = 0; i < 3; i++)
        {
            int gx = first + dir * i;
            if (InBox(gx, them.minX, them.maxX) || InBox(gx, now.minX, now.maxX)) return false;
            // Between us: the target must still be beyond this column.
            if ((beyondX - BotView.CellCenter(new Point(gx, 0)).X) * dir <= 0f) return false;
            int g = BarrierPlanner.GroundRow(v, gx, me.groundRow - 1, 3);
            if (g < 0) continue;                          // pit: nothing to build on here
            if (v.Solid(gx, g - 2)) return false;         // there's already a wall/step in the way
            column = gx; ground = g;
            return true;
        }
        return false;
    }

    private static bool InBox(int x, int lo, int hi) => x >= lo && x <= hi;

    public override void Begin(BotView v)
    {
        _frames     = 0;
        _done       = false;
        _origType   = v.Self.ActiveBlockType;
        _origSpikes = v.Self.SpikesSelected;
        _layer.Reset(spikes: false, TileType.Stone);
        _layer.StallFrames = 15;
        int ground = -1;
        _crystal = CrystalThreat(v, CrystalThreatRange, CrystalThreatVertical, out int cdir, out float cx)
                && TryPickColumn(v, cdir, cx, out _column, out ground);
        bool picked = _crystal || TryPickColumn(v, v.Toward, v.TargetPos.X, out _column, out ground);
        if (picked)
            BarrierPlanner.AddColumn(v, _layer.Cells, _column, ground, ground - Height, depth: 0);
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _frames++;
        if (_done || _frames > MaxFrames || _layer.Cells.Count == 0) return Restore(ref input);

        // The target walked into the column before it went up: whatever is still
        // unrequested would grow into its body. Keep what stands, stop paying.
        var them = BarrierPlanner.BodyBox(v, v.TargetPos, 0f);
        if (_column >= them.minX && _column <= them.maxX) return Restore(ref input);

        var s = _layer.Step(v, ref input);
        if (s != BarrierLayer.Status.Placing) return Restore(ref input);
        return true;
    }

    // RMB up, original picker key: see EncloseManeuver.Restore.
    // Hit mid-build: the brain drops us, but the picker must still be put back.
    public override void Abort(BotView v, ref PlayerInput input) => Restore(ref input);

    private bool Restore(ref PlayerInput input)
    {
        _done = true;
        input.RightClick = false;
        input.Shift      = false;
        if (_origSpikes) input.Num5 = true;
        else             BarrierPlanner.PressPicker(ref input, _origType);
        return false;
    }
}
