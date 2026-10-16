using System;
using Microsoft.Xna.Framework;

namespace MTile;

// ── THE RAY EMITTER ─────────────────────────────────────────────────────────
// A rooted node that projects a cone-shaped ray a very long way in one locked
// direction. The Prism boss plants them (PrismPlantAction); nothing stops another
// caster from doing the same with different numbers.
//
//   * Lifecycle: [0, Telegraph) the cone is shown as a silhouette and does nothing;
//     [Telegraph, Life) it fires; at Life it EXPLODES (a disc blast around the node)
//     and is gone. Killing it early — Health is a real pool, not a tripwire — removes
//     it quietly: no blast. The lifetime is managed here rather than through
//     Projectile.Lifetime precisely so the end of life is an event, not a sweep.
//   * The cone is a fan of narrow wedges (~WedgeDegrees each). Each wedge's centre
//     line is marched through the terrain every frame, ZeusBeam-style, and the wedge
//     reaches exactly as far as the first solid cell on that line. So a block IS
//     cover: the part of the cone behind it is simply not published.
//   * …but cover is spent, not held. The cell that stops a wedge takes
//     TileDamagePerSecond, through a TilesOnly hitbox rather than a direct DamageCell —
//     the same channel every other attack uses, so OnTileBroken fires and the journal
//     records it. Sand and foam go in under half a second, dirt in under two; stone
//     (12 HP) outlasts the emitter's life, so a stone block is a real wall and a dirt
//     one is a delay. The cells are deduped per frame so a wall close to the
//     root (where many wedges land on one cell) isn't eaten WedgeCount times faster.
//   * Bodies are hit through one triangle polygon per wedge (EntitiesOnly, occluded
//     from the root). The ray is continuous, so the HitId rolls every TickSeconds:
//     CombatSystem's per-HitId dedupe then lands one hit per tick on whoever stands
//     in it.
//   * Everything that varies rides EntityData (angle/half-angle in Aim, range /
//     telegraph / life in the mine slots, the owner in LinkedId, the tick + hit id +
//     exploded flag). The per-wedge reach is recomputed from the terrain every Update
//     before it is used, so it is a per-frame derivation, not state — which is also
//     why Telegraph can read it: it is exactly what this frame's hitboxes were.
public sealed class RayEmitter : Projectile, ITelegraphSource
{
    public const float BodyRadius = 7f;
    public const float MaxHp      = 4f;        // eight stock slashes — tough, not immortal

    // The fan.
    public const float WedgeDegrees        = 3f;
    public const int   MaxWedges           = 24;
    public const float TickSeconds         = 0.30f;
    public const float BodyDamagePerTick   = 0.35f;
    public const float TileDamagePerSecond = 1.2f;
    public const float Knockback           = 150f;
    public const float Hitstun             = 0.06f;

    // The end-of-life blast.
    public const float BlastRadius     = 55f;
    public const float BlastDamage     = 1.5f;
    public const float BlastTileDamage = 2.0f;
    public const float BlastKnockback  = 380f;

    private readonly float _angle, _halfAngle, _range, _telegraph, _life;
    private int  _hitId;
    private int  _tick = -1;
    private bool _exploded;

    // Per-frame derivation (see the header): wedge count, each wedge's reach, and the
    // cell that stopped it (int.MinValue = none).
    private int _wedges;
    private readonly float[] _reach = new float[MaxWedges];
    private readonly int[]   _stopX = new int[MaxWedges];
    private readonly int[]   _stopY = new int[MaxWedges];

    public EntityId Owner     { get; }
    public float    Angle     => _angle;
    public float    HalfAngle => _halfAngle;
    public float    Range     => _range;
    public bool     Firing    => Age >= _telegraph && !_exploded;
    public float    LifeLeft  => MathF.Max(0f, _life - Age);
    public override EntityKind Kind => EntityKind.RayEmitter;

    public RayEmitter(Vector2 pos, EntityId owner, int team,
                      float angle, float halfAngle, float range, float telegraph, float life)
        : base(new PhysicsBody(Polygon.CreateRegular(BodyRadius, 6), pos),
               health: MaxHp, lifetime: 0f, owner: Faction.Enemy)
    {
        Owner        = owner;
        Team         = team;
        _angle       = angle;
        _halfAngle   = MathF.Max(halfAngle, MathHelper.ToRadians(0.5f));
        _range       = range;
        _telegraph   = telegraph;
        _life        = life;
        Rooted       = true;
        GravityScale = 0f;
        Mass         = 6f;
        Color        = Idle;
        Sprite       = Sprites.RayEmitter(BodyRadius);
    }

