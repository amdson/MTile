using System;
using Microsoft.Xna.Framework;

namespace MTile;

// ── THE PLANT BASE ──────────────────────────────────────────────────────────
// A rooted, owned, growable entity planted from the seed bar (Plans/PLANT_SEEDS_PLAN.md
// §4 "Shared rules", §5). Every plant kind subclasses this and gets, for free:
//
//   * Root. The plant sits in its own open cell (CellX, CellY) against a root tile on
//     face `Root`. The moment the root tile is not Solid the plant dies — checked every
//     Update from the grid (the plan's §9 choice: "check their cells in Update" rather
//     than hanging sim behaviour off the cosmetic OnTileBroken event).
//   * Growth. Seedling → mature over GrowSeconds. A seedling does nothing and has
//     PlantTuning.SeedlingHealth; maturing ADDS (MaxHealth − SeedlingHealth), so damage
//     taken while growing is never healed.
//   * Team. Faction and Team are the owner's. Because the hurtbox carries the owner's
//     player faction, CombatSystem's faction gate already keeps the owner's own attacks
//     (slashes, projectiles, thrown clods — all of which carry that faction) off it, and
//     the plant's own hitboxes off its owner. Spikes follow Team (SpikeContactSystem).
//   * Burial (§5). Own cell Solid → Dormant: no growth, no acting, and health drains so a
//     full-health plant dies in PlantTuning.BuryKillSeconds. Uncovered it resumes; what
//     was lost stays lost. Tile placement never checks bodies, and Rooted sets
//     IgnoreTiles, so a block can be built straight over a plant without shoving it.
//   * Snapshot. Everything that varies is in EntityData.Plant (PlantData); subclasses
//     marshal their kind scratch via WritePlant / ReadPlant.
//
// Subclass contract (see SpitterPlant):
//   - ctor(PlantSite, EntityId owner, Faction, int team) : base(site, owner, faction, team, matureHealth)
//   - Seed, GrowSeconds, Kind
//   - Act(...)            — mature and awake only; publish hitboxes / spawn here
//   - OnSuppressed()      — a frame it could not act (seedling/dormant): drop wind-ups
//   - ActivePose          — Idle or Act while mature and awake
//   - WritePlant/ReadPlant — the kind scratch in PlantData
//   - FigureFor(pose)     — render-only: the SpriteAnimation for a pose (Drawing/Plants)
//   - static FromState(in EntityData, Vector2) + one line in EntityFactory.Rehydrate
public abstract class PlantEntity : Entity, ITelegraphSource
{
    public EntityId  Owner  { get; }
    public int       CellX  { get; }
    public int       CellY  { get; }
    public PlantRoot Root   { get; }

    // Seconds grown. Stops while dormant.
    public float Growth { get; protected set; }
    public bool  Dormant { get; private set; }

    public abstract SeedKind Seed { get; }
    public abstract float GrowSeconds { get; }

    public bool  IsMature => Growth >= GrowSeconds;
    public float Growth01 => GrowSeconds > 0f ? MathHelper.Clamp(Growth / GrowSeconds, 0f, 1f) : 1f;
    public Vector2 Up => PlantRules.UpOf(Root);
    public PlantSite Site => new(CellX, CellY, Root);

    protected PlantEntity(PlantSite site, EntityId owner, Faction faction, int team, float matureHealth)
        : base(new PhysicsBody(Polygon.CreateRectangle(PlantTuning.BodyHalf * 2f, PlantTuning.BodyHalf * 2f),
                               site.Center),
               health: matureHealth)
    {
        Owner        = owner;
        CellX        = site.CellX;
        CellY        = site.CellY;
        Root         = site.Root;
        Faction      = faction;
        Team         = team;
        Rooted       = true;
        GravityScale = 0f;
        Mass         = 4f;
        Health       = MathF.Min(PlantTuning.SeedlingHealth, matureHealth);
        Color        = Color.Transparent;   // the figure is the visual; no polygon outline
        Sprite       = _figure;
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        var chunks = spawner?.Chunks;
        if (chunks != null)
        {
            if (!PlantRules.RootHolds(chunks, CellX, CellY, Root)) { Health = 0f; return; }
            Dormant = chunks.GetCellState(CellX, CellY) == TileState.Solid;
        }

        if (Dormant)
        {
            Health -= MaxHealth / PlantTuning.BuryKillSeconds * dt;
            OnSuppressed();
            return;
        }

        if (!IsMature)
        {
            Growth += dt;
            if (IsMature) Health += MathF.Max(0f, MaxHealth - PlantTuning.SeedlingHealth);
            OnSuppressed();
            return;
        }

        Act(dt, player, hitboxes, spawner);
    }

