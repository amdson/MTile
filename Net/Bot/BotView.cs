using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// What a bot sees this frame. Rebuilt by the brain at the top of every Poll and handed,
// read-only by convention, to every maneuver's Score/Begin/Step.
//
// Bots live OUTSIDE the sim (see BrawlerBotInputSource): reading any sim state here is
// fine, but nothing on this view may be written back into the sim. The only output a
// bot has is the PlayerInput record it returns.
public sealed class BotView
{
    public Simulation      Sim;
    public PlayerCharacter Self;
    public PlayerCharacter Target;
    public int             Frame;      // the Poll frame number
    public Random          Rng;        // the bot's seeded stream — the only noise source

    public Vector2 Me;                 // self body position (true)
    public Vector2 MyVel;
    public Vector2 TargetPos;          // target position as the bot perceives it (ReactionFrames stale)
    public Vector2 TargetVel;          // perceived target velocity (from the stale samples), px/s
    // The target's action as the bot perceives it — the same ReactionFrames stale as
    // TargetPos, so reactions (guarding a swing) happen at a human delay, not on the
    // swing's first frame.
    public string  TargetAction = "NullAction";

    // Self's own sim frame counter — the clock CombatState stamps (LastHitFrame, guard
    // cooldowns) are in. Not the same number as Frame in general.
    public int   SelfFrame => Self.Frame;
    // Sim frames since self last took a hit (int.MaxValue if never).
    public int   FramesSinceHit => Self.Combat.DamageTaken > 0f ? SelfFrame - Self.Combat.LastHitFrame : int.MaxValue;
    public float TargetHealth => Target.Health;
    public float MyHealth     => Self.Health;

    public float Dx => TargetPos.X - Me.X;
    public float Dy => TargetPos.Y - Me.Y;          // Y-down: negative ⇒ target is above
    public float Dist => Vector2.Distance(TargetPos, Me);
    public int   Toward => Dx >= 0f ? 1 : -1;        // horizontal sign pointing at the target
    public bool  Grounded => Self.IsGrounded;
    public int   Facing => Self.Facing;

    // What the bot emitted last frame — the controller's GetPrevious(1) for edge logic.
    public PlayerInput Previous;

    // Frames since the brain last chose to attack/maneuver; brains and maneuvers can use
    // it to pace themselves.
    public int FramesSinceManeuver;

    // ── Terrain probes ──────────────────────────────────────────────────────────

    public static Point Cell(Vector2 world) => new(
        (int)MathF.Floor(world.X / Chunk.TileSize),
        (int)MathF.Floor(world.Y / Chunk.TileSize));

    public static Vector2 CellCenter(Point c) => new(
        c.X * Chunk.TileSize + Chunk.TileSize * 0.5f,
        c.Y * Chunk.TileSize + Chunk.TileSize * 0.5f);

    public bool Solid(int gtx, int gty) => Sim.Chunks.GetCellState(gtx, gty) == TileState.Solid;
    public bool Solid(Point c)          => Solid(c.X, c.Y);
    public bool Solid(Vector2 world)    => Solid(Cell(world));

    // First solid cell along the segment a→b (sampled every half tile), or null.
    public Vector2? FirstSolidAlong(Vector2 a, Vector2 b)
    {
        float len = Vector2.Distance(a, b);
        int   n   = Math.Max(1, (int)MathF.Ceiling(len / (Chunk.TileSize * 0.5f)));
        for (int i = 0; i <= n; i++)
        {
            var p = Vector2.Lerp(a, b, i / (float)n);
            if (Solid(p)) return p;
        }
        return null;
    }

    // Clear straight line between self and the perceived target.
    public bool LineOfSight() => FirstSolidAlong(Me, TargetPos) == null;
}

// Small helpers for composing a frame's PlayerInput.
public static class BotInput
{
    // Hold exactly one horizontal direction (dir: -1 left, +1 right, 0 neither).
    public static void Move(ref PlayerInput input, int dir)
    {
        input.Left  = dir < 0;
        input.Right = dir > 0;
    }
}
