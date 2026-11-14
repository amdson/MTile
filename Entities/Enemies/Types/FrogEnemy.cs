using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Entities that are ONE boss between them. BossBarHud sums every live member with the
// same title into a single bar: live members over the cap.
public interface ISwarmBoss
{
    string SwarmTitle { get; }
    int    SwarmCap   { get; }
}

// The Frog Swarm: a boss that is a crowd. Each frog is one block tall, has almost no
// health, and does one thing — rest, then hop. The fight is the arithmetic: every frog
// splits in two now and then, up to SwarmCap alive at once, so the swarm has to be
// killed faster than it breeds.
//
//   * Normal frog (green). Hops in a random direction, biased away from the frogs
//     crowding it, so the swarm spreads over the stage instead of piling up. It is
//     harmless: it cannot hurt anything. Every SplitMin..SplitMax seconds, while
//     resting and under the cap, it swells for SplitSeconds and divides (killing it
//     while it swells stops the split).
//     It wants to climb: a ledge within reach that is higher than where it sits pulls
//     the hop onto it.
//   * Walls stop it. But now and then a resting frog flicks its tongue out and eats a
//     block within TongueRange — only one a player built, and only one that is not
//     flush with the ground (see Edible) — so a wall holds the swarm back for a while,
//     not for good.
//   * Mutant (magenta, spiked). A split occasionally produces one instead. It never
//     splits; it rests briefly, crouches while showing the arc it is about to fly, and
//     leaps at the nearest player from up to MutantRange away — half the time at
//     where they stand, half the time at where their speed will have carried them by
//     the time it lands, give or take LeapNoise. The arc stays drawn through the flight. It bursts on the first
//     body it touches, or against a wall that halts its leap, and dies with the burst. It has less health than a poke deals,
//     so swatting it out of the air is the answer.
//
// A plain Entity with its own five-state loop (like StalkerEnemy), not an EnemyEntity:
// two dozen of these have no use for a brain, a target history or an action FSM.
//
// Determinism: every frog owns one random stream (snapshotted in EntityData.Senses.Rng).
// A stage-spawned frog seeds it from its entity id on its first Update; a child is
// handed a seed drawn from its parent's stream. Neighbours are only looked at when a
// hop or a split is being decided, never per frame.
public sealed class FrogEnemy : Entity, ITelegraphSource, ISwarmBoss
{
    // ── Body ─────────────────────────────────────────────────────────────────
    public const float Width  = 11f;
    public const float Height = Chunk.TileSize;          // one block tall
    public const float FrogHealth   = 0.5f;              // a mid, or two pokes
    public const float MutantHealth = 0.25f;             // any hit

    // ── The swarm ────────────────────────────────────────────────────────────
    public const int   Cap          = 36;                // live frogs, mutants included
    public const int   MutantsAbove = 24;                // more frogs than this alive: every split is a mutant
    public const float MutantChance = 0.4f;              // per split, at or below MutantsAbove
    public const float SplitMin     = 4f, SplitMax = 11f;
    public const float SplitSeconds = 0.8f;              // the swell before dividing
    private const float SplitRetry  = 2f;                // at the cap: look again this much later

    // ── Normal hop ───────────────────────────────────────────────────────────
    private const float RestMin = 0.6f, RestMax = 1.9f;
    private const float CrouchSeconds = 0.22f;
    private const float HopVxMin = 45f,  HopVxMax = 115f;
    private const float HopVyMin = 150f, HopVyMax = 235f;
    public  const float SpreadRadius = 70f;              // frogs nearer than this push the hop away
    // Climbing: a ledge up to ClimbTiles higher within ClimbLook columns pulls the hop
    // onto it (the highest one wins), ClimbChance of the time.
    public  const int   ClimbTiles  = 4, ClimbLook = 5;
    public  const float ClimbChance = 0.85f;
    private const float MinAirSpeed = 25f;               // keeps a hop moving over a lip it clipped

    // ── Tongue ───────────────────────────────────────────────────────────────
    public  const float TongueChance  = 0.2f;            // per rest, when something edible is in reach
    public  const float TongueRange   = 55f;             // five blocks
    public  const float TongueOut     = 0.12f;           // s to reach the block (it is eaten then)
    public  const float TongueSeconds = 0.34f;           // whole flick, out and back

