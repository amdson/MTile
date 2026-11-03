using Microsoft.Xna.Framework;

namespace MTile;

// Hit-physics test target (stage "hover_targets"). A weightless ball that floats where
// it was placed until something hits it. The first hit switches gravity on, so it flies
// a real ballistic arc off whatever the hit gave it, and RespawnSeconds after that hit
// it despawns. The stage's ticker puts a fresh one back at the slot
// (Stages.PopulateHoverTargets) — so each target is a single clean read of one hit's
// knockback, and follow-up hits during the flight don't extend it.
//
// Deliberately undamped: no drag, so the launch is exactly the resolver's Δv. Health
// is large so it's the timer, not damage, that ends the rally. Terrain impacts use the
// player's impact profile (bounce, chip, break-through) so a target lands the way a
// launched player would — without one, physics' bounce gate never opens.
public class HoverTarget : Entity
{
    public const float RespawnSeconds = 3f;
    private const float StartHealth   = 1000f;

    // Frames left until despawn; -1 = untouched, still waiting for its first hit.
    private int _despawnFrames = -1;

    public override EntityKind Kind => EntityKind.HoverTarget;

    public HoverTarget(Vector2 pos, float radius, float mass, Color color)
        : this(new PhysicsBody(Polygon.CreateRegular(radius, 10), pos)
               { Impact = ImpactProfiles.Build(ImpactProfiles.Player) }, radius)
    {
        Mass  = mass;
        Color = color;
    }

    // Rehydrate path: Mass/Color/Health come back through RestoreState.
    private HoverTarget(PhysicsBody body, float radius) : base(body, StartHealth)
    {
        GravityScale = 0f;
        Faction      = Faction.Neutral;
        Sprite       = Sprites.Ball(radius);
    }

    public static HoverTarget FromState(in EntityData d, Vector2 pos)
    {
        var body = new PhysicsBody(d.Polygon, pos) { Impact = d.Impact };
        return new HoverTarget(body, body.Bounds.Width * 0.5f);
    }

    public bool Hit => _despawnFrames >= 0;

    public override Vector2 OnHit(in Hitbox hit, in Hurtbox hurt)
    {
        if (_despawnFrames < 0)
        {
            _despawnFrames = SimFrames.FromSeconds(RespawnSeconds, Simulation.FixedDt);
            GravityScale   = 1f;   // snapshotted via EntityData like any entity's
        }
        return base.OnHit(in hit, in hurt);
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (_despawnFrames < 0 || IsDead) return;
        if (--_despawnFrames <= 0) Health = 0f;
    }

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.AIState = _despawnFrames;   // reuse the AI slot — no AI here
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _despawnFrames = s.AIState;
    }
}
