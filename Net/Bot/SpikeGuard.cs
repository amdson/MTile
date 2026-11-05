using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Spike sense for every brain: a last-pass filter over the frame's finished input, so it
// covers every maneuver (an OwnsLocomotion backoff walks onto a carpet as readily as the
// default chase). Without it the duelists stood on opposing spikes and ticked to death.
//
//   * Standing still on an opposing spike: walk off toward the nearer safe floor, and
//     don't walk straight back on (that loop is what the first version did).
//   * Walking onto / across one: hop it if the carpet is short and there is a safe
//     landing; otherwise keep walking (a tick per 0.5 s is a fair price to reach the
//     target) and swing at the spike ahead — spikes are brittle, any hit on the cell
//     knocks the overlay off (ChunkMap.DamageCell).
//   * Falling onto one: steer toward the nearest safe column.
//
// Spikes are terrain — on screen — so reading them live is within the perception rule.
// A seeding cell counts too: it is about to grow. The placer's own spikes never hurt it
// (SpikeContactSystem.Opposes), so they are ignored.
public sealed class SpikeGuard
{
    public bool Enabled = true;        // ablation (tests)
    public int  HopCells = 2;          // widest carpet worth hopping (a short hop clears ~3 cells)
    public int  ClearEvery = 18;       // frames between spike-clearing swings

    // Diagnostics.
    public int Escapes, Hops, Stops, Clears, Steers;

    private int _jumpHold, _holdDir, _lastClear = -1000;
    private bool _walkOnly;
    private int _avoidDir, _avoidUntil = -1;
    private const int ShuffleFrames = 60;
    public float ShuffleTravel = 3f * Chunk.TileSize;    // net px over ShuffleFrames below which a walker is "parked"
    private readonly float[] _xs = new float[ShuffleFrames];
    private int _xi;
    public float ParkedSpeed = 15f;    // px/s: slower than this on spikes counts as standing
    public int   AvoidFrames = 120;    // after hopping off, hold off walking back on this long

    private static float T => Chunk.TileSize;

    public static bool Hostile(BotView v, int gx, int gy)
    {
        var ch = v.Sim.Chunks;
        if (ch.GetCellState(gx, gy) != TileState.Solid) return false;
        if (ch.Spikes.TryGet(gx, gy, out var s))
            return SpikeContactSystem.Opposes(in s, v.Self.Faction, 0, false);
        if (ch.SpikeSeeds.TryGet(gx, gy, out var seed))
            return SpikeContactSystem.Opposes(in seed.Side, v.Self.Faction, 0, false);
        return false;
    }

    // A cell a body can stand on: solid, not hostile, with headroom above.
    private static bool SafeFloor(BotView v, int gx, int gy)
        => v.Solid(gx, gy) && !Hostile(v, gx, gy) && !v.Solid(gx, gy - 1) && !v.Solid(gx, gy - 2);

    public void Apply(BotView v, ref PlayerInput input)
    {
        if (!Enabled) return;
        var b = v.Self.Body.Bounds;
        int fy = (int)MathF.Floor((b.Bottom + SpikeContactSystem.FootReach + 1f) / T);   // floor row
        int x0 = (int)MathF.Floor(b.Left / T), x1 = (int)MathF.Floor(b.Right / T);
        int top = (int)MathF.Floor(b.Top / T), bot = fy - 1;

        if (_jumpHold > 0)
        {
            _jumpHold--;
            input.Space = !_walkOnly;
            if (_holdDir != 0) BotInput.Move(ref input, _holdDir);
            return;
        }

        int walk = input.Right ? 1 : input.Left ? -1 : 0;
        // Shuffling counts as standing: a spacing bot walks back and forth on a carpet
        // without ever crossing it, so also look at net travel over the last ShuffleFrames.
        _xs[_xi++ % ShuffleFrames] = v.Me.X;
        float travel = _xi > ShuffleFrames ? MathF.Abs(v.Me.X - _xs[_xi % ShuffleFrames]) : float.MaxValue;
        bool parked = walk == 0 || MathF.Abs(v.MyVel.X) < ParkedSpeed || travel < ShuffleTravel;

        // 1. On (or pressed against) an opposing spike. Crossing a carpet is a fair trade —
        //    a tick per half second while walking through — so a bot that is walking is
        //    left to walk (and swings the spike ahead off). Standing still on one is what
        //    kills: hop off toward the nearer safe floor, and don't walk straight back on.
        bool touching = Touching(v, b);
        if (touching && parked)
        {
            // Walk off, don't jump: contact damage ramps with approach speed
            // (SpikeContactSystem: 0.25 at a walk, up to 1.5 at 400 px/s), so landing back
            // on a carpet from a hop costs far more than the walking ticks.
            // Hop only when the hop lands clear (safe floor within HopCells); otherwise walk.
            int dir = SafeSide(v, x0, x1, fy, out int dist);
            if (dist <= HopCells) Jump(ref input, dir, 10); else Walk(ref input, dir, 12);
            if (dir != 0) { _avoidDir = -dir; _avoidUntil = v.Frame + AvoidFrames; }
            Escapes++;
            return;
        }

        // 2. Walking onto one (or across one).
        if (v.Grounded && walk != 0)
        {
            int lead = walk > 0 ? x1 : x0;
            int next = lead + walk;
            bool wall = false;
            for (int y = top; y <= bot && !wall; y++) wall = Hostile(v, next, y);
            if (Hostile(v, next, fy) || wall)
            {
                int w = 0;
                while (w <= HopCells && Hostile(v, next + walk * w, fy)) w++;
                bool landing = !wall && w <= HopCells && SafeFloor(v, next + walk * w, fy);
                if (landing && !touching) { Jump(ref input, walk, 14); Hops++; return; }

                // Don't step onto it when retreating (spacing backs a bot onto a carpet
                // edge again and again, a fresh tick each time), or straight back onto a
                // carpet just hopped off. Walking at the target across it is allowed.
                bool retreating = walk != v.Toward;
                if (!touching && (retreating || (walk == _avoidDir && v.Frame < _avoidUntil)))
                {
                    input.Left = input.Right = false;
                    Stops++;
                }
                // Swing the spike ahead off (brittle: any hit on the cell takes it).
                if (v.Frame - _lastClear >= ClearEvery && !input.LeftClick && !v.Previous.LeftClick && !input.RightClick)
                {
                    int cy = wall ? Math.Clamp((int)MathF.Floor(v.Me.Y / T), top, bot) : fy;
                    input.MouseWorldPosition = BotView.CellCenter(new Point(next, cy));
                    input.LeftClick = true;
                    _lastClear = v.Frame;
                    Clears++;
                }
                return;
            }
        }

        // 3. Falling onto one with no direction of our own: steer for the nearest safe
        //    column under the arc (a bot already walking somewhere keeps its line).
        if (!v.Grounded && v.MyVel.Y > 0f && walk == 0)
        {
            int cx = (int)MathF.Floor(v.Me.X / T);
            for (int y = fy; y < fy + 8; y++)
            {
                if (!v.Solid(cx, y)) continue;
                if (Hostile(v, cx, y))
                {
                    int dir = SafeSide(v, cx, cx, y);
                    if (dir != 0) { BotInput.Move(ref input, dir); Steers++; }
                }
                break;
            }
        }
    }