    protected abstract void Act(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner);
    protected virtual void OnSuppressed() { }

    // ── Pose (render reads it) ──────────────────────────────────────────────
    protected virtual PlantPose ActivePose => PlantPose.Idle;

    public PlantPose Pose
    {
        get
        {
            if (Dormant)          return PlantPose.Dormant;
            if (!IsMature)        return Growth01 < 0.2f ? PlantPose.Seed : PlantPose.Sprout;
            var a = ActivePose;
            if (a == PlantPose.Act) return PlantPose.Act;
            if (Health < PlantTuning.WiltFraction * MaxHealth) return PlantPose.Wilt;
            return a;
        }
    }

    // ── Snapshot ────────────────────────────────────────────────────────────
    protected sealed override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.Plant.Owner   = Owner;
        s.Plant.CellX   = CellX;
        s.Plant.CellY   = CellY;
        s.Plant.RootDir = (int)Root;
        s.Plant.Growth  = Growth;
        s.Plant.Dormant = Dormant;
        WritePlant(ref s.Plant);
    }

    protected sealed override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        Growth  = s.Plant.Growth;
        Dormant = s.Plant.Dormant;
        ReadPlant(in s.Plant);
    }

    protected virtual void WritePlant(ref PlantData p) { }
    protected virtual void ReadPlant(in PlantData p) { }

    // What a Rehydrate passes back to a subclass ctor.
    protected static PlantSite SiteOf(in EntityData d) => new(d.Plant.CellX, d.Plant.CellY, (PlantRoot)d.Plant.RootDir);

    // ── Render (never read by the sim) ──────────────────────────────────────
    private readonly AnimatedSprite _figure = new();
    private SpriteAnimation _playing;

    // The figure's animation for `pose`; may depend on render-only facing/team/growth.
    protected abstract SpriteAnimation FigureFor(PlantPose pose);

    public override void SyncSprite()
    {
        if (Sprite == null) return;
        Sprite.Position = Body.Position;
        Sprite.Rotation = PlantRules.RotationOf(Root);
        var anim = FigureFor(Pose);
        if (anim != null && !ReferenceEquals(anim, _playing)) { _playing = anim; _figure.Play(anim); }
    }

    // Shared overlay: a growth arc while growing, and for a buried plant an outline of
    // its cell plus a health tick, so it stays readable through the covering block (§5).
    // Subclasses add their tells in TelegraphKind.
    public void Telegraph(TelegraphList t)
    {
        if (IsDead) return;
        var c = Body.Position;
        var team = PlantPalette.Team(Team);
        if (Dormant)
        {
            const float h = Chunk.TileSize * 0.5f;
            var col = team * (0.55f + 0.25f * MathF.Sin(Growth * 6f + CellX));
            t.Line(c + new Vector2(-h, -h), c + new Vector2(h, -h), col, 1f);
            t.Line(c + new Vector2(h, -h),  c + new Vector2(h, h),  col, 1f);
            t.Line(c + new Vector2(h, h),   c + new Vector2(-h, h), col, 1f);
            t.Line(c + new Vector2(-h, h),  c + new Vector2(-h, -h), col, 1f);
            float f = MathHelper.Clamp(Health / MaxHealth, 0f, 1f);
            t.Line(c + new Vector2(-h, h + 2f), c + new Vector2(-h + 2f * h * f, h + 2f), PlantPalette.Wilt, 1.5f);
            return;
        }
        if (!IsMature)
        {
            float p = Growth01;
            t.Arc(c, PlantTuning.BodyHalf + 3f, -MathHelper.PiOver2 + p * MathHelper.Pi, p * MathHelper.Pi,
                  team * 0.6f, 12, 1f);
        }
        TelegraphKind(t);
    }

    protected virtual void TelegraphKind(TelegraphList t) { }
}
