using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ── SNAPPER (Plans/PLANT_SEEDS_PLAN.md §4.2) ────────────────────────────────
// A close-range guard for its patch. Mature and awake, when a living opposing player is
// within SnapperRange of its head with a terrain sightline, it WINDS UP for SnapperWindup
// (the tell: head rears back, jaws flare, the bite's reach is outlined), then BITES: one
// heavy-tier hitbox toward the AIM point (published for SnapperBiteActive, one HitId), then
// is SPENT for SnapperRearm. The aim is the target's position when the wind-up began plus
// limited tracking (SnapperTrackSpeed), so stepping out dodges it. Once begun the wind-up
// does not cancel when the target leaves — it bites air and is spent (baiting is the
// opening); only burial/seedling state resets it.
//
// Kind scratch in PlantData: Phase (0 armed/idle, 1 winding, 2 biting, 3 spent), Clock
// (seconds into wind-up / bite), Timer (re-arm seconds left), Aim (locked bite point,
// world px; in phases 0/3 the last seen target or the head, render lean only), HitId.
public sealed class SnapperPlant : PlantEntity
{
    private const int Armed = 0, Winding = 1, Biting = 2, Spent = 3;

    private int     _phase;
    private float   _clock;
    private float   _rearm;
    private Vector2 _aim;
    private int     _hitId;

    public override EntityKind Kind => EntityKind.PlantSnapper;
    public override SeedKind  Seed => SeedKind.Snapper;
    public override float GrowSeconds => PlantTuning.SnapperGrowSeconds;

    public int     Phase => _phase;
    public Vector2 Head  => Body.Position + Up * PlantTuning.SnapperHeadOffset;
    public float   WindupProgress => MathHelper.Clamp(_clock / PlantTuning.SnapperWindup, 0f, 1f);
    public float   RearmProgress  => MathHelper.Clamp(1f - _rearm / PlantTuning.SnapperRearm, 0f, 1f);

    public SnapperPlant(PlantSite site, EntityId owner, Faction faction, int team)
        : base(site, owner, faction, team, PlantTuning.SnapperHealth) { }

    public static SnapperPlant FromState(in EntityData d, Vector2 pos)
        => new(SiteOf(in d), d.Plant.Owner, d.Faction, d.Team);

    protected override PlantPose ActivePose => _phase is Winding or Biting ? PlantPose.Act : PlantPose.Idle;

    // Seedling or buried: a wind-up/bite in progress is dropped. A running re-arm is kept
    // (burying a spent snapper must not refresh it).
    protected override void OnSuppressed()
    {
        if (_phase is Winding or Biting) { _phase = Armed; _clock = 0f; }
    }

    protected override void Act(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        switch (_phase)
        {
            case Armed:
            {
                var target = FindTarget(spawner);
                if (target == null) { _aim = Body.Position; return; }
                _aim = target.Body.Position;
                _phase = Winding;
                _clock = 0f;
                break;
            }
            case Winding:
            {
                var target = FindTarget(spawner);
                if (target != null)
                {
                    var to = target.Body.Position - _aim;
                    float step = PlantTuning.SnapperTrackSpeed * dt, len = to.Length();
                    _aim = len <= step ? target.Body.Position : _aim + to / len * step;
                }
                _clock += dt;
                if (_clock >= PlantTuning.SnapperWindup)
                {
                    _phase = Biting;
                    _clock = 0f;
                    _hitId = spawner?.HitIds?.Next() ?? 0;
                }
                break;
            }
            case Biting:
                Bite(hitboxes);
                _clock += dt;
                if (_clock >= PlantTuning.SnapperBiteActive)
                {
                    _phase = Spent;
                    _clock = 0f;
                    _rearm = PlantTuning.SnapperRearm;
                }
                break;
            default:   // Spent
                _rearm = MathF.Max(0f, _rearm - dt);
                if (_rearm <= 0f) { _phase = Armed; _clock = 0f; }
                break;
        }
    }

    private void BiteGeometry(out Vector2 center, out Vector2 dir, out float rotation)
    {
        var head = Head;
        dir = _aim - head;
        float l = dir.Length();
        dir = l < 1e-3f ? Up : dir / l;
        center = head + dir * (PlantTuning.SnapperBiteReach * 0.5f);
        rotation = MathF.Atan2(dir.Y, dir.X);
    }