    private static readonly Color Idle = new(120, 90, 200);
    private static readonly Color Hot  = new(255, 90, 200);
    private static readonly Color Core = new(255, 235, 250);

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.Aim        = new Vector2(_angle, _halfAngle);
        s.MineRadius = _range;
        s.MineArm    = _telegraph;
        s.MineFuse   = _life;
        s.LinkedId   = Owner;
        s.HitId      = _hitId;
        s.Budget     = _tick;
        s.Exploded   = _exploded;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _hitId    = s.HitId;
        _tick     = s.Budget;
        _exploded = s.Exploded;
    }

    public static RayEmitter FromState(in EntityData d, Vector2 pos)
        => new(pos, d.LinkedId, d.Team, d.Aim.X, d.Aim.Y, d.MineRadius, d.MineArm, d.MineFuse);

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        // The blast was published last frame; CombatSystem has resolved it.
        if (_exploded) { Health = 0f; return; }

        ComputeReach(spawner?.Chunks);
        Color = Firing ? Hot : Idle;

        if (Age >= _life) { Explode(hitboxes, spawner); return; }
        if (Age < _telegraph || hitboxes == null || spawner == null) return;

        int tick = (int)MathF.Floor((Age - _telegraph) / TickSeconds);
        if (tick != _tick) { _tick = tick; _hitId = spawner.HitIds.Next(); }

        PublishRay(hitboxes, dt);
    }

    // ── Geometry ────────────────────────────────────────────────────────────
    private float WedgeStep => (2f * _halfAngle) / _wedges;
    private float WedgeCentre(int i) => _angle - _halfAngle + (i + 0.5f) * WedgeStep;
    private static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));

    private void ComputeReach(ChunkMap chunks)
    {
        _wedges = Math.Clamp((int)MathF.Ceiling(2f * _halfAngle / MathHelper.ToRadians(WedgeDegrees)), 1, MaxWedges);
        var root = Body.Position;
        for (int i = 0; i < _wedges; i++)
        {
            _reach[i] = _range;
            _stopX[i] = int.MinValue;
            _stopY[i] = int.MinValue;
            if (chunks == null) continue;
            var dir = Dir(WedgeCentre(i));
            // Half-tile steps so no cell is skipped; starts outside the node's own body.
            const float Step = Chunk.TileSize * 0.5f;
            int lastX = int.MinValue, lastY = int.MinValue;
            for (float d = BodyRadius + Step; d <= _range; d += Step)
            {
                var p = root + dir * d;
                int gtx = (int)MathF.Floor(p.X / Chunk.TileSize), gty = (int)MathF.Floor(p.Y / Chunk.TileSize);
                if (gtx == lastX && gty == lastY) continue;
                lastX = gtx; lastY = gty;
                if (chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
                _reach[i] = d;          // the stopping cell is included: the wedge touches the wall it hit
                _stopX[i] = gtx;
                _stopY[i] = gty;
                break;
            }
        }
    }

    private void PublishRay(HitboxWorld hitboxes, float dt)
    {
        var root = Body.Position;
        float step = WedgeStep;
        for (int i = 0; i < _wedges; i++)
        {
            float r = _reach[i];
            if (r < 2f) continue;
            float a0 = _angle - _halfAngle + i * step, a1 = a0 + step, ac = a0 + 0.5f * step;
            var poly = new Polygon(new[] { Vector2.Zero, Dir(a0) * r, Dir(a1) * r });
            hitboxes.Publish(new Hitbox(
                poly.GetBoundingBox(root), _hitId, BodyDamagePerTick,
                Dir(ac) * Knockback,
                Faction.Enemy, Id, Hot,
                targets: HitTargets.EntitiesOnly,
                shape: poly, shapePos: root,
                hitstunSecondsOverride: Hitstun,
                origin: root));

            // The cell that stopped this wedge, once per frame however many wedges hit it.
            int gtx = _stopX[i], gty = _stopY[i];
            if (gtx == int.MinValue) continue;
            bool dup = false;
            for (int j = 0; j < i; j++) if (_stopX[j] == gtx && _stopY[j] == gty) { dup = true; break; }
            if (dup) continue;
            const float ts = Chunk.TileSize;
            hitboxes.Publish(new Hitbox(
                new BoundingBox(gtx * ts + 1f, gty * ts + 1f, (gtx + 1) * ts - 1f, (gty + 1) * ts - 1f),
                _hitId, TileDamagePerSecond * dt, Vector2.Zero,
                Faction.Enemy, Id, Core, targets: HitTargets.TilesOnly));
        }
    }

    private void Explode(HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        var c = Body.Position;
        if (hitboxes != null)
        {
            // A disc: a centre box plus a ring of eight overlapping pieces, each pushing
            // outward (the centre one straight up). Same construction as FighterMine.
            float inner = BlastRadius * 0.45f;
            PublishBlast(hitboxes, c, inner, new Vector2(0f, -BlastKnockback));
            float ring = BlastRadius * 0.6f, half = BlastRadius * 0.4f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathHelper.TwoPi / 8f;
                var dir = Dir(a);
                PublishBlast(hitboxes, c + dir * ring, half, dir * BlastKnockback);
            }
        }
        spawner?.NotifyChargedBlast(Id, c, BlastRadius);
        _exploded = true;
    }

    private void PublishBlast(HitboxWorld hitboxes, Vector2 centre, float half, Vector2 knockback)
        => hitboxes.Publish(new Hitbox(
               new BoundingBox(centre.X - half, centre.Y - half, centre.X + half, centre.Y + half),
               _hitId, BlastTileDamage, knockback, Faction.Enemy, Id, Color.OrangeRed,
               targets: HitTargets.All, bodyDamage: BlastDamage, origin: Body.Position));

    // ── Telegraph ───────────────────────────────────────────────────────────
    // Silhouette while charging: the cone's two edges, a thin fan, and the front where
    // each wedge stops, all brightening into a strobe. Firing: each wedge filled as a
    // run of rotated strips that widen with distance (a triangle out of rectangles),
    // a bright core line, sparks where the ray meets rock, and the blast ring rising
    // over the last second of life.
    public void Telegraph(TelegraphList t)
    {
        if (IsDead || _exploded || _wedges == 0) return;
        var root = Body.Position;
        float step = WedgeStep;

        if (Age < _telegraph)
        {
            float p   = _telegraph > 0f ? Age / _telegraph : 1f;
            var   col = Color.Lerp(Idle, Hot, p);
            bool strobe = p > 0.75f && (int)(Age * 24f) % 2 == 0;
            for (int i = 0; i < _wedges; i++)
                t.Line(root, root + Dir(WedgeCentre(i)) * _reach[i], col * (0.10f + 0.25f * p + (strobe ? 0.2f : 0f)));
            Edges(t, col * (0.45f + 0.55f * p), 1.5f);
            Front(t, col * (0.3f + 0.6f * p), 1f);
            t.Ring(root, BodyRadius + 3f + 6f * p, col * (0.4f + 0.6f * p), 12, 1f + p);
            return;
        }

        float flicker = ZeusBeam.Hash01((int)(Age * 60f), Id.Index) * 0.25f;
        float fade    = MathHelper.Clamp(LifeLeft / 0.6f, 0.35f, 1f);   // thins out at the end
        for (int i = 0; i < _wedges; i++)
        {
            float r = _reach[i];
            if (r < 2f) continue;
            float ac = WedgeCentre(i);
            var   dir = Dir(ac);
            int   strips = Math.Clamp((int)(r / 22f), 1, 24);
            float len = r / strips;
            for (int k = 0; k < strips; k++)
            {
                float d = (k + 0.5f) * len;
                float w = 2f * d * MathF.Tan(step * 0.5f) + 1.5f;
                t.RotatedRect(root + dir * d, new Vector2(len + 1f, w * fade), ac, Hot * (0.22f + flicker));
            }
            t.Line(root, root + dir * r, Core * (0.35f + flicker), 1f);
            if (_stopX[i] != int.MinValue)
                t.Disc(root + dir * r, 2.5f + 3f * flicker * 4f, Core * 0.85f);
        }
        Edges(t, Hot * (0.7f + flicker), 1.5f);
        t.Ring(root, BodyRadius + 2f, Core * (0.5f + flicker), 12, 1.5f);

        // The fuse: the blast ring rises over the last second.
        float late = MathHelper.Clamp(1f - LifeLeft, 0f, 1f);
        if (late > 0f)
            t.Ring(root, BlastRadius, Color.OrangeRed * (0.2f + 0.6f * late), 20, 1f + late);
    }

    private void Edges(TelegraphList t, Color c, float thickness)
    {
        var root = Body.Position;
        t.Line(root, root + Dir(_angle - _halfAngle) * _reach[0],           c, thickness);
        t.Line(root, root + Dir(_angle + _halfAngle) * _reach[_wedges - 1], c, thickness);
    }

    private void Front(TelegraphList t, Color c, float thickness)
    {
        var root = Body.Position;
        float step = WedgeStep;
        var prev = root + Dir(_angle - _halfAngle) * _reach[0];
        for (int i = 0; i < _wedges; i++)
        {
            var next = root + Dir(_angle - _halfAngle + (i + 1) * step) * _reach[i];
            t.Line(prev, next, c, thickness);
            prev = next;
        }
    }
}

// What a brain (and the plant action's own cap) sees of a ray emitter. Owner is the
// entity that planted it.
public struct RayView
{
    public Vector2  Position;
    public float    Angle, HalfAngle, Range;
    public bool     Firing;
    public float    LifeLeft;
    public EntityId Owner;
    public int      Team;
}
