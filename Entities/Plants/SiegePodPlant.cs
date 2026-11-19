using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ── SIEGE POD (Plans/PLANT_SEEDS_PLAN.md §4.4, decisions 5 and 9) ───────────
// A one-off: planted with an aim fixed at placement (the press → release drag; a drag
// shorter than SiegePodMinDrag aims straight away from the root face), it grows for
// SiegePodGrowSeconds with that aim telegraphed to everyone — SiegeBlast.Telegraph from the
// muzzle, so the line shows where the lance would end against the terrain as it stands —
// and a louder tell for the last SiegePodChargeSeconds. On the first awake frame after
// ripening it FIRES ONCE (SiegeBlast.Fire, source = this pod's own id), then is Spent: no
// hurtbox, not counted, not holding its cell, kept only SiegePodFireLinger seconds so the
// figure can show its Fire pose and the lance flash, then removed.
//
// Everything else is the base: burial freezes growth (a buried pod never ripens), a broken
// root or lost health removes it with NO shot. The shot is Faction.Hazard — not
// team-filtered — and kills any plant it touches (PlantEntity.OnHit).
//
// MUZZLE. The 2-lane lance rounds its band onto rows/columns (SiegeBlast's rounding note):
// from a cell centre a horizontal shot would cover the pod's row AND the row below — the
// floor it stands on. The muzzle therefore sits on the pod's OPEN face, the cell edge
// opposite the root (cell centre + Up · ts/2), i.e. exactly on a row/column boundary: a
// floor pod's horizontal lanes are its own row and the row above, a wall pod's vertical
// lanes are its own column and the one away from the wall. The root row is never a lane
// of a tangential shot, and small aim slopes (< half a cell per column) keep it that way.
//
// Kind scratch in PlantData: Aim (unit aim), Phase (0 growing, 1 fired), Clock (seconds
// since firing), Timer (the fired lance's reach, px — the linger's flash).
public sealed class SiegePodPlant : PlantEntity
{
    private const int Growing = 0, Fired = 1;

    private Vector2 _aim;
    private int     _phase;
    private float   _clock;
    private float   _shotReach;

    // Render-only read handle for the growth telegraph's reach preview (TelegraphKind has no
    // terrain argument). Never snapshotted, never read by the sim.
    private ChunkMap _terrain;

    public override EntityKind Kind => EntityKind.PlantSiegePod;
    public override SeedKind  Seed => SeedKind.SiegePod;
    public override float GrowSeconds => PlantTuning.SiegePodGrowSeconds;

    public Vector2 Aim    => _aim;
    public Vector2 Muzzle => MuzzleFor(Site);
    public bool    HasFired => _phase == Fired;
    public float   ShotReach => _shotReach;   // the fired lance's reach, px (0 before firing)
    public override bool Spent => _phase == Fired;

    public float ChargeStart => PlantTuning.SiegePodGrowSeconds - PlantTuning.SiegePodChargeSeconds;
    public bool  Charging    => _phase == Growing && !Dormant && Growth >= ChargeStart;
    public float Charge01    => MathHelper.Clamp((Growth - ChargeStart) / PlantTuning.SiegePodChargeSeconds, 0f, 1f);

    public SiegePodPlant(PlantSite site, EntityId owner, Faction faction, int team, Vector2 drag)
        : base(site, owner, faction, team, PlantTuning.SiegePodHealth)
    {
        _aim = AimFor(site, drag);
    }

    public static SiegePodPlant FromState(in EntityData d, Vector2 pos)
        => new(SiteOf(in d), d.Plant.Owner, d.Faction, d.Team, d.Plant.Aim);

    // The locked aim for a drag: its direction, or straight away from the root face when the
    // drag is too short to mean anything.
    public static Vector2 AimFor(PlantSite site, Vector2 drag)
        => drag.LengthSquared() >= PlantTuning.SiegePodMinDrag * PlantTuning.SiegePodMinDrag
            ? Vector2.Normalize(drag)
            : site.Up;

