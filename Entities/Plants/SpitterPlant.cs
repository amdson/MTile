using System;
using Microsoft.Xna.Framework;

namespace MTile;

// ── SPITTER (Plans/PLANT_SEEDS_PLAN.md §4.1) ────────────────────────────────
// Mature and awake, it picks the nearest opposing player within SpitterRange with a clear
// terrain sightline from its mouth, winds up for SpitterWindup (the tell: the head swells
// and an aim line is drawn), then spits one slow poke-tier ball at where the target is
// at release. One shot per SpitterShotPeriod, wind-up included. The ball is an
// EnergyBallProjectile carrying the plant's (= the owner's) faction, so it never hits
// its owner and a held guard blocks it (≤ 0.3 body damage).
//
// Target choice is a pure function of positions: players in the sim's fixed order
// (primary, then secondaries), nearest wins, ties to the earlier. Kind scratch in
// PlantData: Timer (cooldown), Clock (wind-up time), Phase (0 ready/cooling, 1 winding),
// Aim (wind-up aim point).
public sealed class SpitterPlant : PlantEntity
{
    private float   _cooldown;
    private float   _windClock;
    private int     _phase;
    private Vector2 _aim;

    public override EntityKind Kind => EntityKind.PlantSpitter;
    public override SeedKind  Seed => SeedKind.Spitter;
    public override float GrowSeconds => PlantTuning.SpitterGrowSeconds;

    public bool    WindingUp => _phase == 1;
    public float   WindupProgress => MathHelper.Clamp(_windClock / PlantTuning.SpitterWindup, 0f, 1f);
    public Vector2 AimPoint => _aim;
    public Vector2 Mouth => Body.Position + Up * PlantTuning.SpitterMouthOffset;

    public SpitterPlant(PlantSite site, EntityId owner, Faction faction, int team)
        : base(site, owner, faction, team, PlantTuning.SpitterHealth) { }

    public static SpitterPlant FromState(in EntityData d, Vector2 pos)
        => new(SiteOf(in d), d.Plant.Owner, d.Faction, d.Team);

    protected override PlantPose ActivePose => _phase == 1 ? PlantPose.Act : PlantPose.Idle;

    protected override void OnSuppressed()
    {
        _phase = 0;
        _windClock = 0f;
    }

    protected override void Act(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        var target = FindTarget(spawner);
        if (_phase == 0)
        {
            if (_cooldown > 0f) { _cooldown = MathF.Max(0f, _cooldown - dt); return; }
            if (target == null) return;
            _phase     = 1;
            _windClock = 0f;
            _aim       = target.Body.Position;
            return;
        }

        // Winding up: track the target so the tell points where the shot will go; a lost
        // target (out of range, behind cover, dead) cancels without spending the cooldown.
        if (target == null) { _phase = 0; _windClock = 0f; return; }
        _aim = target.Body.Position;
        _windClock += dt;
        if (_windClock < PlantTuning.SpitterWindup) return;

        Spit(spawner);
        _phase     = 0;
        _windClock = 0f;
        _cooldown  = PlantTuning.SpitterShotPeriod - PlantTuning.SpitterWindup;
    }

    private void Spit(IEntitySpawner spawner)
    {
        if (spawner?.HitIds == null) return;
        var from = Mouth;
        var dir  = _aim - from;
        spawner.SpawnEntity(new EnergyBallProjectile(from, dir, spawner.HitIds.Next(), Faction,
                                                     speed: PlantTuning.SpitterShotSpeed,
                                                     damage: PlantTuning.SpitterShotDamage,
                                                     lifetime: PlantTuning.SpitterShotLifetime,
                                                     pierce: false)
        {
            Team  = Team,
            Color = PlantPalette.Spit,
        });
    }

    // Nearest living opposing player in range with a terrain sightline from the mouth.
    private PlayerCharacter FindTarget(IEntitySpawner spawner)
    {
        if (spawner == null) return null;
        var chunks = spawner.Chunks;
        var from = Mouth;
        PlayerCharacter best = null;
        float bestD2 = PlantTuning.SpitterRange * PlantTuning.SpitterRange;
        foreach (var p in spawner.Players)
        {
            if (p == null || !p.IsAlive) continue;
            if (!SpikeContactSystem.Opposes(Team, p.Team)) continue;
            if (p.Faction == Faction) continue;   // never the owner's side, whatever the teams say
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
        p.Timer = _cooldown;
        p.Clock = _windClock;
        p.Phase = _phase;
        p.Aim   = _aim;
    }

    protected override void ReadPlant(in PlantData p)
    {
        _cooldown  = p.Timer;
        _windClock = p.Clock;
        _phase     = p.Phase;
        _aim       = p.Aim;
    }

    // ── Render ──────────────────────────────────────────────────────────────
    // Which way the head turns, in the figure's own (upright) frame: toward the aim along
    // the plant's local x axis. Render-only.
    private int LocalFacing
    {
        get
        {
            var d = _aim - Body.Position;
            var right = new Vector2(-Up.Y, Up.X);   // local +x after RotationOf(Root)
            float x = Vector2.Dot(d, right);
            return x < 0f ? -1 : 1;
        }
    }

    protected override SpriteAnimation FigureFor(PlantPose pose)
        => SpitterFigure.For(Team).Get(pose, LocalFacing, Growth01, WindupProgress);

    // The tell: a thinning aim line from the mouth that brightens through the wind-up.
    protected override void TelegraphKind(TelegraphList t)
    {
        if (_phase != 1) return;
        float p = WindupProgress;
        var from = Mouth;
        var dir = _aim - from;
        float len = dir.Length();
        if (len < 1f) return;
        dir /= len;
        float shown = MathF.Min(len, 18f + 30f * p);
        t.Line(from, from + dir * shown, PlantPalette.Team(Team) * (0.25f + 0.5f * p), 1f + p);
        t.Disc(from, 1.5f + 1.5f * p, PlantPalette.Spit * (0.5f + 0.5f * p));
    }
}