    // ── Mutant leap ──────────────────────────────────────────────────────────
    private const float MutantRestMin = 0.45f, MutantRestMax = 0.9f;
    public  const float MutantCrouchSeconds = 0.45f;
    public  const float MutantRange  = 10 * Chunk.TileSize;   // furthest leap, in any direction
    private const float LeapTimeBase = 0.75f, LeapTimePerPx = 1f / 600f;
    private const float WallHaltMinSpeed = 20f;          // a near-vertical leap has no wall to be halted by
    public  const float LeadChance   = 0.5f;             // leaps aimed at the predicted position
    public  const float LeapNoise    = 28f;              // ± px of error on a predicted aim
    // Wide enough that a burst against a standing player's head still reaches the floor.
    public  const float BlastRadius  = 40f;
    public  const float BlastDamage  = 2.1f;
    // A scratch: a tenth of a dirt tile's health (read from the material table). Ten
    // bursts on the same dirt tile to break it; stone, in practice, never.
    public  static float BlastTileDamage => 0.1f * TileDamage.MaxHPFor(TileType.Dirt);
    private const float BlastKnockback  = 380f;
    private const int   BlastSegments   = 10;
    private const float BlastSegHalf = 10f, BlastCoreHalf = 13f;

    private const float StaggerMin = 0.3f, StaggerResumeSpeed = 90f;
    private const float HopTimeout = 4f;

    private static readonly Color FrogColor   = new(86, 178, 84);
    private static readonly Color FrogBelly   = new(170, 222, 140);
    private static readonly Color MutantColor = new(226, 62, 198);
    private static readonly Color MutantBelly = new(255, 170, 240);
    private static readonly Color BlastColor  = new(255, 120, 230);

    public enum FrogState { Rest, Crouch, Hop, Split, Stagger, Tongue }

    public bool      Mutant   { get; }
    public FrogState State    => _state;
    public bool      Exploded => _exploded;
    public int       Facing   => _facing;
    // The velocity the current crouch will release / the current hop was launched with.
    public Vector2   Launch   => _launch;

    public string SwarmTitle => "Frog Swarm";
    public int    SwarmCap   => Cap;

    public override EntityKind Kind => Mutant ? EntityKind.FrogMutant : EntityKind.Frog;

    private FrogState _state = FrogState.Rest;
    private float   _stateTime;
    private float   _restFor;        // this rest's length
    private float   _splitTimer;     // seconds until the next split attempt
    private int     _facing = 1;
    private int     _hitId;
    private Vector2 _launch;         // the velocity the crouch will release
    private ulong   _rng;            // 0 = not seeded yet
    private bool    _exploded;

    // `seed` 0 = seed from the entity id on the first Update (stage spawns).
    public FrogEnemy(Vector2 pos, bool mutant = false, ulong seed = 0)
        : base(new PhysicsBody(Polygon.CreateRectangle(Width, Height), pos), mutant ? MutantHealth : FrogHealth)
    {
        Mutant       = mutant;
        Mass         = 0.8f;
        GravityScale = 1f;
        Faction      = Faction.Enemy;
        Color        = Color.Transparent;      // drawn by Telegraph
        // A leap is aimed ballistically, so the mutant flies without drag.
        // Hops onto ledges are aimed the same way, so no frog has any.
        Body.QuadraticDrag = 0f;
        // No ground friction: it would eat a slice of every launch (a leap would land
        // short). The states brake the body themselves.
        Body.FrictionScale = 0f;
        if (seed != 0) Seed(seed);
    }

    private void Seed(ulong seed)
    {
        _rng        = BrainRandom.Seed(seed);
        _restFor    = Mutant ? Range(MutantRestMin, MutantRestMax) : Range(0.1f, RestMax);
        _splitTimer = Range(SplitMin, SplitMax);
        _facing     = Roll() < 0.5f ? -1 : 1;
    }