    // The cell edge opposite the root (see MUZZLE above).
    public static Vector2 MuzzleFor(PlantSite site) => site.Center + site.Up * (Chunk.TileSize * 0.5f);

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        _terrain = spawner?.Chunks ?? _terrain;
        if (_phase == Fired)
        {
            // The linger: nothing but a clock. Root and burial no longer matter.
            _clock += dt;
            if (_clock >= PlantTuning.SiegePodFireLinger) Health = 0f;
            return;
        }
        base.Update(dt, player, hitboxes, spawner);
    }

    // Called on the first mature, awake frame: ripe — fire, once.
    protected override void Act(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (_phase != Growing) return;
        var reach = SiegeBlast.Fire(spawner?.Chunks, hitboxes, spawner?.HitIds, Muzzle, _aim, Id);
        _phase     = Fired;
        _clock     = 0f;
        _shotReach = reach.Reach;
    }

    protected override void WritePlant(ref PlantData p)
    {
        p.Aim   = _aim;
        p.Phase = _phase;
        p.Clock = _clock;
        p.Timer = _shotReach;
    }

    protected override void ReadPlant(in PlantData p)
    {
        _aim       = p.Aim;
        _phase     = p.Phase;
        _clock     = p.Clock;
        _shotReach = p.Timer;
    }

    // ── Telegraph ───────────────────────────────────────────────────────────
    // Growing: the would-be lance + blast ring, brightening with growth. Charging: on top of
    // that, a blinking white-hot band and a pulsing ring that speeds up to the shot. Fired:
    // the lance as it actually went, fading over the linger.
    protected override void TelegraphKind(TelegraphList t)
    {
        if (_phase == Fired)
        {
            float f = 1f - MathHelper.Clamp(_clock / PlantTuning.SiegePodFireLinger, 0f, 1f);
            var n = new Vector2(-_aim.Y, _aim.X) * Chunk.TileSize;
            var a = Muzzle; var b = a + _aim * _shotReach;
            var hot = Color.Lerp(new Color(255, 170, 60), Color.White, 0.5f) * f;
            t.Line(a - n, b - n, hot, 1f + 2f * f);
            t.Line(a + n, b + n, hot, 1f + 2f * f);
            t.Line(a, b, Color.White * f, 2f + 4f * f);
            t.Ring(a, SiegeBlast.BlastRadius * (1f - 0.3f * f), Color.OrangeRed * f, 24, 1f + 2f * f);
            return;
        }
        if (_terrain == null) return;
        var reach = SiegeBlast.Telegraph(t, _terrain, Muzzle, _aim, Growth01);
        if (!Charging) return;

        float c = Charge01;
        bool on = ((int)((Growth - ChargeStart) * (6f + 14f * c))) % 2 == 0;
        var col = Color.Lerp(Color.OrangeRed, Color.White, on ? 0.7f : 0.1f) * (0.55f + 0.45f * c);
        for (int j = 0; j < reach.LaneCount; j++)
        {
            var lane = reach[j];
            t.Line(lane.Start, lane.End, col, 2f + 2f * c);
        }
        float pulse = ((Growth - ChargeStart) * (2f + 4f * c)) % 1f;
        t.Ring(reach.Muzzle, SiegeBlast.BlastRadius * (0.2f + 0.8f * pulse), col * (1f - pulse), 20, 1.5f);
        t.Ring(reach.Muzzle, SiegeBlast.BlastRadius, col, 24, 1.5f + c);
    }

    // ── Render ──────────────────────────────────────────────────────────────
    // The aim in the figure's own frame: PlantEntity.SyncSprite rotates the sprite by
    // RotationOf(Root), and the figure is always built for a floor root, so the world angle
    // is taken back through that rotation here (otherwise the aim would rotate twice).
    public float LocalAimRadians => MathF.Atan2(_aim.Y, _aim.X) - PlantRules.RotationOf(Root);

    private const float SeedEnd = 0.05f, SproutEnd = 0.2f;

    protected override SpriteAnimation FigureFor(PlantPose pose)
    {
        SiegePodFigure.PodPose p;
        float g = Growth01, stage = 0f;
        float pool = MathF.Min(PlantTuning.SeedlingHealth, MaxHealth);
        if (_phase == Fired)
        {
            p = SiegePodFigure.PodPose.Fire;
            stage = MathHelper.Clamp(_clock / PlantTuning.SiegePodFireLinger, 0f, 1f);
        }
        else if (Dormant)                p = SiegePodFigure.PodPose.Dormant;
        else if (g < SeedEnd)            p = SiegePodFigure.PodPose.Seed;
        else if (g < SproutEnd)
        {
            p = SiegePodFigure.PodPose.Sprout;
            g = (g - SeedEnd) / (SproutEnd - SeedEnd);
        }
        else if (Charging)
        {
            p = SiegePodFigure.PodPose.Charging;
            stage = Charge01;
        }
        else if (Health < 0.5f * pool)
        {
            p = SiegePodFigure.PodPose.Wilt;
            stage = MathHelper.Clamp(1f - Health / pool, 0f, 1f);
        }
        else p = SiegePodFigure.PodPose.Growing;

        if (p is SiegePodFigure.PodPose.Growing or SiegePodFigure.PodPose.Wilt or SiegePodFigure.PodPose.Dormant)
            g = MathHelper.Clamp((Growth - SproutEnd * GrowSeconds) / (ChargeStart - SproutEnd * GrowSeconds), 0f, 1f);

        return SiegePodAnims.For(Team).Get(p, g, LocalAimRadians, stage);
    }
}

