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
//     ball would wall itself in within a few frames of release.
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
        s.TileType  = _tileType;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _mass = s.BuildMass;
    }

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

        float leak = MathF.Min(_mass, (_mass * LeakFraction + LeakFloor) * dt);
        _mass -= leak;

        int gtx = (int)MathF.Floor(Body.Position.X / Chunk.TileSize);
        int gty = (int)MathF.Floor(Body.Position.Y / Chunk.TileSize);
        // Wave provenance: the ball IS the wave. Direction locks on the first
        // touch (drag is scalar and there's no gravity, so it never changes);
        // the tag rides the mass through spill → request → the schedule gate.
        chunks.TouchWave(Id, Body.Velocity);
        chunks.Mass.Deposit(chunks, gtx, gty, leak, _tileType, Id);
    }
}
