using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The block-grab pulling point (Plans/BLOCK_THROW_PLAN.md §4.3). Spawned by
// BlockGrabAction on the Shift+LMB press; the action DRIVES it while the button is
// held (writes TargetPos/OwnerPos each frame, reads a summary back) and HANDS IT OFF
// on release with the cursor's velocity. Everything that must outlive the button — the
// peel group, the pull contest — lives here rather than in the action, so a follow-up
// action pressed right after the release can neither be eaten by the grab's priority
// lock nor kill a result the player already perceives as committed.
//
// Its Body IS the point: IgnoreTiles, weightless, no hurtbox (nobody can slash the
// cursor, and force fields act on the hurtbox set). While driven the action sets
// Body.Position outright; after hand-off Body.Velocity carries the swipe and physics
// integrates the straight line for free — no drag (§4.4), so a ball that had to chase
// from the crater detaches at the same speed as one that was already in hand.
//
// The BALL is not modelled here (§4.7): at break-out the point spawns a
// LobbedAreaProjectile in its Tracking phase — a body of its own that follows this
// point with a velocity-matching tracker — and keeps only its id (LinkedId). The ball
// owns its dissipation and the detach decision; the point dies once the ball reports
// detached (or is gone), on snap, or at GrabPointMaxSeconds after hand-off.
//
// PEEL MECHANICS (ported verbatim from BlockGrabAction; BlockPeelEnabled). Paint and
// pull are ONE phase, and the gaussian paint kernel is itself the mode switch. While
// the target sweeps over terrain it deposits "tether" onto nearby solid cells (they
// join the group, cap PeelMemberBuffer.Capacity); because the target is near the
// group, the target→group spring — force superlinear in |target − group COM| — is
// slack. Sweep AWAY and the kernel stops reaching terrain while the spring ramps: the
// pull. Each frame the spring force is divided among members by tether share; that
// share erodes both the group→block tether (at zero the block drops from the group,
// staying in the world) and the block→world glue (weight(material) × (core + outward
// solid edges)). When the force beats the group's aggregate remaining glue, every
// member is broken out at once and collapses into the ball. Pull harder than
// PeelSpringMax and the spring SNAPS — the whole attempt cancels and the point dies.
// After hand-off the spring endpoint is the flying point and nothing paints.
//
// The group is a sparse snapshotted component (PeelGroupComp) marshalled in the
// CaptureState/RestoreState overrides; the scalars ride EntityData like any entity.
public sealed class PullPointEntity : Entity, ITelegraphSource
{
    // Reach from the owner's body center, in tiles so it tracks Chunk.TileSize like the
    // rest of the terrain verbs. BlockGrabAction/GrabAction gate the press on it too.
    public const float GrabReach = Chunk.TileSize * 6f;
    // Harvest radius around the press site for the legacy drag-rip. 1.6 tiles ⇒ the
    // pressed cell plus its immediate neighbours, ~9 blocks on open ground.
    private const float LegacyRipRadiusTiles = 1.6f;
    // Width of the material tally, from the enum's own cardinality.
    private const int TileTypeCount = TileTypes.Count;

    public override EntityKind Kind => EntityKind.PullPoint;

    // ── Driven-phase inputs, written by the owning action every held frame ──────────
    public bool    Driven = true;
    public Vector2 TargetPos;   // kernel center / spring endpoint (the cursor, or the held rest position)
    public Vector2 OwnerPos;    // reach origin

    public TileType OrbType;    // material seed for the ball (press-time block type; dominant harvest after)
    public int      HarvestBlocks;  // blocks taken at break-out — >0 ⇔ this grab took something (recovery gate)
    public int      ChargedBlocks;  // how many of those were charged tiles — the ball's blast scaling
    public EntityId BallId;     // the ball spawned at break-out, EntityId.None until then
    public float    HandoffTime;   // seconds since the action released the point

    private PeelGroupComp _group;

    // Read-only summary for the action / tests.
    public bool  Snapped    => _group.Snapped;
    public int   PeelCount  => _group.Count;
    public float PeelStrain => _group.Strain;
    public bool  HasBall    => BallId.Index > 0;
    public PeelGroupComp Group => _group;   // struct copy — for test probes