    private float Roll() => BrainRandom.Unit(ref _rng);
    private float Range(float a, float b) => a + (b - a) * Roll();

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.AIState    = (int)_state;
        s.StateTime  = _stateTime;
        s.Facing     = _facing;
        s.HitId      = _hitId;
        s.Aim        = _launch;
        s.Age        = _splitTimer;
        s.Lifetime   = _restFor;
        s.Exploded   = _exploded;
        s.Senses.Rng = _rng;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _state      = (FrogState)s.AIState;
        _stateTime  = s.StateTime;
        _facing     = s.Facing;
        _hitId      = s.HitId;
        _launch     = s.Aim;
        _splitTimer = s.Age;
        _restFor    = s.Lifetime;
        _exploded   = s.Exploded;
        _rng        = s.Senses.Rng;
    }

    public override Vector2 OnHit(in Hitbox hit, in Hurtbox myHurtbox)
    {
        var delivered = base.OnHit(hit, myHurtbox);
        if (!IsDead && !_exploded) Go(FrogState.Stagger);
        return delivered;
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        // The burst's hitboxes lived through last frame's CombatSystem.Apply; now go.
        if (_exploded) { Health = 0f; return; }
        if (_rng == 0) Seed(BrainRandom.Mix((ulong)(uint)Id.Index, 0xF206UL));

        _stateTime += dt;
        if (!Mutant) _splitTimer -= dt;
        bool grounded = Grounded(spawner?.Chunks);

        if (Mutant && TouchingVictim(spawner?.Hurtboxes)) { Burst(hitboxes, spawner); return; }

        switch (_state)
        {
            case FrogState.Rest:
                if (!grounded) break;
                Body.Velocity.X *= 0.7f;
                if (_stateTime < _restFor) break;
                if (!Mutant && _splitTimer <= 0f)
                {
                    if (CountLive(spawner, mutantsOnly: false) < Cap) { Go(FrogState.Split); break; }
                    _splitTimer = SplitRetry;
                }
                if (!Mutant && spawner?.Chunks != null && Roll() < TongueChance && FindMorsel(spawner.Chunks, out var morsel))
                {
                    _launch = morsel;            // the Aim slot holds the block while the tongue is out
                    if (MathF.Abs(morsel.X - Body.Position.X) > 1f) _facing = morsel.X > Body.Position.X ? 1 : -1;
                    Go(FrogState.Tongue);
                    break;
                }
                PlanHop(player, spawner);
                Go(FrogState.Crouch);
                break;

            case FrogState.Tongue:
                Body.Velocity.X *= 0.6f;
                // The frame the tip arrives, the block goes.
                if (_stateTime >= TongueOut && _stateTime - dt < TongueOut && spawner?.Chunks != null)
                {
                    int gx = (int)MathF.Floor(_launch.X / Chunk.TileSize), gy = (int)MathF.Floor(_launch.Y / Chunk.TileSize);
                    if (Edible(spawner.Chunks, gx, gy)) spawner.Chunks.BreakCell(gx, gy);
                }
                if (_stateTime < TongueSeconds) break;
                _restFor = Range(RestMin, RestMax);
                Go(FrogState.Rest);
                break;

            case FrogState.Crouch:
                Body.Velocity.X *= 0.6f;
                if (_stateTime < (Mutant ? MutantCrouchSeconds : CrouchSeconds)) break;
                Body.Velocity = _launch;
                if (spawner != null) _hitId = spawner.HitIds.Next();
                Go(FrogState.Hop);
                break;

            case FrogState.Hop:
                // A mutant flies without drag or friction, so its horizontal speed only
                // changes when terrain stops it. Halted by a wall mid-leap, it bursts there.
                if (Mutant && MathF.Abs(_launch.X) > WallHaltMinSpeed
                    && MathF.Abs(Body.Velocity.X) < 0.5f * MathF.Abs(_launch.X))
                {
                    Burst(hitboxes, spawner);
                    return;
                }
                if (!Mutant && MathF.Abs(Body.Velocity.X) < MinAirSpeed) Body.Velocity.X = _facing * MinAirSpeed;
                // Landed (a few frames in, so the launch frame's ground contact doesn't count).
                if ((_stateTime > 0.1f && grounded && Body.Velocity.Y >= -1f) || _stateTime > HopTimeout)
                {
                    _restFor = Mutant ? Range(MutantRestMin, MutantRestMax) : Range(RestMin, RestMax);
                    Go(FrogState.Rest);
                }
                break;

            case FrogState.Split:
                Body.Velocity.X *= 0.6f;
                if (_stateTime < SplitSeconds) break;
                Divide(spawner);
                _splitTimer = Range(SplitMin, SplitMax);
                _restFor    = Range(RestMin, RestMax);
                Go(FrogState.Rest);
                break;

            case FrogState.Stagger:
                // Let the knockback play out; only a slow skid on the ground.
                if (grounded) Body.Velocity.X *= 0.96f;
                if (_stateTime >= StaggerMin &&
                    Body.Velocity.LengthSquared() < StaggerResumeSpeed * StaggerResumeSpeed)
                {
                    _restFor = Mutant ? MutantRestMin : Range(0.2f, RestMin);
                    Go(FrogState.Rest);
                }
                break;
        }
    }

    private void Go(FrogState s) { _state = s; _stateTime = 0f; }

    // Solid just under either foot or the middle.
    private bool Grounded(ChunkMap chunks)
    {
        if (chunks == null) return false;
        var b = Body.Bounds;
        float y = b.Bottom + 1.5f;
        return TileQuery.IsSolidAt(chunks, b.Left + 1f, y)
            || TileQuery.IsSolidAt(chunks, Body.Position.X, y)
            || TileQuery.IsSolidAt(chunks, b.Right - 1f, y);
    }

    private static int CountLive(IEntitySpawner spawner, bool mutantsOnly)
    {
        if (spawner == null) return 0;
        int n = 0;
        foreach (var e in spawner.LiveEntities)
            if (e is FrogEnemy f && !f.IsDead && (!mutantsOnly || f.Mutant)) n++;
        return n;
    }

    // ── Deciding a hop ───────────────────────────────────────────────────────

    private void PlanHop(PlayerCharacter player, IEntitySpawner spawner)
    {
        if (Mutant) { PlanLeap(player, spawner); return; }

        // Crowding: each frog within SpreadRadius pushes away from itself, harder the
        // nearer it is. A frog sitting exactly on this one pushes a random way.
        float push = 0f, crowd = 0f;
        if (spawner != null)
            foreach (var e in spawner.LiveEntities)
            {
                if (e == this || e is not FrogEnemy f || f.IsDead) continue;
                var d = Body.Position - f.Body.Position;
                float dist = d.Length();
                if (dist >= SpreadRadius) continue;
                float w = 1f - dist / SpreadRadius;
                crowd += w;
                push  += MathF.Abs(d.X) > 0.5f ? MathF.Sign(d.X) * w : 0f;
            }

        float r = Roll();
        int dir = MathF.Abs(push) > 0.15f && r < 0.85f ? (push > 0f ? 1 : -1)
                : Roll() < 0.5f ? -1 : 1;
        // A wall two blocks high dead ahead: go the other way. One block is hoppable.
        var chunks = spawner?.Chunks;
        if (chunks != null)
        {
            float ax = Body.Position.X + dir * (Width * 0.5f + 5f);
            if (TileQuery.IsSolidAt(chunks, ax, Body.Position.Y) &&
                TileQuery.IsSolidAt(chunks, ax, Body.Position.Y - Chunk.TileSize))
                dir = -dir;
        }
        // The climbing instinct beats all of that when there is somewhere higher to get to.
        if (chunks != null && FindLedge(chunks, out var ledge, out int ledgeDir, out int gain) && Roll() < ClimbChance)
        {
            _facing = ledgeDir;
            _launch = LeapVelocity(ledge - Body.Position, 0.5f + 0.06f * gain + 0.0025f * MathF.Abs(ledge.X - Body.Position.X));
            return;
        }
        // The more crowded, the further it hops.
        float reach = MathF.Max(Roll(), MathF.Min(1f, crowd * 0.5f));
        _facing = dir;
        _launch = new Vector2(dir * MathHelper.Lerp(HopVxMin, HopVxMax, reach),
                              -MathHelper.Lerp(HopVyMin, HopVyMax, Roll()));
    }

    // The highest standable cell within ClimbLook columns either side that is above this
    // frog's own row by 1..ClimbTiles: open, with solid directly under it. Ties go to the
    // nearer column, then to the right. `ledge` is where the body's centre would rest,
    // raised a little so the hop comes down onto it.
    private bool FindLedge(ChunkMap chunks, out Vector2 ledge, out int dir, out int gain)
    {
        const int ts = Chunk.TileSize;
        int gx0 = (int)MathF.Floor(Body.Position.X / ts), gy0 = (int)MathF.Floor(Body.Position.Y / ts);
        ledge = default; dir = 0; gain = 0;
        for (int k = 1; k <= ClimbLook; k++)
            for (int d = 1; d >= -1; d -= 2)
                for (int g = ClimbTiles; g > gain; g--)
                {
                    int gx = gx0 + d * k, gy = gy0 - g;
                    if (chunks.GetCellState(gx, gy) == TileState.Solid) continue;
                    if (chunks.GetCellState(gx, gy + 1) != TileState.Solid) continue;
                    gain  = g; dir = d;
                    ledge = new Vector2(gx * ts + ts * 0.5f, gy * ts + ts * 0.5f - 3f);
                    break;
                }
        return gain > 0;
    }

    // ── The tongue ───────────────────────────────────────────────────────────
    // A cell the tongue may take: built by a player (BlockOwnership — until the claim
    // fades), and not flush with the ground — it has open air to its left, its right or
    // underneath, so it is part of something standing up or hanging, not of a floor.
    public static bool Edible(ChunkMap chunks, int gx, int gy)
    {
        if (chunks.GetCellState(gx, gy) != TileState.Solid) return false;
        if (!chunks.Ownership.TryGet(gx, gy, chunks.TerrainFrame, out _, out _)) return false;
        return chunks.GetCellState(gx - 1, gy) != TileState.Solid
            || chunks.GetCellState(gx + 1, gy) != TileState.Solid
            || chunks.GetCellState(gx, gy + 1) != TileState.Solid;
    }

    // One edible cell within TongueRange that the tongue can reach in a straight line,
    // chosen uniformly among them (reservoir pick off this frog's stream).
    private bool FindMorsel(ChunkMap chunks, out Vector2 at)
    {
        const int ts = Chunk.TileSize;
        at = default;
        var mouth = Body.Position;
        int reach = (int)MathF.Ceiling(TongueRange / ts);
        int gx0 = (int)MathF.Floor(mouth.X / ts), gy0 = (int)MathF.Floor(mouth.Y / ts);
        int found = 0;
        for (int gx = gx0 - reach; gx <= gx0 + reach; gx++)
            for (int gy = gy0 - reach; gy <= gy0 + reach; gy++)
            {
                if (!Edible(chunks, gx, gy)) continue;
                var c = new Vector2(gx * ts + ts * 0.5f, gy * ts + ts * 0.5f);
                var d = c - mouth;
                float len = d.Length();
                if (len > TongueRange || len < 1f) continue;
                // Straight line: the first solid thing along it must be this cell.
                bool clear = true;
                for (float t = 3f; t < len; t += 3f)
                {
                    var q = mouth + d * (t / len);
                    int qx = (int)MathF.Floor(q.X / ts), qy = (int)MathF.Floor(q.Y / ts);
                    if (qx == gx && qy == gy) break;
                    if (chunks.GetCellState(qx, qy) == TileState.Solid) { clear = false; break; }
                }
                if (!clear) continue;
                found++;
                if (Roll() * found < 1f) at = c;
            }
        return found > 0;
    }

    // A ballistic leap at the nearest player: either onto where they stand now, or onto
    // where their horizontal speed will have put them when the leap comes down (crouch
    // plus flight), off by up to LeapNoise. Only the horizontal speed is extrapolated — a
    // jump's vertical speed says little about where they will be a second later. Out of
    // range, it leaps the full range toward them instead.
    private void PlanLeap(PlayerCharacter player, IEntitySpawner spawner)
    {
        if (spawner != null) player = spawner.NearestPlayer(Body.Position, player);
        var to = player != null ? player.Body.Position - Body.Position : new Vector2(_facing * 60f, 0f);
        bool  lead  = Roll() < LeadChance;
        float noise = (Roll() * 2f - 1f) * LeapNoise;
        if (lead && player != null)
        {
            float eta = MutantCrouchSeconds + LeapTimeBase + MathF.Abs(to.X) * LeapTimePerPx;
            to.X += player.Body.Velocity.X * eta + noise;
        }
        // Further than it can leap (in any direction): the full range along the same line.
        float far = to.Length();
        if (far > MutantRange) to *= MutantRange / far;
        if (MathF.Abs(to.X) > 1f) _facing = to.X > 0f ? 1 : -1;
        float T = LeapTimeBase + MathF.Abs(to.X) * LeapTimePerPx;
        _launch = LeapVelocity(to, T);
    }

    // The launch velocity that covers `to` in `T` seconds under gravity alone.
    public static Vector2 LeapVelocity(Vector2 to, float T)
        => new(to.X / T, (to.Y - 0.5f * Simulation.Gravity.Y * T * T) / T);

    // ── Dividing ─────────────────────────────────────────────────────────────

    private void Divide(IEntitySpawner spawner)
    {
        if (spawner == null || CountLive(spawner, mutantsOnly: false) >= Cap) return;
        bool mutant = Roll() < MutantChance || CountLive(spawner, mutantsOnly: false) > MutantsAbove;
        int  side   = Roll() < 0.5f ? -1 : 1;
        var child = new FrogEnemy(Body.Position, mutant, BrainRandom.Mix(BrainRandom.Next(ref _rng), 1UL)) { Team = Team };
        // The two halves pop apart.
        child.Body.Velocity = new Vector2( side * 70f, -150f);
        Body.Velocity       = new Vector2(-side * 70f, -120f);
        spawner.SpawnEntity(child);
    }

    // ── The mutant's burst ───────────────────────────────────────────────────

    private bool TouchingVictim(HurtboxWorld hurt)
    {
        if (hurt == null) return false;
        var b = Body.Bounds;
        var region = new BoundingBox(b.Left - 2f, b.Top - 2f, b.Right + 2f, b.Bottom + 2f);
        foreach (var hb in hurt.All)
            if (Factions.IsPlayer(hb.Owner) && HitboxWorld.Overlaps(hb.Region, region))
                return true;
        return false;
    }

    private void Burst(HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        var p = Body.Position;
        if (hitboxes != null)
        {
            int id = spawner != null ? spawner.HitIds.Next() : _hitId;
            hitboxes.Publish(new Hitbox(
                new BoundingBox(p.X - BlastCoreHalf, p.Y - BlastCoreHalf, p.X + BlastCoreHalf, p.Y + BlastCoreHalf),
                id, BlastTileDamage, new Vector2(_facing * BlastKnockback * 0.5f, -BlastKnockback),
                Faction, Id, BlastColor, bodyDamage: BlastDamage, origin: p));
            for (int i = 0; i < BlastSegments; i++)
            {
                float a   = i * MathHelper.TwoPi / BlastSegments;
                var   dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                var   sc  = p + dir * (BlastRadius - BlastSegHalf);
                hitboxes.Publish(new Hitbox(
                    new BoundingBox(sc.X - BlastSegHalf, sc.Y - BlastSegHalf, sc.X + BlastSegHalf, sc.Y + BlastSegHalf),
                    id, BlastTileDamage, dir * BlastKnockback,
                    Faction, Id, BlastColor, bodyDamage: BlastDamage, origin: p));
            }
        }
        spawner?.NotifyChargedBlast(Id, p, BlastRadius);   // cosmetic: particles + sound
        _exploded     = true;
        Body.Velocity = Vector2.Zero;
        GravityScale  = 0f;
    }

    // ── Drawing (render-only) ────────────────────────────────────────────────
    // A squat block with two eyes on top and folded legs; it squashes into a crouch,
    // stretches in the air, and swells with a seam down the middle before dividing.
    public void Telegraph(TelegraphList t)
    {
        if (IsDead) return;
        var c = Body.Position;
        if (_exploded) { t.Disc(c, BlastRadius, BlastColor * 0.45f); return; }

        var   body  = Mutant ? MutantColor : FrogColor;
        var   belly = Mutant ? MutantBelly : FrogBelly;
        float sx = 1f, sy = 1f;            // squash / stretch
        switch (_state)
        {
            case FrogState.Rest:
                sy = 1f + 0.05f * MathF.Sin(_stateTime * 5f);
                break;
            case FrogState.Crouch:
            {
                float k = MathHelper.Clamp(_stateTime / (Mutant ? MutantCrouchSeconds : CrouchSeconds), 0f, 1f);
                sx = 1f + 0.25f * k; sy = 1f - 0.4f * k;
                break;
            }
            case FrogState.Hop:
                sx = 0.85f; sy = 1.2f;
                break;
            case FrogState.Split:
            {
                float k = MathHelper.Clamp(_stateTime / SplitSeconds, 0f, 1f);
                sx = 1f + 0.75f * k + 0.06f * MathF.Sin(_stateTime * 40f);
                sy = 1f + 0.1f * k;
                break;
            }
            case FrogState.Stagger:
                sx = 1.1f; sy = 0.85f;
                break;
        }

        float w = Width * sx, h = Height * sy;
        float baseY = c.Y + Height * 0.5f;
        var   mid   = new Vector2(c.X, baseY - h * 0.5f);
        float top   = baseY - h;

        // Legs first, so the body sits over their roots.
        bool air = _state == FrogState.Hop;
        for (int s = -1; s <= 1; s += 2)
        {
            var hip = new Vector2(c.X + s * w * 0.4f, baseY - 2f);
            if (air)
            {
                var knee = hip + new Vector2(-_facing * 4f + s * 1.5f, 4f);
                t.Line(hip, knee, body, 2f);
                t.Line(knee, knee + new Vector2(-_facing * 3f, 3.5f), body, 1.5f);
            }
            else
            {
                var knee = hip + new Vector2(s * 3.5f, -3f);
                t.Line(hip, knee, body, 2f);
                t.Line(knee, new Vector2(knee.X + s * 1.5f, baseY), body, 1.5f);
            }
        }

        t.Rect(mid, new Vector2(w, h), body);
        t.Box(c.X - w * 0.5f + 2f, baseY - h * 0.4f, w - 4f, h * 0.4f - 1f, belly * 0.85f);

        // Eyes: two bumps on top, pupils toward the facing.
        for (int s = -1; s <= 1; s += 2)
        {
            var eye = new Vector2(c.X + s * w * 0.3f, top);
            t.Disc(eye, 2.4f, body);
            t.Disc(eye, 1.6f, Mutant ? new Color(255, 235, 90) : Color.White);
            t.Disc(eye + new Vector2(_facing * 0.7f, 0f), 0.8f, Color.Black);
        }

        if (_state == FrogState.Tongue)
        {
            // Out to the block, then back with it.
            float k = _stateTime < TongueOut ? _stateTime / TongueOut
                    : 1f - (_stateTime - TongueOut) / (TongueSeconds - TongueOut);
            k = MathHelper.Clamp(k, 0f, 1f);
            var mouth = new Vector2(c.X + _facing * w * 0.4f, baseY - h * 0.55f);
            var tip   = Vector2.Lerp(mouth, _launch, k);
            var pink  = new Color(245, 120, 150);
            t.Line(mouth, tip, pink, 2f);
            t.Disc(tip, 2.2f, pink);
            if (_stateTime >= TongueOut) t.Rect(tip, 5f * (0.4f + 0.6f * k), new Color(150, 130, 110));
        }

        if (_state == FrogState.Split)
        {
            float k = MathHelper.Clamp(_stateTime / SplitSeconds, 0f, 1f);
            t.Line(new Vector2(c.X, top + 1f), new Vector2(c.X, baseY), Color.Lerp(body, Color.Black, 0.6f) * k, 1f + k);
        }

        if (!Mutant) return;

        // Back spikes.
        for (int i = -1; i <= 1; i++)
        {
            var root = new Vector2(c.X + i * w * 0.22f - _facing * 1.5f, top + 1f);
            t.Line(root, root + new Vector2(-_facing * 2f + i, -4f), MutantBelly, 1.5f);
        }

        // The crouch shows the leap: a pulsing ring, and the arc it is about to fly as a
        // dashed line.
        if (_state == FrogState.Crouch)
        {
            float k = MathHelper.Clamp(_stateTime / MutantCrouchSeconds, 0f, 1f);
            t.Ring(c, 9f + 5f * (1f - k), BlastColor * (0.35f + 0.5f * k), 12, 1.5f);
            const float step = 1f / 30f;
            var p = c; var v = _launch;
            for (int i = 0; i < 60; i++)
            {
                v.Y += Simulation.Gravity.Y * step;
                var next = p + v * step;
                if (i % 2 == 1) t.Line(p, next, BlastColor * (0.3f + 0.4f * k), 1f);
                p = next;
                if (v.Y > 0f && p.Y > c.Y + 60f) break;
            }
        }
        else if (_state == FrogState.Hop)
        {
            float pulse = 0.5f + 0.5f * MathF.Sin(_stateTime * 30f);
            t.Ring(c, 9f + pulse, BlastColor * (0.4f + 0.4f * pulse), 10, 1f);
            // The rest of the flight, from where it is now.
            const float step = 1f / 30f;
            var p = c; var v = Body.Velocity;
            for (int i = 0; i < 60; i++)
            {
                v.Y += Simulation.Gravity.Y * step;
                var next = p + v * step;
                if (i % 2 == 1) t.Line(p, next, BlastColor * 0.6f, 1f);
                p = next;
                if (v.Y > 0f && p.Y > c.Y + 60f) break;
            }
        }
    }
}