    // SpikeContactSystem's contact test, approximated from outside: the body AABB grown by
    // ContactSlop, plus FootReach below it while grounded (the stand fold hovers the torso
    // ~15 px over the floor, so the floor can be one or two rows under Bounds.Bottom).
    private static bool Touching(BotView v, BoundingBox b)
    {
        float slop = SpikeContactSystem.ContactSlop;
        float bottom = b.Bottom + (v.Grounded ? SpikeContactSystem.FootReach : 0f) + slop;
        int gx0 = (int)MathF.Floor((b.Left - slop) / T), gx1 = (int)MathF.Floor((b.Right + slop) / T);
        int gy0 = (int)MathF.Floor((b.Top - slop) / T),  gy1 = (int)MathF.Floor(bottom / T);
        for (int gy = gy0; gy <= gy1; gy++)
        for (int gx = gx0; gx <= gx1; gx++)
            if (Hostile(v, gx, gy) && Exposed(v, gx, gy)) return true;
        return false;
    }

    // A face of the cell borders open air (a buried spiked cell can't be touched).
    private static bool Exposed(BotView v, int gx, int gy)
        => !v.Solid(gx, gy - 1) || !v.Solid(gx - 1, gy) || !v.Solid(gx + 1, gy) || !v.Solid(gx, gy + 1);

    private void Walk(ref PlayerInput input, int dir, int hold)
    {
        if (dir == 0) return;
        input.Space = false;
        BotInput.Move(ref input, dir);
        _jumpHold = hold; _holdDir = dir; _walkOnly = true;
    }

    private void Jump(ref PlayerInput input, int dir, int hold)
    {
        input.Space = true;
        if (dir != 0) BotInput.Move(ref input, dir);
        _jumpHold = hold; _holdDir = dir; _walkOnly = false;
    }

    // Direction of the nearer safe floor cell on row `fy`, scanning out from [x0, x1].
    private static int SafeSide(BotView v, int x0, int x1, int fy) => SafeSide(v, x0, x1, fy, out _);

    private static int SafeSide(BotView v, int x0, int x1, int fy, out int dist)
    {
        dist = int.MaxValue;
        for (int k = 1; k <= 8; k++)
        {
            bool l = SafeFloor(v, x0 - k, fy) || SafeFloor(v, x0 - k, fy - 1);
            bool r = SafeFloor(v, x1 + k, fy) || SafeFloor(v, x1 + k, fy - 1);
            if (l || r) dist = k;
            if (l && r) return -v.Toward;          // both: away from the target's side
            if (l) return -1;
            if (r) return 1;
        }
        return 0;
    }
}

// SpikeGuard as a BotController constraint (Plans/BOT_BEHAVIOR_ARCHITECTURE.md): the same
// last-pass filter, run by the controller after every behaviour has written its input.
public sealed class SpikeGuardConstraint : Constraint
{
    public readonly SpikeGuard Guard;
    public SpikeGuardConstraint(SpikeGuard guard) { Guard = guard ?? throw new ArgumentNullException(nameof(guard)); }

    public override string Name => nameof(SpikeGuard);

    public override bool Mask(BotView v, ref PlayerInput input)
    {
        var before = input;
        Guard.Apply(v, ref input);
        return before.Left != input.Left || before.Right != input.Right || before.Space != input.Space
            || before.LeftClick != input.LeftClick || before.RightClick != input.RightClick
            || before.MouseWorldPosition != input.MouseWorldPosition;
    }
}
