using System;
using Microsoft.Xna.Framework;

namespace MTile;

// A destructible nest that keeps up to MaxLive of one entity kind alive near itself:
// it spawns one every Interval seconds while fewer than MaxLive live entities of that
// kind are within LocalRadius, pauses at the cap, resumes when the count drops, and
// stops for good once destroyed. Sim-side and snapshotted.
//
// The read:
//   * Counting is by kind and distance, not ownership: any live entity of SpawnKind
//     within LocalRadius of the nest counts, whoever made it. That keeps the rule
//     stateless (no list of children to snapshot or to fall out of sync) — a kill, a
//     despawn or a wanderer leaving the radius all just lower the count.
//   * One timer. It starts at FirstDelay; every frame under the cap it counts down and
//     a spawn resets it to Interval. At the cap it is held at Interval, so the frame
//     the count drops the countdown starts from a full interval — a kill never refills
//     instantly, and it never banks time while idle.
//   * It is a plain Entity, not an EnemyEntity: enemy brains only target EnemyEntity /
//     players, so a nest is never a target. Players hit it through its ordinary hurtbox.
//   * Rooted (no gravity, no knockback, no depenetration). Position is constant from
//     construction, so only the config and the timer/count ride the snapshot, through
//     EntityData slots (see WriteState); FromState rebuilds a recreated nest from them.
//   * Visual is a Telegraph: body disc, a ring that fills toward the next spawn, and
//     one pip per cap slot, lit for each live spawnee.
public sealed class EnemySpawner : Entity, ITelegraphSource
{
    public const float BodyRadius  = 12f;
    public const float FirstDelay  = 1f;

    public EntityKind SpawnKind   { get; }
    public int        MaxLive     { get; }
    public float      Interval    { get; }
    public float      LocalRadius { get; }

    // Live spawnees counted on the last Update (render + tests).
    public int   LiveCount  { get; private set; }
    // Total spawned so far; seeds each fighter brain.
    public int   SpawnCount { get; private set; }
    // Seconds until the next spawn.
    public float Timer      { get; private set; } = FirstDelay;
    public bool  IsDestroyed => IsDead;

    public override EntityKind Kind => EntityKind.Spawner;

    private static readonly Color BodyColor  = new(110, 70, 90);
    private static readonly Color Charge = new(255, 150, 90);

    public EnemySpawner(Vector2 pos, EntityKind spawnKind, int maxLive, float interval = 3f,
                        float localRadius = 320f, float health = 3f)
        : base(new PhysicsBody(Polygon.CreateRegular(BodyRadius, 6), pos), health)
    {
        SpawnKind   = spawnKind;
        MaxLive     = Math.Max(1, maxLive);
        Interval    = Math.Max(0.05f, interval);
        LocalRadius = localRadius;
        Faction      = Faction.Enemy;
        Team         = Teams.Enemies;
        Rooted       = true;
        GravityScale = 0f;
        Mass         = 100f;
        Color        = BodyColor;
    }

    // Config rides EntityData: kind in AIState, cap in Budget, radius in MineRadius,
    // interval in MineFuse, countdown in StateTime, spawn count in HitId.
    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.AIState    = (int)SpawnKind;
        s.Budget     = MaxLive;
        s.MineRadius = LocalRadius;
        s.MineFuse   = Interval;
        s.StateTime  = Timer;
        s.HitId      = SpawnCount;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        Timer      = s.StateTime;
        SpawnCount = s.HitId;
    }

    public static EnemySpawner FromState(in EntityData d, Vector2 pos)
        => new(pos, (EntityKind)d.AIState, d.Budget, d.MineFuse, d.MineRadius, d.MaxHealth);

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead || spawner == null) return;

        int live = 0;
        float r2 = LocalRadius * LocalRadius;
        foreach (var e in spawner.LiveEntities)
            if (e.Kind == SpawnKind && !e.IsDead && e != this
                && Vector2.DistanceSquared(e.Body.Position, Body.Position) <= r2)
                live++;
        LiveCount = live;

        if (live >= MaxLive) { Timer = Interval; return; }

        Timer -= dt;
        if (Timer > 0f) return;
        Timer = Interval;
        Spawn(spawner);
    }

    private void Spawn(IEntitySpawner spawner)
    {
        var e = EntityFactory.CreateEnemy(SpawnKind, Body.Position, spawner.BlueprintTable);
        e.Body.Position = ClearSpot(e, spawner.Chunks);
        e.Team = Team;
        if (e is EnemyEntity en)
            en.SeedBrain(BrainRandom.Mix((ulong)(uint)Id.Index, (ulong)SpawnCount + 1));
        SpawnCount++;
        spawner.SpawnEntity(e);
        LiveCount++;
    }

    // First of up / sideways / down, at the distance where the spawnee just clears the
    // nest, whose body is free of terrain. Falls back to straight up.
    private Vector2 ClearSpot(Entity e, ChunkMap chunks)
    {
        var b = e.Body.Bounds;
        float hx = b.Width * 0.5f, hy = b.Height * 0.5f;
        Span<Vector2> dirs = stackalloc Vector2[]
            { new(0, -1), new(1, 0), new(-1, 0), new(1, -1), new(-1, -1), new(0, 1) };
        var first = default(Vector2);
        for (int i = 0; i < dirs.Length; i++)
        {
            var d = dirs[i];
            var p = Body.Position + new Vector2(d.X * (BodyRadius + hx + 2f), d.Y * (BodyRadius + hy + 2f));
            if (i == 0) first = p;
            if (chunks == null || BodyFree(chunks, p, hx, hy)) return p;
        }
        return first;
    }

    private static bool BodyFree(ChunkMap chunks, Vector2 p, float hx, float hy)
    {
        for (int ix = -1; ix <= 1; ix++)
            for (int iy = -1; iy <= 1; iy++)
                if (TileQuery.IsSolidAt(chunks, p.X + ix * hx, p.Y + iy * hy)) return false;
        return true;
    }

    public void Telegraph(TelegraphList t)
    {
        if (IsDead) return;
        var c = Body.Position;
        t.Disc(c, BodyRadius - 2f, BodyColor);
        t.Ring(c, BodyRadius, Color.Lerp(BodyColor, Color.White, 0.4f), 6, 1.5f);

        // Charge ring: fills as the countdown runs; dim and full while at the cap.
        bool capped = LiveCount >= MaxLive;
        float p = capped ? 1f : 1f - MathHelper.Clamp(Timer / Interval, 0f, 1f);
        float rr = BodyRadius + 5f;
        t.Ring(c, rr, Charge * 0.15f, 20, 1f);
        if (p > 0.02f)
            t.Arc(c, rr, -MathHelper.PiOver2 + p * MathHelper.Pi, p * MathHelper.Pi,
                  capped ? Charge * 0.35f : Charge, 16, 1.5f);

        // Pips above: one per cap slot, lit for each live spawnee.
        float step = 5f, x0 = c.X - (MaxLive - 1) * step * 0.5f, y = c.Y - BodyRadius - 12f;
        for (int i = 0; i < MaxLive; i++)
            t.Disc(new Vector2(x0 + i * step, y), 1.8f, i < LiveCount ? Charge : Charge * 0.2f);
    }
}