    // The bite: a heavy-tier box from the head along the locked aim. Body damage only —
    // tile damage 0. Occluded from the head so it cannot bite through a wall.
    private void Bite(HitboxWorld hitboxes)
    {
        if (hitboxes == null) return;
        BiteGeometry(out var center, out var dir, out var rot);
        var poly = Polygon.CreateRectangle(PlantTuning.SnapperBiteReach, PlantTuning.SnapperBiteWidth);
        hitboxes.Publish(new Hitbox(
            poly.GetBoundingBox(center, rot), _hitId,
            0f,
            dir * PlantTuning.SnapperKnockback,
            Faction, Id, Color.OrangeRed,
            targets: HitTargets.EntitiesOnly,
            bodyDamage: PlantTuning.SnapperBiteDamage,
            shape: poly, shapePos: center, shapeRotation: rot,
            hitstunSecondsOverride: PlantTuning.SnapperHitstun,
            origin: Head,
            launchDecay: PlantTuning.SnapperLaunchDecay,
            clank: ClankKind.Blunt));
    }

    // Nearest living opposing player within range of the head, with a terrain sightline.
    // Players in the sim's fixed order, nearest wins, ties to the earlier.
    private PlayerCharacter FindTarget(IEntitySpawner spawner)
    {
        if (spawner == null) return null;
        var chunks = spawner.Chunks;
        var from = Head;
        PlayerCharacter best = null;
        float bestD2 = PlantTuning.SnapperRange * PlantTuning.SnapperRange;
        foreach (var p in spawner.Players)
        {
            if (p == null || !p.IsAlive) continue;
            if (!SpikeContactSystem.Opposes(Team, p.Team)) continue;
            if (p.Faction == Faction) continue;
            var c = p.Body.Position;
            float d2 = Vector2.DistanceSquared(from, c);
            if (!(d2 <= bestD2)) continue;
            if (best != null && !(d2 < bestD2)) continue;
            if (chunks != null && !TileReach.IsPointReachable(chunks, from, c)) continue;
            best = p; bestD2 = d2;
        }
        return best;
    }

    protected override void WritePlant(ref PlantData p)
    {
        p.Phase = _phase;
        p.Clock = _clock;
        p.Timer = _rearm;
        p.Aim   = _aim;
        p.HitId = _hitId;
    }

    protected override void ReadPlant(in PlantData p)
    {
        _phase = p.Phase;
        _clock = p.Clock;
        _rearm = p.Timer;
        _aim   = p.Aim;
        _hitId = p.HitId;
    }

    // ── Render ──────────────────────────────────────────────────────────────
    // Local +x after the sprite's root rotation (the figure is drawn upright on a floor).
    private int LocalFacing
    {
        get
        {
            var right = new Vector2(-Up.Y, Up.X);
            float x = Vector2.Dot(_aim - Body.Position, right);
            return x < -0.5f ? -1 : x > 0.5f ? 1 : 0;
        }
    }

    protected override SpriteAnimation FigureFor(PlantPose pose)
    {
        var state = pose switch
        {
            PlantPose.Seed    => SnapperFigure.State.Seed,
            PlantPose.Sprout  => SnapperFigure.State.Sprout,
            PlantPose.Dormant => SnapperFigure.State.Dormant,
            PlantPose.Wilt    => SnapperFigure.State.Wilt,
            PlantPose.Act     => _phase == Biting ? SnapperFigure.State.Bite : SnapperFigure.State.WindUp,
            _                 => _phase == Spent ? SnapperFigure.State.Rearm : SnapperFigure.State.Idle,
        };
        return SnapperAnims.For(Team).Get(state, LocalFacing, Growth01, RearmProgress);
    }

