using System;
using Microsoft.Xna.Framework;

namespace MTile;

// A fighter's mine (ActionKind.Mine, EnemyMineAction). Dropped onto the floor under the
// fighter and rooted there; it goes off on the first of:
//   * contact — an opposing body's hurtbox overlaps it, once ArmDelay has passed. Read
//     from this frame's hurtboxes, so a dash's untouchable frames (no hurtbox) carry a
//     fighter over it without setting it off;
//   * a hit — anything that damages it (a shot, a swing, another mine's blast), armed or
//     not, so a mine can be set off from range next to whoever is standing on it;
//   * the fuse — Fuse seconds after it was laid.
//
// The blast is a disc of BlastRadius published for one frame as Faction.Neutral hitboxes:
// Neutral so it lands on every side, its owner and its owner's other mines included —
// placing one is a decision, and a row of them chains. Entities only: a blast never digs
// the floor out of an arena. Knockback points away from the centre.
//
// Rollback: everything that varies (tuning, owner, age, the exploded flag) rides
// EntityData; Rooted is set in the ctor and the position never changes, so Rehydrate
// rebuilds the same mine.
public sealed class FighterMine : Projectile, ITelegraphSource
{
    // Body: a small flat disc. Health is a tripwire, not a pool — any damage at all
    // sets the mine off (see Update), so a single 0.1 tick is enough.
    public  const float BodyRadius = 4f;
    private const float Tripwire   = 100f;
    // How far beyond the body an opposing hurtbox may reach and still count as touching.
    private const float TouchSlack = 3f;
    // Outlives its fuse so a mine that somehow never fires is still swept.
    private const float LifetimeSlack = 0.5f;

    private readonly int      _hitId;
    private readonly float    _damage, _radius, _arm, _fuse, _knockback;
    private bool _exploded;

    public EntityId Owner     { get; }
    public float    Radius    => _radius;
    public bool     Armed     => Age >= _arm;
    public float    FuseLeft  => MathF.Max(0f, _fuse - Age);
    public override EntityKind Kind => EntityKind.FighterMine;

    public FighterMine(Vector2 pos, EntityId owner, int team, int hitId,
                       float damage, float radius, float arm, float fuse, float knockback)
        : base(new PhysicsBody(Polygon.CreateRegular(BodyRadius, 6), pos),
               health: Tripwire, lifetime: fuse + LifetimeSlack, owner: Faction.Enemy)
    {
        Owner      = owner;
        Team       = team;
        _hitId     = hitId;
        _damage    = damage;
        _radius    = radius;
        _arm       = arm;
        _fuse      = fuse;
        _knockback = knockback;
        Rooted       = true;
        GravityScale = 0f;
        Mass         = 5f;
        Color        = new Color(150, 120, 60);
        Sprite       = Sprites.Ball(BodyRadius);
    }

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.HitId      = _hitId;
        s.ProjDamage = _damage;
        s.MineRadius = _radius;
        s.MineArm    = _arm;
        s.MineFuse   = _fuse;
        s.LinkedId   = Owner;
        s.Exploded   = _exploded;
        s.Aim        = new Vector2(_knockback, 0f);
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _exploded = s.Exploded;
    }

    // Rehydrate's constructor call: everything comes back from the snapshotted slots.
    public static FighterMine FromState(in EntityData d, Vector2 pos)
        => new(pos, d.LinkedId, d.Team, d.HitId, d.ProjDamage, d.MineRadius, d.MineArm, d.MineFuse, d.Aim.X);

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        // The blast was published last frame; CombatSystem has resolved it.
        if (_exploded) { Health = 0f; return; }

        bool hit     = Health < Tripwire;
        bool fuse    = Age >= _fuse;
        bool touched = Armed && Touched(spawner);
        if (Armed) Color = new Color(230, 70, 40);
        if (!(hit || fuse || touched)) return;

        Explode(hitboxes, spawner);
    }

    // An opposing body's hurtbox overlapping the mine. Players count by their faction's
    // team; entities only if they are fighters (EnemyEntity) on another team — shots,
    // balls and other mines never step on a mine.
    private bool Touched(IEntitySpawner spawner)
    {
        var hurt = spawner?.Hurtboxes;
        if (hurt == null) return false;
        var p = Body.Position;
        float r = BodyRadius + TouchSlack;
        var zone = new BoundingBox(p.X - r, p.Y - r, p.X + r, p.Y + r);
        foreach (var hb in hurt.All)
        {
            var b = hb.Region;
            if (b.Right <= zone.Left || b.Left >= zone.Right || b.Bottom <= zone.Top || b.Top >= zone.Bottom) continue;
            int team;
            if (Factions.IsPlayer(hb.Owner))
                team = hb.Owner == Faction.Player1 ? Teams.Player : Teams.Player2;
            else if (spawner.Resolve(hb.Target) is EnemyEntity e && !e.IsDead)
                team = e.Team;
            else
                continue;
            if (team != Team) return true;
        }
        return false;
    }

    private void Explode(HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        var c = Body.Position;
        if (hitboxes != null)
        {
            // A disc: a centre box plus a ring of eight, overlapping, so a body anywhere
            // within BlastRadius is touched by at least one. Each piece pushes outward
            // from the centre (the centre piece straight up). CombatSystem dedupes by
            // HitId, so a body under two pieces is hit once.
            float inner = _radius * 0.45f;
            Publish(hitboxes, c, inner, new Vector2(0f, -_knockback));
            float ring = _radius * 0.6f, half = _radius * 0.4f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathHelper.TwoPi / 8f;
                var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                Publish(hitboxes, c + dir * ring, half, dir * _knockback);
            }
        }
        spawner?.NotifyChargedBlast(Id, c, _radius);
        _exploded = true;
    }

    private void Publish(HitboxWorld hitboxes, Vector2 centre, float half, Vector2 knockback)
        => hitboxes.Publish(new Hitbox(
               new BoundingBox(centre.X - half, centre.Y - half, centre.X + half, centre.Y + half),
               _hitId, _damage, knockback, Faction.Neutral, Id, Color.OrangeRed,
               targets: HitTargets.EntitiesOnly, origin: Body.Position));

    // Blast reach as a faint ring once armed, brightening over the last second of fuse.
    public void Telegraph(TelegraphList t)
    {
        if (_exploded || IsDead) return;
        if (!Armed) { t.Ring(Body.Position, _radius, new Color(200, 160, 80) * 0.15f, 20); return; }
        float late = MathHelper.Clamp(1f - FuseLeft, 0f, 1f);
        t.Ring(Body.Position, _radius, Color.OrangeRed * (0.25f + 0.5f * late), 20);
    }
}
