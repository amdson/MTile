using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The eruption payload: a ball of build mass coasting through the world, leaking into
// TileMassField as it goes. Spawned by BlockPaintAction's eruption upgrade on RMB release,
// carrying the ball's velocity at that instant — which is what makes the eruption
// continue past the cursor instead of stopping where you let go.
//
// This replaces the old MassBallPlanner, which faked exactly this: a spring chasing a
// "puller" that walked a *recorded* sample path and extrapolated off the end, all
// resolved inside a single frame at Exit. A live entity gets the inertia for free, is
// its own preview (you watch it fly), and needs no gesture history — which is what
// retired BlockEruptionAction's PathSample list and the EruptionGestureState deep-copy.
//
// Two deliberate non-behaviours:
//   • No terrain collision (Body.IgnoreTiles). It deposits as it flies, so a colliding
//     ball would wall itself in within a few frames of release. Instead it does its own,
//     provenance-aware wall test (todo #4, "walls block avalanches"): it flies through
//     cells ITS OWN wave requested (ChunkMap.Waves tags them at request time — a
//     finished tile keeps no provenance) but stops on entering any other occupied cell
//     — pre-existing terrain, anyone's barricade, a rival wave — and dumps its remaining
//     mass on the near side of that face. Arming: a release usually leaves the lagging
//     paint ball still inside the ground it was charged in, so the test only switches
//     on once the ball has been in a non-foreign cell.
//   • No gravity (GravityScale 0). An upward sweep should coast upward; arcing the ball
//     down would fight the gesture the player just made. Drag is what stops it.
public class MassBall : Projectile
{
    // Backstop only — mass depletion is the real end condition.
    private const float LifeSeconds = 2.5f;
    // Leak per second: proportional to what's left, plus a floor so the tail doesn't
    // dribble forever. Matches the old planner's per-step curve (mass·0.1 + 1 at 60Hz),
    // so a full 240 budget still spends itself over roughly a second.
    private const float LeakFraction = 6.0f;
    private const float LeakFloor    = 60.0f;
    // Velocity decay per second. The ball coasts a readable distance and settles rather
    // than crossing the map.
    private const float Drag         = 0.3f;
    private const float DoneMass     = 0.01f;

    private float    _mass;
    private bool     _armed;
    private readonly TileType _tileType;

    public override EntityKind Kind => EntityKind.MassBall;

