using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Juggling-practice target. A gravity-bound ball that shatters the moment it
// touches any solid tile and immediately respawns at its spawn point — so the
// drill is "keep it airborne with slashes": every floor/wall touch resets the
// rally cleanly instead of leaving a ball rolling around the stage.
//
// Break detection is an explicit tile probe (cells overlapping the slightly
// padded body bounds), not a physics-impulse threshold — a glancing wall graze
// counts as a break even though the solver barely deflects it. Runs in Update
// (before the physics step), so the ball dies at the contact surface rather
// than after a bounce frame.
public class PracticeBall : Entity
{
    private const float Radius     = 6f;
    // Probe pad beyond the body bounds. The swept solver halts bodies a hair off
    // the surface, so a zero pad would let the ball rest a sub-pixel above the
    // floor forever without "touching" it.
    private const float ContactPad = 1.5f;

    // Where the ball (re)appears. Mutable only through ReadState — a mid-flight
    // snapshot rehydrates the ball at its flight position, and the spawn point
    // must survive that round-trip rather than being re-derived from it.
    private Vector2 _spawn;

    public override EntityKind Kind => EntityKind.PracticeBall;

    public PracticeBall(Vector2 spawn)
        : base(new PhysicsBody(Polygon.CreateRegular(Radius, 8), spawn), health: 50f)
    {
        _spawn = spawn;
        // Light — juggle hits should ping it (see HitResolver's mass-ratio
        // regimes once moves go collision-mode; under impulse mode, light mass
        // just means a big Δv per hit). Generous health because slash damage
        // still chips it between breaks; every break refills.
        Mass         = 0.8f;
        GravityScale = 1f;
        Color        = Color.Gold;
        Faction      = Faction.Neutral;
        Sprite       = Sprites.Ball(Radius);
    }

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.Aim = _spawn;   // reuse the AI aim slot — practice balls have no AI
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _spawn = s.Aim;
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        var chunks = spawner?.Chunks;
        if (chunks == null || IsDead) return;
        if (TouchingSolidTile(chunks)) Break();
    }

    // "Break" = instant respawn: back to the spawn point, dead-stopped, health
    // refilled. Deliberately no despawn/spawn cycle — the same entity persists,
    // which keeps EntityId stable (combat dedupe, snapshots) and needs no stage
    // ticker watching for a dead ball.
    private void Break()
    {
        Body.Position = _spawn;
        Body.Velocity = Vector2.Zero;
        Health        = MaxHealth;
    }

    private bool TouchingSolidTile(ChunkMap chunks)
    {
        var b = Body.Bounds;
        int gtx0 = (int)MathF.Floor((b.Left   - ContactPad) / Chunk.TileSize);
        int gtx1 = (int)MathF.Floor((b.Right  + ContactPad) / Chunk.TileSize);
        int gty0 = (int)MathF.Floor((b.Top    - ContactPad) / Chunk.TileSize);
        int gty1 = (int)MathF.Floor((b.Bottom + ContactPad) / Chunk.TileSize);
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
            if (chunks.GetCellState(gtx, gty) == TileState.Solid)
                return true;
        return false;
    }
}