// Render-only cache of SiegePodFigure frames, per team, keyed on (pose, growth bucket,
// local aim in 1/96 turns, stage bucket). SiegePodFigure.Build allocates, so once warm
// nothing here builds per frame; a pod's aim never changes, so it only ever touches a
// handful of entries. Always built for RootFace.Floor (SyncSprite rotates onto the root).
public sealed class SiegePodAnims
{
    private static readonly Dictionary<int, SiegePodAnims> _byTeam = new();
    public static SiegePodAnims For(int team)
    {
        if (!_byTeam.TryGetValue(team, out var a)) _byTeam[team] = a = new SiegePodAnims(team);
        return a;
    }

    private const int GrowthBuckets = 16, ChargeBuckets = 6, FireBuckets = 4, WiltBuckets = 4, AimSteps = 96;
    private readonly Color _team;
    private readonly Dictionary<(int, int, int, int), SpriteAnimation> _cache = new();

    private SiegePodAnims(int team) { _team = PlantPalette.Team(team); }

    public static int QuantiseAim(float radians)
        => ((int)MathF.Round(radians / MathHelper.TwoPi * AimSteps) % AimSteps + AimSteps) % AimSteps;

    public SpriteAnimation Get(SiegePodFigure.PodPose pose, float growth01, float localAim, float stage01)
    {
        int aq = QuantiseAim(localAim);
        int gb = 0, sb = 0;
        switch (pose)
        {
            case SiegePodFigure.PodPose.Charging: sb = Bucket(stage01, ChargeBuckets); break;
            case SiegePodFigure.PodPose.Fire:     sb = Bucket(stage01, FireBuckets);   break;
            case SiegePodFigure.PodPose.Wilt:     sb = Bucket(stage01, WiltBuckets); gb = Bucket(growth01, GrowthBuckets); break;
            case SiegePodFigure.PodPose.Seed:     break;
            default:                              gb = Bucket(growth01, GrowthBuckets); break;
        }
        var key = ((int)pose, gb, aq, sb);
        if (!_cache.TryGetValue(key, out var anim)) _cache[key] = anim = Make(pose, gb, aq, sb);
        return anim;
    }

    private static int Bucket(float v, int n) => Math.Clamp((int)(v * n), 0, n - 1);

    private SpriteAnimation Make(SiegePodFigure.PodPose pose, int gb, int aq, int sb)
    {
        float aim = aq / (float)AimSteps * MathHelper.TwoPi;
        float g = (gb + 0.5f) / GrowthBuckets;
        float s = pose switch
        {
            SiegePodFigure.PodPose.Charging => (sb + 0.5f) / ChargeBuckets,
            SiegePodFigure.PodPose.Fire     => (sb + 0.5f) / FireBuckets,
            SiegePodFigure.PodPose.Wilt     => (sb + 0.5f) / WiltBuckets,
            _                               => 0f,
        };
        Pose At(float time) => SiegePodFigure.Build(pose, g, aim, SiegePodFigure.RootFace.Floor, _team, time, s);

        switch (pose)
        {
            case SiegePodFigure.PodPose.Charging:
                return Loop(At, 6, 0.5f);                  // 12 fps: the shake and the rib run
            case SiegePodFigure.PodPose.Fire:
            case SiegePodFigure.PodPose.Dormant:
                return new SpriteAnimation(new[] { At(0f) }, 1f, true);
            default:                                       // Seed, Sprout, Growing, Wilt: breathing
                return Loop(At, 8, 2f);
        }
    }

    private static SpriteAnimation Loop(Func<float, Pose> at, int n, float period)
    {
        var frames = new Pose[n];
        for (int i = 0; i < n; i++) frames[i] = at(period * i / n);
        return new SpriteAnimation(frames, period / n, true);
    }

    // The cursor ghost: ripe, at rest, aimed `localAim` in the figure's (floor) frame.
    private static readonly Dictionary<int, Pose> _silhouettes = new();
    public static Pose Silhouette(float localAim)
    {
        int aq = QuantiseAim(localAim);
        if (!_silhouettes.TryGetValue(aq, out var p))
            _silhouettes[aq] = p = SiegePodFigure.Build(SiegePodFigure.PodPose.Growing, 1f,
                aq / (float)AimSteps * MathHelper.TwoPi, SiegePodFigure.RootFace.Floor, Color.White, 0f);
        return p;
    }
}