    // The tell: the bite's reach outlined and brightening through the wind-up (and held
    // during the bite). Declared from the locked aim, so it shows where the bite will land.
    protected override void TelegraphKind(TelegraphList t)
    {
        if (_phase is not (Winding or Biting)) return;
        float p = _phase == Biting ? 1f : WindupProgress;
        BiteGeometry(out _, out var dir, out _);
        var n = new Vector2(-dir.Y, dir.X) * (PlantTuning.SnapperBiteWidth * 0.5f);
        var a = Head;
        var b = Head + dir * PlantTuning.SnapperBiteReach;
        var col = Color.Lerp(PlantPalette.Team(Team), Color.OrangeRed, p) * (0.25f + 0.6f * p);
        t.Line(a + n, b + n, col, 1f);
        t.Line(a - n, b - n, col, 1f);
        t.Line(b + n, b - n, col, 1f);
    }
}

// Render-only cache of SnapperFigure frames, per team, built lazily per (state, facing,
// growth/rearm bucket). SnapperFigure.Build allocates, so nothing here runs per frame once
// warm. Always built for RootFace.Floor: PlantEntity.SyncSprite rotates the sprite onto the
// real root face (the figure's other RootFace values are for callers that do not rotate).
public sealed class SnapperAnims
{
    private static readonly Dictionary<int, SnapperAnims> _byTeam = new();
    public static SnapperAnims For(int team)
    {
        if (!_byTeam.TryGetValue(team, out var a)) _byTeam[team] = a = new SnapperAnims(team);
        return a;
    }

    private const int GrowthBuckets = 4, RearmBuckets = 8;
    private readonly Color _team;
    private readonly Dictionary<int, SpriteAnimation> _cache = new();

    private SnapperAnims(int team) { _team = PlantPalette.Team(team); }

    // facing: -1 / 0 / +1 (lean toward the target along the surface tangent).
    public SpriteAnimation Get(SnapperFigure.State s, int facing, float growth01, float rearm01)
    {
        int bucket = 0;
        if (s is SnapperFigure.State.Seed or SnapperFigure.State.Sprout)
            bucket = Math.Clamp((int)(growth01 * GrowthBuckets), 0, GrowthBuckets - 1);
        else if (s == SnapperFigure.State.Rearm)
            bucket = Math.Clamp((int)(rearm01 * RearmBuckets), 0, RearmBuckets - 1);
        if (s != SnapperFigure.State.Idle) facing = facing < 0 ? -1 : 1;
        int key = ((int)s * 3 + (facing + 1)) * 16 + bucket;
        if (!_cache.TryGetValue(key, out var anim)) _cache[key] = anim = Make(s, facing, bucket);
        return anim;
    }

    private SpriteAnimation Make(SnapperFigure.State s, int facing, int bucket)
    {
        float lean = facing;
        float g = s is SnapperFigure.State.Seed or SnapperFigure.State.Sprout
            ? (bucket + 0.5f) / GrowthBuckets
            : s == SnapperFigure.State.Dormant ? 0f : 1f;   // dormant curls up to fit its cell
        float r = (bucket + 0.5f) / RearmBuckets;
        Pose At(float time) => SnapperFigure.Build(s, g, lean, r, SnapperFigure.RootFace.Floor, _team, time);

        switch (s)
        {
            case SnapperFigure.State.WindUp:
                return Loop(At, 8, SnapperFigure.WindupLoop);
            case SnapperFigure.State.Idle:
                return Loop(At, 12, SnapperFigure.IdleLoop);
            case SnapperFigure.State.Bite:
                return new SpriteAnimation(new[] { At(0f) }, 1f, false);
            case SnapperFigure.State.Rearm:
            case SnapperFigure.State.Dormant:
                return new SpriteAnimation(new[] { At(0f) }, 1f, true);
            default:   // Seed, Sprout, Wilt: gentle looping
                return Loop(At, 8, 2f);
        }
    }

    private static SpriteAnimation Loop(Func<float, Pose> at, int n, float period)
    {
        var frames = new Pose[n];
        for (int i = 0; i < n; i++) frames[i] = at(period * i / n);
        return new SpriteAnimation(frames, period / n, true);
    }

    // The ghost's silhouette: mature, armed, upright.
    public static Pose Silhouette => _silhouette ??= SnapperFigure.Build(SnapperFigure.State.Idle, 1f, 0f, 0f,
        SnapperFigure.RootFace.Floor, Color.White, 0f);
    private static Pose _silhouette;
}