    public PullPointEntity(Vector2 pos, Faction owner, TileType blockType)
        : base(new PhysicsBody(Polygon.CreateRegular(2f, 4), pos), health: 1f)
    {
        Faction          = owner;
        OrbType          = blockType;
        TargetPos        = pos;
        OwnerPos         = pos;
        Body.IgnoreTiles = true;
        GravityScale     = 0f;
        Mass             = 0f;                       // immovable to knockback (never hit anyway)
        Color            = Color.Transparent;
        Sprite           = new Sprite { Visible = false };   // no polygon-outline fallback either
    }

    // Not a target: no hurtbox, so neither hits nor force fields ever find it.
    public override void PublishHurtboxes(HurtboxWorld world) { }

    public void Kill() => Health = 0f;

    // Hand-off: the action lets go. The point flies on at the cursor's velocity; any
    // ball in hand is already an entity chasing it, and any unresolved contest keeps
    // running against the flying endpoint.
    public void Release(Vector2 swipeVel)
    {
        Driven        = false;
        Body.Velocity = swipeVel;
        HandoffTime   = 0f;
    }

    // Legacy drag-rip (BlockPeelEnabled false): destroy every solid cell within
    // LegacyRipRadiusTiles of the press site and bank the count. BreakCell (not
    // DamageCell) because a grab takes the whole block. The dominant material becomes
    // the ball's type, so a dig through mixed ground throws back whatever it was mostly
    // made of. Returns false when nothing solid was left at the site.
    public bool RipBlocks(IEntitySpawner spawner, Vector2 site)
    {
        var chunks = (spawner as IChunkProvider)?.Chunks;
        if (chunks == null) return false;
        int  cx   = (int)MathF.Floor(site.X / Chunk.TileSize);
        int  cy   = (int)MathF.Floor(site.Y / Chunk.TileSize);
        int  span = (int)MathF.Ceiling(LegacyRipRadiusTiles);
        float r2  = LegacyRipRadiusTiles * LegacyRipRadiusTiles;

        // Fixed array indexed by TileType rather than a Dictionary: no per-frame
        // allocation on the sim path, and a fixed winner-scan order.
        Span<int> counts = stackalloc int[TileTypeCount];
        int taken = 0, charged = 0;
        for (int dy = -span; dy <= span; dy++)
        for (int dx = -span; dx <= span; dx++)
        {
            if (dx * dx + dy * dy > r2) continue;
            int gtx = cx + dx, gty = cy + dy;
            if (chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
            var type = chunks.GetCellType(gtx, gty);
            if (!TileTypes.IsGrabbable(type)) continue;   // hardened rock isn't harvestable
            // Read the charge BEFORE the break — BreakCell clears the flag, so asking
            // afterwards always says no.
            bool wasCharged = chunks.Charge.IsCharged(gtx, gty);
            if (!chunks.BreakCell(gtx, gty)) continue;
            counts[(int)type]++;
            if (wasCharged) charged++;
            taken++;
        }
        if (taken == 0) return false;
        SpawnBall(spawner, site, taken, DominantType(counts), charged);
        return true;
    }

    private TileType DominantType(Span<int> counts)
    {
        var best = OrbType;
        int bestN = 0;
        for (int t = 0; t < TileTypeCount; t++)
            if (counts[t] > bestN) { bestN = counts[t]; best = (TileType)t; }
        return best;
    }

    // The harvest becomes a ball: a LobbedAreaProjectile born at rest where the blocks
    // were, tracking this point. Same call whether the point is still in hand or
    // already flying — that is the uniformity between "release with the clod in hand"
    // and "release while the blocks are still coming loose", in code.
    private void SpawnBall(IEntitySpawner spawner, Vector2 at, int blocks, TileType type, int charged)
    {
        OrbType       = type;
        HarvestBlocks = blocks;
        ChargedBlocks = charged;
        var ball = LobbedAreaProjectile.MakeTracking(at, blocks, type, spawner.HitIds.Next(), Faction, Id, charged);
        spawner.SpawnEntity(ball);
        BallId = ball.Id;
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        var cfg = MovementConfig.Current;

        if (HasBall)
        {
            // The ball owns its life from here: it bleeds while held, chases after
            // hand-off, and detaches. This point exists only as its target.
            var ball = spawner.Resolve(BallId) as LobbedAreaProjectile;
            if (ball == null || ball.IsDead || !ball.Tracking) Health = 0f;
            return;
        }

        var chunks = (spawner as IChunkProvider)?.Chunks;

        if (!Driven)
        {
            // Released with the blocks still in the ground: the contest finishes
            // against the flying point (T5 — "drag and release in one motion"). No
            // painting; the spring endpoint is wherever the point has flown to, so the
            // force ramps as it recedes and the group either comes free — spawning the
            // same tracking ball — or the spring snaps. A hard cap bounds the wait.
            HandoffTime += dt;
            if (HandoffTime >= cfg.GrabPointMaxSeconds || _group.Count == 0
                || !cfg.BlockPeelEnabled || chunks == null)
            {
                Health = 0f;
                return;
            }
            TargetPos = Body.Position;
            UpdatePeel(chunks, spawner, dt, paint: false);
            return;
        }

        if (!cfg.BlockPeelEnabled) return;   // legacy: the action rips
        if (chunks == null) return;
        UpdatePeel(chunks, spawner, dt, paint: true);
    }

    // One frame of the paint/pull phase. Order is fixed and load-bearing for
    // determinism: prune → paint → spring → wear → compact → break-out, with every scan
    // in ascending index / row-major cell order. `paint` is off after hand-off: the
    // flying point pulls but admits nothing.
    private void UpdatePeel(ChunkMap chunks, IEntitySpawner spawner, float dt, bool paint)
    {
        var cfg = MovementConfig.Current;

        // 1. Cells broken out from under us (another player, decay) leave the group.
        for (int i = _group.Count - 1; i >= 0; i--)
            if (chunks.GetCellState(_group.Members[i].Gtx, _group.Members[i].Gty) != TileState.Solid)
                RemoveMember(i);

        // 2. Paint: deposit tether on solid cells under the kernel.
        if (paint) PaintTether(chunks, cfg, dt);

        if (_group.Count == 0) { _group.Strain = 0f; return; }

        // 3. The target→group spring, superlinear in target distance from the COM.
        var com = Vector2.Zero;
        for (int i = 0; i < _group.Count; i++)
            com += CellCenter(_group.Members[i].Gtx, _group.Members[i].Gty);
        com /= _group.Count;

        float dist  = (TargetPos - com).Length();
        float force = cfg.PeelSpringCoeff * MathF.Pow(dist / Chunk.TileSize, cfg.PeelSpringPower);
        _group.Strain = Math.Clamp(force / MathF.Max(1e-3f, cfg.PeelSpringMax), 0f, 1f);

        if (force > cfg.PeelSpringMax)
        {
            // Pulled harder than the grip holds: the spring snaps and the whole
            // attempt dies. Nothing persists — glue wear resets with the group.
            _group.Snapped = true;
            _group.Count   = 0;
            Health = 0f;
            return;
        }

        // 4. Divide the force among members by tether share; each share erodes that
        // member's tether AND its world glue.
        float tetherSum = 0f;
        for (int i = 0; i < _group.Count; i++) tetherSum += _group.Members[i].Tether;
        if (tetherSum <= 1e-6f) return;

        for (int i = 0; i < _group.Count; i++)
        {
            ref var m = ref _group.Members[i];
            float share = force * m.Tether / tetherSum;
            m.Tether   -= cfg.PeelTetherWear * share * dt;
            m.GlueWear += cfg.PeelGlueWear  * share * dt;
        }

        // 5. Members whose tether wore through drop off — the block stays in the world.
        for (int i = _group.Count - 1; i >= 0; i--)
            if (_group.Members[i].Tether <= 0f)
                RemoveMember(i);
        if (_group.Count == 0) return;

        // 6. Aggregate remaining glue of the survivors vs the pull. Glue base is
        // recomputed live (neighbors join the group / get broken), floored so an
        // oversized group stays unliftable no matter how long it's worked.
        float glueTotal = 0f;
        for (int i = 0; i < _group.Count; i++)
        {
            ref var m = ref _group.Members[i];
            float baseGlue = BaseGlue(chunks, m.Gtx, m.Gty, cfg);
            glueTotal += MathF.Max(baseGlue * cfg.PeelGlueFloor, baseGlue - m.GlueWear);
        }

        if (force >= glueTotal)
            BreakOutGroup(chunks, spawner, com);
    }

    // Gaussian deposit around the target. Admission and accumulation share the kernel:
    // a cell is admitted when the kernel weight over it reaches PeelJoinThreshold (a
    // real pass, not a graze at the skirt), and every member under the kernel keeps
    // accumulating — "time spent over the block, weighted by a fast-die-off kernel".
    // Cells beyond GrabReach of the owner never join (same arm's-reach rule as the
    // press gate), and a full buffer admits nobody: paint deliberately.
    private void PaintTether(ChunkMap chunks, MovementConfig cfg, float dt)
    {
        var   cursor = TargetPos;
        float sigma  = MathF.Max(1f, cfg.PeelKernelSigma);
        float extent = 2.5f * sigma;
        float inv2s2 = 1f / (2f * sigma * sigma);

        int cx   = (int)MathF.Floor(cursor.X / Chunk.TileSize);
        int cy   = (int)MathF.Floor(cursor.Y / Chunk.TileSize);
        int span = (int)MathF.Ceiling(extent / Chunk.TileSize);

        for (int dy = -span; dy <= span; dy++)
        for (int dx = -span; dx <= span; dx++)
        {
            int gtx = cx + dx, gty = cy + dy;
            if (chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
            // Hardened rock takes no tether at all, so it can neither join the group nor
            // be dragged along inside one. Rejecting it here (rather than giving it a
            // huge glue) is what makes the kernel paint straight over a hardened seam
            // and lift only the soft material around it.
            if (!TileTypes.IsGrabbable(chunks.GetCellType(gtx, gty))) continue;

            var center = CellCenter(gtx, gty);
            float r2 = (center - cursor).LengthSquared();
            if (r2 > extent * extent) continue;
            if ((center - OwnerPos).LengthSquared() > GrabReach * GrabReach) continue;

            float weight = MathF.Exp(-r2 * inv2s2);
            int idx = FindMember(gtx, gty);
            if (idx < 0)
            {
                if (weight < cfg.PeelJoinThreshold) continue;        // skirt graze — no admission
                if (_group.Count >= PeelMemberBuffer.Capacity) continue;
                idx = _group.Count++;
                _group.Members[idx] = new PeelMember { Gtx = gtx, Gty = gty };
            }
            _group.Members[idx].Tether += cfg.PeelTetherRate * weight * dt;
        }
    }

    // Block→world attachment: weight(material) × (core + Σ outward edges), where an
    // outward edge is a solid neighbor OUTSIDE the group — 1 for same material,
    // PeelCrossMaterialEdge for different. Edges into the group don't anchor (the
    // group moves as one), which is what makes painting a block's neighbors loosen it.
    private float BaseGlue(ChunkMap chunks, int gtx, int gty, MovementConfig cfg)
    {
        var  myType = chunks.GetCellType(gtx, gty);
        float edges = 0f;
        Span<int> nx = stackalloc int[4] { gtx, gtx + 1, gtx, gtx - 1 };
        Span<int> ny = stackalloc int[4] { gty - 1, gty, gty + 1, gty };
        for (int k = 0; k < 4; k++)
        {
            if (chunks.GetCellState(nx[k], ny[k]) != TileState.Solid) continue;
            if (FindMember(nx[k], ny[k]) >= 0) continue;
            edges += chunks.GetCellType(nx[k], ny[k]) == myType ? 1f : cfg.PeelCrossMaterialEdge;
        }
        return cfg.PeelWeight(myType) * (cfg.PeelGlueCore + edges);
    }

    // The pull beat the glue: every member breaks out at once and collapses into the
    // ball, born at the group's COM — count is the throw budget, dominant material the
    // ball's type.
    private void BreakOutGroup(ChunkMap chunks, IEntitySpawner spawner, Vector2 com)
    {
        Span<int> counts = stackalloc int[TileTypeCount];
        int taken = 0, charged = 0;
        for (int i = 0; i < _group.Count; i++)
        {
            ref var m = ref _group.Members[i];
            var type = chunks.GetCellType(m.Gtx, m.Gty);
            // Charge is read before the break for the same reason as in RipBlocks:
            // BreakCell clears it. A charged tile peeled into the clod is what turns
            // the throw from a splat into a demolition charge (LobbedAreaProjectile).
            bool wasCharged = chunks.Charge.IsCharged(m.Gtx, m.Gty);
            if (!chunks.BreakCell(m.Gtx, m.Gty)) continue;
            counts[(int)type]++;
            if (wasCharged) charged++;
            taken++;
        }
        _group.Count  = 0;
        _group.Strain = 0f;
        if (taken == 0) return;
        SpawnBall(spawner, com, taken, DominantType(counts), charged);
    }

    private int FindMember(int gtx, int gty)
    {
        for (int i = 0; i < _group.Count; i++)
            if (_group.Members[i].Gtx == gtx && _group.Members[i].Gty == gty) return i;
        return -1;
    }

    // Order-preserving removal (shift the tail down), so member iteration order is
    // identical before and after a rollback restore.
    private void RemoveMember(int index)
    {
        for (int i = index; i < _group.Count - 1; i++)
            _group.Members[i] = _group.Members[i + 1];
        _group.Count--;
    }

    private static Vector2 CellCenter(int gtx, int gty) => new(
        gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
        gty * Chunk.TileSize + Chunk.TileSize * 0.5f);

    // ── Snapshot ────────────────────────────────────────────────────────────────────
    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.Driven        = Driven;
        s.TargetPos     = TargetPos;
        s.OwnerPos      = OwnerPos;
        s.TileType      = OrbType;
        s.HarvestBlocks = HarvestBlocks;
        s.ChargedBlocks = ChargedBlocks;
        s.LinkedId      = BallId;
        s.HandoffTime   = HandoffTime;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        Driven        = s.Driven;
        TargetPos     = s.TargetPos;
        OwnerPos      = s.OwnerPos;
        OrbType       = s.TileType;
        HarvestBlocks = s.HarvestBlocks;
        ChargedBlocks = s.ChargedBlocks;
        BallId        = s.LinkedId;
        HandoffTime   = s.HandoffTime;
    }

    // The group rides its own sparse store. Added lazily at first capture so a point
    // that never reaches a snapshot boundary costs the World nothing.
    public override void CaptureState(World world)
    {
        base.CaptureState(world);
        if (!world.Has<PeelGroupComp>(Id)) world.Add<PeelGroupComp>(Id);
        world.Get<PeelGroupComp>(Id) = _group;
    }

    public override void RestoreState(World world)
    {
        base.RestoreState(world);
        _group = world.Has<PeelGroupComp>(Id) ? world.Get<PeelGroupComp>(Id) : default;
    }

    // ── Render ──────────────────────────────────────────────────────────────────────
    // Peel-phase feedback: tethered cells darken with tether strength, and the shade
    // slides toward red as the spring nears its snap cap. The ball draws itself. Pure
    // render — reads sim state, feeds nothing back.
    public void Telegraph(TelegraphList t)
    {
        if (IsDead || _group.Count == 0) return;
        var tint = Color.Lerp(Color.Black, Color.DarkRed, _group.Strain);
        for (int i = 0; i < _group.Count; i++)
        {
            var m = _group.Members[i];
            float a = MathHelper.Clamp(0.12f + 0.35f * (m.Tether / 1.5f), 0f, 0.55f);
            t.Box(m.Gtx * Chunk.TileSize, m.Gty * Chunk.TileSize,
                  Chunk.TileSize, Chunk.TileSize, tint * a);
        }
    }
}