    // Build mass left to leak, in tile-equivalents. Read-only view for tests and
    // feedback — a supercharged eruption (charged blocks recruited at launch,
    // BlockEruptionHelpers.RecruitChargedBlocks) is simply one of these carrying a
    // number well past what a full meter alone could buy.
    public float BuildMass => _mass;

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.BuildMass = _mass;
        s.BallArmed = _armed;
        s.TileType  = _tileType;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _mass  = s.BuildMass;
        _armed = s.BallArmed;
    }

    // True once the ball has stopped against a face (it dies the same frame).
    public bool StoppedAtWall { get; private set; }

    public MassBall(Vector2 pos, Vector2 velocity, float mass, TileType tileType, Faction owner)
        : base(new PhysicsBody(Polygon.CreateRegular(4f, 6), pos), health: 0.1f, lifetime: LifeSeconds, owner: owner)
    {
        Body.Velocity    = velocity;
        Body.IgnoreTiles = true;
        Mass             = 0.5f;
        GravityScale     = 0f;
        Color            = new Color(255, 170, 60);
        Sprite           = Sprites.Ball(4f);
        _mass            = mass;
        _tileType        = tileType;
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (_mass <= DoneMass) { Health = 0f; return; }

        // Drag, applied to velocity directly — the body carries no gravity and never
        // touches a contact, so there's nothing else acting on it.
        Body.Velocity *= MathF.Max(0f, 1f - Drag * dt);

        var chunks = (spawner as IChunkProvider)?.Chunks;
        if (chunks == null) return;

        int gtx = (int)MathF.Floor(Body.Position.X / Chunk.TileSize);
        int gty = (int)MathF.Floor(Body.Position.Y / Chunk.TileSize);
        // Wave provenance: the ball IS the wave. Direction locks on the first
        // touch (drag is scalar and there's no gravity, so it never changes);
        // the tag rides the mass through spill → request → the schedule gate.
        chunks.TouchWave(Id, Body.Velocity);
        byte owner = BlockOwnership.OwnerFor(Faction);

        // Walls block avalanches. Checked BEFORE this frame's deposit, along the segment
        // the physics sweep is about to carry the ball over (entities update before
        // StepSwept), so a fast ball can't tunnel a 1-thick barricade between frames.
        if (!_armed)
            _armed = !chunks.IsForeignToWave(Id, gtx, gty);
        if (_armed && FindStop(chunks, gtx, gty, out int nx, out int ny))
        {
            // Dump everything left against the face, on the side the ball came from.
            // The spill can't enter the face (TileMassField refuses foreign solid for
            // wave mass), so it banks up the near side instead of leaking through.
            float dump = _mass;
            _mass = 0f;
            chunks.Mass.Deposit(chunks, nx, ny, dump, _tileType, Id, owner);
            Body.Position = BlockEruptionHelpers.CellCenter(nx, ny);
            Body.Velocity = Vector2.Zero;
            StoppedAtWall = true;
            Health = 0f;
            return;
        }

        float leak = MathF.Min(_mass, (_mass * LeakFraction + LeakFloor) * dt);
        _mass -= leak;
        // The eruption builds for whoever launched it (Faction rides the snapshot).
        chunks.Mass.Deposit(chunks, gtx, gty, leak, _tileType, Id, owner);
    }

    // Walk the cells from the ball's current cell along this frame's travel (Amanatides–
    // Woo grid traversal). The first foreign cell is the face; (nx, ny) is the last cell
    // before it. If the ball's OWN cell turned foreign (someone built on it), the near
    // cell is one step back along the dominant axis of travel.
    private bool FindStop(ChunkMap chunks, int gtx, int gty, out int nx, out int ny)
    {
        nx = gtx; ny = gty;
        Vector2 v = Body.Velocity;
        if (chunks.IsForeignToWave(Id, gtx, gty))
        {
            if (MathF.Abs(v.X) >= MathF.Abs(v.Y)) nx -= MathF.Sign(v.X);
            else                                   ny -= MathF.Sign(v.Y);
            return true;
        }

        float ts = Chunk.TileSize;
        Vector2 p0 = Body.Position;
        Vector2 d  = v * Simulation.FixedDt;
        int stepX = MathF.Sign(d.X), stepY = MathF.Sign(d.Y);
        float tMaxX = stepX == 0 ? float.PositiveInfinity
            : ((stepX > 0 ? (gtx + 1) * ts : gtx * ts) - p0.X) / d.X;
        float tMaxY = stepY == 0 ? float.PositiveInfinity
            : ((stepY > 0 ? (gty + 1) * ts : gty * ts) - p0.Y) / d.Y;
        float tDeltaX = stepX == 0 ? float.PositiveInfinity : ts / MathF.Abs(d.X);
        float tDeltaY = stepY == 0 ? float.PositiveInfinity : ts / MathF.Abs(d.Y);
        int cx = gtx, cy = gty;
        // Bounded: one frame of travel crosses at most a handful of cells.
        for (int i = 0; i < 64; i++)
        {
            if (tMaxX > 1f && tMaxY > 1f) return false;
            if (tMaxX < tMaxY) { cx += stepX; tMaxX += tDeltaX; }
            else               { cy += stepY; tMaxY += tDeltaY; }
            if (chunks.IsForeignToWave(Id, cx, cy)) return true;
            nx = cx; ny = cy;
        }
        return false;
    }
}